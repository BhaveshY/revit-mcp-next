param(
    [switch] $Help,
    [switch] $DryRun,
    [switch] $SkipBuild,
    [switch] $SkipInstall,
    [switch] $NoLaunch,
    [switch] $NoEvidence,
    [switch] $RequireTypeChange,
    [int] $RevitYear = 2024,
    [string] $RevitApiPath = "",
    [string] $RevitExePath = "",
    [string] $ModelPath = "",
    [string] $InstallRoot = "",
    [string] $OutputRoot = "",
    [string] $PackageRoot = "",
    [int] $StartupWaitSeconds = 120
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next local-smoke] $Message"
}

function Show-Help {
    Write-Host @"
Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\local-release-smoke.ps1 [options]

Builds a staged release candidate, installs it into a run-local install root,
copies a disposable RVT model, launches Revit when needed, runs doctor/live
smoke/support/evidence, and writes all artifacts under a short local work root.

Options:
  -RevitYear <year>          Revit major year. Default: 2024.
  -RevitApiPath <path>       Directory containing RevitAPI.dll. Default: Program Files Autodesk Revit <year>.
  -RevitExePath <path>       Revit.exe path. Default: Program Files Autodesk Revit <year>\Revit.exe.
  -ModelPath <path>          Disposable RVT to copy before launch. Default: Dynamo sample RVT for the year.
  -InstallRoot <path>        Install root. Default: current run directory\install.
  -OutputRoot <path>         Evidence root. Default: C:\tmp\revit-mcp-next-smoke when writable.
  -PackageRoot <path>        Existing staged package root when using -SkipBuild.
  -StartupWaitSeconds <n>    Wait after Revit starts before doctor/smoke. Default: 120.
  -SkipBuild                 Reuse an existing staged package under the run root.
  -SkipInstall               Smoke an already installed add-in.
  -NoLaunch                  Require Revit to already be running.
  -NoEvidence                Skip release evidence bundle collection.
  -RequireTypeChange         Require change_element_type smoke coverage.
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
        return Resolve-RequiredFile $Configured "Configured disposable RVT model was not found."
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

function Find-PackageRoot {
    param([string] $PackageOutputRoot, [string] $Version)

    $expected = Join-Path $PackageOutputRoot "revit-mcp-next-$Version-windows"
    return Resolve-RequiredDirectory $expected "Expected package root was not created."
}

if ($Help) {
    Show-Help
    exit 0
}

if ($StartupWaitSeconds -lt 0 -or $StartupWaitSeconds -gt 900) {
    throw "-StartupWaitSeconds must be between 0 and 900."
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

$node = Resolve-NodeCommand
$npm = Resolve-NpmCommand
$revitApi = Resolve-RevitApiPath $RevitYear $RevitApiPath
$revitExe = Resolve-RevitExePath $RevitYear $RevitExePath
$apiDll = Resolve-RequiredFile (Join-Path $revitApi "RevitAPI.dll") "RevitAPI.dll was not found."
$apiUiDll = Resolve-RequiredFile (Join-Path $revitApi "RevitAPIUI.dll") "RevitAPIUI.dll was not found."
$sourceModel = Resolve-SourceModel $RevitYear $ModelPath
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

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $runRoot "install"
}
$installRootFull = Get-FullPath $InstallRoot
$launcherPath = Join-Path $installRootFull "launch-revit-mcp-next.cmd"
$disposableModel = Join-Path $runRoot ("disposable-model\revit-mcp-next-smoke-$timestamp.rvt")

Assert-PathChild $outputRootFull $runRoot "local smoke run root"
Assert-PathChild $runRoot $evidenceDir "evidence directory"
Assert-PathChild $runRoot $packageOutputRoot "package output root"
if ($installRootFull.StartsWith((Add-TrailingSeparator $runRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
    Assert-PathChild $runRoot $installRootFull "install root"
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
    launcherPath = $launcherPath
    packageRoot = $PackageRoot
    outputRoot = $outputRootFull
    runRoot = $runRoot
    skipBuild = [bool] $SkipBuild
    skipInstall = [bool] $SkipInstall
    noLaunch = [bool] $NoLaunch
    noEvidence = [bool] $NoEvidence
    requireTypeChange = [bool] $RequireTypeChange
    dryRun = [bool] $DryRun
}
$runInputs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidenceDir "run-inputs.json") -Encoding UTF8

Write-Step "Run root: $runRoot"
Write-Step "Revit: $revitExe"
Write-Step "Source model: $sourceModel"
Write-Step "Install root: $installRootFull"

$runningAtStart = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
if ($runningAtStart -and -not $SkipInstall) {
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
    Invoke-Logged "Package release candidate" (Join-Path $logsDir "package-release.log") $npm @("run", "package:windows", "--", "-OutputRoot", $packageOutputRoot, "-RevitYears", "$RevitYear")
}

if (-not $DryRun -and -not $SkipBuild -and [string]::IsNullOrWhiteSpace($PackageRoot)) {
    $packageRoot = Find-PackageRoot $packageOutputRoot $version
} elseif (-not $DryRun) {
    $packageRoot = Resolve-RequiredDirectory $packageRoot "Package root was not found."
}

if (-not $SkipInstall) {
    $runningForInstall = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($runningForInstall) {
        throw "Revit is already running as process id $($runningForInstall.Id). Close Revit before installing, or rerun with -SkipInstall to smoke the already installed add-in."
    }

    $installer = Join-Path $packageRoot "installer\install-windows.ps1"
    Invoke-Logged "Install staged package" (Join-Path $logsDir "install-windows.log") "powershell" @(
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
}

Set-RevitAuthEnvironment $installRootFull

$running = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $running) {
    if ($NoLaunch) {
        throw "Revit is not running and -NoLaunch was supplied."
    }

    Copy-DisposableModel $sourceModel $disposableModel
    Write-Step "Launching Revit with disposable model copy."
    if (-not $DryRun) {
        Start-Process -FilePath $revitExe -ArgumentList "`"$disposableModel`""
        $deadline = (Get-Date).AddSeconds([Math]::Max($StartupWaitSeconds, 30))
        do {
            Start-Sleep -Seconds 5
            $running = Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -First 1
        } while (-not $running -and (Get-Date) -lt $deadline)

        if (-not $running) {
            throw "Revit did not start before the wait deadline."
        }

        Write-Step "Revit started as process id $($running.Id). Waiting $StartupWaitSeconds seconds before diagnostics."
        if ($StartupWaitSeconds -gt 0) {
            Start-Sleep -Seconds $StartupWaitSeconds
        }
    }
} else {
    Write-Step "Revit is already running as process id $($running.Id)."
}

Invoke-Logged "Run install doctor" (Join-Path $logsDir "doctor-windows.log") $npm @("run", "doctor:windows", "--", "-InstallRoot", $installRootFull, "-RevitYear", "$RevitYear")

$smokeArgs = @("run", "smoke:revit", "--", "-LauncherPath", $launcherPath)
if ($RequireTypeChange) {
    $smokeArgs += "-RequireTypeChange"
}
Invoke-Logged "Run live Revit smoke" (Join-Path $evidenceDir "smoke-revit.log") $npm $smokeArgs

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

    $evidenceArgs = @(
        "run", "evidence:release:windows", "--",
        "-PackageRoot", $packageRoot,
        "-OutputRoot", $releaseEvidenceRoot,
        "-SigningSkipReason", "No release certificate configured for this local release smoke.",
        "-LiveSmokeEvidencePath", $evidenceDir
    )
    foreach ($optionalLog in @(
        @{ Name = "-ValidateRepoLogPath"; Path = (Join-Path $logsDir "validate-repo.log") },
        @{ Name = "-PackageLogPath"; Path = (Join-Path $logsDir "package-release.log") },
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
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot "run-summary.json") -Encoding UTF8

Write-Step "Local release smoke completed."
Write-Step "Run summary: $(Join-Path $runRoot "run-summary.json")"
