param(
    [switch] $DryRun,
    [switch] $SkipDependencyInstall,
    [int[]] $RevitYears = @(2024),
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [string] $NodePath = ""
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next] $Message"
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-InstallChild($Path) {
    $root = Get-FullPath $InstallRoot
    $child = Get-FullPath $Path
    if (-not $child.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside install root. Root: $root Target: $child"
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

Write-Step "Installing Revit MCP Next"
Write-Step "Install root: $InstallRoot"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$nodeExe = Resolve-NodeExe
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

if ($SkipDependencyInstall) {
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
    } finally {
        Pop-Location
    }
}

$launcher = Join-Path $InstallRoot "launch-revit-mcp-next.cmd"
$launcherContent = @"
@echo off
setlocal
set "REVIT_MCP_NEXT_PIPE=revit-mcp-next"
set "REVIT_MCP_NEXT_VERSION=0.1.0"
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

Write-Step "MCP launcher command for clients:"
Write-Host "  cmd /c `"$launcher`""
Write-Step "Done. Open Revit, then run revit.status from Claude Code or Codex."
