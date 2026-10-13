<#
.SYNOPSIS
  Builds the portable release zip: artifacts\release\revit-mcp-next-<version>-win-x64.zip

.DESCRIPTION
  The zip needs no build tools on the target PC. It contains:
    INSTALL.txt, install.cmd, uninstall.cmd, release.json, checksums.sha256
    installer\                      install.ps1, uninstall.ps1, lib\Rmn.psm1, agents-snippet.md
    runtime\node.exe                copy of the build machine's node (24+)
    artifacts\broker\               revit-mcp.mjs, revit-mcp-cli.mjs, build-info.json (esbuild bundles)
    artifacts\addin\<year>\         prebuilt add-in payloads for Revit 2024 and 2027
    artifacts\loader\<year>\        the loaders the manifests point to
    artifacts\catalog\              the tool catalog
    plugins\                        Codex + Claude marketplaces with the revit / revit-setup skills
    docs\, README.md, SECURITY.md, LICENSE
    e2e\ + broker\dist + node_modules (production closure) + scripts\dev-install.ps1 ...  the e2e harness:
                                    runtime\node.exe e2e\run.mjs --years 2024,2027 --launch --profile full
  Run from the repo root: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [string] $DotnetPath = "",
    [string] $NodePath = "",
    [string] $OutDir = "",
    [switch] $KeepStaging
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$years = @(2024, 2027)

function Say($m) { Write-Host "[revit-mcp-next package] $m" }
function Invoke-Checked([string] $What, [scriptblock] $Block) {
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

# ------------------------------------------------------------------------------------------------ build
Push-Location $repo
try {
    if (-not $SkipBuild) {
        Say "npm run build"
        Invoke-Checked "npm run build" { npm run build }
        Say "npm run build:bundle"
        Invoke-Checked "npm run build:bundle" { npm run build:bundle }
        foreach ($year in $years) {
            Say "build-addin $year"
            $buildArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repo "scripts\build-addin.ps1"), "-RevitYear", "$year")
            if ($DotnetPath) { $buildArgs += @("-DotnetPath", $DotnetPath) }
            Invoke-Checked "build-addin $year" { powershell.exe @buildArgs }
        }
    }
} finally { Pop-Location }

$artifacts = Join-Path $repo "artifacts"
$required = @(
    "broker\revit-mcp.mjs", "broker\revit-mcp-cli.mjs", "broker\build-info.json", "catalog\catalog.json"
) + ($years | ForEach-Object { "addin\$_\RevitMcpNext.Addin.dll"; "loader\$_\RevitMcpNext.Loader.dll" })
foreach ($r in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifacts $r) -PathType Leaf)) { throw "artifacts\$r is missing; run without -SkipBuild." }
}
foreach ($r in @("broker\dist\src\index.js", "contracts\dist\home.js")) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $r) -PathType Leaf)) { throw "$r is missing; run npm run build." }
}
if ([string]::IsNullOrWhiteSpace($NodePath)) { $NodePath = (Get-Command node.exe -ErrorAction Stop).Source }
$nodeVersion = (& $NodePath --version).Trim()
if ($nodeVersion -notmatch '^v(\d+)\.' -or [int] $Matches[1] -lt 24) { throw "$NodePath is $nodeVersion; the release needs Node 24+." }

$buildInfo = Get-Content -LiteralPath (Join-Path $artifacts "broker\build-info.json") -Raw | ConvertFrom-Json
$version = [string] $buildInfo.version
$name = "revit-mcp-next-$version-win-x64"
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $artifacts "release" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stage = Join-Path $OutDir $name
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Say "staging $stage ($version, $($buildInfo.gitSha), node $nodeVersion)"

function Copy-Into([string] $Source, [string] $Target, [string[]] $Exclude = @()) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "$Source is missing." }
    if (Test-Path -LiteralPath $Source -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) | Out-Null
        Copy-Item -LiteralPath $Source -Destination $Target -Force
        return
    }
    $root = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length + 1)
        foreach ($pattern in $Exclude) { if ($relative -like $pattern) { return } }
        $dest = Join-Path $Target $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
    }
}

# ------------------------------------------------------------------------------------------------ contents
Copy-Into (Join-Path $repo "installer\install.ps1") (Join-Path $stage "installer\install.ps1")
Copy-Into (Join-Path $repo "installer\uninstall.ps1") (Join-Path $stage "installer\uninstall.ps1")
Copy-Into (Join-Path $repo "installer\agents-snippet.md") (Join-Path $stage "installer\agents-snippet.md")
Copy-Into (Join-Path $repo "installer\lib") (Join-Path $stage "installer\lib")
Copy-Into $NodePath (Join-Path $stage "runtime\node.exe")
Copy-Into (Join-Path $artifacts "broker") (Join-Path $stage "artifacts\broker")
Copy-Into (Join-Path $artifacts "catalog") (Join-Path $stage "artifacts\catalog")
foreach ($year in $years) {
    Copy-Into (Join-Path $artifacts "addin\$year") (Join-Path $stage "artifacts\addin\$year")
    Copy-Into (Join-Path $artifacts "loader\$year") (Join-Path $stage "artifacts\loader\$year")
}
Copy-Into (Join-Path $repo "plugins") (Join-Path $stage "plugins")
Copy-Into (Join-Path $repo "docs") (Join-Path $stage "docs")
foreach ($f in @("README.md", "SECURITY.md", "LICENSE")) { if (Test-Path (Join-Path $repo $f)) { Copy-Into (Join-Path $repo $f) (Join-Path $stage $f) } }

# e2e harness: the repo-shaped subset it imports (e2e/, broker/dist, contracts via node_modules, dev-install).
Copy-Into (Join-Path $repo "e2e") (Join-Path $stage "e2e")
$noMaps = @("*.map", "*.d.ts")
Copy-Into (Join-Path $repo "broker\dist") (Join-Path $stage "broker\dist") $noMaps
Copy-Into (Join-Path $repo "broker\package.json") (Join-Path $stage "broker\package.json")
foreach ($s in @("dev-install.ps1", "ensure-revit-addin-trust.ps1", "resolve-dotnet-sdk.ps1")) {
    if (Test-Path (Join-Path $repo "scripts\$s")) { Copy-Into (Join-Path $repo "scripts\$s") (Join-Path $stage "scripts\$s") }
}
Copy-Into (Join-Path $repo "addin\RevitMcpNext.Addin\RevitMcpNext.addin.template") (Join-Path $stage "addin\RevitMcpNext.Addin\RevitMcpNext.addin.template")
$rootPackage = Get-Content -LiteralPath (Join-Path $repo "package.json") -Raw | ConvertFrom-Json
$minimal = [ordered] @{ name = "revit-mcp-next-release"; private = $true; version = $version; description = "Extracted revit-mcp-next release (see INSTALL.txt)."; engines = $rootPackage.engines }
[System.IO.File]::WriteAllText((Join-Path $stage "package.json"), (($minimal | ConvertTo-Json) + "`n"), (New-Object System.Text.UTF8Encoding($false)))

# node_modules: the production closure (npm ls); workspace packages become real folders with package.json + dist.
Push-Location $repo
try { $closure = @(npm ls --omit=dev --all --parseable 2>$null) } finally { Pop-Location }
$nodeModules = Join-Path $repo "node_modules"
foreach ($path in $closure) {
    if (-not $path -or -not $path.StartsWith($nodeModules, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
    $relative = $path.Substring($nodeModules.Length + 1)
    $target = Join-Path $stage "node_modules\$relative"
    $item = Get-Item -LiteralPath $path -Force
    if ($item.LinkType) {
        if ($relative -eq "@revit-mcp-next\broker") { continue }  # the harness uses broker\dist by path
        $real = $item.Target | Select-Object -First 1
        if (-not [System.IO.Path]::IsPathRooted($real)) { $real = Join-Path (Split-Path -Parent $path) $real }
        Copy-Into (Join-Path $real "package.json") (Join-Path $target "package.json")
        Copy-Into (Join-Path $real "dist") (Join-Path $target "dist") $noMaps
    } else {
        Copy-Into $path $target
    }
}

# ------------------------------------------------------------------------------------------------ text files
$sha = [string] $buildInfo.gitSha
$readme = @"
revit-mcp-next $version ($sha) - Revit 2024 / 2027 MCP server for Codex and Claude Code, Windows x64
=====================================================================================================

INSTALL (one command)
  Close Revit. Open a normal Windows PowerShell (not "Run as administrator", not inside the Codex or Claude app),
  cd into this extracted folder and run:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1

  (or double-click install.cmd). Nothing is built on this PC: runtime\node.exe, the MCP server bundle and the
  prebuilt Revit add-ins are in this folder. Preview without changing anything: add -DryRun.
  Options: -TrustRevitAlwaysLoad (no "Always Load" prompt in Revit)  -DisableConflictingPlugins (turns off
  revit-mcp-cowork in Codex)  -NoAgentsSnippet  -RemoveLegacy (old 0.2/0.3 folders)  -RevitYears 2024,2027
  -Clients codex,claude,claude-desktop  -Home <dir>  -Json.

  What it changes (backups next to every client file, *.revit-mcp-next-<time>.bak):
    %USERPROFILE%\.revit-mcp-next\                               the home (runtime, broker, add-in, config, logs)
    %APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin     points at the loader in the home
    %USERPROFILE%\.codex\config.toml                              [mcp_servers.revit] + the revit-mcp-next plugin
    %USERPROFILE%\.codex\AGENTS.md                                short Revit rule block (unless -NoAgentsSnippet)
    Claude Code                                                   claude mcp add-json revit (user) + skills plugin

AFTER INSTALL
  1. Restart Codex (quit/reopen the desktop app or start a new CLI session) and Claude Code.
  2. Start Revit 2024 or 2027 normally and open a model. First start: choose "Always Load" if Revit asks.
  3. Check everything:
       & "`$env:USERPROFILE\.revit-mcp-next\runtime\node.exe" "`$env:USERPROFILE\.revit-mcp-next\broker\revit-mcp-cli.mjs" doctor
  4. In Codex or Claude: "Use the revit MCP: call status and report what it says."

END-TO-END TESTS (real Revit, disposable models only; run from this folder in a normal PowerShell)
    .\runtime\node.exe e2e\run.mjs --profile surface                          no Revit needed
    .\runtime\node.exe e2e\run.mjs --years 2024,2027 --launch --profile full   launches Revit itself (WMI)
  The harness uses its own home %USERPROFILE%\.revit-mcp-next-e2e, launches Revit outside the AI app, creates
  throwaway projects from the English templates and writes artifacts\e2e\<run>\summary.json. Never run it on
  real project files. See e2e\README.md and docs\.

SUPPORT
    & "`$env:USERPROFILE\.revit-mcp-next\runtime\node.exe" "`$env:USERPROFILE\.revit-mcp-next\broker\revit-mcp-cli.mjs" support
  writes a zip of logs and reports (no tokens) under the home's support\ folder.

UNINSTALL
    powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\uninstall.ps1          (keeps config and logs)
    powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\uninstall.ps1 -Purge   (deletes the whole home)
"@
[System.IO.File]::WriteAllText((Join-Path $stage "INSTALL.txt"), $readme.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
$cmd = "@echo off`r`nrem revit-mcp-next installer: run from a normal (not elevated) Windows session.`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0installer\install.ps1`" %*`r`nif errorlevel 1 (echo Install failed - see the messages above.) else (echo Done.)`r`npause`r`n"
[System.IO.File]::WriteAllText((Join-Path $stage "install.cmd"), $cmd, (New-Object System.Text.UTF8Encoding($false)))
$ucmd = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0installer\uninstall.ps1`" %*`r`npause`r`n"
[System.IO.File]::WriteAllText((Join-Path $stage "uninstall.cmd"), $ucmd, (New-Object System.Text.UTF8Encoding($false)))

$payloads = [ordered] @{}
foreach ($year in $years) {
    $dll = Join-Path $artifacts "addin\$year\RevitMcpNext.Addin.dll"
    $payloads["$year"] = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
}
$release = [ordered] @{
    name = $name
    version = $version
    gitSha = $sha
    builtAtUtc = [DateTime]::UtcNow.ToString("o")
    node = $nodeVersion
    sdk = [string] $buildInfo.sdk
    revitYears = $years
    addin = $payloads
    install = "powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1"
}
[System.IO.File]::WriteAllText((Join-Path $stage "release.json"), (($release | ConvertTo-Json -Depth 4) + "`n"), (New-Object System.Text.UTF8Encoding($false)))

$stageRoot = (Resolve-Path -LiteralPath $stage).Path.TrimEnd('\')
$sums = Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.FullName.Substring($stageRoot.Length + 1).Replace('\', '/'))"
}
[System.IO.File]::WriteAllLines((Join-Path $stage "checksums.sha256"), [string[]] $sums)

# ------------------------------------------------------------------------------------------------ zip
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$zip.sha256", "$zipHash  $name.zip`n", (New-Object System.Text.UTF8Encoding($false)))
if (-not $KeepStaging) { Remove-Item -LiteralPath $stage -Recurse -Force }
$sizeMb = [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
Say "wrote $zip ($sizeMb MB, sha256 $zipHash; $($sums.Count) files)"
