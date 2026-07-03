[CmdletBinding()]
param(
    [switch]$Help,
    [string]$DocumentFingerprint,
    [double]$WallLengthMm = 4000,
    [double]$MoveYMm = 250,
    [double]$WallHeightMm = 3000,
    [string]$TransactionPrefix = "Revit MCP Next smoke",
    [string]$LauncherPath,
    [int]$ExpectedRevitYear = 0,
    [string]$SummaryPath = "",
    [switch]$RequireTypeChange,
    [switch]$RequireRoomTag,
    [switch]$RequireElementTag,
    [switch]$RequireTags,
    [switch]$StatusOnly
)

$ErrorActionPreference = "Stop"

function Fail-Friendly {
    param([string]$Message)
    [Console]::Error.WriteLine($Message)
    exit 1
}

function Format-InvariantNumber {
    param([double]$Value)
    return $Value.ToString("G", [System.Globalization.CultureInfo]::InvariantCulture)
}

$scriptPath = Join-Path $PSScriptRoot "live-smoke-revit.mjs"
if (-not (Test-Path -LiteralPath $scriptPath)) {
    Fail-Friendly "Cannot find Node smoke script at $scriptPath."
}

$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCommand) {
    Fail-Friendly "Node.js was not found on PATH. Install Node.js 24.x or open a shell where node is available."
}

if ($Help) {
    & $nodeCommand.Source $scriptPath --help
    exit $LASTEXITCODE
}

if ($WallLengthMm -le 0) {
    Fail-Friendly "-WallLengthMm must be greater than zero."
}

if ($WallHeightMm -le 0) {
    Fail-Friendly "-WallHeightMm must be greater than zero."
}

if ($MoveYMm -eq 0) {
    Fail-Friendly "-MoveYMm must be non-zero because Revit rejects zero-length moves."
}

if ([string]::IsNullOrWhiteSpace($TransactionPrefix)) {
    Fail-Friendly "-TransactionPrefix cannot be empty."
}

if ([string]::IsNullOrWhiteSpace($LauncherPath)) {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        Fail-Friendly "LOCALAPPDATA is not set. Pass -LauncherPath explicitly."
    }

    $LauncherPath = Join-Path $env:LOCALAPPDATA "RevitMcpNext\launch-revit-mcp-next.cmd"
}

if (-not (Test-Path -LiteralPath $LauncherPath)) {
    Fail-Friendly "MCP launcher was not found at $LauncherPath. Run installer\install-windows.ps1 first, or pass -LauncherPath."
}

$nodeArgs = @(
    $scriptPath,
    "--wall-length-mm", (Format-InvariantNumber $WallLengthMm),
    "--move-y-mm", (Format-InvariantNumber $MoveYMm),
    "--wall-height-mm", (Format-InvariantNumber $WallHeightMm),
    "--transaction-prefix", $TransactionPrefix,
    "--launcher-path", $LauncherPath
)

if ($RequireTypeChange) {
    $nodeArgs += @("--require-type-change")
}

if ($RequireTags) {
    $nodeArgs += @("--require-tags")
} else {
    if ($RequireRoomTag) {
        $nodeArgs += @("--require-room-tag")
    }

    if ($RequireElementTag) {
        $nodeArgs += @("--require-element-tag")
    }
}

if ($StatusOnly) {
    $nodeArgs += @("--status-only")
}

if ($ExpectedRevitYear -gt 0) {
    $nodeArgs += @("--expected-revit-year", "$ExpectedRevitYear")
}

if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
    $nodeArgs += @("--summary-path", $SummaryPath)
}

if (-not [string]::IsNullOrWhiteSpace($DocumentFingerprint)) {
    $nodeArgs += @("--document-fingerprint", $DocumentFingerprint)
}

Write-Host "Running live Revit smoke through node..."
& $nodeCommand.Source @nodeArgs
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    [Console]::Error.WriteLine("Live Revit smoke failed with exit code $exitCode. Confirm Revit is running, the add-in is loaded, and an active project document is open.")
    exit $exitCode
}

if ($StatusOnly) {
    Write-Host "Live Revit status probe completed successfully."
} else {
    Write-Host "Live Revit smoke completed successfully."
}
