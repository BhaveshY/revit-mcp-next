param(
    [switch] $DryRun,
    [switch] $SkipDependencyInstall,
    [switch] $SkipChecksumVerification,
    [int[]] $RevitYears = @(2024),
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [string] $NodePath = "",
    [string] $PackageRoot = ""
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next] $Message"
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Remove-TrailingSeparator($Path) {
    return $Path.TrimEnd([char[]] @("\", "/"))
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

function Assert-SafeInstallRoot {
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        throw "InstallRoot cannot be empty."
    }

    $root = Get-FullPath $InstallRoot
    $driveRoot = [System.IO.Path]::GetPathRoot($root)
    if ((Remove-TrailingSeparator $root) -eq (Remove-TrailingSeparator $driveRoot)) {
        throw "Refusing to install directly into a drive root: $root"
    }

    $blockedRoots = @($env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, $env:TEMP) | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    }

    foreach ($blockedRoot in $blockedRoots) {
        if ((Remove-TrailingSeparator (Get-FullPath $blockedRoot)) -eq (Remove-TrailingSeparator $root)) {
            throw "Refusing to install directly into a broad profile directory: $root"
        }
    }
}

function Assert-InstallChild($Path) {
    Assert-PathChild $InstallRoot $Path "install target"
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

function Resolve-NodeExe {
    if (-not [string]::IsNullOrWhiteSpace($NodePath)) {
        return Resolve-RequiredFile $NodePath "Configured node.exe was not found."
    }

    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) {
        throw "node.exe not found. Install Node 24, then rerun installer\install-windows.ps1."
    }

    return $node.Source
}

function Assert-NodeVersion($NodeExe) {
    $versionText = (& $NodeExe --version)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($versionText)) {
        throw "Unable to execute node.exe at $NodeExe."
    }

    if ($versionText -notmatch "^v?(\d+)\.") {
        throw "Unable to parse Node version from '$versionText'."
    }

    $major = [int] $Matches[1]
    if ($major -ne 24) {
        throw "Node $versionText is not supported. revit-mcp-next currently requires Node 24.x."
    }

    Write-Step "Using node.exe $versionText at $NodeExe"
}

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Test-PackageChecksums($Root) {
    $checksumFile = Resolve-RequiredFile (Join-Path $Root "CHECKSUMS.sha256") "Package checksum file is missing."
    Write-Step "Verifying package checksums"

    $verified = 0
    foreach ($line in Get-Content -LiteralPath $checksumFile) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
            continue
        }

        if ($line -notmatch "^([a-fA-F0-9]{64})\s\s(.+)$") {
            throw "Invalid checksum line: $line"
        }

        $expected = $Matches[1].ToLowerInvariant()
        $relativePath = $Matches[2]
        if ([System.IO.Path]::IsPathRooted($relativePath) -or $relativePath.Contains("..")) {
            throw "Unsafe checksum path: $relativePath"
        }

        $filePath = Join-Path $Root ($relativePath -replace "/", "\")
        Assert-PathChild $Root $filePath "checksum target"
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Checksum target is missing: $relativePath"
        }

        $actual = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "Checksum mismatch for $relativePath. Expected $expected, got $actual."
        }

        $verified++
    }

    Write-Step "Verified $verified package checksums"
}

function Sync-Directory($Source, $Destination) {
    Resolve-RequiredDirectory $Source "Required build output was not found." | Out-Null
    Assert-InstallChild $Destination

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

function Copy-File($Source, $Destination) {
    Resolve-RequiredFile $Source "Required file was not found." | Out-Null
    Assert-InstallChild $Destination

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

    Assert-InstallChild $Destination

    if ($DryRun) {
        Write-Step "Would copy optional file $Source -> $Destination"
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Get-ReleaseVersion($SourceMode, $PackageRootPath, $RepoRootPath) {
    if ($SourceMode -eq "package") {
        $manifestPath = Join-Path $PackageRootPath "release-manifest.json"
        if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
            $manifest = Read-JsonFile $manifestPath
            if ($manifest.package.version) {
                return [string] $manifest.package.version
            }
        }
    }

    $packageJsonPath = Join-Path $RepoRootPath "package.json"
    if (Test-Path -LiteralPath $packageJsonPath -PathType Leaf) {
        return [string] (Read-JsonFile $packageJsonPath).version
    }

    return "0.1.0"
}

Assert-SafeInstallRoot

Write-Step "Installing Revit MCP Next"
Write-Step "Install root: $InstallRoot"

$scriptRootParent = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $autoPayloadRoot = Join-Path $scriptRootParent "payload"
    if (Test-Path -LiteralPath $autoPayloadRoot -PathType Container) {
        $PackageRoot = $scriptRootParent
    }
}

$sourceMode = "repository"
$resolvedPackageRoot = ""
$repoRoot = $scriptRootParent

if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) {
    $sourceMode = "package"
    $resolvedPackageRoot = Resolve-RequiredDirectory $PackageRoot "Package root was not found."
    if (-not $SkipChecksumVerification) {
        Test-PackageChecksums $resolvedPackageRoot
    } else {
        Write-Step "Skipping package checksum verification by request."
    }
}

$nodeExe = Resolve-NodeExe
Assert-NodeVersion $nodeExe
$releaseVersion = Get-ReleaseVersion $sourceMode $resolvedPackageRoot $repoRoot

if ($sourceMode -eq "package") {
    $payloadRoot = Resolve-RequiredDirectory (Join-Path $resolvedPackageRoot "payload") "Package payload is missing."
    $brokerRuntimeDist = Resolve-RequiredDirectory (Join-Path $payloadRoot "broker\dist\src") "Packaged broker runtime output is missing."
    $contractsDist = Resolve-RequiredDirectory (Join-Path $payloadRoot "contracts\dist") "Packaged contracts output is missing."
    $schemasDir = Resolve-RequiredDirectory (Join-Path $payloadRoot "contracts\schemas") "Packaged contract schemas are missing."
    $brokerPackage = Resolve-RequiredFile (Join-Path $payloadRoot "broker\package.json") "Packaged broker package metadata is missing."
    $contractsPackage = Resolve-RequiredFile (Join-Path $payloadRoot "contracts\package.json") "Packaged contracts package metadata is missing."
    $brokerEntrySource = Resolve-RequiredFile (Join-Path $payloadRoot "broker\dist\src\index.js") "Packaged broker entry point is missing."
    $addinTemplate = Resolve-RequiredFile (Join-Path $payloadRoot "addin\RevitMcpNext.addin.template") "Packaged add-in manifest template is missing."
    $addinDll = Resolve-RequiredFile (Join-Path $payloadRoot "addin\RevitMcpNext.Addin.dll") "Packaged add-in DLL is missing."
    $contractsDll = Resolve-RequiredFile (Join-Path $payloadRoot "addin\RevitMcpNext.Contracts.dll") "Packaged contracts DLL is missing."
    $addinPdb = Join-Path $payloadRoot "addin\RevitMcpNext.Addin.pdb"
    $contractsPdb = Join-Path $payloadRoot "addin\RevitMcpNext.Contracts.pdb"
    $packagedNodeModulesCandidate = Join-Path $payloadRoot "broker\node_modules"
} else {
    $brokerDist = Resolve-RequiredDirectory (Join-Path $repoRoot "broker\dist") "Broker is not built. Run npm install and npm run build first."
    $brokerRuntimeDist = Resolve-RequiredDirectory (Join-Path $brokerDist "src") "Broker runtime output is missing."
    $contractsDist = Resolve-RequiredDirectory (Join-Path $repoRoot "contracts\dist") "Contracts are not built. Run npm install and npm run build first."
    $schemasDir = Resolve-RequiredDirectory (Join-Path $repoRoot "contracts\schemas") "Contracts schemas are missing."
    $brokerPackage = Resolve-RequiredFile (Join-Path $repoRoot "broker\package.json") "Broker package metadata is missing."
    $contractsPackage = Resolve-RequiredFile (Join-Path $repoRoot "contracts\package.json") "Contracts package metadata is missing."
    $brokerEntrySource = Resolve-RequiredFile (Join-Path $brokerDist "src\index.js") "Broker entry point is missing."
    $addinTemplate = Resolve-RequiredFile (Join-Path $repoRoot "addin\RevitMcpNext.Addin\RevitMcpNext.addin.template") "Add-in manifest template is missing."

    $addinOut = Join-Path $repoRoot "addin\RevitMcpNext.Addin\bin\Release\net48"
    if (-not (Test-Path -LiteralPath $addinOut -PathType Container)) {
        $addinOut = Join-Path $repoRoot "addin\RevitMcpNext.Addin\bin\Debug\net48"
    }

    $addinDll = Resolve-RequiredFile (Join-Path $addinOut "RevitMcpNext.Addin.dll") "Add-in is not built. Build it with dotnet build addin\RevitMcpNext.Addin\RevitMcpNext.Addin.csproj -c Release."
    $contractsDll = Resolve-RequiredFile (Join-Path $addinOut "RevitMcpNext.Contracts.dll") "Contracts DLL is missing from the add-in output."
    $addinPdb = Join-Path $addinOut "RevitMcpNext.Addin.pdb"
    $contractsPdb = Join-Path $addinOut "RevitMcpNext.Contracts.pdb"
    $packagedNodeModulesCandidate = ""
}

$brokerEntrySource | Out-Null

$installedBroker = Join-Path $InstallRoot "broker"
$installedContracts = Join-Path $InstallRoot "contracts"
$installedAddin = Join-Path $InstallRoot "addin"
$installedBrokerDist = Join-Path $installedBroker "dist"
$installedBrokerEntry = Join-Path $installedBroker "dist\src\index.js"

if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
}

Assert-InstallChild $installedBrokerDist
if ($DryRun) {
    Write-Step "Would reset installed broker dist directory: $installedBrokerDist"
} else {
    if (Test-Path -LiteralPath $installedBrokerDist) {
        Remove-Item -LiteralPath $installedBrokerDist -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $installedBrokerDist | Out-Null
}

Sync-Directory $brokerRuntimeDist (Join-Path $installedBrokerDist "src")
Copy-File $brokerPackage (Join-Path $installedBroker "package.json")
Sync-Directory $contractsDist (Join-Path $installedContracts "dist")
Sync-Directory $schemasDir (Join-Path $installedContracts "schemas")
Copy-File $contractsPackage (Join-Path $installedContracts "package.json")
Copy-File $addinDll (Join-Path $installedAddin "RevitMcpNext.Addin.dll")
Copy-File $contractsDll (Join-Path $installedAddin "RevitMcpNext.Contracts.dll")
Copy-OptionalFile $addinPdb (Join-Path $installedAddin "RevitMcpNext.Addin.pdb")
Copy-OptionalFile $contractsPdb (Join-Path $installedAddin "RevitMcpNext.Contracts.pdb")

if ($sourceMode -eq "package") {
    Copy-OptionalFile (Join-Path $resolvedPackageRoot "release-manifest.json") (Join-Path $InstallRoot "release-manifest.json")
    Copy-OptionalFile (Join-Path $resolvedPackageRoot "CHECKSUMS.sha256") (Join-Path $InstallRoot "release-CHECKSUMS.sha256")
}

$packagedNodeModules = ""
if (-not [string]::IsNullOrWhiteSpace($packagedNodeModulesCandidate) -and (Test-Path -LiteralPath $packagedNodeModulesCandidate -PathType Container)) {
    $packagedNodeModules = (Resolve-Path -LiteralPath $packagedNodeModulesCandidate).Path
}

if (-not [string]::IsNullOrWhiteSpace($packagedNodeModules)) {
    Sync-Directory $packagedNodeModules (Join-Path $installedBroker "node_modules")
    Write-Step "Using packaged broker production dependencies."
} elseif ($SkipDependencyInstall) {
    Write-Step "Skipping broker production dependency install by request."
} elseif ($DryRun) {
    Write-Step "Would run npm install --omit=dev in $installedBroker"
} else {
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) {
        throw "npm.cmd not found. Install Node 24 with npm, or rerun with -SkipDependencyInstall after staging node_modules manually."
    }

    Push-Location $installedBroker
    try {
        & $npm.Source install --omit=dev --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }
}

$launcher = Join-Path $InstallRoot "launch-revit-mcp-next.cmd"
$launcherContent = @"
@echo off
setlocal
set "REVIT_MCP_NEXT_PIPE=revit-mcp-next"
set "REVIT_MCP_NEXT_VERSION=$releaseVersion"
"$nodeExe" "$installedBrokerEntry"
exit /b %ERRORLEVEL%
"@

if ($DryRun) {
    Write-Step "Would write launcher: $launcher"
} else {
    Set-Content -LiteralPath $launcher -Value $launcherContent -Encoding ASCII
}

foreach ($year in $RevitYears) {
    $addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $addinPath = Join-Path $addinDir "RevitMcpNext.addin"
    $assemblyPath = Join-Path $installedAddin "RevitMcpNext.Addin.dll"
    $manifest = (Get-Content -LiteralPath $addinTemplate -Raw).Replace("{{ASSEMBLY_PATH}}", $assemblyPath)

    if ($DryRun) {
        Write-Step "Would install add-in manifest for Revit $year at $addinPath"
    } else {
        New-Item -ItemType Directory -Force -Path $addinDir | Out-Null
        Set-Content -LiteralPath $addinPath -Value $manifest -Encoding UTF8
    }
}

$receiptPackageRoot = $null
if ($sourceMode -eq "package") {
    $receiptPackageRoot = $resolvedPackageRoot
}

$installReceipt = [ordered] @{
    installedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    version = $releaseVersion
    sourceMode = $sourceMode
    packageRoot = $receiptPackageRoot
    installRoot = (Get-FullPath $InstallRoot)
    revitYears = $RevitYears
    packagedNodeModules = -not [string]::IsNullOrWhiteSpace($packagedNodeModules)
    checksumVerification = ($sourceMode -ne "package" -or -not $SkipChecksumVerification)
    nodePath = $nodeExe
}

$receiptPath = Join-Path $InstallRoot "install-receipt.json"
if ($DryRun) {
    Write-Step "Would write install receipt: $receiptPath"
} else {
    Set-Content -LiteralPath $receiptPath -Value ($installReceipt | ConvertTo-Json -Depth 6) -Encoding UTF8
}

Write-Step "MCP launcher command for clients:"
Write-Host "  cmd /c `"$launcher`""
Write-Step "Done. Open Revit, then run revit.status from Claude Code or Codex."
