[CmdletBinding(PositionalBinding = $false)]
param(
    [int] $RevitYear = 2024,
    [string] $ModelPath = "",
    [string] $EvidencePath = "",
    [string] $OutputRoot = "",
    [string] $InstallRoot = "",
    [string] $GraphPath = "",
    [string] $RevitPath = "",
    [string] $DynamoSettingsPath = "",
    [string] $PreflightReportPath = "",
    [int] $TimeoutSeconds = 900,
    [switch] $LaunchRevit,
    [switch] $UseDynamoJournal,
    [switch] $AllowUnwarmedDynamoJournal,
    [string] $DynamoJournalPath = "",
    [switch] $PreflightOnly,
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

function Get-OptionalRevitPathForReport {
    if (-not [string]::IsNullOrWhiteSpace($RevitPath)) {
        return Get-FullPath $RevitPath
    }

    $default = "C:\Program Files\Autodesk\Revit $RevitYear\Revit.exe"
    if (Test-Path -LiteralPath $default -PathType Leaf) {
        return $default
    }

    return ""
}

function Get-DynamoVersionFromRevit($RevitExecutablePath) {
    $empty = [ordered] @{
        version = ""
        sourcePath = ""
    }

    $revitRoot = ""
    if (-not [string]::IsNullOrWhiteSpace($RevitExecutablePath)) {
        $revitRoot = Split-Path -Parent $RevitExecutablePath
    } else {
        $defaultRevitPath = "C:\Program Files\Autodesk\Revit $RevitYear\Revit.exe"
        if (Test-Path -LiteralPath $defaultRevitPath -PathType Leaf) {
            $revitRoot = Split-Path -Parent $defaultRevitPath
        }
    }

    if ([string]::IsNullOrWhiteSpace($revitRoot)) {
        return $empty
    }

    $relativeCandidates = @(
        "AddIns\DynamoForRevit\DynamoRevitDS.dll",
        "AddIns\DynamoForRevit\DynamoRevit.dll",
        "AddIns\DynamoForRevit\DynamoCore.dll",
        "AddIns\DynamoForRevit\DynamoServices.dll"
    )

    foreach ($relative in $relativeCandidates) {
        $candidate = Join-Path $revitRoot $relative
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $item = Get-Item -LiteralPath $candidate
        $version = [string] $item.VersionInfo.ProductVersion
        if ([string]::IsNullOrWhiteSpace($version)) {
            $version = [string] $item.VersionInfo.FileVersion
        }

        return [ordered] @{
            version = $version
            sourcePath = $item.FullName
        }
    }

    return $empty
}

function Get-DynamoMajorMinorVersion($Version) {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        return ""
    }

    if ($Version -match '^(\d+)\.(\d+)') {
        return "$($matches[1]).$($matches[2])"
    }

    return ""
}

function Resolve-DynamoSettingsPath($RequestedPath, $DynamoVersion) {
    $majorMinor = Get-DynamoMajorMinorVersion $DynamoVersion
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return [ordered] @{
            path = Get-FullPath $RequestedPath
            source = "explicit"
            expectedVersion = $majorMinor
            confidence = "explicit"
        }
    }

    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        return [ordered] @{
            path = ""
            source = ""
            expectedVersion = $majorMinor
            confidence = "none"
        }
    }

    $dynamoRevitRoot = Join-Path $env:APPDATA "Dynamo\Dynamo Revit"

    if (Test-Path -LiteralPath $dynamoRevitRoot -PathType Container) {
        $existingSettings = @(Get-ChildItem -LiteralPath $dynamoRevitRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object {
                $settings = Join-Path $_.FullName "DynamoSettings.xml"
                if (Test-Path -LiteralPath $settings -PathType Leaf) {
                    Get-Item -LiteralPath $settings
                }
            })

        if (-not [string]::IsNullOrWhiteSpace($majorMinor)) {
            $matching = $existingSettings |
                Where-Object { (Split-Path -Leaf $_.DirectoryName) -eq $majorMinor } |
                Sort-Object LastWriteTimeUtc -Descending |
                Select-Object -First 1
            if ($matching) {
                return [ordered] @{
                    path = $matching.FullName
                    source = "appdata-exact-version"
                    expectedVersion = $majorMinor
                    confidence = "version-match"
                }
            }

            return [ordered] @{
                path = Get-FullPath (Join-Path $dynamoRevitRoot "$majorMinor\DynamoSettings.xml")
                source = "expected-from-dynamo-version"
                expectedVersion = $majorMinor
                confidence = "expected-path"
            }
        }

        $latest = $existingSettings | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($latest) {
            return [ordered] @{
                path = $latest.FullName
                source = "appdata-latest-fallback"
                expectedVersion = ""
                confidence = "latest-fallback"
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($majorMinor)) {
        return [ordered] @{
            path = Get-FullPath (Join-Path $dynamoRevitRoot "$majorMinor\DynamoSettings.xml")
            source = "expected-from-dynamo-version"
            expectedVersion = $majorMinor
            confidence = "expected-path"
        }
    }

    return [ordered] @{
        path = ""
        source = ""
        expectedVersion = ""
        confidence = "none"
    }
}

function Get-DynamoSettingsReport($RequestedPath, $DynamoVersion) {
    $selected = Resolve-DynamoSettingsPath $RequestedPath $DynamoVersion
    $selectedPath = [string] $selected.path
    if ([string]::IsNullOrWhiteSpace($selectedPath)) {
        return [ordered] @{
            path = ""
            source = ""
            version = ""
            expectedVersion = [string] $selected.expectedVersion
            confidence = [string] $selected.confidence
            exists = $false
            parseableXml = $false
            appearsWarmed = $false
            warmedReason = "settings-path-not-discovered"
            lastWriteTimeUtc = $null
        }
    }

    $exists = Test-Path -LiteralPath $selectedPath -PathType Leaf
    $lastWriteTimeUtc = $null
    if ($exists) {
        $lastWriteTimeUtc = (Get-Item -LiteralPath $selectedPath).LastWriteTimeUtc.ToString("o")
    }

    $parseableXml = $false
    $warmedReason = "settings-file-not-found"
    if ($exists) {
        try {
            [xml] $settingsXml = Get-Content -LiteralPath $selectedPath -Raw
            if ($settingsXml.DocumentElement) {
                $parseableXml = $true
                $warmedReason = "existing-parseable-settings-file"
            } else {
                $warmedReason = "settings-file-has-no-root-element"
            }
        } catch {
            $warmedReason = "settings-file-not-parseable"
        }
    }

    return [ordered] @{
        path = $selectedPath
        source = [string] $selected.source
        version = Split-Path -Leaf (Split-Path -Parent $selectedPath)
        expectedVersion = [string] $selected.expectedVersion
        confidence = [string] $selected.confidence
        exists = [bool] $exists
        parseableXml = [bool] $parseableXml
        appearsWarmed = [bool] ($exists -and $parseableXml)
        warmedReason = $warmedReason
        lastWriteTimeUtc = $lastWriteTimeUtc
    }
}

function Get-DefaultPreflightReportPath($EvidenceFull) {
    $parent = Split-Path -Parent $EvidenceFull
    if ([string]::IsNullOrWhiteSpace($parent)) {
        $parent = (Get-Location).Path
    }

    return Join-Path $parent "dynamo-preflight.json"
}

function New-DynamoPreflightReport($RevitExecutablePath, $InstallRootFull, $GraphFull, $EvidenceFull, $ModelFull, $ReportPath) {
    $versionInfo = Get-DynamoVersionFromRevit $RevitExecutablePath
    $settingsInfo = Get-DynamoSettingsReport $DynamoSettingsPath ([string] $versionInfo.version)
    $dynamoVersion = [string] $versionInfo.version
    $dynamoVersionSource = [string] $versionInfo.sourcePath
    if ([string]::IsNullOrWhiteSpace($dynamoVersion) -and -not [string]::IsNullOrWhiteSpace([string] $settingsInfo.version)) {
        $dynamoVersion = [string] $settingsInfo.version
        $dynamoVersionSource = "DynamoSettings.xml parent directory"
    }

    $modelExists = $null
    if (-not [string]::IsNullOrWhiteSpace($ModelFull)) {
        $modelExists = Test-Path -LiteralPath $ModelFull -PathType Leaf
    }

    return [ordered] @{
        schemaVersion = 1
        status = "preflight"
        createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        revitYear = $RevitYear
        revitPath = $RevitExecutablePath
        dynamoVersion = $dynamoVersion
        dynamoVersionSource = $dynamoVersionSource
        dynamoSettingsPath = [string] $settingsInfo.path
        dynamoSettingsSource = [string] $settingsInfo.source
        dynamoSettingsVersion = [string] $settingsInfo.version
        dynamoSettingsExpectedVersion = [string] $settingsInfo.expectedVersion
        dynamoSettingsConfidence = [string] $settingsInfo.confidence
        dynamoSettingsExists = [bool] $settingsInfo.exists
        dynamoSettingsParseableXml = [bool] $settingsInfo.parseableXml
        dynamoSettingsAppearsWarmed = [bool] $settingsInfo.appearsWarmed
        dynamoSettingsWarmedReason = [string] $settingsInfo.warmedReason
        dynamoSettingsLastWriteTimeUtc = $settingsInfo.lastWriteTimeUtc
        graphPath = $GraphFull
        graphExists = Test-Path -LiteralPath $GraphFull -PathType Leaf
        installRoot = $InstallRootFull
        installRootExists = Test-Path -LiteralPath $InstallRootFull -PathType Container
        evidencePath = $EvidenceFull
        modelPath = $ModelFull
        modelExists = $modelExists
        preflightReportPath = $ReportPath
        privacySettingsChanged = $false
        privacyPromptAutomation = $false
        uiPromptAutomation = $false
        note = "This preflight only inspects existing files and planned paths. It does not edit DynamoSettings.xml, change privacy choices, or automate Dynamo/Revit prompts."
    }
}

function Write-DynamoPreflight($Report) {
    Write-Step "Dynamo preflight report:"
    Write-Step "  Revit year: $($Report.revitYear)"
    $version = if ([string]::IsNullOrWhiteSpace([string] $Report.dynamoVersion)) { "not discovered" } else { [string] $Report.dynamoVersion }
    Write-Step "  Dynamo version: $version"
    $settingsPath = if ([string]::IsNullOrWhiteSpace([string] $Report.dynamoSettingsPath)) { "not discovered" } else { [string] $Report.dynamoSettingsPath }
    Write-Step "  Dynamo settings path: $settingsPath"
    Write-Step "  Dynamo settings warmed: $($Report.dynamoSettingsAppearsWarmed) ($($Report.dynamoSettingsWarmedReason))"
    Write-Step "  Graph path: $($Report.graphPath)"
    Write-Step "  Install root: $($Report.installRoot)"
    Write-Step "  Evidence path: $($Report.evidencePath)"
    $modelPath = if ([string]::IsNullOrWhiteSpace([string] $Report.modelPath)) { "not supplied" } else { [string] $Report.modelPath }
    Write-Step "  Model path: $modelPath"
    Write-Step "  Privacy/UI prompt automation: false"
}

function Save-DynamoPreflightReport($Report, $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $parent = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($parent)) {
        $parent = (Get-Location).Path
    }

    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Set-Content -LiteralPath $Path -Value ($Report | ConvertTo-Json -Depth 12) -Encoding UTF8
}

function ConvertTo-JournalString($Value) {
    return ([string] $Value).Replace('"', '""')
}

function Get-DefaultDynamoJournalPath($EvidenceFull) {
    $parent = Split-Path -Parent $EvidenceFull
    if ([string]::IsNullOrWhiteSpace($parent)) {
        $parent = (Get-Location).Path
    }

    return Join-Path $parent "dynamo-host-smoke.journal.txt"
}

function New-DynamoJournalFile($Path, $GraphFull) {
    $parent = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($parent)) {
        $parent = (Get-Location).Path
    }

    New-Item -ItemType Directory -Force -Path $parent | Out-Null

    $graphJournal = ConvertTo-JournalString $GraphFull
    $content = @(
        "' Revit MCP Next Dynamo host-smoke journal.",
        "' This journal only opens/runs the packaged Dynamo graph after Dynamo has already been manually warmed.",
        "Dim Jrn",
        "Set Jrn = CrsJournalScript",
        "Jrn.Data  _",
        "        ""dynShowUI""  , ""True""",
        "Jrn.Data  _",
        "        ""dynAutomation""  , ""False""",
        "Jrn.Data  _",
        "        ""dynForceManualRun""  , ""False""",
        "Jrn.Data  _",
        "        ""dynPath""  , ""$graphJournal""",
        "Jrn.Data  _",
        "        ""dynPathExecute""  , ""True""",
        "Jrn.Command ""Ribbon""  , ""Dynamo starten , ID_VISUAL_PROGRAMMING_DYNAMO"""
    )

    Set-Content -LiteralPath $Path -Value $content -Encoding ASCII
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
$authConfigFull = Get-FullPath (Join-Path $installRootFull "config\auth.env")

if ([string]::IsNullOrWhiteSpace($GraphPath)) {
    $GraphPath = Join-Path $installRootFull "integrations\dynamo\revit_mcp_next_host_smoke.dyn"
}
$graphFull = Get-FullPath $GraphPath

$modelFull = if ([string]::IsNullOrWhiteSpace($ModelPath)) { "" } else { Get-FullPath $ModelPath }
$revitExe = if ($LaunchRevit -and -not $PreflightOnly) { Resolve-RevitPath } else { Get-OptionalRevitPathForReport }

if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Get-DefaultEvidencePath
}
$evidenceFull = Get-FullPath $EvidencePath
if ([string]::IsNullOrWhiteSpace($PreflightReportPath)) {
    $PreflightReportPath = Get-DefaultPreflightReportPath $evidenceFull
}
$preflightReportFull = Get-FullPath $PreflightReportPath
$preflight = New-DynamoPreflightReport $revitExe $installRootFull $graphFull $evidenceFull $modelFull $preflightReportFull
if ([string]::IsNullOrWhiteSpace($DynamoJournalPath)) {
    $DynamoJournalPath = Get-DefaultDynamoJournalPath $evidenceFull
}
$dynamoJournalFull = Get-FullPath $DynamoJournalPath

$instructions = @(
    "Open Revit $RevitYear with the disposable model.",
    "Open Dynamo for Revit.",
    "Open graph: $graphFull",
    "Run the graph once.",
    "Wait for evidence: $evidenceFull",
    "If Revit or Dynamo shows Autodesk/Dynamo privacy or startup prompts, answer them manually in the intended test profile. This script does not click prompts or simulate consent."
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
        preflightReportPath = $preflightReportFull
        evidencePath = $evidenceFull
        timeoutSeconds = $TimeoutSeconds
        useDynamoJournal = [bool] $UseDynamoJournal
        dynamoJournalPath = if ($UseDynamoJournal) { $dynamoJournalFull } else { "" }
        dynamoJournalRequiresWarmedSettings = [bool] ($UseDynamoJournal -and -not $AllowUnwarmedDynamoJournal)
        dynamoJournalAllowed = [bool] (-not $UseDynamoJournal -or $AllowUnwarmedDynamoJournal -or [bool] $preflight.dynamoSettingsAppearsWarmed)
        environment = [ordered] @{
            REVIT_MCP_NEXT_INSTALL_ROOT = $installRootFull
            REVIT_MCP_NEXT_AUTH_CONFIG = $authConfigFull
            REVIT_MCP_NEXT_DYNAMO_EVIDENCE = $evidenceFull
            REVIT_MCP_NEXT_DYNAMO_MODEL = $modelFull
        }
        preflight = $preflight
        instructions = $instructions
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    } else {
        Write-Step "Would prepare Dynamo host smoke."
        Write-DynamoPreflight $preflight
    }
    return
}

if ($PreflightOnly) {
    Save-DynamoPreflightReport $preflight $preflightReportFull
    $result = [ordered] @{
        status = "preflight"
        preflightReportPath = $preflightReportFull
        preflight = $preflight
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    } else {
        Write-DynamoPreflight $preflight
        Write-Step "Dynamo preflight report: $preflightReportFull"
    }
    return
}

if (-not (Test-Path -LiteralPath $graphFull -PathType Leaf)) {
    throw "Dynamo host-smoke graph file was not found: $GraphPath"
}

if (-not [string]::IsNullOrWhiteSpace($modelFull) -and -not (Test-Path -LiteralPath $modelFull -PathType Leaf)) {
    throw "Disposable Revit model file was not found: $ModelPath"
}

$shouldWritePreflightReport = -not $ValidateOnly
if ($shouldWritePreflightReport) {
    Save-DynamoPreflightReport $preflight $preflightReportFull
}

if ($UseDynamoJournal -and -not $AllowUnwarmedDynamoJournal -and [bool] $preflight.dynamoSettingsAppearsWarmed -ne $true) {
    throw "Dynamo journal launch requires an existing parseable DynamoSettings.xml so Autodesk/Dynamo privacy and startup prompts are handled manually before automation. Run Dynamo once in this test profile, answer prompts manually, then rerun. Pass -AllowUnwarmedDynamoJournal only for explicitly supervised local experiments."
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
        preflightReportPath = if ($shouldWritePreflightReport) { $preflightReportFull } else { "" }
        preflight = $preflight
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
$oldInstallRoot = $env:REVIT_MCP_NEXT_INSTALL_ROOT
$oldAuthConfig = $env:REVIT_MCP_NEXT_AUTH_CONFIG
try {
    $env:REVIT_MCP_NEXT_INSTALL_ROOT = $installRootFull
    $env:REVIT_MCP_NEXT_AUTH_CONFIG = $authConfigFull
    $env:REVIT_MCP_NEXT_DYNAMO_EVIDENCE = $evidenceFull
    if (-not [string]::IsNullOrWhiteSpace($modelFull)) {
        $env:REVIT_MCP_NEXT_DYNAMO_MODEL = $modelFull
    }

    Write-DynamoPreflight $preflight
    if ($shouldWritePreflightReport) {
        Write-Step "Dynamo preflight report: $preflightReportFull"
    }

    if ($LaunchRevit) {
        $launchArgs = @()
        if (-not [string]::IsNullOrWhiteSpace($modelFull)) {
            $launchArgs += "`"$modelFull`""
        }

        if ($UseDynamoJournal) {
            New-DynamoJournalFile $dynamoJournalFull $graphFull
            $launchArgs += "/J"
            $launchArgs += "`"$dynamoJournalFull`""
        }

        if ($UseDynamoJournal) {
            Write-Step "Launching Revit with a warmed-profile Dynamo journal so it inherits smoke environment variables and runs the graph."
            Write-Step "Dynamo journal: $dynamoJournalFull"
        } else {
            Write-Step "Launching Revit so it inherits Dynamo smoke environment variables."
        }
        Start-Process -FilePath $revitExe -ArgumentList $launchArgs | Out-Null
    }

    Write-Step "Dynamo host smoke is waiting for evidence."
    Write-Step "Graph: $graphFull"
    Write-Step "Evidence path: $evidenceFull"
    if ($UseDynamoJournal) {
        Write-Step "Waiting for the Dynamo journal to open and run the packaged graph. If Dynamo shows privacy or startup prompts, handle them manually and rerun."
    } else {
        Write-Step "Open Dynamo for Revit, run the graph once, then keep this script open until validation completes."
    }

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
    $env:REVIT_MCP_NEXT_INSTALL_ROOT = $oldInstallRoot
    $env:REVIT_MCP_NEXT_AUTH_CONFIG = $oldAuthConfig
}

$result = [ordered] @{
    status = [string] $evidence.status
    evidencePath = $evidenceFull
    graphPath = $graphFull
    modelPath = $modelFull
    installRoot = $installRootFull
    preflightReportPath = if ($shouldWritePreflightReport) { $preflightReportFull } else { "" }
    dynamoJournalPath = if ($UseDynamoJournal) { $dynamoJournalFull } else { "" }
    preflight = $preflight
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
} else {
    Write-Step "Dynamo host smoke evidence: $evidenceFull"
}
