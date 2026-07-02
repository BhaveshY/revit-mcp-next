param(
    [string] $PyRevitEvidencePath = "",
    [string] $DynamoEvidencePath = "",
    [string] $OutputRoot = "",
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if (-not $Json) {
        Write-Host "[revit-mcp-next host-evidence] $Message"
    }
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function New-Directory($Path) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
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

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
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

function Assert-HostEvidence($Evidence, $ExpectedHost, $Path) {
    if ($Evidence.schemaVersion -ne 1) {
        throw "$ExpectedHost evidence has unexpected schemaVersion: $($Evidence.schemaVersion). File: $Path"
    }

    if ([string] $Evidence.status -ne "passed") {
        throw "$ExpectedHost evidence did not pass. Status: $($Evidence.status). File: $Path"
    }

    if ([string] $Evidence.host -ne $ExpectedHost) {
        throw "$ExpectedHost evidence has unexpected host '$($Evidence.host)'. File: $Path"
    }

    if ([bool] $Evidence.previewReady -ne $true) {
        throw "$ExpectedHost evidence did not report previewReady=true. File: $Path"
    }

    if ([bool] $Evidence.applyWrites -ne $true) {
        throw "$ExpectedHost evidence did not run write coverage. File: $Path"
    }

    $coveredOperations = Get-JsonArray $Evidence.coveredOperations
    if (-not ($coveredOperations -contains "create_level")) {
        throw "$ExpectedHost evidence did not cover create_level. File: $Path"
    }

    $createdElementIds = Get-JsonArray $Evidence.createdElementIds
    if ($createdElementIds.Count -lt 1) {
        throw "$ExpectedHost evidence did not record createdElementIds. File: $Path"
    }

    $fingerprint = [string] $Evidence.activeDocument.fingerprint
    if ([string]::IsNullOrWhiteSpace($fingerprint)) {
        throw "$ExpectedHost evidence did not record activeDocument.fingerprint. File: $Path"
    }

    if ($null -eq $Evidence.inProcessBridge) {
        throw "$ExpectedHost evidence did not record inProcessBridge load proof. File: $Path"
    }

    if ([bool] $Evidence.inProcessBridge.addinHandlerActive -ne $true) {
        $handler = [string] $Evidence.inProcessBridge.handler
        if ([string]::IsNullOrWhiteSpace($handler)) {
            $handler = "unknown"
        }

        throw "$ExpectedHost evidence used in-process bridge handler '$handler'; expected configuredAddin. File: $Path"
    }

    if ([string]::IsNullOrWhiteSpace([string] $Evidence.inProcessBridge.assemblyPath)) {
        throw "$ExpectedHost evidence did not record inProcessBridge.assemblyPath. File: $Path"
    }

    if ([string]::IsNullOrWhiteSpace([string] $Evidence.inProcessBridge.assemblySha256)) {
        throw "$ExpectedHost evidence did not record inProcessBridge.assemblySha256. File: $Path"
    }

    return [ordered] @{
        status = [string] $Evidence.status
        evidencePath = "$ExpectedHost.json"
        previewReady = [bool] $Evidence.previewReady
        applyWrites = [bool] $Evidence.applyWrites
        activeDocument = $Evidence.activeDocument
        inProcessBridge = $Evidence.inProcessBridge
        coveredTools = @(Get-JsonArray $Evidence.coveredTools)
        coveredOperations = @($coveredOperations)
        createdElementIds = @($createdElementIds)
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $OutputRoot = Join-Path $repoRoot "artifacts\host-integrations\host-integrations-$timestamp-$runId"
}

$outputRootFull = Get-FullPath $OutputRoot
New-Directory $outputRootFull

$pyRevitSource = Resolve-RequiredFile $PyRevitEvidencePath "pyRevit host-smoke evidence"
$dynamoSource = Resolve-RequiredFile $DynamoEvidencePath "Dynamo host-smoke evidence"

$pyRevitEvidence = Read-JsonFile $pyRevitSource
$dynamoEvidence = Read-JsonFile $dynamoSource

$pyRevitSummary = Assert-HostEvidence $pyRevitEvidence "pyrevit" $pyRevitSource
$dynamoSummary = Assert-HostEvidence $dynamoEvidence "dynamo" $dynamoSource

$pyRevitDest = Join-Path $outputRootFull "pyrevit.json"
$dynamoDest = Join-Path $outputRootFull "dynamo.json"
Copy-Item -LiteralPath $pyRevitSource -Destination $pyRevitDest -Force
Copy-Item -LiteralPath $dynamoSource -Destination $dynamoDest -Force

$summary = [ordered] @{
    schemaVersion = 1
    status = "passed"
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    hosts = [ordered] @{
        pyrevit = $pyRevitSummary
        dynamo = $dynamoSummary
    }
}

$summaryPath = Join-Path $outputRootFull "host-integrations-summary.json"
Set-Content -LiteralPath $summaryPath -Value ($summary | ConvertTo-Json -Depth 12) -Encoding UTF8

$result = [ordered] @{
    status = "passed"
    outputRoot = $outputRootFull
    summaryPath = $summaryPath
    pyRevitEvidencePath = $pyRevitDest
    dynamoEvidencePath = $dynamoDest
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
} else {
    Write-Step "Created hosted integration evidence: $summaryPath"
}
