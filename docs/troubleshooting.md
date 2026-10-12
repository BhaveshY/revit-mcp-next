# Troubleshooting

Start with the doctor. It checks the home, the add-in for each Revit year, the Codex and Claude registrations and
starts the broker exactly the way Codex does:

```powershell
$rmn = "$env:USERPROFILE\.revit-mcp-next"     # or your -Home folder
& "$rmn\runtime\node.exe" "$rmn\broker\revit-mcp-cli.mjs" doctor           # full (also runs codex/claude mcp get)
& "$rmn\runtime\node.exe" "$rmn\broker\revit-mcp-cli.mjs" doctor --quick   # what the installer runs
& "$rmn\runtime\node.exe" "$rmn\broker\revit-mcp-cli.mjs" doctor --json    # machine-readable
```

Each line is `<level> <id>: <message>` with a `fix:` when there is one. `fail` lines break something; `warn` lines
may; `info` lines are facts. The last line is `PASS` or `FAIL` with the counts.

## Doctor checks

| Check id | What it checks | Usual fix |
|---|---|---|
| `node` | The doctor itself runs on Node 24+ | Use the home's `runtime\node.exe` |
| `home` | The home has its `.revit-mcp-next-home` marker and is not under AppData, Temp or an app package | Rerun the installer |
| `receipt` | `install.json` exists (a dev install has none) | Rerun the installer |
| `runtime`, `broker` | `runtime\node.exe` runs and `broker\revit-mcp.mjs` exists (version, git sha, SDK) | Rerun the installer |
| `settings` | `config\settings.json` parses; shows `enableCodeExecution` | Fix the listed keys, or rerun the installer (it merges the file) |
| `auth` | `config\auth.env` has a token (only its fingerprint is printed) | Rerun the installer |
| `addin.env` | A user-level `REVIT_MCP_NEXT_HOME` variable that points elsewhere | Remove the variable, or install into that home |
| `addin.<year>.payload` | `addin\<year>\current.json` names a payload that exists | Rerun the installer |
| `addin.<year>.manifest` | `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin` points at a loader of **this** home. "split install" means the loader belongs to another home | Rerun the installer for the home you use |
| `addin.<year>.trust` | Revit's "Always Load" entry for the add-in | Choose Always Load once, or rerun with `-TrustRevitAlwaysLoad` |
| `addin.<year>.loader-log` | `logs\loader-<year>.log` was written in the last 7 days: the loader could not load the payload | Read the line, rerun the installer, restart Revit |
| `revit.<instance>` | A running Revit registered in this home, with its open docs | - |
| `revit.pid<pid>` | A `Revit.exe` that has no registration in this home: still starting, the add-in failed, or it uses another home | Wait; then check the manifest and loader-log lines |
| `codex.server` | `[mcp_servers.revit]` in `~\.codex\config.toml`: command, bundle, `REVIT_MCP_NEXT_HOME`, timeouts | Rerun the installer |
| `codex.approval` | `approval_mode = "auto"` for `status`, `help`, `set_target`, `ui` | Rerun the installer |
| `codex.legacy` | An old `[mcp_servers.revit-mcp-next]` block is still enabled (two brokers would drive Revit) | Rerun the installer |
| `codex.conflict` | Another Revit MCP server or the `revit-mcp-cowork` plugin is enabled | Disable it, or rerun with `-DisableConflictingPlugins` |
| `codex.plugin`, `codex.agents` | The `revit-mcp-next` skills plugin and the AGENTS.md Revit snippet | Rerun the installer |
| `codex.cli` | `codex mcp get revit --json` accepts the block (full doctor only) | Rerun the installer |
| `spawn.legacy`, `spawn.modern` | The broker starts with the Codex environment, answers `initialize` (2025-06-18 and 2026-07-28), lists the tools and runs `status` | Read the stderr part; antivirus exclusions for the home if it is slow |
| `claude.server` | User-scope `mcpServers.revit` in `~\.claude.json` | Rerun the installer |
| `claude.conflict` | Another Revit server, or `revit-mcp-cowork` enabled in Claude Code (it auto-approves every revit call) | `claude plugin disable <id>` |
| `claude.plugin`, `claude.cli` | The skills plugin, and `claude mcp get revit` reports connected | Rerun the installer; `/mcp` in Claude Code |
| `claude-desktop` | `mcpServers.revit` in Claude Desktop's config | Install with `-Clients codex,claude,claude-desktop` |
| `legacy` | Old 0.2/0.3 folders (`%LOCALAPPDATA%\RevitMcpNext`, packaged copies, `Addins\<year>\RevitMcpNext`) | Rerun with `-RemoveLegacy` |

## Common situations

**The revit tools are missing in the AI app.** MCP servers load when a thread or session starts. Codex: start a new
thread, or quit and reopen the desktop app; `codex mcp list` must show `revit`. Claude Code: `/mcp`, reconnect `revit`
or restart; `claude mcp list` must show it. Then run the doctor and look at `codex.server`, `claude.server` and the
`spawn.*` lines.

**`status` says no Revit is running, but Revit is open.** The add-in did not register in this home. Check the doctor's
`addin.<year>.manifest` and `addin.<year>.loader-log` lines and `revit.pid<pid>`. Causes: Revit was started before the
install (restart it), the manifest points to another home (split install), the user chose "Do not load" at the unsigned
add-in prompt, or Revit is still starting.

**Revit asks about an unsigned add-in.** Choose **Always Load** once; or rerun the installer with
`-TrustRevitAlwaysLoad`, which seeds `HKCU\Software\Autodesk\Revit\Autodesk Revit <year>\CodeSigning` for the add-in's
ClientId `6F78E70D-BE13-4E0B-9B11-9E28F876AF71`.

**The installer stops with "runs inside the app package".** A terminal inside the Codex desktop app is an MSIX package;
writes to `%APPDATA%` and `HKCU` would be virtualized and Revit would not see them. Use a normal Windows PowerShell, or
add `-Relaunch` (it starts a clean copy outside the package and waits for it).

**A call returns `ERROR <CODE>`.** The `fix:` line under it is the exact next step (a complete call, `ask the user: ...`
or `retry in N s`). `help {"topic":"<CODE>"}` explains any code. The table below lists all of them.

**`REVIT_BUSY` keeps coming back.** Revit is working on something else (a long MCP job, a sync, an export, a family
reload). `status {}` answers even then and shows what is executing; wait or `cancel_job` your own job.

**Revit hangs on a dialog.** `ui {"op":"dialogs"}` lists it; tools press only Cancel or Close by themselves. Known
harmless dialogs that appear during MCP work are answered automatically (`warn: REVIT_DIALOG_ANSWERED`); see
[SECURITY.md](../SECURITY.md#dialogs).

## Logs and support bundle

Everything is under the home (`%USERPROFILE%\.revit-mcp-next\logs` by default):

| File | Written by |
|---|---|
| `broker-YYYYMMDD.<n>.jsonl` | the broker (one process per client session) |
| `addin-<year>-YYYYMMDD.<n>.jsonl` | the add-in, per Revit year |
| `loader-<year>.log` | the loader, only when it fails |
| `install-<time>.log` | the installer |
| `code-exec-audit.jsonl`, `code\<sha256>.cs` | `run_csharp` (see [SECURITY.md](../SECURITY.md)) |

Requests carry a requestId across broker and add-in logs. For a bug report:

```powershell
& "$rmn\runtime\node.exe" "$rmn\broker\revit-mcp-cli.mjs" support
```

It writes `<home>\support\support-<time>.zip` with the doctor report, receipts, settings, registrations, manifests, the
`revit` client entries and the newest logs. It never includes `auth.env`, tokens or whole client config files.

For add-in diagnostics without an AI client, `revit-mcp-cli raw` sends one protocol frame to a running Revit and prints
the answer, for example `raw hello`, `raw health --year 2024` or
`raw list.levels --kind read --year 2024`. The token is read from the home and never printed.

## Error codes

Every error a tool returns is one of these. "Model changed?" says whether the model may have changed (`maybe`) or is
guaranteed unchanged (`no`). `<name>` parts are filled in from the error's details.

<!-- error-codes:begin -->
| Code | Meaning | Model changed? | fix: line |
|---|---|---|---|
| `ELEMENTS_PINNED` | Some of the elements are pinned; nothing was changed. | no | ask the user: <total> element(s) are pinned; only if the user agrees, repeat the call with allow_pinned:true |
| `EXPECT_MISMATCH` | The delete set (targets + dependents) differs from expect_count/expect_ids; nothing was deleted. | no | the same call with `{"preview":true}` (shows the real delete set; ask the user before deleting it) |
| `NEEDS_TYPES` | The family has several types and the call did not say which. | no | the same call with `{"types":["*"]}` (changes every type; or list the types to change) |
| `NOT_EDITABLE_FAMILY` | System, in-place and protected families cannot be edited. | no | `list {"kind":"families"}` (pick a loadable family) |
| `FORMULA_INVALID` | Revit rejected the formula. | no | the same call with `{"formula":"<corrected formula>"}` |
| `PARAM_IN_USE` | The parameter is used by a formula, label or another family element. | no | when usedBy: ask the user: remove its uses first (<usedBy>), or keep the parameter; otherwise: ask the user: remove its uses first, or keep the parameter |
| `PARAM_HAS_FORMULA` | The parameter's value comes from a formula. | no | `edit_family {"op":"set_formula","family":<family>,"doc":<doc>,"name":<name>,"formula":""}` (clears the formula first) |
| `SHARED_PARAM_FILE_MISSING` | No shared parameter file is set or it cannot be read. | no | ask the user: set a shared parameter file in Revit (Manage > Shared Parameters), then retry |
| `SHARED_PARAM_RENAME` | Shared parameters cannot be renamed inside a family. | no | ask the user: shared parameters cannot be renamed; remove it and add the other shared parameter (remove_param, then add_param with shared=) |
| `NO_REVIT_RUNNING` | No running Revit has the revit-mcp-next add-in loaded. | no | when lastExited.doc: ask the user: start Revit <lastExited.year> and open <lastExited.doc>; this session reconnects automatically; otherwise: ask the user: start Revit 2024 or 2027 with the revit-mcp-next add-in and open the model, then call status |
| `REVIT_STARTING` | Revit is registered but still starting. | no | retry in 10 s |
| `REVIT_EXITED` | The Revit instance exited before or during the call. | maybe | when doc: ask the user: restart Revit <year> and open <doc>; this session reconnects automatically; otherwise: ask the user: restart Revit and open the model; this session reconnects automatically |
| `ADDIN_PIPE_MISSING` | Revit holds its registration lock but the add-in pipe is missing (the pipe host failed). | no | when logPath: ask the user: restart Revit <year>; if it happens again, send the add-in log <logPath>; otherwise: ask the user: restart Revit; if it happens again, run status {"detail":"full"} and send the add-in log |
| `ADDIN_OUTDATED` | The running add-in speaks an older protocol or catalog than this broker. | no | when year: ask the user: restart Revit <year> so it loads the installed add-in; otherwise: ask the user: restart Revit so it loads the installed add-in |
| `ADDIN_NEWER_THAN_BROKER` | The add-in is newer than this broker. | no | ask the user: re-run the revit-mcp-next installer, then start a new chat thread |
| `AUTH_MISMATCH` | The broker and Revit use different auth tokens. | no | ask the user: re-run the revit-mcp-next installer (it keeps one token), or restart Revit |
| `AUTH_NOT_CONFIGURED` | No auth token is available yet. | no | ask the user: start Revit once with the add-in (it creates the token file), or re-run the revit-mcp-next installer |
| `BRIDGE_ACCESS_DENIED` | Access to the Revit pipe was denied. | no | ask the user: run Revit and the AI client as the same Windows user, both non-elevated |
| `BRIDGE_BUSY` | Too many calls are already waiting for this Revit; nothing was sent. | no | retry in 10 s |
| `REVIT_QUEUE_FULL` | The add-in's queue is full; nothing was sent. | no | retry in 10 s |
| `REVIT_BUSY` | Revit is executing other work (an MCP op, a job or a native operation); nothing ran. | no | when ref: `job_status {"id":<ref>}`; otherwise: retry in 15 s |
| `REVIT_DIALOG_OPEN` | A modal dialog blocks Revit; nothing ran. | no | `ui {"op":"dialogs","instance":<instance>}` |
| `REVIT_NOT_RESPONDING` | Revit's main window has not responded for 5 s or more; nothing ran. | no | ask the user: check Revit (it may be busy in a native operation), then retry once |
| `REVIT_EDIT_MODE_OR_COMMAND` | Revit is probably in a command or edit mode (sketch, placement); nothing ran. | no | ask the user: press Esc twice or click Finish in Revit, then retry once |
| `REQUEST_CANCELLED` | The client cancelled the call. | no | ask the user: nothing to do; repeat the call only if the user still wants it |
| `REVIT_SHUTTING_DOWN` | Revit is closing; the call was not run. | no | ask the user: wait until Revit has closed or restart it, then retry |
| `REQUEST_ID_CONFLICT` | A request id was reused with different content. | no | retry in 1 s |
| `RESPONSE_SERIALIZATION_FAILED` | The add-in could not serialize the result. | no | the same call with `{"limit":20}` (or ask for fewer fields) |
| `RESPONSE_TOO_LARGE` | The result exceeded the 4 MiB frame. | no | when suggestedLimit: the same call with `{"limit":<suggestedLimit>}`; otherwise: the same call with `{"limit":20}` |
| `CATALOG_MISSING` | The add-in was built without the embedded catalog. | no | ask the user: rebuild the add-in (npm run build, then scripts/build-addin.ps1) and restart Revit |
| `INTERNAL_ERROR` | Unexpected error (logged with the requestId). | maybe | when requestId: ask the user: report requestId <requestId> to the developer; retry once; otherwise: ask the user: report this error to the developer; retry once |
| `NO_OPEN_DOCUMENT` | Revit is running with no document open. | no | when path: `manage_document {"op":"open","path":<path>}`; when year: ask the user: open a model in Revit <year>, or tell me its path so I can open it with manage_document; otherwise: ask the user: open a model in Revit, or tell me its path so I can open it with manage_document |
| `TARGET_AMBIGUOUS` | Several documents match and none is pinned; nothing ran. | no | `set_target {"doc":<one of the options>}` |
| `TARGET_CLOSED` | The pinned document was closed. | no | when path: `manage_document {"op":"open","path":<path>}`; when options.0: `set_target {"doc":<one of the options>}`; otherwise: `status {}` |
| `DOC_NOT_OPEN` | The document is not open (for example after a Revit restart). | no | when path: `manage_document {"op":"open","path":<path>}`; when options.0: `set_target {"doc":<one of the options>}`; otherwise: `status {}` |
| `TARGET_CHANGED` | The target document changed since your last write; nothing was written. | no | repeat the identical call (writes into <to>); or (when fromDoc) `set_target {"doc":<fromDoc>}`, or `set_target {"doc":"active"}` |
| `TARGET_STALE` | The document reference no longer matches (internal; the broker refreshes and retries once). | no | `status {}` |
| `FAMILY_DOC_REQUIRED` | This op needs a family document, but the target is a project. | no | when family: `edit_family {"op":"open","family":<family>}`; when options.0: `set_target {"doc":<one of the options>}`; otherwise: `help {"tool":"edit_family","op":"open"}` |
| `PROJECT_DOC_REQUIRED` | This op needs a project document, but the target is a family. | no | when options.0: `set_target {"doc":<one of the options>}`; otherwise: `status {}` |
| `DOC_READ_ONLY` | The target copy is read-only. | no | when editable: `set_target {"doc":<editable>}`; otherwise: ask the user: open an editable copy of the model, or save this one under a new name with manage_document save_as |
| `LINK_READ_ONLY` | Elements of linked models are read-only. | no | when path: `manage_document {"op":"open","path":<path>}`; otherwise: ask the user: open the linked model itself to edit it; find_elements link= is read-only |
| `NEEDS_ACTIVE_DOC` | This op needs the target document to be the active document in Revit. | no | when doc: `ui {"op":"activate_doc","doc":<doc>}`; otherwise: `ui {"op":"activate_doc","doc":<doc.n>}` |
| `WRITE_STILL_RUNNING` | The write is still running in Revit at the time budget. | maybe | `job_status {"id":<ref>}` (never repeat the write) |
| `READ_STILL_RUNNING` | A read that cannot yield is still running at the time budget. | no | `job_status {"id":<ref>}` (or repeat the same call: it returns the cached result) |
| `WRITE_OUTCOME_UNKNOWN` | The response was lost and Revit is gone; the write may or may not have committed. | maybe | when elements: ask the user: reopen the model in Revit and check <elements>; never repeat the write blindly; otherwise: ask the user: reopen the model in Revit and check whether the change is there; never repeat the write blindly |
| `REVIT_TRANSACTION_ROLLED_BACK` | Revit rolled the change back; the failures are listed. | no | `help {"tool":<tool>,"op":<op>}` (then repeat the call with the named element or parameter fixed) |
| `REVIT_REFUSED` | The Revit API refused the operation; its message is quoted. | no | `help {"tool":<tool>,"op":<op>}` |
| `INVALID_ARGS` | The arguments did not validate; nothing ran. | no | when example: `<tool> <example args>`; otherwise: `help {"tool":<tool>,"op":<op>}` |
| `UNKNOWN_OP` | The op is not one of this tool's ops. | no | when closest: the same call with `{<param>:<closest>}`; otherwise: `help {"tool":<tool>}` |
| `NOT_FOUND` | A name or id did not resolve. | no | when candidates.0: the same call with `{<param>:<candidates.0>}`; when kind: ask the user: check the <kind> name; list or find_elements show what exists; otherwise: ask the user: check the name or id; list or find_elements show what exists |
| `AMBIGUOUS_NAME` | A name matches several items. | no | when candidates.0: the same call with `{<param>:<candidates.0>}` (or another of the options); otherwise: ask the user: use the exact name or the id; list or find_elements show what exists |
| `NAME_TAKEN` | The name or number is already used. | no | when next: the same call with `{<param>:<next>}`; otherwise: ask the user: choose a name or number that is not used yet |
| `UNSUPPORTED_VERSION` | This op needs a newer Revit. | no | when alternative.tool: `<alternative.tool> <alternative.args>`; when min: ask the user: this needs Revit <min> or newer; open the model in Revit <min>; otherwise: ask the user: this needs a newer Revit version |
| `UNSUPPORTED_OP` | The Revit API cannot do this. | no | when alternative.tool: `<alternative.tool> <alternative.args>`; when hint: ask the user: <hint>; otherwise: ask the user: this step must be done by hand in Revit |
| `NOT_EDITABLE` | The element is owned by another user or was updated in central. | no | when reason=updated_in_central: `worksharing {"op":"reload_latest"}` (then retry); when owner: ask the user: have <owner> relinquish the element (or sync), then retry; otherwise: ask the user: have its owner relinquish the element (or sync), then retry |
| `TEMPLATE_CONTROLLED` | The view's template controls this setting. | no | when template_id: `set_parameters {"ids":[<template_id>],"values":<values>}` (changes the template, which affects every view using it); when template: the same call with `{"view":<template>}` (changes the template, which affects every view using it); otherwise: ask the user: remove the view template (edit_views set template:'none') or change the template itself |
| `NO_TAG_FAMILY` | No tag type is loaded for the category. | no | when category: ask the user: load a <category> tag family first (place_family op=load with the tag .rfa path); otherwise: ask the user: load a tag family for this category first (place_family op=load with the tag .rfa path) |
| `LEGEND_NEEDS_SEED` | Revit cannot create a legend from nothing and none exists to copy. | no | ask the user: create one empty legend view in Revit once, then retry |
| `CANNOT_CLOSE_ACTIVE` | The only open document is the active one; the API cannot close it. | no | ask the user: close it in Revit yourself |
| `LAST_VIEW` | Revit refuses to close the last open view of a document. | no | `manage_document {"op":"close","doc":<doc.n>}` |
| `SAVE_AS_REQUIRED` | The document was never saved. | no | when tool=edit_family: ask the user: tell me where to save the family (.rfa path), then I call edit_family save_as with that path; when path: `manage_document {"op":"save_as","path":<path>}`; otherwise: ask the user: tell me where to save it, then I call manage_document save_as with that path |
| `EXPORTER_MISSING` | The exporter for this format is not installed in this Revit. | no | when format: ask the user: install the <format> exporter for this Revit version, or choose another format; otherwise: ask the user: install the exporter for this format, or choose another format |
| `CAPTURE_GPU_UNAVAILABLE` | Revit could not render the image (DirectX lost, e.g. over RDP). | no | the same call with `{"style":"hidden"}` |
| `CAPTURE_NOT_EXPORTABLE` | This view cannot be exported as an image. | no | when viewKind=Schedule: `read_schedule {"schedule":<view>}`; otherwise: `list {"kind":"views","graphical":true}` |
| `CONFIRM_STALE` | Elements of the confirmed plan changed since the plan was made. | no | the same call without `confirm` (gets a new plan) |
| `CONFIRM_EXPIRED` | The confirm token expired or is unknown (tokens live 10 min and are single use). | no | when retry: `<tool> <retry args>` (gets a new plan); otherwise: ask the user: repeat the original call without confirm to get a new plan |
| `CONFIRM_MISMATCH` | The token belongs to a different call, or the re-sent args differ from the plan. | no | when retry: `<tool> <retry args>`; otherwise: the same call without `confirm` (gets a new plan) |
| `PAGE_EXPIRED` | The page token expired (30 min) or is unknown. | no | when repeat.tool: `<repeat.tool> <repeat.args>`; otherwise: ask the user: repeat the original call without page |
| `HANDLE_EXPIRED` | The handle expired or is unknown (r# lives 60 min). | no | when repeat.tool: `<repeat.tool> <repeat.args>`; otherwise: ask the user: repeat the find_elements call that produced the handle |
| `CAPTURE_GONE` | The capture id is unknown or its file was cleaned up. | no | when repeat.tool: `<repeat.tool> <repeat.args>`; otherwise: ask the user: capture the view again, then compare with the new id |
| `JOB_UNKNOWN` | The job or write id is unknown to this session (ids do not survive a broker restart). | no | `status {"include":["writes"]}` |
| `OUTCOME_EXPIRED` | The write outcome is no longer recorded. | no | `status {"include":["writes"]}` |
| `UNDO_BLOCKED` | A newer change that is not yours is on top of Revit's undo stack. | no | when compensable=true: `undo {"mode":"compensate"}`; when top: ask the user: press Ctrl+Z in Revit (the newest change is "<top>"); otherwise: ask the user: press Ctrl+Z in Revit |
| `UNDO_UNCONFIRMED` | Revit did not confirm the undo within 10 s. | maybe | `get_changes {"since":"last"}` |
| `IRREVERSIBLE` | This change cannot be compensated (delete, purge, family load or code). | no | ask the user: press Ctrl+Z in Revit to undo it |
| `CODE_EXECUTION_DISABLED` | run_csharp is not enabled on this computer. | no | ask the user: C# execution is off; only you can enable it in the revit-mcp-next settings, and only if you want it (an agent must never edit settings.json); otherwise I use a tool |
| `CODE_EXECUTION_DECLINED` | The user clicked Block in Revit's code-execution prompt. | no | ask the user: allow code execution in the Revit prompt if you want it, or I use a tool instead |
| `CODE_EXECUTION_UNAVAILABLE` | The C# engine could not be loaded in this Revit. | no | `status {"detail":"full"}` (shows why; use a tool instead) |
| `CODE_COMPILE_ERROR` | The C# code did not compile. | no | the same call with `{"code":"<corrected code>"}` (fix line <line>, col <col>) |
| `CODE_DENIED_API` | The code uses an API that is not allowed in this mode. | no | the same call with `{"code":"<code without <symbol>>"}` |
| `CODE_TIMEOUT` | The code exceeded its time limit and was stopped; everything was rolled back. | no | the same call with `{"code":"<faster code>"}` |
| `CODE_RUNTIME_ERROR` | The code threw an exception; everything was rolled back. | no | the same call with `{"code":"<corrected code>"}` |
| `DIALOG_NOT_FOUND` | No such dialog is open. | no | `ui {"op":"dialogs"}` |
| `DIALOG_PROTECTED` | This button could discard work or answers one of our own prompts; tools never press it. | no | ask the user: press the button yourself in Revit if you want it |
| `BUTTON_NOT_FOUND` | The dialog has no such button. | no | when options.0: `ui {"op":"press","dialog":<dialog>,"button":<one of the options>}`; otherwise: `ui {"op":"dialogs"}` |

Warnings (`warn:` lines; the call still worked):

| Code | Meaning |
|---|---|
| `REVIT_WARNING` | Revit reported a warning; the change was applied. |
| `REVIT_DIALOG_ANSWERED` | A known-safe Revit dialog was answered automatically. |
| `VALUE_CLAMPED` | A number was outside its range and was clamped. |
| `PARAM_IGNORED` | A param is not used by this op (or unknown) and was ignored. |
| `PARAM_RENAMED` | A param name was recognized under another spelling. |
| `PARTIAL_RESULT` | Only part of the result is shown. |
| `FITTING_FAILED` | A fitting could not be placed; the runs were created. |
| `IDS_GONE` | Some ids no longer exist and were skipped. |
| `NON_FINITE_NUMBER` | A number was not finite and became null. |
| `BRIDGE_RESPONSE_RECOVERED` | The response was lost on the pipe and recovered from the add-in ledger. |
| `SETTINGS_INVALID` | settings.json is invalid; the last good values are used. |
| `LATE_RESULT` | A call that was given up earlier completed. |

Notices (`notice:` lines; information only):

| Code | Meaning |
|---|---|
| `AUTO_PINNED` | The target doc was chosen automatically and pinned. |
| `TARGET_NOW` | The pin moved to this doc (explicit). |
| `REBOUND` | The pinned doc was found again (Revit restarted or the doc was reopened). |
| `SAVED_AS` | The pinned doc was saved under a new name; work continues on it. |
| `PIN_MOVED` | The automatic pin moved to another doc. |
| `ACTIVE_DIFFERS` | Revit's active doc is not the pinned target. |
| `FAMILY_EDITING` | The user is editing a family in Revit. |
| `RECOVERED_WRITE` | A previous session's write outcome was recovered. |
| `CODE_EXECUTION_ENABLED` | C# code execution is enabled in settings. |
| `SELECTION_STALE` | The selection is from when the doc was last active. |
<!-- error-codes:end -->

Notices the add-in adds to single ops (also information only):

| Code | Tool and op | Meaning |
|---|---|---|
| `NO_ROW_IDS` | `read_schedule` | Rows carry no element ids, because no field maps rows 1:1 to elements; `data.elementIds` lists the schedule's elements instead. |
| `ALREADY_OPEN` | `manage_document open` | The file was already open; no second copy was opened. |
| `LOCAL_COPY` | `manage_document open` | A central model was opened as a local copy (an existing local, or a new one). The central file is not edited. |
| `LOCAL_SAVED` | `manage_document save` | A workshared doc: only the local file was saved; `worksharing {"op":"sync"}` sends the changes to central. |
| `UNDO_CLEARED` | `worksharing sync`, `reload_latest` | Revit cleared its undo history; `undo` cannot step back over this point. |
| `UNSYNCED_KEPT` | `worksharing relinquish` | Elements changed since the last sync stay borrowed until the next sync. |
| `SAVE_AS_CENTRAL` | `worksharing enable` | Worksharing is on, but the doc has no file yet: save it once with `manage_document {"op":"save_as","path":"<central .rvt path>","as_central":true}` to create the central model. |
| `GBXML_BUILDING_ELEMENTS` | `export gbxml` | No placed rooms or spaces: the energy model is built from building elements (slower). |
