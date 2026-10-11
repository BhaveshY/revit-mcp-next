# Rmn.psm1 - shared helpers of installer\install.ps1 and installer\uninstall.ps1 (SPEC sections 3, 9.2 and 12).
# Windows PowerShell 5.1 compatible. Files are written UTF-8 without BOM and only when their content differs.

Set-StrictMode -Version 2

$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:ClientId = "6F78E70D-BE13-4E0B-9B11-9E28F876AF71"
$script:SupportedYears = @(2024, 2027)
$script:ManifestTemplate = @'
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <!-- Written by the revit-mcp-next installer. Points at the loader (SPEC section 9.2); the loader loads the payload of its runtime home. Same ClientId as 0.3.x so Revit's add-in trust carries over. -->
  <AddIn Type="Application">
    <Name>Revit MCP Next</Name>
    <Assembly>{{ASSEMBLY_PATH}}</Assembly>
    <ClientId>6F78E70D-BE13-4E0B-9B11-9E28F876AF71</ClientId>
    <FullClassName>RevitMcpNext.Loader.LoaderApplication</FullClassName>
    <VendorId>BHVY</VendorId>
    <VendorDescription>BhaveshY Revit MCP Next</VendorDescription>
  </AddIn>
</RevitAddIns>
'@

function Get-RmnClientId { return $script:ClientId }

# Optional property of a ConvertFrom-Json object ($null when absent; StrictMode-safe).
function Get-RmnProp($Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}
function Get-RmnSupportedYears { return $script:SupportedYears }

# ------------------------------------------------------------------------------------------------ packaged (MSIX) process

if (-not ("RmnNative.AppModel" -as [type])) {
    Add-Type -Namespace RmnNative -Name AppModel -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder packageFullName);
'@
}

# Returns the package full name when this process runs with an app package identity (MSIX), else $null.
function Get-RmnPackageIdentity {
    try {
        $length = 0
        $rc = [RmnNative.AppModel]::GetCurrentPackageFullName([ref] $length, $null)
        if ($rc -eq 15700) { return $null }  # APPMODEL_ERROR_NO_PACKAGE
        $buffer = New-Object System.Text.StringBuilder ($length + 1)
        $rc = [RmnNative.AppModel]::GetCurrentPackageFullName([ref] $length, $buffer)
        if ($rc -eq 0) { return $buffer.ToString() }
    } catch { }
    return $null
}

# Starts a process through WMI (Win32_Process.Create): the new process is not part of the caller's job or app package.
function Start-RmnOutsideProcess([string] $CommandLine, [string] $WorkingDirectory) {
    $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $CommandLine; CurrentDirectory = $WorkingDirectory }
    if ($result.ReturnValue -ne 0) { throw "Win32_Process.Create failed with code $($result.ReturnValue)." }
    return [int] $result.ProcessId
}

# ------------------------------------------------------------------------------------------------ paths

function Get-RmnFullPath([string] $Path) {
    return [System.IO.Path]::GetFullPath($Path.Trim().Trim('"')).TrimEnd('\')
}

function Test-RmnUnder([string] $Path, [string] $Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    $p = (Get-RmnFullPath $Path) + '\'
    $r = (Get-RmnFullPath $Root) + '\'
    return $p.StartsWith($r, [System.StringComparison]::OrdinalIgnoreCase)
}

# Why a home location is refused (SPEC section 3), or $null.
function Get-RmnHomeProblem([string] $HomeDir) {
    if (-not [System.IO.Path]::IsPathRooted($HomeDir)) { return "the home must be an absolute path" }
    if ($HomeDir -match '\\(WindowsApps|Packages)\\' -or $HomeDir -match '\\WindowsApps$') { return "a home inside an app package folder is virtualized" }
    foreach ($entry in @(@{ N = "LOCALAPPDATA"; P = $env:LOCALAPPDATA }, @{ N = "APPDATA"; P = $env:APPDATA }, @{ N = "TEMP"; P = [System.IO.Path]::GetTempPath() })) {
        if ($entry.P -and (Test-RmnUnder $HomeDir $entry.P)) { return "%$($entry.N)% may be virtualized for packaged apps (Codex desktop); use a folder under %USERPROFILE%" }
    }
    if ($env:USERPROFILE -and ((Get-RmnFullPath $HomeDir) -ieq (Get-RmnFullPath $env:USERPROFILE))) { return "the home must be a folder inside the user profile, not the profile itself" }
    if ($HomeDir -match '^[A-Za-z]:\\?$') { return "the home must not be a drive root" }
    return $null
}

function Get-RmnRevitExe([int] $Year) {
    $roots = @($env:ProgramW6432, $env:ProgramFiles, "C:\Program Files") | Where-Object { $_ } | Select-Object -Unique
    foreach ($root in $roots) {
        $exe = Join-Path $root "Autodesk\Revit $Year\Revit.exe"
        if (Test-Path -LiteralPath $exe -PathType Leaf) { return $exe }
    }
    return $null
}

function Get-RmnRunningRevit {
    return @(Get-Process -Name Revit -ErrorAction SilentlyContinue | ForEach-Object {
        $year = $null
        try { if ($_.Path -match 'Revit (\d{4})\\') { $year = [int] $Matches[1] } } catch { }
        [pscustomobject] @{ Pid = $_.Id; Year = $year }
    })
}

# ------------------------------------------------------------------------------------------------ files

function Get-RmnSha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RmnBytesSha256([byte[]] $Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString("x2") }) -join "") } finally { $sha.Dispose() }
}

function Get-RmnRelativeFiles([string] $Root) {
    $rootFull = Get-RmnFullPath $Root
    return @(Get-ChildItem -LiteralPath $rootFull -Recurse -File | ForEach-Object {
        [pscustomobject] @{ Relative = $_.FullName.Substring($rootFull.Length + 1); FullName = $_.FullName }
    } | Sort-Object -Property Relative -CaseSensitive)
}

# payloadId = first 12 hex of sha256 over the sorted "relative/path:sha256" lines (same rule as scripts\dev-install.ps1).
function Get-RmnPayloadId($Files) {
    $lines = @($Files | ForEach-Object { ($_.Relative -replace '\\', '/') + ":" + (Get-RmnSha256 $_.FullName) })
    return (Get-RmnBytesSha256 ($script:Utf8.GetBytes([string]::Join("`n", [string[]] $lines)))).Substring(0, 12)
}

function Write-RmnText([string] $Path, [string] $Text) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        if ([System.IO.File]::ReadAllText($Path, $script:Utf8) -ceq $Text) { return $false }
    }
    $tmp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [System.IO.File]::WriteAllText($tmp, $Text, $script:Utf8)
    if (Test-Path -LiteralPath $Path -PathType Leaf) { [System.IO.File]::Replace($tmp, $Path, [NullString]::Value, $true) } else { [System.IO.File]::Move($tmp, $Path) }
    return $true
}

function Copy-RmnTree($Files, [string] $Destination) {
    $copied = 0
    foreach ($file in $Files) {
        $target = Join-Path $Destination $file.Relative
        $dir = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        if ((Test-Path -LiteralPath $target -PathType Leaf) -and ((Get-RmnSha256 $target) -eq (Get-RmnSha256 $file.FullName))) { continue }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $copied++
    }
    return $copied
}

# Mirror a directory: copy changed files, delete files that are not in the source (only inside $Destination).
function Sync-RmnDirectory([string] $Source, [string] $Destination) {
    $files = Get-RmnRelativeFiles $Source
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $copied = Copy-RmnTree $files $Destination
    $keep = @{}
    foreach ($f in $files) { $keep[$f.Relative.ToLowerInvariant()] = $true }
    $removed = 0
    foreach ($existing in (Get-RmnRelativeFiles $Destination)) {
        if (-not $keep.ContainsKey($existing.Relative.ToLowerInvariant())) { Remove-Item -LiteralPath $existing.FullName -Force; $removed++ }
    }
    return [pscustomobject] @{ Copied = $copied; Removed = $removed }
}

function Backup-RmnFile([string] $Path, [string] $BackupDir, [string] $Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $target = Join-Path $BackupDir $Name
    Copy-Item -LiteralPath $Path -Destination $target -Force
    return $target
}

# ------------------------------------------------------------------------------------------------ home files

function Set-RmnPrivateAcl([string] $Path, [switch] $Container) {
    try {
        $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $system = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")
        $admins = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
        $inherit = [System.Security.AccessControl.InheritanceFlags]::None
        if ($Container) {
            $inherit = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
            $acl = New-Object System.Security.AccessControl.DirectorySecurity
        } else {
            $acl = New-Object System.Security.AccessControl.FileSecurity
        }
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @($user, $system, $admins)) {
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, "FullControl", $inherit, "None", "Allow"))) | Out-Null
        }
        if ($Container) { [System.IO.Directory]::SetAccessControl($Path, $acl) } else { [System.IO.File]::SetAccessControl($Path, $acl) }
        return $true
    } catch {
        Write-Warning "Could not restrict the ACL of ${Path}: $($_.Exception.Message)"
        return $false
    }
}

function Ensure-RmnMarker([string] $HomeDir, [string] $CreatedBy = "installer") {
    $marker = Join-Path $HomeDir ".revit-mcp-next-home"
    if (Test-Path -LiteralPath $marker -PathType Leaf) { return $false }
    $body = [ordered] @{ schema = 1; createdAtUtc = [DateTime]::UtcNow.ToString("o"); createdBy = $CreatedBy } | ConvertTo-Json -Compress
    return (Write-RmnText $marker $body)
}

# auth.env (D4 section 4.7): kept when it holds a valid token; never carried over from legacy installs (SPEC 12.1).
function Ensure-RmnAuth([string] $HomeDir) {
    $configDir = Join-Path $HomeDir "config"
    $file = Join-Path $configDir "auth.env"
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        $existing = [System.IO.File]::ReadAllText($file, $script:Utf8)
        if ($existing -match '(?m)^\s*REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*"?[A-Za-z0-9_-]{43,}"?\s*$') {
            Set-RmnPrivateAcl $file | Out-Null
            return "kept"
        }
    }
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    Write-RmnText $file "# revit-mcp-next local pipe token. Local only; never share or paste.`r`nAUTH_CONFIG_VERSION=1`r`nREVIT_MCP_NEXT_AUTH_TOKEN=$token`r`n" | Out-Null
    Set-RmnPrivateAcl $file | Out-Null
    return "created"
}

# ------------------------------------------------------------------------------------------------ add-in

# Copies the payload to <home>\addin\<year>\<payloadId>\ and writes current.json. Returns the payload facts.
function Install-RmnPayload([string] $HomeDir, [int] $Year, [string] $Source, [string] $InstalledBy = "installer") {
    $dll = Join-Path $Source "RevitMcpNext.Addin.dll"
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "The Revit $Year payload is missing: $dll" }
    $files = Get-RmnRelativeFiles $Source
    $payloadId = Get-RmnPayloadId $files
    $addinDir = Join-Path $HomeDir "addin\$Year"
    $payloadDir = Join-Path $addinDir $payloadId
    $copied = Copy-RmnTree $files $payloadDir
    $currentJson = Join-Path $addinDir "current.json"
    $existingId = $null
    if (Test-Path -LiteralPath $currentJson -PathType Leaf) {
        try { $existingId = ([System.IO.File]::ReadAllText($currentJson, $script:Utf8) | ConvertFrom-Json).payloadId } catch { $existingId = $null }
    }
    $gitSha = ""
    try {
        $product = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
        if ($product -match '\+([0-9A-Za-z]+)$') { $gitSha = $Matches[1] }
    } catch { }
    $updated = $false
    if ($existingId -ne $payloadId) {
        $current = [ordered] @{
            payloadId = $payloadId
            gitSha = $gitSha
            builtAtUtc = (Get-Item -LiteralPath $dll).LastWriteTimeUtc.ToString("o")
            installedAtUtc = [DateTime]::UtcNow.ToString("o")
            installedBy = $InstalledBy
        } | ConvertTo-Json -Compress
        $updated = Write-RmnText $currentJson $current
    }
    return [pscustomobject] @{ PayloadId = $payloadId; PayloadDir = $payloadDir; Copied = $copied; CurrentUpdated = $updated; Previous = $existingId; GitSha = $gitSha }
}

# Deletes payload folders other than the current one and the $Keep most recent ones; folders in use (locked) stay.
function Remove-RmnOldPayloads([string] $HomeDir, [int] $Year, [string] $CurrentId, [int] $Keep = 2) {
    $addinDir = Join-Path $HomeDir "addin\$Year"
    if (-not (Test-Path -LiteralPath $addinDir)) { return @() }
    $old = @(Get-ChildItem -LiteralPath $addinDir -Directory | Where-Object { $_.Name -match '^[0-9a-f]{12}$' -and $_.Name -ne $CurrentId } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip $Keep)
    $removed = @()
    foreach ($dir in $old) {
        try { Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction Stop; $removed += $dir.Name } catch { }
    }
    return $removed
}

# Copies the loader to <home>\addin\<year>\loader\<loaderId>\ (loaderId = first 12 hex of the DLL's sha256).
function Install-RmnLoader([string] $HomeDir, [int] $Year, [string] $Source) {
    $dll = Join-Path $Source "RevitMcpNext.Loader.dll"
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "The Revit $Year loader is missing: $dll" }
    $loaderId = (Get-RmnSha256 $dll).Substring(0, 12)
    $dir = Join-Path $HomeDir "addin\$Year\loader\$loaderId"
    $files = @(Get-RmnRelativeFiles $Source | Where-Object { $_.Relative -like "RevitMcpNext.Loader.*" })
    $copied = Copy-RmnTree $files $dir
    return [pscustomobject] @{ LoaderId = $loaderId; Dll = (Join-Path $dir "RevitMcpNext.Loader.dll"); Copied = $copied }
}

function Get-RmnManifestText([string] $LoaderDll) {
    return $script:ManifestTemplate.Replace("{{ASSEMBLY_PATH}}", [System.Security.SecurityElement]::Escape($LoaderDll)).Replace("`r`n", "`n").Replace("`n", "`r`n")
}

function Get-RmnManifestAssembly([string] $ManifestPath) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { return $null }
    try {
        $xml = [xml] [System.IO.File]::ReadAllText($ManifestPath, $script:Utf8)
        return [pscustomobject] @{ Assembly = [string] $xml.RevitAddIns.AddIn.Assembly; FullClassName = [string] $xml.RevitAddIns.AddIn.FullClassName }
    } catch { return [pscustomobject] @{ Assembly = ""; FullClassName = "" } }
}

# ------------------------------------------------------------------------------------------------ trust

function Get-RmnTrustKey([int] $Year) { return "HKCU:\Software\Autodesk\Revit\Autodesk Revit $Year\CodeSigning" }

function Get-RmnTrust([int] $Year) {
    $key = Get-RmnTrustKey $Year
    $name = $script:ClientId.ToLowerInvariant()
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties[$name]) { return [int] $item.$name }
    return $null
}

# Seeds "Always Load" for our ClientId (DWORD 1). Returns the previous value ($null when there was none).
function Set-RmnTrust([int] $Year) {
    $key = Get-RmnTrustKey $Year
    $before = Get-RmnTrust $Year
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name $script:ClientId.ToLowerInvariant() -Value 1 -PropertyType DWord -Force | Out-Null
    return $before
}

function Remove-RmnTrust([int] $Year) {
    $key = Get-RmnTrustKey $Year
    if (Test-Path -LiteralPath $key) { Remove-ItemProperty -LiteralPath $key -Name $script:ClientId.ToLowerInvariant() -ErrorAction SilentlyContinue }
}

# ------------------------------------------------------------------------------------------------ legacy

# Old layouts (0.2/0.3): %LOCALAPPDATA%\RevitMcpNext, packaged LocalCache copies, %APPDATA%\...\Addins\<year>\RevitMcpNext.
function Find-RmnLegacy {
    $found = New-Object System.Collections.Generic.List[string]
    if ($env:LOCALAPPDATA) {
        $root = Join-Path $env:LOCALAPPDATA "RevitMcpNext"
        if (Test-Path -LiteralPath $root) { $found.Add($root) }
        $packages = Join-Path $env:LOCALAPPDATA "Packages"
        if (Test-Path -LiteralPath $packages) {
            foreach ($pkg in (Get-ChildItem -LiteralPath $packages -Directory -ErrorAction SilentlyContinue)) {
                $cache = Join-Path $pkg.FullName "LocalCache\Local"
                if (Test-Path -LiteralPath $cache) {
                    Get-ChildItem -LiteralPath $cache -Directory -Filter "RevitMcpNext*" -ErrorAction SilentlyContinue | ForEach-Object { $found.Add($_.FullName) }
                }
            }
        }
    }
    if ($env:APPDATA) {
        foreach ($year in @(2021, 2022, 2023, 2024, 2025, 2026, 2027)) {
            $old = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\RevitMcpNext"
            if (Test-Path -LiteralPath $old) { $found.Add($old) }
        }
    }
    return @($found)
}

# ------------------------------------------------------------------------------------------------ node + CLI

function Get-RmnNodeVersion([string] $NodeExe) {
    try {
        $v = (& $NodeExe --version 2>$null | Select-Object -First 1)
        if ($v -match '^v(\d+)\.') { return [pscustomobject] @{ Text = $v.Trim(); Major = [int] $Matches[1] } }
    } catch { }
    return $null
}

# Runs <node> <cli> <args...> and returns @{ ExitCode; Output; Json }. Environment overrides are applied for the call only.
function Invoke-RmnCli([string] $NodeExe, [string] $Cli, [string[]] $Arguments, [hashtable] $Environment = @{}) {
    $saved = @{}
    foreach ($k in $Environment.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, "Process")
        [Environment]::SetEnvironmentVariable($k, $Environment[$k], "Process")
    }
    # Windows PowerShell turns native stderr lines into ErrorRecords; never let them stop the installer.
    $ErrorActionPreference = "Continue"
    try {
        $raw = @(& $NodeExe $Cli @Arguments 2>&1)
        $code = $LASTEXITCODE
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], "Process") }
    }
    $stdout = @($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { [string] $_ })
    $stderr = @($raw | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() })
    $text = ($stdout -join "`n")
    $json = $null
    $start = $text.IndexOf("{")
    if ($start -ge 0) { try { $json = $text.Substring($start) | ConvertFrom-Json } catch { $json = $null } }
    $all = $text
    if ($stderr.Count) { $all = ($text + "`n" + ($stderr -join "`n")).Trim() }
    return [pscustomobject] @{ ExitCode = $code; Output = $all; Json = $json }
}

function Get-RmnTimestamp { return (Get-Date).ToString("yyyyMMdd-HHmmss") }

Export-ModuleMember -Function *-Rmn*
