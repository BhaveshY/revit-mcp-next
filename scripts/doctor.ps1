param(
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [int] $RevitYear = 2024
)

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]

function Test-RequiredFile($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "[ok] $Label"
    } else {
        Write-Host "[missing] $Label -> $Path"
        $failures.Add("$Label missing: $Path")
    }
}

function Test-RequiredDirectory($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Container) {
        Write-Host "[ok] $Label"
    } else {
        Write-Host "[missing] $Label -> $Path"
        $failures.Add("$Label missing: $Path")
    }
}

Write-Host "[revit-mcp-next doctor] Install root: $InstallRoot"

$node = Get-Command node.exe -ErrorAction SilentlyContinue
if ($node) {
    Write-Host "[ok] node.exe $(& $node.Source --version) at $($node.Source)"
} else {
    Write-Host "[missing] node.exe"
    $failures.Add("node.exe not found")
}

$launcher = Join-Path $InstallRoot "launch-revit-mcp-next.cmd"
$brokerEntry = Join-Path $InstallRoot "broker\dist\src\index.js"
$brokerServer = Join-Path $InstallRoot "broker\dist\src\server.js"
$addinDll = Join-Path $InstallRoot "addin\RevitMcpNext.Addin.dll"
$contractsDll = Join-Path $InstallRoot "addin\RevitMcpNext.Contracts.dll"
$manifest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\RevitMcpNext.addin"
$logs = Join-Path $InstallRoot "logs"

Test-RequiredFile $launcher "MCP launcher"
Test-RequiredFile $brokerEntry "broker entry"
Test-RequiredFile $brokerServer "broker server module"
Test-RequiredFile $addinDll "Revit add-in DLL"
Test-RequiredFile $contractsDll "Revit contracts DLL"
Test-RequiredFile $manifest "Revit add-in manifest"
Test-RequiredDirectory (Join-Path $InstallRoot "broker\node_modules") "broker production node_modules"

if ($node -and (Test-Path -LiteralPath $brokerServer -PathType Leaf)) {
    $script = "const p = process.argv[1]; import('file:///' + p.replace(/\\/g,'/')).then(() => console.log('[ok] broker imports')).catch((error) => { console.error(error); process.exit(1); });"
    & $node.Source -e $script $brokerServer
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("broker import failed")
    }
}

if (Test-Path -LiteralPath $logs -PathType Container) {
    Write-Host "[ok] logs directory: $logs"
} else {
    Write-Host "[info] logs directory will be created when the add-in writes diagnostics: $logs"
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "[revit-mcp-next doctor] FAILED"
    $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "[revit-mcp-next doctor] OK"
