param(
    [ValidateSet("all", "claude-code", "claude-desktop", "codex")]
    [string] $Client = "all",
    [string] $InstallRoot = "",
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Resolve-InstallRoot {
    if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) {
        return Get-FullPath $InstallRoot
    }

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw "LOCALAPPDATA is not set. Pass -InstallRoot with the installed Revit MCP Next root."
    }

    return Get-FullPath (Join-Path $env:LOCALAPPDATA "RevitMcpNext")
}

function Read-ClientDiscovery($Root) {
    $discoveryPath = Join-Path $Root "config\client-discovery.json"
    if (-not (Test-Path -LiteralPath $discoveryPath -PathType Leaf)) {
        throw "Client discovery config was not found: $discoveryPath. Run the Windows installer first or pass -InstallRoot."
    }

    $discovery = Get-Content -LiteralPath $discoveryPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string] $discovery.launcherPath)) {
        throw "Client discovery config does not contain launcherPath: $discoveryPath"
    }

    return $discovery
}

function Escape-TomlBasicString($Value) {
    return $Value.Replace("\", "\\").Replace('"', '\"')
}

function New-ClaudeDesktopConfig($LauncherPath) {
    return [ordered] @{
        mcpServers = [ordered] @{
            "revit-mcp-next" = [ordered] @{
                command = "cmd"
                args = @("/c", $LauncherPath)
            }
        }
    }
}

function New-ConfigPayload($Discovery) {
    $launcherPath = Get-FullPath ([string] $Discovery.launcherPath)
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        throw "Configured launcher was not found: $launcherPath"
    }

    $claudeCode = "claude mcp add --scope user revit-mcp-next -- cmd /c `"$launcherPath`""
    $claudeDesktop = New-ClaudeDesktopConfig $launcherPath
    $codexToml = @(
        "[mcp_servers.revit-mcp-next]",
        "command = `"cmd`"",
        "args = [`"/c`", `"$(Escape-TomlBasicString $launcherPath)`"]"
    ) -join [Environment]::NewLine

    return [ordered] @{
        product = "revit-mcp-next"
        version = [string] $Discovery.version
        installRoot = [string] $Discovery.installRoot
        launcherPath = $launcherPath
        claudeCode = $claudeCode
        claudeDesktop = $claudeDesktop
        codexToml = $codexToml
    }
}

function Write-TextConfig($Payload) {
    if ($Client -eq "all" -or $Client -eq "claude-code") {
        Write-Host "Claude Code:"
        Write-Host $Payload.claudeCode
        Write-Host ""
    }

    if ($Client -eq "all" -or $Client -eq "claude-desktop") {
        Write-Host "Claude Desktop claude_desktop_config.json entry:"
        $Payload.claudeDesktop | ConvertTo-Json -Depth 6
        Write-Host ""
    }

    if ($Client -eq "all" -or $Client -eq "codex") {
        Write-Host "Codex config.toml entry:"
        Write-Host $Payload.codexToml
        Write-Host ""
    }
}

$installRootFull = Resolve-InstallRoot
$discovery = Read-ClientDiscovery $installRootFull
$payload = New-ConfigPayload $discovery

if ($Json) {
    $payload | ConvertTo-Json -Depth 8
} else {
    Write-TextConfig $payload
}
