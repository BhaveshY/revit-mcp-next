# Test-PC checklist (live e2e on Revit 2024 and 2027)

Everything in this repo was built and checked on a machine where Revit cannot start, so every Revit behaviour below
is still unverified. This checklist runs it on a second Windows PC from the release package and lists what to send
back. Allow about half a day: roughly 2 h of unattended `full` runs plus the short manual parts.

Rules: disposable models only (the harness creates its own under `%USERPROFILE%\.revit-mcp-next-e2e\runs\`), never
point it at real project files, never let an AI app drive Revit's UI, and run every command from a normal (not
elevated) Windows PowerShell, never from inside the Codex or Claude app.

## 0. Prerequisites

- [ ] Windows 10/11 x64, 32 GB RAM recommended (both Revit versions run at the same time during `full`).
- [ ] Revit 2024 and Revit 2027 installed under `C:\Program Files\Autodesk\` (else set `E2E_REVIT_2024_EXE` /
      `E2E_REVIT_2027_EXE`), with the English templates in `C:\ProgramData\Autodesk\RVT <year>\Templates\English\`.
- [ ] Each Revit started once by hand on this Windows user, signed in to Autodesk, then closed. The harness cannot pass
      licensing and reports `blocked: licensing` instead of retrying.
- [ ] Close every Revit before starting (the harness only kills the Revit processes it started itself).
- [ ] Optional: Codex CLI 0.153+ and/or 0.159 (`codex --version`) and Claude Code (`claude --version`) for steps 5-6.
- [ ] The release zip `revit-mcp-next-<version>-win-x64.zip` (from `scripts\package.ps1`) and its `.sha256`.

## 1. Unpack and install

```powershell
Get-FileHash .\revit-mcp-next-<version>-win-x64.zip -Algorithm SHA256   # compare with the .sha256 file
Expand-Archive .\revit-mcp-next-<version>-win-x64.zip -DestinationPath C:\rmn-test
cd C:\rmn-test\revit-mcp-next-<version>-win-x64
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1 -DryRun     # read what it would change
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1 -RevitYears 2024,2027 -TrustRevitAlwaysLoad
& "$env:USERPROFILE\.revit-mcp-next\runtime\node.exe" "$env:USERPROFILE\.revit-mcp-next\broker\revit-mcp-cli.mjs" doctor
```

- [ ] The installer summary lists the home, both manifests, Codex `[mcp_servers.revit]`, the Claude registration and the
      AGENTS.md edit (or say which client was not found).
- [ ] `doctor` passes (keep its output).
- [ ] Start Revit 2024 by hand, open any sample model: no "unsigned add-in" prompt (V16, -TrustRevitAlwaysLoad), then in
      Codex or Claude ask "use the revit MCP: call status". Close Revit. Repeat once with Revit 2027.

All commands below run from the unpacked folder with its own Node: `.\runtime\node.exe e2e\run.mjs ...`. Each run
writes `artifacts\e2e\<runId>\summary.json` (all years) and `summary-<year>.json` and prints the gates at the end.

## 2. Surface (no Revit, ~10 s)

```powershell
.\runtime\node.exe e2e\run.mjs --profile surface
```

- [ ] `6 passed, 0 failed`, gates hints and t1 ok. If this fails, stop and send the summary: the package is broken.

## 3. Full run on both years (~2 h, unattended)

```powershell
.\runtime\node.exe e2e\run.mjs --years 2024,2027 --launch --profile full
```

The harness dev-installs the package's add-in into `%USERPROFILE%\.revit-mcp-next-e2e` (test ops enabled), starts
both Revit versions through WMI outside the terminal's process tree, runs every scenario on each year, closes the
Revit it started and evaluates three gates: coverage (243 keys + 5 modes per year), hints (W1/W2 on every call) and
t1 (output budgets). Leave the PC alone while it runs (R7 and R12 put dialogs and commands on screen; S90 activates
views; do not click into Revit). If Revit hangs on a dialog at startup, the run reports the TaskDialog id; dismiss it
by hand once and rerun with `--keep-revit`.

- [ ] Note the final line (`N passed, N failed, N skipped/blocked; gates ...`).
- [ ] If something failed, rerun just that scenario once to tell flakiness from a defect, e.g.
      `.\runtime\node.exe e2e\run.mjs --years 2027 --launch --only S40 --verbose`.

Note: the dev install rewrites `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin` to a loader in the default
home (same build). Rerun the installer afterwards if you want the installer's own manifest back.

## 4. Language, consent and soak (short manual parts)

```powershell
# German UI pass of S21 (English names on a DEU Revit) and R7 dialogs on DEU (V17), both years:
.\runtime\node.exe e2e\run.mjs --years 2024,2027 --launch --restart --language DEU --only S21,R7
# run_csharp consent dialog (V13; a person must click "Allow for this Revit session" when the dialog appears):
$env:E2E_CONSENT_PROMPT = "1"; .\runtime\node.exe e2e\run.mjs --years 2024 --launch --restart --only S95; Remove-Item Env:E2E_CONSENT_PROMPT
# optional soak, 30 min per year:
$env:E2E_SOAK_MIN = "30"; .\runtime\node.exe e2e\run.mjs --years 2024,2027 --launch --restart --profile soak
```

- [ ] S21 on DEU logs `UI language DEU` and passes; send `%USERPROFILE%\.revit-mcp-next-e2e\logs\aliases-<year>-*.json`
      from the English run (step 3) too: they become the generated alias tables.
- [ ] The consent dialog appeared, `ui dialogs` listed it and `ui press` was refused (S95 consent step passes).

## 5. Codex (installed and isolated config; optional real-model run)

```powershell
.\runtime\node.exe e2e\run.mjs --profile codex                       # no model quota
# real-model run: first leave a harness Revit with a project open, then run C1 with E2E_CODEX_EXEC
.\runtime\node.exe e2e\run.mjs --years 2024 --launch --keep-revit --only S01
$env:E2E_CODEX_EXEC = "1"; .\runtime\node.exe e2e\run.mjs --profile codex; Remove-Item Env:E2E_CODEX_EXEC
```

The real-model run uses your Codex quota once (model `gpt-6-sol`, reasoning medium; set `E2E_CODEX_MODEL` to change).
The kept Revit from S01 provides the ~40 KB `detail:"full"` result; close that Revit window by hand afterwards. C1
measures whether Codex shortens tool output (V22) and saves the raw events as `codex-exec-events.jsonl`. Repeat both
with Codex 0.159 if you have it.

- [ ] C1 passes: the isolated block, the V22 TOML syntax and the installed `[mcp_servers.revit]` (node + revit-mcp.mjs,
      REVIT_MCP_NEXT_HOME, startup 30 s, tool 120 s, no legacy `revit-mcp-next` block).
- [ ] With E2E_CODEX_EXEC: the logged report shows `shortened:false` for every call (else note the sizes).
- [ ] In the Codex app: `status`, `help`, `set_target` and `ui` run without an approval prompt; a write such as
      `create_elements` follows your approval policy.

## 6. Claude Code

```powershell
.\runtime\node.exe e2e\run.mjs --profile claude
```

- [ ] C2 passes (`claude mcp get revit` is connected with node + revit-mcp.mjs).
- [ ] The `claude -p` large-capture check is not approved and is skipped; do not set `E2E_CLAUDE_P`.

## 7. What to send back

Zip these (no file under `config\` holds anything you need to send, and never send `config\auth.env`):

```powershell
$out = "$env:USERPROFILE\Desktop\rmn-e2e-results.zip"
$e2e = "$env:USERPROFILE\.revit-mcp-next-e2e"
$items = @(
  ".\artifacts\e2e",                                   # every summary.json / summary-<year>.json / codex-exec-events.jsonl
  "$e2e\logs",                                         # addin-<year>-*.jsonl, broker-*.jsonl, loader-*.log, aliases-*.json, code-exec-audit.jsonl
  "$env:USERPROFILE\.revit-mcp-next\logs"              # the installed home's logs (install-*.log, doctor)
) | Where-Object { Test-Path $_ }
Compress-Archive -Path $items -DestinationPath $out -Force
```

Also send: the installer summary and `doctor` output (step 1), the final lines of every run, the Revit versions
(Help > About, or `summary.json` > `environment.revit`), and for any failed scenario the newest Revit journal from
`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <year>\Journals\`. Note anything you saw on screen that the logs cannot
show (a dialog that stayed open, Revit flashing to the front, a slow wake-up).

## 8. Live verification items (what each item is and where its evidence is)

The full run covers these; tick them from `summary-<year>.json` (scenario steps, `logs`, `calls`) and the add-in logs.
Items marked *manual* need a look at the screen or a second run.

### Reliability (R-scenarios)
- [ ] R1 targeting: a fresh session's auto pin never picks a family doc; a user tab switch makes the next write return
      TARGET_CHANGED and the identical repeat succeed; ACTIVE_DIFFERS about 1 s after another session's switch; after
      save_as Tower→Tower2 the pin follows.
- [ ] R3 (runs in the 2027 iteration): `@2024` / `@2027` pick the right Revit for new_project with no document open.
- [ ] R4: the error after the kill arrives in ≤ 3 s; the first call after the relaunch succeeds with REBOUND in ≤ 2 s;
      the slot file follows the new pid.
- [ ] R5 busy: REVIT_BUSY within 10 s; 24 reads within 12 s; export/family-reload variants give REVIT_BUSY or ok from a
      second session, never REVIT_DIALOG_OPEN or a hang; cancel_request before answering.
- [ ] R6 / V18: dev.ping p50/p95 normal, minimized (wake.wmNull on) and the wmNull-off A/B (logged); calls after a 60 s
      idle (`E2E_R6_IDLE_S` changes it); `health.pump.execViaIdling` grows when the ExternalEvent is silent; ExternalEvent
      re-created after three Denied/TimedOut raises (`health.pump.recreates`).
- [ ] R7 / V17 / V20 dialogs, EN (step 3) and DE (step 4): UIA lists TaskDialog DirectUI buttons and command links with
      the right ids (1/2/6/7/8, 1001+); which press path won (uia, tdm, wm_command, bm_click: `evt dialogs` in
      `addin-<year>-*.jsonl`); `health.lastDialog.dialogId` equals the TaskDialog.Id of test.show_dialog; tier A answers
      an unknown dialog during our request with REVIT_DIALOG_ANSWERED, tier B only allowlisted ids. *Manual*: check the
      built-in allowlist ids against real dialogs (TaskDialog_Unresolved_References → 1002,
      TaskDialog_Missing_Third_Party_Updater(s) → 1001, Dialog_Revit_PasteSimilarSymbolsPaste → 1) and the denylist.
      The unresolved-references variant of R7 is not written yet.
- [ ] R8: a new broker session after a crash recovers; old tokens/ids fail with actionable errors; no orphan processes.
- [ ] R9: on a rotated auth.env the add-in re-reads it (success) or answers AUTH_MISMATCH with a fix; which one happened.
- [ ] R10: after the registration file is deleted, the heartbeat rewrites it within 35 s; junk files are ignored.
- [ ] R12: Revit raises neither Idling nor ExternalEvents while in the Wall command (else the edit-mode rule never
      fires); test.enter_command's two VK_ESCAPE posts really end the command.
- [ ] R13: RECOVERED_WRITE on one of the first results of a new broker (E11); the ledger notice says "running" for an
      accepted unfinished write; read_many stays ≤ 55 s with BUDGET vs REVIT_BUSY counts (E15).

### Model, modify, views, sheets, annotation
- [ ] S30 V7: a 30° footprint roof edge shows Slope 30° in Revit; a 3° floor slope matches. *Manual*: look at the S30
      model in `runs\<runId>\<year>\S30.rvt` (open a copy).
- [ ] S30 V8/V8b: one undo removes stairs, runs, landing and railings; preview leaves nothing; straight and L paths reach
      the top level without an "actual risers differ" warning.
- [ ] S30 V9: curtain grid vertical and horizontal lines come out right on both years.
- [ ] S30 place_family: auto wall host within 2 m; sill via INSTANCE_SILL_HEIGHT_PARAM; floor/ceiling/roof-hosted families
      (FAMILY_HOSTING_BEHAVIOR 1-4 are assumed values).
- [ ] S30 roof_extrusion extends depth mm to the left of start→end.
- [ ] S31: a refused elbow gives warn FITTING_FAILED and the call still commits; 2027 mep.zone creates a space-based
      GenericZone (and a "Space Zone" type when none exists).
- [ ] S40 split: the wall copy keeps its doors, door count unchanged, each half hosts one door; a door spanning the split
      point gives UNSUPPORTED_OP without false positives from swing/trim; copy to levels keeps base/top offsets.
- [ ] S40 attach on 2027 (Wall.AddAttachment) and UNSUPPORTED_VERSION on 2024; cut/uncut floor+wall (ok, or Revit's
      refusal as UNSUPPORTED_OP); *manual*: ColumnAttachment on structural columns, align lock:true on family instances,
      set_layers compound structures, place_group with a level, set_parameters on a template-controlled view
      (TEMPLATE_CONTROLLED, fix writes on the template).
- [ ] S32 blast flows: delete 25 → NOT APPLIED → confirm; set_parameters 250 → bulk NOT APPLIED → confirm; delete preview
      is a dry run.
- [ ] S50 V5: section and elevation look=[0,1], far_clip 5000; a view template controls the scale by default; filter
      rules ('Comments contains E2E', 'Width >= 300', has value/no value) on both years; CreateCallout accepts the
      parent's own ViewFamilyType.
- [ ] S55: duplicate with_views restores viewport type, rotation and label; copied views renamed with the prefix.
- [ ] S60: annotate.dimension mode faces passes; *manual*: modes centers/core (else PARTIAL_RESULT fallback), spot on a
      floor in a plan, 1 room tag in a default plan.
- [ ] S65: id column and total=3, ids still map after the descending re-sort; RefreshData in a read never throws;
      category= field probing leaves no residue (no generation bump); set_filters on lengths (Width >= 10).

### Families, documents, worksharing, links, export, delivery
- [ ] S70 V6: famDoc.LoadFamily(project) works with the WriteScope TransactionGroup open and the group rollback reverts
      the reload (if not: set ChangeEngine.FamilySegmentsLast = true and report). V6b: Type Comments survives an
      overwrite:false add_param reload. set_category on the open family doc is "unchanged".
- [ ] S70: TransactionGroup.Assimilate on an empty group returns Committed (family= reload:false, load_into); EditFamily
      of a family already open in the editor gives REVIT_REFUSED naming the doc (message text on both years); the
      'Other' parameter group (empty ForgeTypeId) is accepted; read_family path= reuses an open .rfa and an old .rfa
      upgrades without a dialog.
- [ ] S75 V26: closing the active doc runs as two job steps; V10: closing a non-active family doc with open views;
      V4: activate an already open file; BasePoint parameters writable (true_north 10).
- [ ] S78 V19: BasicFileInfo central detection; the opened doc is a local, its path differs from the central and the
      central's mtime is unchanged before sync; V15 borrow/NotOwned previews; SetActiveWorksetId and RenameWorkset in
      a transaction; V11: sync and reload_latest clear the undo history and `undo` then refuses cleanly (no
      UNDO_UNCONFIRMED).
- [ ] S80: AcquireCoordinates in an open transaction; link_ifc: OpenIFCDocument + RevitLinkType.CreateFromIFC writes
      `<file>.ifc.RVT` (or Revit's refusal is reported); CAD unload gives UNSUPPORTED_OP with a fix naming links remove.
- [ ] S85/S96: PDF of non-sheet views with PaperFormat Default; ExportImage naming; IFC export inside the rolled-back
      temp scope leaves no residue (logged get_changes); gbXML on 2027 with default options; multi-file export as a job.
- [ ] S97: model_delivery end to end (fixture, inspect, preview token, execute job publishes, source sha unchanged); a
      preview that outlives the call budget returns its token through job_status.

### Reads, capture, UI, control
- [ ] S20: every list kind under 8 KB; find totals, `Length >= 10000`, stable handle pages, group_by, from=handle;
      describe writable/all/names, geometry, relations (wall hosts door); check_model; get_quantities wall length 40 m;
      get_changes session/mark/last; read_many lenient items. *Manual on a large model*: purgeable never gives
      READ_STILL_RUNNING unexpectedly; 'Comments empty' matches new walls; room area ≈ 92 m²; for_element marks the
      current wall type.
- [ ] S89: exactly one pair for overlapping floors, depth = floor thickness, volume 3 x 2 m x thickness, tolerance,
      a=r# handle, linked model (b ids are link ids, point in host coordinates).
- [ ] S92 V1/V2/V3/V14: ExportImage with only the TempScope group open (else the log says "trying fallback B" and undo
      still steps over the temp capture steps); crop/sheet pixel mapping (aspect within 1 %); background docs and
      never-opened 3D views (NEEDS_ACTIVE_DOC otherwise); *manual*: a capture while Revit is minimized or over RDP
      (all-black → CAPTURE_GPU_UNAVAILABLE); DPI_150 line weights at 768/1280/1568 px; cached:true on a repeat.
- [ ] S90 V4: activate_doc and activate_view of a background doc bring it to the front.
- [ ] S35: change_set $0/$prev refs, one undo step, aggregate NOT APPLIED for manage_document.coordinates (its confirm no
      longer goes CONFIRM_STALE after earlier edits).
- [ ] S36 V25: PostCommand Undo/Redo from the ExternalEvent with DocumentChanged(TransactionUndone) confirmation; whether
      an assimilated group reports one undone event or one per inner transaction (log); compensate on a fresh Revit (the
      UndoLedger now attaches at startup); no phantom steps after NOT APPLIED writes.
- [ ] S95 V13: Roslyn loads on 2024 and 2027, also with pyRevit/Dynamo installed; Assembly.Load(il, pdb) resolves
      RevitAPI and runtime errors carry user line numbers; the audit line is in `logs\code-exec-audit.jsonl`.
- [ ] S99: in-process reads work; destructive/lifecycle/code keys are refused.
- [ ] W3R: units m/ft and unit strings land in mm (level 3500 mm, wall 3048 mm, move 1000 mm).
- [ ] W4R: no help example fails the add-in's validation (the log line lists the error codes per example).
- [ ] Coverage gate: `gates.coverage.perYear` shows 0 missing on both years; any missing key is a scenario that did not
      reach it (send the summary; do not edit coverage-exemptions.json on the test PC).

## 9. Troubleshooting

| symptom | what to do |
|---|---|
| `blocked: licensing` | start that Revit by hand once, sign in, close it, rerun |
| startup dialog id in the error (e.g. `TaskDialog_Security_Unsigned_File_Loading`) | rerun the installer with `-TrustRevitAlwaysLoad`, or choose "Always Load" once by hand |
| `all 3 e2e Revit slots are in use` | a previous run kept Revit warm: rerun with `--restart`, or close those Revit windows (only harness ones) |
| `the add-in for Revit <y> is not built` | run from the unpacked release folder (it has `artifacts\addin\<year>`), or pass `--artifacts <folder>` |
| a scenario times out at 55 s | rerun it alone with `--verbose`; send the add-in log of that minute |
| `dev-install failed` mentioning the home location | keep the default e2e home under `%USERPROFILE%` (not under AppData or Temp) |
