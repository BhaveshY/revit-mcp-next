[CmdletBinding(PositionalBinding = $false)]
param(
    [int] $RevitYear = 2024,
    [string] $ModelPath = "",
    [string] $EvidencePath = "",
    [string] $OutputRoot = "",
    [string] $InstallRoot = "",
    [string] $GraphPath = "",
    [string] $RevitPath = "",
    [int] $TimeoutSeconds = 900,
    [switch] $LaunchRevit,
    [switch] $ValidateOnly,
    [switch] $AllowFailed,
    [switch] $DryRun,
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if (-not $Json) {
        Write-Host "[revit-mcp-next dynamo-smoke] $Message"
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

function Resolve-OptionalFile($Path, $Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ($DryRun) {
        return Get-FullPath $Path
    }

    return Resolve-RequiredFile $Path $Label
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
        $root = Join-Path $repoRoot "artifacts\host-integrations\raw\dynamo-$timestamp-$runId"
    } else {
        $root = Get-FullPath $OutputRoot
    }

    return Join-Path $root "dynamo.json"
}

function Resolve-RevitPath {
    if (-not [string]::IsNullOrWhiteSpace($RevitPath)) {
        if ($DryRun) {
            return Get-FullPath $RevitPath
        }

        return Resolve-RequiredFile $RevitPath "Revit executable"
    }

    $default = "C:\Program Files\Autodesk\Revit $RevitYear\Revit.exe"
    if (Test-Path -LiteralPath $default -PathType Leaf) {
        return $default
    }

    if ($DryRun) {
        return $default
    }

    throw "Revit.exe was not found. Pass -RevitPath or install Revit $RevitYear."
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
        throw "Dynamo evidence has unexpected schemaVersion: $($Evidence.schemaVersion). File: $Path"
    }

    if ([string] $Evidence.host -ne "dynamo") {
        throw "Dynamo evidence has unexpected host '$($Evidence.host)'. File: $Path"
    }

    if ([string] $Evidence.status -ne "passed") {
        throw "Dynamo evidence did not pass. Status: $($Evidence.status). File: $Path"
    }

    if ([bool] $Evidence.previewReady -ne $true) {
        throw "Dynamo evidence did not report previewReady=true. File: $Path"
    }

    if ([bool] $Evidence.applyWrites -ne $true) {
        throw "Dynamo evidence did not run write coverage. File: $Path"
    }

    $coveredOperations = Get-JsonArray $Evidence.coveredOperations
    if (-not ($coveredOperations -contains "create_level")) {
        throw "Dynamo evidence did not cover create_level. File: $Path"
    }

    $createdElementIds = Get-JsonArray $Evidence.createdElementIds
    if ($createdElementIds.Count -lt 1) {
        throw "Dynamo evidence did not record createdElementIds. File: $Path"
    }

    if ([string]::IsNullOrWhiteSpace([string] $Evidence.activeDocument.fingerprint)) {
        throw "Dynamo evidence did not record activeDocument.fingerprint. File: $Path"
    }

    if ($null -eq $Evidence.inProcessBridge) {
        throw "Dynamo evidence did not record inProcessBridge load proof. File: $Path"
    }

    if ([bool] $Evidence.inProcessBridge.addinHandlerActive -ne $true) {
        $handler = [string] $Evidence.inProcessBridge.handler
        if ([string]::IsNullOrWhiteSpace($handler)) {
            $handler = "unknown"
        }

        throw "Dynamo evidence used in-process bridge handler '$handler'; expected configuredAddin. File: $Path"
    }
}

function Read-And-ValidateEvidence($Path) {
    $evidence = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $AllowFailed) {
        Assert-HostEvidence $evidence $Path
    }

    return $evidence
}

$installRootFull = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    Get-DefaultInstallRoot
} else {
    Get-FullPath $InstallRoot
}

if ([string]::IsNullOrWhiteSpace($GraphPath)) {
    $GraphPath = Join-Path $installRootFull "integrations\dynamo\revit_mcp_next_host_smoke.dyn"
}
$graphFull = if ($DryRun) { Get-FullPath $GraphPath } else { Resolve-RequiredFile $GraphPath "Dynamo host-smoke graph" }

$modelFull = Resolve-OptionalFile $ModelPath "Disposable Revit model"
$revitExe = if ($LaunchRevit) { Resolve-RevitPath } else { "" }

if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Get-DefaultEvidencePath
}
$evidenceFull = Get-FullPath $EvidencePath

$instructions = @(
    "Open Revit $RevitYear with the disposable model.",
    "Open Dynamo for Revit.",
    "Open graph: $graphFull",
    "Run the graph once.",
    "Wait for evidence: $evidenceFull"
)

if ($DryRun) {
    $result = [ordered] @{
        status = "planned"
        dryRun = $true
        launchRevit = [bool] $LaunchRevit
        validateOnly = [bool] $ValidateOnly
        revitPath = $revitExe
        modelPath = $modelFull
        graphPath = $graphFull
        evidencePath = $evidenceFull
        timeoutSeconds = $TimeoutSeconds
        environment = [ordered] @{
            REVIT_MCP_NEXT_DYNAMO_EVIDENCE = $evidenceFull
            REVIT_MCP_NEXT_DYNAMO_MODEL = $modelFull
        }
        instructions = $instructions
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    } else {
        Write-Step "Would prepare Dynamo host smoke."
        Write-Step "Graph: $graphFull"
        Write-Step "Evidence path: $evidenceFull"
    }
    return
}

if ($ValidateOnly) {
    if (-not (Test-Path -LiteralPath $evidenceFull -PathType Leaf)) {
        throw "Dynamo evidence was not found: $evidenceFull"
    }

    $evidence = Read-And-ValidateEvidence $evidenceFull
    $result = [ordered] @{
        status = [string] $evidence.status
        evidencePath = $evidenceFull
        graphPath = $graphFull
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    } else {
        Write-Step "Dynamo host smoke evidence validated: $evidenceFull"
    }
    return
}

if (-not $LaunchRevit) {
    throw "Dynamo host smoke collection requires -LaunchRevit so Revit inherits REVIT_MCP_NEXT_DYNAMO_EVIDENCE. Use -ValidateOnly to validate evidence from an already completed Dynamo-for-Revit run."
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $evidenceFull) | Out-Null
if (Test-Path -LiteralPath $evidenceFull -PathType Leaf) {
    Remove-Item -LiteralPath $evidenceFull -Force
}

$oldEvidence = $env:REVIT_MCP_NEXT_DYNAMO_EVIDENCE
$oldModel = $env:REVIT_MCP_NEXT_DYNAMO_MODEL
try {
    $env:REVIT_MCP_NEXT_DYNAMO_EVIDENCE = $evidenceFull
    if (-not [string]::IsNullOrWhiteSpace($modelFull)) {
        $env:REVIT_MCP_NEXT_DYNAMO_MODEL = $modelFull
    }

    if ($LaunchRevit) {
        $launchArgs = @()
        if (-not [string]::IsNullOrWhiteSpace($modelFull)) {
            $launchArgs += $modelFull
        }

        Write-Step "Launching Revit so it inherits Dynamo smoke environment variables."
        Start-Process -FilePath $revitExe -ArgumentList $launchArgs | Out-Null
    }

    Write-Step "Dynamo host smoke is waiting for evidence."
    Write-Step "Graph: $graphFull"
    Write-Step "Evidence path: $evidenceFull"
    Write-Step "Open Dynamo for Revit, run the graph once, then keep this script open until validation completes."

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $evidenceFull -PathType Leaf)) {
        if ($TimeoutSeconds -le 0 -or (Get-Date) -gt $deadline) {
            throw "Timed out waiting for Dynamo evidence: $evidenceFull"
        }

        Start-Sleep -Seconds 2
    }

    $evidence = Read-And-ValidateEvidence $evidenceFull
} finally {
    $env:REVIT_MCP_NEXT_DYNAMO_EVIDENCE = $oldEvidence
    $env:REVIT_MCP_NEXT_DYNAMO_MODEL = $oldModel
}

$result = [ordered] @{
    status = [string] $evidence.status
    evidencePath = $evidenceFull
    graphPath = $graphFull
    modelPath = $modelFull
    installRoot = $installRootFull
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
} else {
    Write-Step "Dynamo host smoke evidence: $evidenceFull"
}
