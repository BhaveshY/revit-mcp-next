# Architecture

```
Codex / Claude --stdio MCP--> broker (node; one process per client session)
                                 | catalog-driven tools, lenient input, text results, targeting, jobs
                                 | discovers Revit through <home>\instances\r<year>-<pid>-<6hex>.json + .lock
                                 v  protocol v3: length-prefixed JSON frames over local named pipes, token auth
                  Revit 2024 (net48) / Revit 2027 (net10) -> RevitMcpNext.Loader.dll -> payload RevitMcpNext.Addin.dll
                     primary pipe  -> request queue -> ExternalEvent/Idling -> OperationRegistry -> handlers (UI thread)
                     control pipe  -> hello, snapshot, health, cancel, results, jobs, dialogs, press (no UI thread)
```

The repo has three layers, and one source of truth for the tool surface:

| Path | What |
|---|---|
| `contracts/` | TypeScript contracts shared by the broker, CLI, installer and e2e: the tool catalog, op registry, ErrorCatalog, wire protocol, transaction naming and the home layout + settings schema |
| `broker/` | The MCP server (`src/server.ts`, `src/framework`, `src/tools/<tool>.ts`), transport (`src/ipc`, `src/instances`), targeting, jobs, ledger, and the `revit-mcp-cli` (`src/cli`). Bundled by esbuild into `revit-mcp.mjs` and `revit-mcp-cli.mjs` |
| `addin/` | `RevitMcpNext.Loader` (manifest target), `RevitMcpNext.Addin` (payload), `RevitMcpNext.Contracts` (wire types in C#), `RevitMcpNext.Scripting` (Roslyn host for opt-in `run_csharp`) |
| `installer/`, `plugins/`, `e2e/` | Per-user installer, Codex/Claude skills plugins, the end-to-end harness |

## The catalog

`contracts/src/catalog/tools/<tool>.ts` holds one file per tool (38): the advertised JSON schema, and per op its
required/optional params, examples, help text, error codes and registry metadata (kind, scope, transaction mode, blast
rules, job behaviour, minimum Revit year, in-process and change_set eligibility). `npm run build` emits
`artifacts/catalog/catalog.json` and the exact `tools/list` JSON.

- The broker serves `tools/list` verbatim from it and validates arguments itself (permissive SDK validator), so a
  small model's sloppy input is normalized instead of rejected.
- `scripts/build-addin.ps1` embeds `catalog.json` into the add-in. At startup the add-in checks that every add-in key
  has an `[Op("<key>")]` handler and every handler has a key. The catalog hash ties a broker build to an add-in build.
- `help`, the e2e coverage gate and the generated docs ([tools.md](tools.md)) read the same file.

Keys are `<tool>.<op>` (`create_elements.wall`, `list.levels`, `export.pdf`) or the tool name for tools without an op
(`find_elements`, `capture`). There are 243 keys; 232 are served by the add-in, the rest by the broker (`status`,
`set_target`, `help`, `job_status`, ...) or the control pipe (`ui.dialogs`, `ui.press`).

## Broker

One broker process runs per client session (Codex and Claude each start their own). Per call:

1. **Normalize** the arguments (`framework/normalize.ts`): camelCase to snake_case, scalars to arrays, unit strings
   (`"3.5 m"`, `12'6"`) to mm, enum synonyms, ranges clamped. Unknown params give `warn: PARAM_IGNORED`; a missing
   requirement gives `INVALID_ARGS` with the op's example filled with the caller's values.
2. **Resolve the target document** (`targeting/`): the session pins the first doc it uses and every result names it
   (`- doc: Tower_A (Revit 2024)`). `set_target`, opening or activating a doc re-pin explicitly; a write after the user
   switched projects under an automatic pin returns `TARGET_CHANGED` with nothing written.
3. **Expand short ids**: `r#` handles (element sets from `find_elements` or a write), `last`, `selection`. Short ids
   (`#n`, `r#`, `p#`, `c#`, `w#`, `j#`) are allocated by the broker and never cross the pipe.
4. **Send one frame** to the add-in of the right Revit instance (`ipc/PipeClient.ts`) with a ULID requestId, the auth
   token, the doc ref and the remaining time budget. Reads run in parallel (limiter + coalescer); a read that waits
   behind a long write or a native operation fails fast with `REVIT_BUSY` after about 8 s instead of hanging.
5. **Render** the text result (`framework/render.ts`): status line, notices, warnings, one JSON line, `more:` and
   `next:`; errors get a `fix:` rendered from the ErrorCatalog template, never from add-in free text.

Every call answers within the budget (`callBudgetMs`, 50 s). Work that takes longer continues as a job (`j#`); a write
still running at the budget returns `WRITE_STILL_RUNNING w#`. Write outcomes go to the add-in ledger
(`<home>\ledger\<instanceId>.jsonl`) and the broker's in-flight journal (`<home>\state\inflight`), so a broker that
crashed or restarted can report `RECOVERED_WRITE` instead of guessing.

## Add-in

The manifest points at `RevitMcpNext.Loader.dll`. The loader resolves the home (`REVIT_MCP_NEXT_HOME`, else its own
location), reads `addin\<year>\current.json`, loads that payload's `RevitMcpNext.Addin.dll` and forwards
startup/shutdown. On any failure it writes `logs\loader-<year>.log` and lets Revit start normally. This is what lets the
installed build, the e2e build and developer builds coexist on one PC.

Inside the payload:

- **Ipc**: `NamedPipeHost` hosts the primary pipe `revit-mcp-next-<instanceId>` (16 instances) and the control pipe
  `revit-mcp-next-<instanceId>-control` (4). The pipe ACL admits the current Windows user only, and every frame except
  `hello` must carry the token from `<home>\config\auth.env`. `RuntimeInstanceRegistration` writes
  `<home>\instances\<instanceId>.json` (a mirror of the document snapshot) and holds the `.lock` file while Revit runs.
- **Runtime**: `RevitRequestQueue` + `ExternalEventPump` bring requests onto Revit's UI thread (ExternalEvent with a
  WM_NULL wake-up and an Idling fallback). `DocumentRegistry` keeps the document snapshot (docs, keys, levels,
  selection, generations), the change ring behind `get_changes`, per-element stamps for confirm tokens and the
  transaction journal behind `undo`. `DialogPolicy` + `DialogUia` handle Revit dialogs. `JobRunner` runs long ops as
  jobs in steps.
- **Core**: `Dispatcher` and `OperationRegistry` bind keys to `[Op]` handlers. `ChangeEngine` runs every write in a
  `WriteScope` (a TransactionGroup with per-op Transactions and `FailureCapture`, so warnings come back in the result
  instead of as dialogs). `TempScope` is the always-rolled-back variant used by `preview:true`, capture and probes.
  Transaction names follow one naming table (`MCP <writeTag> <tool>.<op>`), shared with `contracts/src/naming.ts`.
- **Domains**: the handlers, one folder per area (Reads, Model, Modify, Views, Sheets, Annotation, Schedules,
  Families, Mep, Structure, Documents, Worksharing, Links, Export, Delivery, Visual, Ui, Undo, Code, TestOps, Dev).
- **Integration**: `RevitMcpInProcessBridge` lets pyRevit and Dynamo call read and non-destructive keys on the UI
  thread without the pipe ([../integrations](../integrations/README.md)).

## Write pipeline

1. The add-in executes the op inside a WriteScope and records created, modified and deleted elements.
2. It evaluates the blast rules from `settings.json` `confirm.*` (more than 20 deletions including dependents, more
   than 200 existing elements modified, more than 500 created) and the always-confirm cases (file overwrite, closing
   with unsaved changes, opening a central, sync, coordinates, link removal, model delivery, C# commit, a dialog button
   other than Cancel/Close).
3. If a rule triggers and the call had no confirm token, the group is rolled back and the broker stores the plan under
   a 6-character token: `NOT APPLIED - needs the user's OK`, with `next: ask the user, then <tool> {"op":...,"confirm":"K7QM2X"}`.
   A confirm call re-runs the stored args; it fails with `CONFIRM_STALE` only if an affected element changed.
4. Otherwise the group is committed as one undo step. The result carries ids, warnings, an undo hint, a handle `r#`
   and a write id `w#`.

`change_set` runs up to 100 ops in one WriteScope (one undo step, all or nothing) with `$0`/`$prev` references.
`undo` posts Revit's own Undo for this session's writes and verifies it in the transaction journal.

## Home

```
<home>\                                  default %USERPROFILE%\.revit-mcp-next
  .revit-mcp-next-home  install.json     marker, installer receipt
  runtime\node.exe                       Node 24 (copied by the installer)
  broker\revit-mcp.mjs  revit-mcp-cli.mjs  build-info.json
  addin\<year>\loader\<id>\RevitMcpNext.Loader.dll      manifest target
  addin\<year>\<payloadId>\...           payload (scripting\ = Roslyn, opt-in)
  addin\<year>\current.json              active payload
  config\auth.env  config\settings.json  token (user-only ACL), settings
  instances\  ledger\  jobs\  state\inflight\
  logs\  captures\  exports\  recipes\  plugins\  support\
```

`contracts/src/home.ts` and `addin/RevitMcpNext.Addin/Runtime/McpHome.cs` implement this layout and the settings
schema; the installer, doctor and e2e use the TypeScript module.

## Design record

`docs/design/SPEC.md` is the historical design specification of the 0.4 overhaul. The code is the source of truth
where they differ.
