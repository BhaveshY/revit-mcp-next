param(
    [string]$RevitApiPath = "C:\Program Files\Autodesk\Revit 2024"
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "RevitMcpNext.RevitParameterContract.csproj"
$env:PATH = $RevitApiPath + ";" + $env:PATH

dotnet run --project $projectPath -c Release -p:RevitApiPath="$RevitApiPath"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
