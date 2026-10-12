# End-to-end tests

revit-mcp-next has no unit tests and no test doubles. One harness, `e2e/run.mjs`, starts the broker the way Codex
does, talks MCP to it, and (for most scenarios) drives a real Revit 2024 and/or 2027 with throwaway projects. The
same harness runs from a repo checkout and from an extracted release zip.

- Flags, gates in detail and how to write a scenario: [e2e/README.md](../e2e/README.md).
- Step-by-step checklist for a test PC, what to send back, and the live verification items:
  [e2e/TEST-PC.md](../e2e/TEST-PC.md).

## Running it

From the extracted release folder (normal Windows PowerShell, not elevated, not inside the Codex or Claude app):

```powershell
.\runtime\node.exe e2e\run.mjs --profile surface                            # no Revit, ~10 s
.\runtime\node.exe e2e\run.mjs --years 2024,2027 --launch --profile quick    # ~25 min per year
.\runtime\node.exe e2e\run.mjs --years 2024,2027 --launch --profile full     # ~2 h, unattended
```

From a repo checkout: `npm ci`, `npm run build`, then `node e2e/run.mjs ...`; add `--build` to build the add-in too
(needs the .NET 10 SDK), or `--artifacts <extracted release folder>` to test prebuilt add-ins.

What `--launch` does:

1. dev-installs the add-in into the e2e home `%USERPROFILE%\.revit-mcp-next-e2e` (test ops enabled there only);
2. starts `Revit.exe` with `REVIT_MCP_NEXT_HOME` set, through WMI so it runs outside the terminal's process tree
   (Revit started under an AI app fails Autodesk licensing; the harness then reports `blocked: licensing`);
3. waits for the add-in's registration and `hello` (up to 300 s);
4. creates disposable projects from the English templates under `<e2e home>\runs\<runId>\`.

Your own Revit sessions register in the installed home and stay invisible to the harness. Close Revit before a `full`
run anyway: it is memory-hungry with both years running.

## Profiles

| Profile | Revit | Contents | Gates |
|---|---|---|---|
| `surface` | no | S00, W1, W2, W3, W4, T1 (also in CI) | hints, t1 |
| `quick` | yes | surface + bootstrap, one scenario per domain, W3R, R1, R11 | hints, t1 |
| `full` | yes | every scenario, incl. German UI (S21), run_csharp (S95) and the reliability set | coverage, hints, t1 |
| `soak` | yes | S01 + K1 (`E2E_SOAK_MIN`, default 30 min per year) | hints, t1 |
| `codex` | optional | S00 + C1 (Codex config syntax; one real-model run with `E2E_CODEX_EXEC=1`) | hints |
| `claude` | optional | S00 + C2 (Claude Code registration; `E2E_CLAUDE_P=1` for the large-capture check) | hints |

`--only S20,S40` runs single scenarios; `--list` prints everything.

## Scenarios

| Area | IDs |
|---|---|
| Surface and weak-model checks (offline) | S00 surface, W1 hint following, W2 no copying rituals, W3 units, W4 help examples, T1 output budgets |
| Session and reads | S01 bootstrap, S20 reads, S21 German UI with English names, S89 clashes |
| Model and modify | S30 architecture, S31 MEP and structure, S32 blast radius and confirm, S40 modify |
| Views, sheets, annotation | S50 views, S55 sheets, S60 annotate, S65 schedules |
| Families, documents, files | S70 family, S75 documents, S78 worksharing, S80 links, S85 export, S88 purge, S96 jobs, S97 model delivery |
| Window, capture, control | S90 ui and dialog press policy, S92 capture, S35 change_set, S36 undo, S95 run_csharp, S99 in-process bridge |
| In Revit, from the offline checks | W3R units round trip, W4R every help example through the add-in's validation |
| Reliability | R1 targeting, R3 two Revit years, R4 kill and relaunch, R5 busy UI, R6 idle, R7 dialogs, R8 broker crash, R9 auth, R10 registry junk, R11 protocol eras, R12 edit mode, R13 write ledger |
| Long run and clients | K1 soak, C1 Codex, C2 Claude Code |

## Gates

- **coverage** (`full`): every one of the 243 registry keys is called on every Revit year that supports it, plus the
  modes preview, confirm, `detail:"full"`, paging and `from:"r#"`. Exceptions need a reason and an expiry in
  `e2e/coverage-exemptions.json` (currently empty).
- **hints**: every `fix:`, `next:` and `more:` line of every call names an existing tool, op and params and validates;
  no line makes a model copy add-in ids; NOT APPLIED asks the user and then sends only op + confirm.
- **t1**: every result stays within the token budgets (`e2e/budgets.json`, generated from the catalog) and per-op p95
  output may grow at most 20 % over `e2e/t1-baseline.json`.

## Output

Each run writes `artifacts\e2e\<runId>\summary.json` (all years), `summary-<year>.json` and `artifacts\e2e\latest.json`:
Revit and add-in versions, every scenario with its steps and calls, the coverage table, the gates and per-op output
statistics. The exit code is 0 only when no scenario failed and every gate held. Send the `artifacts\e2e` folder and
the e2e home's `logs\` when reporting a problem; never `config\auth.env`.

## Rules the harness enforces

- Every write must target a document under `<home>\runs\<runId>\`; it never opens your files.
- It kills only the Revit processes it started (by pid), never by name.
- At most `RMN_E2E_SLOTS` (default 3) harness Revit processes run machine-wide (`%USERPROFILE%\.revit-mcp-next-slots`).
- Test ops and `codeExecution.e2ePreapproved` work only in a non-default home with `config\e2e-test-ops.enable`.
- Nobody uses computer use or screen automation on Revit; the only manual step is a person clicking the run_csharp
  consent dialog in the TEST-PC consent pass.

## Before a push to main

`npm run build`, `npm run typecheck`, `node e2e/run.mjs --profile surface`, plus your own scenarios on Revit 2024
(`--only <IDs>` with your lane's home); add-in, IPC and loader changes also on 2027. Surface is a build lint; it never
replaces a Revit run. See [AGENTS.md](../AGENTS.md).
