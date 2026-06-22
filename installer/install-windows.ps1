param(
    [switch] $DryRun,
    [int[]] $RevitYears = @(2024),
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext"
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next] $Message"
}

function Invoke-Checked($ScriptBlock) {
    if ($DryRun) {
        Write-Step "DRY RUN: $ScriptBlock"
        return
    }
    & $ScriptBlock
}

Write-Step "Installing Revit MCP Next"
Write-Step "Install root: $InstallRoot"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$brokerEntry = Join-Path $repoRoot "broker\dist\index.js"
$addinTemplate = Join-Path $repoRoot "addin\RevitMcpNext.Addin\RevitMcpNext.addin.template"

if (-not (Test-Path $brokerEntry)) {
    throw "Broker is not built. Run npm install and npm run build first. Missing: $brokerEntry"
}

if (-not (Test-Path $addinTemplate)) {
    throw "Missing add-in manifest template: $addinTemplate"
}

if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
}

$launcher = Join-Path $InstallRoot "launch-revit-mcp-next.cmd"
$launcherContent = @"
@echo off
setlocal
set "NODE_EXE="
for /f "delims=" %%I in ('where node.exe 2^>NUL') do (
  if not defined NODE_EXE set "NODE_EXE=%%I"
)
if not defined NODE_EXE (
  echo Revit MCP Next startup error: node.exe not found. Install the packaged broker build or Node 22 LTS. 1>&2
  exit /b 127
)
"%NODE_EXE%" "$brokerEntry"
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
    $assemblyPath = Join-Path $InstallRoot "RevitMcpNext.Addin.dll"
    $manifest = (Get-Content -LiteralPath $addinTemplate -Raw).Replace("{{ASSEMBLY_PATH}}", $assemblyPath)

    if ($DryRun) {
        Write-Step "Would install add-in manifest for Revit $year at $addinPath"
    } else {
        New-Item -ItemType Directory -Force -Path $addinDir | Out-Null
        Set-Content -LiteralPath $addinPath -Value $manifest -Encoding UTF8
    }
}

Write-Step "MCP launcher command for clients:"
Write-Host "  cmd /c `"$launcher`""

Write-Step "Done. Open Revit, then run the MCP tool revit.status from Claude or Codex."

