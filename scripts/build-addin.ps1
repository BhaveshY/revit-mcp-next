param(
    [string] $Configuration = "Release",
    [ValidateSet(2024, 2027)]
    [int] $RevitYear = 2024,
    [string] $RevitApiPath = "",
    [string] $DotnetPath = "",
    [string] $OutputRoot = "",
    [string] $SourceRevisionId = "",
    [switch] $SkipCatalog
)

# Builds the Revit add-in payload, the loader and (when present) the opt-in scripting project for one Revit year.
#   artifacts\addin\<year>\              payload (RevitMcpNext.Addin.dll + dependencies; scripting\ when built)
#   artifacts\loader\<year>\             RevitMcpNext.Loader.dll (the manifest target, installed separately)
# The tool catalog (artifacts\catalog\catalog.json) is regenerated first when node and the gen:catalog script exist, and
# is embedded into the add-in. AssemblyInformationalVersion becomes 0.4.0+<short git sha>.

$ErrorActionPreference = "Stop"

function Assert-Revit2027ReferenceConflictPolicy($DotnetExe, $ProjectPath, $ApiPath, $Framework) {
    $arguments = @(
        "build", $ProjectPath,
        "-t:Rebuild",
        "-p:Configuration=$Configuration",
        "-p:RevitYear=2027",
        "-p:RevitApiPath=$ApiPath",
        "-p:TargetFramework=$Framework",
        "-p:TargetFrameworks=$Framework",
        "-v:normal"
    )
    $output = @(& $DotnetExe @arguments 2>&1 | ForEach-Object { [string] $_ })
    if ($LASTEXITCODE -ne 0) {
        $tail = ($output | Select-Object -Last 40) -join "`n"
        throw "Revit 2027 reference-conflict preflight failed with exit code $LASTEXITCODE.`n$tail"
    }

    $text = $output -join "`n"
    $matches = [regex]::Matches($text, 'Found conflicts between different versions of "([^"]+)"')
    $actual = @($matches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $allowed = @("Microsoft.VisualBasic", "System.Drawing")
    $unexpected = @($actual | Where-Object { $_ -notin $allowed })
    $missing = @($allowed | Where-Object { $_ -notin $actual })
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
        throw "Revit 2027 MSB3277 policy mismatch. Expected only: $($allowed -join ', '). Actual: $($actual -join ', ')."
    }
    if (-not $text.Contains((Join-Path $ApiPath "RevitAPI.dll")) -or -not $text.Contains((Join-Path $ApiPath "RevitAPIUI.dll"))) {
        throw "Revit 2027 MSB3277 preflight did not prove that the allowed conflicts originate from Autodesk RevitAPI/RevitAPIUI references."
    }
    if (-not $text.Contains("Microsoft.VisualBasic, Version=10.1.0.0") -or -not $text.Contains("System.Drawing, Version=10.0.0.0")) {
        throw "Revit 2027 MSB3277 preflight observed unexpected Autodesk dependency versions. Review the warning before building."
    }

    Write-Host "[revit-mcp-next addin] Verified Autodesk-origin Revit 2027 reference conflicts: $($actual -join ', ')."
}

function Invoke-DotnetBuild($DotnetExe, [string[]] $Arguments, [string] $What) {
    & $DotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

function Test-NpmScript($RepoRoot, $Name) {
    $packageJson = Join-Path $RepoRoot "package.json"
    if (-not (Test-Path -LiteralPath $packageJson -PathType Leaf)) { return $false }
    try {
        $package = Get-Content -LiteralPath $packageJson -Raw | ConvertFrom-Json
        return $null -ne $package.scripts -and $null -ne $package.scripts.PSObject.Properties[$Name]
    } catch {
        return $false
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "addin\RevitMcpNext.Addin\RevitMcpNext.Addin.csproj"
$loaderProject = Join-Path $repoRoot "addin\RevitMcpNext.Loader\RevitMcpNext.Loader.csproj"
$scriptingProject = Get-ChildItem -LiteralPath (Join-Path $repoRoot "addin\RevitMcpNext.Scripting") -Filter "*.csproj" -File -ErrorAction SilentlyContinue | Select-Object -First 1
$catalogPath = Join-Path $repoRoot "artifacts\catalog\catalog.json"

if ([string]::IsNullOrWhiteSpace($RevitApiPath)) {
    $RevitApiPath = Join-Path $env:ProgramFiles "Autodesk\Revit $RevitYear"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

$targetFramework = switch ($RevitYear) {
    2024 { "net48" }
    2027 { "net10.0-windows" }
}
$yearOutput = Join-Path $OutputRoot "addin\$RevitYear"
$loaderOutput = Join-Path $OutputRoot "loader\$RevitYear"
$scriptingOutput = Join-Path $yearOutput "scripting"

# 1. Tool catalog (embedded into the add-in).
if (-not $SkipCatalog) {
    $node = Get-Command node -ErrorAction SilentlyContinue
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) { $npm = Get-Command npm -ErrorAction SilentlyContinue }
    if ($node -and $npm -and (Test-NpmScript $repoRoot "gen:catalog")) {
        Write-Host "[revit-mcp-next addin] npm run gen:catalog"
        Push-Location $repoRoot
        try {
            & $npm.Source run gen:catalog
            if ($LASTEXITCODE -ne 0) { throw "npm run gen:catalog failed with exit code $LASTEXITCODE." }
        } finally {
            Pop-Location
        }
    } elseif (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
        Write-Warning "artifacts\catalog\catalog.json is missing and 'npm run gen:catalog' is unavailable; the add-in will report CATALOG_MISSING."
    }
}

# 2. Version stamp: 0.4.0+<short git sha>.
if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        try {
            $sha = (& $git.Source -C $repoRoot rev-parse --short=7 HEAD 2>$null)
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($sha)) { $SourceRevisionId = ([string] $sha).Trim() }
        } catch {
            $SourceRevisionId = ""
        }
    }
}
if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) { $SourceRevisionId = "local" }

. (Join-Path $PSScriptRoot "resolve-dotnet-sdk.ps1")
$DotnetPath = Resolve-DotnetSdk -DotnetPath $DotnetPath

foreach ($apiAssembly in @("RevitAPI.dll", "RevitAPIUI.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $RevitApiPath $apiAssembly) -PathType Leaf)) {
        throw "$apiAssembly was not found at '$RevitApiPath'. Install Revit $RevitYear or pass -RevitApiPath."
    }
}

Write-Host "[revit-mcp-next addin] Building Revit $RevitYear ($targetFramework, 0.4.0+$SourceRevisionId) -> $yearOutput"

if ($RevitYear -eq 2027) {
    Assert-Revit2027ReferenceConflictPolicy $DotnetPath $project $RevitApiPath $targetFramework
}

$common = @(
    "-c", $Configuration,
    "-p:RevitYear=$RevitYear",
    "-p:RevitApiPath=$RevitApiPath",
    "-p:TargetFramework=$targetFramework",
    "-p:TargetFrameworks=$targetFramework",
    "-p:SourceRevisionId=$SourceRevisionId"
)
if ($RevitYear -eq 2027) {
    # The preflight above fails on any MSB3277 conflict outside the exact Autodesk-origin whitelist.
    $common += "-p:MSBuildWarningsAsMessages=MSB3277"
}

# 3. Payload.
if (Test-Path -LiteralPath $scriptingOutput) { Remove-Item -LiteralPath $scriptingOutput -Recurse -Force }
Invoke-DotnetBuild $DotnetPath (@("build", $project) + $common + @("-o", $yearOutput)) "Revit $RevitYear add-in build"

# 4. Loader (the manifest target; it has no dependency on the payload).
if (Test-Path -LiteralPath $loaderProject -PathType Leaf) {
    Write-Host "[revit-mcp-next addin] Building the Revit $RevitYear loader -> $loaderOutput"
    Invoke-DotnetBuild $DotnetPath (@("build", $loaderProject) + $common + @("-o", $loaderOutput)) "Revit $RevitYear loader build"
} else {
    Write-Warning "addin\RevitMcpNext.Loader was not found; the loader was not built."
}

# 5. Opt-in scripting host (Roslyn), copied to <payload>\scripting without RevitMcpNext.Contracts.dll.
if ($scriptingProject) {
    $scriptingBuild = Join-Path $OutputRoot "scripting-build\$RevitYear"
    Write-Host "[revit-mcp-next addin] Building $($scriptingProject.Name) -> $scriptingOutput"
    if (Test-Path -LiteralPath $scriptingBuild) { Remove-Item -LiteralPath $scriptingBuild -Recurse -Force }
    Invoke-DotnetBuild $DotnetPath (@("build", $scriptingProject.FullName) + $common + @("-o", $scriptingBuild)) "Revit $RevitYear scripting build"
    New-Item -ItemType Directory -Force -Path $scriptingOutput | Out-Null
    Get-ChildItem -LiteralPath $scriptingBuild -Recurse -File | Where-Object {
        $_.Name -notlike "RevitMcpNext.Contracts.*"
    } | ForEach-Object {
        $relative = $_.FullName.Substring($scriptingBuild.Length).TrimStart('\')
        $destination = Join-Path $scriptingOutput $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}

if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    Write-Warning "Built without artifacts\catalog\catalog.json: the add-in reports CATALOG_MISSING until it is rebuilt with the catalog."
}
Write-Host "[revit-mcp-next addin] Revit $RevitYear build complete."
