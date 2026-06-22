param(
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [int] $RevitYear = 2024
)

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]

function Test-RequiredFile($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "[ok] $Label"
        return $true
    }

    Write-Host "[missing] $Label -> $Path"
    $failures.Add("$Label missing: $Path")
    return $false
}

function Test-OptionalFile($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "[ok] $Label"
    } else {
        Write-Host "[info] optional $Label not found: $Path"
    }
}

function Test-RequiredDirectory($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Container) {
        Write-Host "[ok] $Label"
        return $true
    }

    Write-Host "[missing] $Label -> $Path"
    $failures.Add("$Label missing: $Path")
    return $false
}

function Test-NodeVersion($NodeCommand) {
    $versionText = (& $NodeCommand.Source --version)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($versionText)) {
        Write-Host "[missing] node.exe version check failed"
        $failures.Add("node.exe version check failed")
        return
    }

    if ($versionText -notmatch "^v?(\d+)\.") {
        Write-Host "[missing] node.exe version could not be parsed: $versionText"
        $failures.Add("node.exe version could not be parsed: $versionText")
        return
    }

    $major = [int] $Matches[1]
    if ($major -eq 24) {
        Write-Host "[ok] node.exe $versionText at $($NodeCommand.Source)"
    } else {
        Write-Host "[missing] node.exe $versionText at $($NodeCommand.Source) (expected Node 24.x)"
        $failures.Add("node.exe version is $versionText; expected Node 24.x")
    }
}

function Test-ManifestAssemblyPath($ManifestPath, $ExpectedAssemblyPath) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return
    }

    $manifestText = Get-Content -LiteralPath $ManifestPath -Raw
    if ($manifestText.Contains($ExpectedAssemblyPath)) {
        Write-Host "[ok] Revit manifest points at staged add-in DLL"
    } else {
        Write-Host "[missing] Revit manifest does not point at staged add-in DLL"
        $failures.Add("Revit manifest target mismatch: $ManifestPath")
    }
}

Write-Host "[revit-mcp-next doctor] Install root: $InstallRoot"

$node = Get-Command node.exe -ErrorAction SilentlyContinue
if ($node) {
    Test-NodeVersion $node
} else {
    Write-Host "[missing] node.exe"
    $failures.Add("node.exe not found")
}

$launcher = Join-Path $InstallRoot "launch-revit-mcp-next.cmd"
$brokerEntry = Join-Path $InstallRoot "broker\dist\src\index.js"
$brokerServer = Join-Path $InstallRoot "broker\dist\src\server.js"
$addinDll = Join-Path $InstallRoot "addin\RevitMcpNext.Addin.dll"
$contractsDll = Join-Path $InstallRoot "addin\RevitMcpNext.Contracts.dll"
$addinPdb = Join-Path $InstallRoot "addin\RevitMcpNext.Addin.pdb"
$contractsPdb = Join-Path $InstallRoot "addin\RevitMcpNext.Contracts.pdb"
$manifest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\RevitMcpNext.addin"
$logs = Join-Path $InstallRoot "logs"
$receipt = Join-Path $InstallRoot "install-receipt.json"
$releaseManifest = Join-Path $InstallRoot "release-manifest.json"

$launcherOk = Test-RequiredFile $launcher "MCP launcher"
Test-RequiredFile $brokerEntry "broker entry" | Out-Null
Test-RequiredFile $brokerServer "broker server module" | Out-Null
Test-RequiredFile $addinDll "Revit add-in DLL" | Out-Null
Test-RequiredFile $contractsDll "Revit contracts DLL" | Out-Null
Test-RequiredFile $manifest "Revit add-in manifest" | Out-Null
Test-RequiredDirectory (Join-Path $InstallRoot "broker\node_modules") "broker production node_modules" | Out-Null
Test-OptionalFile $addinPdb "Revit add-in PDB"
Test-OptionalFile $contractsPdb "Revit contracts PDB"
Test-OptionalFile $receipt "install receipt"
Test-OptionalFile $releaseManifest "release manifest"
Test-ManifestAssemblyPath $manifest $addinDll

if ($launcherOk) {
    $launcherText = Get-Content -LiteralPath $launcher -Raw
    if ($launcherText.Contains($brokerEntry)) {
        Write-Host "[ok] MCP launcher points at staged broker entry"
    } else {
        Write-Host "[missing] MCP launcher does not point at staged broker entry"
        $failures.Add("MCP launcher target mismatch: $launcher")
    }
}

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
