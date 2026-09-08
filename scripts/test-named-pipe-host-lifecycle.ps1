[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "tests\NamedPipeHostLifecycle\RevitMcpNext.NamedPipeHostLifecycle.csproj"

. (Join-Path $PSScriptRoot "resolve-dotnet-sdk.ps1")
$dotnetSdk = Resolve-DotnetSdk
& $dotnetSdk run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Named-pipe host lifecycle regression harness failed with exit code $LASTEXITCODE."
}

$csharpContracts = Get-Content -Raw (Join-Path $repoRoot "addin\RevitMcpNext.Contracts\BridgeContracts.cs")
$typescriptContracts = Get-Content -Raw (Join-Path $repoRoot "contracts\src\index.ts")
if ($csharpContracts -notmatch 'public const string Version = "([^"]+)"') {
    throw "Could not read the C# bridge protocol version."
}
$csharpBridgeProtocolVersion = $Matches[1]
if ($typescriptContracts -notmatch 'export const BRIDGE_PROTOCOL_VERSION = "([^"]+)"') {
    throw "Could not read the TypeScript bridge protocol version."
}
$typescriptBridgeProtocolVersion = $Matches[1]
if ($csharpBridgeProtocolVersion -ne $typescriptBridgeProtocolVersion) {
    throw "Bridge protocol versions differ. C#: $csharpBridgeProtocolVersion TypeScript: $typescriptBridgeProtocolVersion"
}
Write-Host "[pass] C# and TypeScript bridge protocol versions match: $csharpBridgeProtocolVersion"
