[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $OutputRoot = "",
    [string] $AddinOutputRoot = "",
    [switch] $NoZip
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+-rc\.\d+$') {
    throw "Pilot version must use the form 1.2.3-rc.4. Received: $Version"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\pilot"
}
$outputRootFull = [System.IO.Path]::GetFullPath($OutputRoot)
$packageRoot = Join-Path $outputRootFull "revit-mcp-next-$Version-windows"
$zipPath = "$packageRoot.zip"
$packageScript = Join-Path $repoRoot "scripts\package-release.ps1"

$arguments = @(
    "-Version", $Version,
    "-OutputRoot", $outputRootFull,
    "-RevitYears", "2024,2027"
)
if ($NoZip) { $arguments += "-NoZip" }
if (-not [string]::IsNullOrWhiteSpace($AddinOutputRoot)) { $arguments += @("-AddinOutputRoot", $AddinOutputRoot) }

& powershell -NoProfile -ExecutionPolicy Bypass -File $packageScript @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Pilot RC packaging failed with exit code $LASTEXITCODE."
}

$manifestPath = Join-Path $packageRoot "release-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Pilot package manifest is missing: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string] $manifest.package.version -ne $Version -or @($manifest.package.revitYears).Count -ne 2) {
    throw "Pilot package version/year metadata is incorrect."
}
if ($manifest.signing.requested -ne $false -or [string] $manifest.sharing.signingMode -ne "unsigned" -or $manifest.sharing.publicTrust -ne $false) {
    throw "Controlled-pilot packages must be explicitly unsigned and must not claim public trust."
}

$result = [ordered] @{
    status = "created"
    profile = "controlled-pilot-unsigned"
    version = $Version
    packageRoot = $packageRoot
    packageChecksums = (Join-Path $packageRoot "CHECKSUMS.sha256")
    revitYears = @(2024, 2027)
    signed = $false
    publicTrust = $false
}
if (-not $NoZip) {
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "Pilot package zip is missing: $zipPath"
    }
    $zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipChecksumPath = "$zipPath.sha256"
    Set-Content -LiteralPath $zipChecksumPath -Value "$zipSha256  $([System.IO.Path]::GetFileName($zipPath))" -Encoding ASCII
    $result.zipPath = $zipPath
    $result.zipSha256 = $zipSha256
    $result.zipChecksumPath = $zipChecksumPath
}

$result | ConvertTo-Json -Depth 5
