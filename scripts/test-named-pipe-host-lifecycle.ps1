[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "tests\NamedPipeHostLifecycle\RevitMcpNext.NamedPipeHostLifecycle.csproj"

& dotnet run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Named-pipe host lifecycle regression harness failed with exit code $LASTEXITCODE."
}

$csharpContracts = Get-Content -Raw (Join-Path $repoRoot "addin\RevitMcpNext.Contracts\BridgeContracts.cs")
$typescriptContracts = Get-Content -Raw (Join-Path $repoRoot "contracts\src\index.ts")
if ($csharpContracts -notmatch 'public const string Version = "([^"]+)"') {
    throw "Could not read the C# bridge protocol version."
}
$csharpProtocolVersion = $Matches[1]
if ($typescriptContracts -notmatch 'export const PROTOCOL_VERSION = "([^"]+)"') {
    throw "Could not read the TypeScript bridge protocol version."
}
$typescriptProtocolVersion = $Matches[1]
if ($csharpProtocolVersion -ne $typescriptProtocolVersion) {
    throw "Bridge protocol versions differ. C#: $csharpProtocolVersion TypeScript: $typescriptProtocolVersion"
}
Write-Host "[pass] C# and TypeScript bridge protocol versions match: $csharpProtocolVersion"
