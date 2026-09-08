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
. (Join-Path $PSScriptRoot "../../scripts/resolve-dotnet-sdk.ps1")
$DotnetPath = Resolve-DotnetSdk -DotnetPath $DotnetPath
$env:PATH = $RevitApiPath + ";" + $env:PATH

& $DotnetPath run --project $projectPath -c Release -p:RevitYear=$RevitYear -p:RevitApiPath="$RevitApiPath"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
