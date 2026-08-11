[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "tests\InProcessBridgeContract\RevitMcpNext.InProcessBridgeContract.csproj"

& dotnet run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "In-process bridge protocol contract harness failed with exit code $LASTEXITCODE."
}
