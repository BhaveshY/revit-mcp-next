param(
    [ValidateSet("all", "claude-code", "claude-desktop", "codex")]
    [string] $Client = "all",
    [string] $InstallRoot = "",
    [int] $TimeoutSeconds = 15,
    [switch] $SkipMcpStartup,
    [switch] $Json
)

$ErrorActionPreference = "Stop"

$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
$checks = New-Object System.Collections.Generic.List[object]

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string] $Path))
}

function Add-TrailingSeparator($Path) {
    if ($Path.EndsWith("\") -or $Path.EndsWith("/")) {
        return $Path
    }

    return "$Path\"
}

function Test-SamePath($Left, $Right) {
    return [string]::Equals((Get-FullPath $Left), (Get-FullPath $Right), [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-InstallRoot {
    if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) {
        return Get-FullPath $InstallRoot
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:REVIT_MCP_NEXT_INSTALL_ROOT)) {
        $candidates.Add($env:REVIT_MCP_NEXT_INSTALL_ROOT) | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        $candidates.Add((Join-Path $env:APPDATA "Autodesk\Revit\Addins\2024\RevitMcpNext")) | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA "RevitMcpNext")) | Out-Null
    }

    foreach ($candidate in $candidates) {
        $full = Get-FullPath $candidate
        if (Test-Path -LiteralPath (Join-Path $full "config\client-discovery.json") -PathType Leaf) {
            return $full
        }
    }

    if ($candidates.Count -gt 0) {
        return Get-FullPath $candidates[$candidates.Count - 1]
    }

    throw "Could not infer an install root. Pass -InstallRoot with the installed Revit MCP Next root."
}

function Test-PathChild($Root, $Path) {
    $rootFull = Get-FullPath $Root
    $pathFull = Get-FullPath $Path
    $rootWithSeparator = Add-TrailingSeparator $rootFull

    return $pathFull -eq $rootFull -or $pathFull.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
}

function Escape-TomlBasicString($Value) {
    return ([string] $Value).Replace("\", "\\").Replace('"', '\"')
}

function Write-Check($Status, $Message, $Details = "") {
    $checks.Add([ordered] @{
        status = $Status
        message = $Message
        details = $Details
    }) | Out-Null

    if (-not $Json) {
        if ([string]::IsNullOrWhiteSpace($Details)) {
            Write-Host "[$Status] $Message"
        } else {
            Write-Host "[$Status] $Message - $Details"
        }
    }
}

function Add-Failure($Message) {
    $failures.Add($Message) | Out-Null
    Write-Check "missing" $Message
}

function Add-Warning($Message) {
    $warnings.Add($Message) | Out-Null
    Write-Check "warn" $Message
}

function Test-RequiredFile($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Check "ok" $Label $Path
        return $true
    }

    Add-Failure "$Label missing: $Path"
    return $false
}

function Test-RequiredDirectory($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Container) {
        Write-Check "ok" $Label $Path
        return $true
    }

    Add-Failure "$Label missing: $Path"
    return $false
}

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Read-AuthTokenConfig($Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ""
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*"?([^"\s]+)"?\s*$') {
            return $Matches[1]
        }
    }

    return ""
}

function Test-AuthTokenShape($Token) {
    return -not [string]::IsNullOrWhiteSpace($Token) -and $Token -match "^[A-Za-z0-9_-]{43,}$"
}

function Assert-NoTokenLeak($Text, $Label, $Token) {
    if ([string]::IsNullOrWhiteSpace($Token)) {
        return
    }

    if (($Text | Out-String).Contains($Token)) {
        Add-Failure "$Label leaks the raw MCP auth token"
    } else {
        Write-Check "ok" "$Label does not contain the raw MCP auth token"
    }
}

function Test-RuleGrantsRead($Rule) {
    $rights = $Rule.FileSystemRights
    foreach ($right in @(
        [System.Security.AccessControl.FileSystemRights]::ReadData,
        [System.Security.AccessControl.FileSystemRights]::Read,
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [System.Security.AccessControl.FileSystemRights]::Modify,
        [System.Security.AccessControl.FileSystemRights]::FullControl
    )) {
        if (($rights -band $right) -eq $right) {
            return $true
        }
    }

    return $false
}

function Test-AuthConfigAcl($Path) {
    try {
        $acl = Get-Acl -LiteralPath $Path
        $broadSids = @(
            "S-1-1-0",
            "S-1-5-11",
            "S-1-5-32-545"
        )
        $broadRules = New-Object System.Collections.Generic.List[string]

        if (-not $acl.AreAccessRulesProtected) {
            $broadRules.Add("inherits ACLs") | Out-Null
        }

        foreach ($rule in $acl.Access) {
            if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) {
                continue
            }
            if (-not (Test-RuleGrantsRead $rule)) {
                continue
            }

            try {
                $sid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            } catch {
                $sid = ""
            }

            if ($broadSids -contains $sid) {
                $broadRules.Add("grants read access to $($rule.IdentityReference.Value)") | Out-Null
            }
        }

        if ($broadRules.Count -eq 0) {
            Write-Check "ok" "auth token config ACL is restricted"
        } else {
            Add-Warning ("auth token config ACL may be broad: " + ($broadRules -join "; "))
        }
    } catch {
        Add-Warning "auth token config ACL could not be inspected: $($_.Exception.Message)"
    }
}

function Get-GeneratedConfigPayload($Root) {
    $printer = Join-Path $PSScriptRoot "print-mcp-config.ps1"
    if (-not (Test-Path -LiteralPath $printer -PathType Leaf)) {
        throw "MCP config printer was not found: $printer"
    }

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $printer -Client $Client -InstallRoot $Root -Json 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String)
    if ($exitCode -ne 0) {
        throw "print-mcp-config.ps1 failed with exit code $exitCode. $text"
    }

    return $text | ConvertFrom-Json
}

function Test-GeneratedConfig($Payload, $LauncherPath, $Token) {
    $payloadText = $Payload | ConvertTo-Json -Depth 12
    Assert-NoTokenLeak $payloadText "generated MCP client config payload" $Token

    if (-not (Test-SamePath ([string] $Payload.launcherPath) $LauncherPath)) {
        Add-Failure "generated config launcherPath does not match client discovery launcherPath"
    } else {
        Write-Check "ok" "generated config launcherPath matches client discovery" $LauncherPath
    }

    if ($Client -eq "all" -or $Client -eq "claude-code") {
        $claudeCode = [string] $Payload.claudeCode
        if ($claudeCode.Contains("cmd /c") -and $claudeCode.Contains("`"$LauncherPath`"")) {
            Write-Check "ok" "Claude Code command quotes the installed launcher"
        } else {
            Add-Failure "Claude Code command does not quote the installed launcher path"
        }
        Assert-NoTokenLeak $claudeCode "Claude Code command" $Token
    }

    if ($Client -eq "all" -or $Client -eq "claude-desktop") {
        $server = $Payload.claudeDesktop.mcpServers.'revit-mcp-next'
        if ($server -and [string] $server.command -eq "cmd" -and $server.args.Count -ge 2 -and [string] $server.args[0] -eq "/c" -and (Test-SamePath ([string] $server.args[1]) $LauncherPath)) {
            Write-Check "ok" "Claude Desktop JSON points at the installed launcher"
        } else {
            Add-Failure "Claude Desktop JSON does not point at the installed launcher"
        }
        Assert-NoTokenLeak ($Payload.claudeDesktop | ConvertTo-Json -Depth 8) "Claude Desktop JSON" $Token
    }

    if ($Client -eq "all" -or $Client -eq "codex") {
        $codexToml = [string] $Payload.codexToml
        $tomlLauncher = Escape-TomlBasicString $LauncherPath
        if ($codexToml.Contains('command = "cmd"') -and $codexToml.Contains('args = ["/c", "') -and $codexToml.Contains($tomlLauncher)) {
            Write-Check "ok" "Codex TOML points at the installed launcher"
        } else {
            Add-Failure "Codex TOML does not point at the installed launcher"
        }
        Assert-NoTokenLeak $codexToml "Codex TOML" $Token
    }
}

function Test-ClaudeDesktopConfigFile($Path, $LauncherPath, $Token) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Check "info" "Claude Desktop config not found" $Path
        return
    }

    $text = Get-Content -LiteralPath $Path -Raw
    Assert-NoTokenLeak $text "Claude Desktop config file" $Token
    try {
        $config = $text | ConvertFrom-Json
        $server = $config.mcpServers.'revit-mcp-next'
        if (-not $server) {
            Write-Check "info" "Claude Desktop config exists but has no revit-mcp-next server" $Path
            return
        }

        if ([string] $server.command -eq "cmd" -and $server.args.Count -ge 2 -and [string] $server.args[0] -eq "/c" -and (Test-SamePath ([string] $server.args[1]) $LauncherPath)) {
            Write-Check "ok" "Claude Desktop installed config uses the generated launcher" $Path
        } else {
            Add-Failure "Claude Desktop installed config has a stale or unquoted revit-mcp-next launcher: $Path"
        }
    } catch {
        Add-Warning "Claude Desktop config could not be parsed as JSON: $Path"
    }
}

function Test-CodexConfigFile($Path, $LauncherPath, $Token) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Check "info" "Codex config not found" $Path
        return
    }

    $text = Get-Content -LiteralPath $Path -Raw
    Assert-NoTokenLeak $text "Codex config file" $Token
    $match = [regex]::Match($text, '(?ms)^\s*\[mcp_servers\.revit-mcp-next\]\s*(?<body>.*?)(?=^\s*\[|\z)')
    if (-not $match.Success) {
        Write-Check "info" "Codex config exists but has no revit-mcp-next server" $Path
        return
    }

    $body = $match.Groups["body"].Value
    $tomlLauncher = Escape-TomlBasicString $LauncherPath
    if ($body.Contains('command = "cmd"') -and $body.Contains("/c") -and ($body.Contains($LauncherPath) -or $body.Contains($tomlLauncher))) {
        Write-Check "ok" "Codex installed config uses the generated launcher" $Path
    } else {
        Add-Failure "Codex installed config has a stale or unquoted revit-mcp-next launcher: $Path"
    }
}

function Invoke-McpToolListSmoke($LauncherPath, $ExpectedTools, $TimeoutSeconds) {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) {
        throw "node.exe was not found on PATH, so the MCP startup probe could not run."
    }

    $probeRoot = Join-Path $env:TEMP ("revit-mcp-next-client-doctor-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    $probeScript = Join-Path $probeRoot "mcp-tool-list-probe.js"
    $expectedToolsPath = Join-Path $probeRoot "expected-tools.json"
    $probeSource = @'
const fs = require("node:fs");
const { spawn } = require("node:child_process");

const launcherPath = process.argv[2];
const expectedTools = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));
const timeoutMs = Number(process.argv[4] || "15000");
const comspec = process.env.ComSpec || "cmd.exe";
const child = spawn(comspec, ["/d", "/c", launcherPath], {
  stdio: ["pipe", "pipe", "pipe"],
  windowsHide: true,
});

let stdoutBuffer = "";
let stderrText = "";
const pending = new Map();

function fail(error) {
  try {
    child.kill();
  } catch {
  }
  const message = error && error.message ? error.message : String(error);
  process.stderr.write(message + "\n");
  if (stderrText.trim()) {
    process.stderr.write(stderrText.trim() + "\n");
  }
  process.exit(1);
}

function send(message) {
  child.stdin.write(JSON.stringify(message) + "\n", "utf8");
}

function waitFor(id) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Timed out waiting for MCP response id ${id}.`));
    }, timeoutMs);
    pending.set(id, { resolve, reject, timer });
  });
}

child.stderr.on("data", (chunk) => {
  stderrText += chunk.toString("utf8");
  if (stderrText.length > 16000) {
    stderrText = stderrText.slice(-16000);
  }
});

child.stdout.on("data", (chunk) => {
  stdoutBuffer += chunk.toString("utf8");
  let newlineIndex;
  while ((newlineIndex = stdoutBuffer.indexOf("\n")) >= 0) {
    const line = stdoutBuffer.slice(0, newlineIndex).trim();
    stdoutBuffer = stdoutBuffer.slice(newlineIndex + 1);
    if (!line) continue;

    let message;
    try {
      message = JSON.parse(line);
    } catch {
      fail(new Error(`MCP launcher wrote non-JSON stdout: ${line}`));
      return;
    }

    const waiter = pending.get(message.id);
    if (!waiter) continue;
    clearTimeout(waiter.timer);
    pending.delete(message.id);
    if (message.error) {
      waiter.reject(new Error(`MCP response id ${message.id} returned error ${message.error.code}: ${message.error.message}`));
    } else {
      waiter.resolve(message.result || {});
    }
  }
});

child.on("close", (code) => {
  for (const [id, waiter] of pending.entries()) {
    clearTimeout(waiter.timer);
    waiter.reject(new Error(`MCP launcher exited with code ${code} before response id ${id}.`));
  }
  pending.clear();
});

(async () => {
  send({
    jsonrpc: "2.0",
    id: 1,
    method: "initialize",
    params: {
      protocolVersion: "2025-11-25",
      capabilities: {},
      clientInfo: { name: "revit-mcp-next-client-doctor", version: "0.1.0" },
    },
  });
  const initialize = await waitFor(1);
  if (!initialize.protocolVersion) {
    throw new Error("MCP initialize response did not include protocolVersion.");
  }

  send({ jsonrpc: "2.0", method: "notifications/initialized", params: {} });
  send({ jsonrpc: "2.0", id: 2, method: "tools/list", params: {} });
  const toolList = await waitFor(2);
  const toolNames = (toolList.tools || []).map((tool) => tool && tool.name).filter(Boolean);
  if (toolNames.length === 0) {
    throw new Error("MCP tools/list returned no tools.");
  }

  const missing = expectedTools.filter((name) => !toolNames.includes(name));
  if (missing.length > 0) {
    throw new Error(`MCP tools/list is missing expected discovery tools: ${missing.join(", ")}`);
  }

  child.stdin.end();
  const killTimer = setTimeout(() => {
    try {
      child.kill();
    } catch {
    }
  }, 1000);
  child.on("exit", () => clearTimeout(killTimer));
  process.stdout.write(JSON.stringify({ toolCount: toolNames.length, tools: toolNames }) + "\n");
})().catch(fail);
'@

    try {
        New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null
        Set-Content -LiteralPath $probeScript -Value $probeSource -Encoding ASCII
        Set-Content -LiteralPath $expectedToolsPath -Value ($ExpectedTools | ConvertTo-Json -Compress) -Encoding ASCII

        $output = & $node.Source $probeScript $LauncherPath $expectedToolsPath ([Math]::Max(1, $TimeoutSeconds) * 1000) 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String).Trim()
        if ($exitCode -ne 0) {
            throw $text
        }

        $summaryLine = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_) })[-1]
        $summary = $summaryLine | ConvertFrom-Json
        Write-Check "ok" "MCP startup and tools/list succeeded without requiring Revit connection" "$($summary.toolCount) tool(s)"
    } finally {
        if ((Test-Path -LiteralPath $probeRoot -PathType Container) -and $probeRoot.StartsWith($env:TEMP, [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$installRootFull = Resolve-InstallRoot
if (-not $Json) {
    Write-Host "[revit-mcp-next client doctor] Install root: $installRootFull"
}

$discoveryPath = Join-Path $installRootFull "config\client-discovery.json"
$discovery = $null
$token = ""
$launcherPath = ""

if (Test-RequiredFile $discoveryPath "client discovery config") {
    $discoveryText = Get-Content -LiteralPath $discoveryPath -Raw
    $discovery = $discoveryText | ConvertFrom-Json

    if (Test-SamePath ([string] $discovery.installRoot) $installRootFull) {
        Write-Check "ok" "client discovery installRoot matches requested install root"
    } else {
        Add-Failure "client discovery installRoot is stale. Expected $installRootFull, found $($discovery.installRoot)"
    }

    $launcherPath = Get-FullPath ([string] $discovery.launcherPath)
    $revitCtlPath = Get-FullPath ([string] $discovery.revitctlPath)
    $authConfigPath = Get-FullPath ([string] $discovery.authConfigPath)
    $brokerEntryPath = Get-FullPath ([string] $discovery.brokerEntryPath)
    $pythonClientPath = Get-FullPath ([string] $discovery.pythonClientPath)
    $pythonInProcessPath = Get-FullPath ([string] $discovery.pythonInProcessHelperPath)
    $schemasPath = Get-FullPath ([string] $discovery.contractSchemasPath)

    Test-RequiredFile $launcherPath "MCP launcher" | Out-Null
    Test-RequiredFile $revitCtlPath "revitctl launcher" | Out-Null
    Test-RequiredFile $authConfigPath "auth token config" | Out-Null
    Test-RequiredFile $brokerEntryPath "broker entry" | Out-Null
    Test-RequiredFile $pythonClientPath "Python stdio client helper" | Out-Null
    Test-RequiredFile $pythonInProcessPath "Python in-process helper" | Out-Null
    Test-RequiredDirectory $schemasPath "contract schemas directory" | Out-Null

    foreach ($pathCheck in @(
        @{ Path = $launcherPath; Label = "launcherPath" },
        @{ Path = $revitCtlPath; Label = "revitctlPath" },
        @{ Path = $authConfigPath; Label = "authConfigPath" },
        @{ Path = $brokerEntryPath; Label = "brokerEntryPath" },
        @{ Path = $pythonClientPath; Label = "pythonClientPath" },
        @{ Path = $pythonInProcessPath; Label = "pythonInProcessHelperPath" },
        @{ Path = $schemasPath; Label = "contractSchemasPath" }
    )) {
        if (Test-PathChild $installRootFull $pathCheck.Path) {
            Write-Check "ok" "client discovery $($pathCheck.Label) stays under install root"
        } else {
            Add-Failure "client discovery $($pathCheck.Label) points outside install root: $($pathCheck.Path)"
        }
    }

    $supportedYears = @()
    if ($discovery.PSObject.Properties["supportedRevitYears"] -and $null -ne $discovery.supportedRevitYears) {
        $supportedYears = @($discovery.supportedRevitYears | ForEach-Object { [int] $_ })
    }
    $unsupportedYears = @($supportedYears | Where-Object { $_ -ne 2024 })
    if ($supportedYears.Count -gt 0 -and $unsupportedYears.Count -eq 0) {
        Write-Check "ok" "client discovery remains Revit 2024-only"
    } else {
        Add-Failure "client discovery advertises unsupported Revit years: $($supportedYears -join ', ')"
    }

    $token = Read-AuthTokenConfig $authConfigPath
    if (Test-AuthTokenShape $token) {
        Write-Check "ok" "auth token config contains a strong token (redacted)"
    } else {
        Add-Failure "auth token config is missing a strong token: $authConfigPath"
    }
    Test-AuthConfigAcl $authConfigPath
    Assert-NoTokenLeak $discoveryText "client discovery config" $token

    if (Test-Path -LiteralPath $launcherPath -PathType Leaf) {
        $launcherText = Get-Content -LiteralPath $launcherPath -Raw
        Assert-NoTokenLeak $launcherText "MCP launcher" $token
        if ($launcherText.Contains("set `"REVIT_MCP_NEXT_AUTH_CONFIG=$authConfigPath`"")) {
            Write-Check "ok" "MCP launcher uses quoted auth config assignment"
        } else {
            Add-Failure "MCP launcher does not use the expected quoted auth config path"
        }
        if ($launcherText.Contains("`"$brokerEntryPath`"")) {
            Write-Check "ok" "MCP launcher quotes the staged broker entry"
        } else {
            Add-Failure "MCP launcher does not quote the staged broker entry"
        }
    }

    if (Test-Path -LiteralPath $revitCtlPath -PathType Leaf) {
        $revitCtlText = Get-Content -LiteralPath $revitCtlPath -Raw
        Assert-NoTokenLeak $revitCtlText "revitctl launcher" $token
        if ($revitCtlText.Contains("`"$authConfigPath`"") -and $revitCtlText.Contains("`"$discoveryPath`"")) {
            Write-Check "ok" "revitctl launcher quotes installed discovery and auth config"
        } else {
            Add-Failure "revitctl launcher does not quote the installed discovery/auth config paths"
        }
    }

    try {
        $payload = Get-GeneratedConfigPayload $installRootFull
        Test-GeneratedConfig $payload $launcherPath $token
    } catch {
        Add-Failure "generated client config could not be produced: $($_.Exception.Message)"
    }

    if (-not [string]::IsNullOrWhiteSpace($env:REVIT_MCP_NEXT_AUTH_TOKEN)) {
        Add-Warning "REVIT_MCP_NEXT_AUTH_TOKEN is set in the current environment; generated client configs should rely on the launcher instead"
    }

    if ($Client -eq "all" -or $Client -eq "claude-desktop") {
        if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
            Test-ClaudeDesktopConfigFile (Join-Path $env:APPDATA "Claude\claude_desktop_config.json") $launcherPath $token
        } else {
            Write-Check "info" "APPDATA is not set; skipping Claude Desktop config file inspection"
        }
    }

    if ($Client -eq "all" -or $Client -eq "codex") {
        $codexCandidates = New-Object System.Collections.Generic.List[string]
        if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
            $codexCandidates.Add((Join-Path $env:CODEX_HOME "config.toml")) | Out-Null
        }
        if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
            $codexCandidates.Add((Join-Path $env:USERPROFILE ".codex\config.toml")) | Out-Null
        }
        if ($codexCandidates.Count -eq 0) {
            Write-Check "info" "CODEX_HOME and USERPROFILE are not set; skipping Codex config file inspection"
        } else {
            $seenCodex = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($candidate in $codexCandidates) {
                $fullCandidate = Get-FullPath $candidate
                if ($seenCodex.Add($fullCandidate)) {
                    Test-CodexConfigFile $fullCandidate $launcherPath $token
                }
            }
        }
    }

    if ($SkipMcpStartup) {
        Write-Check "info" "MCP startup smoke skipped by request"
    } elseif (Test-Path -LiteralPath $launcherPath -PathType Leaf) {
        try {
            Invoke-McpToolListSmoke $launcherPath @($discovery.tools | ForEach-Object { [string] $_ }) $TimeoutSeconds
        } catch {
            Add-Failure "MCP startup/tools-list smoke failed: $($_.Exception.Message)"
        }
    }
}

$status = if ($failures.Count -gt 0) { "failed" } else { "ok" }
$summary = [ordered] @{
    schemaVersion = 1
    status = $status
    installRoot = $installRootFull
    client = $Client
    checkedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    checks = $checks
    warnings = $warnings
    failures = $failures
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 8
} elseif ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "[revit-mcp-next client doctor] FAILED"
    $failures | ForEach-Object { Write-Host " - $_" }
} else {
    Write-Host "[revit-mcp-next client doctor] OK"
}

if ($failures.Count -gt 0) {
    exit 1
}
