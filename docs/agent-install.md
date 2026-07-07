# AI Agent Install Guide

Use this when an AI coding agent needs to install Revit MCP Next for Claude Code, Claude Desktop, Codex, or another MCP client on a Windows machine.

## Fast Path

Prerequisites:

- Windows with Autodesk Revit 2024 installed.
- Node.js 24.x on `PATH`.
- PowerShell.
- The target MCP client installed when configuring that client: Claude Code with the `claude` CLI, Claude Desktop, Codex, or another MCP-compatible client.
- A disposable or test `.rvt` project for the first connection test.

From an extracted release package:

```powershell
cd C:\path\to\revit-mcp-next-<version>-windows
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install-windows.ps1 -RevitYears 2024 -TrustRevitAlwaysLoad
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\print-mcp-config.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\doctor-clients.ps1
```

From a source checkout:

```powershell
npm install
npm run build
npm run build:addin
npm run install:windows -- -RevitYears 2024 -TrustRevitAlwaysLoad
npm run mcp:config
npm run doctor:clients
```

The installer writes the Revit add-in, MCP launcher, `revitctl.cmd`, integration examples, and `config\client-discovery.json` under the install root. The package installer default is:

```text
%LOCALAPPDATA%\RevitMcpNext
```

The Revit `.addin` manifest is written separately under `%APPDATA%\Autodesk\Revit\Addins\2024` and points at the installed add-in DLL.

## MCP Client Setup

Always generate client config from the installed discovery file. Do not hand-write paths unless the generator is unavailable.

Claude Code:

```powershell
npm run mcp:config -- -Client claude-code
```

Run the printed `claude mcp add ...` command.

Claude Desktop:

```powershell
npm run mcp:config -- -Client claude-desktop
```

Merge the printed `mcpServers.revit-mcp-next` JSON into `claude_desktop_config.json`, then restart Claude Desktop. Common path:

```text
%APPDATA%\Claude\claude_desktop_config.json
```

Codex:

```powershell
npm run mcp:config -- -Client codex
```

Paste the printed TOML into Codex `config.toml`, then restart Codex. Common paths:

```text
%CODEX_HOME%\config.toml
%USERPROFILE%\.codex\config.toml
```

If npm is unavailable but the package is extracted, run the script directly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\print-mcp-config.ps1 -Client all
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\print-mcp-config.ps1 -Client claude-code
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\print-mcp-config.ps1 -Client claude-desktop
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\print-mcp-config.ps1 -Client codex
```

## First Connection Check

1. Open Revit 2024 with a disposable `.rvt` project.
2. If Revit shows an unsigned or unknown-publisher add-in prompt, only choose the trust/load option when the user confirms the package source and checksum.
3. In the MCP client, call `revit.status`.
4. Then call `revit.read_bundle` for compact model context.

Debug outside the MCP client:

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" status --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" doctor --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" read-bundle --payload '{"include":{"modelContext":true,"warnings":true},"currentViewElements":{"limit":5}}' --pretty
```

For custom `-InstallRoot` installs, use the `revitctlPath` printed by `print-mcp-config.ps1` instead of the default-root examples above.

If `revitctl status` returns `BRIDGE_UNAVAILABLE`, Revit is closed, the add-in did not load, the auth config does not match, or the named pipe is not listening.

## Agent Operating Rules

- Keep the MCP launcher as the primary interface. Use `revitctl.cmd` only for diagnostics, support, or scripted smoke checks.
- Never copy or print `config\auth.env`; the launcher reads it for you.
- Start most workflows with `revit.status` or `revit.read_bundle`.
- Use compact reads first. Prefer paginated tools, explicit limits, and `preset: "geometrySummary"` or `preset: "writableEdit"` when appropriate.
- For writes, call `revit.preview_change_set` first, show the user the intended changes when needed, then call `revit.apply_change_set` with the exact `previewId`, `baseGeneration`, `changeSetHash`, and expiry metadata.
- Treat preview tokens as single-use and short-lived.
- Use disposable/test models for first install and write smoke.
- Do not automate Windows/Revit security prompts unless the user has explicitly authorized that exact prompt and source.

## pyRevit And Dynamo

pyRevit and Dynamo scripts run inside Revit and use the packaged in-process helper instead of the external MCP stdio path. After install, examples are staged under:

```text
%LOCALAPPDATA%\RevitMcpNext\integrations
```

Useful checks:

```powershell
npm run smoke:pyrevit-host -- -RevitYear 2024
npm run smoke:dynamo-host -- -RevitYear 2024 -RequireWarmedDynamo
```

For release-candidate proof, prefer the aggregate hosted integration smoke:

```powershell
npm run smoke:host-integrations -- -RevitYear 2024 -SeedPyRevitHosts -RequireWarmedDynamoForDynamo
```

Dynamo graphs opened from untrusted folders can trigger a Dynamo trust prompt. The host-smoke tooling stages the graph under a trusted Dynamo location when one is available.

## Troubleshooting

Run:

```powershell
npm run doctor:windows
npm run doctor:clients
npm run support:bundle
```

Common fixes:

- `PROTOCOL_VERSION_MISMATCH`: rebuild, repackage, and reinstall so broker and add-in contracts match.
- `REVIT_EXTERNAL_EVENT_TIMEOUT`: bring Revit forward, close ordinary Revit modals, wait for Revit to become idle, then retry. If still stuck, close Revit and reopen the disposable model.
- Repeated Revit add-in prompt: use a signed/trusted package for durable no-prompt behavior; `-TrustRevitAlwaysLoad` is supplemental and not a public signing substitute.
- Wrong install root in client config: rerun `npm run mcp:config` or pass `-InstallRoot` to `scripts\print-mcp-config.ps1`.

More detail:

- [External preview sharing](external-preview.md)
- [Agent workflows](agent-workflows.md)
- [Troubleshooting](troubleshooting.md)
- [Release packaging](release-packaging.md)
