param(
    [Parameter(Mandatory = $true)]
    [Alias("Home")]
    [string] $HomeDir,
    [string[]] $RevitYears = @("2024", "2027"),
    [string] $ArtifactsRoot = "",
    [string] $DefaultHome = "",
    [string] $AddinsRoot = "",
    [switch] $TrustAddin,
    [switch] $EnableTestOps,
    [switch] $NoManifest,
    [switch] $AllowAnyLocation,
    [switch] $Json
)

# Dev install of a built add-in into a runtime home (SPEC section 9.2):
#   1. copies artifacts\addin\<year>\* to <Home>\addin\<year>\<payloadId>\ (payloadId = first 12 hex of sha256 over the
#      sorted "name:sha256" lines of the payload files);
#   2. writes <Home>\addin\<year>\current.json and the home marker;
#   3. ensures <Home>\config\auth.env;
#   4. ensures the loader in the DEFAULT home (<DefaultHome>\addin\<year>\loader\<loaderId>\) and that
#      %APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin points to it.
# Every file is written only when its content differs. Codex/Claude configuration is never touched.
# Start Revit with REVIT_MCP_NEXT_HOME=<Home> to run this payload; without the variable the loader uses the default home.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2

$utf8 = New-Object System.Text.UTF8Encoding($false)
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) { $ArtifactsRoot = Join-Path $repoRoot "artifacts" }
if ([string]::IsNullOrWhiteSpace($DefaultHome)) { $DefaultHome = Join-Path $env:USERPROFILE ".revit-mcp-next" }
if ([string]::IsNullOrWhiteSpace($AddinsRoot)) { $AddinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins" }

$HomeDir = [System.IO.Path]::GetFullPath($HomeDir.Trim().Trim('"')).TrimEnd('\')
$DefaultHome = [System.IO.Path]::GetFullPath($DefaultHome).TrimEnd('\')
$ArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
# "-RevitYears 2024,2027" arrives as one string under powershell -File: split on separators.
$years = @($RevitYears | ForEach-Object { "$_" -split "[,; ]+" } | Where-Object { $_ } | ForEach-Object { [int] $_ } | Sort-Object -Unique)
$template =Join-Path $repoRoot "addin\RevitMcpNext.Addin\RevitMcpNext.addin.template"
$summary = New-Object System.Collections.Generic.List[object]

function Write-Step($Message) {
    if (-not $Json) { Write-Host "[revit-mcp-next dev-install] $Message" }
}

function Assert-HomeLocation($Path) {
    if ($AllowAnyLocation) { return }
    $forbidden = @(
        @{ Name = "LOCALAPPDATA"; Path = $env:LOCALAPPDATA },
        @{ Name = "APPDATA"; Path = $env:APPDATA },
        @{ Name = "TEMP"; Path = [System.IO.Path]::GetTempPath() }
    )
    foreach ($entry in $forbidden) {
        if ([string]::IsNullOrWhiteSpace($entry.Path)) { continue }
        $root = [System.IO.Path]::GetFullPath($entry.Path).TrimEnd('\') + '\'
        if (($Path + '\').StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The home '$Path' is under %$($entry.Name)%, which MSIX-packaged clients may not see. Use %USERPROFILE%\.revit-mcp-next-dev-<id> (or pass -AllowAnyLocation)."
        }
    }
    if ($Path -match '\\(WindowsApps|Packages)\\') {
        throw "The home '$Path' is inside an app package folder. Use %USERPROFILE%\.revit-mcp-next-dev-<id> (or pass -AllowAnyLocation)."
    }
}

function Get-Sha256Hex([byte[]] $Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString("x2") }) -join "") } finally { $sha.Dispose() }
}

function Get-FileSha256($Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativeFiles($Root) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    return @(Get-ChildItem -LiteralPath $rootFull -Recurse -File | ForEach-Object {
        [pscustomobject] @{ Relative = $_.FullName.Substring($rootFull.Length + 1); FullName = $_.FullName }
    } | Sort-Object -Property Relative -CaseSensitive)
}

function Get-PayloadId($Files) {
    $lines = @($Files | ForEach-Object { ($_.Relative -replace '\\', '/') + ":" + (Get-FileSha256 $_.FullName) })
    $joined = [string]::Join("`n", [string[]]$lines)
    return (Get-Sha256Hex ($utf8.GetBytes($joined))).Substring(0, 12)
}

function Write-TextIfDifferent($Path, $Text) {
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $existing = [System.IO.File]::ReadAllText($Path, $utf8)
        if ($existing -ceq $Text) { return $false }
    }
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [System.IO.File]::WriteAllText($temporary, $Text, $utf8)
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        [System.IO.File]::Replace($temporary, $Path, $null, $true)
    } else {
        [System.IO.File]::Move($temporary, $Path)
    }
    return $true
}

function Copy-TreeIfDifferent($Files, $Destination) {
    $copied = 0
    foreach ($file in $Files) {
        $target = Join-Path $Destination $file.Relative
        $targetDir = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
        if ((Test-Path -LiteralPath $target -PathType Leaf) -and ((Get-FileSha256 $target) -eq (Get-FileSha256 $file.FullName))) { continue }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $copied++
    }
    return $copied
}

function Ensure-Marker($Root, $CreatedBy) {
    $marker = Join-Path $Root ".revit-mcp-next-home"
    if (Test-Path -LiteralPath $marker -PathType Leaf) { return $false }
    $body = [ordered] @{ schema = 1; createdAtUtc = [DateTime]::UtcNow.ToString("o"); createdBy = $CreatedBy } | ConvertTo-Json -Compress
    return (Write-TextIfDifferent $marker $body)
}

function Set-PrivateAcl($Path) {
    try {
        $acl = New-Object System.Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        foreach ($sid in @($user, (New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")), (New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")))) {
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($sid, "FullControl", "Allow")
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $Path -AclObject $acl
    } catch {
        Write-Warning "Could not restrict the ACL of ${Path}: $($_.Exception.Message)"
    }
}

function Ensure-AuthFile($Root) {
    $configDir = Join-Path $Root "config"
    $authFile = Join-Path $configDir "auth.env"
    if (Test-Path -LiteralPath $authFile -PathType Leaf) {
        $existing = [System.IO.File]::ReadAllText($authFile, $utf8)
        if ($existing -match '(?m)^\s*REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*"?[A-Za-z0-9_-]{43,}"?\s*$') { return $false }
    }
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $content = "# revit-mcp-next local pipe token. Local only; never share or paste.`r`nAUTH_CONFIG_VERSION=1`r`nREVIT_MCP_NEXT_AUTH_TOKEN=$token`r`n"
    Write-TextIfDifferent $authFile $content | Out-Null
    Set-PrivateAcl $authFile
    return $true
}

function Get-GitShaFromDll($Path) {
    try {
        $product = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
        if ($product -match '\+([0-9A-Za-z]+)$') { return $Matches[1] }
    } catch { }
    return ""
}

Assert-HomeLocation $HomeDir
New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null
$markerWritten = Ensure-Marker $HomeDir "dev-install"
$authWritten = Ensure-AuthFile $HomeDir
Write-Step "home $HomeDir (marker $(if ($markerWritten) { 'written' } else { 'present' }), auth.env $(if ($authWritten) { 'created' } else { 'present' }))"

if ($EnableTestOps) {
    if ([string]::Equals($HomeDir, $DefaultHome, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-EnableTestOps is refused for the default home ($DefaultHome); test ops are e2e-only."
    }
    Write-TextIfDifferent (Join-Path $HomeDir "config\e2e-test-ops.enable") "enabled by dev-install`r`n" | Out-Null
    Write-Step "test ops enabled (config\e2e-test-ops.enable)"
}

foreach ($year in $years) {
    if ($year -ne 2024 -and $year -ne 2027) { throw "Unsupported Revit year $year (2024 and 2027 are supported)." }
    $source = Join-Path $ArtifactsRoot "addin\$year"
    $payloadDll = Join-Path $source "RevitMcpNext.Addin.dll"
    if (-not (Test-Path -LiteralPath $payloadDll -PathType Leaf)) {
        throw "$payloadDll was not found. Build first: scripts/build-addin.ps1 -RevitYear $year"
    }

    # 1-2. payload + current.json
    $files = Get-RelativeFiles $source
    $payloadId = Get-PayloadId $files
    $addinDir = Join-Path $HomeDir "addin\$year"
    $payloadDir = Join-Path $addinDir $payloadId
    $copied = Copy-TreeIfDifferent $files $payloadDir
    $currentJson = Join-Path $addinDir "current.json"
    $currentWritten = $false
    $existingId = $null
    if (Test-Path -LiteralPath $currentJson -PathType Leaf) {
        try { $existingId = ([System.IO.File]::ReadAllText($currentJson, $utf8) | ConvertFrom-Json).payloadId } catch { $existingId = $null }
    }
    if ($existingId -ne $payloadId) {
        $current = [ordered] @{
            payloadId = $payloadId
            gitSha = (Get-GitShaFromDll $payloadDll)
            builtAtUtc = (Get-Item -LiteralPath $payloadDll).LastWriteTimeUtc.ToString("o")
            installedAtUtc = [DateTime]::UtcNow.ToString("o")
            installedBy = "dev-install"
        } | ConvertTo-Json -Compress
        $currentWritten = Write-TextIfDifferent $currentJson $current
    }
    Write-Step "Revit ${year}: payload $payloadId ($copied file(s) copied; current.json $(if ($currentWritten) { 'updated' } else { 'unchanged' }))"

    # 4. loader in the default home + manifest
    $loaderDll = Join-Path $ArtifactsRoot "loader\$year\RevitMcpNext.Loader.dll"
    $loaderPath = $null
    $manifestWritten = $false
    $manifestPath = Join-Path $AddinsRoot "$year\RevitMcpNext.addin"
    if (Test-Path -LiteralPath $loaderDll -PathType Leaf) {
        $loaderId = (Get-FileSha256 $loaderDll).Substring(0, 12)
        $loaderDir = Join-Path $DefaultHome "addin\$year\loader\$loaderId"
        $loaderFiles = @(Get-RelativeFiles (Split-Path -Parent $loaderDll) | Where-Object { $_.Relative -like "RevitMcpNext.Loader.*" })
        New-Item -ItemType Directory -Force -Path $loaderDir | Out-Null
        Copy-TreeIfDifferent $loaderFiles $loaderDir | Out-Null
        Ensure-Marker $DefaultHome "dev-install" | Out-Null
        $loaderPath = Join-Path $loaderDir "RevitMcpNext.Loader.dll"
        if (-not $NoManifest) {
            $manifest = ([System.IO.File]::ReadAllText($template, $utf8)).Replace("{{ASSEMBLY_PATH}}", [System.Security.SecurityElement]::Escape($loaderPath))
            $manifestWritten = Write-TextIfDifferent $manifestPath $manifest
        }
        Write-Step "Revit ${year}: loader $loaderPath; manifest $(if ($NoManifest) { 'skipped' } elseif ($manifestWritten) { 'written' } else { 'unchanged' }) ($manifestPath)"
    } else {
        Write-Warning "Revit ${year}: $loaderDll was not found; the loader and manifest were not installed (build with scripts/build-addin.ps1)."
    }

    $summary.Add([ordered] @{
        revitYear = $year
        home = $HomeDir
        payloadId = $payloadId
        payloadDir = $payloadDir
        filesCopied = $copied
        currentJsonUpdated = $currentWritten
        loader = $loaderPath
        manifest = if ($NoManifest) { $null } else { $manifestPath }
        manifestWritten = $manifestWritten
    }) | Out-Null
}

if ($TrustAddin -and -not $NoManifest) {
    $trustScript = Join-Path $PSScriptRoot "ensure-revit-addin-trust.ps1"
    if (Test-Path -LiteralPath $trustScript -PathType Leaf) {
        & $trustScript -RevitYears ([int[]] $years) | ForEach-Object { Write-Step $_ }
    } else {
        Write-Warning "ensure-revit-addin-trust.ps1 was not found; add-in trust was not seeded."
    }
}

Write-Step "done. Start Revit with REVIT_MCP_NEXT_HOME=$HomeDir to load this payload."
if ($Json) {
    [ordered] @{ home = $HomeDir; defaultHome = $DefaultHome; years = $summary } | ConvertTo-Json -Depth 5 -Compress
}
