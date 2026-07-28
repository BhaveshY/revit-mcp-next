[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "tests\NamedPipeHostLifecycle\RevitMcpNext.NamedPipeHostLifecycle.csproj"

& dotnet run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Named-pipe host lifecycle regression harness failed with exit code $LASTEXITCODE."
}
