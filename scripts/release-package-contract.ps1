param(
    [string] $OutputRoot = "",
    [switch] $KeepArtifacts
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next release-contract] $Message"
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

function Invoke-RepoScript([string] $Path, [string[]] $Arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Path failed with exit code $LASTEXITCODE."
    }
}

function Invoke-RepoScriptCapture([string] $Path, [string[]] $Arguments) {
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String)
    if ($exitCode -ne 0) {
        Write-Host $text
        throw "$Path failed with exit code $exitCode."
    }

    return $text
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
    Set-Content -LiteralPath (Join-Path $Root "RevitMcpNext.Addin.dll") -Value "synthetic add-in placeholder for package contract" -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $Root "RevitMcpNext.Contracts.dll") -Value "synthetic contracts placeholder for package contract" -Encoding ASCII
}

function Assert-DevSigningDryRuns($RepoRoot, $SyntheticAddinRoot, $RunRoot) {
    $devCertScript = Join-Path $RepoRoot "scripts\ensure-dev-signing-certificate.ps1"
    $devCertOutput = Invoke-RepoScriptCapture $devCertScript @("-DryRun", "-Trust", "-Json")
    $devCertState = $devCertOutput | ConvertFrom-Json
    if ($devCertState.dryRun -ne $true) {
        throw "Dev signing certificate dry run did not report dryRun=true."
    }
    if ($devCertState.trusted.requested -ne $true) {
        throw "Dev signing certificate dry run did not request CurrentUser trust."
    }
    if ([string] $devCertState.store -ne "Cert:\CurrentUser\My") {
        throw "Dev signing certificate dry run used unexpected store: $($devCertState.store)"
    }

    $statusOutput = Invoke-RepoScriptCapture $devCertScript @("-StatusOnly", "-Json")
    $statusState = $statusOutput | ConvertFrom-Json
    if ($statusState.statusOnly -ne $true) {
        throw "Dev signing certificate status mode did not report statusOnly=true."
    }
    if (-not $statusState.stores -or $statusState.stores.Count -ne 3) {
        throw "Dev signing certificate status mode did not inspect the expected CurrentUser stores."
    }

    $removeOutput = Invoke-RepoScriptCapture $devCertScript @("-Remove", "-DryRun", "-Json")
    $removeState = $removeOutput | ConvertFrom-Json
    if ($removeState.dryRun -ne $true) {
        throw "Dev signing certificate remove dry run did not report dryRun=true."
    }
    if ($null -eq $removeState.removed) {
        throw "Dev signing certificate remove dry run did not report removed entries."
    }

    $packageScript = Join-Path $RepoRoot "scripts\package-release.ps1"
    $dryRunOutputRoot = Join-Path $RunRoot "signed-dry-run"
    $packageOutput = Invoke-RepoScriptCapture $packageScript @(
        "-OutputRoot", $dryRunOutputRoot,
        "-AddinOutputRoot", $SyntheticAddinRoot,
        "-DryRun",
        "-Sign",
        "-RequireSigned",
        "-RequireTrustedSignatures",
        "-SigningCertificateThumbprint", "0000000000000000000000000000000000000000",
        "-NoTimestamp"
    )
    if (-not $packageOutput.Contains("Would sign Authenticode targets under")) {
        throw "Signed package dry run did not plan Authenticode signing."
    }

    $signScript = Join-Path $RepoRoot "scripts\sign-release.ps1"
    $signOutput = Invoke-RepoScriptCapture $signScript @(
        "-Path", (Join-Path $SyntheticAddinRoot "RevitMcpNext.Addin.dll"),
        "-DryRun",
        "-NoTimestamp"
    )
    if (-not $signOutput.Contains("Would sign:")) {
        throw "Signer dry run did not accept -NoTimestamp."
    }
}

function Assert-RevitTrustDryRun($RepoRoot) {
    $trustScript = Join-Path $RepoRoot "scripts\ensure-revit-addin-trust.ps1"
    $trustOutput = Invoke-RepoScriptCapture $trustScript @("-DryRun", "-RevitYears", "2024", "-Json")
    $trustState = $trustOutput | ConvertFrom-Json
    if ($trustState.dryRun -ne $true) {
        throw "Revit Always Load trust dry run did not report dryRun=true."
    }
    if ([string] $trustState.clientId -ne "6f78e70d-be13-4e0b-9b11-9e28f876af71") {
        throw "Revit Always Load trust dry run used unexpected client id: $($trustState.clientId)"
    }
    if (-not $trustState.entries -or [string] $trustState.entries[0].path -ne "HKCU:\Software\Autodesk\Revit\Autodesk Revit 2024\CodeSigning") {
        throw "Revit Always Load trust dry run used unexpected registry path."
    }
}

function Assert-PyRevitHostsCacheDryRun($RepoRoot, $RunRoot) {
    $pyRevitHostsScript = Join-Path $RepoRoot "scripts\ensure-pyrevit-hosts-cache.ps1"
    $cachePath = Join-Path $RunRoot "pyrevit-hosts-cache\pyrevit-hosts.json"
    $output = Invoke-RepoScriptCapture $pyRevitHostsScript @(
        "-DryRun",
        "-Json",
        "-CachePath", $cachePath,
        "-Builds", "20990101_1515"
    )
    $state = $output | ConvertFrom-Json
    if ($state.dryRun -ne $true) {
        throw "pyRevit hosts cache dry run did not report dryRun=true."
    }
    if ([string] $state.cachePath -ne [System.IO.Path]::GetFullPath($cachePath)) {
        throw "pyRevit hosts cache dry run used unexpected cache path: $($state.cachePath)"
    }
    if (-not ($state.addedBuilds -contains "20990101_1515")) {
        throw "pyRevit hosts cache dry run did not plan the requested build."
    }
    if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
        throw "pyRevit hosts cache dry run wrote the cache file."
    }
}

function Assert-HostedSmokeWrapperDryRuns($PackageRoot, $InstallRoot, $RunRoot) {
    $pyRevitSmokeScript = Join-Path $PackageRoot "scripts\run-pyrevit-host-smoke.ps1"
    $dynamoSmokeScript = Join-Path $PackageRoot "scripts\run-dynamo-host-smoke.ps1"
    $hostIntegrationsSmokeScript = Join-Path $PackageRoot "scripts\run-host-integrations-smoke.ps1"
    $pyRevitEvidencePath = Join-Path $RunRoot "host-smoke\pyrevit.json"
    $dynamoEvidencePath = Join-Path $RunRoot "host-smoke\dynamo.json"
    $modelPath = Join-Path $RunRoot "host-smoke\disposable.rvt"

    $pyRevitOutput = Invoke-RepoScriptCapture $pyRevitSmokeScript @(
        "-DryRun",
        "-Json",
        "-InstallRoot", $InstallRoot,
        "-EvidencePath", $pyRevitEvidencePath,
        "-ModelPath", $modelPath,
        "-PyRevitPath", (Join-Path $RunRoot "tools\pyrevit.exe")
    )
    $pyRevitState = $pyRevitOutput | ConvertFrom-Json
    if ($pyRevitState.status -ne "planned") {
        throw "pyRevit host smoke dry run did not report planned status."
    }
    if ([string] $pyRevitState.evidencePath -ne [System.IO.Path]::GetFullPath($pyRevitEvidencePath)) {
        throw "pyRevit host smoke dry run used unexpected evidence path: $($pyRevitState.evidencePath)"
    }
    if (-not ([string] $pyRevitState.hostSmokeScript).Contains("Host Smoke.pushbutton\script.py")) {
        throw "pyRevit host smoke dry run did not target the packaged Host Smoke command."
    }
    if ($pyRevitState.runnerAddinImport.enabled -ne $true) {
        throw "pyRevit host smoke dry run did not plan the runner add-in import."
    }
    if (-not ([string] $pyRevitState.runnerAddinImport.manifestPath).EndsWith("pyrevit-runner-addin-import\RevitMcpNext.addin")) {
        throw "pyRevit host smoke dry run used unexpected runner add-in manifest path: $($pyRevitState.runnerAddinImport.manifestPath)"
    }
    if (-not ([string] $pyRevitState.command).Contains("--import=")) {
        throw "pyRevit host smoke dry run command did not include pyRevit --import."
    }
    if ([string] $pyRevitState.environment.REVIT_MCP_NEXT_INSTALL_ROOT -ne [System.IO.Path]::GetFullPath($InstallRoot)) {
        throw "pyRevit host smoke dry run did not pin REVIT_MCP_NEXT_INSTALL_ROOT."
    }
    if ([string] $pyRevitState.environment.REVIT_MCP_NEXT_AUTH_CONFIG -ne [System.IO.Path]::GetFullPath((Join-Path $InstallRoot "config\auth.env"))) {
        throw "pyRevit host smoke dry run did not pin REVIT_MCP_NEXT_AUTH_CONFIG."
    }

    $dynamoOutput = Invoke-RepoScriptCapture $dynamoSmokeScript @(
        "-DryRun",
        "-Json",
        "-InstallRoot", $InstallRoot,
        "-EvidencePath", $dynamoEvidencePath,
        "-ModelPath", $modelPath,
        "-LaunchRevit",
        "-RevitPath", (Join-Path $RunRoot "tools\Revit.exe")
    )
    $dynamoState = $dynamoOutput | ConvertFrom-Json
    if ($dynamoState.status -ne "planned") {
        throw "Dynamo host smoke dry run did not report planned status."
    }
    if ([string] $dynamoState.evidencePath -ne [System.IO.Path]::GetFullPath($dynamoEvidencePath)) {
        throw "Dynamo host smoke dry run used unexpected evidence path: $($dynamoState.evidencePath)"
    }
    if (-not ([string] $dynamoState.graphPath).EndsWith("integrations\dynamo\revit_mcp_next_host_smoke.dyn")) {
        throw "Dynamo host smoke dry run did not target the packaged host-smoke graph."
    }
    if ([string] $dynamoState.environment.REVIT_MCP_NEXT_DYNAMO_EVIDENCE -ne [System.IO.Path]::GetFullPath($dynamoEvidencePath)) {
        throw "Dynamo host smoke dry run did not plan the evidence environment variable."
    }

    $aggregateOutputRoot = Join-Path $RunRoot "host-integrations"
    $aggregateOutput = Invoke-RepoScriptCapture $hostIntegrationsSmokeScript @(
        "-DryRun",
        "-Json",
        "-OutputRoot", $aggregateOutputRoot,
        "-InstallRoot", $InstallRoot,
        "-ModelPath", $modelPath,
        "-PyRevitPath", (Join-Path $RunRoot "tools\pyrevit.exe"),
        "-LaunchRevitForDynamo",
        "-DynamoRevitPath", (Join-Path $RunRoot "tools\Revit.exe")
    )
    $aggregateState = $aggregateOutput | ConvertFrom-Json
    if ($aggregateState.status -ne "planned") {
        throw "Aggregate hosted integration smoke dry run did not report planned status."
    }
    if ([string] $aggregateState.summaryPath -ne [System.IO.Path]::GetFullPath((Join-Path $aggregateOutputRoot "host-integrations-summary.json"))) {
        throw "Aggregate hosted integration smoke dry run used unexpected summary path."
    }

    $passedPyRevitEvidencePath = Join-Path $RunRoot "host-smoke\passed-pyrevit.json"
    $passedDynamoEvidencePath = Join-Path $RunRoot "host-smoke\passed-dynamo.json"
    $failedDynamoEvidencePath = Join-Path $RunRoot "host-smoke\failed-dynamo.json"
    $fallbackPyRevitEvidencePath = Join-Path $RunRoot "host-smoke\fallback-pyrevit.json"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $passedPyRevitEvidencePath) | Out-Null

    $passedPyRevit = [ordered] @{
        schemaVersion = 1
        status = "passed"
        host = "pyrevit"
        previewReady = $true
        applyWrites = $true
        activeDocument = [ordered] @{ fingerprint = "doc-package-contract" }
        inProcessBridge = [ordered] @{
            addinHandlerActive = $true
            handler = "configuredAddin"
            directFallbackActive = $false
        }
        coveredTools = @("revit.status", "revit.preview_change_set", "revit.apply_change_set")
        coveredOperations = @("create_level")
        createdElementIds = @(1001)
    }
    $passedDynamo = [ordered] @{
        schemaVersion = 1
        status = "passed"
        host = "dynamo"
        previewReady = $true
        applyWrites = $true
        activeDocument = [ordered] @{ fingerprint = "doc-package-contract" }
        inProcessBridge = [ordered] @{
            addinHandlerActive = $true
            handler = "configuredAddin"
            directFallbackActive = $false
        }
        coveredTools = @("revit.status", "revit.preview_change_set", "revit.apply_change_set")
        coveredOperations = @("create_level")
        createdElementIds = @(1002)
    }
    $fallbackPyRevit = [ordered] @{}
    foreach ($property in $passedPyRevit.GetEnumerator()) {
        $fallbackPyRevit[$property.Key] = $property.Value
    }
    $fallbackPyRevit["inProcessBridge"] = [ordered] @{
        addinHandlerActive = $false
        handler = "directFallback"
        directFallbackActive = $true
    }
    $failedDynamo = [ordered] @{
        schemaVersion = 1
        status = "failed"
        host = "dynamo"
        previewReady = $false
        applyWrites = $false
        activeDocument = [ordered] @{ fingerprint = "doc-package-contract" }
        coveredTools = @()
        coveredOperations = @()
        createdElementIds = @()
    }
    Set-Content -LiteralPath $passedPyRevitEvidencePath -Value ($passedPyRevit | ConvertTo-Json -Depth 8) -Encoding UTF8
    Set-Content -LiteralPath $passedDynamoEvidencePath -Value ($passedDynamo | ConvertTo-Json -Depth 8) -Encoding UTF8
    Set-Content -LiteralPath $failedDynamoEvidencePath -Value ($failedDynamo | ConvertTo-Json -Depth 8) -Encoding UTF8
    Set-Content -LiteralPath $fallbackPyRevitEvidencePath -Value ($fallbackPyRevit | ConvertTo-Json -Depth 8) -Encoding UTF8

    Invoke-RepoScript $pyRevitSmokeScript @(
        "-ValidateOnly",
        "-InstallRoot", $InstallRoot,
        "-EvidencePath", $passedPyRevitEvidencePath
    )
    Invoke-RepoScript $dynamoSmokeScript @(
        "-ValidateOnly",
        "-InstallRoot", $InstallRoot,
        "-EvidencePath", $passedDynamoEvidencePath
    )
    Assert-ScriptFailsLike $dynamoSmokeScript @(
        "-ValidateOnly",
        "-InstallRoot", $InstallRoot,
        "-EvidencePath", $failedDynamoEvidencePath
    ) "*Dynamo evidence did not pass*" "Dynamo host smoke failed-evidence gate"
    Assert-ScriptFailsLike $pyRevitSmokeScript @(
        "-ValidateOnly",
        "-InstallRoot", $InstallRoot,
        "-EvidencePath", $fallbackPyRevitEvidencePath
    ) "*expected configuredAddin*" "pyRevit host smoke direct-fallback gate"
}

function Assert-PackagedNpmAliases($PackageRoot) {
    $package = Read-JsonFile (Join-Path $PackageRoot "package.json")
    foreach ($alias in @("doctor:clients", "smoke:pyrevit-host", "smoke:dynamo-host", "smoke:host-integrations", "evidence:host-integrations")) {
        if (-not $package.scripts.PSObject.Properties[$alias]) {
            throw "Packaged package.json is missing npm alias: $alias"
        }
    }
}

function Assert-NoRawTokenInSupportBundle($SupportRoot, $Token) {
    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw "Auth token was not found in the temp install."
    }

    $textFiles = Get-ChildItem -LiteralPath $SupportRoot -Recurse -File |
        Where-Object { $_.Extension -ne ".zip" }

    foreach ($file in $textFiles) {
        try {
            $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        } catch {
            continue
        }

        if ($text.Contains($Token)) {
            throw "Support bundle leaked the raw auth token in $($file.FullName)."
        }
    }
}

function Assert-TamperedPackageFails($PackageRoot, $RunRoot, $InstallerScript) {
    $tamperRoot = Join-Path $RunRoot "tmp"
    $tamperedPackage = Join-Path $tamperRoot "p"
    New-Item -ItemType Directory -Force -Path $tamperRoot | Out-Null
    Copy-Item -LiteralPath $PackageRoot -Destination $tamperedPackage -Recurse -Force
    Add-Content -LiteralPath (Join-Path $tamperedPackage "README.md") -Value "tampered-by-release-contract"

    $tamperInstallRoot = Join-Path $RunRoot "ti"
    $failedAsExpected = $false
    try {
        & $InstallerScript `
            -PackageRoot $tamperedPackage `
            -InstallRoot $tamperInstallRoot `
            -DryRun `
            -SkipDependencyInstall
    } catch {
        if ($_.Exception.Message -like "*Checksum mismatch*") {
            $failedAsExpected = $true
        } else {
            throw
        }
    }

    if (-not $failedAsExpected) {
        throw "Tampered package dry-run unexpectedly passed checksum verification."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release-contract"
}

$outputRootFull = Get-FullPath $OutputRoot
New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null

$runId = "r-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$runRoot = Join-Path $outputRootFull $runId
Assert-PathChild $outputRootFull $runRoot "release contract run root"

$oldAppData = $env:APPDATA
$oldLocalAppData = $env:LOCALAPPDATA

try {
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $syntheticAddinRoot = Join-Path $runRoot "syn"
    $packageOutputRoot = Join-Path $runRoot "pkg"
    $installRoot = Join-Path $runRoot "inst"
    $supportRoot = Join-Path $runRoot "sup"
    $env:APPDATA = Join-Path $runRoot "ad"
    $env:LOCALAPPDATA = Join-Path $runRoot "lad"
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA | Out-Null

    Write-Step "Run root: $runRoot"
    New-SyntheticAddinOutput $syntheticAddinRoot
    Assert-DevSigningDryRuns $repoRoot $syntheticAddinRoot $runRoot
    Assert-RevitTrustDryRun $repoRoot
    Assert-PyRevitHostsCacheDryRun $repoRoot $runRoot

    $packageScript = Join-Path $repoRoot "scripts\package-release.ps1"
    Assert-ScriptFailsLike $packageScript @(
        "-DryRun",
        "-OutputRoot", (Join-Path $runRoot "unsupported-year-package"),
        "-AddinOutputRoot", $syntheticAddinRoot,
        "-RevitYears", "2025"
    ) "*Revit 2025 packaging is not supported yet*" "Unsupported Revit year package gate"

    Invoke-RepoScript $packageScript @(
        "-OutputRoot", $packageOutputRoot,
        "-AddinOutputRoot", $syntheticAddinRoot
    )

    $rootPackage = Read-JsonFile (Join-Path $repoRoot "package.json")
    $packageRoot = Join-Path $packageOutputRoot "revit-mcp-next-$($rootPackage.version)-windows"
    Assert-DirectoryExists $packageRoot "staged package"
    Assert-FileExists "$packageRoot.zip" "package zip"
    Assert-FileExists (Join-Path $packageRoot "release-manifest.json") "release manifest"
    Assert-FileExists (Join-Path $packageRoot "CHECKSUMS.sha256") "package checksums"

    $manifest = Read-JsonFile (Join-Path $packageRoot "release-manifest.json")
    if ($manifest.signing.requested -ne $false) {
        throw "Release contract expected an unsigned package manifest."
    }
    if ($manifest.package.nodeModulesBundled -ne $true) {
        throw "Release contract expected packaged broker production node_modules."
    }
    if ($manifest.package.integrationsIncluded -ne $true) {
        throw "Release contract expected packaged pyRevit/Dynamo integrations."
    }
    Assert-FileExists (Join-Path $packageRoot "integrations\python\revit_mcp_next_client.py") "packaged Python integration client"
    Assert-FileExists (Join-Path $packageRoot "integrations\python\revit_mcp_next_inprocess.py") "packaged Python in-process integration helper"
    Assert-FileExists (Join-Path $packageRoot "integrations\python\revit_mcp_next_host_smoke.py") "packaged Python host-smoke evidence helper"
    Assert-FileExists (Join-Path $packageRoot "integrations\python\revit_mcp_next_workflow_examples.py") "packaged Python workflow examples helper"
    Assert-FileExists (Join-Path $packageRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Diagnostics.panel\Status.pushbutton\script.py") "packaged pyRevit status command"
    Assert-FileExists (Join-Path $packageRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Diagnostics.panel\Host Smoke.pushbutton\script.py") "packaged pyRevit host-smoke command"
    Assert-FileExists (Join-Path $packageRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Examples.panel\Workflow Samples.pushbutton\script.py") "packaged pyRevit workflow examples command"
    Assert-FileExists (Join-Path $packageRoot "integrations\dynamo\status_node.py") "packaged Dynamo status node"
    Assert-FileExists (Join-Path $packageRoot "integrations\dynamo\host_smoke_node.py") "packaged Dynamo host-smoke node"
    Assert-FileExists (Join-Path $packageRoot "integrations\dynamo\workflow_examples_node.py") "packaged Dynamo workflow examples node"
    Assert-FileExists (Join-Path $packageRoot "integrations\dynamo\revit_mcp_next_host_smoke.dyn") "packaged Dynamo host-smoke graph"

    $installerScript = Join-Path $packageRoot "installer\install-windows.ps1"
    Assert-ScriptFailsLike $installerScript @(
        "-PackageRoot", $packageRoot,
        "-InstallRoot", (Join-Path $runRoot "unsupported-year-install"),
        "-RevitYears", "2025"
    ) "*Revit 2025 install is not supported yet*" "Unsupported Revit year install gate"

    Invoke-RepoScript $installerScript @(
        "-PackageRoot", $packageRoot,
        "-InstallRoot", $installRoot,
        "-RevitYears", "2024"
    )
    Assert-FileExists (Join-Path $installRoot "integrations\python\revit_mcp_next_client.py") "installed Python integration client"
    Assert-FileExists (Join-Path $installRoot "integrations\python\revit_mcp_next_inprocess.py") "installed Python in-process integration helper"
    Assert-FileExists (Join-Path $installRoot "integrations\python\revit_mcp_next_host_smoke.py") "installed Python host-smoke evidence helper"
    Assert-FileExists (Join-Path $installRoot "integrations\python\revit_mcp_next_workflow_examples.py") "installed Python workflow examples helper"
    Assert-FileExists (Join-Path $installRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Diagnostics.panel\Status.pushbutton\script.py") "installed pyRevit status command"
    Assert-FileExists (Join-Path $installRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Diagnostics.panel\Host Smoke.pushbutton\script.py") "installed pyRevit host-smoke command"
    Assert-FileExists (Join-Path $installRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Examples.panel\Workflow Samples.pushbutton\script.py") "installed pyRevit workflow examples command"
    Assert-FileExists (Join-Path $installRoot "integrations\dynamo\status_node.py") "installed Dynamo status node"
    Assert-FileExists (Join-Path $installRoot "integrations\dynamo\host_smoke_node.py") "installed Dynamo host-smoke node"
    Assert-FileExists (Join-Path $installRoot "integrations\dynamo\workflow_examples_node.py") "installed Dynamo workflow examples node"
    Assert-FileExists (Join-Path $installRoot "integrations\dynamo\revit_mcp_next_host_smoke.dyn") "installed Dynamo host-smoke graph"
    Assert-FileExists (Join-Path $installRoot "config\client-discovery.json") "installed client discovery config"
    Assert-FileExists (Join-Path $packageRoot "scripts\print-mcp-config.ps1") "packaged MCP config printer"
    Assert-FileExists (Join-Path $packageRoot "scripts\doctor-clients.ps1") "packaged client doctor"
    Assert-FileExists (Join-Path $packageRoot "scripts\ensure-revit-addin-trust.ps1") "packaged Revit trust helper"
    Assert-FileExists (Join-Path $packageRoot "scripts\ensure-pyrevit-hosts-cache.ps1") "packaged pyRevit hosts cache helper"
    Assert-FileExists (Join-Path $packageRoot "scripts\run-pyrevit-host-smoke.ps1") "packaged pyRevit host-smoke runner"
    Assert-FileExists (Join-Path $packageRoot "scripts\run-dynamo-host-smoke.ps1") "packaged Dynamo host-smoke runner"
    Assert-FileExists (Join-Path $packageRoot "scripts\run-host-integrations-smoke.ps1") "packaged aggregate hosted integration smoke runner"
    Assert-FileExists (Join-Path $packageRoot "scripts\collect-host-integration-evidence.ps1") "packaged hosted integration evidence composer"
    Assert-PackagedNpmAliases $packageRoot
    Assert-HostedSmokeWrapperDryRuns $packageRoot $installRoot $runRoot

    $addinManifestPath = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2024\RevitMcpNext.addin"
    Assert-FileExists $addinManifestPath "installed Revit add-in manifest"
    $addinManifestText = Get-Content -LiteralPath $addinManifestPath -Raw
    if (-not $addinManifestText.Contains("<ClientId>6F78E70D-BE13-4E0B-9B11-9E28F876AF71</ClientId>")) {
        throw "Installed Revit add-in manifest does not use ClientId."
    }
    if ($addinManifestText.Contains("<AddInId>6F78E70D-BE13-4E0B-9B11-9E28F876AF71</AddInId>")) {
        throw "Installed Revit add-in manifest still uses AddInId."
    }

    $clientDiscovery = Read-JsonFile (Join-Path $installRoot "config\client-discovery.json")
    if ([string] $clientDiscovery.addinClientId -ne "6F78E70D-BE13-4E0B-9B11-9E28F876AF71") {
        throw "Client discovery did not record the Revit add-in ClientId."
    }
    if (-not $clientDiscovery.supportedRevitYears -or [int] $clientDiscovery.supportedRevitYears[0] -ne 2024) {
        throw "Client discovery did not record supported Revit year 2024."
    }
    if (-not $clientDiscovery.addinAssemblyPaths.PSObject.Properties["2024"]) {
        throw "Client discovery did not record a year-specific add-in assembly path."
    }
    foreach ($expectedTool in @("revit.get_model_readiness", "revit.catalog", "revit.preview_change_set", "revit.apply_change_set")) {
        if (@($clientDiscovery.tools) -notcontains $expectedTool) {
            throw "Client discovery did not advertise expected tool: $expectedTool"
        }
    }
    foreach ($expectedOperation in @("place_family_instance", "create_room", "delete_element")) {
        if (@($clientDiscovery.writeOperations) -notcontains $expectedOperation) {
            throw "Client discovery did not advertise expected write operation: $expectedOperation"
        }
    }

    $configOutput = Invoke-RepoScriptCapture (Join-Path $packageRoot "scripts\print-mcp-config.ps1") @(
        "-InstallRoot", $installRoot
    )
    $launcherPath = Join-Path $installRoot "launch-revit-mcp-next.cmd"
    if (-not $configOutput.Contains("claude mcp add --scope user revit-mcp-next")) {
        throw "MCP config printer did not include a Claude Code command."
    }
    if (-not $configOutput.Contains("claude_desktop_config.json")) {
        throw "MCP config printer did not include a Claude Desktop config heading."
    }
    if (-not $configOutput.Contains("[mcp_servers.revit-mcp-next]")) {
        throw "MCP config printer did not include a Codex TOML entry."
    }
    if (-not $configOutput.Contains($launcherPath)) {
        throw "MCP config printer did not include the installed launcher path."
    }

    $doctorScript = Join-Path $packageRoot "scripts\doctor.ps1"
    Invoke-RepoScript $doctorScript @(
        "-InstallRoot", $installRoot,
        "-RevitYear", "2024"
    )

    $supportScript = Join-Path $packageRoot "scripts\collect-support-bundle.ps1"
    Invoke-RepoScript $supportScript @(
        "-InstallRoot", $installRoot,
        "-OutputRoot", $supportRoot,
        "-RevitYears", "2024"
    )
    $supportZip = Get-ChildItem -LiteralPath $supportRoot -Filter "*.zip" -File | Select-Object -First 1
    if (-not $supportZip) {
        throw "Support bundle zip was not created under $supportRoot."
    }

    $authToken = Read-AuthTokenConfig (Join-Path $installRoot "config\auth.env")
    if (-not [string]::IsNullOrWhiteSpace($authToken) -and $configOutput.Contains($authToken)) {
        throw "MCP config printer leaked the raw auth token."
    }
    Assert-NoRawTokenInSupportBundle $supportRoot $authToken

    Assert-TamperedPackageFails $packageRoot $runRoot $installerScript
    Write-Step "Release package contract passed."
} finally {
    $env:APPDATA = $oldAppData
    $env:LOCALAPPDATA = $oldLocalAppData

    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $runRoot -PathType Container)) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    } elseif ($KeepArtifacts) {
        Write-Step "Kept artifacts: $runRoot"
    }
}
