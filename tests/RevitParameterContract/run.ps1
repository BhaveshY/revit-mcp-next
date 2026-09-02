param(
    [ValidateSet(2021, 2024)]
    [int]$RevitYear = 2024,
    [string]$RevitApiPath = "",
    [string]$DotnetPath = ""
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "RevitMcpNext.RevitParameterContract.csproj"
if ([string]::IsNullOrWhiteSpace($RevitApiPath)) {
    $RevitApiPath = "C:\Program Files\Autodesk\Revit $RevitYear"
}
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($dotnet) { $DotnetPath = $dotnet.Source }
}
if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "dotnet.exe not found. Install the .NET SDK or pass -DotnetPath."
}
$env:PATH = $RevitApiPath + ";" + $env:PATH

& $DotnetPath run --project $projectPath -c Release -p:RevitYear=$RevitYear -p:RevitApiPath="$RevitApiPath"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
