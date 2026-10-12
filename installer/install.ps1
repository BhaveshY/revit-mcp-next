<#
.SYNOPSIS
  Installs revit-mcp-next for this Windows user: runtime home, Revit add-in (loader + payload), and the Codex /
  Claude registrations (SPEC sections 3, 9.2 and 12).

.DESCRIPTION
  Run from a normal Windows PowerShell (never from inside the Codex or Claude desktop app):
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\install.ps1
  Works from the extracted release zip (runtime\node.exe + artifacts\ next to installer\) and from a built repo
  (npm run build:all). Re-running it upgrades in place. Nothing outside these places is changed:
    <home>                                   default %USERPROFILE%\.revit-mcp-next
    %APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin   (points at the loader in <home>)
    Codex:  <CODEX_HOME>\config.toml [mcp_servers.revit], the revit-mcp-next plugin, AGENTS.md snippet
    Claude: claude mcp add-json revit (user scope) + the revit-mcp-next skills plugin
  Every changed client file is backed up next to itself as <file>.revit-mcp-next-<timestamp>.bak first.

.PARAMETER Home
  Runtime home. Default %USERPROFILE%\.revit-mcp-next. Never under %LOCALAPPDATA%, %APPDATA%, %TEMP% or app packages.
.PARAMETER RevitYears
  2024, 2027 or both. Default: the installed Revit versions (both payloads are installed when none is found).
.PARAMETER Clients
  auto (default: Codex and Claude Code when present), none, or a list of codex, claude, claude-desktop.
.PARAMETER NoAgentsSnippet
  Do not add the short Revit rule block to <CODEX_HOME>\AGENTS.md.
.PARAMETER PluginClients
  Clients that get the revit / revit-setup skills plugin (default codex, claude; none for neither).
.PARAMETER DisableConflictingPlugins
  Disable the revit-mcp-cowork plugin in Codex (its hooks act on every revit tool call). Claude Code is only reported.
.PARAMETER TrustRevitAlwaysLoad
  Seed HKCU "Always Load" trust for the add-in ClientId so Revit does not ask about the unsigned add-in.
.PARAMETER EnableCodeExecution
  Turn on run_csharp (settings.json enableCodeExecution). Off by default; -DisableCodeExecution turns it off again.
.PARAMETER RemoveLegacy
  Delete old 0.2/0.3 install folders (%LOCALAPPDATA%\RevitMcpNext, packaged LocalCache copies, Addins\<year>\RevitMcpNext).
.PARAMETER DryRun
  Show what would change; write nothing.
.PARAMETER Json
  Print only the JSON summary.
.PARAMETER Relaunch
  When started inside an app package (MSIX, e.g. from the Codex desktop app), start a clean copy of this installer
  outside the package (WMI Win32_Process.Create) and wait for it, instead of refusing.
#>
[CmdletBinding()]
param(
    [Alias("Home")]
    [string] $HomeDir = "",
    [string[]] $RevitYears = @(),
    [string[]] $Clients = @("auto"),
    [switch] $NoAgentsSnippet,
    [switch] $DisableConflictingPlugins,
    [switch] $TrustRevitAlwaysLoad,
    [switch] $EnableCodeExecution,
    [switch] $DisableCodeExecution,
    [switch] $RemoveLegacy,
    [switch] $NoPlugins,
    [string[]] $PluginClients = @("codex", "claude"),
    [switch] $NoManifest,
    [switch] $SkipDoctor,
    [switch] $DryRun,
    [switch] $Json,
    [switch] $Relaunch,
    [string] $PackageRoot = "",
    [string] $NodePath = "",
    [string] $CodexHome = "",
    [string] $ClaudeConfigDir = "",
    [string] $ClaudeDesktopConfig = "",
    [string] $AddinsRoot = "",
    [string] $LogFile = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2
Import-Module (Join-Path $PSScriptRoot "lib\Rmn.psm1") -Force -DisableNameChecking

$stamp = Get-RmnTimestamp
$steps = New-Object System.Collections.Generic.List[object]
$warnings = New-Object System.Collections.Generic.List[string]
$log = New-Object System.Collections.Generic.List[string]

function Say([string] $Message, [string] $Level = "info") {
    $line = "[revit-mcp-next] $Message"
    $log.Add("$((Get-Date).ToString('HH:mm:ss')) $Level $Message")
    if ($Json) { return }
    switch ($Level) {
        "warn" { Write-Host $line -ForegroundColor Yellow }
        "error" { Write-Host $line -ForegroundColor Red }
        "ok" { Write-Host $line -ForegroundColor Green }
        default { Write-Host $line }
    }
}

function Step([string] $Name, [string] $Result, [string] $Detail, $Data = $null) {
    $steps.Add([ordered] @{ step = $Name; result = $Result; detail = $Detail; data = $Data }) | Out-Null
    $level = if ($Result -in @("failed")) { "error" } elseif ($Result -in @("warn")) { "warn" } else { "info" }
    if ($Result -eq "warn") { $warnings.Add("${Name}: $Detail") | Out-Null }
    Say "$Name - $Result$(if ($Detail) { ": $Detail" })" $level
}

# ------------------------------------------------------------------------------------------------ 0. app package (V24)
$package = Get-RmnPackageIdentity
if ($package) {
    if (-not $Relaunch) {
        throw "This PowerShell runs inside the app package '$package' (MSIX, e.g. the Codex desktop app). Writes to %APPDATA% and HKCU are virtualized there, so Revit would not see the add-in. Run the installer from a normal Windows PowerShell, or add -Relaunch to start it outside the package."
    }
    $resolvedHome = if ($HomeDir) { Get-RmnFullPath $HomeDir } else { Join-Path $env:USERPROFILE ".revit-mcp-next" }
    $relaunchLog = Join-Path $resolvedHome "logs\install-$stamp-relaunch.log"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $relaunchLog) | Out-Null
    $argList = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        if ($entry.Key -in @("Relaunch", "LogFile")) { continue }
        if ($entry.Value -is [System.Management.Automation.SwitchParameter]) { if ($entry.Value.IsPresent) { $argList.Add("-$($entry.Key)") } }
        elseif ($entry.Value -is [array]) { $argList.Add("-$($entry.Key)"); $argList.Add("'" + (($entry.Value | ForEach-Object { "$_" }) -join ",").Replace("'", "''") + "'") }
        else { $argList.Add("-$($entry.Key)"); $argList.Add("'" + ([string] $entry.Value).Replace("'", "''") + "'") }
    }
    $argList.Add("-LogFile"); $argList.Add("'" + $relaunchLog.Replace("'", "''") + "'")
    $ps = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    $inner = "& '" + $PSCommandPath.Replace("'", "''") + "' " + ($argList -join " ") + "; exit `$LASTEXITCODE"
    $commandLine = "`"$ps`" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command `"$($inner.Replace('"', '\"'))`""
    Write-Host "[revit-mcp-next] running inside '$package'; relaunching outside the package (log $relaunchLog)"
    $childPid = Start-RmnOutsideProcess $commandLine (Split-Path -Parent $PSCommandPath)
    while (Get-Process -Id $childPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
    if (Test-Path -LiteralPath $relaunchLog) { Get-Content -LiteralPath $relaunchLog | ForEach-Object { Write-Host $_ } }
    else { throw "The relaunched installer (pid $childPid) wrote no log at $relaunchLog." }
    $last = Get-Content -LiteralPath $relaunchLog -Tail 1
    if ($last -match 'RESULT (\w+)') { if ($Matches[1] -ne "ok") { exit 1 } }
    exit 0
}

# ------------------------------------------------------------------------------------------------ 1. inputs
if ([string]::IsNullOrWhiteSpace($HomeDir)) { $HomeDir = Join-Path $env:USERPROFILE ".revit-mcp-next" }
$HomeDir = Get-RmnFullPath $HomeDir
$homeProblem = Get-RmnHomeProblem $HomeDir
if ($homeProblem) { throw "Refusing home '$HomeDir': $homeProblem." }
$defaultHome = Get-RmnFullPath (Join-Path $env:USERPROFILE ".revit-mcp-next")

if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = Split-Path -Parent $PSScriptRoot }
$PackageRoot = Get-RmnFullPath $PackageRoot
$artifacts = Join-Path $PackageRoot "artifacts"
$brokerSrc = Join-Path $artifacts "broker"
foreach ($required in @("revit-mcp.mjs", "revit-mcp-cli.mjs")) {
    if (-not (Test-Path -LiteralPath (Join-Path $brokerSrc $required) -PathType Leaf)) {
        throw "$(Join-Path $brokerSrc $required) is missing. Use the extracted release zip, or build the repo first: npm ci; npm run build:all"
    }
}
$isZip = Test-Path -LiteralPath (Join-Path $PackageRoot "release.json") -PathType Leaf
$release = if ($isZip) { Get-Content -LiteralPath (Join-Path $PackageRoot "release.json") -Raw | ConvertFrom-Json } else { $null }
$buildInfo = Get-Content -LiteralPath (Join-Path $brokerSrc "build-info.json") -Raw | ConvertFrom-Json
$version = [string] $buildInfo.version
$gitSha = [string] $buildInfo.gitSha

# Node: -NodePath, else the package's runtime\node.exe, else node on PATH (repo mode).
if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $candidate = Join-Path $PackageRoot "runtime\node.exe"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $NodePath = $candidate }
    else {
        $cmd = Get-Command node.exe -ErrorAction SilentlyContinue
        if ($cmd) { $NodePath = $cmd.Source }
    }
}
if ([string]::IsNullOrWhiteSpace($NodePath) -or -not (Test-Path -LiteralPath $NodePath -PathType Leaf)) { throw "node.exe was not found (pass -NodePath or use the release zip, which ships runtime\node.exe)." }
$nodeVersion = Get-RmnNodeVersion $NodePath
if (-not $nodeVersion -or $nodeVersion.Major -lt 24) { throw "$NodePath is $(if ($nodeVersion) { $nodeVersion.Text } else { 'not runnable' }); Node 24 or newer is required." }

# Years: explicit, else installed Revit versions, else every payload in the package.
$years = @($RevitYears | ForEach-Object { "$_" -split "[,; ]+" } | Where-Object { $_ } | ForEach-Object { [int] $_ } | Sort-Object -Unique)
$available = @(Get-RmnSupportedYears | Where-Object { Test-Path -LiteralPath (Join-Path $artifacts "addin\$_\RevitMcpNext.Addin.dll") })
if ($years.Count -eq 0) {
    $years = @(Get-RmnSupportedYears | Where-Object { Get-RmnRevitExe $_ })
    if ($years.Count -eq 0) { $years = $available; $warnings.Add("no Revit 2024/2027 found on this PC; installed the add-in for $($available -join ', ') anyway") | Out-Null }
}
foreach ($y in $years) {
    if ($y -notin (Get-RmnSupportedYears)) { throw "Revit $y is not supported (2024 and 2027 are)." }
    if ($y -notin $available) { throw "The package has no Revit $y payload ($(Join-Path $artifacts "addin\$y"))." }
}

# Clients.
$clientList = @($Clients | ForEach-Object { "$_" -split "[,; ]+" } | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
$codexHomeResolved = if ($CodexHome) { Get-RmnFullPath $CodexHome } elseif ($env:CODEX_HOME) { Get-RmnFullPath $env:CODEX_HOME } else { Join-Path $env:USERPROFILE ".codex" }
if ($clientList -contains "auto") {
    $clientList = @()
    if ((Test-Path -LiteralPath $codexHomeResolved) -or (Get-Command codex -ErrorAction SilentlyContinue)) { $clientList += "codex" }
    if (Get-Command claude -ErrorAction SilentlyContinue) { $clientList += "claude" }
} elseif ($clientList -contains "none") { $clientList = @() }
foreach ($c in $clientList) { if ($c -notin @("codex", "claude", "claude-desktop")) { throw "Unknown client '$c' (codex, claude, claude-desktop, auto, none)." } }
$addinsRootResolved = if ($AddinsRoot) { Get-RmnFullPath $AddinsRoot } else { Join-Path $env:APPDATA "Autodesk\Revit\Addins" }
$cliEnv = @{}
if ($CodexHome) { $cliEnv["CODEX_HOME"] = $codexHomeResolved }
if ($ClaudeConfigDir) { $cliEnv["CLAUDE_CONFIG_DIR"] = (Get-RmnFullPath $ClaudeConfigDir) }

Say "revit-mcp-next $version ($gitSha) from $(if ($isZip) { 'release package' } else { 'repo build' }) $PackageRoot"
Say "home $HomeDir; Revit $($years -join ', '); clients $(if ($clientList.Count) { $clientList -join ', ' } else { 'none' })$(if ($DryRun) { '; DRY RUN' })"
$running = @(Get-RmnRunningRevit)
if ($running.Count -gt 0) { Step "revit" "warn" "Revit is running (pid $(($running | ForEach-Object { $_.Pid }) -join ', ')): it keeps the add-in it loaded; restart Revit after the install to load the new one" }

# ------------------------------------------------------------------------------------------------ 2. home, runtime, broker
$homeNode = Join-Path $HomeDir "runtime\node.exe"
$homeCli = Join-Path $HomeDir "broker\revit-mcp-cli.mjs"
$backupDir = Join-Path $HomeDir "backups\$stamp"
$receiptPath = Join-Path $HomeDir "install.json"
$previous = $null
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) { try { $previous = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } catch { $previous = $null } }

if ($DryRun) {
    Step "home" "would" "create/update $HomeDir (marker, runtime\node.exe $($nodeVersion.Text), broker bundle, config\auth.env)"
} else {
    New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null
    $markerNew = Ensure-RmnMarker $HomeDir "installer"
    foreach ($d in @("runtime", "broker", "config", "logs", "instances", "plugins")) { New-Item -ItemType Directory -Force -Path (Join-Path $HomeDir $d) | Out-Null }
    $nodeChanged = $false
    if (-not (Test-Path -LiteralPath $homeNode -PathType Leaf) -or (Get-RmnSha256 $homeNode) -ne (Get-RmnSha256 $NodePath)) {
        try { Copy-Item -LiteralPath $NodePath -Destination $homeNode -Force; $nodeChanged = $true }
        catch { throw "Could not replace $homeNode (is a broker still running? close Codex/Claude sessions that use Revit and retry): $($_.Exception.Message)" }
    }
    $brokerSync = Sync-RmnDirectory $brokerSrc (Join-Path $HomeDir "broker")
    $auth = Ensure-RmnAuth $HomeDir
    Step "home" "done" "$HomeDir (marker $(if ($markerNew) { 'written' } else { 'present' }); node $($nodeVersion.Text) $(if ($nodeChanged) { 'copied' } else { 'unchanged' }); broker $($brokerSync.Copied) file(s) updated; auth.env $auth)"
}

# Settings merge (SPEC 3.1) through the CLI so the TypeScript schema is the only validator.
$cli = if ($DryRun -or -not (Test-Path -LiteralPath $homeCli)) { Join-Path $brokerSrc "revit-mcp-cli.mjs" } else { $homeCli }
$cliNode = if ($DryRun -or -not (Test-Path -LiteralPath $homeNode)) { $NodePath } else { $homeNode }
$settingsArgs = @("settings", "merge", "--home", $HomeDir, "--json", "--stamp", $stamp)
if ($EnableCodeExecution) { $settingsArgs += "--enable-code-execution" }
if ($DisableCodeExecution) { $settingsArgs += "--disable-code-execution" }
if ($DryRun) { $settingsArgs += "--dry-run" }
$settings = Invoke-RmnCli $cliNode $cli $settingsArgs
if ($settings.ExitCode -ne 0 -or -not $settings.Json) { Step "settings" "failed" $settings.Output }
else {
    $s = $settings.Json
    $detail = "enableCodeExecution=$($s.enableCodeExecution)$(if ($s.dropped.Count) { "; dropped $($s.dropped -join ', ')" })$(if ($s.issues.Count) { "; $($s.issues -join '; ')" })"
    Step "settings" $(if ($DryRun) { "would" } elseif ($s.changed) { "done" } else { "unchanged" }) $detail
}

# ------------------------------------------------------------------------------------------------ 3. add-in per year
$yearFacts = New-Object System.Collections.Generic.List[object]
foreach ($year in $years) {
    $payloadSrc = Join-Path $artifacts "addin\$year"
    $loaderSrc = Join-Path $artifacts "loader\$year"
    $manifestPath = Join-Path $addinsRootResolved "$year\RevitMcpNext.addin"
    if ($DryRun) {
        $payloadId = Get-RmnPayloadId (Get-RmnRelativeFiles $payloadSrc)
        $loaderId = (Get-RmnSha256 (Join-Path $loaderSrc "RevitMcpNext.Loader.dll")).Substring(0, 12)
        $loaderDll = Join-Path $HomeDir "addin\$year\loader\$loaderId\RevitMcpNext.Loader.dll"
        $existing = Get-RmnManifestAssembly $manifestPath
        Step "addin $year" "would" "payload $payloadId, loader $loaderId; manifest $manifestPath -> $loaderDll$(if ($existing) { " (now: $($existing.Assembly))" })"
        $yearFacts.Add([ordered] @{ year = $year; payloadId = $payloadId; loader = $loaderDll; manifest = $manifestPath }) | Out-Null
        continue
    }
    $payload = Install-RmnPayload $HomeDir $year $payloadSrc
    $loader = Install-RmnLoader $HomeDir $year $loaderSrc
    $pruned = @(Remove-RmnOldPayloads $HomeDir $year $payload.PayloadId)
    $manifestBackup = $null
    $manifestWritten = $false
    if (-not $NoManifest) {
        $text = Get-RmnManifestText $loader.Dll
        $existing = Get-RmnManifestAssembly $manifestPath
        if ((Test-Path -LiteralPath $manifestPath -PathType Leaf) -and ([System.IO.File]::ReadAllText($manifestPath) -cne $text)) {
            $manifestBackup = Backup-RmnFile $manifestPath $backupDir "RevitMcpNext-$year.addin"
        }
        $manifestWritten = Write-RmnText $manifestPath $text
        if ($existing -and $existing.Assembly -and -not (Test-RmnUnder ([System.IO.Path]::Combine((Split-Path -Parent $manifestPath), $existing.Assembly)) $HomeDir) -and $manifestWritten) {
            Say "Revit ${year}: the manifest pointed to $($existing.Assembly); it now points to the loader in this home"
        }
    }
    Step "addin $year" "done" "payload $($payload.PayloadId) ($($payload.Copied) file(s) copied$(if ($pruned.Count) { "; removed old $($pruned -join ', ')" })); loader $($loader.LoaderId); manifest $(if ($NoManifest) { 'skipped' } elseif ($manifestWritten) { 'written' } else { 'unchanged' })"
    if (-not (Get-RmnRevitExe $year)) { Step "addin $year" "warn" "Revit $year is not installed here; the add-in is ready for when it is" }
    $yearFacts.Add([ordered] @{
        year = $year; payloadId = $payload.PayloadId; gitSha = $payload.GitSha; loader = $loader.Dll
        manifest = if ($NoManifest) { $null } else { $manifestPath }; manifestBackup = $manifestBackup
    }) | Out-Null
}
if (-not (Test-RmnUnder $HomeDir $defaultHome) -and -not ((Get-RmnFullPath $HomeDir) -ieq $defaultHome) -and -not $NoManifest) {
    Say "note: the manifests now point to the loader in $HomeDir, so Revit uses this home" "warn"
}

# Trust (opt-in).
$trusted = @()
if ($TrustRevitAlwaysLoad) {
    foreach ($year in $years) {
        if ($DryRun) { Step "trust $year" "would" "HKCU Revit $year CodeSigning $((Get-RmnClientId).ToLowerInvariant()) = 1"; continue }
        $before = Set-RmnTrust $year
        $trusted += [ordered] @{ year = $year; before = $before }
        Step "trust $year" $(if ($before -eq 1) { "unchanged" } else { "done" }) "Always Load seeded for ClientId $(Get-RmnClientId)"
    }
}

# ------------------------------------------------------------------------------------------------ 4. plugins + clients
$pluginsSrc = Join-Path $PackageRoot "plugins"
$pluginsDir = Join-Path $HomeDir "plugins"
if (-not $NoPlugins -and (Test-Path -LiteralPath $pluginsSrc)) {
    if ($DryRun) { Step "plugins" "would" "copy $pluginsSrc to $pluginsDir" }
    else {
        $sync = Sync-RmnDirectory $pluginsSrc $pluginsDir
        Step "plugins" "done" "$pluginsDir ($($sync.Copied) copied, $($sync.Removed) removed)"
    }
}
$clientChanges = @()
if ($clientList.Count -gt 0) {
    $clientArgs = @("clients", "install", "--home", $HomeDir, "--clients", ($clientList -join ","), "--json", "--stamp", $stamp, "--plugins-dir", $pluginsDir,
        "--agents-snippet", (Join-Path $PSScriptRoot "agents-snippet.md"))
    if ($NoAgentsSnippet) { $clientArgs += "--no-agents-snippet" }
    if ($DisableConflictingPlugins) { $clientArgs += "--disable-conflicting-plugins" }
    if ($NoPlugins) { $clientArgs += "--no-plugins" }
    $pluginList = @($PluginClients | ForEach-Object { "$_" -split "[,; ]+" } | Where-Object { $_ })
    $clientArgs += "--plugin-clients"; $clientArgs += $(if ($pluginList.Count) { $pluginList -join "," } else { "none" })
    if ($DryRun) { $clientArgs += "--dry-run"; $clientArgs += "--command"; $clientArgs += $homeNode; $clientArgs += "--bundle"; $clientArgs += (Join-Path $HomeDir "broker\revit-mcp.mjs") }
    if ($CodexHome) { $clientArgs += "--codex-home"; $clientArgs += $codexHomeResolved }
    if ($ClaudeDesktopConfig) { $clientArgs += "--claude-desktop-config"; $clientArgs += (Get-RmnFullPath $ClaudeDesktopConfig) }
    $r = Invoke-RmnCli $cliNode $cli $clientArgs $cliEnv
    if (-not $r.Json) { Step "clients" "failed" $r.Output }
    else {
        $clientChanges = @($r.Json.changes)
        foreach ($c in $clientChanges) {
            $res = switch ($c.result) { "failed" { "failed" } "warn" { "warn" } default { $c.result } }
            Step "$($c.client) $($c.step)" $res "$($c.detail)$(if (Get-RmnProp $c "backup") { " (backup $(Get-RmnProp $c "backup"))" })"
        }
    }
}

# ------------------------------------------------------------------------------------------------ 5. legacy
$legacy = @(Find-RmnLegacy)
$legacyRemoved = @()
if ($legacy.Count -gt 0) {
    if ($RemoveLegacy -and -not $DryRun) {
        foreach ($path in $legacy) {
            try { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop; $legacyRemoved += $path } catch { Step "legacy" "warn" "could not remove ${path}: $($_.Exception.Message)" }
        }
        Step "legacy" "done" "removed $($legacyRemoved -join '; ')"
    } else {
        Step "legacy" $(if ($RemoveLegacy) { "would" } else { "info" }) "old install folders: $($legacy -join '; ')$(if (-not $RemoveLegacy) { ' (rerun with -RemoveLegacy to delete them)' })"
    }
}

# ------------------------------------------------------------------------------------------------ 6. receipt + doctor
$failed = @($steps | Where-Object { $_.result -eq "failed" })
if (-not $DryRun) {
    $receipt = [ordered] @{
        schema = 1
        product = "revit-mcp-next"
        version = $version
        gitSha = $gitSha
        installedAtUtc = [DateTime]::UtcNow.ToString("o")
        installedBy = "$env:USERDOMAIN\$env:USERNAME"
        packageRoot = $PackageRoot
        packageKind = if ($isZip) { "release" } else { "repo" }
        home = $HomeDir
        node = $nodeVersion.Text
        years = $yearFacts.ToArray()
        trust = @($trusted)
        clients = @($clientList)
        clientChanges = @($clientChanges)
        agentsSnippet = (-not $NoAgentsSnippet) -and ($clientList -contains "codex")
        disableConflictingPlugins = [bool] $DisableConflictingPlugins
        codexHome = $codexHomeResolved
        legacyFound = @($legacy)
        legacyRemoved = @($legacyRemoved)
        previous = if ($previous) { [ordered] @{ version = $previous.version; gitSha = $previous.gitSha; installedAtUtc = $previous.installedAtUtc } } else { $null }
    }
    Write-RmnText $receiptPath (($receipt | ConvertTo-Json -Depth 8) + "`r`n") | Out-Null
}

$doctor = $null
if (-not $DryRun -and -not $SkipDoctor) {
    $doctorArgs = @("doctor", "--quick", "--json", "--home", $HomeDir, "--clients", (($clientList + @()) -join ","))
    if ($CodexHome) { $doctorArgs += "--codex-home"; $doctorArgs += $codexHomeResolved }
    if ($AddinsRoot) { $doctorArgs += "--addins-root"; $doctorArgs += $addinsRootResolved }
    $d = Invoke-RmnCli $homeNode $homeCli $doctorArgs $cliEnv
    $doctor = $d.Json
    if ($doctor) {
        foreach ($check in @($doctor.checks | Where-Object { $_.level -in @("fail", "warn") })) {
            Say "doctor $($check.level): $($check.id): $($check.message)$(if ($check.fix) { " (fix: $($check.fix))" })" $(if ($check.level -eq "fail") { "error" } else { "warn" })
        }
        Step "doctor" $(if ($doctor.ok) { "done" } else { "failed" }) "quick doctor: $($doctor.fails) failure(s), $($doctor.warns) warning(s)"
    } else { Step "doctor" "warn" "the doctor did not return JSON: $($d.Output)" }
}

$failed = @($steps | Where-Object { $_.result -eq "failed" })
$ok = $failed.Count -eq 0
$clientNames = @{ "codex" = "Codex (quit and reopen the desktop app, or start a new CLI session)"; "claude" = "Claude Code"; "claude-desktop" = "Claude Desktop" }
$restartNext = if ($clientList.Count) { "Restart so the 'revit' server loads: $(($clientList | ForEach-Object { $clientNames[$_] }) -join '; ')." } else { "No AI client was registered; rerun with -Clients codex,claude (see docs\clients.md)." }
$summary = [ordered] @{
    ok = $ok
    dryRun = [bool] $DryRun
    version = $version
    gitSha = $gitSha
    home = $HomeDir
    years = $yearFacts.ToArray()
    clients = @($clientList)
    agentsMdEdited = @($clientChanges | Where-Object { $_.step -eq "AGENTS.md" -and $_.result -eq "done" }).Count -gt 0
    steps = $steps.ToArray()
    warnings = $warnings.ToArray()
    doctor = if ($doctor) { [ordered] @{ ok = $doctor.ok; fails = $doctor.fails; warns = $doctor.warns } } else { $null }
    next = @(
        $restartNext,
        "Start Revit $($years -join ' / ') normally; the first start may ask once to load the add-in (choose Always Load).",
        "Check (PowerShell): & `"$homeNode`" `"$homeCli`" doctor"
    )
}

if (-not $Json) {
    Write-Host ""
    $edited = @($clientChanges | Where-Object { $_.result -eq "done" -and $_.step -ne "backup" -and (Get-RmnProp $_ "file") } | ForEach-Object { Get-RmnProp $_ "file" } | Select-Object -Unique)
    if ($edited.Count) { Write-Host "Changed client files (backups next to each): $($edited -join ', ')" }
    if ($summary.agentsMdEdited) { Write-Host "Edited $codexHomeResolved\AGENTS.md: added/updated the short Revit rule block (rerun with -NoAgentsSnippet to leave it alone)." }
    if ($ok) { Write-Host "revit-mcp-next $version installed in $HomeDir$(if ($DryRun) { ' (dry run: nothing was written)' })." -ForegroundColor Green }
    else { Write-Host "Install finished with $($failed.Count) failure(s); see above." -ForegroundColor Red }
    foreach ($n in $summary.next) { Write-Host "  next: $n" }
}
if ($LogFile -or -not $DryRun) {
    $logPath = if ($LogFile) { $LogFile } else { Join-Path $HomeDir "logs\install-$stamp.log" }
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logPath) | Out-Null
        $log.Add("RESULT $(if ($ok) { 'ok' } else { 'failed' })")
        [System.IO.File]::WriteAllLines($logPath, [string[]] $log)
    } catch { }
}
if ($Json) { $summary | ConvertTo-Json -Depth 8 }
if (-not $ok) { exit 1 }
