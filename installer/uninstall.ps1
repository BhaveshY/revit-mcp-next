<#
.SYNOPSIS
  Removes revit-mcp-next for this Windows user (the reverse of installer\install.ps1).

.DESCRIPTION
  powershell -NoProfile -ExecutionPolicy Bypass -File installer\uninstall.ps1 [-Purge] [-DryRun]
  1. Client registrations: Codex [mcp_servers.revit] block, plugin and AGENTS.md snippet (a plugin the installer
     disabled is re-enabled), Claude Code user MCP server and skills plugin, Claude Desktop entry. Backups first.
  2. Revit manifests that point into this home, and the trust entries the installer seeded.
  3. runtime\, broker\, addin\ and plugins\ of the home. config\, logs\ and the receipt stay unless -Purge
     (which deletes the whole home). Folders in use by a running Revit stay and are reported.
  Never closes Revit, Codex or Claude.
#>
[CmdletBinding()]
param(
    [Alias("Home")]
    [string] $HomeDir = "",
    [string[]] $Clients = @("codex", "claude", "claude-desktop"),
    [switch] $KeepTrust,
    [switch] $Purge,
    [switch] $DryRun,
    [switch] $Json,
    [string] $CodexHome = "",
    [string] $ClaudeConfigDir = "",
    [string] $ClaudeDesktopConfig = "",
    [string] $AddinsRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2
Import-Module (Join-Path $PSScriptRoot "lib\Rmn.psm1") -Force -DisableNameChecking

$package = Get-RmnPackageIdentity
if ($package) { throw "This PowerShell runs inside the app package '$package' (MSIX); run the uninstaller from a normal Windows PowerShell." }

if ([string]::IsNullOrWhiteSpace($HomeDir)) { $HomeDir = Join-Path $env:USERPROFILE ".revit-mcp-next" }
$HomeDir = Get-RmnFullPath $HomeDir
$problem = Get-RmnHomeProblem $HomeDir
if ($problem) { throw "Refusing home '$HomeDir': $problem." }
if (-not (Test-Path -LiteralPath (Join-Path $HomeDir ".revit-mcp-next-home") -PathType Leaf)) { throw "$HomeDir is not a revit-mcp-next home (no .revit-mcp-next-home marker); nothing was changed." }

$stamp = Get-RmnTimestamp
$steps = New-Object System.Collections.Generic.List[object]
function Step([string] $Name, [string] $Result, [string] $Detail) {
    $steps.Add([ordered] @{ step = $Name; result = $Result; detail = $Detail }) | Out-Null
    if (-not $Json) { Write-Host "[revit-mcp-next] $Name - $Result$(if ($Detail) { ": $Detail" })" }
}

$receipt = $null
$receiptPath = Join-Path $HomeDir "install.json"
if (Test-Path -LiteralPath $receiptPath) { try { $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } catch { $receipt = $null } }
$addinsRootResolved = if ($AddinsRoot) { Get-RmnFullPath $AddinsRoot } else { Join-Path $env:APPDATA "Autodesk\Revit\Addins" }

# 1. clients (needs the home's node + CLI, so before the files go)
$node = Join-Path $HomeDir "runtime\node.exe"
$cli = Join-Path $HomeDir "broker\revit-mcp-cli.mjs"
$clientList = @($Clients | ForEach-Object { "$_" -split "[,; ]+" } | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() } | Where-Object { $_ -ne "none" })
if ($clientList.Count -gt 0) {
    if ((Test-Path -LiteralPath $node) -and (Test-Path -LiteralPath $cli)) {
        $cliArgs = @("clients", "uninstall", "--home", $HomeDir, "--clients", ($clientList -join ","), "--json", "--stamp", $stamp)
        if ($DryRun) { $cliArgs += "--dry-run" }
        if ($CodexHome) { $cliArgs += "--codex-home"; $cliArgs += (Get-RmnFullPath $CodexHome) }
        if ($ClaudeDesktopConfig) { $cliArgs += "--claude-desktop-config"; $cliArgs += (Get-RmnFullPath $ClaudeDesktopConfig) }
        $envs = @{}
        if ($ClaudeConfigDir) { $envs["CLAUDE_CONFIG_DIR"] = (Get-RmnFullPath $ClaudeConfigDir) }
        $r = Invoke-RmnCli $node $cli $cliArgs $envs
        if ($r.Json) { foreach ($c in @($r.Json.changes)) { Step "$($c.client) $($c.step)" $c.result "$($c.detail)$(if (Get-RmnProp $c "backup") { " (backup $(Get-RmnProp $c "backup"))" })" } }
        else { Step "clients" "failed" $r.Output }
    } else {
        Step "clients" "warn" "the home has no runtime\node.exe or broker CLI; remove [mcp_servers.revit] from Codex and 'claude mcp remove revit -s user' by hand"
    }
}

# 2. manifests + trust
$running = @(Get-RmnRunningRevit)
foreach ($year in (Get-RmnSupportedYears)) {
    $manifest = Join-Path $addinsRootResolved "$year\RevitMcpNext.addin"
    $facts = Get-RmnManifestAssembly $manifest
    if ($facts -and $facts.Assembly -and (Test-RmnUnder $facts.Assembly $HomeDir)) {
        if ($DryRun) { Step "manifest $year" "would" "remove $manifest" }
        else {
            $b = Backup-RmnFile $manifest (Join-Path $HomeDir "backups\$stamp") "RevitMcpNext-$year.addin"
            Remove-Item -LiteralPath $manifest -Force
            Step "manifest $year" "done" "removed $manifest$(if ($b) { " (backup $b)" })"
        }
    } elseif ($facts) {
        Step "manifest $year" "skipped" "$manifest points to $($facts.Assembly), not into $HomeDir"
    }
}
if (-not $KeepTrust -and $receipt -and $receipt.PSObject.Properties["trust"]) {
    foreach ($t in @($receipt.trust)) {
        if ($null -eq $t) { continue }
        if ($null -eq $t.before) {
            if ($DryRun) { Step "trust $($t.year)" "would" "remove the Always Load entry the installer seeded" }
            else { Remove-RmnTrust ([int] $t.year); Step "trust $($t.year)" "done" "removed the Always Load entry the installer seeded" }
        }
    }
}

# 3. files
$targets = if ($Purge) { @($HomeDir) } else { @("runtime", "broker", "addin", "plugins") | ForEach-Object { Join-Path $HomeDir $_ } }
foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) { continue }
    if ($DryRun) { Step "files" "would" "delete $target"; continue }
    try {
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
        Step "files" "done" "deleted $target"
    } catch {
        Step "files" "warn" "could not delete $target (in use$(if ($running.Count) { ' by a running Revit' }); close it and rerun): $($_.Exception.Message)"
    }
}
if (-not $Purge -and -not $DryRun -and (Test-Path -LiteralPath $receiptPath)) {
    Rename-Item -LiteralPath $receiptPath -NewName "install.uninstalled-$stamp.json" -ErrorAction SilentlyContinue
}

$ok = -not ($steps | Where-Object { $_.result -eq "failed" })
if ($Json) { [ordered] @{ ok = [bool] $ok; dryRun = [bool] $DryRun; home = $HomeDir; steps = $steps.ToArray() } | ConvertTo-Json -Depth 6 }
elseif (-not $Purge) { Write-Host "Kept $HomeDir\config and logs (use -Purge to delete the whole home). Restart Codex/Claude so the revit server disappears." }
if (-not $ok) { exit 1 }
