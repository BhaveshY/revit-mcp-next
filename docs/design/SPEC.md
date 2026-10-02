# SPEC: revit-mcp-next overhaul (authoritative implementation spec)

Status: normative. Date 2026-09-30. Synthesized from D1 (tool catalog), D2 (reliability), D3 (add-in capabilities), D4 (integration and e2e), the critic review (37 findings, all resolved in §19), and the investigation files. The repo is at HEAD `5406155`.

## 0. How to use this spec

**Precedence.** When sources disagree, use this order:
1. This file.
2. The normative companion generators next to it: `SPEC-catalog.mjs` (advertised tools/list), `SPEC-registry.mjs` (op registry), `SPEC-instructions.mjs` (server instructions). Their generated outputs `SPEC-tools-list.json`, `SPEC-registry.json`, `SPEC-catalog.generated.md` (Appendix A) and `SPEC-registry.generated.md` (Appendix B) are derived; regenerate with `node SPEC-catalog.mjs && node SPEC-registry.mjs`.
3. D3 §3 (Revit API routes per capability), then D1 §4B (behaviour per op), then D2, then D4.

Those documents are incorporated by reference **except where this spec overrides them**. Every override is listed in §19. Where D1/D2/D3/D4 use a name, code, path or key that this spec renames, this spec's name wins.

**Folder.** All design files live in `C:\Users\Bhavesh\AppData\Local\Temp\claude\C--Users-Bhavesh-Downloads\1f5fae82-cdab-437d-890e-b9ee4a3a7738\scratchpad\design\`. Package W1-BROKER copies `SPEC.md` and the `SPEC-*.mjs|json|md` files into the repo at `docs/design/`, so every worktree has them.

**Terms.** The *broker* is the TypeScript stdio MCP server (`broker/`, `contracts/`). The *add-in* is the C# Revit add-in (`addin/`). A *lane* is one wave-2 work package. The *home* is the runtime home (§3). A *key* is an op registry key `<tool>.<op>` (or `<tool>` for tools without a discriminator).

---

## 1. Requirements traceability

| # | Owner requirement | Where met |
|---|---|---|
| R1 | Current MCP (2026-07-28 GA, SDK 2.2.0), dual-era for Codex (2025-06-18) | §5.1, §8.13 |
| R2 | Seamless with Codex CLI 0.153 / desktop 0.159 on Windows; Claude Code/Desktop | §3, §12, §8.12 |
| R3 | Usable by weak models | §5.3-§5.8, §6.6, §7, instructions §5.7 |
| R4 | Never breaks mid-session (2 Revit versions, many docs, restart, tab switch, Save As, busy, dialogs, idle) | §7, §8 |
| R5 | Fast and reliable | §8, §9.6 |
| R6 | All Revit tasks incl. visual; opt-in C# | §5.9, §6, §10, Appendix A/B |
| R7 | Token efficient without cutting capability | §15, §17 |
| R8 | No unit tests; e2e only against real Revit | §13 |
| R9 | Commits straight to main | §20 |

---

## 2. Architecture overview

```
 Codex / Claude  --stdio MCP (2025-06-18 legacy or 2026-07-28 modern)-->  broker (node, one process per client connection)
                                                                            |  framework: catalog-driven tools, lenient input, text results
                                                                            |  targeting: pin + rebind by documentKey
                                                                            |  transport: v3 frames over named pipes, per-instance limiter
             <home>\instances\r<year>-<pid>-<6hex>.json (+.lock)  <-------  |  (discovery by file + lock liveness)
                                                                            v
                         Revit 2024 (net48)  /  Revit 2027 (net10)  add-in via loader
                           primary pipe (16 inst.)  -> RevitRequestQueue -> ExternalEvent/Idling -> OperationRegistry -> handlers
                           control pipe (4 inst.)   -> hello/snapshot/health/cancel/results/jobs/dialogs/press (no UI thread)
                           DocumentRegistry (event-driven snapshot, keys, generations, change ring, txn journal)
```

Lead decisions A-H are adopted with the refinements in §19. The biggest structural decisions of this spec:

1. **The catalog in `contracts/` is the single source of truth** for advertised schemas, the op registry, help, examples and errors (§4). It is emitted as JSON for the broker and embedded into the add-in. C# attributes only bind handlers to keys.
2. **Writes run through one add-in engine** (`ChangeEngine`: WriteScope/TempScope/FailureCapture). A single tool call is a change set of one op.
3. **A loader add-in** (`RevitMcpNext.Loader.dll`) is what the manifest points to. It loads the payload of whichever home the Revit process uses. This lets the installed build, the e2e build and each lane's build coexist on one machine without rewriting the global manifest (§9.2).
4. **No per-lane edits of shared files in wave 2**: registration is by adding files (broker tool modules, catalog tool files, `[Op]` handlers, e2e scenario files), all pre-created or globbed by wave 1 (§18, §21).

---

## 3. Runtime home (normative layout)

`home = env REVIT_MCP_NEXT_HOME ?? <self-located home> ?? %USERPROFILE%\.revit-mcp-next`.
- The broker self-locates by walking up from its own file for the marker `.revit-mcp-next-home`.
- The loader self-locates from its DLL path (`<home>\addin\<year>\loader\<ver>\`) and then sets `REVIT_MCP_NEXT_HOME` in-process for the add-in.
- Never under `%LOCALAPPDATA%`, `%APPDATA%`, `%TEMP%`, `...\Packages\...` or `WindowsApps`. The installer refuses those (D4 §2).

```
<home>\
  .revit-mcp-next-home                 marker JSON {schema:1, createdAtUtc, createdBy: installer|dev-install|addin|broker}
  install.json                         receipt, installer only (D4 §4.12)
  runtime\node.exe                     copied at install
  broker\revit-mcp.mjs                 installed MCP bundle (esbuild); broker\revit-mcp-cli.mjs; broker\build-info.json
  addin\<year>\loader\<loaderVer>\RevitMcpNext.Loader.dll      manifest target
  addin\<year>\<payloadId>\RevitMcpNext.Addin.dll ...          payload; scripting\ holds Roslyn (opt-in)
  addin\<year>\current.json            {payloadId, gitSha, builtAtUtc, installedAtUtc}
  config\auth.env                      token file (format D4 §4.7), ACL user+SYSTEM+Administrators
  config\settings.json                 §3.1
  config\e2e-test-ops.enable           optional; honoured only when home != %USERPROFILE%\.revit-mcp-next (§13.4)
  instances\r<year>-<pid>-<6hex>.json  registration = DocSnapshot mirror (§4.6.5) + writtenAtUtc
  instances\r<year>-<pid>-<6hex>.lock  liveness lock held FileShare.None, DeleteOnClose
  ledger\<instanceId>.jsonl            terminal write outcomes (§8.10)
  jobs\<instanceId>\<jobId>.json       terminal job records
  state\inflight\<brokerPid>-<startMs>.json   broker in-flight write journal
  logs\broker-YYYYMMDD.<n>.jsonl  logs\addin-<year>-YYYYMMDD.<n>.jsonl  logs\loader-<year>.log
  logs\code-exec-audit.jsonl  logs\code\<sha256>.cs  logs\install-<ts>.log
  captures\<yyyy-MM-dd>\<HHmmss>-<rand6>\image.<png|jpg> + meta.json
  exports\<docTitle>\...               default export folder
  recipes\                             model-delivery recipes
  plugins\                             Codex/Claude marketplace copy (installer)
```

Manifests stay in `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin`. Each points at the loader of the installed home. The ClientId `6F78E70D-BE13-4E0B-9B11-9E28F876AF71` is kept so existing trust entries still apply (verify V16).

### 3.1 `config/settings.json` (single schema, one key per setting)

Missing keys take these defaults. The add-in hot-reloads the file (FileSystemWatcher, 500 ms debounce) and the broker `stat`s it with a 2 s cache. Invalid JSON keeps the last good values and adds `SETTINGS_INVALID` to status.

```json
{
  "schemaVersion": 1,
  "enableCodeExecution": false,
  "codeExecution": { "timeoutSec": 30, "maxOutputKB": 64, "allowUnsafeApis": false, "experimentalOutOfProcess": false },
  "callBudgetMs": 50000,
  "perInstancePrimarySlots": 4,
  "wake": { "watchdogMs": 200, "wmNull": true, "idlingFallback": true, "idlingFallbackAfterMs": 1000 },
  "stall": { "dialogMs": 3000, "hungMs": 5000, "editModeMs": 6000, "busyFailFastMs": 8000 },
  "dialogPolicy": { "autoRespond": true, "extra": {}, "deny": [] },
  "confirm": { "deleteOver": 20, "bulkOver": 200, "createOver": 500 },
  "capture": { "size": "medium", "format": "auto", "retainHours": 72, "maxMB": 1024, "maxFolders": 500 },
  "log": { "level": "info", "maxFileMb": 10, "retainDays": 14 },
  "useElicitation": false,
  "experimentalUndoStack": false,
  "defaultTemplate": { "2024": "", "2027": "" }
}
```

Rules:
- `callBudgetMs` is clamped to 10000-55000.
- `enableCodeExecution` is changed only by the installer switches or by the user editing the file. It is never changed by a tool argument. Instructions, SKILL and AGENTS text forbid agents from editing this file (§5.7, §12.5).
- `dialogPolicy.extra` can never add ids starting `RevitMcpNext_` or ids on the denylist (§8.5).
- The e2e-only key `codeExecution.e2ePreapproved` is honoured only under the §13.4 conditions.

`contracts/src/home.ts` (TS) and `addin/RevitMcpNext.Addin/Runtime/McpHome.cs` plus `Runtime/Settings.cs` (C#) implement exactly this layout and schema. The installer, doctor and e2e consume the TS module.

---

## 4. Contracts: single sources of truth (`contracts/`, owner W1-BROKER, frozen in wave 2 except per-tool catalog files)

### 4.1 Catalog files

```
contracts/src/catalog/types.ts           ToolSpec, OpSpec, OpMeta, ParamSpec types (below)
contracts/src/catalog/tools/<tool>.ts    one file per tool (38 files); exports `tool: ToolSpec`
contracts/src/catalog/index.ts           imports all 38 files (pre-populated by W1); exports CATALOG, byName, byKey
contracts/src/catalog/topics.ts          help topics (units, targeting, selectors, confirm, paging, jobs, images, errors, recipe, run_csharp, workflow:*)
contracts/src/catalog/emit.ts            writes artifacts/catalog/catalog.json and artifacts/catalog/tools-list.json
contracts/src/catalog/checks.ts          surface gates (§15) used by e2e S00 and `npm run gen:catalog -- --check`
```

```ts
export type Kind = "read" | "write" | "ui" | "lifecycle" | "control" | "code";
export type Impl = "addin" | "broker" | "control" | "both";
export type Scope = "none" | "any" | "project" | "family" | "project_or_family";
export type Tx = "none" | "in" | "own" | "temp" | "group" | "lifecycle";
export type Job = "never" | "auto" | "always";
export type Blast = "delete" | "bulk" | "create" | "always" | "file_overwrite" | "unsaved_close" | "multi_doc" | "central_open" | "code_commit" | "button";
export interface OpMeta { kind: Kind; impl: Impl; scope: Scope; ui: boolean; min: 2024 | 2027; idle: boolean; tx: Tx;
  blast: Blast[]; strict: boolean; job: Job; inproc: boolean; cs: boolean; }
export interface ParamSpec {            // advertised part = JSON Schema subset: type, description, enum, items, properties, required
  type: "string" | "number" | "integer" | "boolean" | "object" | "array"; description: string; enum?: string[];
  items?: ParamSpec | { type: string; items?: unknown; enum?: string[] }; properties?: Record<string, ParamSpec>; required?: string[];
  unit?: "mm" | "deg" | "pct" | "m2" | "m3" | "s" | "px";   // NOT advertised: drives lenient unit parsing (§5.3)
  synonyms?: Record<string, string>;                         // NOT advertised: enum synonyms, e.g. {"3D":"3d","FloorPlan":"floor_plan"}
}
export interface OpSpec { req: string; opt: string; meta: OpMeta; help?: string; api?: string;
  examples: Record<string, unknown>[];                        // >=1 per op by end of wave 2; used by help and e2e W4
  errors?: string[]; }                                        // codes this op may return (documentation)
export interface ToolSpec { name: string; title: string; description: string; group: string;
  annotations: { readOnlyHint: boolean; destructiveHint?: boolean; idempotentHint?: boolean; openWorldHint: boolean };
  properties: Record<string, ParamSpec>; required?: string[];
  discriminator: "op" | "kind" | "check" | "format" | null;   // null: single implicit op, key = tool name
  ops: Record<string, OpSpec>;                                 // key "" when discriminator is null
  profiles: ("core" | "full")[]; optIn?: "enableCodeExecution";
  errors?: Record<string, ErrorSpec>;                          // tool-specific codes, merged into the ErrorCatalog
}
```

- **Contents.** The advertised part of every tool must equal `SPEC-catalog.mjs` (Appendix A). Op `req`/`opt` come from the `*_OPS` tables in that file. `meta` must equal `SPEC-registry.mjs` (Appendix B). Examples are seeded from D1 §13 and completed by the lane that owns the tool.
- **The `op` param description** is generated at emit time from `req`/`opt` exactly as `opParam()` in `SPEC-catalog.mjs` does (`sel = ids, from or filter. Params per op, optional after ';': ...`).
- **`catalog.json`** = `{ version, hash, tools: ToolSpec[] (full, incl. non-advertised fields), registry: Array<OpMeta & {key, tool, op}>, errors: ErrorSpec[], naming, protocol }`. `hash` = sha256 of the canonical JSON of `tools` + `registry`.
- **`tools-list.json`** = exactly what `tools/list` serves (§5.1).
- **Build.** `npm run build` (root) builds contracts and broker, then runs `npm run gen:catalog`, which writes both files into `artifacts/catalog/` (gitignored). `scripts/build-addin.ps1` runs `npm run gen:catalog` first when node is on PATH, and embeds `artifacts/catalog/catalog.json` as resource `RevitMcpNext.catalog.json`. If the file is missing, the build prints a warning and the add-in reports `CATALOG_MISSING` in `status detail:full`.

### 4.2 Op registry

Appendix B is the full registry: 243 keys, 232 of them bound to add-in handlers. The fields are defined in §4.1. The rules:

- Keys are identical in broker and add-in. A tool with discriminator `kind`/`check`/`format` uses `list.<kind>`, `check_model.<check>`, `export.<format>`. A tool without a discriminator uses the tool name (`find_elements`, `set_parameters`, `capture`, `run_csharp`, `change_set`, `undo`).
- `impl` says where the handler lives:
  - `addin` means an `[Op(key)]` handler must exist.
  - `broker` means broker only.
  - `control` means the add-in control pipe (`ui.dialogs`, `ui.press`).
  - `both` means the broker orchestrates and then calls the add-in key (`change_set`).
- Internal add-in keys outside the catalog use the prefixes `dev.` (e.g. `dev.ping`, `dev.dump_labels`) and `test.` (§13.4). Registry checks skip them.
- **Add-in startup check.** Every catalog key with impl `addin|both` must have a bound handler, and every bound key must be in the catalog (or be `dev.*`/`test.*`). Mismatches are logged and reported in `status detail:full` and in `hello.capabilities.mismatches`.
- **Broker mapping.** If a key is not in `hello.capabilities.keys`, the broker returns `UNSUPPORTED_VERSION` when `meta.min > year` and `ADDIN_OUTDATED` otherwise.
- **Kind check.** The broker sends `kind`, and the add-in compares it with its embedded catalog. A mismatch returns `ADDIN_OUTDATED` (catalog skew).

### 4.3 ErrorCatalog (`contracts/src/errors.ts`)

```ts
export interface ErrorSpec { code: string; meaning: string; nothingChanged: boolean; fix: FixTemplate; }
export type FixTemplate =
  | { kind: "call"; tool: string; args: Record<string, unknown> }   // string values may contain ${details.x}, ${args.x}, ${doc.n}
  | { kind: "same_call_with"; set: Record<string, string> }         // the failing call with these args replaced (templated)
  | { kind: "same_call_without"; drop: string[] }
  | { kind: "choose"; tool: string; arg: string }                   // renders options from details.options as "1) ... 2) ..."
  | { kind: "ask"; text: string }                                   // "ask the user: <text>"
  | { kind: "retry_in"; seconds: number };
```

**Rendering.** The add-in returns only `code`, `message` (concrete names and values, never tool names) and structured `details`. The broker renders the `fix:` line from the catalog. It never uses free text from the add-in as a fix. Tool-specific codes live in the owning `ToolSpec.errors` and are merged in. e2e W1 asserts that every `call` template names a tool and op that exist.

Unified code list. D1, D2, D3 and D4 synonyms are mapped in §19 C22.

| Code | Meaning | details | fix |
|---|---|---|---|
| NO_REVIT_RUNNING | No live Revit with the add-in | lastExited? {year,pid,doc,at} | ask: start Revit 2024 or 2027 (and open <doc>) |
| REVIT_STARTING | Instance registered, not ready | year | retry_in 10 |
| REVIT_EXITED | The instance died before/during the call | year, pid, doc | ask: restart Revit and open <doc>; the session reconnects |
| ADDIN_PIPE_MISSING | Lock held but no pipe | logPath | ask: restart Revit <year> |
| ADDIN_OUTDATED / ADDIN_NEWER_THAN_BROKER | Protocol or catalog skew | addinVersion, brokerVersion | ask: restart Revit / re-run the installer and start a new thread |
| AUTH_MISMATCH / AUTH_NOT_CONFIGURED | Token problem | authFile, addinFp, brokerFp | ask: re-run the installer, or restart Revit |
| BRIDGE_ACCESS_DENIED | EACCES/EPERM on the pipe | - | ask: run Revit and the AI client as the same user, both non-elevated |
| BRIDGE_BUSY / REVIT_QUEUE_FULL | Limiter or add-in admission full; nothing sent | mine, others, executing | retry_in 10 |
| REVIT_BUSY | Revit is executing other work (MCP op, job or native op) | executing{op, elapsedS, ref}, native? | call job_status {id:${details.ref}} when ref is yours, else retry_in 15 |
| REVIT_DIALOG_OPEN | A modal dialog blocks Revit; nothing ran | instance, title, text, dialogId, buttons | call ui {op:"dialogs", instance:${details.instance}} |
| REVIT_NOT_RESPONDING | Main window hung ≥5 s | - | ask: check Revit, then retry once |
| REVIT_EDIT_MODE_OR_COMMAND | Probably in a command/sketch; nothing ran | - | ask: press Esc twice or Finish in Revit, then retry once |
| REQUEST_CANCELLED | Client cancelled | - | - |
| NO_OPEN_DOCUMENT | Revit runs with no doc | year | call manage_document {op:"open", path:"<path>"} or ask |
| TARGET_AMBIGUOUS | Several candidates, no pin | options[] | choose set_target doc |
| TARGET_CLOSED / DOC_NOT_OPEN | Pinned doc closed / not reopened | title, path | call manage_document {op:"open", path:${details.path}} |
| TARGET_CHANGED | Write gate after an automatic or user-driven doc change; nothing written | from, to, reason | same_call_with {} (repeat exactly), or choose set_target doc |
| FAMILY_DOC_REQUIRED / PROJECT_DOC_REQUIRED | Wrong doc kind | family?, options | call edit_family {op:"open", family:${details.family}} / choose set_target doc |
| DOC_READ_ONLY / LINK_READ_ONLY | Read-only copy / link element | editable? | set_target the editable copy / call links ... |
| NEEDS_ACTIVE_DOC | Op needs the UI-active doc | doc | call ui {op:"activate_doc", doc:${details.doc}} |
| WRITE_STILL_RUNNING | A write still runs at the budget | ref (w#) | call job_status {id:${details.ref}}; never repeat |
| READ_STILL_RUNNING | A non-yieldable read still runs | ref (j#) | call job_status {id:${details.ref}} (or repeat the same call: it returns the cached result) |
| WRITE_OUTCOME_UNKNOWN | Response lost and Revit is gone | ref | ask: reopen the model and check <elements>; never repeat |
| REVIT_TRANSACTION_ROLLED_BACK | Revit rolled back; failures listed | failures[] | same call after fixing the named element/param; help {tool,op} |
| REVIT_REFUSED | The Revit API refused (InvalidOperation/Argument), message verbatim | apiMessage | help {tool,op} |
| INVALID_ARGS | Validation failed | param, reason, example | call <tool> <example with user values> |
| UNKNOWN_OP | Unknown op | ops[], closest | same_call_with {op: closest} |
| NOT_FOUND / AMBIGUOUS_NAME | Name/id did not resolve | param, kind, value, candidates[] | same_call_with {<param>: candidates[0]} |
| NAME_TAKEN | Duplicate name/number | param, next | same_call_with {<param>: next} |
| UNSUPPORTED_VERSION | Needs a newer Revit | min, year, alternative? | call <alternative> or run_csharp when enabled |
| UNSUPPORTED_OP | The API cannot do it | alternative? | as given, or ask |
| NOT_EDITABLE | Element owned by another user / updated in central | owner, reason | ask: have <owner> relinquish; or call worksharing {op:"reload_latest"} |
| TEMPLATE_CONTROLLED | View template controls the setting | template | same_call_with {view:${details.template}} |
| NO_TAG_FAMILY | No tag type loaded for the category | category | call place_family {op:"load", path:"<tag .rfa>"} or ask |
| LEGEND_NEEDS_SEED | No legend exists to duplicate | - | ask: create one empty legend once |
| CANNOT_CLOSE_ACTIVE | Only open doc is the active doc | - | ask: close it in Revit |
| LAST_VIEW | Revit refuses to close the last view | - | call manage_document {op:"close"} |
| SAVE_AS_REQUIRED | Doc never saved | - | call manage_document {op:"save_as", path:"<path>"} |
| EXPORTER_MISSING | NWC/IFC exporter not installed | format | ask: install the <format> exporter |
| CAPTURE_GPU_UNAVAILABLE | DirectX lost (RDP) | - | same_call_with {style:"hidden"} |
| CAPTURE_NOT_EXPORTABLE | Schedules, templates, internal views | viewKind | call read_schedule {...} for schedules |
| CONFIRM_STALE / CONFIRM_EXPIRED / CONFIRM_MISMATCH | Token problem | changedIds? | same_call_without [confirm] (gets a new plan) |
| PAGE_EXPIRED / HANDLE_EXPIRED / CAPTURE_GONE / JOB_UNKNOWN / OUTCOME_EXPIRED | Session cache lost | - | repeat the call that produced it; JOB_UNKNOWN/OUTCOME_EXPIRED: call status {include:["writes"]} |
| UNDO_BLOCKED | Newer non-MCP change on top | top | ask: press Ctrl+Z in Revit; or call undo {mode:"compensate"} when compensable |
| UNDO_UNCONFIRMED | PostCommand not confirmed in 10 s | - | call get_changes {since:"last"} |
| IRREVERSIBLE | Compensation impossible (delete, purge, load, code) | - | ask: press Ctrl+Z in Revit |
| NEEDS_TYPES / NOT_EDITABLE_FAMILY / FORMULA_INVALID / PARAM_IN_USE / PARAM_HAS_FORMULA / SHARED_PARAM_FILE_MISSING / SHARED_PARAM_RENAME | Family edit problems (D3 §3.7) | per code | per code (in edit_family.ts errors) |
| CODE_EXECUTION_DISABLED | run_csharp not enabled | - | ask: only if you want it, enable code execution in revit-mcp-next settings |
| CODE_EXECUTION_DECLINED | User clicked Block in Revit | - | ask: allow code execution in the Revit prompt, or use a tool |
| CODE_EXECUTION_UNAVAILABLE / CODE_COMPILE_ERROR / CODE_DENIED_API / CODE_TIMEOUT / CODE_RUNTIME_ERROR | Code problems | line, col, symbol | same call with corrected code |
| DIALOG_NOT_FOUND / DIALOG_PROTECTED / BUTTON_NOT_FOUND | ui press problems | buttons[] | call ui {op:"dialogs"} / ask |
| INTERNAL_ERROR | Unexpected exception (logged with requestId) | requestId, logPath | ask: report requestId; retry once |
| RESPONSE_TOO_LARGE | >4 MiB frame | - | same call with a smaller limit/page |

**Warning codes** (`warn:` lines): REVIT_WARNING, REVIT_DIALOG_ANSWERED, VALUE_CLAMPED, PARAM_IGNORED, PARAM_RENAMED, PARTIAL_RESULT, FITTING_FAILED, IDS_GONE, NON_FINITE_NUMBER, BRIDGE_RESPONSE_RECOVERED, SETTINGS_INVALID, LATE_RESULT.

**Notice codes** (`notice:` lines): AUTO_PINNED, TARGET_NOW, REBOUND, SAVED_AS, PIN_MOVED, ACTIVE_DIFFERS, FAMILY_EDITING, RECOVERED_WRITE, CODE_EXECUTION_ENABLED, SELECTION_STALE.

### 4.4 Naming table and parser (`contracts/src/naming.ts`, `addin/.../Core/Naming.cs`)

| Class | Transaction / group name | Example |
|---|---|---|
| write | `MCP <wtag> <tool>.<op>` (tool call) or `MCP <wtag> change_set <name?> (<n> ops)` | `MCP w17 create_elements.wall` |
| write (family doc) | `MCP <wtag> edit_family.<op>` (inside the family document) | `MCP w18 edit_family.add_param` |
| write (compensation) | `MCP <wtag> undo.compensate` | |
| temp | `MCP temp <purpose>`; purpose in preview, capture, probe, run_csharp, export.ifc, schedule_fields | `MCP temp capture` |
| ui | `MCP ui <op>` | `MCP ui isolate` |

- **Parser.** `^MCP (?:(w\d+) (.+)|temp (.+)|ui (\S+))$` classifies a name as `write | temp | ui`, and anything else as `foreign`.
- **Where names come from.** `wtag` is the broker's session-unique short id (`w17`). Inner Transactions of a WriteScope reuse the group name. Only the group (assimilated) name is visible in the undo list.
- **Users of the parser.** Every filter uses it:
  - generation and element stamps ignore `temp`;
  - the FailuresProcessing safety net acts on any `MCP ` name while an MCP item executes;
  - undo treats `ui` entries as ours and harmless;
  - get_changes marks `write|ui` as `ours`.
- **No free-form names.** Nothing else may write free-form transaction names.

### 4.5 Home layout constants

`contracts/src/home.ts` exports the path builders for every entry of §3: `instanceFile(id)`, `lockFile(id)`, `captureDir(date, stamp)`, and so on. It also exports the settings defaults and a validator. The same constants appear in `Runtime/McpHome.cs`.

### 4.6 Wire protocol v3 (`contracts/src/protocol.ts`, `addin/RevitMcpNext.Contracts/BridgeContracts.cs`)

#### 4.6.1 Transport

- **Framing.** 4-byte big-endian length + UTF-8 JSON. The maximum frame is 4,194,304 bytes, measured in bytes.
- **Connections.** One request per connection. Images never cross the pipe; the add-in writes a file and returns its path.
- **Pipes.** Primary `revit-mcp-next-<instanceId>` and control `revit-mcp-next-<instanceId>-control`. The instanceId is `r<year>-<pid>-<6 hex>`. The pipe ACL admits the current user only (existing PipeSecurityFactory). The add-in hosts 16 primary and 4 control pipe instances. The handshake timeout (read request frame) is 5 s.
- **Version.** `v = "2026-10-01"`. The add-in accepts the range [min,max] it advertises in `hello`. A broker older than the range gets `ADDIN_NEWER_THAN_BROKER`; a newer broker gets `ADDIN_OUTDATED`.

#### 4.6.2 Request

```jsonc
{
  "v": "2026-10-01",
  "requestId": "01JA7Q9M4F3Z8K2N6P0R5S7T9V",   // ULID (26 chars), globally unique, ledger key
  "clientKey": "codex-mcp-client#19876",        // clientInfo.name + '#' + broker pid
  "auth": { "token": "...", "fp": "9f3a1c07", "file": "C:\\Users\\...\\config\\auth.env" },   // omitted for hello
  "op": "create_elements.wall",                 // registry key, or control op name on the control pipe
  "kind": "write",                              // registry kind ("control" on the control pipe)
  "mode": "apply",                              // write/code only: "apply" | "preview"
  "timeoutMs": 46500,                           // time left for this request, from receipt
  "doc": { "rid": 3, "key": "2024|file|c:\\p\\a.rvt" },   // null for scope none
  "writeTag": "w17",                            // write, lifecycle and code-commit requests
  "confirmed": { "stamp": 118, "deleteSet": [304512, 304513] },   // only when applying a confirmed plan
  "args": { }                                   // normalized args (§5.3); r#/last already expanded to ids
}
```

#### 4.6.3 Response

```jsonc
{
  "v": "2026-10-01", "requestId": "...", "ok": true,
  "code": null, "message": null, "details": null,   // when ok=false
  "summary": "created 4 walls (Basic Wall: Generic - 200mm) on Level 1, top Level 2",
  "data": { },                                      // op payload in §5.5 output conventions
  "changes": { "created": [304601], "modified": [], "deleted": [], "createdTotal": 1, "modifiedTotal": 0, "deletedTotal": 0 },
  "needsConfirm": null,                             // or { rule, plan, blast:{deleteTotal,byCategory,sample,modifyTotal,createTotal}, stamp, deleteSet }
  "outputs": { },                                   // named outputs for $refs: type, view, sheet, level, schedule, family, room
  "warnings": [ { "code": "REVIT_WARNING", "text": "...", "ids": [304601, 304512], "n": 1 } ],
  "notices": [ ],
  "doc": { "rid": 3, "key": "...", "title": "Tower_A", "year": 2024, "kind": "project", "generation": 18, "modified": true },
  "page": null,                                     // or { "total": 1234, "offset": 0, "count": 50, "ids": [...] } (ids only when wantIds)
  "partial": null,                                  // or { "reason": "deadline"|"limit", "resume": { } }
  "file": null,                                     // or { "path": "...", "mime": "image/png", "w": 1189, "h": 841, "bytes": 412345, "meta": { } }
  "job": null,                                      // or { "jobId": "0f3c...", "state": "running", "stage": "sheet 12/40", "done": 12, "total": 40 }
  "metrics": { "queueWaitMs": 3, "raiseToExecMs": 14, "execMs": 220, "via": "externalEvent", "cacheHit": false }
}
```

#### 4.6.4 Control ops

These are served on pipe threads and never touch the UI thread. All need auth except `hello`.

| op | payload | data |
|---|---|---|
| hello | - | {instanceId, pid, year, build, language, addinVersion, gitSha, payloadId, catalogHash, protocol{min,max}, state, home, authFile, authFp, authState, capabilities{keys[], mismatches[]}, testOps} |
| snapshot | {sinceSeq?} | DocSnapshot (§4.6.5) or {unchanged:true} |
| health | - | {queue{pending, executing{requestId, op, clientKey, writeTag?, jobId?, startedAtUtc, elapsedMs}?, byClient}, pump{p50,p90,p99,max,n,over1s,raiseResults,wmNullPosts,execViaIdling,recreates}, ui{mainWindowEnabled, hung, minimized, foreground, popup?{hwnd,title,class,isProgress}}, native?, lastIdlingAtUtc, lastDialog?, listeners{primaryWaiting, primaryActive, controlWaiting}, admissionRejections, jobs[]} |
| cancel_request | {requestId} | {cancelled: "queued"\|"cooperative"\|"not_found"\|"running_write"} |
| get_request_result | {requestId} | ledger entry incl. recorded response |
| recent_writes | {docKey, limit≤20} | [{requestId, writeTag, clientKey, key, state, atUtc, counts, saved}] |
| job_status | {jobId} | {state: queued\|running\|succeeded\|failed\|cancelled\|interrupted, stage, done, total, elapsedMs, result?} |
| job_cancel | {jobId} | {cancelled} |
| dialogs | - | [{dialog:"d3", hwnd, title, text, buttons[{name, id}], dialogId?, since, ours}] |
| press | {dialog?, button, confirmed?:bool} | {pressed, via: "uia"\|"tdm"\|"wm_command"\|"bm_click"} or error DIALOG_PROTECTED / BUTTON_NOT_FOUND / needsConfirm |

`job_start` is a primary-pipe variant of a normal request: `mode` stays, plus the envelope field `"asJob": true`. It returns `{job:{jobId, state:"queued"}}` immediately.

#### 4.6.5 DocSnapshot (control `snapshot`; also the registration file body)

```jsonc
{
  "schemaVersion": 3, "seq": 42, "atUtc": "...", "writtenAtUtc": "...",
  "instanceId": "r2024-19356-a1b2c3", "pid": 19356, "year": 2024, "build": "24.3.30.11", "language": "DEU",
  "addinVersion": "0.4.0", "gitSha": "a1b2c3d", "payloadId": "1a2b3c4d5e6f", "catalogHash": "...",
  "protocol": { "min": "2026-10-01", "max": "2026-10-01" },
  "pipe": "revit-mcp-next-r2024-19356-a1b2c3", "controlPipe": "revit-mcp-next-r2024-19356-a1b2c3-control",
  "home": "C:\\Users\\Bhavesh\\.revit-mcp-next", "state": "starting|ready|stopping",
  "ui": { "foreground": false, "lastForegroundAtUtc": "...", "minimized": false, "mainWindowEnabled": true, "hung": false,
          "popup": null },                          // or { "title": "...", "class": "#32770", "isProgress": false }
  "lastIdlingAtUtc": "...",
  "native": null,                                   // or { "kind": "sync|opening|saving|exporting|printing|reloading", "rid": 3, "sinceUtc": "...", "progress": { "caption": "...", "pos": 3, "max": 10 } }
  "executing": null,                                // or { "op": "export.pdf", "sinceUtc": "...", "clientKey": "...", "writeTag": "w17", "jobId": "..." }
  "activeRid": 3, "lastActiveProjectRid": 3,
  "codeExecution": { "enabled": false, "consented": false },
  "docs": [ {
    "rid": 3, "key": "2024|central|\\\\srv\\p\\tower.rvt", "aliases": [], "title": "Tower_bhavesh", "kind": "project",
    "path": "c:\\users\\b\\documents\\tower_bhavesh.rvt", "central": "\\\\srv\\p\\tower.rvt",
    "workshared": true, "cloud": false, "readOnly": false, "modified": true, "generation": 17, "closing": false,
    "activeView": { "id": 311, "name": "Level 1", "type": "FloorPlan", "scale": 100 }, "lastActivatedAtUtc": "...",
    "levels": [[311, "Level 0", 0], [312, "Level 1", 3500]], "levelsMore": 0,
    "selection": { "count": 3, "atUtc": "..." },
    "familySourceRid": null                         // for family docs opened by EditFamily from a project
  } ]
}
```

- **Levels** are capped at 30 per doc (`levelsMore` counts the rest). They are refreshed when `DocumentChangedEventArgs.GetAddedElementIds(new ElementClassFilter(typeof(Level)))` or `GetModifiedElementIds(same filter)` is non-empty, or when `GetDeletedElementIds()` intersects the known level ids.
- **Selection** count and ids (ids kept in memory only, capped at 10,000) come from `UIApplication.SelectionChanged` (verify V21 on 2024) and from the active doc at each `ExecuteBatch`.
- **Size.** The file is capped at 64 KB and 50 docs.

**Document key** (D2 §9.2, adopted): `<year>|cloud|<projGuid>|<modelGuid>`, `<year>|central|<norm central>`, `<year>|file|<norm path>`, `<year>|detached|<title>|<rid>`, `<year>|unsaved|<title>|<rid>`. A local copy of a central model has the central key, so opening a new local re-binds to the same key.

---

## 5. MCP surface

### 5.1 Tools, registration, eras

The 37 default tools (and opt-in `run_csharp`) are listed with their schemas in Appendix A. The measured `tools/list` is 77,661 B (78,649 B with run_csharp), against 373 KB today (§15). Tool order is deterministic, grouped session, read, visual, ui, write, control, opt-in.

Tool names are bare snake_case. None is on the banned list: `ai_element_filter, get_current_view_elements, get_selected_elements, get_available_family_types, say_hello, delete_element, send_code_to_revit` and the other upstream names in D4 §7.7.1.

**Registration (SDK 2.2.0).** Every tool is registered as

```ts
server.registerTool(spec.name, { title, description, annotations,
  inputSchema: fromJsonSchema(advertisedSchema(spec), PERMISSIVE_VALIDATOR) }, handler)
// PERMISSIVE_VALIDATOR = { getValidator: () => (data) => ({ valid: true, data, errorMessage: undefined }) }
```

- **Why this works.** The SDK then serves the catalog JSON verbatim: `standardSchemaToJsonSchema` returns `{type:"object", ...schema}`. It never rejects arguments, so lenient validation (§5.3) runs in the handler and produces §5.6 errors.
- **Handlers never throw.** Every handler is wrapped. Exceptions become `ERROR INTERNAL_ERROR` results, because the SDK's `createToolError` would otherwise strip the format.
- **No output schema.** Nothing registers `outputSchema`.
- **W1-BROKER verifies** that the served `tools/list` deep-equals `artifacts/catalog/tools-list.json` (which deep-equals `SPEC-tools-list.json`), and that no `$schema` is added.

**Eras.** `serveStdio(factory, { legacy: "serve", onerror })`.
- Drop the manual `"2026-07-28"` entry in `supportedProtocolVersions` and let SDK 2.2.0 own era selection.
- Legacy `initialize` at 2025-06-18 (Codex) and 2025-11-25 (Claude) both work, and so does modern `server/discover`.
- `cacheHints` stay: tools/list, prompts/list, resources/list, resources/read and resources/templates/list with ttl 300 s, scope private.
- `tools/list` never varies per connection. It varies per server configuration only:
  - `REVIT_MCP_NEXT_PROFILE=core` gives the 17-tool core profile (§15);
  - `run_csharp` is listed only when `enableCodeExecution` was true at broker start.

**Annotations** (final):
- Reads, `capture`, `status`, `help` and `job_status`: readOnlyHint true.
- `set_target`, `ui` and `cancel_job`: readOnly false, destructive false.
- Tools that can delete model content or files: destructive true.
- Tools that touch files outside Revit: openWorld true.

### 5.2 Schema compatibility rules

These follow D1 §11 and are enforced by the surface gate (§15):
- The root is `{type:"object", properties, required?}`.
- The only keywords are `type, description, enum, items, properties, required`. There is no `const/default/min/max/pattern/format/$ref/$defs/anyOf/oneOf/allOf/additionalProperties/min*/max*`.
- Every node has `type`, and every array has typed `items`.
- Every param, including enum params, has a description.
- Depth ≤ 5.

### 5.3 Lenient input normalization (broker, `framework/normalize.ts`)

Steps are applied in order before validation. Anything unsafe becomes an error, never a guess.

1. `camelCase` keys become snake_case when that names a real param (`allowPinned` → `allow_pinned`), with `warn: PARAM_RENAMED`. An unknown key within edit distance ≤2 of a real param gives `INVALID_ARGS` ("did you mean"). Other unknown keys are dropped with `warn: PARAM_IGNORED`.
2. Where the schema says array, a scalar becomes `[scalar]`, a JSON-array string is parsed, and a comma-separated string of ids is split.
3. Numbers: numeric strings are converted. Strings with units are converted to the param's `unit`:
   - lengths: `mm cm m in ft`, `12'6"` and `900mm`;
   - angles: `°` or `deg`;
   - slope: `%`.
   The per-call `units` param scales every length input.
4. Booleans: `true/yes/1/"true"` and `false/no/0/"false"`.
5. Enums: case-insensitive match, then the `synonyms` table, then `INVALID_ARGS` listing the values.
6. Integer and number ranges stated in descriptions are clamped with `warn: VALUE_CLAMPED`.
7. Ids: numbers or strings are accepted (UniqueIds allowed where ids are).
8. Per-op validation is derived from the `req`/`opt` signature:
   - `/` = exactly one alternative, `+` = together, `sel` = exactly one of ids/from/filter;
   - a missing requirement gives `INVALID_ARGS` with the op's example filled with the user's values;
   - params not in the op's list give `warn: PARAM_IGNORED`.
9. `op` missing gives `INVALID_ARGS` listing the ops; an unknown `op` gives `UNKNOWN_OP` with the closest.

The add-in `PayloadReader` is lenient in the same ways, as defense in depth and for in-process callers.

### 5.4 Shared vocabulary, units, geometry

D1 §2 and §6 are normative, with these overrides:
- **Points.** A 3D point's z is height above `level` when a level is given, otherwise absolute. This includes toposolid; the D1 toposolid exception is removed.
- **Wall openings** use `start`/`end` as model XY points on or near the wall; they are projected onto the wall line. `offset` is the sill height above the wall's base and `height` is the opening height.
- **roof_extrusion** uses `profile` `[[s,z],...]`.
- **curtain_grid** uses `direction: vertical|horizontal`. The mapping to U/V is fixed after V9.
- **English names.**
  - Categories and built-in parameters resolve English names on any UI language first, then localized names, then `bip:`/`OST_` tokens (§9.7).
  - Outputs use the stable English category label (`category` column) on every UI language.
  - Parameter keys of built-in parameters are output in English when a mapping exists; otherwise the display name is used.

### 5.5 Result format

D1 §7 is normative, with these overrides:
- **Content.** `content` holds text, plus image blocks for captures. There is no `structuredContent` by default.
  - With `REVIT_MCP_NEXT_STRUCTURED=1`, non-image results add `structuredContent {status, summary, notices, warnings, data, more, next, doc}`.
  - Image results never add it.
- **Text layout, exactly:**
  ```
  <STATUS>: <summary> - doc: <title> (Revit <year>)
  notice: ...        (0..n)
  warn: <CODE> ...   (0..10, then "warn: +N more")
  <one line minified JSON>
  more: <tool> {"page":"p7"}
  next: <exact call>
  ```
  STATUS is `ok`, `NOT APPLIED - needs the user's OK`, `running` or `ERROR <CODE>`.
- **Paging hint in the summary.** Paged results repeat the page token in the summary ("showing 1-50 of 1,234, more p4"), because Codex middle-truncates long outputs.
- **Caps.** 12,000 B of text by default and 40,000 B with `detail:"full"`. W3-E2E measures the Codex `tool_output_token_limit` default on 0.153/0.159 and lowers the full cap if needed (§12.3).
- **Write results** always carry `created, modified, deleted, warnings, undo, handle, write`.
- **NOT APPLIED results:**
  ```
  NOT APPLIED - needs the user's OK: delete would remove 57 elements (32 targets + 25 dependents) - doc: Tower_A (Revit 2024)
  {"delete":{"total":57,"by_category":{"Walls":32,"Doors":18,"Door Tags":7},"sample":[304512,304513]},"confirm":"K7QM2X","expires_min":10}
  next: ask the user, then modify_elements {"op":"delete","confirm":"K7QM2X"}
  ```
- **Image blocks:** `{type:"image", mimeType, data, _meta:{"codex/imageDetail":"high"}}` (V22 verifies Codex reads block `_meta`).

### 5.6 Errors

```
ERROR <CODE>: <what happened, with concrete names/values>
fix: <rendered from the ErrorCatalog template>
options: 1) ... 2) ...                       (optional)
{"details":...}                              (optional)
```

`isError:true` only for `ERROR`. `NOT APPLIED` and `running` are not errors.

### 5.7 Server instructions (exact; `SPEC-instructions.mjs`)

Head, 504 characters (self-contained within Codex's 512):

```
Revit tools. Start with status: it lists open docs (#1, #2...), the target doc and the active view. Lengths mm, angles degrees, points [x,y] or [x,y,z]; names work where ids do. Writes apply at once and return ids plus an undo hint; preview:true is a dry run. NOT APPLIED means it needs the user's OK: show the plan; repeat with confirm only if the user asked for exactly this or approves. Every error ends with fix: do exactly that. To see the model, call capture. Never use screen automation for Revit.
```

Continuation (total 1,129 characters):

```
 Target: the first doc used is pinned and every result names it; set_target {doc:"#2"} switches; opening or activating a doc re-pins. find_elements returns a handle r#: pass from:"r3", "selection" or "last" instead of copying ids. Totals are exact; continue lists with the page token in a more: line. In op lists, params after ';' are optional; help {tool,op} gives a working example. change_set runs several ops as one undo step ('$0' = first result id). Long work returns a job j#: call job_status. Never repeat a write that is running or unknown (w#); call job_status. Never edit the revit-mcp-next settings.json yourself.
```

The same text is served by `initialize` and `server/discover`, and it is the head of `help {}`.

**Elicitation** (optional, `useElicitation:true`): on NOT APPLIED, if the client declared elicitation, the broker asks through the SDK's `inputRequired()`, which works in both eras. Accept applies in the same call. It is off by default.

### 5.8 Prompts and resources

- **Prompts.** `start_workflow` and `workflow {name}`, where name is one of audit, selection-update, sheet-planning, family-placement, room-layout, visual-check, family-edit, documentation.
- **Resources.** `revit://help/overview` and template `revit://help/{name}`, with completion over tool names, `tool.op` and topics.
- Content equals `help` output.

### 5.9 Behaviour overrides per tool

The table lists only what differs from D1 §4B and D3 §3.

| Tool / op | Normative behaviour |
|---|---|
| status | Reads only control `snapshot` + `health` of every live instance, in parallel, 800 ms each (the budget is 5 s). Compact output follows D1 §7.4: instances `[year,pid,state]`; docs table `#, title, kind, year, flags`; target; active view; selection count; levels of the target doc (≤30). No paths and no broker/auth internals unless `detail:"full"`. `include` sections call other ops with a shared 10 s budget: views=list.views, selection=find_elements from=selection, view_elements=find_elements view=active group_by=category, readiness=check_model.readiness, context=list project_info/phases/worksets/links, warnings=check_model.warnings, writes=control recent_writes. Busy instances give `unavailable: REVIT_BUSY` inline. When code execution is enabled, add `notice: CODE_EXECUTION_ENABLED since <settings mtime>`. `detail:"full"` adds D2 §17.3 plus `codeExecution{enabled, enabledSinceUtc, consented per instance}`, catalog mismatches, settings summary, legacy roots and Codex config findings. |
| set_target | Not read-only. It acts as a per-session barrier: calls arriving while a set_target is in flight wait for it. Grammar: §7.3. |
| list | Adds `graphical`, `printable` (views: `!IsTemplate && CanBePrinted` and view type not Schedule/ProjectBrowser/SystemBrowser/Internal) and `class` (types). |
| find_elements | `where` compiles to `ElementParameterFilter` rules where possible. The category column uses English labels. The handle `r#` stores the full id list, up to 100,000, in the broker. Pages re-query rows for id slices, so paging is stable. |
| read_schedule | Rows come from `GetCellText`. An `id` column is emitted **only** when the `key` field (default Mark) is in the schedule, is itemized, and its values map 1:1 onto the collector's elements (`FilteredElementCollector(doc, schedule.Id)` elements' key values are unique, and each row's key cell matches exactly one). Otherwise there is no id column: `data.elementIds` holds the collector ids plus `notice: rows have no ids (<reason>)`. `available` + `name` lists schedulable fields (TempScope probe, cached per (doc, category)). |
| read_many | Takes one `Deadline` for the whole call. Sub-call i gets `remaining/(n-i)`. A sub-call that cannot finish returns `[i] unavailable: BUDGET - run alone: <tool> <args>`. At most 2 images and 2 MB image data in total; further captures return `[i] image omitted (limit) - run alone: capture {...}`. |
| capture | §10.1. |
| ui select/zoom_to/open_views/close_views | These need the target doc UI-active, otherwise `NEEDS_ACTIVE_DOC` (fix `ui activate_doc`). They never switch the user's doc implicitly. |
| ui activate_view | Activates the view of the target doc. If the doc is not active, activates the doc first (explicit request) and re-pins (§7.4). |
| ui activate_doc / manage_document activate | Re-pins (§7.4). |
| ui isolate/hide/reset | Run in `MCP ui <op>` transactions. The result says "temporary view mode; undo is not needed". |
| ui dialogs/press | §8.5. |
| create_elements opening | Wall host: the start/end projection spans the opening; sill = `offset`; height = `height` (default 2100); `NewOpening(wall, p1, p2)`. Floor/roof/ceiling host: `points` outline. |
| modify_elements split | MEP curves use `PlumbingUtils.BreakCurve` / `MechanicalUtils.BreakCurve`. Walls and structural framing: copy the element (`ElementTransformUtils.CopyElement` with a zero vector), trim both location curves, then in each half delete the hosted inserts (and their tags) whose host parameter lies outside that half's range. If an insert spans the split point, return `UNSUPPORTED_OP` ("an insert spans the split point; move the split point"). S40 asserts the insert count is unchanged. |
| modify_elements delete | `expect_ids`: abort unless the delete set is exactly these ids. |
| set_parameters | Bare numbers on lengths are mm (never feet). English and localized parameter names both resolve (§9.7). |
| edit_family (family=) | `overwrite` defaults to false. It becomes true only when this call's ops changed type values (`set_values`, `add_type` values, `add_param` with value) or when the caller sets it. It means "overwrite project type parameter values". The result lists `types_changed:[...]`. After an `add_param` reload, project-side type values of other params survive (S70). |
| edit_family load_into | Defaults to the session's project pin only (§7.4 `projectPin`). Other projects must be named in `into`. More than one target doc gives NOT APPLIED (rule multi_doc). |
| edit_family open / manage_document open, new_project, new_family | Re-pin (§7.4). `new_project` gets `activate` (default true, old create_project_from_template parity): `OpenAndActivateDocument` after `SaveAs`. |
| manage_document open (central) | `BasicFileInfo.Extract(path)`: when `IsWorkshared` and the file is the central (V19 fixes the property name on 2024/2027), run `WorksharingUtils.CreateNewLocal(central, local)`. `local` defaults to `%USERPROFILE%\Documents\<name>_<username>.rvt`; if that file exists, open it and add `notice: opened existing local`. Then open the local. `central:true` opens the central itself, which is NOT APPLIED first (rule central_open). The local's key equals the central key. |
| manage_document close | Never closes the UI-active doc directly. If another doc is open: job step 1 activates it, job step 2 (next Execute pass) closes the target (V26). If it is the only doc: `CANNOT_CLOSE_ACTIVE`. There is never a placeholder document. Unsaved changes with `save:false` give NOT APPLIED. |
| check_model warnings | `severity` filters warning or error. |
| undo | §6.9. |
| change_set | §6.7. |
| run_csharp | §11. |

---

## 6. Write semantics

### 6.1 One call applies

1. Broker: normalize (§5.3), resolve the target (§7) and expand `r#` and `last` to ids.
2. Add-in `ChangeEngine`: resolve names, validate, execute in a `WriteScope`.
3. **Blast evaluation after execution.** Evaluate §6.3 from the recorded created/modified/deleted sets **before** committing the group. If a rule triggers and the request carries no `confirmed`:
   - roll back the group;
   - return `needsConfirm {rule, plan, blast, stamp, deleteSet}`.
   The broker stores a confirm token and renders NOT APPLIED.
4. Otherwise commit (Assimilate) and return changes, warnings and outputs. The broker allocates handle `r#`, keeps `last` and renders the result.

In workshared models, execution can borrow elements the user does not own but that are available. A NOT APPLIED result may therefore have borrowed them; they are released at the next sync. This is documented in `help {topic:"confirm"}`. Preview mode (§6.2) never borrows.

### 6.2 `preview:true`

Same pipeline inside a `TempScope` (group always rolled back). For NotOwned elements in workshared docs the engine performs static validation only and notes `not probed: n elements owned by <user>`. Lifecycle ops return a static plan. The result is `ok: preview ...` with plan, counts, delete set sample, expected warnings and a confirm token. Nothing is committed.

### 6.3 Blast rules

Thresholds come from settings `confirm.*`.

| rule | trigger | evaluated by |
|---|---|---|
| delete | delete set (targets + cascade) > deleteOver | add-in (recorded deletes) |
| bulk | existing elements modified (or instances affected by type edits) > bulkOver | add-in |
| create | created > createOver | add-in |
| always | worksharing sync/enable, manage_document coordinates, links remove/acquire_coordinates, model_delivery execute/fixture | broker (static plan; no add-in call) |
| file_overwrite | save_as/new_project/new_family/edit_family save_as/export would overwrite existing files | add-in (File.Exists) |
| unsaved_close | close with unsaved changes and save:false | add-in |
| multi_doc | edit_family load_into > 1 doc | broker |
| central_open | manage_document open central:true | broker |
| code_commit | run_csharp commit | §11 |
| button | ui press of a non-Cancel/Close button | add-in control press |

A change_set is confirmed once for the aggregate.

### 6.4 Confirm tokens

- **Format and lifetime.** 6 chars from `ABCDEFGHJKMNPQRSTVWXYZ23456789`, case-insensitive. Stored in the broker session: {tool, key(s), normalized args, docKey, stamp, deleteSet}. Single use, TTL 10 min, ≤32 per session.
- **Apply.** A confirm call sends the stored args plus `confirmed{stamp, deleteSet}`. The add-in re-executes:
  - `CONFIRM_STALE` if any affected element's stamp > plan stamp, or if the new delete set differs;
  - unrelated user edits do not invalidate the token.
- **Mismatch.** Re-sent args that differ from the stored ones give `CONFIRM_MISMATCH` (fix: send only op + confirm).
- **Generation pins.** `expectedGeneration` is removed from direct writes. The only staleness check is the per-element stamp.

### 6.5 Selectors

D1 §5.6, with these specifics:
- **`selection`** is the last known UI selection of the target doc from DocumentRegistry. If the doc is not UI-active, the result adds `notice: SELECTION_STALE as of <time>`.
- **`last`** is resolved by the broker from this session's last successful write on that doc.
- **Handles.** Link handles are refused for writes (`LINK_READ_ONLY`). Stale ids are skipped with `warn: IDS_GONE`.

### 6.6 Short ids (broker-allocated, session-unique)

| id | maps to | TTL | expired |
|---|---|---|---|
| `#n` | document (stable per broker session, never reused) | while open | TARGET_CLOSED |
| `r#` | {docKey, rid, instanceId, ids ≤100k} | 60 min, LRU 64 | HANDLE_EXPIRED |
| `p#` | {tool, args, handle?, offset} | 30 min, LRU 128 | PAGE_EXPIRED |
| `c#` | {folder, file, view, size, region, docKey} | 200 per session | CAPTURE_GONE |
| `w#` | {instanceId, requestId (ULID), key, docKey} | session | OUTCOME_EXPIRED |
| `j#` | {instanceId, add-in jobId or requestId (READ_STILL_RUNNING)} | 24 h | JOB_UNKNOWN |
| `m#` | doc generation of the target doc (valid while the rid is unchanged) | while open | HANDLE_EXPIRED |
| token | confirm plan | 10 min | CONFIRM_EXPIRED |

- **Add-in ids stay internal.** Add-in job ids and request ids are never shown.
- **After a broker restart** the short ids are gone. `job_status` with an unknown id returns JOB_UNKNOWN with fix `status {include:["writes"]}`, which lists recent writes of all sessions from the add-in ledger. Recovered in-flight writes appear as `notice: RECOVERED_WRITE`.

### 6.7 change_set = WriteScope

- **Broker.** Validates every op with its tool's per-op rules. The `$` references (`$N`, `$N.ids`, `$N.ids[k]`, `$N.<output>`, `$prev`, `$$`) are checked for syntax and backward-only indices. Refs are resolved by the add-in at run time.
- **Add-in engine.** One `TransactionGroup("MCP <wtag> change_set ...")`, Assimilate on success, RollBack on failure. Per op:
  - `tx:in`: its own Transaction (commit per op for per-op warning attribution; `strict` ops roll back the whole group on warnings);
  - `tx:own`: no open Transaction; the handler opens its own edit scope (stairs, V8b);
  - `tx:none`: runs between Transactions (edit_family family segments).
  Ops with `cs:false` are rejected before anything runs, with INVALID_ARGS whose fix names the standalone call. These are lifecycle ops: open, save, save_as, close, sync, reload_latest, relinquish, enable, borrow, links reload/reload_from/unload, export, model_delivery, edit_family open/save/save_as/load_into, undo, run_csharp.
- **Family segments** (`Core/ChangeEngine.FamilySegments.cs`). Consecutive `edit_family` ops on the same `family=` share one `EditFamily`. Each op is its own famDoc Transaction. After the last consecutive op, `famDoc.LoadFamily(P, options)` runs with no open project Transaction, then `Close(false)`.
  - This is atomic with the group **only if V6 passes** (group rollback reverts LoadFamily).
  - If V6 fails, the engine moves family segments to the end of the set, and `help {topic:"confirm"}` documents that a family reload in a change_set is not rolled back and that ops depending on new family params need a second call.
- **Output:** `{ops:[[i,"tool.op",primaryId,count]], created, modified, deleted, handle, write}` plus `warn: [i] ...`.

### 6.8 Jobs and budget

D1 §5.7 and D2 §12, with these changes:
- **Budget.** The per-call budget is `callBudgetMs` (50 s, clamped 10-55 s).
- **Job ops.** Ops with `job:always` start as jobs. The broker waits inline and returns the final result when it is ready within the budget, else `running: ... as job j#`.
- **Reads.** `job:auto` reads that cannot yield and are still executing at the budget return `READ_STILL_RUNNING j#`. The broker keeps waiting in the background, caches the result for 10 min keyed by (key, canonical args, docKey), and returns it from `job_status j#` or from a repeat of the identical call.
- **Writes** still executing at the budget return `WRITE_STILL_RUNNING w#`.
- **Progress.** `notifications/progress` goes out every ≤3 s when the client sent a progressToken, using SDK 2.2's progress API.

### 6.9 undo

- **Mode auto (default).** The target doc must be UI-active, else `NEEDS_ACTIVE_DOC` (fix `ui activate_doc`). The doc is never switched implicitly.
- **Journal.** DocumentRegistry keeps a per-doc transaction journal from `DocumentChanged`: {names, operation Committed/Undone/Redone, ours: requestId/writeTag/clientKey or null}.
- **Undo run:**
  1. The newest non-`ui` entries, `steps` of them, must all belong to this session; `MCP ui` entries on top are undone transparently and not counted.
  2. Each step is one `PostCommand(Undo)` issued from an ExternalEvent pass.
  3. The request is held open (queue deferral, §9.4) until `DocumentChanged(TransactionUndone)` with the expected name arrives, within 10 s (else UNDO_UNCONFIRMED).
  4. The next step is posted in the next pass.
- **Redo** works the same way with the redo top.
- **Blocked.** A foreign entry on top gives `UNDO_BLOCKED` naming it.
- **Mode compensate.** New write `MCP <wtag> undo.compensate` driven by the UndoLedger (created → delete, param before values → restore, transforms → inverse). Irreversible entries (deletes, type changes, family loads, purge, code) give IRREVERSIBLE.
- **Experimental stack.** The UIFrameworkServices path (D3 §3.12 step 1) runs only when `experimentalUndoStack:true`, and only after V12 passes on 2024 and 2027.

---

## 7. Targeting (decision F, final)

### 7.1 Pin model

Per broker session the broker keeps `pin {key, rid, instanceId, pid, year, mode: auto|explicit|follow, title, kind}` and `projectPin` (the most recent project pin; used by `edit_family load_into` and by project-only tools while the pin is a family doc). Rebind follows D2 §9.4 (fast path by rid, key and alias match, REBOUND/SAVED_AS notices, TARGET_CLOSED/DOC_NOT_OPEN, NO_REVIT_RUNNING). The pin is not persisted across broker restarts.

### 7.2 Auto-resolve

Follows D2 §10.2: the only project doc, else the UI-active project of the most recently active Revit window (by `ui.lastForegroundAtUtc`/`lastViewActivatedAtUtc`), else the only family doc, else NO_OPEN_DOCUMENT. Family docs are never auto-picked while a project is open. Resolution never waits for the UI thread (snapshots only). The docKind rules follow D2 §10.2.

### 7.3 The `doc` parameter and set_target grammar

`#3` or `3`, `active`, `follow` (set_target only), `none` (set_target only), exact title (case-insensitive, extension optional), absolute path, or `title@2027`.
- **Reads** also accept a unique title prefix or unique contains match.
- **Writes** (any kind other than read/control) accept exact matches only. Anything else gives `TARGET_AMBIGUOUS` with numbered options.
- A per-call `doc` never moves the pin.

### 7.4 Re-pin and gates

1. **Explicit re-pin.** A successful `set_target` re-pins. So does any successful call by this session to `ui.activate_doc`, `ui.activate_view` (when it had to activate the doc), `manage_document.activate|open|new_project|new_family`, or `edit_family.open`. The new pin is explicit, with `notice: TARGET_NOW #3 Site (Revit 2027)`. `projectPin` follows when the new pin is a project.
2. **Closing the pinned doc** with `manage_document close` clears the pin (mode auto), with a notice.
3. **Auto pin moved** (the doc closed, or it was not reopened after a restart and auto-resolve picked another): the first write returns `TARGET_CHANGED` with nothing written. Repeating the identical call proceeds. The acknowledgement is remembered per (session, newKey) for 10 min.
4. **Follow mode:** the first write after the resolved doc changed gets the same gate.
5. **Auto pin + user switched the active project doc** since this session's last write: the first write returns `TARGET_CHANGED` with `{pinned, active}`. The fix repeats the call to write into the pinned doc, or `set_target {doc:"active"}`. Explicit pins get no gate, only the `ACTIVE_DIFFERS` notice once per change of the active doc.
6. `set_target` is not read-only (§5.1), so Codex serializes it with the writes of a turn.

### 7.5 Edge cases

D2 §10.5 applies. Where D2 says a write carrying `expectedGeneration` gets TARGET_GENERATION_CHANGED, delete that rule (§6.4).

---

## 8. Reliability (decision G, final)

D2 is normative for mechanisms and constants. The overrides:

### 8.1 Invariants

D2 §0 applies with two additions:
- **Invariant 8.** A queued call never waits more than `stall.busyFailFastMs` (8 s) behind work it does not own (§8.8).
- **Invariant 9.** Nothing presses a dialog button that can discard user work (§8.5).

### 8.2 Wake-up pump

D2 §5 applies: WM_NULL after Raise, a 200 ms re-raise watchdog while pending, Idling permanently subscribed with an early return, `SetRaiseWithoutDelay` and the idling fallback only while pending, and only for `idle:true` ops. The Idling handler also records `lastIdlingAtUtc`.

### 8.3 Pipe host and admission

D2 §5.6 applies: 16 primary and 4 control instances, `MaxPending` 32, 8 per clientKey, `REVIT_QUEUE_FULL` immediately, `WaitForPipeDrain`, and client_gone logging.

### 8.4 Failure handling

D2 §7 plus D3 §2.8. `FailureCapture` records every failure and deletes warnings from the accessor (so no dialog appears), but returns them in the result. Error-level failures roll back. The options `SetClearAfterRollback(true)`, `SetForcedModalHandling(false)` and `SetDelayedMiniWarnings(false)` apply on every transaction, including model delivery. The global FailuresProcessing safety net acts only on `MCP ` names while an MCP item executes.

### 8.5 Dialogs

**Auto-responder.** D2 §6.1-§6.3: Tier A/B, the allowlist, the denylist, and the `REVIT_DIALOG_ANSWERED` warning. Additions:
- Ids starting `RevitMcpNext_` (our own dialogs, such as code-execution consent) are never answered.
- `dialogPolicy.extra` cannot add denylisted or `RevitMcpNext_` ids.

**`ui dialogs`** (control op, no UI thread):
- Enumerates the enabled, visible top-level windows owned by each Revit main window. `instance` filters; the default is every live instance.
- Reads buttons through UI Automation: `AutomationElement.FromHandle(hwnd)`, descendants with `ControlType.Button` (TaskDialog command links included), `Name` with `&` stripped, and `AutomationId`.
- The UIA work runs on a dedicated STA thread with a 2 s timeout. On timeout the dialog is still listed, with `buttons:[]` and title text only.

**`ui press`** (control op):
- Cancel and Close (and German Abbrechen and Schließen) apply at once.
- Any other button returns NOT APPLIED (rule button) with the dialog title, text and buttons, and needs `confirm`.
- These are refused always, even with confirm, with DIALOG_PROTECTED and fix "ask the user":
  - the buttons No/Nein, Do not save/Nicht speichern and Yes/Ja on dialogs whose id, title or text matches save, sync or delete (English and German: `save|speicher|synchron|sync|delete|lösch`);
  - dialogs with id prefix `RevitMcpNext_` or our consent title;
  - denylisted ids.
- **Mechanism order:**
  1. UIA `InvokePattern.Invoke()` (2 s timeout);
  2. for TaskDialogs, `SendMessage(hwnd, TDM_CLICK_BUTTON = WM_USER+102, id, 0)`;
  3. `WM_COMMAND(id)` to the dialog;
  4. `BM_CLICK` to a child button hwnd.
- **Verification.** V17 checks this on 2024 and 2027 in EN and DE UIs (R7).

### 8.6 Document snapshot

D2 §8 applies, with the §4.6.5 schema (levels, selection, lastIdlingAtUtc, executing, codeExecution). `status` reads only snapshots and health (§5.9).

### 8.7 Instance registry and transport

D2 §2.4, §11.1-§11.6 apply: lock liveness, last-good cache, derived pipe names, no legacy instance, 3 s connect timer, error mapping, coalescing reads, one auto-retry for reads only.

### 8.8 Busy classification and fail-fast (replaces D2 §11.7 and §12.2 queue behaviour)

While a request of this session is queued, the broker polls control `health` every 1 s and evaluates these rules **in order**. The first match wins.

1. **Executing MCP op** (from health):
   - If it is this session's own outstanding write tool call on the same doc (the read-after-write barrier), keep waiting until the budget, with progress "waiting for your write w17".
   - Else, if the executing item is ≥5 s old, is a job step, or a native op is running: once this request has waited `stall.busyFailFastMs` (8 s), cancel it through control `cancel_request` and return `REVIT_BUSY {executing op, elapsed, ref}`. `ref` is this session's `j#`/`w#` when the work is ours; otherwise the text says "another session".
   - Else keep waiting.
2. **`native != null`** (sync, open, save, export, print, reload): after 3 s, cancel and return `REVIT_BUSY` with the reason and progress.
3. **Dialog:** `mainWindowEnabled == false` on two consecutive samples, and `popup.isProgress == false`. `isProgress` means the popup has a `msctls_progress32` child, or its title matches `progress|fortschritt|loading|laden|export|opening|öffnen|synchron`. After `stall.dialogMs`, cancel and return `REVIT_DIALOG_OPEN` with title, text, id and buttons.
4. **Hung:** `hung == true` for `stall.hungMs`: cancel and return `REVIT_NOT_RESPONDING`.
5. **Edit mode:** not executing, enabled, not hung, no native, still pending, and `lastIdlingAtUtc` older than `stall.editModeMs` (6 s): cancel and return `REVIT_EDIT_MODE_OR_COMMAND` ("Revit is probably in a command or edit mode").
6. **Otherwise** keep waiting. At the budget: cancel, and return `REVIT_BUSY` reason "not picked up".

In every error case the queued item is cancelled first, so "nothing ran" is true. Acceptance is D4 R5: a doc read during a 30 s UI block returns REVIT_BUSY in ≤10 s, 24 parallel reads return in ≤12 s, and a write sent during the block is never executed.

**Barrier and jobs.** The read-after-write barrier applies only while this session's write *tool call* is outstanding. While a write became a job or a native op runs, reads fail fast (rule 1 or 2) with `REVIT_BUSY (job j#)`.

### 8.9 Budget allocation

D2 §12.2 applies, with the budget clamped to 10-55 s and the limiter wait `min(8 s, remaining − 5 s)`.

### 8.10 Ledger and in-flight journal

D2 §13 applies, with these changes:
- The ledger is keyed by requestId (ULID).
- The add-in stores the broker's `writeTag` and `clientKey` in each entry but never allocates short ids.
- Persistence: `<home>\ledger\<instanceId>.jsonl`.
- After an instance died, the broker searches all ledger files by requestId.
- The broker's in-flight journal persists `{requestId, shortId, key, instanceId, docKey}`.

### 8.11 Crash guards and logs

D2 §14 applies (broker console redirect, rejection/exception guards, `safe()` callbacks, idempotent shutdown ≤2 s after stdin EOF, add-in handlers wrapped, background JSONL loggers with correlated requestId).

### 8.12 Codex concurrency

D2 §16 applies with §7.4 (item 6) and §8.8. Reads run concurrently (limiter and coalescer); `set_target`, `ui` and writes are serialized by Codex because they are not read-only.

### 8.13 SDK 2.2.0

D2 §15 steps 1, 3, 4, 5 apply. Step 2 is dropped because validate-repo is deleted. Pin `@modelcontextprotocol/server|core|client` at exactly `2.2.0`. `engines.node` becomes `>=24` (root and workspaces).

---

## 9. Add-in structure (decision H, final)

### 9.1 Layout

This is D3 §2.3, canonical, with these fixed paths:

```
addin/RevitMcpNext.Loader/            NEW: loader project (net48 / net10.0-windows by RevitYear), §9.2
addin/RevitMcpNext.Contracts/         BridgeContracts.cs = protocol v3 (§4.6)
addin/RevitMcpNext.Scripting/         NEW (P-CONTROL): Roslyn host, loaded lazily (§11)
addin/RevitMcpNext.Addin/
  RevitMcpApplication.cs              event wiring
  Core/        RevitExternalEventHandler.cs (class decl, Execute/ExecuteBatch), OpAttribute.cs, OperationRegistry.cs,
               RequestContext.cs (merges D2 §12.3 and D3 §2.7 members), OpResult.cs, ErrorCodes.cs, Naming.cs,
               TransactionService.cs -> WriteScope.cs, TempScope.cs, FailureCapture.cs, ChangeContext.cs, ChangeEngine.cs,
               ChangeEngine.FamilySegments.cs, Handler.Control.cs, Handler.Responses.cs, Handler.Targeting.cs, Handler.Probes.cs
  Common/      PayloadReader.cs, Units.cs, ParamValues.cs, Geometry.cs, Resolve.cs, ElementQuery.cs, Rows.cs, PurgeUtil.cs,
               WorksharingGuard.cs, OutputJson.cs, Aliases/aliases-2024.json, Aliases/aliases-2027.json (embedded)
  Compat/      RevitCompat.cs, RevitVersion.cs (all #if and reflection shims)
  Serialization/ Handler.Snapshots.cs, Handler.FieldSets.cs
  Runtime/     McpHome.cs, Settings.cs, AuthTokenStore.cs, DocumentRegistry.cs (snapshot, keys, rid, aliases, generation,
               levels, selection, change ring, element stamps, txn journal), JobRunner.cs, RevitRequestQueue.cs,
               QueuedRevitWorkItem.cs, ExternalEventPump.cs, DialogPolicy.cs, DialogUia.cs, UiStateMonitor.cs
  Native/      NativeMethods.cs
  Ipc/         NamedPipeHost.cs, ControlOperations.cs, RuntimeInstanceRegistration.cs, RequestOutcomeLedger.cs, JsonWireCodec.cs,
               PipeNameProvider.cs, PipeSecurityFactory.cs, FramedPipeTransport.cs, BridgeProtocolGuard.cs
  Diagnostics/ DiagnosticsLogger.cs, LatencyHistogram.cs
  Integration/ RevitMcpInProcessBridge.cs
  Legacy/      old change-set types and PreviewTokenStore/CanonicalJson while referenced (deleted in wave 3)
  Domains/     Status, Documents, Worksharing, Links, Delivery, Export, Reads, Health, Model, Mep, Structure, Modify, Views,
               Visual, Ui, Sheets, Annotation, Schedules, Families, Undo, Code, TestOps, Dev
```

**Split procedure (W1-ADDIN).**
1. Run `split-handler.mjs` (design folder) on the unchanged HEAD file, with one change to its map: `"Domains/Families/Ops.LoadFamily.cs"` becomes `"Domains/Model/Ops.LoadFamily.cs"`.
2. Do the D3 §2.4 whole-file moves: `PreviewTokenStore.cs` goes to `Legacy/`; `CanonicalJson.cs` goes to `Legacy/` too, since only legacy code uses it.
3. Move `Domains/ChangeSets/Handler.ChangeSet.cs` to `Legacy/` after deleting its dispatch methods (PreviewOperation, ApplyOperation, HandlePreviewChangeSet, HandleApplyChangeSet).
4. Delete the old `Handle` switch and `ExpectedOperationKinds`.

Old per-op functions stay in their domain files until the owning lane rewrites them. Lanes may delete legacy members only in files they own, and only when nothing outside their files references them. Lanes never newly reference legacy members in other lanes' files.

### 9.2 Loader

`RevitMcpNext.Loader.dll` implements `IExternalApplication` and has no other dependencies.

- **OnStartup:**
  1. Resolve the home: env `REVIT_MCP_NEXT_HOME`, else its own `<home>` derived from its path (with marker check).
  2. Read `<home>\addin\<year>\current.json` and set `REVIT_MCP_NEXT_HOME` and `REVIT_MCP_NEXT_PAYLOAD_DIR` in-process.
  3. Register assembly resolution for `RevitMcpNext.*` from the payload dir: `AppDomain.AssemblyResolve` on net48; on net10, `AssemblyLoadContext.GetLoadContext(loaderAssembly).Resolving`.
  4. `Assembly.LoadFrom(payload\RevitMcpNext.Addin.dll)`, instantiate `RevitMcpNext.Addin.RevitMcpApplication` and forward OnStartup/OnShutdown.
- **On any failure:** write `<home>\logs\loader-<year>.log` and return `Result.Succeeded`. It never shows a dialog and never blocks Revit.
- **Manifest.** `Assembly` = loader path, `FullClassName` = `RevitMcpNext.Loader.LoaderApplication`, same ClientId (V16 checks trust carries over).
- **`scripts/dev-install.ps1 -Home <dir> -RevitYears 2024,2027`:**
  1. Copies `artifacts\addin\<year>\*` to `<dir>\addin\<year>\<payloadId>\` (payloadId = first 12 hex of sha256 over sorted `name:sha256` lines).
  2. Writes `current.json` and the marker.
  3. Ensures `auth.env` exists.
  4. Ensures the loader exists in the default home and that the manifest points to it. It writes the manifest only when it differs.
  It never touches Codex/Claude config.

### 9.3 OperationRegistry and handler binding

```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class OpAttribute : Attribute { public OpAttribute(string key) { Key = key; } public string Key { get; } }
// Keys: exact ("create_elements.wall") or tool wildcard ("list.*"). Methods: static, in any type of the add-in assembly.
// Shapes:  static OpResult X(RequestContext ctx)   -> kinds read, ui, lifecycle, code (and write ops with tx=lifecycle)
//          static void     X(ChangeContext c)      -> write ops executed by ChangeEngine (tx in|own|none)
```

- **Build.** `OperationRegistry.Build()` reflects once at startup, joins the bindings with the embedded catalog, and performs the §4.2 checks.
- **Dispatch pipeline:**
  1. protocol/auth (host);
  2. key lookup (else UNKNOWN_OP or ADDIN_OUTDATED);
  3. kind check;
  4. `min` year gate (UNSUPPORTED_VERSION);
  5. doc resolution by `{rid,key}` per `scope` (TARGET_STALE, FAMILY_DOC_REQUIRED, PROJECT_DOC_REQUIRED, DOC_READ_ONLY);
  6. `ui` check (NEEDS_ACTIVE_DOC);
  7. the `idle` flag for the Idling fallback;
  8. invoke inside the dialog responder scope, mapping exceptions (D3 B13) to ErrorCodes.
- **In-process bridge.** Callers only reach keys with `inproc:true`.

### 9.4 Queue, deferral and jobs

- **RevitRequestQueue** keeps D2 §5.4 and §5.6 behaviour. It adds `ctx.DeferCompletion(Task<OpResult> t, TimeSpan timeout)`: the item stays executing, no other item starts until `t` completes or the timeout passes, and the pump keeps waking. This is used by undo (PostCommand) and by close step sequencing.
- **JobRunner.** `JobRunner.Start(ctx, IEnumerable<Func<JobStep, StepResult>> steps)` runs one step per Execute pass. It exposes `job_status`/`job_cancel` control ops and persists terminal records. Ops with `job:always|auto` use it.

### 9.5 Shared helpers (W1-ADDIN provides; lanes use them and do not fork them)

| Helper | API summary |
|---|---|
| PayloadReader | lenient `Str/Int/Double/Bool/Mm/Deg/Point/Points/Loops/Ids/Dict/List` with INVALID_ARGS naming the field |
| Units | `MmToFt/FtToMm`, `DegToRad`, `M2`, `M3`, spec-aware `ToInternal(ForgeTypeId spec, double)` and `ToOutput`; rounding per D1 §6.9 |
| ParamValues | `Resolve(Element, key)` (display name English/localized, `bip:`, `guid:`, `id:`) → Parameter; `Set(Parameter, object, units)` with spec conversion; `Get(Parameter)` → output value |
| Resolve | `Level, Type<T>("Family: Type"\|name\|id), View(name\|id\|sheet number\|active), Sheet, Material, FillPattern(solid via IsSolidFill), LinePattern, LineStyle, Workset, Phase, DesignOption, Link, Category(English/localized/OST_)`; every miss gives NOT_FOUND/AMBIGUOUS_NAME with candidates |
| ElementQuery | compiles the find_elements filter (`category, class, level, type, family, view, hidden, workset, phase, design_option, box, where, ids, link, types`) into native quick/slow filters plus post-filters; `Count()`, `Ids(limit)`, `ShouldYield` |
| Rows | table builder `{total, cols, rows}`, default columns per category (D1 §4B find_elements), special fields (location, bbox, host, room, workset, phase, pinned, uid, owner_view) |
| WriteScope / TempScope / FailureCapture | §6, §4.4 names |
| ChangeContext | D3 §2.7 plus `Output(name, value)` and `Warn(code, text, ids)` |
| PurgeUtil | `Candidates(doc, passes)` via `GetUnusedElements` + `DocumentValidation.CanDeleteElement` |
| WorksharingGuard | owned-by-other → NOT_EDITABLE; updated-in-central → NOT_EDITABLE(reason updated_in_central) |
| OutputJson | minified output, non-finite → null + NON_FINITE_NUMBER |

### 9.6 Performance fixes

D3 §4 items 1-17 are in scope. Each lane owns the items in its domain; W1-ADDIN owns items 3, 5, 12, 13 and 17.

### 9.7 English aliases

- **Source.** `Common/Aliases/aliases-<year>.json` holds `{categories: {"Walls":"OST_Walls",...}, parameters: {"Comments":["ALL_MODEL_INSTANCE_COMMENTS"], "Width":["DOOR_WIDTH","WINDOW_WIDTH","FAMILY_WIDTH_PARAM",...]}}`. It is generated once from ENU Revit 2024 and 2027 with `LabelUtils.GetLabelFor(BuiltInCategory/BuiltInParameter)` by the internal op `dev.dump_labels`, run under `/language ENU` (P-READ).
- **Seed.** W1-ADDIN ships a hand-written seed of about 80 common entries, so resolution works before P-READ lands.
- **Resolution order:** exact English label, then case-insensitive English, then localized (`doc.Settings.Categories` / definition names), then `OST_`/`bip:` tokens. With several BIPs per English label, the one present on the element wins.

---

## 9B. Broker structure (W1-BROKER builds it; framework frozen in wave 2, transport dirs owned by P-REL-BROKER)

```
broker/src/index.ts                bootstrap (D2 §14.1 order): console->stderr, crash guards, home, settings, logger, registry,
                                   in-flight recovery (async), serveStdio(factory, {legacy:"serve", onerror}), idempotent shutdown
broker/src/server.ts               McpServer, instructions (SPEC-instructions), cacheHints, prompts, resources, registers ALL_TOOL_MODULES
broker/src/framework/
  registry.ts                      ToolRegistry: catalog lookup, module map, per-op validators, profile filter, run_csharp gate
  normalize.ts  validate.ts        §5.3 (signature-derived validation; uses ParamSpec.unit/synonyms)
  context.ts                       ToolContext implementation
  defaults.ts                      defaultRead / defaultWrite / defaultLifecycle / defaultControl handlers
  render.ts                        ToolOutcome -> CallToolResult text (§5.5), caps, structured flag
  errors.ts                        ErrorCatalog rendering of fix/options (§4.3)
  confirm.ts                       ConfirmTokenStore (§6.4), elicitation hook (useElicitation)
  session.ts                       Session: pin/projectPin, doc numbering, short-id maps (§6.6), last-write per doc, TARGET_CHANGED acks
  images.ts                        readCaptureImage(path) -> image block (path must be under <home>\captures; size cap)
broker/src/runtime/                home.ts auth.ts settings.ts deadline.ts ids.ts (ULID) log.ts crashGuards.ts
broker/src/instances/              InstanceRegistry.ts (§8.7), InstanceChannel.ts
broker/src/ipc/                    PipeClient.ts (v3 frames, connect timer, error map, reconciliation), errors.ts, ConcurrencyLimiter.ts, ReadCoalescer.ts, HealthPoller.ts
broker/src/targeting/              TargetResolver.ts (§7), docParam.ts (grammar §7.3)
broker/src/jobs/                   JobRunner.ts (job_start + poll), ResultCache.ts (READ_STILL_RUNNING)
broker/src/state/                  InflightJournal.ts
broker/src/recipes/                ModelDeliveryRecipeStore.ts (<home>\recipes)
broker/src/tools/index.ts          ALL_TOOL_MODULES: one import per tool file below (pre-populated; never edited in wave 2)
broker/src/tools/<tool>.ts         38 files, one per catalog tool; each exports `module: ToolModule`
```

```ts
export interface ToolModule {
  name: string;                                              // catalog tool name
  handle?: (ctx: ToolContext) => Promise<ToolOutcome>;       // omit to use the default handler for the op's kind
  prepare?: (ctx: ToolContext) => Promise<void>;             // optional pre-call hook (extra validation, arg rewriting)
  finish?: (ctx: ToolContext, r: AddinResult, o: ToolOutcome) => Promise<ToolOutcome>;  // optional post-call hook
}
export interface ToolContext {
  readonly tool: string; readonly op: string | null; readonly key: string; readonly meta: OpMeta;
  args: Record<string, unknown>;                             // normalized + validated (mutable in prepare)
  readonly session: Session; readonly deadline: Deadline; readonly signal: AbortSignal; readonly settings: Settings;
  progress(message: string, done?: number, total?: number): void;
  notice(code: string, text: string): void;
  warn(code: string, text: string, ids?: number[]): void;
  resolveDoc(): Promise<ResolvedDoc | null>;                 // scope rules, doc param grammar, pin, gates (§7)
  call(key?: string, args?: object, o?: { doc?: ResolvedDoc | null; mode?: "apply" | "preview"; asJob?: boolean;
       confirmed?: { stamp: number; deleteSet?: number[] }; wantIds?: boolean }): Promise<AddinResult>;
  control(instance: InstanceRef, op: ControlOp, payload?: object, timeoutMs?: number): Promise<AddinResult>;
  instances(): Promise<InstanceInfo[]>;                      // live instances with snapshots
  outcomeFrom(r: AddinResult): ToolOutcome;                  // standard mapping incl. handles, pages, needsConfirm -> NOT APPLIED
}
export interface ToolOutcome { status: "ok" | "not_applied" | "running" | "error"; summary: string; code?: string;
  data?: unknown; notices?: string[]; warnings?: string[]; images?: ImageBlock[]; more?: string; next?: string; fix?: string;
  options?: string[]; details?: unknown; }
```

- **Default handlers:**
  - `defaultRead`: resolve doc → `call` → page and handle bookkeeping.
  - `defaultWrite`: resolve doc with the write gates → `call` (`mode` from `preview`) → on `needsConfirm`, store a token and return NOT APPLIED → on success, record `last`, allocate `r#` and `w#`, and add the undo hint.
  - `defaultLifecycle`: `always` blast is answered statically; `job:always` uses `asJob` and JobRunner.
- **Tool modules** only add what the default cannot: images (capture), control ops (ui dialogs/press), orchestration (status, read_many, change_set), and broker-only state (help, recipes, job_status).
- **Wave-1 coverage.** Every `broker/src/tools/<tool>.ts` exists after wave 1 and works through the defaults, so an add-in handler landing in wave 2 immediately works end to end.

---

## 10. Visual capture (decision G/R6, final; P-VIEWS-VISUAL)

### 10.1 Capture spec (the only capture table)

| size | long edge max L | megapixel cap | typical sheet 1.414:1 |
|---|---|---|---|
| small | 768 | 0.40 | 752x532 |
| medium (default) | 1280 | 1.00 | 1189x841 |
| large | 1568 | 1.15 | 1275x902 |

- **Target size.** `L_eff = min(L, floor(sqrt(MPcap * aspectLong/Short)))`.
- **Export.** `ImageExportOptions{ExportRange=SetOfViews, SetViewsAndSheets([id]), ZoomType=FitToPage, PixelSize=L_eff, FitDirection by crop aspect, HLRandWFViewsFileType=PNG, ShadowViewsFileType=PNG, FilePath=<unique folder>\image}`. Take the single file produced in the folder; `GetFileName` is only a cross-check, because SetOfViews appends the view type and name.
- **Raster work** (crop, downscale, JPEG, compare diff) is done by the add-in with WPF imaging: `PresentationCore`/`WindowsBase` on net48, `UseWPF` on net10. The broker only reads the file (the path must be under `<home>\captures`) and base64-encodes it.
- **Format.**
  - PNG for hidden, wireframe, sheets and compare.
  - JPEG q85 when the style is shaded, consistent or realistic and the PNG is > 400 KB, or when format=jpg.
  - Cap: encoded file ≤ 1.1 MB (base64 ≤ ~1.5 MB). Above that, re-encode as JPEG q80, then halve L and retry.
- **Temporary changes** (region, highlight, annotations:false, ids/from focus, style, orient) are normative per D3 §3.1:
  1. `TempScope("MCP temp capture")`;
  2. an inner Transaction creates/duplicates/configures the temp view;
  3. commit the inner transaction;
  4. `ExportImage` with only the group open;
  5. `RollBack` the group;
  6. residue check.
  If V1 fails, use fallback B (D3 §3.1).
- **Folders and retention.** Each capture gets its own folder (`<home>\captures\<yyyy-MM-dd>\<HHmmss>-<rand6>\`). Retention: 72 h, 1 GB, 500 folders; the add-in cleans up at startup and hourly on a background thread.
- **Cache.** (docKey, viewId, generation, optionsHash) maps to the file for 10 min (`cacheHit`).
- **Text part:** `ok: captured Level 1 (FloorPlan 1:100) 1189x841 png as c4 - doc: ...` plus `{capture, view, px, bounds, mm_per_px}` (D1 §4B).
- **compare:**
  - The broker resolves `c#` to the earlier file and meta.
  - Same view, size and region only, else INVALID_ARGS.
  - The add-in produces the diff image (changed pixels magenta over a faded base) and `changed{pixels, bbox_px, bbox_model}`.
- **Reliability.** Capture is `job:auto`. At the budget it returns READ_STILL_RUNNING j#, and the repeat returns the cached image.

### 10.2 UI control

D3 §3.2 applies with the §5.9 and §8.5 overrides.

---

## 11. Opt-in C# execution (`run_csharp`, P-CONTROL)

1. **Gate.**
   - `settings.json enableCodeExecution:true`. The broker lists the tool only when this was true at start.
   - The add-in re-reads the gate on every call (mtime cache) and refuses with `CODE_EXECUTION_DISABLED` regardless of the broker.
   - `allowInProcess` is false.
2. **Consent per Revit session.**
   - The first run in each Revit process shows an add-in-owned `TaskDialog` (Id `RevitMcpNext_CodeConsent`, title "Revit MCP Next: allow AI code execution?") with the client name, the code sha and the first 12 lines.
   - Its command links are "Allow for this Revit session" and "Block".
   - The dialog is shown as job step 1 (`job:auto`). A waiting call returns `running ... j#` with `next: ask the user to answer the prompt in Revit, then job_status`.
   - Block gives `CODE_EXECUTION_DECLINED`, and the prompt is not shown again for 10 min.
   - DialogPolicy and `ui press` can never answer this dialog (§8.5).
   - Isolated e2e homes can pre-approve it (§13.4).
3. **Signature.** `public static object Run(Document doc, UIDocument uidoc, Application app, IDictionary<string, object> args, Action<object> log)` in a generated `public static class __McpScript`. The script references only framework assemblies, RevitAPI and RevitAPIUI. `args` holds only BCL types (Dictionary, List, string, double, long, bool).
4. **Loop guard.** A generated `public static class __McpGuard { public static long DeadlineTicks; public static volatile bool Cancel; [ThreadStatic] public static int Depth; public static void Tick() }` is compiled into the script assembly. A `CSharpSyntaxRewriter` inserts `global::__McpGuard.Tick();` in loop bodies and at method/lambda entry. The host sets the fields by reflection on the compiled type before invoking, so the script references nothing of ours. The depth cap is 256.
5. **Modes.**
   - `read`: TransactionGroup always rolled back. Semantic analysis rejects constructing `Transaction`, `SubTransaction`, `TransactionGroup` or any `*EditScope` (CODE_DENIED_API).
   - `dry_run` (default): TransactionGroup always rolled back. The host opens `Transaction("MCP temp run_csharp")` unless the code constructs its own. It returns the value, log (≤ maxOutputKB), DocumentChanged-derived changes, warnings and a confirm token.
   - `commit`: needs the dry run's token, which is bound to sha256(code), sha256(canonical args) and docKey, stored in the broker with the code and args, TTL 10 min. It is NOT APPLIED semantics: the dry-run result's `next:` is "ask the user, then run_csharp {"mode":"commit","confirm":"<token>"}". The code is not re-sent. It runs in `TransactionGroup("MCP <wtag> run_csharp")` + Assimilate, and is recorded in the UndoLedger as irreversible-by-compensation.
6. **Deny lists** (semantic model, not regex):
   - **Always:** `System.Diagnostics.Process`, `System.Net.*`, `Microsoft.Win32.*`, `Marshal`/`DllImport`/`unsafe`, `System.Reflection.Emit`, `Assembly.Load*`, `AppDomain`, `Environment.Exit/FailFast`, `Thread`/`ThreadPool`/`Task.Run`/`Parallel`, `File`/`Directory`/`FileInfo` write/delete/move/copy/create members, `UIApplication.PostCommand`, `UIFrameworkServices`, `AdWindows`, `System.Windows.Forms`/`System.Windows.Window` construction.
   - **Lifecycle** (read and dry_run always; commit unless `codeExecution.allowUnsafeApis`): `Document.Save/SaveAs/SaveCloudModel/Close/SynchronizeWithCentral/ReloadLatest/Export/ExportImage/EnableWorksharing/EditFamily`, `Application.OpenDocumentFile/NewProjectDocument/NewFamilyDocument`, `UIApplication.OpenAndActivateDocument`, `RevitLinkType.Reload/Unload/LoadFrom`, `WorksharingUtils.*`.
7. **Loading.**
   - `RevitMcpNext.Scripting.dll` (Roslyn `Microsoft.CodeAnalysis.CSharp` 4.8.0) sits in `<payload>\scripting\` and implements `IScriptCompiler` from `RevitMcpNext.Contracts`. `RevitMcpNext.Contracts.dll` is **not** copied into `scripting\`.
   - **net48:** `Assembly.LoadFrom`, plus an `AppDomain.AssemblyResolve` handler limited to names starting `RevitMcpNext.` that returns the already-loaded assemblies. Script assemblies load with `Assembly.Load(bytes)` and are cached by sha.
   - **net10:** a private non-collectible ALC for Roslyn via `AssemblyDependencyResolver`. Its `Load` returns `AssemblyLoadContext.GetLoadContext(typeof(ScriptHost).Assembly).LoadFromAssemblyName` results for `RevitMcpNext.*`, and null for framework and Revit assemblies. Scripts get a collectible ALC per run.
   - Spike V13 runs first, with pyRevit and Dynamo loaded. If it fails, use the out-of-process csc fallback (D3 §3.13.9) behind `codeExecution.experimentalOutOfProcess`.
8. **Timeout.** `timeout` 1-45 s (default 30, capped by `codeExecution.timeoutSec`), cooperative.
9. **Output.** Return value depth 4 / 200 items; Element → {id, category, name}; ElementId → number; XYZ → mm array. Compile errors come back as `line:col` relative to the user code.
10. **Audit.** `<home>\logs\code-exec-audit.jsonl` gets `{ts, client, session, revitYear, docKey, docTitle, mode, codeSha256, argsSha256, token?, consent, durationMs, outcome, error?, changes{added,modified,deleted}, warnings}`, and the source goes to `<home>\logs\code\<sha256>.cs`.
11. **status** shows the enablement (§5.9).

---

## 12. Install, Codex and Claude integration (D4 owns it; W3-INSTALL)

D4 §2-§7 are normative, with these overrides.

### 12.1 Payload, bundle and receipt

- **Payload.** The broker ships as the esbuild bundle `<home>\broker\revit-mcp.mjs` (+ `revit-mcp-cli.mjs`). The add-in is loader + payload per §3 and §9.2, and the manifest points to the loader.
- **Home files.** The installer writes both the marker and `install.json`. Instance files follow `r<year>-<pid>-<6hex>.json`. Log names and the settings schema follow §3.
- **No carry-over.** There is no legacy token carry-over, because old brokers cannot speak protocol v3.
- **Migration** follows D4 §4.11. Legacy LocalCache roots are reported and removed only with `-RemoveLegacy`.

### 12.2 Codex TOML

This is text-block surgery per D4 §4.9.

```toml
# managed by revit-mcp-next installer <version> (<sha>)
[mcp_servers.revit]
command = 'C:\Users\Bhavesh\.revit-mcp-next\runtime\node.exe'
args = ['C:\Users\Bhavesh\.revit-mcp-next\broker\revit-mcp.mjs']
startup_timeout_sec = 30
tool_timeout_sec = 120

[mcp_servers.revit.env]
REVIT_MCP_NEXT_HOME = 'C:\Users\Bhavesh\.revit-mcp-next'

[mcp_servers.revit.tools.status]
approval_mode = "auto"
[mcp_servers.revit.tools.help]
approval_mode = "auto"
[mcp_servers.revit.tools.set_target]
approval_mode = "auto"
[mcp_servers.revit.tools.ui]
approval_mode = "auto"
```

- **Approval modes.** Destructive tools stay on the user's policy.
- **Validation.** Verify the per-tool table syntax with `codex mcp get revit --json` (V22). If Codex rejects it, write `[mcp_servers.revit.tools]` inline tables instead.
- **Output limits.** If §5.5 measurement shows the Codex output limit is below 40 KB of text, add `output_token_limit` per tool.

### 12.3 Codex output limits (V22)

W3-E2E measures `tool_output_token_limit` defaults on 0.153 and 0.159 with a `detail:"full"` 40 KB result. If truncation occurs, it lowers the `detail:"full"` cap in `framework/render.ts` (a wave-3 fix right) and records the value in `docs/clients.md`.

### 12.4 Claude

- **Claude Code:** `claude mcp add-json revit '<json>' -s user`.
- **Claude Desktop:** JSON edit per D4.
- **Large captures.** W3-E2E checks that a large (≈1.5 MB base64) capture reaches Claude Code without truncation (`MAX_MCP_OUTPUT_TOKENS`). If it does not, lower the large preset for Claude clients: the broker picks the preset by `clientInfo.name`.

### 12.5 Plugin, skill and AGENTS.md

D4 §7 applies, with the SKILL rules replaced:
- **Rule 5:** "Writes apply at once... If a result says NOT APPLIED, show the user the plan and ask; call again with `confirm` only when the user asked for exactly this or approves."
- **Rule 12:** "run_csharp only when no tool fits and the user agreed; never edit `~\.revit-mcp-next\config\settings.json` or enable code execution yourself."
- **Rule 9:** "REVIT_DIALOG_OPEN: call `ui {"op":"dialogs"}`; press only Cancel or Close yourself; anything else, ask the user."

The AGENTS.md snippet gets the same NOT APPLIED and settings sentences (still under 700 chars). The installer reports the `~/.codex/AGENTS.md` edit in its summary and keeps `-NoAgentsSnippet`.

### 12.6 Conflicting plugins

D4 §7.7 applies. The installer reports the revit-mcp-cowork auto-allow hook and offers `-DisableConflictingPlugins`. The docs explain that while cowork is enabled, Claude Code auto-approves our destructive tools (its hook returns allow).

---

## 13. End-to-end test plan (single harness, D4 §8 + D2 §21 + D3 lane scenarios)

### 13.1 Harness

- **Location.** `e2e/` is the only harness. W1-BROKER creates the skeleton; lanes add scenario files; W3-E2E completes it (coverage gate, profiles, resilience, codex/claude, summary).
- **Scenario contract.** D4 §8.2. `t.call` enforces a 55 s client timeout, parses the text contract, decodes images, fails on structuredContent, and marks coverage.
- **Guard.** Every write asserts that the target doc path is under `<testHome>\runs\<runId>\`.

### 13.2 Homes, Revit instances and slots (shared machine protocol)

- **One home per worktree.** Each lane uses `%USERPROFILE%\.revit-mcp-next-dev-<package-id>` (W3-E2E uses `...-e2e`).
- **Launch.** `node e2e/run.mjs --home <home> --years 2024 --launch --only <ids>`:
  1. dev-installs the lane's build into its home;
  2. launches `C:\Program Files\Autodesk\Revit <y>\Revit.exe` with `REVIT_MCP_NEXT_HOME=<home>`, detached, outside any package;
  3. waits for `instances\r<y>-<pid>-*.json` + `hello` (≤300 s, with journal tail for dialogs, D4 §8.4);
  4. creates disposable models from `C:\ProgramData\Autodesk\RVT <y>\Templates\English\Default-Multi-Discipline_Metric.rte` via `manage_document new_project` into `<home>\runs\<runId>\`.
- **Isolation.** The user's own Revit sessions register in the default home and are invisible to lanes.
- **Slots.** At most `RMN_E2E_SLOTS` (default 4) harness-launched Revit processes machine-wide. Locks are files in `%USERPROFILE%\.revit-mcp-next-slots\slot-<n>.lock`, held exclusively for the Revit lifetime and released when the process exits. `--keep-revit` keeps a warm Revit; rebuilding the add-in requires `--restart`.
- **Kill.** The harness kills only the pids it started (`taskkill /PID <pid> /F`).

### 13.3 Scenarios and owners

Each file is `e2e/scenarios/<ID>-<slug>.mjs`.

| ID | Owner | Content / key assertions |
|---|---|---|
| S00 surface | W1-BROKER (W3 extends) | D4 §8.6 with §15 gates. Eras 2025-06-18, 2025-11-25, 2026-07-28. tools/list equals `artifacts/catalog/tools-list.json`. Instructions head ≤512. Lenient input: `find_elements {ids:[304512]}` and `category:"Walls"` never fail validation. Stdin EOF exits ≤2 s |
| S01 bootstrap | W1-BROKER | launch, hello, status, new_project A (activate true, re-pins), set_target grammar basics, help |
| S20 reads, S21 DEU localization, S89 clashes | P-READ | D4 S20. S21 runs S20/S30/S40 subsets under `/language DEU` with English names (full profile). S89 exact pair + tolerance + link |
| S30 architecture | P-MODEL-ARCH | D4 S30 (create_elements, place_family). Wall opening via model points; toposolid z rule |
| S31 MEP and structure | P-MODEL-MEPSTRUCT | D4 S30 mep/structure part; fittings or FITTING_FAILED warning |
| S32 blast radius, S40 modify | P-MODIFY | D4 S32 (delete 25, set_parameters 250, expect_ids). S40 incl. split insert count unchanged, attach on 2027 / UNSUPPORTED_VERSION on 2024 |
| S50 views, S90 ui, S92 capture | P-VIEWS-VISUAL | D4 S50/S90/S92. S92 asserts long edge ≤ cap and aspect ratio within ±1 %, not exact pixels; base64 ≤ 1.5 MB. S90 press: Cancel works; OK needs confirm; No on a save dialog refused |
| S55 sheets, S60 annotate, S65 schedules | P-SHEETS-ANNO | D4 S55/S60/S65. S65 re-sorts the schedule and asserts the id-to-row mapping is still correct when ids are emitted, or absent with a notice |
| S70 family | P-FAMILY | D4 S70, plus: a project-side type value of another param survives an add_param reload (overwrite false); load_into 2 docs gives NOT APPLIED |
| S75 documents, S78 worksharing, S80 links, S88 purge | P-DOCS | D4 S75/S78/S80/S88. S78 asserts the opened doc is a local (`IsWorkshared && !IsCentral`, path ≠ central) and that the central mtime is unchanged before sync; `central:true` needs confirm. S75 close via job steps |
| S85 export, S96 jobs, S97 delivery | P-EXPORT | D4 S85/S96/S97 |
| S35 change_set, S36 undo, S95 code | P-CONTROL | D4 S35/S36/S95. S95 adds: read-mode `doc.SynchronizeWithCentral`, `doc.Close`, `new Transaction(...).Commit()` are refused (CODE_DENIED_API); the add-in refuses when disabled via the raw pipe; the consent prompt is shown when not pre-approved (via test op `test.show_dialog` watch); the audit line is present in `code-exec-audit.jsonl`. S36: ui isolate then undo undoes the last real write transparently |
| R5 busy, R6 idle, R7 dialogs, R10 registry junk, R12 edit mode, S99 in-process bridge | P-REL-ADDIN | D4 R5/R6/R7/R10 and D2 E1, E2, E9, E10, E12. R5 and R12 run variants with an export and a family reload executing (must report REVIT_BUSY, not dialog or hang). S99 drives `test.inproc_call` (read ok; destructive refused) |
| R1 targeting, R3 two years, R4 kill/relaunch, R8 broker crash, R9 auth, R11 eras, R13 ledger | P-REL-BROKER | D4 R1 (updated per §7.4: activate_doc re-pins with notice; write `doc:"Tow"` gives TARGET_AMBIGUOUS; read prefix ok), R3, R4, R8, R9, R11; D2 E7, E11, E13, E14, E15 (read_many with slow sub-calls stays ≤55 s, unfinished marked BUDGET) |
| W1-W4, T1, codex, claude, coverage gate | W3-E2E | D4 §8.9, §8.10, §8.13 |

### 13.4 Test ops

The test ops are compiled into the release DLL (`Domains/TestOps`, P-REL-ADDIN). They are registered only when the home ≠ `%USERPROFILE%\.revit-mcp-next` **and** `<home>\config\e2e-test-ops.enable` exists:
- `test.sleep_ui {ms}` blocks the UI thread;
- `test.show_dialog {id, title, buttons}` shows a TaskDialog with a non-allowlisted id;
- `test.enter_command {command:"Wall"}` PostCommands a placement tool so Revit sits in a command;
- `test.inproc_call {key, args}` calls through `RevitMcpInProcessBridge`.

Under the same conditions, `codeExecution.e2ePreapproved:true` in settings skips the consent dialog, and isolated homes may raise `codeExecution.timeoutSec` for R5. Users never see these ops.

### 13.5 Profiles, gates and push rules

- **Profiles:** `surface`, `quick`, `full`, `soak`, `codex`, `claude` (D4 §8.11).
- **Coverage gate** (D4 §8.10) binds to Appendix B keys; exemptions go in `e2e/coverage-exemptions.json`.
- **Before every push to main:** `npm run build`, `npm run typecheck`, `node e2e/run.mjs --profile surface`, plus the lane's own scenarios on 2024. Add-in/IPC changes also run on 2027. Surface is a build lint and never replaces the real-Revit run.

---

## 14. Docs plan (W3-DOCS)

D4 §9 applies: README ≤150 lines; AGENTS.md rewrite; CONTRIBUTING and SECURITY; docs/architecture.md, clients.md, e2e.md, troubleshooting.md, model-delivery.md; generated `docs/tools.md`; delete the stale docs and `integrations/codex|claude`. Additions:
- **Content additions:**
  - `docs/clients.md` covers approval modes, output limits (§12.3), the loader and homes, and the cowork auto-allow warning.
  - `SECURITY.md` covers the consent dialog, the audit log, "agents must not edit settings.json", and the dialog press policy.
  - `docs/design/` keeps SPEC.md with a header "historical design; code is the source of truth".
- **Generated docs.** `scripts/gen-tool-docs.mjs` reads `artifacts/catalog/catalog.json` rather than spawning the broker. It writes `docs/tools.md`, the README tool table and the SKILL tool map; `--check` fails on drift.

---

## 15. Token budget

| Metric | Old | New (measured by SPEC-catalog.mjs) |
|---|---|---|
| Tools | 42 | 37 default (+ run_csharp opt-in) |
| Registry keys | 30 change ops + 32 bridge ops | 243 (232 add-in-bound) |
| tools/list | 373,067 B | **77,661 B** (78,649 B with run_csharp); about 21k tokens |
| Largest tool | 59,925 B | 5,992 B (create_elements) |
| outputSchema | 174,333 B | 0 |
| Core profile (17 tools: status, set_target, list, find_elements, describe_elements, get_view, capture, ui, create_elements, place_family, modify_elements, set_parameters, change_set, undo, job_status, help, read_many) | n/a | about 32 KB |
| Instructions | 1,152 chars (critical rules after 512) | head 504, total 1,129 |

The core profile is a **lossy opt-in**. All reads are reachable through `read_many`, and all non-lifecycle writes through `change_set`. Lifecycle ops (documents, worksharing, links reload, export, model delivery), `cancel_job` and `run_csharp` are not reachable in core. This is documented in `help {topic:"profiles"}`.

**Surface gates** (`contracts/src/catalog/checks.ts`, run by `npm run gen:catalog -- --check` and e2e S00). `e2e/budgets.json` is generated from these values:
- total ≤ 90,000 B;
- per tool ≤ 6,000 B;
- growth over the budgets.json baseline > 5 % needs a baseline update in the same commit;
- description ≤ 220 chars;
- depth ≤ 5;
- every param described;
- no forbidden keywords;
- names `^[a-z][a-z0-9_]{1,40}$`, not banned, `mcp__revit__<name>` ≤ 64;
- instructions head ≤ 512 and total ≤ 1,500;
- 37 default tools.

**Output budgets:** status compact ≤ 2 KB; list/find page ≤ 8 KB; result text ≤ 12 KB (40 KB full); image base64 ≤ 1.5 MB; read_many ≤ 2 images / 2 MB. T1 fails on p95 growth > 20 %.

---

## 16. Capability preservation

D1 §9 maps all 42 old tools, 30 change-set ops, 2 prompts and 2 resources. The critic's loss list is resolved here:

| Lost or weakened item | Resolution |
|---|---|
| get_warnings filter.severities | `check_model {check:"warnings", severity}` |
| get_views isGraphical / canBePrinted | `list {kind:"views", graphical, printable}` |
| get_schedule_fields nameContains / includeExistingFields | `read_schedule {available:true, name}`; existing fields are always listed in `fields` |
| catalog filter.classes | `list {kind:"types", class}` |
| delete_element expectedDeletedElementIds (≤20) | `modify_elements {op:"delete", expect_ids}` |
| create_project_from_template activation | `manage_document new_project activate` (default true) + re-pin |
| read-side expectedGeneration | Dropped on purpose; `get_changes since=m#` answers "did anything change". **Owner acceptance requested** (openDecisions) |
| read_bundle includeSectionMetrics, maxElementsScanned | Deadlines + `partial` + page tokens; section metrics via `status include` + `read_many` |
| get_dimensions for a whole view | `find_elements {category:["Dimensions"], view}` then `describe_elements {from:"r#"}` (two calls, nothing lost) |
| core profile | Lossy opt-in (§15); the default full profile loses nothing |
| pyRevit/Dynamo in-process bridge | Kept; covered by S99; `inproc` flag enforced |

---

## 17. Verify-first spikes (run early by the owning package)

| id | Question | Owner | Fallback |
|---|---|---|---|
| V1 | ExportImage with only a TransactionGroup open, and rollback leaves no residue | P-VIEWS-VISUAL | D3 fallback B |
| V2 | FitToPage/crop pixel mapping; sheet Outline mapping | P-VIEWS-VISUAL | omit mapping |
| V3 | ExportImage of views of non-active docs / never-opened views | P-VIEWS-VISUAL | activate temporarily and restore |
| V4 | OpenAndActivateDocument re-activates an open local/cloud doc | P-VIEWS-VISUAL | ShowElements trick |
| V5 | Section depth sign; elevation index → direction | P-VIEWS-VISUAL | fix constants |
| V6 | famDoc.LoadFamily(P) with a TransactionGroup open in P, and group rollback reverts it | P-FAMILY | family segments last (§6.7) |
| V6b | EditFamily copies reflect project-side type values | P-FAMILY | document overwrite semantics |
| V7 | FootPrintRoof SlopeAngle units | P-MODEL-ARCH | fix conversion |
| V8 / V8b | Stairs get railings automatically; StairsEditScope inside a TransactionGroup | P-MODEL-ARCH | Railing.Create; stairs cs:false |
| V9 | Curtain grid U/V orientation | P-MODEL-ARCH | fix mapping |
| V10 | Close a non-active family doc with open views | P-DOCS | "close in UI" |
| V11 | SWC/ReloadLatest clears the undo stack | P-DOCS (reports to P-CONTROL) | clear UndoLedger |
| V12 | UIFrameworkServices undo internals | P-CONTROL | keep experimental off |
| V13 | Roslyn load on 2024/2027 with pyRevit + Dynamo loaded | P-CONTROL (first task) | out-of-process csc |
| V14 | ExportImage while Revit is minimized/unfocused | P-VIEWS-VISUAL | CAPTURE_GPU_UNAVAILABLE hint |
| V15 | Workshared previews borrow NotOwned elements | P-DOCS | static validation only |
| V16 | Loader manifest keeps trust (no unsigned prompt) with the same ClientId | W1-ADDIN | document one-time "Always Load" |
| V17 | UIA enumerate/press on TaskDialog DirectUI and Win32 dialogs, EN/DE, 2024/2027 | P-REL-ADDIN | TDM_CLICK_BUTTON / WM_COMMAND |
| V18 | WM_NULL + Idling reduce background wake latency (D2 E1 A/B) | P-REL-ADDIN | keep watchdog |
| V19 | BasicFileInfo central detection property names on 2024/2027 | P-DOCS | ModelPath compare with CentralPath |
| V20 | DialogBoxShowing fires for our own TaskDialog and exposes its Id | P-REL-ADDIN | match by title |
| V21 | UIApplication.SelectionChanged on 2024 | W1-ADDIN | sample in ExecuteBatch |
| V22 | Codex 0.153/0.159: exec event shape, image passthrough, block `_meta` imageDetail, per-tool approval TOML, output token limit, readOnly concurrency | W3-E2E / W3-INSTALL | per D4 §11 |
| V23 | SDK 2.2.0: fromJsonSchema permissive pass-through, no `$schema`, progress API, onerror | W1-BROKER | low-level Server for tools/list |
| V24 | MSIX escape for installer -Relaunch | W3-INSTALL | refuse only |
| V25 | PostCommand(Undo) from ExternalEvent + DocumentChanged(TransactionUndone) confirmation | P-CONTROL | compensate |
| V26 | Activate another doc then Close the target in the next Execute pass | P-DOCS | CANNOT_CLOSE_ACTIVE |

---

## 18. Code ownership map (who may edit what, per wave)

**Wave 1**
- **W1-ADDIN:** `addin/**`, `scripts/build-addin.ps1`, `scripts/resolve-dotnet-sdk.ps1`, `scripts/dev-install.ps1`, `tests/**` (deletes it), `scripts/test-named-pipe-host-lifecycle.ps1` (deletes it).
- **W1-BROKER:** `broker/**`, `contracts/**`, root `package.json`/`package-lock.json`, `e2e/**`, `scripts/validate-repo.mjs` (deletes it), `.github/workflows/**`, `docs/design/**`, `.gitignore`.

**Wave 2**
- Each lane owns exactly the files in its `ownsFiles` list; the lists are disjoint.
- **Frozen in wave 2** (no lane edits; changes go through the lead):
  - add-in: `addin/RevitMcpNext.Addin/Core/**` except `Core/ChangeEngine.FamilySegments.cs` (P-FAMILY), `Common/**` except `Common/Aliases/**` (P-READ), `Compat/**`, `Serialization/**`, `Runtime/McpHome.cs`, `Runtime/Settings.cs`, `Runtime/AuthTokenStore.cs`, `Legacy/**`, `RevitMcpNext.Addin.csproj`, `addin/RevitMcpNext.Contracts/**`, `addin/RevitMcpNext.Loader/**`;
  - broker: `broker/src/framework/**`, `broker/src/server.ts`, `broker/src/index.ts`, `broker/src/runtime/{home,auth,settings,deadline,ids}.ts`;
  - contracts: `contracts/src/{protocol,errors,naming,home,index}.ts`, `contracts/src/catalog/{types,index,topics,emit,checks}.ts`;
  - e2e harness: `e2e/run.mjs`, `e2e/lib/**`;
  - build and packages: `scripts/build-addin.ps1`, `scripts/dev-install.ps1`, root `package.json`.
- If a lane needs a frozen-file change, it stops and reports the exact diff it needs.

**Wave 3**
- **W3-INSTALL:** `installer/**`, `broker/src/cli/**`, `broker/build/**`, `plugins/**`, `.agents/**`, `scripts/**` except `scripts/gen-tool-docs.mjs`, root `package.json`, `.github/workflows/**`.
- **W3-E2E:** `e2e/**`, plus fix rights on `addin/**`, `broker/src/**` (except `broker/src/cli/**` and `broker/build/**`) and `contracts/**`; it also does the dead-code sweep of `Legacy/`.
- **W3-DOCS:** `README.md`, `AGENTS.md`, `CONTRIBUTING.md`, `SECURITY.md`, `docs/**`, `integrations/**`, `scripts/gen-tool-docs.mjs`, `contracts/src/catalog/topics.ts`.

---

## 19. Critic findings: resolutions

Each row states the resolution and where it is specified.

| # | Finding (short) | Resolution | Section |
|---|---|---|---|
| C1 | run_csharp gate holes (read mode, key, agent-editable gate, token binding) | Read mode runs in an always-rolled-back TransactionGroup and rejects Transaction/SubTransaction/TransactionGroup/*EditScope construction. The lifecycle deny list applies in read and dry_run, and in commit unless allowUnsafeApis. Single key `enableCodeExecution`, enforced by the add-in on every call. An add-in-owned consent TaskDialog per Revit session that ui press and dialogPolicy can never answer. Instructions/SKILL/AGENTS forbid editing settings.json. status shows enablement time. Token bound to code sha + args sha + docKey, stored in the broker. S95 refusal cases | §11, §5.7, §12.5, §13.3 |
| C2 | Opening a central edits the central | BasicFileInfo detection → CreateNewLocal to Documents\<name>_<user>.rvt or `local`; `central:true` + confirm; the local shares the central key; S78 asserts local + central mtime | §5.9, §4.6.5, §13.3 |
| C3 | edit_family overwrite default and load_into scope | overwrite default false (true only when ops changed type values or asked), described as project type values, result lists types_changed; load_into = projectPin only, `into` for others, >1 doc confirm; S70 assertion | §5.9, Appendix A |
| C4 | ui press unsafe and would not work | Allowlist Cancel/Close only; others NOT APPLIED + confirm; No/Do not save/Yes on save/sync/delete dialogs never; UIA InvokePattern with TDM/WM_COMMAND/BM_CLICK fallbacks; our dialogs and denylist refused; `instance` param; V17 on EN/DE 2024/2027 | §8.5, Appendix A |
| C5 | Writes land in the wrong doc | set_target not read-only + barrier + auto approval in the TOML; explicit activation/open/new_*/edit_family open re-pin with TARGET_NOW; TARGET_CHANGED gate for auto pins after a user doc switch; write `doc` exact-only; R1 updated | §7.3-§7.4, §12.2, §13.3 |
| C6 | Instructions make NOT APPLIED an automatic repeat | New head (504 chars): NOT APPLIED = the user's OK; `next: ask the user, then ...`; SKILL/AGENTS updated; optional elicitation via inputRequired() | §5.5, §5.7, §12.5 |
| C7 | read_schedule ids map to the wrong elements | Ids only via a unique key field mapping 1:1; else elementIds + notice; S65 re-sort check | §5.9 |
| C8 | read_many exceeds client timeouts; images too big | One Deadline, remaining/(n-i), BUDGET markers, ≤2 images/2 MB, budget clamp ≤55 s, E15 case | §5.9, §3.1, §13.3 |
| C9 | Busy classification reports the wrong cause | New order (executing → native → dialog-not-progress → hung → edit mode with Idling silence), lastIdlingAtUtc in the snapshot, "probably" wording, export/family-reload variants | §8.8, §4.6.5 |
| C10 | D2 vs D4 on queued calls behind long work | Fail-fast: cancel the queued item and return REVIT_BUSY in ~8 s with executing op, elapsed, ref; D4 R5 is acceptance | §8.8 |
| C11 | Short ids collide across instances | Broker allocates session-unique ids mapped to (instanceId, requestId/jobId); add-in ids internal; ledger lookup by ULID; JOB_UNKNOWN fix → status writes | §6.6, §8.10 |
| C12 | expectedGeneration friction | Removed from direct writes; only per-element stamps (CONFIRM_STALE) | §6.4, §7.5 |
| C13 | change_set cannot be one Transaction | WriteScope group with per-op Transactions, own/none segments, `tx` and `cs` registry fields, lifecycle ops rejected with a fix, family atomicity only after V6 else reload last, static preview for lifecycle | §6.7, §4.2, Appendix B |
| C14 | Undo internals risky, contract gaps | PostCommand primary with journal-verified top and one-step posts; internal API behind experimentalUndoStack after V12; `mode:auto\|compensate`; NEEDS_ACTIVE_DOC, never an implicit switch; `MCP ui` entries transparent | §6.9, Appendix A |
| C15 | Script loading breaks binding | D1 signature, BCL/Revit refs only, generated in-assembly guard, RevitMcpNext.*-scoped resolve, 2027 ALC via GetLoadContext, V13 first | §11 |
| C16 | Capture ownership/spec conflicts | The add-in owns all raster work via WPF (UseWPF kept); one table (768/1280/1568 + MP caps + 1.5 MB), unique folder, 72 h/1 GB/500 retention, normative commit-export-rollback sequence; e2e asserts cap + aspect ±1 % | §10.1, §13.3 |
| C17 | status needs data not in the snapshot | Snapshot adds levels (≤30), selection, lastIdlingAtUtc, executing; status reads only snapshot/health; D1 §7.4 default format; internals only in detail:full | §4.6.5, §5.9 |
| C18 | SDK validation defeats lenient input | fromJsonSchema(catalogSchema, permissive) → verbatim tools/list; lenient zod-free normalization/validation in handlers; handlers never throw; S00 lenient checks | §5.1, §5.3, §13.3 |
| C19 | German Revit, English names | Generated English alias tables from ENU 2024/2027 via LabelUtils; English-first resolution; stable English category output; DEU run (S21) in full | §9.7, §5.4, §13.3 |
| C20 | No agreed registry source of truth | Catalog in contracts holds all per-op metadata (Appendix B), emitted as JSON and embedded in the add-in; attributes only bind; both-way startup check; ADDIN_OUTDATED/UNSUPPORTED_VERSION mapping; coverage gate replaces validate-repo | §4.1-§4.2, §9.3 |
| C21 | Parallel implementers collide | Ownership map; D3 layout canonical; fixed paths for D2 components (DocumentRegistry, RequestContext, ExternalEventPump, DialogPolicy); broker seam (framework frozen, ToolContext); contracts single owner | §18, §9.1, §20 |
| C22 | Divergent error codes and bogus fix targets | One ErrorCatalog; the add-in returns codes and details only; the broker renders fix; W1 asserts fix targets exist. Mappings: TARGET_NOT_ACTIVE→NEEDS_ACTIVE_DOC; OP_UNAVAILABLE_IN_REVIT_<y>→UNSUPPORTED_VERSION; LEVEL_NOT_FOUND→NOT_FOUND; RELOAD_LATEST_REQUIRED and ELEMENT_OWNED_BY_OTHER→NOT_EDITABLE; VIEW_TEMPLATE_CONTROLS→TEMPLATE_CONTROLLED; LEGEND_TEMPLATE_REQUIRED→LEGEND_NEEDS_SEED; NWC_EXPORTER_UNAVAILABLE→EXPORTER_MISSING; INVALID_ARGUMENT→INVALID_ARGS; REVIT_EDIT_MODE→REVIT_EDIT_MODE_OR_COMMAND; DIALOG_DISMISSED→REVIT_DIALOG_ANSWERED; DOC_KIND→FAMILY_DOC_REQUIRED/PROJECT_DOC_REQUIRED; REVIT_DIALOG→REVIT_DIALOG_OPEN; NUMBER_TAKEN→NAME_TAKEN; ZOOM_NO_VIEW→NOT_FOUND(kind view) | §4.3 |
| C23 | Transaction names disagree | One naming table and parser in contracts and C#; every filter uses it; only TempScope/WriteScope create names | §4.4 |
| C24 | Home layout and installer conflicts | D4 owns the installer; one home spec (marker + receipt, r<year> files, log names, single settings schema); bundle entry; legacy token carry-over dropped | §3, §12.1 |
| C25 | Three e2e harnesses | One harness `e2e/`; D2/D3 scenarios become files; test ops in the release DLL gated by a non-default home + enable file; isolated homes raise script timeouts | §13 |
| C26 | validate-repo/tests churn | W1 deletes tests/** and validate-repo.mjs before the split; gates moved to catalog checks + e2e surface | §13.5, §15, §18 |
| C27 | Surface gate constants wrong | Gates generated from SPEC-catalog stats: depth ≤5, 220 chars, 37 tools, every param described (enum descriptions added), help rendering fixed | §15, Appendix A |
| C28 | Param names change meaning | Wall openings use model points (projected) + offset/height; `profile` for roof_extrusion; toposolid follows the global z rule; curtain_grid `direction: vertical\|horizontal` | §5.4, Appendix A |
| C29 | Wall split duplicates inserts | BreakCurve for MEP; walls/framing delete inserts outside each half, refuse inserts spanning the split; S40 insert count | §5.9 |
| C30 | Closing the active doc via placeholder | D1 behaviour: two-step job (activate other, close next pass), else CANNOT_CLOSE_ACTIVE, never a placeholder | §5.9 |
| C31 | new_project stays hidden | `activate` default true + re-pin | §5.9, §7.4 |
| C32 | Lost options; core not lossless | Added severity, graphical, printable, read_schedule name, list class, expect_ids; core documented as lossy, read_many added to core | §16, §15 |
| C33 | Codex output truncation | Page token repeated in the summary; measure limits (V22); output_token_limit in the TOML if needed; Claude large-capture check | §5.5, §12.2-§12.4 |
| C34 | Per-element tracking has no owner | DocumentRegistry (W1-ADDIN; P-REL-ADDIN in wave 2): Dictionary<ElementId,long> stamps pruned on close, cap 500k with a stamp floor on overflow; 20,000-entry ring with overflow mark → get_changes `incomplete:true`, confirm treats unknown as stale | §6.4, §9.1 |
| C35 | Barrier undefined for jobs; lost late capture | Barrier only while the write tool call is outstanding; jobs/native → REVIT_BUSY fail fast; READ_STILL_RUNNING j# with result cache | §8.8, §6.8, §10.1 |
| C36 | Approval modes / AGENTS.md | TOML approval auto only for status, help, set_target, ui; AGENTS edit reported; -NoAgentsSnippet kept | §12.2, §12.5 |
| C37 | In-process bridge untested | S99 via test.inproc_call; `inproc` registry flag enforced (false for destructive, lifecycle, code) | §13.3, §13.4, Appendix B |

---

## 20. Process rules for all packages

- **Worktrees.** Each package works in its own git worktree: `git -C "C:\Users\Bhavesh\Documents\ChatGPT\Revit MCp\revit-mcp-next" worktree add "C:\Users\Bhavesh\Documents\ChatGPT\Revit MCp\wt\<package-id>" -b lane/<package-id> origin/main`. It runs `npm ci` there. It never works in the primary checkout.
- **Delivery.** Straight to main: `git fetch origin && git rebase origin/main`, build and verify again, then `git push origin HEAD:main`. Small commits. Never force-push. If a rebase conflicts in a file you do not own, stop and report.
- **Build commands:**
  - broker: `npm run build` and `npm run typecheck` at the repo root;
  - add-in: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-addin.ps1 -RevitYear 2024 -DotnetPath "C:\Users\Bhavesh\AppData\Local\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\Microsoft\dotnet\dotnet.exe"`, and the same with `-RevitYear 2027`. The SDK is 10.0.401; `resolve-dotnet-sdk.ps1` also accepts that path when passed.
- **Dev install and e2e:**
  - `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev-install.ps1 -Home "$env:USERPROFILE\.revit-mcp-next-dev-<package-id>" -RevitYears 2024,2027`;
  - `node e2e/run.mjs --home "%USERPROFILE%\.revit-mcp-next-dev-<package-id>" --years 2024 --launch --only <IDs>`.
- **Tests.** No unit tests and no test doubles. Only the e2e scenarios.
- **Other rules:**
  - Never target non-disposable documents.
  - Never use computer use or screen automation on Revit.
  - Never edit files outside your ownership list.
  - Report every open question and spike result in your final message.
- **Commit messages** end with the attribution trailer your harness requires.

---

## 21. Work packages (summary; full briefs are delivered to the lead as structured output)

| id | wave | depends on | scope |
|---|---|---|---|
| W1-ADDIN | 1 | - | Delete tests/**; mechanical split; drop 2021; loader; protocol v3 (C#); Core/Common/Runtime/Ipc foundation (registry, engine, scopes, resolver, query, DocumentRegistry, JobRunner, queue deferral, control ops); dev-install; adapter manage_document.new_project |
| W1-BROKER | 1 | - | SDK 2.2.0; delete old broker + validate-repo; contracts (protocol, errors, naming, home, catalog with 38 tool files + registry + emit/checks); broker framework, runtime, transport, targeting core, jobs basic; tools status/set_target/help/job_status/cancel_job/change_set + generic modules for all others; prompts/resources; e2e skeleton + S00/S01; docs/design copy |
| P-REL-ADDIN | 2 | W1-* | Pump (WM_NULL, Idling), admission, dialog policy + UIA dialogs/press, UI monitor, ledger persistence, in-process bridge, test ops, crash guards/logging; R5 R6 R7 R10 R12 S99 |
| P-REL-BROKER | 2 | W1-* | Registry resilience, limiter, coalescer, health poller, busy classification + fail-fast, barrier, reconciliation, in-flight journal, READ_STILL_RUNNING cache, targeting rebind/re-pin/gates hardening, status full; R1 R3 R4 R8 R9 R11 R13 |
| P-VIEWS-VISUAL | 2 | W1-* | capture, ui (non-control ops), get_view, edit_views, view_graphics; S50 S90 S92 |
| P-READ | 2 | W1-* | list, find_elements, describe_elements, check_model, get_quantities, get_changes, read_many; English alias generation; S20 S21 S89 |
| P-MODEL-ARCH | 2 | W1-* | create_elements, place_family; S30 |
| P-MODEL-MEPSTRUCT | 2 | W1-* | mep, structure; S31 |
| P-MODIFY | 2 | W1-* | modify_elements, set_parameters, edit_types; S32 S40 |
| P-SHEETS-ANNO | 2 | W1-* | edit_sheets, annotate, edit_schedules, read_schedule; S55 S60 S65 |
| P-FAMILY | 2 | W1-* | edit_family, read_family, ChangeEngine.FamilySegments; S70 |
| P-DOCS | 2 | W1-* | manage_document, worksharing, links; S75 S78 S80 S88 |
| P-EXPORT | 2 | W1-* | export, model_delivery, recipes; S85 S96 S97 |
| P-CONTROL | 2 | W1-* | undo, run_csharp (+ Scripting project), change_set tool; S35 S36 S95 |
| W3-INSTALL | 3 | all wave 2 | installer/uninstall, CLI (doctor, clients, raw, support), esbuild bundle, plugins/skills/AGENTS snippet, Codex/Claude registration, package, CI |
| W3-E2E | 3 | all wave 2, W3-INSTALL for installed-mode checks | harness completion (coverage gate, profiles, W1-W4, T1, codex/claude), full runs on 2024+2027, fixes, legacy sweep |
| W3-DOCS | 3 | all wave 2, W3-INSTALL | README, AGENTS.md, docs/*, generated tools.md, SECURITY, CONTRIBUTING, deletions |

## Appendix A. Final tool catalog (generated from SPEC-catalog.mjs; normative)

Legend: parameter types are JSON Schema types; `point/box` = number array; `points` = array of [x,y(,z)]; `loops` = array of point arrays. Signatures: params before `;` required, `/` alternatives, `+` together, `sel` = one of ids/from/filter. Byte sizes are advertised JSON sizes.

### Session, targeting, health

#### `status` (readOnly, idempotent; 920 B)

Start here. Lists running Revit versions and open docs (#1, #2...), the pinned target doc, active view, selection count, levels and busy/dialog state. include= adds sections; detail=full adds health.

| param | type | notes |
|---|---|---|
| include | [views\|selection\|view_elements\|readiness\|context\|warnings\|writes] | Extra sections for the target doc (writes = recent writes by any session). |
| detail | compact\|full | full adds versions, paths, queue and pipe health. |
| instance | string | Only this Revit: year (2024) or process id. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `set_target` (write (non-destructive), idempotent; 631 B)

Pin the doc that every call uses this session: 'active', a # from status, a title or a path. 'follow' tracks the active tab; 'none' unpins. Family docs work too; links are read with find_elements link=.

| param | type | notes |
|---|---|---|
| doc **(required)** | string | 'active', 'follow', 'none', #, title or path. |
| instance | string | Revit year or process id when the same file is open twice. |

### Reads

#### `list` (readOnly, idempotent; 2633 B)

List items of one kind (levels, views, sheets, schedules, rooms, types, families, materials, worksets, phases, links, revisions, filters, templates...) as compact rows. name= filters; for_element= valid types.

| param | type | notes |
|---|---|---|
| kind **(required)** | enum(34) | What to list. |
| name | string | Name contains (case-insensitive, * wildcard). |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| level | string | Level name or id (rooms, areas, spaces, views). |
| view_type | string | For views/view_types: FloorPlan, CeilingPlan, Section, Elevation, ThreeD, Drafting, Legend, AreaPlan, Sheet... |
| family | string | For types: family name. |
| for_element | string | For types: element id; returns only types it can switch to. |
| placed | boolean | Views: on a sheet or not. Rooms/areas: placed or not. |
| graphical | boolean | Views: only graphical views (no schedules or browser-only views). |
| printable | boolean | Views: only views that can be printed or exported. |
| class | string | Types: Revit API class, e.g. WallType, FloorType, FamilySymbol. |
| where | string[] | Conditions 'Param op value' like find_elements. |
| ids | string[] | Only these ids. |
| fields | string[] | Extra columns: location, bbox, host, room, workset, phase, or any parameter name. |
| detail | compact\|full | Default compact. |
| limit | integer | Rows per page, 1-500. Default 50. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `find_elements` (readOnly, idempotent; 2683 B)

Find elements by category, level, type, view, selection, parameter values, box or link. Returns the exact total, compact rows and a handle r# for from= in later calls. count_only/group_by for fast counts.

| param | type | notes |
|---|---|---|
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| class | string | Revit API class, e.g. Wall, FamilyInstance, Floor. |
| level | string[] | Level names or ids (a string is fine). |
| type | string | Type name, 'Family: Type' or id; * wildcard. |
| family | string | Family name; * wildcard. |
| view | string | Only elements visible in this view ('active' ok). |
| from | string | Restrict to 'selection' or an r# handle. |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| where | string[] | Conditions 'Param op value', op: = != > >= < <= contains startswith empty notempty. Lengths mm. |
| box | point/box | Intersects box [x0,y0,x1,y1] (plan) or [x0,y0,z0,x1,y1,z1] mm. |
| link | string | Search inside this linked model (name or id); read-only, host coordinates. |
| workset | string[] | Workset names. |
| phase | string | Phase created name. |
| design_option | string[] | Design option names, or 'main'. |
| hidden | boolean | With view=: include elements hidden in the view. |
| types | boolean | Find element types instead of instances. |
| fields | string[] | Extra columns: location, bbox, host, room, workset, phase, or any parameter name. |
| group_by | string | Count per category, type, family, level, workset or a parameter name. |
| count_only | boolean | Only the total (and group counts). |
| detail | compact\|full | Default compact. |
| limit | integer | Rows per page, 1-500. Default 50. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `describe_elements` (readOnly, idempotent; 1305 B)

Details for up to 50 elements: parameters with values, units and writability, type parameters, geometry, host/hosted, room, joins, group, MEP connectors; dimensions show witness refs. Use before set_parameters.

| param | type | notes |
|---|---|---|
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| params | writable\|all\|names\|none | Instance parameters: writable (default) values, all values, names only, none. |
| include | [type\|geometry\|relations\|connectors] | Extra blocks. type = type parameters. |
| match | string | Only parameters whose name contains this. |
| link | string | Elements are inside this linked model. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `get_view` (readOnly, idempotent; 720 B)

One view or sheet: type, scale, detail level, template, crop, view range, phase, discipline; include= overrides, filters, hidden categories, viewports, revisions. Default: the active view.

| param | type | notes |
|---|---|---|
| view | string | View name, id, sheet number, or 'active' (default). |
| include | [overrides\|filters\|categories\|viewports\|revisions] | Extra blocks. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `read_schedule` (readOnly, idempotent; 1166 B)

Read a schedule as a table (paged) with its fields, filters and sorting. category= without schedule= lists the fields available for a new schedule of that category.

| param | type | notes |
|---|---|---|
| schedule | string | Schedule name or id. |
| category | string | Category for available fields before edit_schedules create. |
| rows | boolean | Include body rows. Default true. |
| available | boolean | Include fields that could be added. Default false. |
| name | string | available: field name contains. |
| key | string | Field unique per row that maps rows to element ids. Default Mark; no id column if not unique. |
| limit | integer | Rows per page, 1-1000. Default 100. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `read_family` (readOnly, idempotent; 896 B)

Family parameters (name, type/instance, group, data type, formula, shared), family types with per-type values, category and nested families. family= loaded family, path= an .rfa file, or doc= an open family doc.

| param | type | notes |
|---|---|---|
| family | string | Loaded family name or id (read without opening the UI). |
| path | string | Path to an .rfa file (opened hidden, read-only). |
| types | string[] | Only these family types' values. Default all (up to 30). |
| detail | compact\|full | Default compact. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `check_model` (readOnly, idempotent; 1758 B)

Model health: stats (counts by category/level), warnings (grouped, element ids), readiness for common tasks, purgeable (unused items), clashes (set a vs b, links ok). Default check=stats.

| param | type | notes |
|---|---|---|
| check | stats\|warnings\|readiness\|purgeable\|clashes | What to check. Default stats. |
| a | string[] | Clashes: categories or an r# handle. |
| b | string[] | Clashes: categories or an r# handle. Default = a. |
| link | string | Clashes: set b is inside this linked model. |
| tolerance | number | Clashes: ignore overlaps smaller than this, mm. Default 0. |
| match | string | Warnings: text contains. |
| severity | warning\|error | Warnings: only this severity. |
| ids | string[] | Warnings involving these elements. |
| group_by | string | Stats: category, level, type, workset. |
| scenarios | string[] | Readiness: walls, floors, rooms, families, sheets, tags... Default common set. |
| detail | compact\|full | Default compact. |
| limit | integer | Rows per page, 1-500. Default 50. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `get_quantities` (readOnly, idempotent; 1283 B)

Quantity takeoff: count, length (m), area (m2), volume (m3) summed by material, type, level, category or family, for all or filtered elements. Default by=material.

| param | type | notes |
|---|---|---|
| by | material\|type\|level\|category\|family | Group rows by. Default material. |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| level | string | Level name or id. |
| view | string | Only elements visible in this view. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| material | string | Material name contains. |
| paint | boolean | Include painted areas. Default false. |
| limit | integer | Rows per page, 1-500. Default 50. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `get_changes` (readOnly, idempotent; 655 B)

What changed since a point: added, modified and deleted elements (id, category) and transaction names, by you or the user. since='session' (default), 'last' (your last write) or a mark m#.

| param | type | notes |
|---|---|---|
| since | string | 'session', 'last', or a mark like m12 from an earlier result. |
| limit | integer | Rows per page, 1-500. Default 50. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `read_many` (readOnly, idempotent; 823 B)

Run up to 8 read calls in one round trip, sharing one time budget (max 2 images): calls=[{tool:'list',args:{kind:'levels'}},{tool:'capture',args:{}}]. One failure does not stop the rest.

| param | type | notes |
|---|---|---|
| calls **(required)** | object[] | Up to 8 {tool, args}; args as for that tool. |

### Visual

#### `capture` (readOnly; 1845 B)

See the model: returns an image of a view or sheet, or of elements (ids/from) in a temporary 3D box. region= zooms; size small|medium|large; compare= c# shows what changed. Nothing is kept in the model.

| param | type | notes |
|---|---|---|
| view | string | View name, id, sheet number, or 'active' (default). |
| ids | string[] | Focus these elements: temp 3D view boxed around them. |
| from | string | Focus an element set: r# handle, 'selection' or 'last'. |
| region | point/box | Zoom to [x0,y0,x1,y1] mm (model coords; sheet mm on sheets). |
| orient | enum(9) | 3D direction. Default iso_se. |
| style | wireframe\|hidden\|shaded\|consistent\|realistic | Default: view's own; temp 3D shaded. |
| size | small\|medium\|large | Long edge at most 768, 1280 or 1568 px. Default medium. |
| margin | number | Space around focused elements, mm. Default 1000. |
| highlight | string[] | Tint these element ids red in the image. |
| annotations | boolean | Show annotations. Default true for views, false for focus. |
| compare | string | Earlier capture id (c#): marks changed pixels. |
| format | auto\|png\|jpg | Default auto (png drawings, jpg shaded). |
| doc | string | Doc title, path or # from status. Default: pinned target. |

### UI control

#### `ui` (write (non-destructive), idempotent; 2477 B)

Drive the Revit window without changing the model: select, zoom_to, temporary isolate/hide, reset, activate_view, activate_doc, list/close open views, list dialogs and press a dialog button.

| param | type | notes |
|---|---|---|
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| view | string | View name, id, sheet number, or 'active'. |
| views | string[] | close_views: names/ids, or ['all_but_active']. |
| region | point/box | [x0,y0,x1,y1] mm. |
| fit | boolean | zoom_to: zoom to fit the whole view. |
| mode | replace\|add\|remove | select: default replace. |
| zoom | boolean | select: also zoom to the selection. |
| dialog | string | Dialog id from op=dialogs. Default: the open one. |
| button | string | Button text or id. Cancel/Close apply at once; other buttons need the user's OK (confirm). |
| instance | string | dialogs/press: Revit year or process id. Default: every Revit. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
select(sel;mode,zoom)
zoom_to(ids/from/region/fit;view)
isolate(ids/from/category;view)
hide(ids/from/category;view)
reset(;view)
activate_view(view)
activate_doc(doc)
open_views()
close_views(views)
dialogs(;instance)
press(button;dialog,instance,confirm)
```

### Domain write tools

#### `create_elements` (write (non-destructive); 5992 B)

Create levels, grids, walls, floors, roofs, ceilings, rooms, areas, separators, openings, shafts, ref planes, model lines, stairs, railings, curtain grids, toposolids. mm; applies now, returns ids and undo.

| param | type | notes |
|---|---|---|
| level | string | Base level name or id. |
| top | string | Top level (wall top constraint, shaft, stairs). |
| elevation | number | Level elevation mm. |
| name | string | Name. |
| number | string | Room/area number. |
| department | string | Room department. |
| plans | boolean | level: also create floor and ceiling plans. Default true. |
| count | integer | level/grid: how many to create. Default 1. |
| spacing | number | level/grid: distance between copies mm; curtain_grid: grid spacing. |
| start | point/box | Start point [x,y(,z)] mm. |
| end | point/box | End point [x,y(,z)] mm. |
| through | point/box | Point on the arc: makes an arc (single segment). |
| points | points | [[x,y],...] mm: wall path, outline, stair path; toposolid [x,y,z] (z above level if set, else absolute). |
| profile | points | roof_extrusion: profile [[s,z],...] mm; s along start->end, z above level. |
| holes | loops | Inner loops [[[x,y],...],...]. |
| closed | boolean | Close the loop back to the first point. |
| from | string | floor/ceiling: follow room boundaries: r# handle or 'selection' of rooms. |
| type | string | Type: 'Family: Type', type name, or id. |
| height | number | wall: unconnected height mm when no top (default 3000); opening: opening height mm. |
| offset | number | Height above level mm (wall opening: sill height). Default 0. |
| top_offset | number | Offset from top level mm. |
| line | center\|core_center\|finish_ext\|finish_int\|core_ext\|core_int | wall location line. Default center. |
| structural | boolean | Structural wall/floor. |
| flip | boolean | wall: flip orientation. |
| slope | number | Degrees (floor, roof). |
| slope_edges | integer[] | roof: edge indexes that slope. Default all when slope set. |
| overhang | number | roof: overhang mm. |
| depth | number | roof_extrusion: extrusion length mm (left of start->end). |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| all | boolean | room: place rooms in every enclosed area of the level. |
| allow_duplicate | boolean | room: allow a duplicate number. |
| view | string | Plan view (separators, areas, ref plane). |
| host | string | Host id: wall/floor/roof/ceiling (opening), stairs (railing), curtain wall (curtain_grid). |
| width | number | stairs: run width mm. |
| risers | integer | stairs: riser count. Default from height. |
| direction | vertical\|horizontal | curtain_grid: direction of the new grid lines. |
| positions | number[] | curtain_grid: offsets mm from the wall start (vertical) or base (horizontal). |
| mullion | string | curtain_grid: mullion type to add on new lines. |
| line_style | string | Line style name. |
| survey | points | toposolid: extra [[x,y,z]] points inside the outline. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
level(elevation;name,plans,count,spacing)
grid(start,end;through,name,count,spacing)
wall(level,points/start+end;type,height,top,offset,top_offset,line,structural,flip,closed,through)
floor(level,points/from;type,holes,offset,slope,structural)
roof(level,points;type,slope,slope_edges,overhang,offset)
roof_extrusion(level,start,end,profile,depth;type)
ceiling(level,points/from;type,holes,offset)
room(level,at/all;name,number,department,allow_duplicate)
room_separator(view,points;closed)
area(view,at;name,number)
area_boundary(view,points;closed)
opening(host,points/start+end;offset,height)
shaft(level,top,points;offset,top_offset)
ref_plane(start,end;name,view)
model_line(points;level,line_style,closed,through)
stairs(level,top,points;type,width,risers)
railing(points/host;level,type)
curtain_grid(host,direction,positions/spacing;mullion)
toposolid(points;type,level,survey)
```

#### `place_family` (write (non-destructive); 2793 B)

Place instances of any loadable family (doors, windows, furniture, fixtures, equipment, generic, detail-free) at points, on hosts or along lines; load .rfa families. Hosts are found automatically when omitted.

| param | type | notes |
|---|---|---|
| type | string | Family type: 'Family: Type', type name, or id. |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| points | points | Several insertion points [[x,y(,z)],...]: one instance each. |
| start | point/box | Start point [x,y(,z)] mm. |
| end | point/box | End point [x,y(,z)] mm. |
| level | string | Level for z; z of points is height above it. |
| host | string | Host id (wall, floor, ceiling, roof, face). 'auto' (default) finds the nearest valid host. |
| offset | number | Height above level, mm. Default 0. |
| rotation | number | Degrees, counterclockwise. Default 0. |
| flip_hand | boolean | Flip hand. |
| flip_facing | boolean | Flip facing. |
| allow_pinned | boolean | Allow a pinned host. |
| path | string | load: .rfa path visible to Revit. |
| symbols | string[] | load: only these types. Default all. |
| overwrite | boolean | load: overwrite existing parameter values. Default false. |
| categories | string[] | load: refuse unless the family is one of these categories. |
| sha256 | string | load: refuse unless the file hash matches. |
| allow_network | boolean | load: allow UNC/network paths. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
place(type,at/points/start+end;level,host,offset,rotation,flip_hand,flip_facing,allow_pinned)
load(path;symbols,overwrite,categories,sha256,allow_network)
```

#### `modify_elements` (destructive; 4615 B)

Edit existing elements chosen by ids, from= or filter=: move, copy, rotate, mirror, array, align, delete, pin, change_type, join/cut/attach, flip, split, reshape, group/ungroup. Deleting >20 asks to confirm.

| param | type | notes |
|---|---|---|
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| by | point/box | Offset vector [dx,dy(,dz)] mm. |
| to | point/box | Move/copy so the element's location point lands here [x,y(,z)] mm. |
| levels | string[] | copy: copy to these levels, aligned (vertical offset only). |
| count | integer | copy/array: number of copies (array includes original). |
| angle | number | Degrees counterclockwise (rotate, radial array). |
| center | point/box | Rotation/radial center [x,y(,z)]. Default: element center. |
| axis | point/box | Rotation axis direction. Default [0,0,1]. |
| start | point/box | mirror: axis start; reshape: new curve start. |
| end | point/box | mirror: axis end; reshape: new curve end. |
| points | points | reshape: new path for curve elements. |
| through | point/box | reshape: point on arc. |
| keep | boolean | mirror: keep original (mirror a copy). Default true. |
| kind | linear\|radial | array: default linear. |
| target | string | align: grid, level, ref plane or element id to align to. |
| lock | boolean | align: lock the alignment. |
| with | string | join/cut/attach: the other element id or r# handle. |
| side | top\|base\|hand\|facing\|wall | attach/detach: top\|base; flip: hand\|facing\|wall. |
| at | point/box | split: split point; place_group: insertion point. |
| type | string | Type: 'Family: Type', type name, or id. |
| pinned | boolean | pin: true pins (default), false unpins. |
| allow_pinned | boolean | Allow changing pinned elements. |
| expect_count | integer | delete: abort unless exactly this many elements (incl. dependents) would be deleted. |
| expect_ids | string[] | delete: abort unless the delete set (incl. dependents) is exactly these ids. |
| name | string | group/place_group: group name. |
| level | string | place_group: level. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
move(sel,by/to)
copy(sel,by/to/levels;count)
rotate(sel,angle;center,axis)
mirror(sel,start,end;keep)
array(sel,count,by/angle;center,kind)
align(ids,target;lock)
delete(sel;allow_pinned,expect_count,expect_ids)
pin(sel;pinned)
change_type(sel,type)
join(ids,with)
unjoin(ids,with)
switch_join(ids,with)
cut(ids,with)
uncut(ids,with)
attach(ids,with;side)
detach(ids;with,side)
flip(sel,side)
split(ids,at)
reshape(ids,start+end/points;through)
group(sel;name)
ungroup(ids/from)
place_group(name,at;level)
```

#### `set_parameters` (write (non-destructive); 1567 B)

Set parameter values on elements, their types, views, sheets or project info: values={Mark:'D1',Width:900} for every target or rows=[{id,values}] each. Lengths mm, areas m2, angles deg; '{Param}' copies values.

| param | type | notes |
|---|---|---|
| ids | string[] | Element, type, view or sheet ids; ['project_info'] for project info. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| values | object | {param name: value}. Text may use '{Other Param}' and '{n}' (1,2,3...). |
| rows | object[] | Per element: [{id, values:{...}}]. |
| on | instance\|type | type = set on the targets' types. Default instance. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

#### `edit_types` (destructive; 2184 B)

Types and materials: duplicate, rename or delete a type; set wall/floor/roof/ceiling layers; default type per category; create or edit materials (color, transparency, patterns); rename a family.

| param | type | notes |
|---|---|---|
| type | string | Type: 'Family: Type', type name, or id. |
| name | string | New name. |
| category | string | set_default: category. |
| family | string | Family name. |
| material | string | Material name (material_create: copy from this one). |
| layers | object[] | [{function:structure\|substrate\|thermal\|finish1\|finish2\|membrane, material, thickness}] exterior first. |
| color | string | Color: #RRGGBB, 'r,g,b' or a name like red. |
| transparency | integer | 0-100. |
| surface_pattern | string | Fill pattern name for surfaces. |
| cut_pattern | string | Fill pattern name for cut. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
duplicate_type(type,name)
rename_type(type,name)
delete_type(type)
set_layers(type,layers)
set_default(category,type)
material_create(name;color,transparency,surface_pattern,cut_pattern,material)
material_edit(material;name,color,transparency,surface_pattern,cut_pattern)
rename_family(family,name)
```

#### `edit_views` (destructive; 4848 B)

Create views (floor, ceiling, structural, area plan, section, elevation, 3D, callout, drafting, legend copy), duplicate, rename, delete, templates; set scale, detail, crop, view range, phase, 3D orientation, section box.

| param | type | notes |
|---|---|---|
| kind | enum(10) | create: view kind. |
| view | string | View name or id (callout/legend: the parent/source view). |
| views | string[] | View/sheet names or ids. |
| level | string | Level name or id. |
| name | string | Name. |
| view_type | string | View family type name. Default: first of the kind. |
| template | string | View template name; 'none' removes it. |
| scale | integer | Scale denominator: 100 = 1:100. |
| detail_level | coarse\|medium\|fine | Detail level. |
| style | wireframe\|hidden\|shaded\|consistent\|realistic | Visual style. |
| discipline | architectural\|structural\|mechanical\|electrical\|plumbing\|coordination | View discipline. |
| phase | string | Phase name. |
| phase_filter | string | Phase filter name. |
| crop | boolean | Crop view on/off. |
| crop_box | point/box | Crop [x0,y0,x1,y1] mm (plan) or [x0,y0,z0,x1,y1,z1]. |
| crop_visible | boolean | Show crop region. |
| view_range | object | Plans: {top,cut,bottom,depth} mm above the view level; or {cut:1200}. |
| far_clip | number | Sections/elevations: far clip depth mm. |
| scope_box | string | Scope box name; 'none' removes. |
| orient | enum(9) | 3D direction. |
| section_box | point/box | 3D: [x0,y0,z0,x1,y1,z1] mm; [] turns it off. |
| ids | string[] | create 3d/section: fit the box around these elements. |
| from | string | Like ids: r# handle or 'selection'. |
| start | point/box | section: cut line start [x,y]. |
| end | point/box | section: cut line end [x,y] (looks left of start->end). |
| depth | number | section: view depth mm. Default 3000. |
| height | number | section: height mm above level. Default level-to-level. |
| at | point/box | elevation: marker point [x,y]; duplicate: viewport center on sheet. |
| direction | north\|south\|east\|west | elevation: looking direction. |
| region | point/box | callout: [x0,y0,x1,y1] mm in the parent view. |
| area_scheme | string | area_plan: area scheme name. |
| perspective | boolean | 3d: perspective camera. Default false (isometric). |
| mode | copy\|detailing\|dependent | duplicate: default copy. |
| sheet | string | duplicate: also place the copy on this sheet. |
| underlay | string | Underlay level name; 'none'. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
create(kind;level,name,view_type,template,scale,start,end,depth,height,at,direction,orient,section_box,ids,from,region,view,area_scheme,perspective)
duplicate(view;mode,name,sheet,at)
rename(view,name)
set(view/views;scale,detail_level,style,discipline,phase,phase_filter,template,crop,crop_box,crop_visible,view_range,far_clip,scope_box,orient,section_box,underlay)
create_template(view,name)
delete(views)
```

#### `view_graphics` (write (non-destructive); 3144 B)

Change how a view shows things: override color, lines, pattern, transparency, halftone for elements, categories or filters; permanent hide/unhide; view filters with rules; color by parameter; reset.

| param | type | notes |
|---|---|---|
| view | string | View name/id or 'active'; a template name also works. |
| views | string[] | View/sheet names or ids. |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| view_filter | string | Revit view filter name. |
| categories | string[] | filter_create/edit: categories the filter applies to. |
| rules | string[] | Filter rules 'Param op value' (ANDed), op: = != > >= < <= contains startswith. |
| param | string | color_by: parameter whose values get distinct colors. |
| color | string | Color: #RRGGBB, 'r,g,b' or a name like red. |
| fill | boolean | Solid surface fill in color. Default true for elements, false for filters. |
| line_weight | integer | 1-16. |
| fill_pattern | string | Fill pattern name. Default Solid fill. |
| transparency | integer | 0-100. |
| halftone | boolean | Halftone. |
| visible | boolean | filter_apply: filtered elements visible. Default true. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
override(view,sel/category/view_filter;color,fill,line_weight,fill_pattern,transparency,halftone)
reset(view;ids/from/category/view_filter)
hide(view,ids/from/category)
unhide(view,ids/from/category)
color_by(view,category,param)
filter_create(view_filter,categories,rules;view,visible,color,fill,transparency,halftone)
filter_edit(view_filter;categories,rules)
filter_apply(view_filter,view/views;visible,color,fill,line_weight,fill_pattern,transparency,halftone)
filter_remove(view_filter,view/views)
```

#### `edit_sheets` (destructive; 3042 B)

Sheets: create (one or many), duplicate, renumber/rename, set titleblock; place, move or remove views, schedules and legends; viewport type; revisions create/edit and add/remove on sheets. Sheet mm from origin.

| param | type | notes |
|---|---|---|
| sheet | string | Sheet number, name or id. |
| sheets | string[] | Several sheets (numbers/ids). |
| number | string | Sheet number (duplicate/rename: the new number). |
| name | string | Name. |
| items | object[] | create many: [{number,name}]. |
| titleblock | string | Titleblock type. Default: first loaded. |
| mode | empty\|with_views\|with_detailing | duplicate: default with_views. |
| prefix | string | duplicate: prefix for copied view names. |
| view | string | View, schedule or legend name/id (placed or to place). |
| at | point/box | Viewport center [x,y] mm on the sheet. Default: sheet center. |
| by | point/box | move_viewport: shift [dx,dy] mm. |
| type | string | Viewport type name. |
| revision | string | Revision description, number or id. |
| description | string | Revision description. |
| date | string | Revision date text. |
| issued_by | string | Issued by. |
| issued_to | string | Issued to. |
| issued | boolean | Mark revision issued. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
create(number/items;name,titleblock)
duplicate(sheet,number;name,mode,prefix)
rename(sheet;number,name)
set_titleblock(sheet/sheets,titleblock)
place(sheet,view;at,type)
move_viewport(sheet,view,at/by)
remove_viewport(sheet,view)
set_viewport_type(sheet,view,type)
revision_create(description;date,issued_by,issued_to)
revision_edit(revision;description,date,issued_by,issued_to,issued)
revision_add(revision,sheet/sheets)
revision_remove(revision,sheet/sheets)
```

#### `annotate` (write (non-destructive); 4517 B)

Annotation in a view: tag elements or tag_all by category, text notes, dimensions (refs, grids, walls), spot elevations/coordinates, detail lines, filled regions, detail items/symbols, revision clouds, copy to views.

| param | type | notes |
|---|---|---|
| view | string | View name/id or 'active'. |
| views | string[] | copy_to_views: target views. |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| type | string | Tag/text/dimension/region/detail type. Default: category default. |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| start | point/box | Dimension line start / line-based item start. |
| end | point/box | Dimension line end / line-based item end. |
| through | point/box | Point on arc. |
| points | points | Line/loop points [[x,y],...] mm. |
| holes | loops | filled_region inner loops. |
| text | string | Text note content. |
| width | number | Text wrap width mm (paper). |
| rotation | number | Degrees, counterclockwise. Default 0. |
| leader | boolean | Add a leader. |
| orientation | horizontal\|vertical\|model | Tag orientation. |
| untagged_only | boolean | tag_all: skip elements already tagged. Default true. |
| offset | point/box | tag_all: tag head offset [dx,dy] mm from element center. |
| refs | string[] | dimension: stable reference strings from describe_elements. |
| mode | centers\|faces\|exterior\|core | dimension with ids: which references. Default centers. |
| overrides | object[] | Dimension text: [{segment,value,prefix,suffix,above,below}]. |
| by | point/box | Offset vector [dx,dy(,dz)] mm. |
| kind | elevation\|coordinate\|slope | spot kind. |
| host | string | spot: element id whose face is picked at 'at'. |
| revision | string | Revision description or number. |
| line_style | string | Line style name. |
| closed | boolean | Close the loop. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
tag(view,sel;type,at,leader,orientation)
tag_all(view,category;type,untagged_only,leader,offset)
text(view,at,text;type,width,rotation,leader)
dimension(view,refs/ids,start,end;type,mode,overrides)
dimension_edit(ids;type,by,overrides,refs,start,end)
spot(view,kind,host,at;type,leader)
detail_line(view,points;line_style,closed,through)
filled_region(view,points;type,holes,line_style)
detail_item(view,type,at/start+end;rotation)
revision_cloud(view,points,revision)
copy_to_views(view,views,ids/from)
```

#### `edit_schedules` (write (non-destructive); 2376 B)

Create schedules (regular, material takeoff, key) with fields, filters, sorting; add, remove, order, rename or hide fields; set filters, sort/group, itemize, totals; duplicate. Read rows with read_schedule.

| param | type | notes |
|---|---|---|
| schedule | string | Schedule name or id. |
| category | string | create: category, e.g. Doors. |
| name | string | Name. |
| kind | regular\|material_takeoff\|key | create: default regular. |
| fields | string[] | Field names, e.g. ['Mark','Type','Width']. |
| field | string | One field name. |
| heading | string | Column heading. |
| hidden | boolean | Hide the column. |
| filters | string[] | ['Level = Level 1', 'Width > 900'] (max 8, ANDed); [] clears. |
| sort | string[] | Sort/group fields in order; prefix '-' for descending. |
| headers | boolean | set_sort: group headers. Default false. |
| itemize | boolean | Itemize every instance. Default true. |
| totals | boolean | Grand totals (set_field: column total). |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
create(category;name,kind,fields,filters,sort,itemize,totals)
add_fields(schedule,fields)
remove_fields(schedule,fields)
set_field(schedule,field;heading,hidden,totals)
order_fields(schedule,fields)
set_filters(schedule,filters)
set_sort(schedule,sort;headers,totals)
set_options(schedule;name,itemize,totals)
duplicate(schedule,name)
```

#### `edit_family` (destructive; 3414 B)

Edit a family's parameters and types: add/remove/rename params, formulas, values per type, add/rename/delete types. family= a loaded family (edited, then reloaded) or doc= an open .rfa; save/save_as.

| param | type | notes |
|---|---|---|
| family | string | Loaded family: edited in the background and reloaded. open: keep it open as doc #n. |
| name | string | Parameter name (type ops: the type name). |
| new_name | string | New name. |
| kind | type\|instance | Default type. |
| data | string | Data type: text, length, area, volume, angle, number, integer, yesno, material, url, image, or family_type:<Category>. |
| group | string | Properties group, e.g. Dimensions, Identity Data, Constraints. Default Other. |
| shared | string | Shared parameter name or GUID (from the shared parameter file). |
| reporting | boolean | Reporting parameter. |
| formula | string | Formula, e.g. 'Width / 2'; '' clears. |
| value | string | Value (lengths mm; '900' ok). |
| values | object | add_type: {param: value}. |
| types | string[] | set_values: family types to change; ['*'] = all. Required when >1 type. |
| rows | object[] | set_values: [{type, values:{param: value}}]. |
| copy_from | string | add_type: copy values from this type. |
| category | string | set_category: family category. |
| reload | boolean | With family=: reload into the project after the edit. Default true. |
| overwrite | boolean | On reload: overwrite project type parameter values. Default: only if this call changed type values. |
| into | string[] | load_into: open projects to load into. Default: the pinned project only; more than one asks to confirm. |
| path | string | save_as: .rfa path. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
open(family)
add_param(name;kind,data,group,shared,formula,value,reporting)
remove_param(name)
rename_param(name,new_name)
set_param(name;kind,group,reporting)
set_formula(name,formula)
set_values(name+value/rows;types)
add_type(name;copy_from,values)
rename_type(name,new_name)
delete_type(name)
set_category(category)
load_into(;into,overwrite)
save()
save_as(path)
```

#### `mep` (write (non-destructive); 3181 B)

MEP: pipes, ducts, conduits, cable trays and flex runs along points with automatic elbows; connect elements; create/edit piping, duct and electrical systems, circuits; insulation; spaces and zones.

| param | type | notes |
|---|---|---|
| points | points | Run path [[x,y],...] or [[x,y,z],...] (z above level). |
| level | string | Level name or id. |
| offset | number | Run centerline height above level mm. Default 2700. |
| type | string | Pipe/duct/conduit/tray/insulation type name. |
| system | string | System type (e.g. Domestic Cold Water, Supply Air) or system name. |
| size | number | Diameter mm. |
| width | number | Duct/tray width mm. |
| height | number | Duct/tray height mm. |
| slope | number | Pipes: slope in percent. Default 0. |
| fittings | boolean | Add elbows/tees at bends. Default true. |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| kind | piping\|duct\|electrical | system_create kind. |
| name | string | Name. |
| number | string | Space number. |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| all | boolean | space: in every enclosed area of the level. |
| thickness | number | insulate: mm. |
| panel | string | circuit: panel name or id. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
pipe(points,level;type,system,size,offset,slope,fittings)
duct(points,level;type,system,size/width+height,offset,fittings)
conduit(points,level;type,size,offset,fittings)
cable_tray(points,level;type,width,height,offset,fittings)
flex_pipe(points,level;type,system,size,offset)
flex_duct(points,level;type,system,size,offset)
connect(ids)
system_create(ids,kind;name,type)
system_add(system,ids)
system_remove(system,ids)
circuit(ids;panel)
insulate(ids/from,type,thickness)
space(level,at/all;name,number)
zone(name,ids;level)
```

#### `structure` (write (non-destructive); 2879 B)

Structure: columns at points or grid intersections, beams along lines or between columns, braces, beam systems, isolated/wall/slab foundations. type='Family: Type'; lengths mm.

| param | type | notes |
|---|---|---|
| type | string | Type: 'Family: Type', type name, or id. |
| level | string | Base or reference level. |
| top | string | column: top level. Default next level up. |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| points | points | Several points (columns) or a chain (beams, beam_system outline). |
| grids | string[] | column: at intersections of these grids; ['*'] = all. |
| between | string[] | beam: column ids to connect in order. |
| start | number[] | Beam/brace or slanted column start [x,y,z]. |
| end | number[] | Beam/brace or slanted column end [x,y,z]. |
| offset | number | Offset from level mm (beam z, column base). |
| top_offset | number | column: top offset mm. |
| rotation | number | Degrees, counterclockwise. Default 0. |
| spacing | number | beam_system: spacing mm. |
| direction | number | beam_system: beam direction degrees. Default 0 (x axis). |
| kind | isolated\|wall\|slab | foundation kind. |
| ids | string[] | foundation: columns (isolated) or walls (wall). |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
column(type,level,at/points/grids;top,offset,top_offset,rotation,start,end)
beam(type,level,start+end/points/between;offset)
brace(type,level,start,end)
beam_system(type,level,points;spacing,direction)
foundation(kind,type;ids/from,points,level)
```

#### `manage_document` (destructive, openWorld; 2975 B)

Files: open (rvt/rfa; detach, audit, worksets), activate, save, save_as, close, new project from template, new family from .rft, purge unused, project units, base/survey point. Risky ones ask to confirm.

| param | type | notes |
|---|---|---|
| path | string | File path (.rvt, .rfa). |
| template | string | Template .rte/.rft path or name. new_project default: settings template. |
| activate | boolean | open/new_project: show it in the UI. Default true. |
| local | string | open (central model): path of the new local copy. Default Documents\<name>_<user>.rvt. |
| central | boolean | open: open the central file itself instead of a local copy (asks to confirm). |
| detach | no\|preserve\|discard | open: detach from central. Default no. |
| audit | boolean | open: audit. |
| worksets | all\|none\|last\|editable | open: worksets to open. Default last. |
| overwrite | boolean | Replace an existing file. |
| as_central | boolean | save_as: save as central model. |
| compact | boolean | Compact the file. |
| save | boolean | close: save first. Default false (unsaved changes ask to confirm). |
| passes | integer | purge: repeat passes 1-3. Default 3. |
| unit | mm\|cm\|m\|in\|ft | set_units: project length unit. |
| accuracy | number | set_units: rounding, e.g. 1 or 0.1. |
| base_point | point/box | coordinates: project base point [x,y,z] mm. |
| survey_point | point/box | coordinates: survey point [x,y,z] mm. |
| true_north | number | coordinates: true north angle degrees. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
open(path;activate,detach,audit,worksets,local,central)
activate(doc)
save(;compact)
save_as(path;overwrite,as_central,compact)
close(doc;save)
new_project(path;template,overwrite,activate)
new_family(template,path)
purge(;passes)
set_units(unit;accuracy)
coordinates(base_point/survey_point/true_north)
```

#### `worksharing` (destructive, openWorld; 1873 B)

Workshared models: sync with central (comment, relinquish), reload latest, relinquish all, create/rename worksets, set active workset, move elements to a workset, borrow elements, enable worksharing.

| param | type | notes |
|---|---|---|
| comment | string | sync: comment. |
| relinquish | boolean | sync: relinquish everything after. Default true. |
| compact | boolean | sync: compact central. |
| workset | string | Workset name. |
| name | string | New workset name (enable: name for the default workset). |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
sync(;comment,relinquish,compact)
reload_latest()
relinquish()
workset_create(name)
workset_rename(workset,name)
set_active(workset)
move_to(workset,sel)
borrow(sel)
enable(;name)
```

#### `links` (destructive, openWorld; 1683 B)

Linked models and CAD: link RVT/IFC/DWG (origin, center or shared coordinates), import CAD, reload, reload from a new path, unload, remove, acquire coordinates. Read link contents via find_elements link=.

| param | type | notes |
|---|---|---|
| path | string | File path. |
| link | string | Link name or id. |
| position | origin\|center\|shared\|base_point | Placement. Default origin. |
| view | string | CAD: view to place in. Default active. |
| cad_units | auto\|mm\|cm\|m\|in\|ft | CAD import units. Default auto. |
| this_view_only | boolean | CAD: current view only. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
link_rvt(path;position)
link_ifc(path;position)
link_cad(path;position,view,cad_units,this_view_only)
import_cad(path;position,view,cad_units,this_view_only)
reload(link)
reload_from(link,path)
unload(link)
remove(link)
acquire_coordinates(link)
```

#### `export` (write (non-destructive), openWorld; 1483 B)

Export pdf (sheets/views, combined or not), dwg, dxf, ifc, nwc, image (hi-res), csv (schedules), fbx, gbxml to a folder (default <home>/exports/<doc>). Long exports continue as a job.

| param | type | notes |
|---|---|---|
| format **(required)** | enum(9) | File format. |
| views | string[] | Views/sheets (names, numbers, ids), or ['all_sheets'], ['set:<sheet set>']. Default active view. |
| folder | string | Output folder. Default <home>/exports/<doc>. |
| name | string | File name pattern, e.g. '{number} - {name}'. |
| combine | boolean | pdf: one combined file. Default false. |
| setup | string | dwg/dxf/ifc: export setup name. |
| size | integer | image: long edge px. Default 3000. |
| color | color\|gray\|bw | pdf/image: default color. |
| overwrite | boolean | Replace existing files. Default false. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

#### `model_delivery` (destructive, openWorld; 1513 B)

Delivery packages of standalone RVTs (links, cleanup, exports, QA) from a saved recipe: inspect sources, preview (gives confirm), execute (job), save/get/list recipes, test fixture. Recipe shape: help topic=recipe.

| param | type | notes |
|---|---|---|
| sources | string[] | inspect: source .rvt paths. |
| project | string | Project id (recipe library key). |
| recipe | object | Delivery recipe; shape in help {topic:'recipe'}. |
| expected_sha | string | save_recipe: only overwrite this version. |
| template | string | fixture: .rte template path. |
| folder | string | fixture: new empty folder. |
| id | string | fixture: fixture id. |
| limit | integer | list_recipes: default 100. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
inspect(sources;project,recipe)
preview(recipe)
execute(confirm)
save_recipe(recipe;expected_sha)
get_recipe(project)
list_recipes(;limit)
fixture(template,folder,id)
```

### Batch, undo, jobs, help

#### `change_set` (destructive; 1400 B)

Run up to 100 write ops in one undo step, all or nothing; later ops use earlier results: '$0' (first id), '$0.ids', '$1.type'. ops=[{tool:'create_elements',op:'level',...}]. Not for open/save/sync/close.

| param | type | notes |
|---|---|---|
| ops | object[] | [{tool, op, ...that op's params}]. |
| name | string | Undo history name. Default 'change_set'. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

#### `undo` (destructive; 727 B)

Undo your last write(s) in the target doc while they are still Revit's newest changes (steps 1-10, default 1); redo=true redoes. If the user edited since, explains how to revert instead.

| param | type | notes |
|---|---|---|
| steps | integer | 1-10. Default 1. |
| redo | boolean | Redo instead. |
| mode | auto\|compensate | auto (default): Revit undo; compensate: apply the inverse change as a new write. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `job_status` (readOnly, idempotent; 536 B)

State, progress and result of a job (j#: export, sync, open, delivery, long scan) or of a write whose outcome was unclear (w#). wait= seconds to wait, 0-45, default 20. No id: your recent jobs.

| param | type | notes |
|---|---|---|
| id | string | Job id j# or write id w#. |
| wait | integer | Seconds to wait for completion, 0-45. Default 20. |

#### `cancel_job` (write (non-destructive), idempotent; 504 B)

Cancel a queued or running job or queued request. A running Revit step finishes or rolls back safely; partial exports and delivery staging are removed.

| param | type | notes |
|---|---|---|
| id **(required)** | string | Job id j# or request id w#. |
| reason | string | Why (logged). |

#### `help` (readOnly, idempotent; 572 B)

Params and a working example for a tool or op: help {tool:'create_elements',op:'wall'}. topic= units, targeting, selectors, confirm, errors, recipe, run_csharp, workflow:<audit|sheets|family|rooms|visual>, error codes.

| param | type | notes |
|---|---|---|
| tool | string | Tool name. |
| op | string | Op name. |
| topic | string | Topic, or an error code. |

### Opt-in

#### `run_csharp` (destructive, openWorld; 987 B)

Run C# on the Revit API when no tool fits. Off unless the user enabled it locally. read and dry_run (default) always roll back; dry_run reports changes and a token; commit needs that token and the user's OK.

| param | type | notes |
|---|---|---|
| code **(required)** | string | C# method body with doc, uidoc, app, args (IDictionary), log(object); return a value. |
| args | object | Values passed as args. |
| mode | read\|dry_run\|commit | Default dry_run. |
| timeout | integer | Seconds 1-45. Default 30. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

---

## Appendix B. Op registry (generated from SPEC-registry.mjs; normative)

Columns: kind; impl (addin = [Op] handler required, broker = broker only, control = add-in control pipe, both); scope; ui = needs UI-active doc; min = minimum Revit year; idle = may run from the Idling fallback; tx; blast rules; strict = warnings roll back; job; inproc = callable from the in-process bridge; cs = allowed inside change_set.

| key | kind | impl | scope | ui | min | idle | tx | blast | strict | job | inproc | cs |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| status | control | broker | none | - | 2024 | y | none | - | - | never | - | - |
| set_target | control | broker | none | - | 2024 | y | none | - | - | never | - | - |
| list.levels | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.grids | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.views | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.sheets | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.schedules | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.rooms | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.areas | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.spaces | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.types | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.families | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.materials | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.worksets | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.phases | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.phase_filters | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.design_options | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.links | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.revisions | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.view_templates | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.view_filters | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.view_types | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.viewport_types | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.title_blocks | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.text_types | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.dimension_types | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.tag_types | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.line_styles | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.line_patterns | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.fill_patterns | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.scope_boxes | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.groups | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.systems | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.system_types | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| list.project_info | read | addin | project | - | 2024 | y | none | - | - | never | y | - |
| list.units | read | addin | project | - | 2024 | y | none | - | - | never | y | - |
| find_elements | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| describe_elements | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| get_view | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| read_schedule | read | addin | project | - | 2024 | y | temp | - | - | never | y | - |
| read_family | read | addin | project_or_family | - | 2024 | - | none | - | - | auto | y | - |
| check_model.stats | read | addin | project | - | 2024 | y | none | - | - | never | y | - |
| check_model.warnings | read | addin | project | - | 2024 | y | none | - | - | never | y | - |
| check_model.readiness | read | addin | project | - | 2024 | y | none | - | - | never | y | - |
| check_model.purgeable | read | addin | project | - | 2024 | y | none | - | - | auto | y | - |
| check_model.clashes | read | addin | project | - | 2024 | y | none | - | - | auto | y | - |
| get_quantities | read | addin | project | - | 2024 | y | none | - | - | never | y | - |
| get_changes | read | addin | any | - | 2024 | y | none | - | - | never | y | - |
| read_many | read | broker | none | - | 2024 | y | none | - | - | never | - | - |
| capture | read | addin | any | - | 2024 | - | temp | - | - | auto | y | - |
| ui.select | ui | addin | project_or_family | y | 2024 | y | none | - | - | never | y | - |
| ui.zoom_to | ui | addin | project_or_family | y | 2024 | y | none | - | - | never | y | - |
| ui.isolate | ui | addin | project_or_family | - | 2024 | y | in | - | - | never | y | - |
| ui.hide | ui | addin | project_or_family | - | 2024 | y | in | - | - | never | y | - |
| ui.reset | ui | addin | project_or_family | - | 2024 | y | in | - | - | never | y | - |
| ui.activate_view | ui | addin | project_or_family | - | 2024 | - | none | - | - | never | y | - |
| ui.activate_doc | ui | addin | none | - | 2024 | - | none | - | - | never | y | - |
| ui.open_views | ui | addin | project_or_family | y | 2024 | y | none | - | - | never | y | - |
| ui.close_views | ui | addin | project_or_family | y | 2024 | y | none | - | - | never | y | - |
| ui.dialogs | control | control | none | - | 2024 | y | none | - | - | never | y | - |
| ui.press | control | control | none | - | 2024 | y | none | button | - | never | y | - |
| create_elements.level | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.grid | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.wall | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.floor | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.roof | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.roof_extrusion | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.ceiling | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.room | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.room_separator | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.area | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.area_boundary | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.opening | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.shaft | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.ref_plane | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.model_line | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.stairs | write | addin | project | - | 2024 | - | own | create | - | never | y | y |
| create_elements.railing | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.curtain_grid | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| create_elements.toposolid | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| place_family.place | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| place_family.load | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.move | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| modify_elements.copy | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| modify_elements.rotate | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| modify_elements.mirror | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| modify_elements.array | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| modify_elements.align | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.delete | write | addin | project | - | 2024 | y | in | delete | - | never | - | y |
| modify_elements.pin | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| modify_elements.change_type | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| modify_elements.join | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.unjoin | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.switch_join | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.cut | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.uncut | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.attach | write | addin | project | - | 2027 | y | in | - | - | never | y | y |
| modify_elements.detach | write | addin | project | - | 2027 | y | in | - | - | never | y | y |
| modify_elements.flip | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| modify_elements.split | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.reshape | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.group | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.ungroup | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| modify_elements.place_group | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| set_parameters | write | addin | project_or_family | - | 2024 | y | in | bulk | - | never | y | y |
| edit_types.duplicate_type | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_types.rename_type | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_types.delete_type | write | addin | project | - | 2024 | y | in | delete | - | never | - | y |
| edit_types.set_layers | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_types.set_default | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_types.material_create | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_types.material_edit | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_types.rename_family | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_views.create | write | addin | project | - | 2024 | y | in | - | y | never | y | y |
| edit_views.duplicate | write | addin | project | - | 2024 | y | in | - | y | never | y | y |
| edit_views.rename | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_views.set | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_views.create_template | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_views.delete | write | addin | project | - | 2024 | y | in | delete | - | never | - | y |
| view_graphics.override | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| view_graphics.reset | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| view_graphics.hide | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| view_graphics.unhide | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| view_graphics.color_by | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| view_graphics.filter_create | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| view_graphics.filter_edit | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| view_graphics.filter_apply | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| view_graphics.filter_remove | write | addin | project | - | 2024 | y | in | bulk | - | never | y | y |
| edit_sheets.create | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| edit_sheets.duplicate | write | addin | project | - | 2024 | y | in | - | y | never | y | y |
| edit_sheets.rename | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_sheets.set_titleblock | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_sheets.place | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_sheets.move_viewport | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_sheets.remove_viewport | write | addin | project | - | 2024 | y | in | - | - | never | - | y |
| edit_sheets.set_viewport_type | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_sheets.revision_create | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_sheets.revision_edit | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_sheets.revision_add | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_sheets.revision_remove | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| annotate.tag | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| annotate.tag_all | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| annotate.text | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| annotate.dimension | write | addin | project | - | 2024 | y | in | - | y | never | y | y |
| annotate.dimension_edit | write | addin | project | - | 2024 | y | in | - | y | never | y | y |
| annotate.spot | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| annotate.detail_line | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| annotate.filled_region | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| annotate.detail_item | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| annotate.revision_cloud | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| annotate.copy_to_views | write | addin | project | - | 2024 | y | in | - | y | never | y | y |
| edit_schedules.create | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_schedules.add_fields | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_schedules.remove_fields | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_schedules.set_field | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_schedules.order_fields | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_schedules.set_filters | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_schedules.set_sort | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_schedules.set_options | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_schedules.duplicate | write | addin | project | - | 2024 | y | in | - | - | never | y | y |
| edit_family.open | lifecycle | addin | project_or_family | - | 2024 | - | lifecycle | - | - | auto | - | - |
| edit_family.add_param | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.remove_param | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.rename_param | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.set_param | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.set_formula | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.set_values | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.add_type | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.rename_type | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.delete_type | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.set_category | write | addin | project_or_family | - | 2024 | - | none | - | - | never | - | y |
| edit_family.load_into | write | addin | family | - | 2024 | - | none | multi_doc | - | never | - | - |
| edit_family.save | lifecycle | addin | family | - | 2024 | - | lifecycle | - | - | never | - | - |
| edit_family.save_as | lifecycle | addin | family | - | 2024 | - | lifecycle | file_overwrite | - | never | - | - |
| mep.pipe | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.duct | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.conduit | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.cable_tray | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.flex_pipe | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.flex_duct | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.connect | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.system_create | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.system_add | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.system_remove | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.circuit | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.insulate | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.space | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| mep.zone | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| structure.column | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| structure.beam | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| structure.brace | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| structure.beam_system | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| structure.foundation | write | addin | project | - | 2024 | y | in | create | - | never | y | y |
| manage_document.open | lifecycle | addin | none | - | 2024 | - | lifecycle | central_open | - | auto | - | - |
| manage_document.activate | ui | addin | none | - | 2024 | - | none | - | - | never | - | - |
| manage_document.save | lifecycle | addin | project_or_family | - | 2024 | - | lifecycle | - | - | auto | - | - |
| manage_document.save_as | lifecycle | addin | project_or_family | - | 2024 | - | lifecycle | file_overwrite | - | auto | - | - |
| manage_document.close | lifecycle | addin | project_or_family | - | 2024 | - | lifecycle | unsaved_close | - | auto | - | - |
| manage_document.new_project | lifecycle | addin | none | - | 2024 | - | lifecycle | file_overwrite | - | auto | - | - |
| manage_document.new_family | lifecycle | addin | none | - | 2024 | - | lifecycle | file_overwrite | - | auto | - | - |
| manage_document.purge | write | addin | project | - | 2024 | y | in | delete | - | auto | - | y |
| manage_document.set_units | write | addin | project | - | 2024 | y | in | - | - | never | - | y |
| manage_document.coordinates | write | addin | project | - | 2024 | y | in | always | - | never | - | y |
| worksharing.sync | lifecycle | addin | project | - | 2024 | - | lifecycle | always | - | always | - | - |
| worksharing.reload_latest | lifecycle | addin | project | - | 2024 | - | lifecycle | - | - | always | - | - |
| worksharing.relinquish | lifecycle | addin | project | - | 2024 | - | lifecycle | - | - | auto | - | - |
| worksharing.workset_create | write | addin | project | - | 2024 | y | in | - | - | never | - | y |
| worksharing.workset_rename | write | addin | project | - | 2024 | y | in | - | - | never | - | y |
| worksharing.set_active | write | addin | project | - | 2024 | y | in | - | - | never | - | y |
| worksharing.move_to | write | addin | project | - | 2024 | y | in | bulk | - | never | - | y |
| worksharing.borrow | lifecycle | addin | project | - | 2024 | - | none | - | - | auto | - | - |
| worksharing.enable | lifecycle | addin | project | - | 2024 | - | lifecycle | always | - | auto | - | - |
| links.link_rvt | write | addin | project | - | 2024 | - | in | - | - | never | - | y |
| links.link_ifc | write | addin | project | - | 2024 | - | in | - | - | never | - | y |
| links.link_cad | write | addin | project | - | 2024 | - | in | - | - | never | - | y |
| links.import_cad | write | addin | project | - | 2024 | - | in | - | - | never | - | y |
| links.reload | lifecycle | addin | project | - | 2024 | - | lifecycle | - | - | auto | - | - |
| links.reload_from | lifecycle | addin | project | - | 2024 | - | lifecycle | - | - | auto | - | - |
| links.unload | lifecycle | addin | project | - | 2024 | - | lifecycle | - | - | never | - | - |
| links.remove | write | addin | project | - | 2024 | - | in | always | - | never | - | y |
| links.acquire_coordinates | write | addin | project | - | 2024 | - | in | always | - | never | - | y |
| export.pdf | lifecycle | addin | project | - | 2024 | - | none | file_overwrite | - | auto | - | - |
| export.dwg | lifecycle | addin | project | - | 2024 | - | none | file_overwrite | - | auto | - | - |
| export.dxf | lifecycle | addin | project | - | 2024 | - | none | file_overwrite | - | auto | - | - |
| export.ifc | lifecycle | addin | project | - | 2024 | - | temp | file_overwrite | - | auto | - | - |
| export.nwc | lifecycle | addin | project | - | 2024 | - | none | file_overwrite | - | auto | - | - |
| export.image | lifecycle | addin | project | - | 2024 | - | none | file_overwrite | - | auto | - | - |
| export.csv | lifecycle | addin | project | - | 2024 | - | none | file_overwrite | - | auto | - | - |
| export.fbx | lifecycle | addin | project | - | 2024 | - | none | file_overwrite | - | auto | - | - |
| export.gbxml | lifecycle | addin | project | - | 2024 | - | none | file_overwrite | - | auto | - | - |
| model_delivery.inspect | read | addin | none | - | 2024 | - | lifecycle | - | - | always | - | - |
| model_delivery.preview | read | addin | none | - | 2024 | - | lifecycle | - | - | always | - | - |
| model_delivery.execute | lifecycle | addin | none | - | 2024 | - | lifecycle | always | - | always | - | - |
| model_delivery.save_recipe | control | broker | none | - | 2024 | - | lifecycle | - | - | never | - | - |
| model_delivery.get_recipe | read | broker | none | - | 2024 | - | lifecycle | - | - | never | - | - |
| model_delivery.list_recipes | read | broker | none | - | 2024 | - | lifecycle | - | - | never | - | - |
| model_delivery.fixture | lifecycle | addin | none | - | 2024 | - | lifecycle | always | - | always | - | - |
| change_set | write | both | project_or_family | - | 2024 | y | group | delete,bulk,create | - | never | y | - |
| undo | write | addin | project_or_family | y | 2024 | - | none | - | - | never | - | - |
| job_status | control | broker | none | - | 2024 | y | none | - | - | never | - | - |
| cancel_job | control | broker | none | - | 2024 | y | none | - | - | never | - | - |
| help | control | broker | none | - | 2024 | y | none | - | - | never | - | - |
| run_csharp | code | addin | project_or_family | - | 2024 | - | temp | code_commit | - | auto | - | - |
