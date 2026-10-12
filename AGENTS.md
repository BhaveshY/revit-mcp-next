# Agent instructions (revit-mcp-next)

For coding agents working **on this repo**. To use Revit through the MCP server, read "Using Revit" at the end and the
`revit` skill (`plugins/*/revit-mcp-next/skills/revit/SKILL.md`). User docs: [README.md](README.md).

## Repo layout

| Path | What |
|---|---|
| `contracts/` | Single source of truth: tool catalog (`src/catalog/tools/<tool>.ts`, one file per tool), op registry, help topics (`src/catalog/topics.ts`), server instructions, ErrorCatalog (`src/errors.ts`), transaction naming, wire protocol v3, home layout + settings schema (`src/home.ts`). `npm run gen:catalog` emits `artifacts/catalog/` |
| `broker/` | MCP server (`src/server.ts`, frozen `src/framework/`, `src/tools/<tool>.ts`), transport (`src/ipc`, `src/instances`), targeting, jobs, recipes, the CLI (`src/cli`: doctor, clients, settings, raw, support). `broker/build/bundle.mjs` bundles it |
| `addin/` | `RevitMcpNext.Loader` (manifest target), `RevitMcpNext.Addin` (payload: `Core`, `Runtime`, `Ipc`, `Domains/<area>`), `RevitMcpNext.Contracts`, `RevitMcpNext.Scripting` (Roslyn, opt-in) |
| `installer/` | `install.ps1`, `uninstall.ps1`, `lib/Rmn.psm1`, `agents-snippet.md` |
| `plugins/` | Codex and Claude plugins with the `revit` and `revit-setup` skills |
| `e2e/` | The only test harness (scenarios, profiles, gates); `e2e/TEST-PC.md` for the test PC |
| `scripts/` | `build-addin.ps1`, `dev-install.ps1`, `package.ps1`, `gen-tool-docs.mjs`, `validate-plugins.mjs`, host-integration helpers |
| `docs/` | User and design docs; `docs/tools.md` is generated; `docs/design/SPEC.md` is historical |
| `integrations/` | pyRevit, Dynamo and Python examples using the in-process bridge |

Legacy files excluded from the build stay until the owner approves their removal (`scripts/validate-repo.mjs`,
`broker/src/cli/revitctl.ts`, `broker/src/tools/{coreTools,discovery,pageCursor,toolResult}.ts`, the old `broker/src/ipc`
and `instances` clients, `contracts/schemas/**`, `addin/RevitMcpNext.Addin/Legacy/**`,
`.github/workflows/live-revit-smoke.yml`). Do not delete or revive them.

## Build

```powershell
npm ci
npm run build                      # contracts, broker, catalog (artifacts/catalog)
npm run typecheck
npm run gen:catalog -- --check     # catalog gates; tools/list must match
npm run build:bundle               # artifacts/broker/revit-mcp.mjs + revit-mcp-cli.mjs
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-addin.ps1 -RevitYear 2024   # net48
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-addin.ps1 -RevitYear 2027   # net10
node scripts/gen-tool-docs.mjs     # after any catalog change; --check in CI
```

- Needs Node 24+, the .NET 10 SDK and the Revit 2024/2027 API assemblies (`C:\Program Files\Autodesk\Revit <year>`,
  or `-RevitApiPath`). Both years must build with 0 errors. Never commit Autodesk DLLs.
- **dotnet.** `scripts/resolve-dotnet-sdk.ps1` finds the SDK: `-DotnetPath`, `REVIT_MCP_NEXT_DOTNET`, `DOTNET_ROOT`,
  PATH, Program Files, the user installs, and last an MSIX-virtualized copy under
  `%LOCALAPPDATA%\Packages\<app>\LocalCache\Local\Microsoft\dotnet\` (for example the one the Codex app installed). It
  prints a warning for that copy and sets `DOTNET_ROOT` for the build only. Never hard-code such a path; pass
  `-DotnetPath` when auto-detection picks the wrong SDK.
- **Revit 2027 MSB3277.** Revit 2027's API references `Microsoft.VisualBasic` 10.1 and `System.Drawing` 10.0, newer
  than the .NET 10 reference pack, so MSBuild reports MSB3277. `build-addin.ps1` first checks that the conflict set is
  exactly those two assemblies from the Revit API, and only then demotes the warning. If the set changes, the build
  stops: investigate, do not widen the filter.
- The add-in embeds `artifacts/catalog/catalog.json`. `status` reports "catalog differs from the broker's" when the
  broker and the add-in were built from different catalogs, so rebuild both from the same commit.
- `npm run package` builds the release zip (`artifacts/release/`).

## Dev install, homes and slots

Each worktree or lane uses its own home, never the installed one:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev-install.ps1 -Home "$env:USERPROFILE\.revit-mcp-next-dev-<name>" -RevitYears 2024,2027
```

It copies the built payload into `<home>\addin\<year>\<payloadId>\`, writes `current.json` and `auth.env`, and keeps the
loader in the default home with the manifest pointing at it. Revit uses the dev payload only when started with
`REVIT_MCP_NEXT_HOME=<home>`; the e2e harness does that for you. Homes are never under AppData, Temp or an app package.
`-EnableTestOps` writes `config\e2e-test-ops.enable` (non-default homes only).

The harness runs at most `RMN_E2E_SLOTS` (default 3) Revit processes machine-wide (lock files in
`%USERPROFILE%\.revit-mcp-next-slots`). `--keep-revit` keeps a warm Revit; after rebuilding the add-in use `--restart`.

## Tests and the rules before a push

There are no unit tests and no test doubles; everything is verified end to end (`e2e/README.md`, [docs/e2e.md](docs/e2e.md)).

Before every push to `main`:

1. `npm run build`, `npm run typecheck`, `npm run gen:catalog -- --check`, `node scripts/gen-tool-docs.mjs --check`.
2. `node e2e/run.mjs --profile surface` (no Revit; a build lint, never a substitute for Revit runs).
3. Your scenarios against real Revit 2024:
   `node e2e/run.mjs --home "$env:USERPROFILE\.revit-mcp-next-dev-<name>" --years 2024 --launch --only <IDs>`.
   Add-in, IPC or loader changes also run on 2027.
4. Both add-in years build with 0 errors.

Work straight on `main`: small commits, `git fetch && git rebase origin/main`, rebuild and recheck, then
`git push origin HEAD:main`. Never force-push. End commit messages with the attribution trailer your tool requires.

## Hard rules

- **Never use computer use, screenshots, UI Automation or clicks on Revit.** Drive it only through the MCP tools or the
  harness. Never touch Revit processes you did not start; the harness kills only its own pids.
- **Launch Revit outside the AI app's process tree.** The harness starts it through WMI `Win32_Process.Create`; Revit
  started as a child of Codex or Claude fails Autodesk licensing.
- **Disposable models only.** Tests and experiments create their own projects (`manage_document new_project` from the
  English templates) under `<home>\runs\`. Never open, save or sync a real project file.
- **Never print or copy `config\auth.env`**, and never edit `settings.json` to enable code execution.
- **Clean room.** Do not copy code, handlers, tool schemas or generated output from other Revit MCP projects (for
  example revit-mcp-cowork or mcp-servers-for-revit). Ideas and public Autodesk/MCP documentation are fine.
- **Keep the surface stable.** Tool schemas come only from the catalog. Every error has a code from
  `contracts/src/errors.ts` and a `fix:` that names an existing tool, op and params (W1 gate). Respect the token
  budgets (`contracts/src/catalog/checks.ts`).
- **Portable.** No hard-coded user names or machine paths; anything machine-specific is an auto-detected optional
  parameter.

## Using Revit

When you work on a Revit model through the `revit` MCP server (the same rules the installer writes to
`~\.codex\AGENTS.md`):

Use only the `revit` MCP tools for Revit and follow the revit skill. Never use screen automation, screenshots or
clicks on Revit. Start with status. Do exactly what each next:/fix: line says; "ask the user" means stop and ask. If a
result says NOT APPLIED, show the user the plan and ask; call again with confirm only when the user asked for exactly
this or approves. Never repeat a write that is running or unknown; call job_status. run_csharp only when no tool fits
and the user agreed; never edit ~\.revit-mcp-next\config\settings.json or enable code execution yourself. If the revit
tools are missing, tell the user to start a new thread and run the revit-mcp-next doctor.
