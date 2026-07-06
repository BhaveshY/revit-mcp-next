[CmdletBinding(PositionalBinding = $false)]
param(
    [int] $RevitYear = 2024,
    [string] $ModelPath = "",
    [string] $OutputRoot = "",
    [string] $InstallRoot = "",
    [string] $GraphPath = "",
    [string] $EvidencePath = "",
    [string] $DynamoSettingsPath = "",
    [string] $RevitPath = "",
    [int] $TimeoutSeconds = 900,
    [switch] $LaunchRevit,
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if (-not $Json) {
        Write-Host "[revit-mcp-next dynamo-warmup] $Message"
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

function Resolve-RevitPath {
    if (-not [string]::IsNullOrWhiteSpace($RevitPath)) {
        return Resolve-RequiredFile $RevitPath "Revit executable"
    }

    $default = "C:\Program Files\Autodesk\Revit $RevitYear\Revit.exe"
    if (Test-Path -LiteralPath $default -PathType Leaf) {
        return $default
    }

    throw "Revit.exe was not found. Pass -RevitPath or install Revit $RevitYear."
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
            return Get-FullPath $candidate
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

function Test-ParseableXmlFile($Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    try {
        [xml] $xml = Get-Content -LiteralPath $Path -Raw
        return $null -ne $xml.DocumentElement
    } catch {
        return $false
    }
}

function Join-CommandLine([string[]] $Parts) {
    $escaped = foreach ($part in $Parts) {
        if ($null -eq $part) {
            continue
        }

        $value = [string] $part
        if ($value -match '[\s"`]') {
            '"' + $value.Replace('"', '\"') + '"'
        } else {
            $value
        }
    }

    return ($escaped -join " ")
}

function New-DynamoHostCommand($Mode, $ModelFull, $EvidenceFull, $OutputRootFull) {
    if ($Mode -eq "aggregate") {
        $parts = @(
            "npm", "run", "smoke:host-integrations", "--",
            "-RevitYear", "$RevitYear",
            "-OutputRoot", (Join-Path $OutputRootFull "host-integrations"),
            "-SeedPyRevitHosts",
            "-LaunchRevitForDynamo",
            "-UseDynamoJournalForDynamo",
            "-RequireWarmedDynamoForDynamo"
        )
        if (-not [string]::IsNullOrWhiteSpace($ModelFull)) {
            $parts += @("-ModelPath", $ModelFull)
        }
        if (-not [string]::IsNullOrWhiteSpace($InstallRootFull)) {
            $parts += @("-InstallRoot", $InstallRootFull)
        }
        return Join-CommandLine $parts
    }

    $dynamoParts = @(
        "npm", "run", "smoke:dynamo-host", "--",
        "-RevitYear", "$RevitYear",
        "-EvidencePath", $EvidenceFull,
        "-LaunchRevit",
        "-UseDynamoJournal",
        "-RequireWarmedDynamo"
    )
    if (-not [string]::IsNullOrWhiteSpace($ModelFull)) {
        $dynamoParts += @("-ModelPath", $ModelFull)
    }
    if (-not [string]::IsNullOrWhiteSpace($InstallRootFull)) {
        $dynamoParts += @("-InstallRoot", $InstallRootFull)
    }
    return Join-CommandLine $dynamoParts
}

function Invoke-Preflight($ScriptPath, $EvidenceFull, $PreflightReportFull, $InstallRootFull, $ModelFull, $GraphFull) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $ScriptPath,
        "-RevitYear", "$RevitYear",
        "-EvidencePath", $EvidenceFull,
        "-PreflightReportPath", $PreflightReportFull,
        "-PreflightOnly"
    )
    if (-not [string]::IsNullOrWhiteSpace($InstallRootFull)) {
        $arguments += @("-InstallRoot", $InstallRootFull)
    }
    if (-not [string]::IsNullOrWhiteSpace($ModelFull)) {
        $arguments += @("-ModelPath", $ModelFull)
    }
    if (-not [string]::IsNullOrWhiteSpace($GraphFull)) {
        $arguments += @("-GraphPath", $GraphFull)
    }
    if (-not [string]::IsNullOrWhiteSpace($RevitPath)) {
        $arguments += @("-RevitPath", $RevitPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($DynamoSettingsPath)) {
        $arguments += @("-DynamoSettingsPath", $DynamoSettingsPath)
    }
    if ($Json) {
        $arguments += "-Json"
    }

    Write-Step "Collecting Dynamo preflight."
    $output = & powershell @arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $text = ($output | Out-String).Trim()
        throw "Dynamo preflight failed with exit code $exitCode. $text"
    }
    if (-not (Test-Path -LiteralPath $PreflightReportFull -PathType Leaf)) {
        throw "Dynamo preflight report was not created: $PreflightReportFull"
    }

    return Get-Content -LiteralPath $PreflightReportFull -Raw | ConvertFrom-Json
}

function Invoke-DynamoEvidenceValidation($ScriptPath, $EvidenceFull, $PreflightReportFull, $InstallRootFull, $ModelFull, $GraphFull) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $ScriptPath,
        "-RevitYear", "$RevitYear",
        "-EvidencePath", $EvidenceFull,
        "-PreflightReportPath", $PreflightReportFull,
        "-ValidateOnly"
    )
    if (-not [string]::IsNullOrWhiteSpace($InstallRootFull)) {
        $arguments += @("-InstallRoot", $InstallRootFull)
    }
    if (-not [string]::IsNullOrWhiteSpace($ModelFull)) {
        $arguments += @("-ModelPath", $ModelFull)
    }
    if (-not [string]::IsNullOrWhiteSpace($GraphFull)) {
        $arguments += @("-GraphPath", $GraphFull)
    }
    if (-not [string]::IsNullOrWhiteSpace($DynamoSettingsPath)) {
        $arguments += @("-DynamoSettingsPath", $DynamoSettingsPath)
    }

    $output = & powershell @arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        return [ordered] @{
            status = "failed"
            exitCode = $exitCode
            message = ($output | Out-String).Trim()
        }
    }

    return [ordered] @{
        status = "passed"
        exitCode = 0
        message = "Dynamo evidence validated."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dynamoSmokeScript = Resolve-RequiredFile (Join-Path $PSScriptRoot "run-dynamo-host-smoke.ps1") "Dynamo host-smoke runner"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $OutputRoot = Join-Path $repoRoot "artifacts\dynamo-warmup\dynamo-warmup-$timestamp-$runId"
}

$outputRootFull = Get-FullPath $OutputRoot
$rawRoot = Join-Path $outputRootFull "raw"
$installRootFull = if ([string]::IsNullOrWhiteSpace($InstallRoot)) { Get-DefaultInstallRoot } else { Get-FullPath $InstallRoot }
$modelFull = if ([string]::IsNullOrWhiteSpace($ModelPath)) { "" } else { Get-FullPath $ModelPath }
$graphFull = if ([string]::IsNullOrWhiteSpace($GraphPath)) { "" } else { Get-FullPath $GraphPath }
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $rawRoot "dynamo.json"
}
$evidenceFull = Get-FullPath $EvidencePath
$preflightReportFull = Join-Path $rawRoot "dynamo-preflight.json"
$reportPath = Join-Path $outputRootFull "dynamo-warmup-report.json"
$instructionsPath = Join-Path $outputRootFull "dynamo-warmup-next-steps.txt"

New-Item -ItemType Directory -Force -Path $outputRootFull, $rawRoot | Out-Null

$preflight = Invoke-Preflight $dynamoSmokeScript $evidenceFull $preflightReportFull $installRootFull $modelFull $graphFull
$settingsPath = [string] $preflight.dynamoSettingsPath
$initialWarmed = [bool] $preflight.dynamoSettingsAppearsWarmed
$currentWarmed = $initialWarmed
$launchedRevit = $false
$launchedRevitProcessId = $null
$evidenceValidation = $null
$waitTimedOut = $false

if (-not $initialWarmed -and $LaunchRevit) {
    $revitExe = Resolve-RevitPath
    $launchArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($modelFull)) {
        $launchArgs += "`"$modelFull`""
    }

    $oldInstallRoot = $env:REVIT_MCP_NEXT_INSTALL_ROOT
    $oldAuthConfig = $env:REVIT_MCP_NEXT_AUTH_CONFIG
    $oldDynamoEvidence = $env:REVIT_MCP_NEXT_DYNAMO_EVIDENCE
    $oldDynamoModel = $env:REVIT_MCP_NEXT_DYNAMO_MODEL
    try {
        $env:REVIT_MCP_NEXT_INSTALL_ROOT = $installRootFull
        $env:REVIT_MCP_NEXT_AUTH_CONFIG = Join-Path $installRootFull "config\auth.env"
        $env:REVIT_MCP_NEXT_DYNAMO_EVIDENCE = $evidenceFull
        if (-not [string]::IsNullOrWhiteSpace($modelFull)) {
            $env:REVIT_MCP_NEXT_DYNAMO_MODEL = $modelFull
        }

        Write-Step "Launching Revit so manual Dynamo warm-up inherits evidence environment variables."
        $process = Start-Process -FilePath $revitExe -ArgumentList $launchArgs -PassThru
        $launchedRevit = $true
        $launchedRevitProcessId = [int] $process.Id
    } finally {
        $env:REVIT_MCP_NEXT_INSTALL_ROOT = $oldInstallRoot
        $env:REVIT_MCP_NEXT_AUTH_CONFIG = $oldAuthConfig
        $env:REVIT_MCP_NEXT_DYNAMO_EVIDENCE = $oldDynamoEvidence
        $env:REVIT_MCP_NEXT_DYNAMO_MODEL = $oldDynamoModel
    }

    Write-Step "Waiting for DynamoSettings.xml or Dynamo evidence."
    Write-Step "Dynamo settings: $settingsPath"
    Write-Step "Dynamo evidence: $evidenceFull"
    Write-Step "If prompts appear, answer them manually in this test profile. This script does not click prompts or change privacy settings."

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        if (Test-Path -LiteralPath $evidenceFull -PathType Leaf) {
            $evidenceValidation = Invoke-DynamoEvidenceValidation $dynamoSmokeScript $evidenceFull $preflightReportFull $installRootFull $modelFull $graphFull
            break
        }

        $currentWarmed = Test-ParseableXmlFile $settingsPath
        if ($currentWarmed) {
            break
        }

        if ($TimeoutSeconds -le 0 -or (Get-Date) -gt $deadline) {
            $waitTimedOut = $true
            break
        }

        Start-Sleep -Seconds 2
    }
}

$aggregateCommand = New-DynamoHostCommand "aggregate" $modelFull $evidenceFull $outputRootFull
$dynamoCommand = New-DynamoHostCommand "dynamo" $modelFull $evidenceFull $outputRootFull
$status = "needs_manual_warmup"
if ($evidenceValidation -and [string] $evidenceValidation.status -eq "passed") {
    $status = "evidence_passed"
} elseif ($currentWarmed -and -not $initialWarmed) {
    $status = "warmed"
} elseif ($initialWarmed) {
    $status = "already_warmed"
}

$manualSteps = @(
    "Open Revit $RevitYear in this Windows profile.",
    "Open Dynamo for Revit.",
    "Answer Autodesk/Dynamo startup or privacy prompts manually.",
    "Open the packaged graph: $($preflight.graphPath)",
    "Run the graph once if you are collecting host evidence.",
    "Rerun hosted integration evidence after the settings file exists."
)

$result = [ordered] @{
    schemaVersion = 1
    status = $status
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    revitYear = $RevitYear
    outputRoot = $outputRootFull
    installRoot = $installRootFull
    modelPath = $modelFull
    graphPath = [string] $preflight.graphPath
    evidencePath = $evidenceFull
    preflightReportPath = $preflightReportFull
    instructionsPath = $instructionsPath
    dynamoSettingsPath = $settingsPath
    dynamoSettingsAppearsWarmed = [bool] $currentWarmed
    dynamoSettingsInitialAppearsWarmed = [bool] $initialWarmed
    dynamoSettingsWarmedReason = [string] $preflight.dynamoSettingsWarmedReason
    launchRevit = [bool] $LaunchRevit
    launchedRevit = [bool] $launchedRevit
    launchedRevitProcessId = $launchedRevitProcessId
    waitTimedOut = [bool] $waitTimedOut
    timeoutSeconds = $TimeoutSeconds
    promptAutomationPolicy = [ordered] @{
        privacyPromptAutomation = $false
        uiPromptAutomation = $false
        privacySettingsChanged = $false
    }
    evidenceValidation = $evidenceValidation
    next = [ordered] @{
        aggregateHostSmokeCommand = $aggregateCommand
        dynamoHostSmokeCommand = $dynamoCommand
        manualSteps = $manualSteps
    }
}

$instructions = @(
    "Revit MCP Next Dynamo warm-up",
    "",
    "Status: $status",
    "Dynamo settings: $settingsPath",
    "Preflight report: $preflightReportFull",
    "Evidence path: $evidenceFull",
    "",
    "Manual steps:",
    ($manualSteps | ForEach-Object { "- $_" }),
    "",
    "After the Dynamo profile is warmed, run:",
    $aggregateCommand,
    "",
    "For Dynamo-only evidence, run:",
    $dynamoCommand,
    "",
    "Policy: this helper does not click prompts, preseed consent, or change Dynamo privacy settings."
)

Set-Content -LiteralPath $instructionsPath -Value $instructions -Encoding UTF8
Set-Content -LiteralPath $reportPath -Value ($result | ConvertTo-Json -Depth 12) -Encoding UTF8

if ($Json) {
    $result | ConvertTo-Json -Depth 12
} else {
    Write-Step "Status: $status"
    Write-Step "Warm-up report: $reportPath"
    Write-Step "Next steps: $instructionsPath"
    if ($status -eq "needs_manual_warmup") {
        Write-Step "Open Dynamo for Revit once in this profile, handle prompts manually, then rerun hosted integration evidence."
    } elseif ($status -eq "already_warmed" -or $status -eq "warmed") {
        Write-Step "Dynamo profile is warmed. Run hosted integration evidence next."
        Write-Step $aggregateCommand
    } elseif ($status -eq "evidence_passed") {
        Write-Step "Dynamo evidence validated. Compose hosted integration evidence next."
    }
}
