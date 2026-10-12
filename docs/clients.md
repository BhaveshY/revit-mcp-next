# Clients: Codex, Claude Code, Claude Desktop

Every client runs the same MCP server, named `revit`. The client starts it as a stdio process:

```
<home>\runtime\node.exe <home>\broker\revit-mcp.mjs        env REVIT_MCP_NEXT_HOME=<home>
```

`<home>` is `%USERPROFILE%\.revit-mcp-next` unless you installed with `-Home`. The installer
(`installer\install.ps1`) writes every registration below and backs up each file it edits
(`<file>.revit-mcp-next-<time>.bak`). To see the exact text for your home without changing anything:

```powershell
$rmn = "$env:USERPROFILE\.revit-mcp-next"
& "$rmn\runtime\node.exe" "$rmn\broker\revit-mcp-cli.mjs" clients show
```

`clients install --clients codex,claude` and `clients uninstall` do the client part of the installer on their own
(add `--dry-run` to preview).

## Codex (CLI 0.153+, desktop app 0.159)

The installer replaces any old `[mcp_servers.revit-mcp-next]` block and writes this to `~\.codex\config.toml`
(or `%CODEX_HOME%\config.toml`):

```toml
# managed by revit-mcp-next installer <version> (<sha>); rerun the installer instead of editing
[mcp_servers.revit]
command = 'C:\Users\<you>\.revit-mcp-next\runtime\node.exe'
args = ['C:\Users\<you>\.revit-mcp-next\broker\revit-mcp.mjs']
startup_timeout_sec = 30
tool_timeout_sec = 120
required = true

[mcp_servers.revit.env]
REVIT_MCP_NEXT_HOME = 'C:\Users\<you>\.revit-mcp-next'

[mcp_servers.revit.tools.status]
approval_mode = "auto"
[mcp_servers.revit.tools.help]
approval_mode = "auto"
[mcp_servers.revit.tools.set_target]
approval_mode = "auto"
[mcp_servers.revit.tools.ui]
approval_mode = "auto"
```

- `required = true` makes Codex report a failed start instead of silently running without Revit tools.
- `tool_timeout_sec = 120` is above the broker's own 50 s call budget, so the broker always answers first (long work
  continues as a job, `job_status`).
- It also adds the `revit-mcp-next` plugin (the `revit` and `revit-setup` skills) from `<home>\plugins` and a short
  Revit rule block in `~\.codex\AGENTS.md` between `<!-- revit-mcp-next:begin ... -->` and `<!-- revit-mcp-next:end -->`.
  `-NoAgentsSnippet` skips the AGENTS.md edit; `-PluginClients none` skips the plugin.

**Approval modes.** Only the four tools above are auto-approved: `status`, `help` and `set_target` never change the
model and `ui` only changes the Revit window (selection, zoom, active view). Every other tool keeps your Codex approval
policy, so writes and file operations ask when your policy says so. Codex 0.153 accepts `auto`, `prompt`, `writes` and
`approve` for `approval_mode`; change a tool's mode by adding its own `[mcp_servers.revit.tools.<tool>]` table (the
installer rewrites only its own block, so put your additions after it or rerun the installer afterwards).

**Restart after installing.** Codex loads MCP servers when a session or thread starts. CLI: start a new session.
Desktop app: quit it completely (also from the tray) and reopen it; a new thread in a running app may still use the old
server list. Check with `codex mcp list` and `codex mcp get revit --json`.

**Do not install from inside the Codex app.** Its terminal runs inside an MSIX package, where writes to `%APPDATA%` and
`HKCU` are virtualized and Revit would never see the add-in. The installer refuses there; `-Relaunch` starts a copy
outside the package. The same applies to launching Revit: start it yourself, never from an AI app's terminal (Autodesk
licensing fails inside the app's process tree).

## Claude Code

```powershell
claude mcp add-json revit '{"type":"stdio","command":"C:\\Users\\<you>\\.revit-mcp-next\\runtime\\node.exe","args":["C:\\Users\\<you>\\.revit-mcp-next\\broker\\revit-mcp.mjs"],"env":{"REVIT_MCP_NEXT_HOME":"C:\\Users\\<you>\\.revit-mcp-next"}}' -s user
claude plugin marketplace add C:\Users\<you>\.revit-mcp-next\plugins
claude plugin install revit-mcp-next@revit-mcp-next -s user
```

The installer runs these (user scope, so every project sees the server). Restart Claude Code, then `/mcp` shows
`revit`; `claude mcp get revit` should say connected. Claude Code asks before each tool call according to its own
permission settings.

## Claude Desktop

Install with `-Clients codex,claude,claude-desktop`. The installer sets `mcpServers.revit` (same command, args and env as
above) in `%APPDATA%\Claude\claude_desktop_config.json`, or in the MSIX copy under
`%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\`. Restart Claude Desktop afterwards.

## Output limits

The broker keeps results small so no client cuts them:

- Text results are capped at 12,000 bytes, or 40,000 bytes with `detail:"full"`. Longer lists are paged; the summary
  line repeats the page token ("showing 1-50 of 1,234, more p4") because Codex shortens long outputs in the middle.
- `capture` images are at most about 1.5 MB of base64 (long edge 768/1280/1568 px for small/medium/large); `read_many`
  returns at most 2 images (2 MB).
- Codex: the default `tool_output_token_limit` of 0.153/0.159 for a 40 KB `detail:"full"` result is measured by the e2e
  scenario C1 (`E2E_CODEX_EXEC=1`, see [e2e.md](e2e.md)) and has not been measured on a live Revit yet. If C1 reports
  `shortened:true`, Codex 0.153 supports a per-tool `output_token_limit` key in the same
  `[mcp_servers.revit.tools.<tool>]` table.
- Claude Code: `MAX_MCP_OUTPUT_TOKENS` limits MCP output. The large-capture check (scenario C2 with `E2E_CLAUDE_P=1`)
  is not run yet.

## Homes and the loader

A *home* is one self-contained install: runtime, broker, add-in payloads, config, logs. The broker finds its home from
`REVIT_MCP_NEXT_HOME`, else from the folder it is installed in (marker `.revit-mcp-next-home`), else
`%USERPROFILE%\.revit-mcp-next`. Homes are never under AppData, Temp or an app package.

Revit loads `RevitMcpNext.Loader.dll`, which the manifest `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin`
points to. The loader picks the home from `REVIT_MCP_NEXT_HOME` in Revit's environment, else from its own location, and
loads that home's current payload (`addin\<year>\current.json`). So several homes can share one machine:

| Home | Used by |
|---|---|
| `%USERPROFILE%\.revit-mcp-next` | the installed product (your normal Revit sessions) |
| `%USERPROFILE%\.revit-mcp-next-e2e` | the e2e harness (Revit started by the harness with `REVIT_MCP_NEXT_HOME` set) |
| `%USERPROFILE%\.revit-mcp-next-dev-<name>` | developer builds (`scripts\dev-install.ps1`) |

A broker only sees the Revit processes registered in its own home. The doctor flags a *split install* (the manifest
points at another home's loader) and a user-level `REVIT_MCP_NEXT_HOME` variable that redirects Revit.

## Profiles and switches

| Environment variable | Effect |
|---|---|
| `REVIT_MCP_NEXT_PROFILE=core` | 17 tools instead of 37 (`help {"topic":"profiles"}` lists them); for clients with small tool budgets |
| `REVIT_MCP_NEXT_STRUCTURED=1` | Adds `structuredContent` to non-image results (off by default) |

`run_csharp` is listed only when `enableCodeExecution` was true in `<home>\config\settings.json` when the broker
started; see [SECURITY.md](../SECURITY.md).

## Conflicting plugins and servers

Use one Revit MCP server per client. The doctor warns about other servers whose name contains "revit" and about old
`revit-mcp-next` blocks.

**revit-mcp-cowork.** This plugin drives a different Revit server, and its hook answers *allow* for the revit tool calls
it sees. While it is enabled, Claude Code and Codex run our destructive tools (deletes, purges,
syncs, overwrites) **without asking you**. The installer reports it; `-DisableConflictingPlugins` disables it in Codex
(the uninstaller re-enables it). In Claude Code it is only reported: disable it yourself with
`claude plugin disable revit-mcp-cowork@<marketplace>` if you want approval prompts for revit tools.
