param(
    [string] $PackageRoot = "",
    [string] $PackageZipPath = "",
    [string] $OutputRoot = "",
    [string] $SigningSkipReason = "",
    [string] $LiveSmokeEvidencePath = "",
    [string] $LiveSmokeSkipReason = "",
    [string] $SupportBundlePath = "",
    [string] $SupportBundleSkipReason = "",
    [string] $ValidateRepoLogPath = "",
    [string] $PackageLogPath = "",
    [string] $DoctorLogPath = "",
    [string] $SigningLogPath = "",
    [string[]] $CommandLogPaths = @(),
    [string[]] $AdditionalEvidencePaths = @(),
    [switch] $NoZip
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next evidence] $Message"
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Add-TrailingSeparator($Path) {
    if ($Path.EndsWith("\") -or $Path.EndsWith("/")) {
        return $Path
    }

    return "$Path\"
}

function Assert-PathChild($Root, $Path, $Label) {
    $rootFull = Get-FullPath $Root
    $pathFull = Get-FullPath $Path
    $rootWithSeparator = Add-TrailingSeparator $rootFull

    if ($pathFull -ne $rootFull -and -not $pathFull.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use $Label outside expected root. Root: $rootFull Target: $pathFull"
    }
}

function Get-RelativePath($Root, $Path) {
    $rootFull = Add-TrailingSeparator (Get-FullPath $Root)
    $pathFull = Get-FullPath $Path
    $rootUri = New-Object System.Uri($rootFull)
    $pathUri = New-Object System.Uri($pathFull)
    return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
}

function Get-Sha256Hash($Path) {
    $stream = [System.IO.File]::OpenRead((Get-FullPath $Path))
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace("-", "").ToLowerInvariant()
        } finally {
            $sha256.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Resolve-RequiredFile($Path, $Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Message Missing: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-RequiredDirectory($Path, $Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Message Missing: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-RequiredEvidencePath($Path, $Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label path cannot be empty."
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if (Test-Path -LiteralPath $expanded -PathType Leaf) {
        return (Resolve-Path -LiteralPath $expanded).Path
    }

    if (Test-Path -LiteralPath $expanded -PathType Container) {
        return (Resolve-Path -LiteralPath $expanded).Path
    }

    throw "$Label path was not found: $Path"
}

function Find-LatestPackageRoot($ReleaseRoot) {
    if (-not (Test-Path -LiteralPath $ReleaseRoot -PathType Container)) {
        throw "PackageRoot was not provided and default release root was not found: $ReleaseRoot"
    }

    $candidates = Get-ChildItem -LiteralPath $ReleaseRoot -Directory -Filter "revit-mcp-next-*-windows" |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "release-manifest.json") -PathType Leaf } |
        Sort-Object LastWriteTimeUtc -Descending

    $candidate = $candidates | Select-Object -First 1
    if (-not $candidate) {
        throw "PackageRoot was not provided and no staged release package was found under $ReleaseRoot."
    }

    return $candidate.FullName
}

function New-Directory($Path) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Copy-RequiredFile($Source, $Destination) {
    Resolve-RequiredFile $Source "Required evidence file was not found." | Out-Null
    New-Directory (Split-Path -Parent $Destination)
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-EvidencePath($Source, $DestinationRoot, $Label) {
    $resolvedSource = Resolve-RequiredEvidencePath $Source $Label
    New-Directory $DestinationRoot

    if (Test-Path -LiteralPath $resolvedSource -PathType Leaf) {
        $destination = Join-Path $DestinationRoot (Split-Path -Leaf $resolvedSource)
        Copy-Item -LiteralPath $resolvedSource -Destination $destination -Force
        return $destination
    }

    Get-ChildItem -LiteralPath $resolvedSource -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $DestinationRoot -Recurse -Force
    }
    return $DestinationRoot
}

function Copy-NamedEvidenceFile($Path, $DestinationRoot, $StoredName, $Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [ordered] @{
            present = $false
            sourcePath = $null
            storedAs = $null
            sha256 = $null
            size = $null
        }
    }

    $resolvedPath = Resolve-RequiredFile ([Environment]::ExpandEnvironmentVariables($Path)) "$Label file was not found."
    New-Directory $DestinationRoot
    $destination = Join-Path $DestinationRoot $StoredName
    Copy-Item -LiteralPath $resolvedPath -Destination $destination -Force
    $file = Get-Item -LiteralPath $destination

    return [ordered] @{
        present = $true
        sourcePath = $resolvedPath
        storedAs = ((Get-RelativePath $stageRoot $destination) -replace "\\", "/")
        sha256 = Get-Sha256Hash $destination
        size = $file.Length
    }
}

function Get-InventoryEntries($Root, [string[]] $ExcludeRelativePaths = @()) {
    $excluded = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($exclude in $ExcludeRelativePaths) {
        $excluded.Add(($exclude -replace "\\", "/")) | Out-Null
    }

    $entries = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $Root)) {
        return $entries
    }

    if (Test-Path -LiteralPath $Root -PathType Leaf) {
        $file = Get-Item -LiteralPath $Root
        if ($excluded.Contains($file.Name)) {
            return $entries
        }

        $entries.Add([ordered] @{
            path = $file.Name
            sha256 = Get-Sha256Hash $file.FullName
            size = $file.Length
        }) | Out-Null
        return $entries
    }

    $files = Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName
    foreach ($file in $files) {
        $relativePath = (Get-RelativePath $Root $file.FullName) -replace "\\", "/"
        if ($excluded.Contains($relativePath)) {
            continue
        }

        $entries.Add([ordered] @{
            path = $relativePath
            sha256 = Get-Sha256Hash $file.FullName
            size = $file.Length
        }) | Out-Null
    }

    return $entries
}

function Get-SafeFileName($Path, $Index) {
    $leaf = Split-Path -Leaf $Path
    if ([string]::IsNullOrWhiteSpace($leaf)) {
        $leaf = "evidence-$Index"
    }

    $invalidChars = [System.IO.Path]::GetInvalidFileNameChars()
    foreach ($char in $invalidChars) {
        $leaf = $leaf.Replace($char, "_")
    }

    if ($Index -le 0) {
        return $leaf
    }

    $extension = [System.IO.Path]::GetExtension($leaf)
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($leaf)
    return "$stem-$Index$extension"
}

function Copy-PathList($Paths, $DestinationRoot, $Label) {
    $copied = New-Object System.Collections.Generic.List[object]
    $index = 0
    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $resolvedPath = Resolve-RequiredEvidencePath $path $Label
        $safeName = Get-SafeFileName $resolvedPath $index
        $destination = Join-Path $DestinationRoot $safeName
        New-Directory $DestinationRoot

        if (Test-Path -LiteralPath $resolvedPath -PathType Leaf) {
            Copy-Item -LiteralPath $resolvedPath -Destination $destination -Force
            $inventoryRoot = $destination
        } else {
            New-Directory $destination
            Get-ChildItem -LiteralPath $resolvedPath -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
            }
            $inventoryRoot = $destination
        }

        $copied.Add([ordered] @{
            sourcePath = $resolvedPath
            storedAs = ((Get-RelativePath $stageRoot $destination) -replace "\\", "/")
            files = Get-InventoryEntries $inventoryRoot
        }) | Out-Null
        $index++
    }

    return $copied
}

function Require-SkipOrEvidence($EvidencePath, $SkipReason, $Label) {
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($SkipReason)) {
        throw "$Label evidence was not provided. Pass evidence path or an explicit skip reason."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release-evidence"
}

if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Find-LatestPackageRoot (Join-Path $repoRoot "artifacts\release")
}

$packageRootFull = Resolve-RequiredDirectory $PackageRoot "Package root was not found."
$releaseManifestPath = Resolve-RequiredFile (Join-Path $packageRootFull "release-manifest.json") "Release manifest is missing."
$checksumsPath = Resolve-RequiredFile (Join-Path $packageRootFull "CHECKSUMS.sha256") "Package checksum file is missing."

if ([string]::IsNullOrWhiteSpace($PackageZipPath)) {
    $PackageZipPath = "$packageRootFull.zip"
}
$packageZipFull = Resolve-RequiredFile $PackageZipPath "Package zip is missing."

Require-SkipOrEvidence $LiveSmokeEvidencePath $LiveSmokeSkipReason "Live Revit smoke"
Require-SkipOrEvidence $SupportBundlePath $SupportBundleSkipReason "Support bundle"

$releaseManifest = Read-JsonFile $releaseManifestPath
$version = [string] $releaseManifest.package.version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Release manifest package.version is missing."
}

$platform = [string] $releaseManifest.package.platform
if ([string]::IsNullOrWhiteSpace($platform)) {
    $platform = "windows"
}

$outputRootFull = Get-FullPath $OutputRoot
New-Directory $outputRootFull

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$evidenceName = "revit-mcp-next-$version-$platform-evidence-$timestamp-$runId"
$stageRoot = Join-Path $outputRootFull $evidenceName
$bundleZipPath = "$stageRoot.zip"
Assert-PathChild $outputRootFull $stageRoot "release evidence root"
Assert-PathChild $outputRootFull $bundleZipPath "release evidence zip"
New-Directory $stageRoot

Write-Step "Collecting release evidence: $stageRoot"

$packageEvidenceRoot = Join-Path $stageRoot "package"
Copy-RequiredFile $releaseManifestPath (Join-Path $packageEvidenceRoot "release-manifest.json")
Copy-RequiredFile $checksumsPath (Join-Path $packageEvidenceRoot "CHECKSUMS.sha256")

$packageZip = Get-Item -LiteralPath $packageZipFull
$packageZipHash = Get-Sha256Hash $packageZip.FullName
$packageZipHashLine = "$packageZipHash  $($packageZip.Name)"
Set-Content -LiteralPath (Join-Path $packageEvidenceRoot "package-zip.sha256") -Value $packageZipHashLine -Encoding ASCII

$signingRequested = [bool] $releaseManifest.signing.requested
$signingStatus = "captured"
$effectiveSigningSkipReason = $SigningSkipReason
if (-not $signingRequested) {
    if ([string]::IsNullOrWhiteSpace($effectiveSigningSkipReason)) {
        throw "Signing was not requested for this build. Pass -SigningSkipReason to make that release evidence explicit."
    }

    $signingStatus = "skipped"
} elseif ([string]::IsNullOrWhiteSpace($SigningLogPath)) {
    throw "Signing was requested in release-manifest.json. Pass -SigningLogPath with signing verification output."
}

$liveSmokeSection = [ordered] @{
    status = "skipped"
    sourcePath = $null
    skipReason = $LiveSmokeSkipReason
    files = @()
}
if (-not [string]::IsNullOrWhiteSpace($LiveSmokeEvidencePath)) {
    $liveSmokeRoot = Join-Path $stageRoot "live-smoke"
    $copiedLiveSmoke = Copy-EvidencePath $LiveSmokeEvidencePath $liveSmokeRoot "Live Revit smoke evidence"
    $liveSmokeSection = [ordered] @{
        status = "captured"
        sourcePath = (Resolve-RequiredEvidencePath $LiveSmokeEvidencePath "Live Revit smoke evidence")
        storedAs = ((Get-RelativePath $stageRoot $liveSmokeRoot) -replace "\\", "/")
        skipReason = $null
        files = Get-InventoryEntries $copiedLiveSmoke
    }
}

$supportSection = [ordered] @{
    status = "skipped"
    sourcePath = $null
    skipReason = $SupportBundleSkipReason
    files = @()
}
if (-not [string]::IsNullOrWhiteSpace($SupportBundlePath)) {
    $supportRoot = Join-Path $stageRoot "support"
    $copiedSupport = Copy-EvidencePath $SupportBundlePath $supportRoot "Support bundle evidence"
    $supportSection = [ordered] @{
        status = "captured"
        sourcePath = (Resolve-RequiredEvidencePath $SupportBundlePath "Support bundle evidence")
        storedAs = ((Get-RelativePath $stageRoot $supportRoot) -replace "\\", "/")
        skipReason = $null
        files = Get-InventoryEntries $copiedSupport
    }
}

$commandLogs = Copy-PathList $CommandLogPaths (Join-Path $stageRoot "command-logs") "Command log evidence"
$additionalEvidence = Copy-PathList $AdditionalEvidencePaths (Join-Path $stageRoot "additional") "Additional evidence"
$validationRoot = Join-Path $stageRoot "validation"
$validationSection = [ordered] @{
    validateRepoLog = Copy-NamedEvidenceFile $ValidateRepoLogPath $validationRoot "validate-repo.log" "validate-repo"
    packageLog = Copy-NamedEvidenceFile $PackageLogPath $validationRoot "package-release.log" "package-release"
    doctorLog = Copy-NamedEvidenceFile $DoctorLogPath $validationRoot "doctor-windows.log" "doctor"
}
$signingLog = Copy-NamedEvidenceFile $SigningLogPath (Join-Path $stageRoot "signing") "signing.log" "signing"

$packageSummary = [ordered] @{
    packageRoot = $packageRootFull
    packageZipPath = $packageZip.FullName
    packageZipName = $packageZip.Name
    packageZipSha256 = $packageZipHash
    packageZipSize = $packageZip.Length
    releaseManifest = "package/release-manifest.json"
    checksums = "package/CHECKSUMS.sha256"
    packageZipChecksum = "package/package-zip.sha256"
}

$evidenceManifest = [ordered] @{
    schemaVersion = 1
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    generatedBy = "scripts/collect-release-evidence.ps1"
    package = [ordered] @{
        name = [string] $releaseManifest.package.name
        version = $version
        platform = $platform
        createdAtUtc = [string] $releaseManifest.package.createdAtUtc
        gitCommit = [string] $releaseManifest.package.gitCommit
        gitDirty = [bool] $releaseManifest.package.gitDirty
        revitYears = $releaseManifest.package.revitYears
        nodeMajor = $releaseManifest.package.nodeMajor
        nodeModulesBundled = [bool] $releaseManifest.package.nodeModulesBundled
        evidence = $packageSummary
    }
    signing = [ordered] @{
        status = $signingStatus
        skipReason = $effectiveSigningSkipReason
        requested = $signingRequested
        requireSigned = [bool] $releaseManifest.signing.requireSigned
        requireTrusted = [bool] $releaseManifest.signing.requireTrusted
        timestampServer = [string] $releaseManifest.signing.timestampServer
        log = $signingLog
        targets = $releaseManifest.signing.targets
    }
    validation = $validationSection
    liveSmoke = $liveSmokeSection
    supportBundle = $supportSection
    commandLogs = $commandLogs
    additionalEvidence = $additionalEvidence
    contents = @()
}

$summaryLines = @(
    "# Revit MCP Next Release Evidence",
    "",
    "- Package: revit-mcp-next $version ($platform)",
    "- Package zip: $($packageZip.Name)",
    "- Package zip SHA-256: $packageZipHash",
    "- Git commit: $($releaseManifest.package.gitCommit)",
    "- Git dirty: $($releaseManifest.package.gitDirty)",
    "- Signing: $signingStatus",
    "- Live smoke: $($liveSmokeSection.status)",
    "- Support bundle: $($supportSection.status)"
)

if ($signingStatus -eq "skipped") {
    $summaryLines += "- Signing skip reason: $effectiveSigningSkipReason"
}
if ($liveSmokeSection.status -eq "skipped") {
    $summaryLines += "- Live smoke skip reason: $LiveSmokeSkipReason"
}
if ($supportSection.status -eq "skipped") {
    $summaryLines += "- Support bundle skip reason: $SupportBundleSkipReason"
}

$summaryLines += @(
    "",
    "See release-evidence-manifest.json for the complete evidence inventory."
)

Set-Content -LiteralPath (Join-Path $stageRoot "release-evidence-summary.md") -Value $summaryLines -Encoding UTF8

$evidenceManifest["contents"] = Get-InventoryEntries $stageRoot @("release-evidence-manifest.json")
Set-Content -LiteralPath (Join-Path $stageRoot "release-evidence-manifest.json") -Value ($evidenceManifest | ConvertTo-Json -Depth 12) -Encoding UTF8

if (-not $NoZip) {
    Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $bundleZipPath -Force
    Write-Step "Created release evidence zip: $bundleZipPath"
}

Write-Step "Created release evidence: $stageRoot"
