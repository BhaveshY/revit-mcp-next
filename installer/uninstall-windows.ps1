[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [string[]] $RevitYears = @("2021", "2024", "2027"),
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [switch] $RemoveTrust,
    [switch] $KeepConfig,
    [switch] $Force
)

$ErrorActionPreference = "Stop"
$clientId = "6F78E70D-BE13-4E0B-9B11-9E28F876AF71"
$installRootFull = [System.IO.Path]::GetFullPath($InstallRoot)
$receiptPath = Join-Path $installRootFull "install-receipt.json"
$years = @($RevitYears | ForEach-Object { ([string] $_).Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) } | ForEach-Object { [int] $_.Trim() } | Sort-Object -Unique)

if ((Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close Revit before uninstalling the connector. The uninstaller never closes Revit sessions."
}
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
    throw "Install receipt was not found; refusing to remove an unverified directory: $receiptPath"
}
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ([string] $receipt.product -ne "revit-mcp-next" -or [System.IO.Path]::GetFullPath([string] $receipt.installRoot) -ne $installRootFull) {
    throw "Install receipt does not identify this exact Revit MCP Next install root."
}

foreach ($year in $years) {
    $addinRoot = [System.IO.Path]::GetFullPath((Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"))
    $manifest = Join-Path $addinRoot "RevitMcpNext.addin"
    $runtime = Join-Path $addinRoot "RevitMcpNext"
    if ($PSCmdlet.ShouldProcess("Revit $year add-in files", "Remove")) {
        if (Test-Path -LiteralPath $manifest -PathType Leaf) { Remove-Item -LiteralPath $manifest -Force }
        if (Test-Path -LiteralPath $runtime -PathType Container) { Remove-Item -LiteralPath $runtime -Recurse -Force }
    }
}

if ($RemoveTrust) {
    $trustScript = Join-Path $installRootFull "scripts\ensure-revit-addin-trust.ps1"
    if (-not (Test-Path -LiteralPath $trustScript -PathType Leaf)) {
        $trustScript = Join-Path $PSScriptRoot "..\scripts\ensure-revit-addin-trust.ps1"
    }
    if (Test-Path -LiteralPath $trustScript -PathType Leaf) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $trustScript -RevitYears $years -ClientId $clientId -Remove
        if ($LASTEXITCODE -ne 0) { throw "Revit trust removal failed with exit code $LASTEXITCODE." }
    }
}

if ($KeepConfig) {
    $configBackup = Join-Path ([System.IO.Path]::GetTempPath()) ("RevitMcpNext-config-" + [Guid]::NewGuid().ToString("N"))
    $config = Join-Path $installRootFull "config"
    if (Test-Path -LiteralPath $config -PathType Container) { Move-Item -LiteralPath $config -Destination $configBackup }
    if ($PSCmdlet.ShouldProcess($installRootFull, "Remove verified connector install")) { Remove-Item -LiteralPath $installRootFull -Recurse -Force }
    if (Test-Path -LiteralPath $configBackup -PathType Container) {
        New-Item -ItemType Directory -Force -Path $installRootFull | Out-Null
        Move-Item -LiteralPath $configBackup -Destination (Join-Path $installRootFull "config")
    }
} elseif ($PSCmdlet.ShouldProcess($installRootFull, "Remove verified connector install")) {
    Remove-Item -LiteralPath $installRootFull -Recurse -Force
}

Write-Host "[revit-mcp-next uninstall] Removed connector files. Recovery requires reinstall or a pilot rollback backup."
