# revit-mcp-next end-to-end tests

Real Revit only: no unit tests and no test doubles (SPEC §13). The harness spawns the broker exactly like Codex does
(the Codex environment whitelist, `REVIT_MCP_NEXT_HOME`, protocol 2025-06-18, client `codex-mcp-client`) and drives it
through MCP. Revit runs with an isolated e2e home, so your own Revit sessions and documents are never visible to it.

## Surface check (no Revit, ~5 s, also in CI)

```powershell
npm ci
npm run build
node e2e/run.mjs --profile surface
```

S00 checks the three protocol eras (2025-06-18, 2025-11-25, 2026-07-28), that `tools/list` equals the emitted catalog
byte for byte (77,661 B), the §15 gates, lenient input, `status` without Revit, the core/opt-in profiles and that the
broker exits within 2 s of stdin EOF with nothing but JSON-RPC on stdout.

## Bootstrap with Revit (S01) on a test PC — one command

Prerequisites on that PC (all paths are detected; nothing is tied to one user or machine):

- Windows 10/11, Node.js 24+, git, and the .NET SDK 10 (`winget install Microsoft.DotNet.SDK.10`).
- Revit 2024 and/or 2027 installed under Program Files (or set `E2E_REVIT_2024_EXE` / `E2E_REVIT_2027_EXE`).
- Each Revit started once by hand on this Windows user and signed in to Autodesk (the harness cannot pass licensing).
- An unlocked interactive desktop session; Revit and the terminal non-elevated.

Then, from the repo root:

```powershell
npm ci
node e2e/run.mjs --build --years 2024,2027 --launch --only S00,S01
```

`--build` runs `npm run build` and `scripts/build-addin.ps1 -RevitYear <year>` (set `E2E_DOTNET` to a `dotnet.exe` if it is
not on PATH), `--launch` dev-installs the build into the e2e home and starts Revit outside the terminal's process tree
through WMI (Revit started as a child of an app process fails Autodesk licensing). Drop `--build` when the add-in is
already built, add `--keep-revit` to keep Revit warm for the next run, and `--restart` after rebuilding the add-in.

The run writes `artifacts/e2e/<runId>/summary.json` (and `artifacts/e2e/latest.json`) and exits 0 only when no scenario
failed. A Revit that cannot get past licensing is reported as `blocked: licensing` (journal `Adlsdk Error`, or stuck at
`manage licensing` for more than 120 s); the harness then kills only the Revit it started and does not retry.

## Flags

| flag | meaning |
|---|---|
| `--home <dir>` | e2e home; default `%USERPROFILE%\.revit-mcp-next-e2e` (lanes use `%USERPROFILE%\.revit-mcp-next-dev-<package>`) |
| `--years 2024,2027` | Revit years for Revit scenarios (default: the first installed) |
| `--launch` | dev-install into the home and launch Revit when no harness Revit of this home is running |
| `--keep-revit` / `--restart` | keep the harness Revit running / restart it first |
| `--build` | build the broker (and the add-in with `--launch`) first |
| `--only S00,S01` | run these scenario ids |
| `--profile surface\|lane\|quick\|full` | select scenarios by profile (default `surface`, or `lane` with `--only`) |
| `--broker <path>` | broker entry (default `broker/dist/src/index.js`) |
| `--json <path>` | also write the summary there |
| `--verbose` | print every call |

Environment: `RMN_E2E_SLOTS` (harness Revit processes machine-wide, default 3), `E2E_REVIT_<year>_EXE`, `E2E_DOTNET`.

## Safety rules the harness enforces

- Every document it writes is created under `<home>\runs\<runId>\` (write guard in `t.call`); it never opens your files.
- It kills only the Revit processes it started (`taskkill /PID <pid> /F`), never by name.
- At most `RMN_E2E_SLOTS` harness Revit processes run at once (`%USERPROFILE%\.revit-mcp-next-slots`).

## Writing a scenario

Add `e2e/scenarios/<ID>-<slug>.mjs` (lanes own their files; `run.mjs` and `lib/` are frozen in wave 2):

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
`structuredContent`, on an `ERROR` without a `fix:` line and on unexpected errors, and marks coverage per (tool, op).
