# Security

revit-mcp-next lets an AI client read and change open Revit models. Treat it as local automation with write access to
valuable project files. Report security issues privately to the repository owner (a GitHub security advisory on
`BhaveshY/revit-mcp-next`), not in a public issue.

## Local transport and the token

- The broker talks to Revit only over local named pipes (protocol v3). The add-in creates its pipes with an ACL for the
  current user, and every connection must start with `hello` carrying the token from `<home>\config\auth.env`.
  Requests without the right token are refused before anything runs in Revit. Neither side opens a network socket.
- `auth.env` holds a 256-bit random token per home, created by the installer (or by the add-in under a mutex) and
  restricted to the user, SYSTEM and Administrators. Each home has its own token, so a dev or e2e home cannot drive
  the Revit sessions of the installed home.
- The doctor and `revit-mcp-cli support` show only a token fingerprint. Support zips never contain `auth.env`, tokens
  or whole client config files. Never paste `auth.env` into a chat, an issue or a support mail.

## What a model can change without asking

Writes apply at once, each as one Revit undo step, but large or risky changes stop first. Such a result says
`NOT APPLIED - needs the user's OK` and stores the plan under a short confirm token. The thresholds are
`confirm.deleteOver` (20), `confirm.bulkOver` (200) and `confirm.createOver` (500) in `settings.json`. Overwriting a
file, closing an unsaved doc, syncing with central, opening a central with `central:true`, loading a family into
several docs, pressing a dialog button other than Cancel/Close and committing `run_csharp` always stop. The server
instructions, the `revit` skill and the AGENTS snippet tell the model to show the plan and ask; it calls again with
`confirm` only when the user asked for exactly this or agrees. A confirm call re-runs the stored arguments and fails
with `CONFIRM_STALE` if an affected element changed meanwhile.

Client approvals add a second layer. The installer auto-approves only `status`, `help`, `set_target` and `ui` in
Codex; every other tool follows the client's approval policy. **While the `revit-mcp-cowork` plugin is enabled, its
hook answers "allow" for revit tool calls, so Claude Code and Codex run destructive tools without asking.** See
[docs/clients.md](docs/clients.md#conflicting-plugins-and-servers).

## Opt-in C# execution (`run_csharp`)

`run_csharp` compiles and runs C# inside Revit. It is **off** by default and stays off unless the user turns it on.

- **Gate.** `enableCodeExecution` in `<home>\config\settings.json`. The user turns it on with the installer switch
  `-EnableCodeExecution` (off again with `-DisableCodeExecution`) or with
  `revit-mcp-cli settings merge --enable-code-execution`. The broker lists the tool only when the key was true at its
  start; the add-in re-reads the key on every call and refuses with `CODE_EXECUTION_DISABLED` whatever the broker
  says. No tool argument can change it.
- **Agents must not edit `settings.json`.** The server instructions, the `revit` skill and the AGENTS snippet forbid
  editing `~\.revit-mcp-next\config\settings.json` and enabling code execution. An agent that does so anyway works
  against the user; do not give a coding agent write access to the home for unrelated tasks.
- **Consent dialog.** With `codeExecution.consentPrompt: true`, the first run in each Revit session shows the add-in's
  own TaskDialog "Revit MCP Next: allow AI code execution?" (id `RevitMcpNext_CodeConsent`) with the client name, the
  code hash and the first lines of code. "Allow for this Revit session" consents until Revit exits; "Block" returns
  `CODE_EXECUTION_DECLINED` and suppresses the prompt for 10 minutes. The waiting call returns a job
  (`next: ask the user to answer the prompt in Revit, then job_status`). The dialog policy and `ui press` never answer
  it. The prompt is off by default (`consentPrompt: false`); turn it on for shared or demo machines.
- **Modes.** `read` and `dry_run` (the default) run inside a transaction group that is always rolled back. `commit`
  needs the confirm token of a dry run of the same code, args and doc, so the user is asked first (NOT APPLIED).
- **Deny lists**, checked on the semantic model: processes, network, registry, P/Invoke and `unsafe`, reflection emit
  and assembly loading, threads, file writes, `PostCommand` and UI windows are always refused. Save, close, sync,
  reload, export, open, worksharing and link-reload APIs are refused in `read` and `dry_run`, and in `commit` unless
  `codeExecution.allowUnsafeApis` is true. Refusals are `CODE_DENIED_API`.
- **Limits.** Timeout 1-45 s (default 30, capped by `codeExecution.timeoutSec`), a loop guard, and output capped by
  `codeExecution.maxOutputKB`.
- **Audit log.** Every run appends a line to `<home>\logs\code-exec-audit.jsonl` (time, client, session, Revit year,
  doc, mode, code and args hashes, token, consent state, duration, outcome, error, changed element counts, warnings);
  the source is kept as `<home>\logs\code\<sha256>.cs`.
- `status` shows whether code execution is enabled; results carry the notice `CODE_EXECUTION_ENABLED` while it is.

## Dialogs

The add-in answers Revit dialogs only when they are a side effect of its own work, and never in a way that can discard
user work:

- **Never answered:** our own dialogs (ids starting `RevitMcpNext_`), a built-in denylist (save, save as, changes not
  saved, not synchronized with central, relinquish, editable elements), the ids in `dialogPolicy.deny`, and any dialog
  whose id, title or text mentions save, sync, delete, discard or relinquish (English and German).
- **While one of our requests runs:** allowlisted dialogs get their fixed answer (continue opening with unresolved
  references, continue without third-party updaters, keep destination types on paste); any other dialog gets its safe
  dismiss answer (Cancel, or OK on an OK-only message). Yes/No-only messages are never answered. The call reports
  `REVIT_DIALOG_ANSWERED`.
- **While Revit is idle for us:** the dialog belongs to the user and is only recorded; calls return
  `REVIT_DIALOG_OPEN`.
- `dialogPolicy.autoRespond: false` turns answering off. `dialogPolicy.extra` can add allowlist answers, never for our
  own ids or denylisted ids.
- **`ui press`** (the model pressing a button): `Cancel` and `Close` apply at once; any other button returns NOT
  APPLIED and needs the user's OK; buttons that could discard work and our own dialogs are refused. The skill tells the
  model to press only Cancel or Close itself and to ask the user about anything else.

## Test-only features

The e2e test ops (`test.sleep_ui`, `test.show_dialog`, `test.enter_command`, `test.inproc_call`) and
`codeExecution.e2ePreapproved` work only in a home other than `%USERPROFILE%\.revit-mcp-next` that also contains
`config\e2e-test-ops.enable`. The installed product never registers them.

## Release boundaries

- Release zips are unsigned unless `scripts\sign-release.ps1` signed them with a real certificate. Do not describe an
  unsigned or dev-signed build as a signed release.
- Run the e2e tests and demos only on disposable models. The harness creates its own projects under
  `<e2e home>\runs\` and refuses writes elsewhere.
- `undo` covers this session's writes only; it is no backup. Keep project files under your normal backup regime.
