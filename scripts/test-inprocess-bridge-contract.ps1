[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "tests\InProcessBridgeContract\RevitMcpNext.InProcessBridgeContract.csproj"

. (Join-Path $PSScriptRoot "resolve-dotnet-sdk.ps1")
$dotnetSdk = Resolve-DotnetSdk
& $dotnetSdk run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "In-process bridge protocol contract harness failed with exit code $LASTEXITCODE."
}
