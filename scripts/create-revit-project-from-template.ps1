param(
    [int] $RevitYear = 2024,
    [string] $TemplatePath = "",
    [string] $OutputPath = "",
    [string] $InstallRoot = "",
    [string] $RevitExePath = "",
    [int] $StartupWaitSeconds = 60,
    [int] $BridgeReadyTimeoutSeconds = 300,
    [int] $ActivationWaitSeconds = 120,
    [switch] $NoLaunch,
    [switch] $Overwrite,
    [switch] $Json,
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if ($Json) {
        return
    }

    Write-Host "[revit-mcp-next fixture] $Message"
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Resolve-RequiredFile($Path, $Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Message Missing: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-DefaultTemplate($Year) {
    $candidates = @(
        "C:\ProgramData\Autodesk\RVT $Year\Templates\English\DefaultMetric.rte",
        "C:\ProgramData\Autodesk\RVT $Year\Templates\English\Default-Multi-Discipline_Metric.rte",
        "C:\ProgramData\Autodesk\RVT $Year\Templates\German\BIM_Architektur_und_Ingenieurbau.rte"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "No default Revit template was found for Revit $Year. Pass -TemplatePath with a local .rte."
}

function Resolve-RevitExe($Year, $Configured) {
    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        return Resolve-RequiredFile $Configured "Configured Revit.exe was not found."
    }

    return Resolve-RequiredFile "C:\Program Files\Autodesk\Revit $Year\Revit.exe" "Default Revit.exe was not found."
}

function Resolve-InstallRoot($Year, $Configured) {
    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        return Get-FullPath $Configured
    }

    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        throw "APPDATA is not set; pass -InstallRoot explicitly."
    }

    return Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year\RevitMcpNext"
}

function Resolve-OutputPath($Year, $Configured) {
    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        return Get-FullPath $Configured
    }

    $root = "C:\tmp\revit-mcp-next-fixtures"
    $timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
    return Join-Path $root "revit-mcp-next-$Year-$timestamp.rvt"
}

function Assert-Extension($Path, $Expected, $Label) {
    $extension = [System.IO.Path]::GetExtension($Path)
    if ($extension -ine $Expected) {
        throw "$Label must end with $Expected. Got: $Path"
    }
}

function Start-Revit {
    param([string] $RevitExe, [int] $WaitSeconds)

    if ($DryRun) {
        Write-Step "Would launch Revit: $RevitExe"
        return
    }

    Write-Step "Launching Revit without a model so the bridge can create a project from template."
    Start-Process -FilePath $RevitExe
    if ($WaitSeconds -gt 0) {
        Start-Sleep -Seconds $WaitSeconds
    }
}

function Wait-BridgeReady {
    param([string] $RevitCtl, [int] $TimeoutSeconds, [int] $ExpectedYear)

    if ($TimeoutSeconds -eq 0) {
        Write-Step "Skipping bridge readiness wait."
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt = 0
    do {
        $attempt += 1
        Write-Step "Probe Revit bridge readiness (attempt $attempt)."
        if (-not $DryRun) {
            $output = & $RevitCtl status --json --timeout-ms 10000 2>&1
            $exitCode = $LASTEXITCODE
            if ($exitCode -eq 0) {
                try {
                    $state = ($output | Out-String) | ConvertFrom-Json
                    if ($state.ok -eq $true -and $state.data.connected -eq $true) {
                        if ($ExpectedYear -le 0 -or [string] $state.data.revit.version -eq [string] $ExpectedYear) {
                            Write-Step "Revit bridge is ready."
                            return
                        }
                    }
                } catch {
                    Write-Step "Bridge readiness output was not valid JSON yet."
                }
            }
        }

        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    throw "Revit bridge did not become ready within $TimeoutSeconds seconds. If Revit shows an unsigned add-in or first-run prompt, answer it manually on this disposable test profile and rerun."
}

function Wait-ProjectActive {
    param([string] $RevitCtl, [string] $ExpectedPath, [int] $TimeoutSeconds)

    if ($TimeoutSeconds -le 0) {
        return $false
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $expectedFull = Get-FullPath $ExpectedPath
    do {
        if (-not $DryRun) {
            $output = & $RevitCtl status --json --timeout-ms 60000 2>&1
            $exitCode = $LASTEXITCODE
            if ($exitCode -eq 0) {
                try {
                    $state = ($output | Out-String) | ConvertFrom-Json
                    $activePath = [string] $state.data.activeDocument.path
                    if (-not [string]::IsNullOrWhiteSpace($activePath) -and
                        [string]::Equals((Get-FullPath $activePath), $expectedFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                        Write-Step "Created project is active in Revit."
                        return $true
                    }
                } catch {
                    Write-Step "Active-project status output was not valid JSON yet."
                }
            }
        }

        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    return $false
}

if ($StartupWaitSeconds -lt 0 -or $StartupWaitSeconds -gt 900) {
    throw "-StartupWaitSeconds must be between 0 and 900."
}
if ($BridgeReadyTimeoutSeconds -lt 0 -or $BridgeReadyTimeoutSeconds -gt 1800) {
    throw "-BridgeReadyTimeoutSeconds must be between 0 and 1800."
}
if ($ActivationWaitSeconds -lt 0 -or $ActivationWaitSeconds -gt 900) {
    throw "-ActivationWaitSeconds must be between 0 and 900."
}

$templateFull = if ([string]::IsNullOrWhiteSpace($TemplatePath)) { Resolve-DefaultTemplate $RevitYear } else { Resolve-RequiredFile (Get-FullPath $TemplatePath) "Configured template was not found." }
$outputFull = Resolve-OutputPath $RevitYear $OutputPath
Assert-Extension $templateFull ".rte" "TemplatePath"
Assert-Extension $outputFull ".rvt" "OutputPath"

$installRootFull = Resolve-InstallRoot $RevitYear $InstallRoot
$revitCtl = Resolve-RequiredFile (Join-Path $installRootFull "revitctl.cmd") "Installed revitctl launcher was not found."
$authConfig = Resolve-RequiredFile (Join-Path $installRootFull "config\auth.env") "Installed auth config was not found."
$revitExe = Resolve-RevitExe $RevitYear $RevitExePath

$env:REVIT_MCP_NEXT_AUTH_CONFIG = $authConfig
$env:REVIT_MCP_NEXT_PIPE = "revit-mcp-next"

if ((Test-Path -LiteralPath $outputFull -PathType Leaf) -and -not $Overwrite) {
    throw "Output RVT already exists. Pass -Overwrite only for a known disposable fixture: $outputFull"
}

if (-not $NoLaunch) {
    Start-Revit $revitExe $StartupWaitSeconds
}

Wait-BridgeReady $revitCtl $BridgeReadyTimeoutSeconds $RevitYear
$activated = $false

$payload = [ordered] @{
    templatePath = $templateFull
    outputPath = $outputFull
    overwrite = [bool] $Overwrite
    confirm = $true
}

$payloadPath = Join-Path ([System.IO.Path]::GetTempPath()) "revit-mcp-next-create-project-$PID.json"
try {
    if ($DryRun) {
        Write-Step "Would call revitctl create-project with payload:"
        Write-Step ($payload | ConvertTo-Json -Depth 6)
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputFull) | Out-Null
        Set-Content -LiteralPath $payloadPath -Value ($payload | ConvertTo-Json -Depth 6) -Encoding UTF8
        $resultText = & $revitCtl create-project --payload $payloadPath --confirm --timeout-ms 120000 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "revitctl create-project failed with exit code $LASTEXITCODE. Output: $($resultText | Out-String)"
        }
        $result = ($resultText | Out-String) | ConvertFrom-Json
        if ($result.ok -ne $true) {
            throw "revitctl create-project returned a failed response. Output: $($resultText | Out-String)"
        }
        $activated = $result.data.activated -eq $true
        if (-not $activated) {
            Write-Step "Revit created the project; waiting for the UI to finish activating it."
            $activated = Wait-ProjectActive $revitCtl $outputFull $ActivationWaitSeconds
        }
        if (-not $activated) {
            throw "Revit created the project but did not activate it in the UI. Output: $($resultText | Out-String)"
        }
        if (-not (Test-Path -LiteralPath $outputFull -PathType Leaf)) {
            throw "Revit reported project creation but output RVT was not found: $outputFull"
        }
    }
} finally {
    if (Test-Path -LiteralPath $payloadPath -PathType Leaf) {
        Remove-Item -LiteralPath $payloadPath -Force
    }
}

$summary = [ordered] @{
    status = if ($DryRun) { "planned" } else { "passed" }
    revitYear = $RevitYear
    templatePath = $templateFull
    outputPath = $outputFull
    installRoot = $installRootFull
    revitCtlPath = $revitCtl
    launchedRevit = -not [bool] $NoLaunch
    overwritten = [bool] $Overwrite
    activated = [bool] $activated
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 6
} else {
    Write-Step "Disposable Revit project: $outputFull"
}
