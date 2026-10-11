# revit-mcp-next end-to-end tests

Real Revit only: no unit tests and no test doubles (SPEC §13). The harness spawns the broker exactly like Codex does
(the Codex environment whitelist, `REVIT_MCP_NEXT_HOME`, protocol 2025-06-18, client `codex-mcp-client`) and drives it
through MCP. Revit runs with an isolated e2e home, so your own Revit sessions and documents are never visible to it.

The step-by-step checklist for a test PC is [TEST-PC.md](TEST-PC.md).

## Surface check (no Revit, ~10 s, also in CI)

```powershell
npm ci
npm run build
node e2e/run.mjs --profile surface
```

S00 checks the three protocol eras, that `tools/list` equals the emitted catalog byte for byte, the §15 gates, lenient
input, `status` without Revit and stdin EOF. The offline weak-model checks run with it: W1 follows every help `next:`,
INVALID_ARGS and UNKNOWN_OP `fix:` verbatim; W2 looks for copying rituals; W3 checks unit tags and unit parsing; W4
validates all help examples; T1 checks output budgets.

## Full run on a test PC

```powershell
node e2e/run.mjs --years 2024,2027 --launch --profile full
```

`--launch` dev-installs the add-in into the e2e home (with test ops enabled) and starts Revit outside the terminal's
process tree through WMI (Revit started as a child of an app process fails Autodesk licensing). The add-in comes from
`artifacts\` (build it with `--build`, which needs the .NET 10 SDK) or from an extracted release package with
`--artifacts <folder>`. A Revit that cannot get past licensing is reported as `blocked: licensing`.

## Profiles (`e2e/profiles.json`)

| profile | Revit | scenarios | gates |
|---|---|---|---|
| `surface` | no | S00, W1, W2, W3, W4, T1 | hints, t1 |
| `quick` | yes | surface + S01 and one scenario per domain, W3R, R1, R11 | hints, t1 |
| `full` | yes | every scenario incl. reliability (R*), W3R, W4R | coverage, hints, t1 |
| `soak` | yes | S01, K1 (`E2E_SOAK_MIN`, default 30 min per year) | hints, t1 |
| `codex` | optional | S00, C1 (isolated + installed Codex config, V22 TOML syntax; `E2E_CODEX_EXEC=1` for one real-model run) | hints |
| `claude` | optional | S00, C2 (Claude Code registration; `E2E_CLAUDE_P=1` for the claude -p capture check, not approved yet) | hints |

`--only S20,S40` runs single scenarios (profile `lane`, gates hints and t1). `--list` prints profiles and scenarios.

## Gates

- **coverage** (full): every Appendix B key (243) is called successfully or with its declared error (`t.expectError`)
  on every Revit year it supports, and every mode (preview, confirm, detail:full, page, from:r#) is used per year.
  Exceptions go in `e2e/coverage-exemptions.json` with a reason and an expiry date.
- **hints**: every `fix:`, `next:` and `more:` line of every call names an existing tool, op and params and validates
  in the broker (W1); no line makes the model copy add-in ids, NOT APPLIED asks the user and then sends only op +
  confirm, `more:` is a short page token repeated in the summary (W2).
- **t1**: every result stays in the §15 budgets (`e2e/budgets.json`, generated from `contracts/src/catalog/checks.ts`
  by `npm run gen:catalog -- --update-baseline`); per-key p95 output size may grow at most 20 % over
  `e2e/t1-baseline.json` (write it with `--update-t1-baseline`).

## Flags

| flag | meaning |
|---|---|
| `--home <dir>` | e2e home; default `%USERPROFILE%\.revit-mcp-next-e2e` (lanes use `%USERPROFILE%\.revit-mcp-next-dev-<package>`) |
| `--years 2024,2027` | Revit years for Revit scenarios (default: the first installed) |
| `--launch` | dev-install into the home and launch Revit when no harness Revit of this home is running |
| `--artifacts <dir>` | install the add-in from an extracted release package instead of `artifacts\` |
| `--build` | build the broker (and the add-in with `--launch`) first |
| `--keep-revit` / `--restart` | keep the harness Revit running / restart it first |
| `--language DEU` | launch Revit with `/language DEU` (S21 German-UI pass; use with `--restart`) |
| `--profile <name>` / `--only S00,S01` | select scenarios |
| `--update-t1-baseline` | write `e2e/t1-baseline.json` from this run |
| `--no-gates` | report gates without failing the run |
| `--broker <path>` | broker entry (default `broker/dist/src/index.js`) |
| `--json <path>` | also write the summary there |
| `--verbose` | print every call |

Environment: `RMN_E2E_SLOTS` (harness Revit processes machine-wide, default 3), `E2E_REVIT_<year>_EXE`, `E2E_DOTNET`,
`E2E_SOAK_MIN`, `E2E_CODEX_EXEC`, `E2E_CODEX_MODEL` (default gpt-6-sol), `E2E_CLAUDE_P`.

## Output

`artifacts/e2e/<runId>/summary.json` (all years) and `summary-<year>.json` per Revit year, plus
`artifacts/e2e/latest.json`: environment (Revit and add-in versions), every scenario with its steps and calls, the
coverage table, the gates and per-key output statistics. Exit code 0 only when no scenario failed and every gate held.

## Safety rules the harness enforces

- Every document it writes is created under `<home>\runs\<runId>\` (write guard in `t.call`); it never opens your files.
- It kills only the Revit processes it started (`taskkill /PID <pid> /F`), never by name.
- At most `RMN_E2E_SLOTS` harness Revit processes run at once (`%USERPROFILE%\.revit-mcp-next-slots`).
- Test ops exist only in non-default homes with `config\e2e-test-ops.enable`.

## Writing a scenario

Add `e2e/scenarios/<ID>-<slug>.mjs` and list the id in `e2e/profiles.json`:

```js
export default {
  id: "S70", title: "Family parameters", profiles: ["quick", "full"], years: "each",
  async run(t) {
    await t.newProject("A");                                   // disposable project under the run folder
    const r = await t.call("edit_family", { op: "add_param", family: "Single-Flush", name: "E2E_Note", data: "text" });
    t.assertJson(r, (j) => j.write.startsWith("w"));
    await t.expectError("modify_elements", { op: "delete", ids: ["r999"] }, "HANDLE_EXPIRED");
  },
};
```

`t.call(tool, args, {expectError, allowError, timeoutMs})` enforces the 55 s client timeout, parses the text contract
(`status`, `code`, `summary`, `doc`, `notices`, `warnings`, `json`, `more`, `next`, `fix`, `options`, `images`), fails on
`structuredContent`, on an `ERROR` without a `fix:` line and on unexpected errors, and marks coverage per (tool, op)
and mode. `years`: `none` (no Revit), `one` (the first year) or `each`.
