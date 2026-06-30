[CmdletBinding(PositionalBinding = $false)]
param(
    [int] $RevitYear = 2024,
    [string] $ModelPath = "",
    [string] $EvidencePath = "",
    [string] $OutputRoot = "",
    [string] $InstallRoot = "",
    [string] $PyRevitPath = "",
    [string[]] $Builds = @(),
    [switch] $SeedHostsCache,
    [switch] $SkipRunnerAddinImport,
    [switch] $KeepRunnerAddinImport,
    [switch] $ValidateOnly,
    [switch] $AllowFailed,
    [switch] $DryRun,
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if (-not $Json) {
        Write-Host "[revit-mcp-next pyrevit-smoke] $Message"
    }
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Resolve-RequiredFile($Path, $Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label path cannot be empty."
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if (-not (Test-Path -LiteralPath $expanded -PathType Leaf)) {
        throw "$Label file was not found: $Path"
    }

    return (Resolve-Path -LiteralPath $expanded).Path
}

function Resolve-OptionalFile($Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ($DryRun) {
        return Get-FullPath $Path
    }

    return Resolve-RequiredFile $Path "Configured file"
}

function Escape-XmlText($Value) {
    return [System.Security.SecurityElement]::Escape([string] $Value)
}

function Resolve-PyRevitPath {
    if (-not [string]::IsNullOrWhiteSpace($PyRevitPath)) {
        if ($DryRun) {
            return (Get-FullPath $PyRevitPath)
        }

        return Resolve-RequiredFile $PyRevitPath "pyRevit CLI"
    }

    $default = "C:\Program Files\pyRevit-Master\bin\pyrevit.exe"
    if (Test-Path -LiteralPath $default -PathType Leaf) {
        return $default
    }

    $command = Get-Command pyrevit.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    if ($DryRun) {
        return "pyrevit.exe"
    }

    throw "pyrevit.exe was not found. Pass -PyRevitPath or install pyRevit."
}

function Get-RunnerImportRoot($EvidenceFull) {
    $parent = Split-Path -Parent $EvidenceFull
    if ([string]::IsNullOrWhiteSpace($parent)) {
        $parent = (Get-Location).Path
    }

    return Join-Path $parent "pyrevit-runner-addin-import"
}

function Write-RunnerAddinImport($ImportRoot, $InstallRoot) {
    $addinAssembly = Join-Path $InstallRoot "addin\RevitMcpNext.Addin.dll"
    $addinAssemblyFull = Resolve-RequiredFile $addinAssembly "Installed Revit MCP Next add-in assembly"

    if (Test-Path -LiteralPath $ImportRoot -PathType Container) {
        Remove-Item -LiteralPath $ImportRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $ImportRoot | Out-Null
    $manifestPath = Join-Path $ImportRoot "RevitMcpNext.addin"
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Revit MCP Next</Name>
    <Assembly>$(Escape-XmlText $addinAssemblyFull)</Assembly>
    <ClientId>6F78E70D-BE13-4E0B-9B11-9E28F876AF71</ClientId>
    <FullClassName>RevitMcpNext.Addin.RevitMcpApplication</FullClassName>
    <VendorId>BHVY</VendorId>
    <VendorDescription>BhaveshY Revit MCP Next</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8

    return $manifestPath
}

function Get-DefaultInstallRoot {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        $candidates.Add((Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\RevitMcpNext")) | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA "RevitMcpNext")) | Out-Null
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            return (Get-FullPath $candidate)
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        return Join-Path $env:LOCALAPPDATA "RevitMcpNext"
    }

    if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        return Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\RevitMcpNext"
    }

    throw "APPDATA and LOCALAPPDATA are not set. Pass -InstallRoot with the installed Revit MCP Next root."
}

function Get-DefaultEvidencePath {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
        $root = Join-Path $repoRoot "artifacts\host-integrations\raw\pyrevit-$timestamp-$runId"
    } else {
        $root = Get-FullPath $OutputRoot
    }

    return Join-Path $root "pyrevit.json"
}

function Get-JsonArray($Value) {
    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Array]) {
        return @($Value)
    }

    return @($Value)
}

function Assert-HostEvidence($Evidence, $Path) {
    if ($Evidence.schemaVersion -ne 1) {
        throw "pyRevit evidence has unexpected schemaVersion: $($Evidence.schemaVersion). File: $Path"
    }

    if ([string] $Evidence.host -ne "pyrevit") {
        throw "pyRevit evidence has unexpected host '$($Evidence.host)'. File: $Path"
    }

    if ([string] $Evidence.status -ne "passed") {
        throw "pyRevit evidence did not pass. Status: $($Evidence.status). File: $Path"
    }

    if ([bool] $Evidence.previewReady -ne $true) {
        throw "pyRevit evidence did not report previewReady=true. File: $Path"
    }

    if ([bool] $Evidence.applyWrites -ne $true) {
        throw "pyRevit evidence did not run write coverage. File: $Path"
    }

    $coveredOperations = Get-JsonArray $Evidence.coveredOperations
    if (-not ($coveredOperations -contains "create_level")) {
        throw "pyRevit evidence did not cover create_level. File: $Path"
    }

    $createdElementIds = Get-JsonArray $Evidence.createdElementIds
    if ($createdElementIds.Count -lt 1) {
        throw "pyRevit evidence did not record createdElementIds. File: $Path"
    }

    if ([string]::IsNullOrWhiteSpace([string] $Evidence.activeDocument.fingerprint)) {
        throw "pyRevit evidence did not record activeDocument.fingerprint. File: $Path"
    }

    if ($null -eq $Evidence.inProcessBridge) {
        throw "pyRevit evidence did not record inProcessBridge load proof. File: $Path"
    }

    if ([bool] $Evidence.inProcessBridge.addinHandlerActive -ne $true) {
        $handler = [string] $Evidence.inProcessBridge.handler
        if ([string]::IsNullOrWhiteSpace($handler)) {
            $handler = "unknown"
        }

        throw "pyRevit evidence used in-process bridge handler '$handler'; expected configuredAddin. File: $Path"
    }
}

$installRootFull = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    Get-DefaultInstallRoot
} else {
    Get-FullPath $InstallRoot
}

$hostSmokeScript = Join-Path $installRootFull "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Diagnostics.panel\Host Smoke.pushbutton\script.py"
if (-not $DryRun) {
    $hostSmokeScript = Resolve-RequiredFile $hostSmokeScript "Installed pyRevit host-smoke script"
}

$pyRevitExe = if ($ValidateOnly -and -not $DryRun) { "" } else { Resolve-PyRevitPath }
$modelFull = Resolve-OptionalFile $ModelPath

if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Get-DefaultEvidencePath
}
$evidenceFull = Get-FullPath $EvidencePath
$authConfigFull = Get-FullPath (Join-Path $installRootFull "config\auth.env")
$runnerImportRoot = if ($SkipRunnerAddinImport) { "" } else { Get-RunnerImportRoot $evidenceFull }
$runnerImportManifestPath = if ([string]::IsNullOrWhiteSpace($runnerImportRoot)) {
    ""
} else {
    Join-Path $runnerImportRoot "RevitMcpNext.addin"
}

$arguments = New-Object System.Collections.Generic.List[string]
$arguments.Add("run") | Out-Null
$arguments.Add($hostSmokeScript) | Out-Null
if (-not [string]::IsNullOrWhiteSpace($modelFull)) {
    $arguments.Add($modelFull) | Out-Null
}
$arguments.Add("--revit=$RevitYear") | Out-Null
$arguments.Add("--purge") | Out-Null
if (-not [string]::IsNullOrWhiteSpace($runnerImportRoot)) {
    $arguments.Add("--import=$runnerImportRoot") | Out-Null
}

if ($DryRun) {
    $result = [ordered] @{
        status = "planned"
        dryRun = $true
        pyRevitPath = $pyRevitExe
        installRoot = $installRootFull
        hostSmokeScript = $hostSmokeScript
        modelPath = $modelFull
        evidencePath = $evidenceFull
        environment = [ordered] @{
            REVIT_MCP_NEXT_INSTALL_ROOT = $installRootFull
            REVIT_MCP_NEXT_AUTH_CONFIG = $authConfigFull
            REVIT_MCP_NEXT_PYREVIT_EVIDENCE = $evidenceFull
            REVIT_MCP_NEXT_PYREVIT_MODEL = $modelFull
        }
        runnerAddinImport = [ordered] @{
            enabled = -not [bool] $SkipRunnerAddinImport
            importRoot = $runnerImportRoot
            manifestPath = $runnerImportManifestPath
        }
        seedHostsCache = [bool] $SeedHostsCache
        validateOnly = [bool] $ValidateOnly
        command = "pyrevit.exe $($arguments -join ' ')"
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    } else {
        Write-Step "Would run pyRevit host smoke."
        Write-Step "Evidence path: $evidenceFull"
    }
    return
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $evidenceFull) | Out-Null

if ($ValidateOnly) {
    if (-not (Test-Path -LiteralPath $evidenceFull -PathType Leaf)) {
        throw "pyRevit evidence was not found: $evidenceFull"
    }

    $evidence = Get-Content -LiteralPath $evidenceFull -Raw | ConvertFrom-Json
    if (-not $AllowFailed) {
        Assert-HostEvidence $evidence $evidenceFull
    }

    $result = [ordered] @{
        status = [string] $evidence.status
        evidencePath = $evidenceFull
        installRoot = $installRootFull
        hostSmokeScript = $hostSmokeScript
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    } else {
        Write-Step "pyRevit host smoke evidence validated: $evidenceFull"
    }
    return
}

if (Test-Path -LiteralPath $evidenceFull -PathType Leaf) {
    Remove-Item -LiteralPath $evidenceFull -Force
}

if (-not [string]::IsNullOrWhiteSpace($runnerImportRoot)) {
    $runnerImportManifestPath = Write-RunnerAddinImport $runnerImportRoot $installRootFull
    Write-Step "Prepared pyRevit runner add-in import: $runnerImportManifestPath"
}

if ($SeedHostsCache) {
    $hostsArgs = @()
    if ($Builds -and $Builds.Count -gt 0) {
        $hostsArgs += @("-Builds", ($Builds -join ","))
    }

    Write-Step "Seeding pyRevit host cache."
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "ensure-pyrevit-hosts-cache.ps1") @hostsArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pyRevit host cache seeding failed with exit code $LASTEXITCODE."
    }
}

$oldEvidence = $env:REVIT_MCP_NEXT_PYREVIT_EVIDENCE
$oldModel = $env:REVIT_MCP_NEXT_PYREVIT_MODEL
$oldInstallRoot = $env:REVIT_MCP_NEXT_INSTALL_ROOT
$oldAuthConfig = $env:REVIT_MCP_NEXT_AUTH_CONFIG
try {
    $env:REVIT_MCP_NEXT_INSTALL_ROOT = $installRootFull
    $env:REVIT_MCP_NEXT_AUTH_CONFIG = $authConfigFull
    $env:REVIT_MCP_NEXT_PYREVIT_EVIDENCE = $evidenceFull
    if (-not [string]::IsNullOrWhiteSpace($modelFull)) {
        $env:REVIT_MCP_NEXT_PYREVIT_MODEL = $modelFull
    }

    Write-Step "Running pyRevit host smoke."
    & $pyRevitExe @arguments
    $exitCode = $LASTEXITCODE
} finally {
    $env:REVIT_MCP_NEXT_PYREVIT_EVIDENCE = $oldEvidence
    $env:REVIT_MCP_NEXT_PYREVIT_MODEL = $oldModel
    $env:REVIT_MCP_NEXT_INSTALL_ROOT = $oldInstallRoot
    $env:REVIT_MCP_NEXT_AUTH_CONFIG = $oldAuthConfig
    if (-not $KeepRunnerAddinImport -and -not [string]::IsNullOrWhiteSpace($runnerImportRoot) -and (Test-Path -LiteralPath $runnerImportRoot -PathType Container)) {
        Remove-Item -LiteralPath $runnerImportRoot -Recurse -Force
    }
}

if ($exitCode -ne 0 -and -not $AllowFailed) {
    throw "pyRevit host smoke failed with exit code $exitCode."
}

if (-not (Test-Path -LiteralPath $evidenceFull -PathType Leaf)) {
    throw "pyRevit host smoke did not create evidence: $evidenceFull"
}

$evidence = Get-Content -LiteralPath $evidenceFull -Raw | ConvertFrom-Json
if (-not $AllowFailed) {
    Assert-HostEvidence $evidence $evidenceFull
}

$result = [ordered] @{
    status = [string] $evidence.status
    exitCode = $exitCode
    evidencePath = $evidenceFull
    modelPath = $modelFull
    installRoot = $installRootFull
    hostSmokeScript = $hostSmokeScript
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
} else {
    Write-Step "pyRevit host smoke evidence: $evidenceFull"
}
