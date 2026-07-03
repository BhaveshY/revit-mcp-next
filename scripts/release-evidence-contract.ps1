param(
    [string] $OutputRoot = "",
    [switch] $KeepArtifacts
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next evidence-contract] $Message"
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Add-TrailingSeparator($Path) {
    if ($Path.EndsWith("\") -or $Path.EndsWith("/")) {
        return $Path
    }

    return "$Path\"
}

function Assert-PathChild($Root, $Path, $Label) {
    $rootFull = Get-FullPath $Root
    $pathFull = Get-FullPath $Path
    $rootWithSeparator = Add-TrailingSeparator $rootFull

    if ($pathFull -ne $rootFull -and -not $pathFull.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use $Label outside expected root. Root: $rootFull Target: $pathFull"
    }
}

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Read-AuthTokenConfig($Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ""
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*"?([^"\s]+)"?\s*$') {
            return $Matches[1]
        }
    }

    return ""
}

function Invoke-LoggedCommand([string] $LogPath, [string] $Command, [string[]] $Arguments) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
    $oldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $Command @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $oldErrorActionPreference
    }

    Set-Content -LiteralPath $LogPath -Value ($output | Out-String) -Encoding UTF8

    if ($exitCode -ne 0) {
        throw "$Command failed with exit code $exitCode. See $LogPath"
    }
}

function Invoke-RepoScript([string] $LogPath, [string] $Path, [string[]] $Arguments) {
    $allArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $Path)
    $allArguments += $Arguments
    Invoke-LoggedCommand $LogPath "powershell" $allArguments
}

function Assert-FileExists($Path, $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not created: $Path"
    }
}

function Assert-DirectoryExists($Path, $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label was not created: $Path"
    }
}

function New-SyntheticAddinOutput($Root) {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Set-Content -LiteralPath (Join-Path $Root "RevitMcpNext.Addin.dll") -Value "synthetic add-in placeholder for evidence contract" -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $Root "RevitMcpNext.Contracts.dll") -Value "synthetic contracts placeholder for evidence contract" -Encoding ASCII
}

function New-SyntheticHostIntegrationEvidence(
    $Root,
    [switch] $Failed,
    [string] $AddinAssemblySha256 = "",
    [string] $AddinAssemblyPath = "C:\synthetic\RevitMcpNext.Addin.dll"
) {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    $pyRevitEvidencePath = Join-Path $Root "pyrevit.json"
    $dynamoEvidencePath = Join-Path $Root "dynamo.json"
    $dynamoStatus = if ($Failed) { "failed" } else { "passed" }
    $dynamoPreviewReady = if ($Failed) { $false } else { $true }

    Set-Content -LiteralPath $pyRevitEvidencePath -Value ([ordered] @{
        schemaVersion = 1
        status = "passed"
        host = "pyrevit"
        activeDocument = [ordered] @{
            title = "Synthetic Evidence Contract.rvt"
            fingerprint = "doc-synthetic-evidence-contract"
            generation = 42
        }
        coveredTools = @("inprocess.status", "inprocess.preview_change_set", "inprocess.apply_change_set")
        coveredOperations = @("create_level")
        applyWrites = $true
        previewReady = $true
        inProcessBridge = [ordered] @{
            addinHandlerActive = $true
            handler = "configuredAddin"
            directFallbackActive = $false
            assemblyPath = $AddinAssemblyPath
            assemblySha256 = $AddinAssemblySha256
            fileVersion = "0.1.0.0"
            productVersion = "0.1.0.0"
        }
        createdElementIds = @("9001")
        evidencePath = $pyRevitEvidencePath
    } | ConvertTo-Json -Depth 8) -Encoding UTF8

    Set-Content -LiteralPath $dynamoEvidencePath -Value ([ordered] @{
        schemaVersion = 1
        status = $dynamoStatus
        host = "dynamo"
        activeDocument = [ordered] @{
            title = "Synthetic Evidence Contract.rvt"
            fingerprint = "doc-synthetic-evidence-contract"
            generation = 42
        }
        coveredTools = @("inprocess.status", "inprocess.preview_change_set", "inprocess.apply_change_set")
        coveredOperations = @("create_level")
        applyWrites = $true
        previewReady = $dynamoPreviewReady
        inProcessBridge = [ordered] @{
            addinHandlerActive = -not $Failed
            handler = if ($Failed) { "directFallback" } else { "configuredAddin" }
            directFallbackActive = [bool] $Failed
            assemblyPath = $AddinAssemblyPath
            assemblySha256 = $AddinAssemblySha256
            fileVersion = "0.1.0.0"
            productVersion = "0.1.0.0"
        }
        createdElementIds = if ($Failed) { @() } else { @("9002") }
        evidencePath = $dynamoEvidencePath
    } | ConvertTo-Json -Depth 8) -Encoding UTF8

    Set-Content -LiteralPath (Join-Path $Root "host-integrations-summary.json") -Value ([ordered] @{
        schemaVersion = 1
        status = if ($Failed) { "failed" } else { "passed" }
        hosts = [ordered] @{
            pyrevit = [ordered] @{
                status = "passed"
                evidencePath = "pyrevit.json"
                previewReady = $true
                inProcessBridge = [ordered] @{
                    addinHandlerActive = $true
                    handler = "configuredAddin"
                    directFallbackActive = $false
                    assemblyPath = $AddinAssemblyPath
                    assemblySha256 = $AddinAssemblySha256
                    fileVersion = "0.1.0.0"
                    productVersion = "0.1.0.0"
                }
                createdElementIds = @("9001")
            }
            dynamo = [ordered] @{
                status = $dynamoStatus
                evidencePath = "dynamo.json"
                previewReady = $dynamoPreviewReady
                inProcessBridge = [ordered] @{
                    addinHandlerActive = -not $Failed
                    handler = if ($Failed) { "directFallback" } else { "configuredAddin" }
                    directFallbackActive = [bool] $Failed
                    assemblyPath = $AddinAssemblyPath
                    assemblySha256 = $AddinAssemblySha256
                    fileVersion = "0.1.0.0"
                    productVersion = "0.1.0.0"
                }
                createdElementIds = if ($Failed) { @() } else { @("9002") }
            }
        }
    } | ConvertTo-Json -Depth 8) -Encoding UTF8
}

function Assert-ScriptFailsLike([string] $Path, [string[]] $Arguments, [string] $ExpectedPattern, [string] $Label) {
    $oldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $oldErrorActionPreference
    }

    if ($exitCode -eq 0) {
        throw "$Label unexpectedly passed."
    }

    $text = ($output | Out-String)
    if ($text -notlike $ExpectedPattern) {
        throw "$Label failed with unexpected output: $text"
    }
}

function Assert-InventoryContains($Entries, $Path) {
    foreach ($entry in $Entries) {
        if ($entry.path -eq $Path) {
            return
        }
    }

    throw "Evidence contents did not include $Path."
}

function Assert-NoRawTokenInEvidence($EvidenceRoot, $Token) {
    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw "Auth token was not found in the temp install."
    }

    $textExtensions = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(".json", ".md", ".txt", ".log", ".sha256", ".cmd", ".env")) {
        $textExtensions.Add($extension) | Out-Null
    }

    $textFiles = Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File |
        Where-Object { $textExtensions.Contains($_.Extension) }

    foreach ($file in $textFiles) {
        try {
            $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        } catch {
            continue
        }

        if ($text.Contains($Token)) {
            throw "Release evidence leaked the raw auth token in $($file.FullName)."
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release-evidence-contract"
}

$outputRootFull = Get-FullPath $OutputRoot
New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null

$runId = "r-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$runRoot = Join-Path $outputRootFull $runId
Assert-PathChild $outputRootFull $runRoot "release evidence contract run root"

$oldAppData = $env:APPDATA
$oldLocalAppData = $env:LOCALAPPDATA

try {
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $syntheticAddinRoot = Join-Path $runRoot "syn"
    $packageOutputRoot = Join-Path $runRoot "pkg"
    $installRoot = Join-Path $runRoot "inst"
    $supportRoot = Join-Path $runRoot "sup"
    $liveSmokeRoot = Join-Path $runRoot "live"
    $hostIntegrationRawRoot = Join-Path $runRoot "host-integrations-raw"
    $hostIntegrationRoot = Join-Path $runRoot "host-integrations"
    $evidenceOutputRoot = Join-Path $runRoot "evidence"
    $logsRoot = Join-Path $runRoot "logs"
    $env:APPDATA = Join-Path $runRoot "ad"
    $env:LOCALAPPDATA = Join-Path $runRoot "lad"
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $logsRoot | Out-Null

    Write-Step "Run root: $runRoot"
    New-SyntheticAddinOutput $syntheticAddinRoot

    $validateLog = Join-Path $logsRoot "validate-repo.log"
    Invoke-LoggedCommand $validateLog "node" @((Join-Path $repoRoot "scripts\validate-repo.mjs"))

    $packageLog = Join-Path $logsRoot "package-release.log"
    Invoke-RepoScript $packageLog (Join-Path $repoRoot "scripts\package-release.ps1") @(
        "-OutputRoot", $packageOutputRoot,
        "-AddinOutputRoot", $syntheticAddinRoot
    )

    $rootPackage = Read-JsonFile (Join-Path $repoRoot "package.json")
    $packageRoot = Join-Path $packageOutputRoot "revit-mcp-next-$($rootPackage.version)-windows"
    Assert-DirectoryExists $packageRoot "staged package"
    Assert-FileExists "$packageRoot.zip" "package zip"
    $packagedAddinSha256 = (Get-FileHash -LiteralPath (Join-Path $packageRoot "payload\addin\RevitMcpNext.Addin.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
    $installedAddinPath = Join-Path $installRoot "addin\RevitMcpNext.Addin.dll"

    $installerScript = Join-Path $packageRoot "installer\install-windows.ps1"
    Invoke-RepoScript (Join-Path $logsRoot "install-windows.log") $installerScript @(
        "-PackageRoot", $packageRoot,
        "-InstallRoot", $installRoot,
        "-RevitYears", "2024"
    )

    $doctorLog = Join-Path $logsRoot "doctor-windows.log"
    Invoke-RepoScript $doctorLog (Join-Path $packageRoot "scripts\doctor.ps1") @(
        "-InstallRoot", $installRoot,
        "-RevitYear", "2024"
    )

    $supportLog = Join-Path $logsRoot "support-bundle.log"
    Invoke-RepoScript $supportLog (Join-Path $packageRoot "scripts\collect-support-bundle.ps1") @(
        "-InstallRoot", $installRoot,
        "-OutputRoot", $supportRoot,
        "-RevitYears", "2024"
    )
    $supportZip = Get-ChildItem -LiteralPath $supportRoot -Filter "*.zip" -File | Select-Object -First 1
    if (-not $supportZip) {
        throw "Support bundle zip was not created under $supportRoot."
    }

    $authToken = Read-AuthTokenConfig (Join-Path $installRoot "config\auth.env")
    if ([string]::IsNullOrWhiteSpace($authToken)) {
        throw "Auth token was not found in the temp install."
    }

    New-Item -ItemType Directory -Force -Path $liveSmokeRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $liveSmokeRoot "smoke-revit.log") -Value "synthetic live smoke artifact for evidence contract" -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $liveSmokeRoot "run-inputs.json") -Value (@{ revitYear = 2024; synthetic = $true } | ConvertTo-Json) -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $liveSmokeRoot "smoke-summary.json") -Value ([ordered] @{
        schemaVersion = 1
        status = "passed"
        mode = "full"
        expectedRevitYear = "2024"
        revit = [ordered] @{
            version = "2024"
            build = "synthetic"
        }
        addinAssembly = [ordered] @{
            assemblyPath = $installedAddinPath
            assemblySha256 = $packagedAddinSha256
            fileVersion = "0.1.0.0"
            productVersion = "0.1.0"
        }
        activeDocument = [ordered] @{
            title = "Synthetic Evidence Contract.rvt"
            fingerprint = "doc-synthetic-evidence-contract"
            generation = 42
        }
        documentFingerprint = "doc-synthetic-evidence-contract"
        coveredTools = @("revit.status", "revit.cancel_request", "revit.get_rooms", "revit.preview_change_set", "revit.apply_change_set")
        coveredOperations = @("create_level", "create_wall", "create_room", "tag_room", "tag_element")
        skippedOperations = @()
        requiredCoverage = [ordered] @{
            typeChange = $false
            roomTag = $true
            elementTag = $true
        }
        tagSelectors = [ordered] @{
            room = [ordered] @{
                nameContains = "Room"
            }
            element = [ordered] @{
                nameContains = "Wall"
            }
        }
        result = [ordered] @{
            tagCoverage = [ordered] @{
                room = [ordered] @{
                    roomId = "601"
                    roomUniqueId = "room-601"
                    viewId = "1024"
                    tagTypeId = "9700"
                    tagTypeName = "Room Tag"
                    createdTagId = "1601"
                }
                element = [ordered] @{
                    elementId = "501"
                    elementUniqueId = "wall-501"
                    viewId = "1024"
                    tagTypeId = "9701"
                    tagTypeName = "Wall Tag"
                    createdTagId = "1602"
                }
            }
        }
    } | ConvertTo-Json -Depth 8) -Encoding UTF8

    $failedLiveSmokeRoot = Join-Path $runRoot "live-failed"
    New-Item -ItemType Directory -Force -Path $failedLiveSmokeRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $failedLiveSmokeRoot "smoke-summary.json") -Value (@{
        schemaVersion = 1
        status = "failed"
        error = "synthetic failed smoke"
    } | ConvertTo-Json) -Encoding UTF8

    $ambiguousLiveSmokeRoot = Join-Path $runRoot "live-ambiguous"
    New-Item -ItemType Directory -Force -Path (Join-Path $ambiguousLiveSmokeRoot "a") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $ambiguousLiveSmokeRoot "b") | Out-Null
    Copy-Item -LiteralPath (Join-Path $liveSmokeRoot "smoke-summary.json") -Destination (Join-Path $ambiguousLiveSmokeRoot "a\smoke-summary.json") -Force
    Copy-Item -LiteralPath (Join-Path $liveSmokeRoot "smoke-summary.json") -Destination (Join-Path $ambiguousLiveSmokeRoot "b\smoke-summary.json") -Force

    $missingIdentityLiveSmokeRoot = Join-Path $runRoot "live-missing-identity"
    New-Item -ItemType Directory -Force -Path $missingIdentityLiveSmokeRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $missingIdentityLiveSmokeRoot "smoke-summary.json") -Value ([ordered] @{
        schemaVersion = 1
        status = "passed"
        mode = "full"
        expectedRevitYear = "2024"
        revit = @{ version = "2024"; build = "synthetic" }
        activeDocument = @{ title = "Synthetic Evidence Contract.rvt"; fingerprint = "doc-synthetic-evidence-contract"; generation = 42 }
        documentFingerprint = "doc-synthetic-evidence-contract"
        coveredTools = @("revit.status", "revit.cancel_request", "revit.preview_change_set", "revit.apply_change_set")
        coveredOperations = @("create_level")
        skippedOperations = @(
            @{ type = "tag_room"; reason = "Synthetic evidence contract does not load room tag families." },
            @{ type = "tag_element"; reason = "Synthetic evidence contract does not load element tag families." }
        )
    } | ConvertTo-Json -Depth 8) -Encoding UTF8

    $mismatchedIdentityLiveSmokeRoot = Join-Path $runRoot "live-mismatched-identity"
    New-Item -ItemType Directory -Force -Path $mismatchedIdentityLiveSmokeRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $mismatchedIdentityLiveSmokeRoot "smoke-summary.json") -Value ([ordered] @{
        schemaVersion = 1
        status = "passed"
        mode = "full"
        expectedRevitYear = "2024"
        revit = @{ version = "2024"; build = "synthetic" }
        addinAssembly = @{ assemblyPath = $installedAddinPath; assemblySha256 = ("0" * 64) }
        activeDocument = @{ title = "Synthetic Evidence Contract.rvt"; fingerprint = "doc-synthetic-evidence-contract"; generation = 42 }
        documentFingerprint = "doc-synthetic-evidence-contract"
        coveredTools = @("revit.status", "revit.cancel_request", "revit.preview_change_set", "revit.apply_change_set")
        coveredOperations = @("create_level")
        skippedOperations = @(
            @{ type = "tag_room"; reason = "Synthetic evidence contract does not load room tag families." },
            @{ type = "tag_element"; reason = "Synthetic evidence contract does not load element tag families." }
        )
    } | ConvertTo-Json -Depth 8) -Encoding UTF8

    New-SyntheticHostIntegrationEvidence $hostIntegrationRawRoot -AddinAssemblySha256 $packagedAddinSha256 -AddinAssemblyPath $installedAddinPath
    Invoke-RepoScript (Join-Path $logsRoot "host-integrations-evidence.log") (Join-Path $repoRoot "scripts\collect-host-integration-evidence.ps1") @(
        "-PyRevitEvidencePath", (Join-Path $hostIntegrationRawRoot "pyrevit.json"),
        "-DynamoEvidencePath", (Join-Path $hostIntegrationRawRoot "dynamo.json"),
        "-OutputRoot", $hostIntegrationRoot
    )

    $failedHostIntegrationRoot = Join-Path $runRoot "host-integrations-failed"
    New-SyntheticHostIntegrationEvidence $failedHostIntegrationRoot -Failed -AddinAssemblySha256 $packagedAddinSha256 -AddinAssemblyPath $installedAddinPath

    $mismatchedHostIntegrationRoot = Join-Path $runRoot "host-integrations-mismatched"
    New-SyntheticHostIntegrationEvidence $mismatchedHostIntegrationRoot -AddinAssemblySha256 ("0" * 64) -AddinAssemblyPath $installedAddinPath

    $ambiguousHostIntegrationRoot = Join-Path $runRoot "host-integrations-ambiguous"
    New-Item -ItemType Directory -Force -Path (Join-Path $ambiguousHostIntegrationRoot "a") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $ambiguousHostIntegrationRoot "b") | Out-Null
    Copy-Item -LiteralPath (Join-Path $hostIntegrationRoot "host-integrations-summary.json") -Destination (Join-Path $ambiguousHostIntegrationRoot "a\host-integrations-summary.json") -Force
    Copy-Item -LiteralPath (Join-Path $hostIntegrationRoot "host-integrations-summary.json") -Destination (Join-Path $ambiguousHostIntegrationRoot "b\host-integrations-summary.json") -Force

    $leakyAdditionalEvidenceRoot = Join-Path $runRoot "leaky-additional-evidence"
    New-Item -ItemType Directory -Force -Path $leakyAdditionalEvidenceRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $leakyAdditionalEvidenceRoot "leaky.log") -Value "REVIT_MCP_NEXT_AUTH_TOKEN=$authToken" -Encoding UTF8

    $evidenceScript = Join-Path $repoRoot "scripts\collect-release-evidence.ps1"
    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-live"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-SupportBundlePath", $supportZip.FullName
    ) "*Live Revit smoke evidence was not provided*" "Missing live-smoke evidence gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-support"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $liveSmokeRoot
    ) "*Support bundle evidence was not provided*" "Missing support-bundle evidence gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-signing"),
        "-LiveSmokeEvidencePath", $liveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $hostIntegrationRoot
    ) "*Signing was not requested for this build*" "Missing signing skip reason gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-smoke-summary"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $failedLiveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $hostIntegrationRoot
    ) "*Live Revit smoke summary did not pass*" "Failed live-smoke summary gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-live-ambiguous"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $ambiguousLiveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $hostIntegrationRoot
    ) "*multiple smoke-summary.json files*" "Ambiguous live-smoke summary gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-live-smoke-missing-identity"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $missingIdentityLiveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $hostIntegrationRoot
    ) "*Live Revit smoke summary is missing addinAssembly identity*" "Missing live-smoke package identity gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-live-smoke-package-identity"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $mismatchedIdentityLiveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $hostIntegrationRoot
    ) "*Live Revit smoke loaded add-in SHA-256*" "Mismatched live-smoke package identity gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-host-integrations-missing"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $liveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName
    ) "*Hosted pyRevit/Dynamo integration smoke evidence was not provided*" "Missing hosted integration evidence gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-host-integrations-summary"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $liveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $failedHostIntegrationRoot
    ) "*Hosted pyRevit/Dynamo integration summary did not pass*" "Failed hosted integration summary gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-host-integrations-ambiguous"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $liveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $ambiguousHostIntegrationRoot
    ) "*multiple host-integrations-summary.json*files*" "Ambiguous hosted integration summary gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-host-integrations-package-identity"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $liveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $mismatchedHostIntegrationRoot
    ) "*loaded add-in SHA-256*" "Mismatched hosted integration package identity gate"

    Assert-ScriptFailsLike $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-OutputRoot", (Join-Path $runRoot "fail-raw-secret"),
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $liveSmokeRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-HostedIntegrationEvidencePath", $hostIntegrationRoot,
        "-AdditionalEvidencePaths", $leakyAdditionalEvidenceRoot
    ) "*potential raw secret*" "Raw secret release evidence gate"

    Invoke-RepoScript (Join-Path $logsRoot "release-evidence.log") $evidenceScript @(
        "-PackageRoot", $packageRoot,
        "-PackageZipPath", "$packageRoot.zip",
        "-OutputRoot", $evidenceOutputRoot,
        "-SigningSkipReason", "No signing certificate configured in hosted evidence contract.",
        "-LiveSmokeEvidencePath", $liveSmokeRoot,
        "-HostedIntegrationEvidencePath", $hostIntegrationRoot,
        "-SupportBundlePath", $supportZip.FullName,
        "-ValidateRepoLogPath", $validateLog,
        "-PackageLogPath", $packageLog,
        "-DoctorLogPath", $doctorLog,
        "-CommandLogPaths", "$supportLog;$(Join-Path $logsRoot "host-integrations-evidence.log")"
    )

    $evidenceRoot = Get-ChildItem -LiteralPath $evidenceOutputRoot -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $evidenceRoot) {
        throw "Release evidence directory was not created under $evidenceOutputRoot."
    }

    Assert-FileExists (Join-Path $evidenceRoot.FullName "release-evidence-manifest.json") "release evidence manifest"
    Assert-FileExists (Join-Path $evidenceRoot.FullName "release-evidence-summary.md") "release evidence summary"
    Assert-FileExists "$($evidenceRoot.FullName).zip" "release evidence zip"
    Assert-FileExists (Join-Path $evidenceRoot.FullName "package\release-manifest.json") "copied package release manifest"
    Assert-FileExists (Join-Path $evidenceRoot.FullName "package\CHECKSUMS.sha256") "copied package checksums"
    Assert-FileExists (Join-Path $evidenceRoot.FullName "package\package-zip.sha256") "package zip checksum"

    $evidenceManifest = Read-JsonFile (Join-Path $evidenceRoot.FullName "release-evidence-manifest.json")
    if ($evidenceManifest.schemaVersion -ne 1) {
        throw "Unexpected evidence schema version: $($evidenceManifest.schemaVersion)"
    }
    if ($evidenceManifest.package.version -ne $rootPackage.version) {
        throw "Evidence package version mismatch."
    }
    if ($evidenceManifest.package.evidence.packageZipSha256 -notmatch "^[a-f0-9]{64}$") {
        throw "Package zip SHA-256 was not recorded."
    }
    if ($evidenceManifest.package.evidence.packageZipName -notlike "*.zip") {
        throw "Package zip metadata did not point at a .zip file."
    }
    if ($evidenceManifest.package.evidence.packagedAddin.sha256 -ne $packagedAddinSha256) {
        throw "Packaged add-in SHA-256 was not recorded in release evidence."
    }
    if ($evidenceManifest.signing.status -ne "skipped" -or [string]::IsNullOrWhiteSpace($evidenceManifest.signing.skipReason)) {
        throw "Unsigned package evidence did not record an explicit signing skip reason."
    }
    if ($evidenceManifest.liveSmoke.status -ne "captured") {
        throw "Live smoke evidence was not marked captured."
    }
    if ($evidenceManifest.liveSmoke.summary.status -ne "passed") {
        throw "Live smoke summary pass status was not recorded."
    }
    if ($evidenceManifest.liveSmoke.summary.expectedRevitYear -ne "2024") {
        throw "Live smoke expected Revit year was not recorded."
    }
    if ($evidenceManifest.liveSmoke.summary.documentFingerprint -ne "doc-synthetic-evidence-contract") {
        throw "Live smoke document fingerprint was not recorded."
    }
    if ($evidenceManifest.liveSmoke.summary.packageIdentity.expectedSha256 -ne $packagedAddinSha256) {
        throw "Live smoke package identity did not record the expected add-in SHA-256."
    }
    if ($evidenceManifest.liveSmoke.summary.packageIdentity.assemblySha256 -ne $packagedAddinSha256) {
        throw "Live smoke loaded add-in SHA-256 was not recorded."
    }
    if ($evidenceManifest.liveSmoke.summary.requiredCoverage.roomTag -ne $true -or $evidenceManifest.liveSmoke.summary.requiredCoverage.elementTag -ne $true) {
        throw "Live smoke required tag coverage flags were not recorded."
    }
    if ($evidenceManifest.liveSmoke.summary.tagSelectors.room.nameContains -ne "Room" -or $evidenceManifest.liveSmoke.summary.tagSelectors.element.nameContains -ne "Wall") {
        throw "Live smoke tag selectors were not recorded."
    }
    if ($evidenceManifest.liveSmoke.summary.tagCoverage.room.createdTagId -ne "1601" -or $evidenceManifest.liveSmoke.summary.tagCoverage.element.createdTagId -ne "1602") {
        throw "Live smoke tag coverage details were not recorded."
    }
    if ($evidenceManifest.supportBundle.status -ne "captured") {
        throw "Support bundle evidence was not marked captured."
    }
    if ($evidenceManifest.hostedIntegrations.status -ne "captured") {
        throw "Hosted pyRevit/Dynamo evidence was not marked captured."
    }
    if ($evidenceManifest.hostedIntegrations.summary.status -ne "passed") {
        throw "Hosted pyRevit/Dynamo summary pass status was not recorded."
    }
    if ($evidenceManifest.hostedIntegrations.summary.hosts.pyrevit.status -ne "passed") {
        throw "pyRevit hosted integration pass status was not recorded."
    }
    if ($evidenceManifest.hostedIntegrations.summary.hosts.dynamo.status -ne "passed") {
        throw "Dynamo hosted integration pass status was not recorded."
    }
    if ($evidenceManifest.hostedIntegrations.summary.packageIdentity.expectedSha256 -ne $packagedAddinSha256) {
        throw "Hosted integration package identity did not record the expected add-in SHA-256."
    }
    if ($evidenceManifest.hostedIntegrations.summary.packageIdentity.hosts.pyrevit.assemblySha256 -ne $packagedAddinSha256) {
        throw "pyRevit hosted integration package identity was not recorded."
    }
    if ($evidenceManifest.hostedIntegrations.summary.packageIdentity.hosts.dynamo.assemblySha256 -ne $packagedAddinSha256) {
        throw "Dynamo hosted integration package identity was not recorded."
    }
    if ($evidenceManifest.validation.validateRepoLog.present -ne $true) {
        throw "validate-repo log was not recorded."
    }
    if ($evidenceManifest.validation.packageLog.present -ne $true) {
        throw "package log was not recorded."
    }
    if ($evidenceManifest.validation.doctorLog.present -ne $true) {
        throw "doctor log was not recorded."
    }

    Assert-InventoryContains $evidenceManifest.contents "package/release-manifest.json"
    Assert-InventoryContains $evidenceManifest.contents "package/CHECKSUMS.sha256"
    Assert-InventoryContains $evidenceManifest.contents "package/package-zip.sha256"
    Assert-InventoryContains $evidenceManifest.contents "host-integrations/host-integrations-summary.json"
    Assert-InventoryContains $evidenceManifest.contents "release-evidence-summary.md"

    $readinessScript = Join-Path $repoRoot "scripts\check-release-readiness.ps1"
    Invoke-RepoScript (Join-Path $logsRoot "readiness-external-preview.log") $readinessScript @(
        "-EvidencePath", $evidenceRoot.FullName,
        "-Profile", "external-preview",
        "-AllowDirty"
    )
    Invoke-RepoScript (Join-Path $logsRoot "readiness-release-candidate.log") $readinessScript @(
        "-EvidencePath", "$($evidenceRoot.FullName).zip",
        "-Profile", "release-candidate",
        "-AllowDirty"
    )
    Assert-ScriptFailsLike $readinessScript @(
        "-EvidencePath", $evidenceRoot.FullName,
        "-Profile", "production",
        "-AllowDirty"
    ) "*Production readiness requires captured signing evidence*" "Production readiness signing gate"

    $tamperedTagEvidenceRoot = Join-Path $runRoot "tampered-required-tag-skipped"
    Copy-Item -LiteralPath $evidenceRoot.FullName -Destination $tamperedTagEvidenceRoot -Recurse -Force
    $tamperedTagManifestPath = Join-Path $tamperedTagEvidenceRoot "release-evidence-manifest.json"
    $tamperedTagManifest = Get-Content -LiteralPath $tamperedTagManifestPath -Raw | ConvertFrom-Json
    $tamperedTagManifest.liveSmoke.summary.coveredOperations = @($tamperedTagManifest.liveSmoke.summary.coveredOperations | Where-Object { [string] $_ -ne "tag_element" })
    $tamperedTagManifest.liveSmoke.summary.tagCoverage.element = $null
    Set-Content -LiteralPath $tamperedTagManifestPath -Value ($tamperedTagManifest | ConvertTo-Json -Depth 12) -Encoding UTF8
    Assert-ScriptFailsLike $readinessScript @(
        "-EvidencePath", $tamperedTagEvidenceRoot,
        "-Profile", "release-candidate",
        "-AllowDirty"
    ) "*Live smoke required tag_element*" "Required tag coverage readiness gate"

    $tamperedEvidenceRoot = Join-Path $runRoot "tampered-missing-evidence-file"
    Copy-Item -LiteralPath $evidenceRoot.FullName -Destination $tamperedEvidenceRoot -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $tamperedEvidenceRoot "live-smoke\smoke-summary.json") -Force
    Assert-ScriptFailsLike $readinessScript @(
        "-EvidencePath", $tamperedEvidenceRoot,
        "-Profile", "external-preview",
        "-AllowDirty"
    ) "*Evidence inventory file is missing*" "Missing evidence file readiness gate"

    Assert-NoRawTokenInEvidence $evidenceRoot.FullName $authToken

    Write-Step "Release evidence contract passed."
} finally {
    $env:APPDATA = $oldAppData
    $env:LOCALAPPDATA = $oldLocalAppData

    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $runRoot -PathType Container)) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    } elseif ($KeepArtifacts) {
        Write-Step "Kept artifacts: $runRoot"
    }
}
