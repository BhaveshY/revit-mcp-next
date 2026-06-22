param(
    [switch] $DryRun,
    [switch] $SkipDependencyInstall,
    [switch] $NoZip,
    [switch] $Sign,
    [switch] $RequireSigned,
    [switch] $RequireTrustedSignatures,
    [int[]] $RevitYears = @(2024),
    [string] $OutputRoot = "",
    [string] $AddinOutputRoot = "",
    [string] $Version = "",
    [string] $SigningCertificateThumbprint = "$env:REVIT_MCP_NEXT_SIGN_CERT_THUMBPRINT",
    [string] $SigningCertificatePath = "$env:REVIT_MCP_NEXT_SIGN_CERT_PATH",
    [string] $SigningCertificatePasswordEnv = "REVIT_MCP_NEXT_SIGN_CERT_PASSWORD",
    [string] $TimestampServer = "$env:REVIT_MCP_NEXT_TIMESTAMP_URL"
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

        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
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
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
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
    if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) {
        $arguments += @("-TimestampServer", $TimestampServer)
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

        if ($RequireSigned -and $entry.status -eq "NotSigned") {
            throw "Signature is missing for $($entry.path)."
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release"
}

$rootPackage = Read-JsonFile (Join-Path $repoRoot "package.json")
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string] $rootPackage.version
}

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

if ([string]::IsNullOrWhiteSpace($AddinOutputRoot)) {
    $addinOut = Join-Path $repoRoot "addin\RevitMcpNext.Addin\bin\Release\net48"
    if (-not (Test-Path -LiteralPath $addinOut -PathType Container)) {
        $addinOut = Join-Path $repoRoot "addin\RevitMcpNext.Addin\bin\Debug\net48"
    }
} else {
    $addinOut = Resolve-RequiredDirectory $AddinOutputRoot "Configured add-in output root was not found."
}

$addinDll = Resolve-RequiredFile (Join-Path $addinOut "RevitMcpNext.Addin.dll") "Add-in is not built. Build it with npm run build:addin."
$contractsDll = Resolve-RequiredFile (Join-Path $addinOut "RevitMcpNext.Contracts.dll") "Contracts DLL is missing from the add-in output."

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
Copy-File $addinDll (Join-Path $payloadRoot "addin\RevitMcpNext.Addin.dll")
Copy-File $contractsDll (Join-Path $payloadRoot "addin\RevitMcpNext.Contracts.dll")
Copy-OptionalFile (Join-Path $addinOut "RevitMcpNext.Addin.pdb") (Join-Path $payloadRoot "addin\RevitMcpNext.Addin.pdb")
Copy-OptionalFile (Join-Path $addinOut "RevitMcpNext.Contracts.pdb") (Join-Path $payloadRoot "addin\RevitMcpNext.Contracts.pdb")
Copy-File $addinTemplate (Join-Path $payloadRoot "addin\RevitMcpNext.addin.template")

Copy-DirectoryContents (Join-Path $repoRoot "installer") (Join-Path $stageRoot "installer")
Copy-DirectoryContents (Join-Path $repoRoot "scripts") (Join-Path $stageRoot "scripts")
Copy-DirectoryContents (Join-Path $repoRoot "docs") (Join-Path $stageRoot "docs")
Copy-File (Join-Path $repoRoot "README.md") (Join-Path $stageRoot "README.md")
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
$fileEntries = Get-PackageFileEntries $stageRoot @("release-manifest.json", "CHECKSUMS.sha256")
$signatureEntries = Get-SignatureEntries $stageRoot
Assert-SignatureEntries $signatureEntries
$manifest = [ordered] @{
    package = [ordered] @{
        name = "revit-mcp-next"
        version = $Version
        platform = "windows"
        createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        revitYears = $RevitYears
        nodeMajor = 24
        gitCommit = $gitCommit
        gitDirty = -not [string]::IsNullOrWhiteSpace($gitStatus)
        nodeModulesBundled = (Test-Path -LiteralPath (Join-Path $payloadRoot "broker\node_modules") -PathType Container)
    }
    signing = [ordered] @{
        requested = [bool] $Sign
        requireSigned = [bool] $RequireSigned
        requireTrusted = [bool] $RequireTrustedSignatures
        timestampServer = $TimestampServer
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
