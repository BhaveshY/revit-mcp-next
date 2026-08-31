[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet("ValidatePackage", "Install", "Status", "Rollback", "Uninstall")]
    [string] $Action = "Status",
    [string] $PackageRoot = "",
    [string[]] $RevitYears = @("2024", "2027"),
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [string] $BackupRoot = "$env:LOCALAPPDATA\RevitMcpNext-PilotBackups",
    [string] $BackupPath = "",
    [string] $NodePath = ""
)

$ErrorActionPreference = "Stop"
$clientId = "6F78E70D-BE13-4E0B-9B11-9E28F876AF71"
$installRootFull = [System.IO.Path]::GetFullPath($InstallRoot)
$backupRootFull = [System.IO.Path]::GetFullPath($BackupRoot)
$years = @($RevitYears | ForEach-Object { ([string] $_).Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) } | ForEach-Object { [int] $_.Trim() } | Sort-Object -Unique)

function Assert-RevitClosed {
    if ((Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0) {
        throw "Close Revit before $Action. This pilot helper never closes Revit sessions."
    }
}

function Read-Json($Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required JSON file was not found: $Path" }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-Sha256($Path) {
    $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($Path))
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
        } finally {
            $sha256.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Get-AddinPaths($Year) {
    $root = [System.IO.Path]::GetFullPath((Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"))
    return [ordered] @{
        root = $root
        manifest = (Join-Path $root "RevitMcpNext.addin")
        runtime = (Join-Path $root "RevitMcpNext")
    }
}

function Assert-ChildPath($Root, $Path, $Label) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is outside the controlled root. Root: $Root Path: $Path"
    }
}

function Get-TrustStatus($TrustScript) {
    $json = & powershell -NoProfile -ExecutionPolicy Bypass -File $TrustScript -RevitYears $years -ClientId $clientId -StatusOnly -Json
    if ($LASTEXITCODE -ne 0) { throw "Revit trust status failed with exit code $LASTEXITCODE." }
    return ($json | Out-String) | ConvertFrom-Json
}

function Get-PilotState {
    return Read-Json (Join-Path $installRootFull "pilot-state.json")
}

function Assert-PilotPackage($Root) {
    $rootFull = (Resolve-Path -LiteralPath $Root).Path
    $manifest = Read-Json (Join-Path $rootFull "release-manifest.json")
    $version = [string] $manifest.package.version
    if ($version -notmatch '^\d+\.\d+\.\d+-rc\.\d+$') { throw "Pilot installation requires a versioned RC package. Received: $version" }
    if ($manifest.signing.requested -ne $false -or [string] $manifest.sharing.signingMode -ne "unsigned" -or $manifest.sharing.publicTrust -ne $false) {
        throw "This helper accepts only an explicitly unsigned controlled-pilot package."
    }
    foreach ($year in $years) {
        if (@($manifest.package.revitYears | ForEach-Object { [int] $_ }) -notcontains $year) { throw "Package does not contain Revit $year." }
    }
    $checksumFile = Join-Path $rootFull "CHECKSUMS.sha256"
    $verified = 0
    foreach ($line in Get-Content -LiteralPath $checksumFile) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([a-fA-F0-9]{64})\s{2}(.+)$') { throw "Invalid package checksum line: $line" }
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $rootFull $Matches[2].Replace('/', '\')))
        Assert-ChildPath $rootFull $candidate "Package checksum target"
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Package checksum target is missing: $candidate" }
        $actual = Get-Sha256 $candidate
        if (-not $actual.Equals($Matches[1], [System.StringComparison]::OrdinalIgnoreCase)) { throw "Package checksum mismatch: $candidate" }
        $verified++
    }
    if ($verified -eq 0) { throw "Package checksum list was empty." }
    return [ordered] @{ root = $rootFull; manifest = $manifest; version = $version; verifiedFiles = $verified }
}

if ($Action -eq "ValidatePackage") {
    if ([string]::IsNullOrWhiteSpace($PackageRoot)) { throw "-PackageRoot is required for package validation." }
    $validated = Assert-PilotPackage $PackageRoot
    [ordered] @{ status = "validated"; profile = "controlled-pilot-unsigned"; version = $validated.version; verifiedFiles = $validated.verifiedFiles; revitYears = $years } | ConvertTo-Json -Depth 5
    return
}

if ($Action -eq "Install") {
    Assert-RevitClosed
    if ([string]::IsNullOrWhiteSpace($PackageRoot)) { throw "-PackageRoot is required for pilot installation." }
    $validated = Assert-PilotPackage $PackageRoot
    $packageRootFull = $validated.root
    $manifest = $validated.manifest
    $version = $validated.version

    New-Item -ItemType Directory -Force -Path $backupRootFull | Out-Null
    $backupName = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ") + "-" + $version
    $resolvedBackup = Join-Path $backupRootFull $backupName
    Assert-ChildPath $backupRootFull $resolvedBackup "Pilot backup"
    if (Test-Path -LiteralPath $resolvedBackup) { throw "Pilot backup already exists: $resolvedBackup" }
    New-Item -ItemType Directory -Path $resolvedBackup | Out-Null

    $trustScript = Join-Path $packageRootFull "scripts\ensure-revit-addin-trust.ps1"
    $trustBefore = Get-TrustStatus $trustScript
    $hadInstall = Test-Path -LiteralPath $installRootFull -PathType Container
    if ($hadInstall) { Copy-Item -LiteralPath $installRootFull -Destination (Join-Path $resolvedBackup "install-root") -Recurse }
    foreach ($year in $years) {
        $paths = Get-AddinPaths $year
        $yearBackup = Join-Path $resolvedBackup "revit-addins\$year"
        New-Item -ItemType Directory -Force -Path $yearBackup | Out-Null
        if (Test-Path -LiteralPath $paths.manifest -PathType Leaf) { Copy-Item -LiteralPath $paths.manifest -Destination $yearBackup }
        if (Test-Path -LiteralPath $paths.runtime -PathType Container) { Copy-Item -LiteralPath $paths.runtime -Destination $yearBackup -Recurse }
    }
    $backupState = [ordered] @{ schemaVersion = 1; version = $version; installRoot = $installRootFull; hadInstall = $hadInstall; revitYears = $years; trustBefore = $trustBefore }
    Set-Content -LiteralPath (Join-Path $resolvedBackup "backup-state.json") -Value ($backupState | ConvertTo-Json -Depth 7) -Encoding UTF8

    $installer = Join-Path $packageRootFull "installer\install-windows.ps1"
    $installArgs = @("-PackageRoot", $packageRootFull, "-InstallRoot", $installRootFull, "-RevitYears", ($years -join ','), "-TrustRevitAlwaysLoad")
    if (-not [string]::IsNullOrWhiteSpace($NodePath)) { $installArgs += @("-NodePath", $NodePath) }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer @installArgs
    if ($LASTEXITCODE -ne 0) { throw "Pilot installation failed. Existing files are retained at $resolvedBackup; run Rollback with that path." }

    $receipt = Read-Json (Join-Path $installRootFull "install-receipt.json")
    if ([string] $receipt.version -ne $version -or $receipt.checksumVerification -ne $true) { throw "Installed receipt did not verify the exact RC package/checksums." }
    $trustAfter = Get-TrustStatus $trustScript
    if (@($trustAfter.entries | Where-Object { -not $_.present -or [int] $_.value -ne 1 }).Count -gt 0) { throw "Pilot Always Load trust verification failed." }
    foreach ($year in $years) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $packageRootFull "scripts\doctor.ps1") -InstallRoot $installRootFull -RevitYear $year
        if ($LASTEXITCODE -ne 0) { throw "Installed Revit $year verification failed." }
    }

    $pilotState = [ordered] @{
        schemaVersion = 1
        profile = "controlled-pilot-unsigned"
        installedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        version = $version
        packageRoot = $packageRootFull
        packageChecksumsSha256 = Get-Sha256 (Join-Path $packageRootFull "CHECKSUMS.sha256")
        backupPath = $resolvedBackup
        revitYears = $years
        trustVerified = $true
        signed = $false
        publicTrust = $false
    }
    Set-Content -LiteralPath (Join-Path $installRootFull "pilot-state.json") -Value ($pilotState | ConvertTo-Json -Depth 6) -Encoding UTF8
    $pilotState | ConvertTo-Json -Depth 6
    return
}

if ($Action -eq "Status") {
    $state = Get-PilotState
    $receipt = Read-Json (Join-Path $installRootFull "install-receipt.json")
    $trustScript = if (Test-Path -LiteralPath (Join-Path ([string] $state.packageRoot) "scripts\ensure-revit-addin-trust.ps1")) {
        Join-Path ([string] $state.packageRoot) "scripts\ensure-revit-addin-trust.ps1"
    } else { Join-Path $PSScriptRoot "..\scripts\ensure-revit-addin-trust.ps1" }
    $trust = Get-TrustStatus $trustScript
    $files = @($years | ForEach-Object {
        $paths = Get-AddinPaths $_
        [ordered] @{ revitYear = $_; manifestPresent = (Test-Path -LiteralPath $paths.manifest -PathType Leaf); addinDllPresent = (Test-Path -LiteralPath (Join-Path $paths.runtime "RevitMcpNext.Addin.dll") -PathType Leaf) }
    })
    [ordered] @{ status = "inspected"; state = $state; receiptVersion = $receipt.version; trust = $trust; files = $files } | ConvertTo-Json -Depth 8
    return
}

Assert-RevitClosed
$state = Get-PilotState
$uninstaller = Join-Path ([string] $state.packageRoot) "installer\uninstall-windows.ps1"
if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { $uninstaller = Join-Path $PSScriptRoot "uninstall-windows.ps1" }
& powershell -NoProfile -ExecutionPolicy Bypass -File $uninstaller -InstallRoot $installRootFull -RevitYears ($years -join ',') -RemoveTrust -Confirm:$false
if ($LASTEXITCODE -ne 0) { throw "Pilot uninstall failed with exit code $LASTEXITCODE." }

if ($Action -eq "Uninstall") {
    [ordered] @{ status = "uninstalled"; version = $state.version; recoverableFrom = $state.backupPath } | ConvertTo-Json
    return
}

$resolvedBackup = if ([string]::IsNullOrWhiteSpace($BackupPath)) { [string] $state.backupPath } else { [System.IO.Path]::GetFullPath($BackupPath) }
Assert-ChildPath $backupRootFull $resolvedBackup "Pilot rollback backup"
$backupState = Read-Json (Join-Path $resolvedBackup "backup-state.json")
if ([string] $backupState.installRoot -ne $installRootFull) { throw "Backup belongs to a different install root." }
if ($backupState.hadInstall -eq $true) {
    Copy-Item -LiteralPath (Join-Path $resolvedBackup "install-root") -Destination $installRootFull -Recurse
}
foreach ($year in $years) {
    $paths = Get-AddinPaths $year
    $yearBackup = Join-Path $resolvedBackup "revit-addins\$year"
    if (Test-Path -LiteralPath (Join-Path $yearBackup "RevitMcpNext.addin") -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path $paths.root | Out-Null
        Copy-Item -LiteralPath (Join-Path $yearBackup "RevitMcpNext.addin") -Destination $paths.manifest
    }
    if (Test-Path -LiteralPath (Join-Path $yearBackup "RevitMcpNext") -PathType Container) {
        Copy-Item -LiteralPath (Join-Path $yearBackup "RevitMcpNext") -Destination $paths.root -Recurse
    }
}
$trustPreviouslyPresent = @($backupState.trustBefore.entries | Where-Object { $_.present -and [int] $_.value -eq 1 } | ForEach-Object { [int] $_.revitYear })
if ($trustPreviouslyPresent.Count -gt 0) {
    $trustScript = Join-Path $PSScriptRoot "..\scripts\ensure-revit-addin-trust.ps1"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $trustScript -RevitYears $trustPreviouslyPresent -ClientId $clientId
    if ($LASTEXITCODE -ne 0) { throw "Previous Revit trust state could not be restored." }
}
[ordered] @{ status = "rolled-back"; restoredFrom = $resolvedBackup; restoredPreviousInstall = [bool] $backupState.hadInstall } | ConvertTo-Json
