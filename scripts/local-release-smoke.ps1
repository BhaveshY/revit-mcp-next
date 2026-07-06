param(
    [switch] $Help,
    [switch] $DryRun,
    [switch] $SkipBuild,
    [switch] $SkipInstall,
    [switch] $NoLaunch,
    [switch] $NoEvidence,
    [switch] $RequireTypeChange,
    [switch] $RequireRoomTag,
    [switch] $RequireElementTag,
    [switch] $RequireTags,
    [string] $RoomTagFamilyPath = "",
    [string] $RoomTagFamilySha256 = "",
    [string] $RoomTagTypeId = "",
    [string] $RoomTagTypeNameContains = "",
    [string] $ElementTagFamilyPath = "",
    [string] $ElementTagFamilySha256 = "",
    [string] $ElementTagTypeId = "",
    [string] $ElementTagTypeNameContains = "",
    [int] $RevitYear = 2024,
    [string] $RevitApiPath = "",
    [string] $RevitExePath = "",
    [string] $ModelPath = "",
    [string] $InstallRoot = "",
    [string] $OutputRoot = "",
    [string] $PackageRoot = "",
    [string] $ValidateRepoLogPath = "",
    [string] $PackageLogPath = "",
    [string] $SigningLogPath = "",
    [string] $HostedIntegrationEvidencePath = "",
    [string] $SigningCertificateThumbprint = "$env:REVIT_MCP_NEXT_SIGN_CERT_THUMBPRINT",
    [switch] $SkipLocalDevSigning,
    [switch] $TrustRevitAlwaysLoad,
    [int] $StartupWaitSeconds = 120,
    [int] $BridgeReadyTimeoutSeconds = 300,
    [switch] $SkipSecondStartupProbe
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next local-smoke] $Message"
}

function Show-Help {
    Write-Host @"
Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\local-release-smoke.ps1 [options]

Builds a staged release candidate, installs it into a stable per-year Revit
Addins install root, copies a disposable RVT model, launches Revit when needed,
runs doctor/live smoke/support/evidence, and writes all artifacts under a short
local work root.

By default, local smoke builds try to create or reuse a trusted CurrentUser
self-signed code-signing certificate, sign the package before checksums, and
verify trusted signatures. On disposable machines, -TrustRevitAlwaysLoad can
also seed Revit's per-user add-in trust entry, but it is only supplemental; the
signed trusted certificate path is what avoids the Revit security prompt.

Options:
  -RevitYear <year>          Revit major year. Default: 2024.
  -RevitApiPath <path>       Directory containing RevitAPI.dll. Default: Program Files Autodesk Revit <year>.
  -RevitExePath <path>       Revit.exe path. Default: Program Files Autodesk Revit <year>\Revit.exe.
  -ModelPath <path>          Disposable RVT to copy before launch. Default: Dynamo sample RVT for the year. Templates (.rte) are rejected; create/save a disposable RVT from the template first.
  -InstallRoot <path>        Install root. Default: %APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.
  -OutputRoot <path>         Evidence root. Default: C:\tmp\revit-mcp-next-smoke when writable.
  -PackageRoot <path>        Existing staged package root when using -SkipBuild.
  -ValidateRepoLogPath <path>
                              Existing validate-repo log to include when smoking an existing package.
  -PackageLogPath <path>     Existing package log to include when smoking an existing package.
  -SigningLogPath <path>     Existing signing verification log to include when smoking an existing signed package.
  -HostedIntegrationEvidencePath <path>
                              Existing host-integrations directory containing host-integrations-summary.json.
  -SigningCertificateThumbprint <thumbprint>
                              Existing code-signing certificate thumbprint. Default: env REVIT_MCP_NEXT_SIGN_CERT_THUMBPRINT, otherwise a local dev cert is created.
  -SkipLocalDevSigning       Package unsigned local smoke artifacts. Revit may show its unsigned add-in prompt.
  -TrustRevitAlwaysLoad      Also seed Revit's per-user Always Load trust registry entry for this add-in GUID on this disposable/test machine.
  -StartupWaitSeconds <n>    Wait after Revit starts before doctor/smoke. Default: 120.
  -BridgeReadyTimeoutSeconds <n>
                              Poll revit.status until Revit is reachable. Default: 300. Use 0 to skip.
  -SkipSecondStartupProbe    Do not close and relaunch the Revit session launched by this script for a second status-only no-prompt probe.
  -SkipBuild                 Reuse an existing staged package under the run root.
  -SkipInstall               Smoke an already installed add-in.
  -NoLaunch                  Require Revit to already be running.
  -NoEvidence                Skip release evidence bundle collection.
  -RequireTypeChange         Require change_element_type smoke coverage.
  -RequireRoomTag            Require tag_room smoke coverage.
  -RequireElementTag         Require tag_element smoke coverage.
  -RequireTags               Require both tag_room and tag_element smoke coverage.
  -RoomTagFamilyPath <path>  Vetted local room-tag .rfa to load before required tag preflight.
  -RoomTagFamilySha256 <hex> Optional SHA-256 guard for -RoomTagFamilyPath.
  -RoomTagTypeId <id>        Use a specific loaded room tag FamilySymbol id.
  -RoomTagTypeNameContains <text>
                              Use a loaded room tag FamilySymbol whose name/family contains this text.
  -ElementTagFamilyPath <path>
                              Vetted local wall/multi-category tag .rfa to load before required tag preflight.
  -ElementTagFamilySha256 <hex>
                              Optional SHA-256 guard for -ElementTagFamilyPath.
  -ElementTagTypeId <id>     Use a specific loaded wall or multi-category tag FamilySymbol id.
  -ElementTagTypeNameContains <text>
                              Use a loaded wall or multi-category tag FamilySymbol whose name/family contains this text.
  -DryRun                    Print the planned paths and commands without running them.
"@
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
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

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Resolve-NodeCommand {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) {
        throw "node.exe was not found on PATH. Install Node 24 or open a shell where node.exe is available."
    }

    return $node.Source
}

function Resolve-NpmCommand {
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) {
        throw "npm.cmd was not found on PATH. Install Node 24 or open a shell where npm.cmd is available."
    }

    return $npm.Source
}

function Resolve-LocalSigningCertificate {
    param(
        [string] $RepoRoot,
        [string] $ConfiguredThumbprint
    )

    $normalizedThumbprint = ""
    if (-not [string]::IsNullOrWhiteSpace($ConfiguredThumbprint)) {
        $normalizedThumbprint = $ConfiguredThumbprint.Replace(" ", "")
    }

    if ($SkipLocalDevSigning -or $SkipBuild) {
        return ""
    }

    if (-not [string]::IsNullOrWhiteSpace($normalizedThumbprint)) {
        Write-Step "Using configured signing certificate thumbprint for local smoke package."
        return $normalizedThumbprint
    }

    if ($DryRun) {
        Write-Step "Would create or reuse a trusted CurrentUser local dev signing certificate."
        return "DRY-RUN-LOCAL-DEV-SIGNING-CERT"
    }

    $devCertScript = Resolve-RequiredFile (Join-Path $RepoRoot "scripts\ensure-dev-signing-certificate.ps1") "Local dev signing certificate helper was not found."
    Write-Step "Ensuring trusted CurrentUser local dev signing certificate."
    $jsonText = & powershell -NoProfile -ExecutionPolicy Bypass -File $devCertScript -Trust -AutoApproveRootTrustPrompt -Json
    if ($LASTEXITCODE -ne 0) {
        throw "Local dev signing certificate setup failed with exit code $LASTEXITCODE."
    }

    $state = ($jsonText | Out-String) | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string] $state.thumbprint)) {
        throw "Local dev signing certificate helper did not return a thumbprint."
    }

    if (-not [bool] $state.trusted.rootPresent -or -not [bool] $state.trusted.trustedPublisherPresent) {
        throw "Local dev signing certificate was not trusted in CurrentUser Root and TrustedPublisher stores."
    }

    Write-Step "Using local dev signing certificate: $($state.thumbprint)"
    return [string] $state.thumbprint
}

function Test-DirectoryWritable {
    param([string] $Path)

    try {
        New-Item -ItemType Directory -Force -Path $Path -ErrorAction Stop | Out-Null
        $probe = Join-Path $Path ".write-probe-$([Guid]::NewGuid().ToString("N")).tmp"
        Set-Content -LiteralPath $probe -Value "probe" -Encoding ASCII -ErrorAction Stop
        Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

function Resolve-RevitApiPath {
    param([int] $Year, [string] $Configured)

    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        return Resolve-RequiredDirectory $Configured "Configured Revit API path was not found."
    }

    return Resolve-RequiredDirectory "C:\Program Files\Autodesk\Revit $Year" "Default Revit API path was not found."
}

function Resolve-RevitExePath {
    param([int] $Year, [string] $Configured)

    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        return Resolve-RequiredFile $Configured "Configured Revit.exe was not found."
    }

    return Resolve-RequiredFile "C:\Program Files\Autodesk\Revit $Year\Revit.exe" "Default Revit.exe was not found."
}

function Resolve-SourceModel {
    param([int] $Year, [string] $Configured)

    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        $resolved = Resolve-RequiredFile $Configured "Configured disposable RVT model was not found."
        $extension = [System.IO.Path]::GetExtension($resolved)
        if ($extension -ieq ".rte") {
            throw "Configured -ModelPath points to a Revit template (.rte): $resolved. local-release-smoke copies the source model to a disposable .rvt before launch, and Revit rejects templates renamed as projects. Create and save a disposable .rvt from this template first, then pass that .rvt with -ModelPath."
        }
        if ($extension -ine ".rvt") {
            throw "Configured -ModelPath must point to a disposable .rvt project file, not '$extension': $resolved."
        }
        return $resolved
    }

    $defaultSample = "C:\ProgramData\Autodesk\RVT $Year\Dynamo\samples\Data\DynamoSample_$Year.rvt"
    if (Test-Path -LiteralPath $defaultSample -PathType Leaf) {
        return (Resolve-Path -LiteralPath $defaultSample).Path
    }

    $sampleRoot = "C:\ProgramData\Autodesk\RVT $Year"
    if (Test-Path -LiteralPath $sampleRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $sampleRoot -Recurse -Filter "*.rvt" -File -ErrorAction SilentlyContinue |
            Sort-Object FullName |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    throw "No default RVT sample model was found for Revit $Year. Pass -ModelPath with a disposable project."
}

function Invoke-Logged {
    param(
        [string] $Label,
        [string] $LogPath,
        [string] $Command,
        [string[]] $Arguments
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
    $display = "$Command $($Arguments -join ' ')"
    Write-Step $Label
    Write-Host "  $display"

    if ($DryRun) {
        "DRY RUN: $display" | Set-Content -LiteralPath $LogPath -Encoding UTF8
        return
    }

    "Command: $display" | Set-Content -LiteralPath $LogPath -Encoding UTF8
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command @Arguments 2>&1 | Tee-Object -FilePath $LogPath -Append
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "$Label failed with exit code $exitCode. See $LogPath"
    }
}

function Set-RevitAuthEnvironment {
    param([string] $Root)

    $authConfig = Join-Path $Root "config\auth.env"
    if (-not $DryRun) {
        Resolve-RequiredFile $authConfig "Installed auth config was not found." | Out-Null
    }

    [Environment]::SetEnvironmentVariable("REVIT_MCP_NEXT_AUTH_CONFIG", $authConfig, "Process")
    Write-Step "Configured Revit launch auth environment: REVIT_MCP_NEXT_AUTH_CONFIG=$authConfig"
}

function Copy-DisposableModel {
    param([string] $Source, [string] $Destination)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    if ($DryRun) {
        Write-Step "Would copy disposable model: $Source -> $Destination"
        return
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Start-RevitForSmoke {
    param(
        [string] $RevitExe,
        [string] $Model,
        [int] $WaitSeconds
    )

    Write-Step "Launching Revit with disposable model copy."
    if ($DryRun) {
        Write-Step "Would launch Revit: $RevitExe `"$Model`""
        return $null
    }

    Start-Process -FilePath $RevitExe -ArgumentList "`"$Model`""
    $deadline = (Get-Date).AddSeconds([Math]::Max($WaitSeconds, 30))
    $runningProcess = $null
    do {
        Start-Sleep -Seconds 5
        $runningProcess = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
    } while (-not $runningProcess -and (Get-Date) -lt $deadline)

    if (-not $runningProcess) {
        throw "Revit did not start before the wait deadline."
    }

    Write-Step "Revit started as process id $($runningProcess.Id). Waiting $WaitSeconds seconds before diagnostics."
    if ($WaitSeconds -gt 0) {
        Start-Sleep -Seconds $WaitSeconds
    }

    return $runningProcess
}

function Stop-RevitForSecondStartupProbe {
    param([int] $ProcessId)

    if ($DryRun) {
        Write-Step "Would close Revit before second-startup probe."
        return
    }

    $process = if ($ProcessId -gt 0) {
        Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    } else {
        $null
    }
    if (-not $process) {
        $process = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $process) {
        Write-Step "Revit is already closed before second-startup probe."
        return
    }

    Write-Step "Closing Revit process $($process.Id) before second-startup probe."
    try {
        $null = $process.CloseMainWindow()
    } catch {
        Write-Step "CloseMainWindow failed; will force close if needed. $($_.Exception.Message)"
    }

    try {
        if (-not $process.WaitForExit(30000)) {
            Write-Step "Revit did not close within 30 seconds; forcing process stop for disposable smoke machine."
            Stop-Process -Id $process.Id -Force
            Start-Sleep -Seconds 3
        }
    } catch {
        Write-Step "Revit process close check ended with: $($_.Exception.Message)"
    }
}

function Find-PackageRoot {
    param([string] $PackageOutputRoot, [string] $Version)

    $expected = Join-Path $PackageOutputRoot "revit-mcp-next-$Version-windows"
    return Resolve-RequiredDirectory $expected "Expected package root was not created."
}

function Invoke-BridgeReadinessProbe {
    param(
        [string] $LogPath,
        [string] $Launcher,
        [int] $TimeoutSeconds,
        [int] $ExpectedYear
    )

    if ($TimeoutSeconds -eq 0) {
        Write-Step "Skipping Revit bridge readiness probe."
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
    $probeArgs = @("scripts\live-smoke-revit.mjs", "--launcher-path", $Launcher, "--status-only")
    if ($ExpectedYear -gt 0) {
        $probeArgs += @("--expected-revit-year", "$ExpectedYear")
    }
    $display = "$node $($probeArgs -join ' ')"
    "Command: $display" | Set-Content -LiteralPath $LogPath -Encoding UTF8
    Write-Step "Waiting up to $TimeoutSeconds seconds for the Revit bridge to become ready."
    Write-Step "If Revit shows an unsigned add-in security prompt, this package was unsigned or the signing certificate was not trusted."

    if ($DryRun) {
        "DRY RUN: $display" | Add-Content -LiteralPath $LogPath -Encoding UTF8
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt = 0
    do {
        $attempt += 1
        $now = Get-Date
        $header = "----- readiness attempt $attempt at $($now.ToString("o")) -----"
        $header | Add-Content -LiteralPath $LogPath -Encoding UTF8
        Write-Step "Probe Revit bridge readiness (attempt $attempt)."

        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $stdoutPath = Join-Path (Split-Path -Parent $LogPath) "bridge-readiness-stdout-$PID.tmp"
        $stderrPath = Join-Path (Split-Path -Parent $LogPath) "bridge-readiness-stderr-$PID.tmp"
        try {
            & $node @probeArgs > $stdoutPath 2> $stderrPath
            $exitCode = $LASTEXITCODE
            $stdoutText = ""
            $stderrText = ""
            if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
                $stdoutText = Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
                $stderrText = Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue
            }
            if (-not [string]::IsNullOrWhiteSpace($stdoutText)) {
                Add-Content -LiteralPath $LogPath -Value $stdoutText -Encoding UTF8
            }
            if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
                Add-Content -LiteralPath $LogPath -Value $stderrText -Encoding UTF8
            }
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
            Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
        }

        if ($exitCode -eq 0) {
            if (-not [string]::IsNullOrWhiteSpace($stdoutText)) {
                Write-Host $stdoutText.Trim()
            }
            Write-Step "Revit bridge is ready."
            return
        }

        Write-Step "Revit bridge is not ready yet (exit code $exitCode)."

        $runningProbe = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $runningProbe) {
            throw "Revit exited before the bridge became ready. See $LogPath"
        }

        if ((Get-Date) -ge $deadline) {
            break
        }

        Start-Sleep -Seconds 10
    } while ($true)

    throw "Revit bridge did not become ready within $TimeoutSeconds seconds. If Revit is waiting on an add-in security prompt, verify the package was signed and the signing certificate is trusted. See $LogPath"
}

if ($Help) {
    Show-Help
    exit 0
}

if ($StartupWaitSeconds -lt 0 -or $StartupWaitSeconds -gt 900) {
    throw "-StartupWaitSeconds must be between 0 and 900."
}
if ($BridgeReadyTimeoutSeconds -lt 0 -or $BridgeReadyTimeoutSeconds -gt 1800) {
    throw "-BridgeReadyTimeoutSeconds must be between 0 and 1800."
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

$node = Resolve-NodeCommand
$npm = Resolve-NpmCommand
$localSigningCertificateThumbprint = Resolve-LocalSigningCertificate $repoRoot $SigningCertificateThumbprint
$localDevSigningEnabled = -not [string]::IsNullOrWhiteSpace($localSigningCertificateThumbprint)
$revitApi = Resolve-RevitApiPath $RevitYear $RevitApiPath
$revitExe = Resolve-RevitExePath $RevitYear $RevitExePath
$apiDll = Resolve-RequiredFile (Join-Path $revitApi "RevitAPI.dll") "RevitAPI.dll was not found."
$apiUiDll = Resolve-RequiredFile (Join-Path $revitApi "RevitAPIUI.dll") "RevitAPIUI.dll was not found."
$sourceModel = Resolve-SourceModel $RevitYear $ModelPath
if (($RequireTags -or $RequireRoomTag -or $RequireElementTag) -and [string]::IsNullOrWhiteSpace($ModelPath)) {
    Write-Step "Required tag coverage requested without -ModelPath. The default sample RVT is not a curated tag fixture; pass a disposable model with printable plan-backed levels and loaded or loadable tag families for release-candidate evidence."
}
$version = (Read-JsonFile (Join-Path $repoRoot "package.json")).version

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $shortRoot = "C:\tmp"
    if (Test-DirectoryWritable $shortRoot) {
        $OutputRoot = Join-Path $shortRoot "revit-mcp-next-smoke"
    } else {
        $OutputRoot = Join-Path (Split-Path -Parent $repoRoot) "revit-mcp-next-smoke"
    }
}
$outputRootFull = Get-FullPath $OutputRoot
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$runRoot = Join-Path $outputRootFull "$timestamp-$runId"
$evidenceDir = Join-Path $runRoot "evidence"
$logsDir = Join-Path $evidenceDir "logs"
$packageOutputRoot = Join-Path $runRoot "pkg"
$supportRoot = Join-Path $runRoot "support"
$releaseEvidenceRoot = Join-Path $runRoot "rel-evidence"

$defaultInstallRoot = [string]::IsNullOrWhiteSpace($InstallRoot)
if ($defaultInstallRoot) {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        throw "APPDATA is not set; pass -InstallRoot explicitly."
    }

    $InstallRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\RevitMcpNext"
}
$installRootFull = Get-FullPath $InstallRoot
$launcherPath = Join-Path $installRootFull "launch-revit-mcp-next.cmd"
$disposableModel = Join-Path $runRoot ("disposable-model\revit-mcp-next-smoke-$timestamp.rvt")
$runSigningCertificateThumbprint = $null
if ($localDevSigningEnabled) {
    $runSigningCertificateThumbprint = $localSigningCertificateThumbprint
}

Assert-PathChild $outputRootFull $runRoot "local smoke run root"
Assert-PathChild $runRoot $evidenceDir "evidence directory"
Assert-PathChild $runRoot $packageOutputRoot "package output root"
if ($installRootFull.StartsWith((Add-TrailingSeparator $runRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
    Assert-PathChild $runRoot $installRootFull "install root"
} elseif ($defaultInstallRoot) {
    $defaultInstallParent = Get-FullPath (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear")
    Assert-PathChild $defaultInstallParent $installRootFull "install root"
}

New-Item -ItemType Directory -Force -Path $evidenceDir, $logsDir | Out-Null

$runInputs = [ordered] @{
    revitYear = $RevitYear
    revitApiPath = $revitApi
    revitApiDll = $apiDll
    revitApiUiDll = $apiUiDll
    revitExePath = $revitExe
    sourceModelPath = $sourceModel
    disposableModelPath = $disposableModel
    installRoot = $installRootFull
    defaultInstallRoot = $defaultInstallRoot
    launcherPath = $launcherPath
    packageRoot = $PackageRoot
    validateRepoLogPath = $ValidateRepoLogPath
    packageLogPath = $PackageLogPath
    signingLogPath = $SigningLogPath
    outputRoot = $outputRootFull
    runRoot = $runRoot
    skipBuild = [bool] $SkipBuild
    skipInstall = [bool] $SkipInstall
    noLaunch = [bool] $NoLaunch
    noEvidence = [bool] $NoEvidence
    requireTypeChange = [bool] $RequireTypeChange
    requireRoomTag = [bool] $RequireRoomTag
    requireElementTag = [bool] $RequireElementTag
    requireTags = [bool] $RequireTags
    roomTagFamilyPath = $RoomTagFamilyPath
    roomTagFamilySha256 = $RoomTagFamilySha256
    roomTagTypeId = $RoomTagTypeId
    roomTagTypeNameContains = $RoomTagTypeNameContains
    elementTagFamilyPath = $ElementTagFamilyPath
    elementTagFamilySha256 = $ElementTagFamilySha256
    elementTagTypeId = $ElementTagTypeId
    elementTagTypeNameContains = $ElementTagTypeNameContains
    trustRevitAlwaysLoad = [bool] $TrustRevitAlwaysLoad
    localDevSigningEnabled = [bool] $localDevSigningEnabled
    signingCertificateThumbprint = $runSigningCertificateThumbprint
    bridgeReadyTimeoutSeconds = $BridgeReadyTimeoutSeconds
    skipSecondStartupProbe = [bool] $SkipSecondStartupProbe
    dryRun = [bool] $DryRun
}
$runInputs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidenceDir "run-inputs.json") -Encoding UTF8

Write-Step "Run root: $runRoot"
Write-Step "Revit: $revitExe"
Write-Step "Source model: $sourceModel"
Write-Step "Install root: $installRootFull"

$runningAtStart = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $DryRun -and $runningAtStart -and -not $SkipInstall) {
    throw "Revit is already running as process id $($runningAtStart.Id). Close Revit before installing a fresh package, or rerun with -SkipInstall to smoke the already installed add-in."
}

if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) {
    $packageRoot = Get-FullPath $PackageRoot
} elseif ($SkipBuild) {
    $defaultPackageRoot = Join-Path $repoRoot "artifacts\release\revit-mcp-next-$version-windows"
    if (Test-Path -LiteralPath $defaultPackageRoot -PathType Container) {
        $packageRoot = (Resolve-Path -LiteralPath $defaultPackageRoot).Path
    } else {
        throw "-SkipBuild requires -PackageRoot or an existing package at $defaultPackageRoot."
    }
} else {
    $packageRoot = Join-Path $packageOutputRoot "revit-mcp-next-$version-windows"
}

if (-not $SkipBuild) {
    Invoke-Logged "Build broker/contracts" (Join-Path $logsDir "npm-build.log") $npm @("run", "build")
    Invoke-Logged "Build Revit add-in" (Join-Path $logsDir "build-addin.log") $npm @("run", "build:addin", "--", "-RevitApiPath", $revitApi)
    Invoke-Logged "Validate repository" (Join-Path $logsDir "validate-repo.log") $node @("scripts\validate-repo.mjs")
    $packageArgs = @("run", "package:windows", "--", "-OutputRoot", $packageOutputRoot, "-RevitYears", "$RevitYear")
    if ($localDevSigningEnabled) {
        $packageArgs += @(
            "-Sign",
            "-RequireSigned",
            "-RequireTrustedSignatures",
            "-SigningCertificateThumbprint",
            $localSigningCertificateThumbprint,
            "-NoTimestamp"
        )
    }

    Invoke-Logged "Package release candidate" (Join-Path $logsDir "package-release.log") $npm $packageArgs
}

if (-not $DryRun -and -not $SkipBuild -and [string]::IsNullOrWhiteSpace($PackageRoot)) {
    $packageRoot = Find-PackageRoot $packageOutputRoot $version
} elseif (-not $DryRun) {
    $packageRoot = Resolve-RequiredDirectory $packageRoot "Package root was not found."
}

if (-not $SkipInstall) {
    $runningForInstall = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $DryRun -and $runningForInstall) {
        throw "Revit is already running as process id $($runningForInstall.Id). Close Revit before installing, or rerun with -SkipInstall to smoke the already installed add-in."
    }

    $installer = Join-Path $packageRoot "installer\install-windows.ps1"
    $installerArgs = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $installer,
        "-PackageRoot",
        $packageRoot,
        "-RevitYears",
        "$RevitYear",
        "-InstallRoot",
        $installRootFull
    )
    if ($TrustRevitAlwaysLoad) {
        $installerArgs += "-TrustRevitAlwaysLoad"
    }

    Invoke-Logged "Install staged package" (Join-Path $logsDir "install-windows.log") "powershell" $installerArgs
} elseif ($TrustRevitAlwaysLoad) {
    $trustScript = Resolve-RequiredFile (Join-Path $repoRoot "scripts\ensure-revit-addin-trust.ps1") "Revit trust helper was not found."
    Invoke-Logged "Seed Revit Always Load trust" (Join-Path $logsDir "revit-always-load-trust.log") "powershell" @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $trustScript,
        "-RevitYears",
        "$RevitYear"
    )
}

Set-RevitAuthEnvironment $installRootFull

$launchedRevitForSmoke = $false
$launchedRevitProcessId = 0
$secondStartupProbeStatus = "skipped"
$secondStartupProbeReason = ""

$running = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $running) {
    if ($NoLaunch) {
        throw "Revit is not running and -NoLaunch was supplied."
    }

    Copy-DisposableModel $sourceModel $disposableModel
    $running = Start-RevitForSmoke $revitExe $disposableModel $StartupWaitSeconds
    $launchedRevitForSmoke = $true
    if ($running) {
        $launchedRevitProcessId = [int] $running.Id
    }
} else {
    Write-Step "Revit is already running as process id $($running.Id)."
    $secondStartupProbeReason = "Revit was already running before local smoke, so the script did not own the process lifecycle."
}

Invoke-Logged "Run install doctor" (Join-Path $logsDir "doctor-windows.log") $npm @("run", "doctor:windows", "--", "-InstallRoot", $installRootFull, "-RevitYear", "$RevitYear")
Invoke-BridgeReadinessProbe (Join-Path $evidenceDir "bridge-readiness.log") $launcherPath $BridgeReadyTimeoutSeconds $RevitYear

$smokeArgs = @(
    "run", "smoke:revit", "--",
    "-LauncherPath", $launcherPath,
    "-ExpectedRevitYear", "$RevitYear",
    "-SummaryPath", (Join-Path $evidenceDir "smoke-summary.json")
)
if ($RequireTypeChange) {
    $smokeArgs += "-RequireTypeChange"
}
if ($RequireTags) {
    $smokeArgs += "-RequireTags"
} else {
    if ($RequireRoomTag) {
        $smokeArgs += "-RequireRoomTag"
    }

    if ($RequireElementTag) {
        $smokeArgs += "-RequireElementTag"
    }
}
if (-not [string]::IsNullOrWhiteSpace($RoomTagFamilyPath)) {
    $smokeArgs += @("-RoomTagFamilyPath", $RoomTagFamilyPath)
}
if (-not [string]::IsNullOrWhiteSpace($RoomTagFamilySha256)) {
    $smokeArgs += @("-RoomTagFamilySha256", $RoomTagFamilySha256)
}
if (-not [string]::IsNullOrWhiteSpace($RoomTagTypeId)) {
    $smokeArgs += @("-RoomTagTypeId", $RoomTagTypeId)
}
if (-not [string]::IsNullOrWhiteSpace($RoomTagTypeNameContains)) {
    $smokeArgs += @("-RoomTagTypeNameContains", $RoomTagTypeNameContains)
}
if (-not [string]::IsNullOrWhiteSpace($ElementTagTypeId)) {
    $smokeArgs += @("-ElementTagTypeId", $ElementTagTypeId)
}
if (-not [string]::IsNullOrWhiteSpace($ElementTagFamilyPath)) {
    $smokeArgs += @("-ElementTagFamilyPath", $ElementTagFamilyPath)
}
if (-not [string]::IsNullOrWhiteSpace($ElementTagFamilySha256)) {
    $smokeArgs += @("-ElementTagFamilySha256", $ElementTagFamilySha256)
}
if (-not [string]::IsNullOrWhiteSpace($ElementTagTypeNameContains)) {
    $smokeArgs += @("-ElementTagTypeNameContains", $ElementTagTypeNameContains)
}
Invoke-Logged "Run live Revit smoke" (Join-Path $evidenceDir "smoke-revit.log") $npm $smokeArgs

if ($SkipSecondStartupProbe) {
    $secondStartupProbeReason = "Skipped by -SkipSecondStartupProbe."
} elseif ($BridgeReadyTimeoutSeconds -eq 0) {
    $secondStartupProbeReason = "Skipped because -BridgeReadyTimeoutSeconds is 0."
} elseif ($NoLaunch) {
    $secondStartupProbeReason = "Skipped because -NoLaunch requires an externally managed Revit session."
} elseif (-not $launchedRevitForSmoke) {
    if ([string]::IsNullOrWhiteSpace($secondStartupProbeReason)) {
        $secondStartupProbeReason = "Skipped because Revit was not launched by this script."
    }
} else {
    $secondStartupLog = Join-Path $evidenceDir "second-startup-readiness.log"
    Stop-RevitForSecondStartupProbe $launchedRevitProcessId
    Copy-DisposableModel $sourceModel $disposableModel
    $running = Start-RevitForSmoke $revitExe $disposableModel $StartupWaitSeconds
    if ($running) {
        $launchedRevitProcessId = [int] $running.Id
    }

    Invoke-BridgeReadinessProbe $secondStartupLog $launcherPath $BridgeReadyTimeoutSeconds $RevitYear
    $secondStartupProbeStatus = if ($DryRun) { "planned" } else { "passed" }
}
if ($secondStartupProbeStatus -ne "passed" -and -not [string]::IsNullOrWhiteSpace($secondStartupProbeReason)) {
    Write-Step "Second-startup no-prompt probe skipped: $secondStartupProbeReason"
}

Invoke-Logged "Collect support bundle" (Join-Path $logsDir "support-bundle.log") $npm @("run", "support:bundle", "--", "-RevitYears", "$RevitYear", "-InstallRoot", $installRootFull, "-OutputRoot", $supportRoot)

if (-not $NoEvidence) {
    $supportZip = $null
    if (-not $DryRun) {
        $supportZip = Get-ChildItem -LiteralPath $supportRoot -Filter "*.zip" -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if (-not $supportZip) {
            throw "Support bundle zip was not found under $supportRoot."
        }
    }

    $signingRequestedForEvidence = $false
    $releaseManifest = $null
    $releaseManifestPath = Join-Path $packageRoot "release-manifest.json"
    if (-not $DryRun -and (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf)) {
        $releaseManifest = Read-JsonFile $releaseManifestPath
        $signingRequestedForEvidence = [bool] $releaseManifest.signing.requested
    } elseif ($localDevSigningEnabled) {
        $signingRequestedForEvidence = $true
    }

    $evidenceArgs = @(
        "run", "evidence:release:windows", "--",
        "-PackageRoot", $packageRoot,
        "-OutputRoot", $releaseEvidenceRoot,
        "-LiveSmokeEvidencePath", $evidenceDir
    )
    if ($signingRequestedForEvidence) {
        $signingEvidenceLogPath = $SigningLogPath
        if ([string]::IsNullOrWhiteSpace($signingEvidenceLogPath)) {
            $builtPackageLogPath = Join-Path $logsDir "package-release.log"
            if (Test-Path -LiteralPath $builtPackageLogPath -PathType Leaf) {
                $signingEvidenceLogPath = $builtPackageLogPath
            } else {
                $verifyLogPath = Join-Path $logsDir "signing-verify.log"
                $verifyArgs = @("run", "sign:windows", "--", "-PackageRoot", $packageRoot, "-VerifyOnly", "-RequireSigned")
                if (-not $DryRun -and $releaseManifest -and [bool] $releaseManifest.signing.requireTrusted) {
                    $verifyArgs += "-RequireTrusted"
                }

                Invoke-Logged "Verify existing package signatures" $verifyLogPath $npm $verifyArgs
                $signingEvidenceLogPath = $verifyLogPath
            }
        }

        if (-not $DryRun -and -not (Test-Path -LiteralPath $signingEvidenceLogPath -PathType Leaf)) {
            throw "Signing was requested, but signing evidence log was not found: $signingEvidenceLogPath"
        }

        $evidenceArgs += @("-SigningLogPath", $signingEvidenceLogPath)
    } else {
        $evidenceArgs += @("-SigningSkipReason", "No release certificate configured for this local release smoke.")
    }

    $validateRepoEvidenceLogPath = $ValidateRepoLogPath
    if ([string]::IsNullOrWhiteSpace($validateRepoEvidenceLogPath)) {
        $validateRepoEvidenceLogPath = Join-Path $logsDir "validate-repo.log"
    }
    $packageEvidenceLogPath = $PackageLogPath
    if ([string]::IsNullOrWhiteSpace($packageEvidenceLogPath)) {
        $packageEvidenceLogPath = Join-Path $logsDir "package-release.log"
    }

    foreach ($optionalLog in @(
        @{ Name = "-ValidateRepoLogPath"; Path = $validateRepoEvidenceLogPath },
        @{ Name = "-PackageLogPath"; Path = $packageEvidenceLogPath },
        @{ Name = "-DoctorLogPath"; Path = (Join-Path $logsDir "doctor-windows.log") }
    )) {
        if (Test-Path -LiteralPath $optionalLog.Path -PathType Leaf) {
            $evidenceArgs += @($optionalLog.Name, $optionalLog.Path)
        }
    }
    if ($DryRun) {
        $evidenceArgs += @("-SupportBundleSkipReason", "Dry run did not collect a support bundle.")
    } else {
        $evidenceArgs += @("-SupportBundlePath", $supportZip.FullName)
    }

    $hostedIntegrationEvidenceCandidate = $HostedIntegrationEvidencePath
    if ([string]::IsNullOrWhiteSpace($hostedIntegrationEvidenceCandidate)) {
        $hostedIntegrationEvidenceCandidate = Join-Path $runRoot "host-integrations"
    }

    $hostedIntegrationSummaryPath = Join-Path $hostedIntegrationEvidenceCandidate "host-integrations-summary.json"
    if (-not [string]::IsNullOrWhiteSpace($HostedIntegrationEvidencePath) -or (Test-Path -LiteralPath $hostedIntegrationSummaryPath -PathType Leaf)) {
        $hostedIntegrationEvidenceFull = Get-FullPath $hostedIntegrationEvidenceCandidate
        if (-not $DryRun -and -not (Test-Path -LiteralPath (Join-Path $hostedIntegrationEvidenceFull "host-integrations-summary.json") -PathType Leaf)) {
            throw "Hosted integration evidence summary was not found: $(Join-Path $hostedIntegrationEvidenceFull "host-integrations-summary.json")"
        }

        $evidenceArgs += @("-HostedIntegrationEvidencePath", $hostedIntegrationEvidenceFull)
        $hostCommandLogPaths = @()
        foreach ($hostLog in @(
            (Join-Path $hostedIntegrationEvidenceFull "logs\pyrevit-host-smoke.log"),
            (Join-Path $hostedIntegrationEvidenceFull "logs\dynamo-host-smoke.log"),
            (Join-Path $hostedIntegrationEvidenceFull "logs\host-integrations-evidence.log")
        )) {
            if ($DryRun -or (Test-Path -LiteralPath $hostLog -PathType Leaf)) {
                $hostCommandLogPaths += $hostLog
            }
        }
        if ($hostCommandLogPaths.Count -gt 0) {
            $evidenceArgs += @("-CommandLogPaths") + $hostCommandLogPaths
        }
    } else {
        $evidenceArgs += @("-HostedIntegrationSkipReason", "Hosted pyRevit/Dynamo smoke was not run by this MCP launcher smoke. Run npm run smoke:host-integrations and pass -HostedIntegrationEvidencePath for production release evidence.")
    }

    Invoke-Logged "Collect release evidence" (Join-Path $logsDir "release-evidence.log") $npm $evidenceArgs
}

$summary = [ordered] @{
    status = "completed"
    runRoot = $runRoot
    evidenceDir = $evidenceDir
    packageRoot = $packageRoot
    installRoot = $installRootFull
    launcherPath = $launcherPath
    disposableModelPath = $disposableModel
    releaseEvidenceRoot = $releaseEvidenceRoot
    secondStartupProbe = [ordered] @{
        status = $secondStartupProbeStatus
        reason = $secondStartupProbeReason
        logPath = Join-Path $evidenceDir "second-startup-readiness.log"
    }
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot "run-summary.json") -Encoding UTF8

Write-Step "Local release smoke completed."
Write-Step "Run summary: $(Join-Path $runRoot "run-summary.json")"
