# revit-mcp-next

An MCP server that lets Codex and Claude read, check and change Autodesk Revit 2024 and 2027 models on Windows. It has
37 tools (walls to sheets, schedules, families, MEP, exports, visual capture and undo), compact text results built for
small models, and an exact `fix:` line on every error. Two parts work together:

- **Broker**: a Node MCP server (stdio) that Codex or Claude starts. It is one bundled file, `revit-mcp.mjs`.
- **Add-in**: a Revit add-in (a loader plus a payload) that runs the requests inside Revit over a local, token-protected
  named pipe.

Both live in one folder per Windows user, the *home*: `%USERPROFILE%\.revit-mcp-next`.

## Install on a new PC (release zip, one command)

You need Windows 10/11 x64 and Revit 2024 and/or 2027. Node, .NET and build tools are not needed: the zip contains them.

1. Get `revit-mcp-next-<version>-win-x64.zip` (and its `.sha256`). To build one from this repo: `npm run package`
   (output in `artifacts\release\`).
2. Close Revit, extract the zip, open a **normal** Windows PowerShell (not "Run as administrator", not a terminal inside
   the Codex or Claude app) in the extracted folder and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1
```

Double-clicking `install.cmd` does the same. To see what it would change first, add `-DryRun`. Useful options:

| Option | Effect |
|---|---|
| `-TrustRevitAlwaysLoad` | Seeds Revit's "Always Load" trust, so Revit does not ask about the unsigned add-in |
| `-RevitYears 2024,2027` | Which Revit versions to install for (default: the ones found on this PC) |
| `-Clients codex,claude,claude-desktop` | Which AI clients to register (default: Codex and Claude Code when present) |
| `-DisableConflictingPlugins` | Turns off the `revit-mcp-cowork` plugin in Codex (see [docs/clients.md](docs/clients.md)) |
| `-NoAgentsSnippet` | Leaves `~\.codex\AGENTS.md` alone |
| `-EnableCodeExecution` | Turns on the opt-in `run_csharp` tool (off by default; see [SECURITY.md](SECURITY.md)) |
| `-Home <dir>` | Another home folder (never under AppData or Temp) |
| `-RemoveLegacy` | Deletes old 0.2/0.3 install folders |

The installer copies the runtime, the broker and the add-in into the home, points
`%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin` at the loader, registers the `revit` server with the
clients, installs the `revit` skills plugin and finishes with a quick doctor run. Every client file it edits is backed up
first (`<file>.revit-mcp-next-<time>.bak`). Running it again upgrades in place.

Check the install at any time:

```powershell
& "$env:USERPROFILE\.revit-mcp-next\runtime\node.exe" "$env:USERPROFILE\.revit-mcp-next\broker\revit-mcp-cli.mjs" doctor
```

Uninstall: `.\installer\uninstall.ps1` (keeps `config\` and `logs\`), or `.\installer\uninstall.ps1 -Purge` (deletes the
whole home).

## Connect Codex and Claude

The installer registers the server; you only restart the client.

- **Codex** (CLI 0.153+, desktop app): quit and reopen the desktop app, or start a new CLI session. `codex mcp list` shows
  `revit`. The installer wrote `[mcp_servers.revit]` to `~\.codex\config.toml` (node + bundle, startup 30 s, tool
  120 s, `required = true`). `status`, `help`, `set_target` and `ui` run without approval; every other tool follows your
  Codex approval policy.
- **Claude Code**: restart it, then `/mcp` (or `claude mcp list`) shows `revit` as connected. The installer ran
  `claude mcp add-json revit ... -s user`.
- **Claude Desktop**: install with `-Clients codex,claude,claude-desktop` and restart Claude Desktop.

Then start Revit normally, open a model and ask: *"Use the revit MCP: call status and tell me what is open."*
The first Revit start may ask once whether to load the add-in: choose **Always Load**.

To print the exact client configuration for a manual setup:
`& "$env:USERPROFILE\.revit-mcp-next\runtime\node.exe" "$env:USERPROFILE\.revit-mcp-next\broker\revit-mcp-cli.mjs" clients show`.
Details, approval modes and output limits: [docs/clients.md](docs/clients.md).

## Run the end-to-end tests

All tests run against real Revit with throwaway models; there are no unit tests. From the extracted release folder:

```powershell
.\runtime\node.exe e2e\run.mjs --profile surface                          # no Revit, about 20 s
.\runtime\node.exe e2e\run.mjs --years 2024,2027 --launch --profile full   # starts Revit itself, about 2 h
```

The harness uses its own home (`%USERPROFILE%\.revit-mcp-next-e2e`), starts Revit outside the terminal's process tree,
creates its own projects from the English templates and writes `artifacts\e2e\<run>\summary.json`. It never opens your
files and only stops the Revit processes it started. From a repo checkout, run `npm ci` and `npm run build`, then use
`node` instead of `.\runtime\node.exe`. Profiles, gates and the test-PC checklist: [docs/e2e.md](docs/e2e.md),
[e2e/TEST-PC.md](e2e/TEST-PC.md).

## Troubleshooting

| Symptom | Do this |
|---|---|
| The `revit` tools are missing in Codex or Claude | Start a new Codex thread or restart the app; in Claude Code run `/mcp`. Then run the doctor. |
| `status` says no Revit is running | Start Revit 2024 or 2027 and open a model. If Revit is open, the doctor's `addin.<year>` lines show whether the add-in loaded. |
| Revit asks about an unsigned add-in on every start | Choose **Always Load**, or rerun the installer with `-TrustRevitAlwaysLoad`. |
| The installer refuses to run inside the Codex app | Run it from a normal Windows PowerShell, or add `-Relaunch`. |
| `REVIT_BUSY`, `REVIT_DIALOG_OPEN` or another `ERROR` | The result's `fix:` line says what to do; [docs/troubleshooting.md](docs/troubleshooting.md) lists every code. |
| Something else | `revit-mcp-cli support` writes a zip of logs and reports (no tokens) to `<home>\support\`. |

Full guide, doctor checks and all error codes: [docs/troubleshooting.md](docs/troubleshooting.md).

## Tools

The server lists 37 tools; `run_csharp` appears only after the user enabled it. `help {"tool":"<tool>","op":"<op>"}`
returns the params and a working example for any op. Full reference: [docs/tools.md](docs/tools.md).

<!-- tool-table:begin -->
| Group | Tools |
|---|---|
| Session and target | `status`, `set_target` |
| Reads | `list`, `find_elements`, `describe_elements`, `get_view`, `read_schedule`, `read_family`, `check_model`, `get_quantities`, `get_changes`, `read_many` |
| Visual | `capture` |
| Revit window (no model change) | `ui` |
| Writes | `create_elements`, `place_family`, `modify_elements`, `set_parameters`, `edit_types`, `edit_views`, `view_graphics`, `edit_sheets`, `annotate`, `edit_schedules`, `edit_family`, `mep`, `structure`, `manage_document`, `worksharing`, `links`, `export`, `model_delivery` |
| Batch, undo, jobs, help | `change_set`, `undo`, `job_status`, `cancel_job`, `help` |
| Opt-in (listed only when the user enabled it) | `run_csharp` |
<!-- tool-table:end -->

How a model works with them: start with `status`, follow `next:` and `fix:` lines exactly, and treat
`NOT APPLIED - needs the user's OK` as a question for the user (large deletes, overwrites, syncs and similar).

## Documentation

| Document | Contents |
|---|---|
| [docs/clients.md](docs/clients.md) | Codex, Claude Code and Claude Desktop setup, approval modes, output limits, homes, conflicting plugins |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Doctor checks, logs, every error code with its fix |
| [docs/architecture.md](docs/architecture.md) | Broker, add-in, loader, home layout, wire protocol, write pipeline |
| [docs/e2e.md](docs/e2e.md) | End-to-end tests, profiles and gates |
| [docs/model-delivery.md](docs/model-delivery.md) | Packaging standalone RVT deliveries with `model_delivery` |
| [docs/tools.md](docs/tools.md) | Every tool, op, parameter and example (generated) |
| [SECURITY.md](SECURITY.md) | Pipe auth, code execution, consent, audit log, dialog policy |
| [AGENTS.md](AGENTS.md), [CONTRIBUTING.md](CONTRIBUTING.md) | Building, dev installs and rules for contributors and coding agents |

## Build from source

Needs Node 24+, the .NET 10 SDK and Revit 2024/2027 installed (for the API assemblies).

```powershell
npm ci
npm run build:all     # broker, catalog, bundle, add-in for 2024 and 2027
npm run package       # release zip in artifacts\release\
```

See [AGENTS.md](AGENTS.md) for dev installs and the rules before a push.

## License

MIT. This is a clean-room implementation; see [CONTRIBUTING.md](CONTRIBUTING.md).
