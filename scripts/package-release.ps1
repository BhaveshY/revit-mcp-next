param(
    [switch] $DryRun,
    [switch] $SkipDependencyInstall,
    [switch] $NoZip,
    [switch] $Sign,
    [switch] $RequireSigned,
    [switch] $RequireTrustedSignatures,
    [string[]] $RevitYears = @("2024"),
    [string] $OutputRoot = "",
    [string] $AddinOutputRoot = "",
    [string] $Version = "",
    [string] $SigningCertificateThumbprint = "$env:REVIT_MCP_NEXT_SIGN_CERT_THUMBPRINT",
    [string] $SigningCertificatePath = "$env:REVIT_MCP_NEXT_SIGN_CERT_PATH",
    [string] $SigningCertificatePasswordEnv = "REVIT_MCP_NEXT_SIGN_CERT_PASSWORD",
    [string] $TimestampServer = "$env:REVIT_MCP_NEXT_TIMESTAMP_URL",
    [switch] $NoTimestamp
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next package] $Message"
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath($Path)
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

function Get-AuthenticodeStatus($Path) {
    try {
        return Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
    } catch {
        return [pscustomobject] @{
            Status = "Unavailable"
            StatusMessage = $_.Exception.Message
            SignerCertificate = $null
        }
    }
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

function Copy-File($Source, $Destination) {
    Resolve-RequiredFile $Source "Required file was not found." | Out-Null

    if ($DryRun) {
        Write-Step "Would copy file $Source -> $Destination"
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-OptionalFile($Source, $Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        return
    }

    if ($DryRun) {
        Write-Step "Would copy optional file $Source -> $Destination"
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-DirectoryContents($Source, $Destination) {
    Resolve-RequiredDirectory $Source "Required directory was not found." | Out-Null

    if ($DryRun) {
        Write-Step "Would copy directory $Source -> $Destination"
        return
    }

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-GitValue($Arguments) {
    $output = & git -C $repoRoot @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return ($output | Out-String).Trim()
}

function Install-ProductionDependencies($BrokerDirectory, $PayloadRoot) {
    if ($SkipDependencyInstall) {
        Write-Step "Skipping packaged broker production dependency install by request."
        return
    }

    if ($DryRun) {
        Write-Step "Would run npm install --omit=dev in $BrokerDirectory"
        return
    }

    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) {
        throw "npm.cmd not found. Install Node 24 with npm, or rerun with -SkipDependencyInstall."
    }

    Push-Location $BrokerDirectory
    try {
        & $npm.Source install --omit=dev --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }

    $contractsModule = Join-Path $BrokerDirectory "node_modules\@revit-mcp-next\contracts"
    if (Test-Path -LiteralPath $contractsModule) {
        Remove-Item -LiteralPath $contractsModule -Recurse -Force
    }

    Copy-DirectoryContents (Join-Path $PayloadRoot "contracts") $contractsModule
}

function Get-PackageFileEntries($Root, [string[]] $ExcludeRelativePaths) {
    $excluded = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($exclude in $ExcludeRelativePaths) {
        $excluded.Add(($exclude -replace "\\", "/")) | Out-Null
    }

    $entries = New-Object System.Collections.Generic.List[object]
    $files = Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName
    foreach ($file in $files) {
        $relativePath = (Get-RelativePath $Root $file.FullName) -replace "\\", "/"
        if ($excluded.Contains($relativePath)) {
            continue
        }

        $hash = Get-Sha256Hash $file.FullName
        $entries.Add([ordered] @{
            path = $relativePath
            sha256 = $hash
            size = $file.Length
        })
    }

    return $entries
}

function Get-SignatureEntries($Root) {
    $entries = New-Object System.Collections.Generic.List[object]
    $files = Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object { $_.Extension -in @(".dll", ".ps1") } |
        Sort-Object FullName

    foreach ($file in $files) {
        $signature = Get-AuthenticodeStatus $file.FullName
        $relativePath = ((Get-RelativePath $Root $file.FullName) -replace "\\", "/")
        $statusMessage = $signature.StatusMessage
        if (-not [string]::IsNullOrWhiteSpace($statusMessage)) {
            $statusMessage = $statusMessage.Replace($file.FullName, $relativePath)
        }

        $signerSubject = $null
        $issuer = $null
        $thumbprint = $null
        if ($signature.SignerCertificate) {
            $signerSubject = $signature.SignerCertificate.Subject
            $issuer = $signature.SignerCertificate.Issuer
            $thumbprint = $signature.SignerCertificate.Thumbprint
        }

        $entries.Add([ordered] @{
            path = $relativePath
            status = $signature.Status.ToString()
            statusMessage = $statusMessage
            signerSubject = $signerSubject
            issuer = $issuer
            thumbprint = $thumbprint
        })
    }

    return $entries
}

function Invoke-PackageSigning($StageRoot) {
    if (-not $Sign) {
        return
    }

    $signScript = Resolve-RequiredFile (Join-Path $repoRoot "scripts\sign-release.ps1") "Signing script is missing."
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $signScript,
        "-PackageRoot", $StageRoot,
        "-CertificatePasswordEnv", $SigningCertificatePasswordEnv
    )

    if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        $arguments += @("-CertificateThumbprint", $SigningCertificateThumbprint)
    }
    if (-not [string]::IsNullOrWhiteSpace($SigningCertificatePath)) {
        $arguments += @("-CertificatePath", $SigningCertificatePath)
    }
    if (-not $NoTimestamp -and -not [string]::IsNullOrWhiteSpace($TimestampServer)) {
        $arguments += @("-TimestampServer", $TimestampServer)
    }
    if ($NoTimestamp) {
        $arguments += "-NoTimestamp"
    }
    if ($RequireSigned) {
        $arguments += "-RequireSigned"
    }
    if ($RequireTrustedSignatures) {
        $arguments += "-RequireTrusted"
    }

    Write-Step "Signing package Authenticode targets."
    & powershell @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Package signing failed with exit code $LASTEXITCODE."
    }
}

function Assert-SignatureEntries($Entries) {
    foreach ($entry in $Entries) {
        if ($RequireTrustedSignatures -and $entry.status -ne "Valid") {
            throw "Signature for $($entry.path) is $($entry.status), expected Valid."
        }

        if ($RequireSigned -and ($entry.status -eq "NotSigned" -or $entry.status -eq "Unavailable")) {
            throw "Signature is missing for $($entry.path)."
        }
    }
}

function Get-SharingMetadata($SignatureEntries) {
    $localDevSubject = "CN=Revit MCP Next Local Dev Code Signing"
    $validSignedTargets = @($SignatureEntries | Where-Object { $_.status -eq "Valid" -and -not [string]::IsNullOrWhiteSpace([string] $_.signerSubject) })
    $usesLocalDevCertificate = @($validSignedTargets | Where-Object { [string] $_.signerSubject -eq $localDevSubject }).Count -gt 0
    $signingMode = "unsigned"
    if ($validSignedTargets.Count -gt 0) {
        if ($usesLocalDevCertificate) {
            $signingMode = "local-dev-signed"
        } else {
            $signingMode = "signed"
        }
    }

    $publicTrust = [bool] ($signingMode -eq "signed" -and $RequireTrustedSignatures)
    $shareProfile = if ($publicTrust) { "release-candidate" } else { "external-preview" }
    $allowedClaim = if ($publicTrust) {
        "Release candidate only after release evidence and readiness gates pass; production requires production readiness evidence for this exact package."
    } else {
        "External preview only; not production signed and not publicly trusted."
    }

    return [ordered] @{
        shareProfile = $shareProfile
        signingMode = $signingMode
        publicTrust = $publicTrust
        allowedClaim = $allowedClaim
        recipientPromptExpected = [bool] (-not $publicTrust)
        localDevCertificateSubject = $localDevSubject
        usesLocalDevCertificate = $usesLocalDevCertificate
    }
}

function Write-SharingNotice($Path, $Sharing, $Version, $Platform, $RevitYears) {
    $prompt = if ([bool] $Sharing.recipientPromptExpected) { "yes" } else { "no" }
    $lines = @(
        "# Revit MCP Next Sharing Notice",
        "",
        "Package: revit-mcp-next $Version ($Platform)",
        "Revit years: $($RevitYears -join ', ')",
        "Share profile: $($Sharing.shareProfile)",
        "Signing mode: $($Sharing.signingMode)",
        "Public trust: $($Sharing.publicTrust)",
        "Recipient security prompt expected: $prompt",
        "",
        "Allowed claim: $($Sharing.allowedClaim)",
        "",
        "Install this package only from an expected source and verify CHECKSUMS.sha256 before use.",
        "Use disposable or test Revit projects for first-run preview workflows.",
        "This notice is generated by scripts/package-release.ps1 and is also recorded in release-manifest.json."
    )
    Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
}

function Assert-SupportedRevitYears {
    if (-not $RevitYears -or $RevitYears.Count -eq 0) {
        throw "At least one Revit year must be supplied."
    }

    $normalizedYears = New-Object System.Collections.Generic.List[int]
    foreach ($value in $RevitYears) {
        foreach ($part in ([string] $value -split ",")) {
            $year = 0
            if (-not [int]::TryParse($part.Trim(), [ref] $year)) {
                throw "Invalid Revit year: $part"
            }
            $normalizedYears.Add($year)
        }
    }

    foreach ($year in ($normalizedYears | Sort-Object -Unique)) {
        if ($year -notin @(2021, 2024, 2027)) {
            throw "Revit $year packaging is not supported yet. Supported Revit years: 2021, 2024, 2027."
        }
    }

    $script:RevitYears = @($normalizedYears | Sort-Object -Unique)
}

function Resolve-AddinOutputForYear($Root, [int] $Year, [int] $YearCount, [bool] $AllowLegacy2024Root) {
    $yearDirectory = Join-Path $Root "$Year"
    if (Test-Path -LiteralPath $yearDirectory -PathType Container) {
        return (Resolve-Path -LiteralPath $yearDirectory).Path
    }

    if ($Year -eq 2024 -and $YearCount -eq 1 -and $AllowLegacy2024Root -and
        (Test-Path -LiteralPath (Join-Path $Root "RevitMcpNext.Addin.dll") -PathType Leaf)) {
        Write-Step "Using legacy direct add-in output for Revit 2024: $Root"
        return (Resolve-Path -LiteralPath $Root).Path
    }

    throw "Year-specific Revit $Year add-in output is missing. Expected: $yearDirectory. Build it with scripts\build-addin.ps1 -RevitYear $Year."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release"
}
$effectiveTimestampServer = $TimestampServer
if ($NoTimestamp) {
    $effectiveTimestampServer = ""
} elseif ($Sign -and [string]::IsNullOrWhiteSpace($effectiveTimestampServer)) {
    $effectiveTimestampServer = "http://timestamp.digicert.com"
}

$rootPackage = Read-JsonFile (Join-Path $repoRoot "package.json")
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string] $rootPackage.version
}

Assert-SupportedRevitYears
$RevitYears = @($RevitYears | Sort-Object -Unique)

$packageName = "revit-mcp-next-$Version-windows"
$outputRootFull = Get-FullPath $OutputRoot
$stageRoot = Join-Path $outputRootFull $packageName
$zipPath = "$stageRoot.zip"
$payloadRoot = Join-Path $stageRoot "payload"

Write-Step "Staging package: $stageRoot"

$brokerRuntimeDist = Resolve-RequiredDirectory (Join-Path $repoRoot "broker\dist\src") "Broker runtime output is missing. Run npm install and npm run build first."
$contractsDist = Resolve-RequiredDirectory (Join-Path $repoRoot "contracts\dist") "Contracts output is missing. Run npm install and npm run build first."
$contractsSchemas = Resolve-RequiredDirectory (Join-Path $repoRoot "contracts\schemas") "Contracts schemas are missing."
$brokerPackage = Resolve-RequiredFile (Join-Path $repoRoot "broker\package.json") "Broker package metadata is missing."
$contractsPackage = Resolve-RequiredFile (Join-Path $repoRoot "contracts\package.json") "Contracts package metadata is missing."
$addinTemplate = Resolve-RequiredFile (Join-Path $repoRoot "addin\RevitMcpNext.Addin\RevitMcpNext.addin.template") "Add-in manifest template is missing."

$allowLegacy2024Root = $false
if ([string]::IsNullOrWhiteSpace($AddinOutputRoot)) {
    $addinOutputBase = Join-Path $repoRoot "artifacts\addin"
    $legacy2024Release = Join-Path $repoRoot "addin\RevitMcpNext.Addin\bin\Release\net48"
    $legacy2024Debug = Join-Path $repoRoot "addin\RevitMcpNext.Addin\bin\Debug\net48"
    if ($RevitYears.Count -eq 1 -and $RevitYears[0] -eq 2024 -and
        -not (Test-Path -LiteralPath (Join-Path $addinOutputBase "2024") -PathType Container)) {
        if (Test-Path -LiteralPath $legacy2024Release -PathType Container) {
            $addinOutputBase = $legacy2024Release
            $allowLegacy2024Root = $true
        } elseif (Test-Path -LiteralPath $legacy2024Debug -PathType Container) {
            $addinOutputBase = $legacy2024Debug
            $allowLegacy2024Root = $true
        }
    }
} else {
    $addinOutputBase = Resolve-RequiredDirectory $AddinOutputRoot "Configured add-in output root was not found."
    $allowLegacy2024Root = $true
}

$addinSources = [ordered] @{}
$seenAddinDlls = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($year in $RevitYears) {
    $addinOut = Resolve-AddinOutputForYear $addinOutputBase $year $RevitYears.Count $allowLegacy2024Root
    $addinDll = Resolve-RequiredFile (Join-Path $addinOut "RevitMcpNext.Addin.dll") "Revit $year add-in is not built."
    $contractsDll = Resolve-RequiredFile (Join-Path $addinOut "RevitMcpNext.Contracts.dll") "Revit $year contracts DLL is missing from the add-in output."
    if (-not $seenAddinDlls.Add($addinDll)) {
        throw "Revit $year resolves to an add-in DLL already assigned to another Revit year. Each year must use a distinct build artifact."
    }

    $addinSources["$year"] = [ordered] @{
        directory = $addinOut
        addinDll = $addinDll
        contractsDll = $contractsDll
        targetFramework = if ($year -in @(2021, 2024)) { "net48" } else { "net10.0-windows" }
    }
}

if ($DryRun) {
    Write-Step "Would reset staging directory: $stageRoot"
} else {
    New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null
    Assert-PathChild $outputRootFull $stageRoot "package staging directory"
    Assert-PathChild $outputRootFull $zipPath "package zip"

    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }

    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Remove-Item -LiteralPath $zipPath -Force
    }
}

Copy-DirectoryContents $brokerRuntimeDist (Join-Path $payloadRoot "broker\dist\src")
Copy-File $brokerPackage (Join-Path $payloadRoot "broker\package.json")
Copy-DirectoryContents $contractsDist (Join-Path $payloadRoot "contracts\dist")
Copy-DirectoryContents $contractsSchemas (Join-Path $payloadRoot "contracts\schemas")
Copy-File $contractsPackage (Join-Path $payloadRoot "contracts\package.json")
foreach ($year in $RevitYears) {
    $source = $addinSources["$year"]
    $yearPayload = Join-Path $payloadRoot "addin\$year"
    Copy-File $source.addinDll (Join-Path $yearPayload "RevitMcpNext.Addin.dll")
    Copy-File $source.contractsDll (Join-Path $yearPayload "RevitMcpNext.Contracts.dll")
    Copy-OptionalFile (Join-Path $source.directory "RevitMcpNext.Addin.pdb") (Join-Path $yearPayload "RevitMcpNext.Addin.pdb")
    Copy-OptionalFile (Join-Path $source.directory "RevitMcpNext.Contracts.pdb") (Join-Path $yearPayload "RevitMcpNext.Contracts.pdb")
}
Copy-File $addinTemplate (Join-Path $payloadRoot "addin\RevitMcpNext.addin.template")

Copy-DirectoryContents (Join-Path $repoRoot "installer") (Join-Path $stageRoot "installer")
Copy-DirectoryContents (Join-Path $repoRoot "scripts") (Join-Path $stageRoot "scripts")
Copy-DirectoryContents (Join-Path $repoRoot "docs") (Join-Path $stageRoot "docs")
Copy-DirectoryContents (Join-Path $repoRoot "integrations") (Join-Path $stageRoot "integrations")
Copy-File (Join-Path $repoRoot "README.md") (Join-Path $stageRoot "README.md")
Copy-File (Join-Path $repoRoot "LICENSE") (Join-Path $stageRoot "LICENSE")
Copy-File (Join-Path $repoRoot "SECURITY.md") (Join-Path $stageRoot "SECURITY.md")
Copy-File (Join-Path $repoRoot "package.json") (Join-Path $stageRoot "package.json")
Copy-File (Join-Path $repoRoot "package-lock.json") (Join-Path $stageRoot "package-lock.json")

Install-ProductionDependencies (Join-Path $payloadRoot "broker") $payloadRoot

if ($DryRun) {
    if ($Sign) {
        Write-Step "Would sign Authenticode targets under $stageRoot"
    }
    Write-Step "Would write release-manifest.json and CHECKSUMS.sha256"
    if (-not $NoZip) {
        Write-Step "Would create package zip: $zipPath"
    }

    return
}

Invoke-PackageSigning $stageRoot

$gitCommit = Get-GitValue @("rev-parse", "HEAD")
$gitStatus = Get-GitValue @("status", "--short")
$signatureEntries = Get-SignatureEntries $stageRoot
Assert-SignatureEntries $signatureEntries
$sharing = Get-SharingMetadata $signatureEntries
Write-SharingNotice (Join-Path $stageRoot "SHARING-NOTICE.md") $sharing $Version "windows" $RevitYears
$fileEntries = Get-PackageFileEntries $stageRoot @("release-manifest.json", "CHECKSUMS.sha256")
$manifest = [ordered] @{
    package = [ordered] @{
        name = "revit-mcp-next"
        version = $Version
        platform = "windows"
        createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        revitYears = $RevitYears
        addinArtifacts = @($RevitYears | ForEach-Object {
            [ordered] @{
                revitYear = $_
                targetFramework = $addinSources["$_"].targetFramework
                path = "payload/addin/$_/RevitMcpNext.Addin.dll"
            }
        })
        nodeMajor = 24
        gitCommit = $gitCommit
        gitDirty = -not [string]::IsNullOrWhiteSpace($gitStatus)
        nodeModulesBundled = (Test-Path -LiteralPath (Join-Path $payloadRoot "broker\node_modules") -PathType Container)
        integrationsIncluded = (
            (Test-Path -LiteralPath (Join-Path $stageRoot "integrations\python\revit_mcp_next_client.py") -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $stageRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Diagnostics.panel\Host Smoke.pushbutton\script.py") -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $stageRoot "integrations\dynamo\revit_mcp_next_host_smoke.dyn") -PathType Leaf)
        )
    }
    sharing = $sharing
    signing = [ordered] @{
        requested = [bool] $Sign
        requireSigned = [bool] $RequireSigned
        requireTrusted = [bool] $RequireTrustedSignatures
        timestampServer = $effectiveTimestampServer
        targets = $signatureEntries
    }
    contents = $fileEntries
}

Set-Content -LiteralPath (Join-Path $stageRoot "release-manifest.json") -Value ($manifest | ConvertTo-Json -Depth 8) -Encoding UTF8

$checksumEntries = Get-PackageFileEntries $stageRoot @("CHECKSUMS.sha256")
$checksumLines = $checksumEntries | ForEach-Object {
    "$($_.sha256)  $($_.path)"
}
Set-Content -LiteralPath (Join-Path $stageRoot "CHECKSUMS.sha256") -Value $checksumLines -Encoding ASCII

if (-not $NoZip) {
    Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -Force
    Write-Step "Created package zip: $zipPath"
}

Write-Step "Created staged package: $stageRoot"
