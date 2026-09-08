param(
    [switch] $DryRun,
    [switch] $SkipDependencyInstall,
    [switch] $SkipChecksumVerification,
    [string[]] $RevitYears = @("2024"),
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [string] $NodePath = "",
    [string] $PackageRoot = "",
    [switch] $TrustRevitAlwaysLoad,
    [switch] $RequireSignedPackage,
    [switch] $RequireTrustedSignatures
)

$ErrorActionPreference = "Stop"
$addinClientId = "6F78E70D-BE13-4E0B-9B11-9E28F876AF71"

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

function Get-RelativePath($Root, $Path) {
    $rootFull = Add-TrailingSeparator (Get-FullPath $Root)
    $pathFull = Get-FullPath $Path
    $rootUri = New-Object System.Uri($rootFull)
    $pathUri = New-Object System.Uri($pathFull)
    return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
}

function Get-ManifestAssemblyPath($AddinDir, $AssemblyPath) {
    $addinDirFull = Add-TrailingSeparator (Get-FullPath $AddinDir)
    $assemblyFull = Get-FullPath $AssemblyPath
    if ($assemblyFull.StartsWith($addinDirFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        return (Get-RelativePath $addinDir $assemblyFull).Replace("\", "/")
    }

    return $assemblyFull
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

        $actual = Get-Sha256Hash $filePath
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

function Copy-FileWithinRoot($Source, $Destination, $AllowedRoot, [switch] $Optional) {
    if ($Optional -and -not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        return
    }

    Resolve-RequiredFile $Source "Required Revit-visible payload file was not found." | Out-Null
    Assert-PathChild $AllowedRoot $Destination "Revit-visible payload target"

    if ($DryRun) {
        Write-Step "Would copy Revit-visible payload file $Source -> $Destination"
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
        (Get-Sha256Hash $Source) -eq (Get-Sha256Hash $Destination)) {
        return
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function New-AuthToken {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    } finally {
        $rng.Dispose()
    }

    return [Convert]::ToBase64String($bytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

function Test-AuthTokenShape($Token) {
    return -not [string]::IsNullOrWhiteSpace($Token) -and $Token -match "^[A-Za-z0-9_-]{43,}$"
}

function Read-AuthTokenConfig($Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ""
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*"?([^"\s]+)"?\s*$') {
            return $Matches[1]
        }
    }

    return ""
}

function Write-AuthTokenConfig($Path, $Token) {
    $content = @(
        "# Revit MCP Next local auth config",
        "AUTH_CONFIG_VERSION=1",
        "REVIT_MCP_NEXT_AUTH_TOKEN=$Token",
        "CREATED_AT_UTC=$((Get-Date).ToUniversalTime().ToString("o"))"
    )

    Set-Content -LiteralPath $Path -Value $content -Encoding ASCII
}

function Set-PrivatePathAcl($Path, [switch] $Container) {
    try {
        $currentUserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $systemSid = New-Object System.Security.Principal.SecurityIdentifier ([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
        $administratorsSid = New-Object System.Security.Principal.SecurityIdentifier ([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]::None
        if ($Container) {
            $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
            $acl = New-Object System.Security.AccessControl.DirectorySecurity
        } else {
            $acl = New-Object System.Security.AccessControl.FileSecurity
        }

        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @($currentUserSid, $systemSid, $administratorsSid)) {
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                $sid,
                [System.Security.AccessControl.FileSystemRights]::FullControl,
                $inheritanceFlags,
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow
            )
            $acl.AddAccessRule($rule) | Out-Null
        }

        if ($PSVersionTable.PSEdition -eq "Core") {
            $item = if ($Container) { [System.IO.DirectoryInfo]::new($Path) } else { [System.IO.FileInfo]::new($Path) }
            [System.IO.FileSystemAclExtensions]::SetAccessControl($item, $acl)
        } elseif ($Container) {
            [System.IO.Directory]::SetAccessControl($Path, $acl)
        } else {
            [System.IO.File]::SetAccessControl($Path, $acl)
        }
        return $true
    } catch {
        throw "Unable to restrict ACL for $Path. $($_.Exception.Message)"
    }
}

function Ensure-AuthTokenConfig($Path) {
    Assert-InstallChild $Path
    $configDir = Split-Path -Parent $Path
    Assert-InstallChild $configDir

    if ($DryRun) {
        Write-Step "Would ensure per-install auth token config: $Path"
        return [ordered] @{
            path = $Path
            token = ""
            created = $false
            aclRestricted = $false
        }
    }

    New-Item -ItemType Directory -Force -Path $configDir | Out-Null

    $token = Read-AuthTokenConfig $Path
    $created = $false
    if (-not (Test-AuthTokenShape $token)) {
        $token = New-AuthToken
        Write-AuthTokenConfig $Path $token
        $created = $true
        Write-Step "Generated per-install auth token config."
    } else {
        Write-Step "Using existing per-install auth token config."
    }

    $dirAclRestricted = Set-PrivatePathAcl $configDir -Container
    $fileAclRestricted = Set-PrivatePathAcl $Path

    return [ordered] @{
        path = $Path
        token = $token
        created = $created
        aclRestricted = ($dirAclRestricted -and $fileAclRestricted)
    }
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
            throw "Revit $year install is not supported yet. Supported Revit years: 2021, 2024, 2027."
        }
    }

    $script:RevitYears = @($normalizedYears | Sort-Object -Unique)
}

function Resolve-RepositoryAddinOutput($RepoRootPath, [int] $Year) {
    $yearOutput = Join-Path $RepoRootPath "artifacts\addin\$Year"
    if (Test-Path -LiteralPath $yearOutput -PathType Container) {
        return (Resolve-Path -LiteralPath $yearOutput).Path
    }

    if ($Year -eq 2024) {
        foreach ($legacyOutput in @(
            (Join-Path $RepoRootPath "addin\RevitMcpNext.Addin\bin\Release\net48"),
            (Join-Path $RepoRootPath "addin\RevitMcpNext.Addin\bin\Debug\net48")
        )) {
            if (Test-Path -LiteralPath $legacyOutput -PathType Container) {
                Write-Step "Using legacy direct add-in output for Revit 2024: $legacyOutput"
                return (Resolve-Path -LiteralPath $legacyOutput).Path
            }
        }
    }

    throw "Year-specific Revit $Year add-in output is missing. Expected: $yearOutput. Build it with scripts\build-addin.ps1 -RevitYear $Year."
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

    return "0.3.0"
}

Assert-SupportedRevitYears
$RevitYears = @($RevitYears | Sort-Object -Unique)
Assert-SafeInstallRoot
$InstallRoot = Get-FullPath $InstallRoot

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
$signatureVerification = "not-requested"

if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) {
    $sourceMode = "package"
    $resolvedPackageRoot = Resolve-RequiredDirectory $PackageRoot "Package root was not found."
    if (-not $SkipChecksumVerification) {
        Test-PackageChecksums $resolvedPackageRoot
    } else {
        Write-Step "Skipping package checksum verification by request."
    }

    $releaseManifestPath = Join-Path $resolvedPackageRoot "release-manifest.json"
    if (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf) {
        $releaseManifest = Read-JsonFile $releaseManifestPath
        $packagedYears = @($releaseManifest.package.revitYears | ForEach-Object { [int] $_ })
        foreach ($year in $RevitYears) {
            if ($packagedYears -notcontains $year) {
                throw "Package does not advertise a Revit $year add-in artifact."
            }
        }
    }

    if ($RequireSignedPackage -or $RequireTrustedSignatures) {
        if (-not (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf)) {
            throw "Signed-package verification requires release-manifest.json."
        }
        if ($releaseManifest.signing.requested -ne $true) {
            throw "This package is explicitly unsigned. A signed production package is required by the selected installer policy."
        }
        $signScript = Resolve-RequiredFile (Join-Path $resolvedPackageRoot "scripts\sign-release.ps1") "Packaged signing verifier is missing."
        $signArguments = @("-PackageRoot", $resolvedPackageRoot, "-VerifyOnly", "-RequireSigned")
        if ($RequireTrustedSignatures) {
            $signArguments += "-RequireTrusted"
        }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $signScript @signArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Package Authenticode verification failed with exit code $LASTEXITCODE."
        }
        $signatureVerification = if ($RequireTrustedSignatures) { "trusted" } else { "signed" }
    }
} elseif ($RequireSignedPackage -or $RequireTrustedSignatures) {
    throw "Signed-package verification is available only when installing an extracted package with -PackageRoot."
}

$nodeExe = Resolve-NodeExe
Assert-NodeVersion $nodeExe
$releaseVersion = Get-ReleaseVersion $sourceMode $resolvedPackageRoot $repoRoot
$revitTrustScript = ""
if ($TrustRevitAlwaysLoad) {
    if ($sourceMode -eq "package") {
        $revitTrustScript = Resolve-RequiredFile (Join-Path $resolvedPackageRoot "scripts\ensure-revit-addin-trust.ps1") "Packaged Revit trust helper is missing."
    } else {
        $revitTrustScript = Resolve-RequiredFile (Join-Path $repoRoot "scripts\ensure-revit-addin-trust.ps1") "Revit trust helper is missing."
    }
}

if ($sourceMode -eq "package") {
    $payloadRoot = Resolve-RequiredDirectory (Join-Path $resolvedPackageRoot "payload") "Package payload is missing."
    $brokerRuntimeDist = Resolve-RequiredDirectory (Join-Path $payloadRoot "broker\dist\src") "Packaged broker runtime output is missing."
    $contractsDist = Resolve-RequiredDirectory (Join-Path $payloadRoot "contracts\dist") "Packaged contracts output is missing."
    $schemasDir = Resolve-RequiredDirectory (Join-Path $payloadRoot "contracts\schemas") "Packaged contract schemas are missing."
    $brokerPackage = Resolve-RequiredFile (Join-Path $payloadRoot "broker\package.json") "Packaged broker package metadata is missing."
    $contractsPackage = Resolve-RequiredFile (Join-Path $payloadRoot "contracts\package.json") "Packaged contracts package metadata is missing."
    $brokerEntrySource = Resolve-RequiredFile (Join-Path $payloadRoot "broker\dist\src\index.js") "Packaged broker entry point is missing."
    $addinTemplate = Resolve-RequiredFile (Join-Path $payloadRoot "addin\RevitMcpNext.addin.template") "Packaged add-in manifest template is missing."
    $addinSources = [ordered] @{}
    foreach ($year in $RevitYears) {
        $yearPayload = Resolve-RequiredDirectory (Join-Path $payloadRoot "addin\$year") "Packaged Revit $year add-in payload is missing."
        $addinSources["$year"] = [ordered] @{
            directory = $yearPayload
            addinDll = Resolve-RequiredFile (Join-Path $yearPayload "RevitMcpNext.Addin.dll") "Packaged Revit $year add-in DLL is missing."
            contractsDll = Resolve-RequiredFile (Join-Path $yearPayload "RevitMcpNext.Contracts.dll") "Packaged Revit $year contracts DLL is missing."
        }
    }
    $packagedNodeModulesCandidate = Join-Path $payloadRoot "broker\node_modules"
    $integrationsCandidate = Join-Path $resolvedPackageRoot "integrations"
} else {
    $brokerDist = Resolve-RequiredDirectory (Join-Path $repoRoot "broker\dist") "Broker is not built. Run npm install and npm run build first."
    $brokerRuntimeDist = Resolve-RequiredDirectory (Join-Path $brokerDist "src") "Broker runtime output is missing."
    $contractsDist = Resolve-RequiredDirectory (Join-Path $repoRoot "contracts\dist") "Contracts are not built. Run npm install and npm run build first."
    $schemasDir = Resolve-RequiredDirectory (Join-Path $repoRoot "contracts\schemas") "Contracts schemas are missing."
    $brokerPackage = Resolve-RequiredFile (Join-Path $repoRoot "broker\package.json") "Broker package metadata is missing."
    $contractsPackage = Resolve-RequiredFile (Join-Path $repoRoot "contracts\package.json") "Contracts package metadata is missing."
    $brokerEntrySource = Resolve-RequiredFile (Join-Path $brokerDist "src\index.js") "Broker entry point is missing."
    $addinTemplate = Resolve-RequiredFile (Join-Path $repoRoot "addin\RevitMcpNext.Addin\RevitMcpNext.addin.template") "Add-in manifest template is missing."

    $addinSources = [ordered] @{}
    foreach ($year in $RevitYears) {
        $addinOut = Resolve-RepositoryAddinOutput $repoRoot $year
        $addinSources["$year"] = [ordered] @{
            directory = $addinOut
            addinDll = Resolve-RequiredFile (Join-Path $addinOut "RevitMcpNext.Addin.dll") "Revit $year add-in is not built."
            contractsDll = Resolve-RequiredFile (Join-Path $addinOut "RevitMcpNext.Contracts.dll") "Revit $year contracts DLL is missing from the add-in output."
        }
    }
    $packagedNodeModulesCandidate = ""
    $integrationsCandidate = Join-Path $repoRoot "integrations"
}

$brokerEntrySource | Out-Null
$brokerCtlSource = Resolve-RequiredFile (Join-Path $brokerRuntimeDist "cli\revitctl.js") "Broker revitctl entry point is missing."
$brokerCtlSource | Out-Null

$installedBroker = Join-Path $InstallRoot "broker"
$installedContracts = Join-Path $InstallRoot "contracts"
$installedAddin = Join-Path $InstallRoot "addin"
$installedIntegrations = Join-Path $InstallRoot "integrations"
$installedBrokerDist = Join-Path $installedBroker "dist"
$installedBrokerEntry = Join-Path $installedBroker "dist\src\index.js"
$installedRevitCtlEntry = Join-Path $installedBroker "dist\src\cli\revitctl.js"

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
foreach ($year in $RevitYears) {
    $source = $addinSources["$year"]
    $yearInstall = Join-Path $installedAddin "$year"
    Copy-File $source.addinDll (Join-Path $yearInstall "RevitMcpNext.Addin.dll")
    Copy-File $source.contractsDll (Join-Path $yearInstall "RevitMcpNext.Contracts.dll")
    Copy-OptionalFile (Join-Path $source.directory "RevitMcpNext.Addin.pdb") (Join-Path $yearInstall "RevitMcpNext.Addin.pdb")
    Copy-OptionalFile (Join-Path $source.directory "RevitMcpNext.Contracts.pdb") (Join-Path $yearInstall "RevitMcpNext.Contracts.pdb")
}
Sync-Directory $integrationsCandidate $installedIntegrations

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

# npm represents file: workspace dependencies as links on Windows. Those links can
# be left unusable after an in-place reinstall or when a staged install directory
# is moved. Keep the installed broker self-contained by replacing the contracts
# workspace link with a physical copy of the already-installed runtime package.
$installedContractsModule = Join-Path $installedBroker "node_modules\@revit-mcp-next\contracts"
Assert-InstallChild $installedContractsModule
if ($DryRun) {
    Write-Step "Would replace the broker contracts workspace dependency with a physical runtime copy."
} else {
    if (Test-Path -LiteralPath $installedContractsModule) {
        $contractsModuleItem = Get-Item -LiteralPath $installedContractsModule -Force
        if (($contractsModuleItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Remove-Item -LiteralPath $installedContractsModule -Force
        } else {
            Remove-Item -LiteralPath $installedContractsModule -Recurse -Force
        }
    }
    Sync-Directory $installedContracts $installedContractsModule
}

$launcher = Join-Path $InstallRoot "launch-revit-mcp-next.cmd"
$revitCtlLauncher = Join-Path $InstallRoot "revitctl.cmd"
$authConfig = Join-Path $InstallRoot "config\auth.env"
$clientDiscovery = Join-Path $InstallRoot "config\client-discovery.json"
$authConfigState = Ensure-AuthTokenConfig $authConfig
$launcherContent = @"
@echo off
setlocal
set "REVIT_MCP_NEXT_PIPE=revit-mcp-next"
set "REVIT_MCP_NEXT_VERSION=$releaseVersion"
set "REVIT_MCP_NEXT_AUTH_CONFIG=$authConfig"
for /f "usebackq tokens=1,* delims==" %%A in ("%REVIT_MCP_NEXT_AUTH_CONFIG%") do if /i "%%A"=="REVIT_MCP_NEXT_AUTH_TOKEN" set "REVIT_MCP_NEXT_AUTH_TOKEN=%%B"
if not defined REVIT_MCP_NEXT_AUTH_TOKEN (
  echo Revit MCP Next auth token config is missing or invalid at %REVIT_MCP_NEXT_AUTH_CONFIG%. 1>&2
  exit /b 126
)
"$nodeExe" "$installedBrokerEntry"
exit /b %ERRORLEVEL%
"@

if ($DryRun) {
    Write-Step "Would write launcher: $launcher"
} else {
    Set-Content -LiteralPath $launcher -Value $launcherContent -Encoding ASCII
    Set-PrivatePathAcl $launcher | Out-Null
}

$revitCtlContent = @"
@echo off
setlocal
set "REVIT_MCP_NEXT_PIPE=revit-mcp-next"
set "REVIT_MCP_NEXT_VERSION=$releaseVersion"
set "REVIT_MCP_NEXT_AUTH_CONFIG=$authConfig"
for /f "usebackq tokens=1,* delims==" %%A in ("%REVIT_MCP_NEXT_AUTH_CONFIG%") do if /i "%%A"=="REVIT_MCP_NEXT_AUTH_TOKEN" set "REVIT_MCP_NEXT_AUTH_TOKEN=%%B"
if not defined REVIT_MCP_NEXT_AUTH_TOKEN (
  echo Revit MCP Next auth token config is missing or invalid at %REVIT_MCP_NEXT_AUTH_CONFIG%. 1>&2
  exit /b 126
)
"$nodeExe" "$installedRevitCtlEntry" --discovery "$clientDiscovery" --auth-config "$authConfig" %*
exit /b %ERRORLEVEL%
"@

if ($DryRun) {
    Write-Step "Would write revitctl launcher: $revitCtlLauncher"
} else {
    Set-Content -LiteralPath $revitCtlLauncher -Value $revitCtlContent -Encoding ASCII
    Set-PrivatePathAcl $revitCtlLauncher | Out-Null
}

$addinAssemblyPaths = [ordered] @{}
$manifestAssemblyPaths = [ordered] @{}
foreach ($year in $RevitYears) {
    $addinAssemblyPaths["$year"] = (Get-FullPath (Join-Path $installedAddin "$year\RevitMcpNext.Addin.dll"))
    $payloadId = (Get-Sha256Hash $addinSources["$year"].addinDll).Substring(0, 12) + "-" +
        (Get-Sha256Hash $addinSources["$year"].contractsDll).Substring(0, 12)
    $manifestAssemblyPaths["$year"] = (Get-FullPath (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\RevitMcpNext\$payloadId\RevitMcpNext.Addin.dll"))
}
$primaryAddinAssemblyPath = $addinAssemblyPaths["$($RevitYears[0])"]

$clientDiscoveryContent = [ordered] @{
    schemaVersion = 1
    product = "revit-mcp-next"
    version = $releaseVersion
    installRoot = (Get-FullPath $InstallRoot)
    protocolVersion = "2026-08-11"
    pipeName = "revit-mcp-next"
    addinClientId = $addinClientId
    launcherPath = (Get-FullPath $launcher)
    revitctlPath = (Get-FullPath $revitCtlLauncher)
    authConfigPath = (Get-FullPath $authConfig)
    brokerEntryPath = (Get-FullPath $installedBrokerEntry)
    addinAssemblyPath = $primaryAddinAssemblyPath
    supportedRevitYears = $RevitYears
    addinAssemblyPaths = $addinAssemblyPaths
    manifestAssemblyPaths = $manifestAssemblyPaths
    integrationsPath = (Get-FullPath $installedIntegrations)
    pythonClientPath = (Get-FullPath (Join-Path $installedIntegrations "python\revit_mcp_next_client.py"))
    pythonInProcessHelperPath = (Get-FullPath (Join-Path $installedIntegrations "python\revit_mcp_next_inprocess.py"))
    contractSchemasPath = (Get-FullPath (Join-Path $installedContracts "schemas"))
    tools = @(
        "revit.list_instances",
        "revit.set_target",
        "revit.get_target",
        "revit.clear_target",
        "revit.bridge_health",
        "revit.get_request_result",
        "revit.status",
        "revit.read_bundle",
        "revit.list_documents",
        "revit.create_project_from_template",
        "revit.get_levels",
        "revit.get_views",
        "revit.get_sheets",
        "revit.get_schedules",
        "revit.get_schedule_fields",
        "revit.get_current_view",
        "revit.get_current_view_elements",
        "revit.get_selection",
        "revit.analyze_model",
        "revit.get_model_readiness",
        "revit.get_model_context",
        "revit.get_material_quantities",
        "revit.get_warnings",
        "revit.get_rooms",
        "revit.catalog",
        "revit.query",
        "revit.describe_parameters",
        "revit.preview_change_set",
        "revit.apply_change_set",
        "revit.cancel_request",
        "revit.create_model_delivery_fixture",
        "revit.inspect_model_delivery",
        "revit.save_model_delivery_recipe",
        "revit.get_model_delivery_recipe",
        "revit.list_model_delivery_recipes",
        "revit.preview_model_delivery",
        "revit.execute_model_delivery",
        "revit.get_model_delivery_status",
        "revit.cancel_model_delivery"
    )
    catalogKinds = @("elementTypes", "familySymbols", "titleBlocks", "viewFamilyTypes", "textNoteTypes", "dimensionTypes", "tagTypes")
    writeOperations = @(
        "set_parameter",
        "create_level",
        "create_wall",
        "place_family_instance",
        "create_sheet",
        "place_view_on_sheet",
        "create_schedule",
        "add_schedule_field",
        "place_schedule_on_sheet",
        "create_text_note",
        "load_family",
        "tag_room",
        "tag_element",
        "create_grid",
        "create_floor",
        "create_room",
        "move_element",
        "rotate_element",
        "copy_element",
        "change_element_type",
        "rename_element_type",
        "duplicate_element_type",
        "set_element_pinned",
        "delete_element"
    )
}

if ($DryRun) {
    Write-Step "Would write client discovery config: $clientDiscovery"
} else {
    Set-Content -LiteralPath $clientDiscovery -Value ($clientDiscoveryContent | ConvertTo-Json -Depth 6) -Encoding UTF8
    Set-PrivatePathAcl $clientDiscovery | Out-Null
}

foreach ($year in $RevitYears) {
    $addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $addinPath = Join-Path $addinDir "RevitMcpNext.addin"
    $assemblyPath = $manifestAssemblyPaths["$year"]
    $runtimeDir = Split-Path -Parent $assemblyPath
    $manifestAssemblyPath = Get-ManifestAssemblyPath $addinDir $assemblyPath
    $manifest = (Get-Content -LiteralPath $addinTemplate -Raw).Replace("{{ASSEMBLY_PATH}}", $manifestAssemblyPath)

    if ($DryRun) {
        Write-Step "Would stage Revit-visible add-in payload for Revit $year at $runtimeDir"
        Write-Step "Would install add-in manifest for Revit $year at $addinPath"
    } else {
        New-Item -ItemType Directory -Force -Path $addinDir | Out-Null
        New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
        Copy-FileWithinRoot $addinAssemblyPaths["$year"] (Join-Path $runtimeDir "RevitMcpNext.Addin.dll") $addinDir
        Copy-FileWithinRoot (Join-Path $installedAddin "$year\RevitMcpNext.Contracts.dll") (Join-Path $runtimeDir "RevitMcpNext.Contracts.dll") $addinDir
        Copy-FileWithinRoot (Join-Path $installedAddin "$year\RevitMcpNext.Addin.pdb") (Join-Path $runtimeDir "RevitMcpNext.Addin.pdb") $addinDir -Optional
        Copy-FileWithinRoot (Join-Path $installedAddin "$year\RevitMcpNext.Contracts.pdb") (Join-Path $runtimeDir "RevitMcpNext.Contracts.pdb") $addinDir -Optional
        Set-Content -LiteralPath $addinPath -Value $manifest -Encoding UTF8
    }
}

if ($TrustRevitAlwaysLoad) {
    $trustArgs = @("-RevitYears")
    foreach ($year in $RevitYears) {
        $trustArgs += "$year"
    }
    $trustArgs += @("-ClientId", $addinClientId)
    if ($DryRun) {
        $trustArgs += "-DryRun"
    }

    Write-Step "Seeding Revit Always Load trust for local/test install."
    & powershell -NoProfile -ExecutionPolicy Bypass -File $revitTrustScript @trustArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Revit Always Load trust helper failed with exit code $LASTEXITCODE."
    }
}

$receiptPackageRoot = $null
if ($sourceMode -eq "package") {
    $receiptPackageRoot = $resolvedPackageRoot
}

$installReceipt = [ordered] @{
    product = "revit-mcp-next"
    installedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    version = $releaseVersion
    sourceMode = $sourceMode
    packageRoot = $receiptPackageRoot
    installRoot = (Get-FullPath $InstallRoot)
    revitYears = $RevitYears
    addinClientId = $addinClientId
    revitAlwaysLoadTrust = [ordered] @{
        requested = [bool] $TrustRevitAlwaysLoad
        helper = $revitTrustScript
    }
    packagedNodeModules = -not [string]::IsNullOrWhiteSpace($packagedNodeModules)
    checksumVerification = ($sourceMode -ne "package" -or -not $SkipChecksumVerification)
    signatureVerification = $signatureVerification
    nodePath = $nodeExe
    authConfig = [ordered] @{
        path = $authConfig
        created = $authConfigState.created
        aclRestricted = $authConfigState.aclRestricted
    }
    clientDiscovery = $clientDiscovery
    manifestAssemblyPaths = $manifestAssemblyPaths
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
