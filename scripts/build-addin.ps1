param(
    [string] $Configuration = "Release",
    [ValidateSet(2024, 2027)]
    [int] $RevitYear = 2024,
    [string] $RevitApiPath = "",
    [string] $DotnetPath = "",
    [string] $OutputRoot = ""
)

$ErrorActionPreference = "Stop"

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

& $DotnetPath build $project `
    -c $Configuration `
    -p:RevitYear=$RevitYear `
    -p:RevitApiPath="$RevitApiPath" `
    -p:TargetFramework=$targetFramework `
    -p:TargetFrameworks=$targetFramework `
    -o $yearOutput

if ($LASTEXITCODE -ne 0) {
    throw "Revit $RevitYear add-in build failed with exit code $LASTEXITCODE."
}
