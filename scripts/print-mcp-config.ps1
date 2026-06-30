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

function Add-TrailingSeparator($Path) {
    if ($Path.EndsWith("\") -or $Path.EndsWith("/")) {
        return $Path
    }

    return "$Path\"
}

function Test-SamePath($Left, $Right) {
    return [string]::Equals((Get-FullPath $Left), (Get-FullPath $Right), [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathChild($Root, $Path) {
    $rootFull = Get-FullPath $Root
    $pathFull = Get-FullPath $Path
    $rootWithSeparator = Add-TrailingSeparator $rootFull

    return $pathFull -eq $rootFull -or $pathFull.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
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

function Assert-DiscoveryMatchesInstallRoot($Discovery, $Root) {
    if ([string]::IsNullOrWhiteSpace([string] $Discovery.installRoot)) {
        throw "Client discovery config does not contain installRoot."
    }

    if (-not (Test-SamePath ([string] $Discovery.installRoot) $Root)) {
        throw "Client discovery installRoot is stale. Expected $Root, found $($Discovery.installRoot)."
    }

    foreach ($entry in @(
        @{ Name = "launcherPath"; Value = [string] $Discovery.launcherPath },
        @{ Name = "authConfigPath"; Value = [string] $Discovery.authConfigPath },
        @{ Name = "brokerEntryPath"; Value = [string] $Discovery.brokerEntryPath },
        @{ Name = "pythonClientPath"; Value = [string] $Discovery.pythonClientPath },
        @{ Name = "pythonInProcessHelperPath"; Value = [string] $Discovery.pythonInProcessHelperPath },
        @{ Name = "contractSchemasPath"; Value = [string] $Discovery.contractSchemasPath },
        @{ Name = "integrationsPath"; Value = [string] $Discovery.integrationsPath }
    )) {
        if ([string]::IsNullOrWhiteSpace($entry.Value)) {
            continue
        }

        if (-not (Test-PathChild $Root $entry.Value)) {
            throw "Client discovery $($entry.Name) points outside installRoot: $($entry.Value)."
        }
    }

    $supportedYears = @()
    if ($Discovery.PSObject.Properties["supportedRevitYears"] -and $null -ne $Discovery.supportedRevitYears) {
        $supportedYears = @($Discovery.supportedRevitYears | ForEach-Object { [int] $_ })
    }
    $unsupportedYears = @($supportedYears | Where-Object { $_ -ne 2024 })
    if ($supportedYears.Count -gt 0 -and $unsupportedYears.Count -gt 0) {
        throw "Client discovery advertises unsupported Revit years: $($supportedYears -join ', '). Revit MCP Next packages are Revit 2024-only."
    }
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
Assert-DiscoveryMatchesInstallRoot $discovery $installRootFull
$payload = New-ConfigPayload $discovery

if ($Json) {
    $payload | ConvertTo-Json -Depth 8
} else {
    Write-TextConfig $payload
}
