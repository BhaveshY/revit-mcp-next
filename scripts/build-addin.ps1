param(
    [string] $Configuration = "Release",
    [ValidateSet(2024, 2027)]
    [int] $RevitYear = 2024,
    [string] $RevitApiPath = "",
    [string] $DotnetPath = "",
    [string] $OutputRoot = ""
)

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
        throw "Revit 2027 reference-conflict preflight failed with exit code $LASTEXITCODE."
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

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "addin\RevitMcpNext.Addin\RevitMcpNext.Addin.csproj"

if ([string]::IsNullOrWhiteSpace($RevitApiPath)) {
    $RevitApiPath = Join-Path $env:ProgramFiles "Autodesk\Revit $RevitYear"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\addin"
}

$targetFramework = switch ($RevitYear) {
    2024 { "net48" }
    2027 { "net10.0-windows" }
}
$yearOutput = Join-Path ([System.IO.Path]::GetFullPath($OutputRoot)) "$RevitYear"

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($dotnet) {
        $DotnetPath = $dotnet.Source
    } else {
        $localDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
        if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
            $DotnetPath = $localDotnet
        }
    }
}

if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "dotnet.exe not found. Install the .NET SDK or pass -DotnetPath."
}

foreach ($apiAssembly in @("RevitAPI.dll", "RevitAPIUI.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $RevitApiPath $apiAssembly) -PathType Leaf)) {
        throw "$apiAssembly was not found at '$RevitApiPath'. Install Revit $RevitYear or pass -RevitApiPath."
    }
}

Write-Host "[revit-mcp-next addin] Building Revit $RevitYear ($targetFramework) -> $yearOutput"

if ($RevitYear -eq 2027) {
    Assert-Revit2027ReferenceConflictPolicy $DotnetPath $project $RevitApiPath $targetFramework
}

$buildArguments = @(
    "build", $project,
    "-c", $Configuration,
    "-p:RevitYear=$RevitYear",
    "-p:RevitApiPath=$RevitApiPath",
    "-p:TargetFramework=$targetFramework",
    "-p:TargetFrameworks=$targetFramework",
    "-o", $yearOutput
)
if ($RevitYear -eq 2027) {
    # The preflight above fails on any MSB3277 conflict outside the exact Autodesk-origin whitelist.
    $buildArguments += "-p:MSBuildWarningsAsMessages=MSB3277"
}

& $DotnetPath @buildArguments

if ($LASTEXITCODE -ne 0) {
    throw "Revit $RevitYear add-in build failed with exit code $LASTEXITCODE."
}
