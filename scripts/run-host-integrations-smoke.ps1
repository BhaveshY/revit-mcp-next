[CmdletBinding(PositionalBinding = $false)]
param(
    [int] $RevitYear = 2024,
    [string] $ModelPath = "",
    [string] $OutputRoot = "",
    [string] $InstallRoot = "",
    [string] $PyRevitPath = "",
    [string[]] $PyRevitBuilds = @(),
    [switch] $SeedPyRevitHosts,
    [string] $PyRevitEvidencePath = "",
    [switch] $PyRevitValidateOnly,
    [string] $DynamoEvidencePath = "",
    [string] $DynamoGraphPath = "",
    [string] $DynamoRevitPath = "",
    [switch] $LaunchRevitForDynamo,
    [switch] $DynamoValidateOnly,
    [int] $DynamoTimeoutSeconds = 900,
    [switch] $AllowFailed,
    [switch] $DryRun,
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if (-not $Json) {
        Write-Host "[revit-mcp-next host-smoke] $Message"
    }
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Resolve-InstallRoot {
    if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) {
        return Get-FullPath $InstallRoot
    }

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
        return Get-FullPath (Join-Path $env:LOCALAPPDATA "RevitMcpNext")
    }

    if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        return Get-FullPath (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\RevitMcpNext")
    }

    throw "APPDATA and LOCALAPPDATA are not set. Pass -InstallRoot with the installed Revit MCP Next root."
}

function Invoke-Logged {
    param(
        [string] $Label,
        [string] $LogPath,
        [string] $ScriptPath,
        [string[]] $Arguments
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
    $display = "powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath $($Arguments -join ' ')"
    Write-Step $Label
    if (-not $Json) {
        Write-Host "  $display"
    }

    if ($DryRun) {
        "DRY RUN: $display" | Set-Content -LiteralPath $LogPath -Encoding UTF8
        return
    }

    "Command: $display" | Set-Content -LiteralPath $LogPath -Encoding UTF8
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($output) {
        $output | Add-Content -LiteralPath $LogPath -Encoding UTF8
        if (-not $Json) {
            $output | ForEach-Object { Write-Host $_ }
        }
    }

    if ($exitCode -ne 0) {
        throw "$Label failed with exit code $exitCode. See $LogPath"
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $OutputRoot = Join-Path $repoRoot "artifacts\host-integrations\host-integrations-$timestamp-$runId"
}

$outputRootFull = Get-FullPath $OutputRoot
$rawRoot = Join-Path $outputRootFull "raw"
$logsRoot = Join-Path $outputRootFull "logs"
$installRootFull = Resolve-InstallRoot

if ([string]::IsNullOrWhiteSpace($PyRevitEvidencePath)) {
    $PyRevitEvidencePath = Join-Path $rawRoot "pyrevit.json"
}
if ([string]::IsNullOrWhiteSpace($DynamoEvidencePath)) {
    $DynamoEvidencePath = Join-Path $rawRoot "dynamo.json"
}

$pyRevitEvidenceFull = Get-FullPath $PyRevitEvidencePath
$dynamoEvidenceFull = Get-FullPath $DynamoEvidencePath

$pyRevitArgs = @(
    "-RevitYear", "$RevitYear",
    "-EvidencePath", $pyRevitEvidenceFull,
    "-InstallRoot", $installRootFull
)
if (-not [string]::IsNullOrWhiteSpace($ModelPath)) {
    $pyRevitArgs += @("-ModelPath", (Get-FullPath $ModelPath))
}
if (-not [string]::IsNullOrWhiteSpace($PyRevitPath)) {
    $pyRevitArgs += @("-PyRevitPath", (Get-FullPath $PyRevitPath))
}
if ($SeedPyRevitHosts) {
    $pyRevitArgs += "-SeedHostsCache"
}
if ($PyRevitBuilds -and $PyRevitBuilds.Count -gt 0) {
    $pyRevitArgs += @("-Builds", ($PyRevitBuilds -join ","))
}
if ($PyRevitValidateOnly) {
    $pyRevitArgs += "-ValidateOnly"
}
if ($AllowFailed) {
    $pyRevitArgs += "-AllowFailed"
}
if ($DryRun) {
    $pyRevitArgs += "-DryRun"
}

$dynamoArgs = @(
    "-RevitYear", "$RevitYear",
    "-EvidencePath", $dynamoEvidenceFull,
    "-TimeoutSeconds", "$DynamoTimeoutSeconds",
    "-InstallRoot", $installRootFull
)
if (-not [string]::IsNullOrWhiteSpace($ModelPath)) {
    $dynamoArgs += @("-ModelPath", (Get-FullPath $ModelPath))
}
if (-not [string]::IsNullOrWhiteSpace($DynamoGraphPath)) {
    $dynamoArgs += @("-GraphPath", (Get-FullPath $DynamoGraphPath))
}
if (-not [string]::IsNullOrWhiteSpace($DynamoRevitPath)) {
    $dynamoArgs += @("-RevitPath", (Get-FullPath $DynamoRevitPath))
}
if ($LaunchRevitForDynamo) {
    $dynamoArgs += "-LaunchRevit"
}
if ($DynamoValidateOnly) {
    $dynamoArgs += "-ValidateOnly"
}
if ($AllowFailed) {
    $dynamoArgs += "-AllowFailed"
}
if ($DryRun) {
    $dynamoArgs += "-DryRun"
}

$composeArgs = @(
    "-PyRevitEvidencePath", $pyRevitEvidenceFull,
    "-DynamoEvidencePath", $dynamoEvidenceFull,
    "-OutputRoot", $outputRootFull
)

if (-not $DryRun -and -not $DynamoValidateOnly -and -not $LaunchRevitForDynamo) {
    throw "Hosted integration smoke requires -LaunchRevitForDynamo so Revit inherits Dynamo evidence environment variables. Use -DynamoValidateOnly to validate an existing Dynamo evidence file."
}

if ($DryRun) {
    $result = [ordered] @{
        status = "planned"
        dryRun = $true
        outputRoot = $outputRootFull
        rawRoot = $rawRoot
        logsRoot = $logsRoot
        installRoot = $installRootFull
        pyRevitEvidencePath = $pyRevitEvidenceFull
        dynamoEvidencePath = $dynamoEvidenceFull
        summaryPath = (Join-Path $outputRootFull "host-integrations-summary.json")
        pyRevitArgs = $pyRevitArgs
        dynamoArgs = $dynamoArgs
        composeArgs = $composeArgs
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    } else {
        Write-Step "Would run hosted pyRevit and Dynamo smoke."
        Write-Step "Output root: $outputRootFull"
    }
    return
}

New-Item -ItemType Directory -Force -Path $rawRoot, $logsRoot | Out-Null

Invoke-Logged "Run pyRevit host smoke" `
    (Join-Path $logsRoot "pyrevit-host-smoke.log") `
    (Join-Path $PSScriptRoot "run-pyrevit-host-smoke.ps1") `
    $pyRevitArgs

Invoke-Logged "Run Dynamo host smoke" `
    (Join-Path $logsRoot "dynamo-host-smoke.log") `
    (Join-Path $PSScriptRoot "run-dynamo-host-smoke.ps1") `
    $dynamoArgs

Invoke-Logged "Compose hosted integration evidence" `
    (Join-Path $logsRoot "host-integrations-evidence.log") `
    (Join-Path $PSScriptRoot "collect-host-integration-evidence.ps1") `
    $composeArgs

$summaryPath = Join-Path $outputRootFull "host-integrations-summary.json"
if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
    throw "Hosted integration summary was not created: $summaryPath"
}

$result = [ordered] @{
    status = "passed"
    outputRoot = $outputRootFull
    summaryPath = $summaryPath
    installRoot = $installRootFull
    pyRevitEvidencePath = Join-Path $outputRootFull "pyrevit.json"
    dynamoEvidencePath = Join-Path $outputRootFull "dynamo.json"
    logsRoot = $logsRoot
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
} else {
    Write-Step "Hosted integration smoke evidence: $summaryPath"
}
