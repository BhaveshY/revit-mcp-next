# Production Readiness Audit

This project is shareable as an unsigned Revit 2024 external preview when labeled clearly. It is not a signed enterprise production release. The current repository state supports local development, staged Windows packaging, install diagnostics, `revitctl` bridge debugging, redacted support bundle collection, and release evidence bundle generation.

Use this audit to separate evidence that already exists from blockers that still need release work.

See [fork-parity.md](fork-parity.md) for the current old-fork capability comparison and the remaining parity backlog.

## Current Evidence

- CI builds and tests the broker/contracts workspaces on Windows with Node 24.
- CI runs `node scripts/validate-repo.mjs`.
- CI builds the .NET bridge contracts.
- CI attempts the Revit add-in build only when Revit 2024 API DLLs are present on the runner.
- `npm run package:windows:dry-run` validates package inputs after the broker/contracts/add-in build outputs exist.
- `npm run package:windows` stages a Windows package with `release-manifest.json` and `CHECKSUMS.sha256`; `-Sign` can request Authenticode signing before manifest, checksum, and zip capture when a certificate is supplied.
- `npm run test:release:windows` runs in hosted CI with synthetic add-in DLL placeholders. It validates unsigned package creation, zip creation, package install into temp profile paths, doctor output, support bundle redaction, and checksum-tamper rejection without requiring Revit API DLLs on the runner.
- `npm run evidence:host-integrations` validates raw pyRevit/Dynamo host-smoke JSON and writes release-ready `host-integrations-summary.json`.
- `npm run smoke:pyrevit-host` runs the packaged pyRevit Host Smoke command through `pyrevit run`, optionally seeds pyRevit's per-user Revit build cache, and rejects failed raw pyRevit evidence.
- `npm run smoke:dynamo-host` prepares a Dynamo-for-Revit host-smoke run, launches Revit with evidence environment variables when requested, waits for the packaged Dynamo graph output, and rejects failed raw Dynamo evidence. This is a Dynamo-for-Revit evidence path, not a headless DynamoCLI substitute.
- `npm run smoke:host-integrations` runs the pyRevit host smoke, runs or validates the Dynamo-for-Revit host smoke, composes `host-integrations-summary.json`, and leaves command logs that can be included in release evidence.
- `npm run evidence:release:windows` creates a release evidence bundle for one staged package. It records package metadata, package zip SHA-256, signing status, named validation logs, support-bundle evidence, passed live-smoke summary evidence, hosted pyRevit/Dynamo evidence, explicit skip reasons, and an inventory of copied evidence files.
- `npm run test:evidence:release:windows` runs in hosted CI with synthetic add-in DLL placeholders. It validates release evidence generation, explicit missing-evidence gates, failed live-smoke and hosted-integration summary rejection, package hash capture, copied package metadata, support/live-smoke/hosted evidence capture, validation log capture, and token redaction.
- `npm run test:integrations:python` syntax-checks pyRevit/Dynamo status, preview/apply examples, and host-smoke examples, and validates the shared stdlib Python MCP client against a fake stdio MCP server.
- `npm run doctor:windows` validates the installed MCP launcher, installed `revitctl` launcher, staged broker files, add-in DLLs, Revit manifest assembly path and stable add-in identity, packaged production dependencies, local pipe auth token shape, staged pyRevit/Dynamo examples, and add-in DLL signature status.
- `npm run doctor:clients` validates generated Claude/Codex client config snippets, existing user config entries when present, stale install roots, launcher quoting, raw token leakage risk, Revit 2024-only client discovery including `revitctlPath`, and MCP startup plus `tools/list` without requiring a Revit connection.
- `npm run support:bundle` collects doctor output, install metadata, logs, file hashes, staged integration example hashes, and redacted auth configuration.
- Query-style read endpoints now use page-plus-one enumeration when `includeTotalCount` is false, so low-limit agent reads avoid full match materialization while still returning `truncated` and `cursor`. The MCP broker returns opaque signed continuation cursors bound to the same tool arguments/session/document state, rejects raw, malformed, tampered, wrong-tool, and mismatched-argument cursors before the add-in can replay page 1, keeps long cursor tokens out of short text hints, and rejects unknown query filter keys before they can broaden a model query.
- Core read tools now advertise typed MCP output schemas for status, documents, levels, current view, paged views/sheets/elements, model statistics/readiness, material quantities, rooms, catalogs, generic queries, and parameter discovery. The schemas expose page fields such as `returnedCount`, `truncated`, `cursor`, `items`, `fields`, and `units` while remaining passthrough-safe for live Revit metadata.
- Core write-control tools now advertise typed MCP output schemas for preview/apply/cancel responses, including preview tokens, change-set hash/generation/expiry fields, risk and itemized change rows, applied counts, and no-op cancellation status.
- The MCP broker now exposes `revit://discovery`, `revit://tools/{name}`, `revit.start_workflow`, and `revit.workflow` so Claude/Codex-style clients can discover compact workflow guidance without dumping the full tool schema into context.
- `revit.describe_parameters` now defaults to compact `preset: "writableEdit"` output for writable instance parameter edits. `preset: "namesOnly"` and `preset: "full"` preserve broader discovery and legacy read-only/type/value detail when explicitly requested.
- `delete_element` preview now rollback-probes Revit's actual delete set, reports dependent deleted IDs, and blocks collateral deletes unless the caller explicitly opts in or echoes the exact expected deleted element IDs/count.
- `revit.apply_change_set` now requires preview-returned `previewId`, `baseGeneration`, `changeSetHash`, and `expiresAt`, validates token metadata before recomputing preview details, and consumes preview tokens before write execution so apply attempts are single-use.
- `revit.catalog` provides compact, paginated discovery for element types, family symbols, title blocks, view family types, text note types, dimension types, and tag types, including room tag types, independent tag symbols, and Revit-compatible type IDs for a target element.
- Read parity with the fork now covers compact view/sheet inventory, current-view metadata, current-view elements, selected elements, parameter discovery, model statistics, material quantities, and room export data through `revit.get_views`, `revit.get_sheets`, `revit.get_current_view`, `revit.get_current_view_elements`, `revit.get_selection`, `revit.describe_parameters`, `revit.analyze_model`, `revit.get_material_quantities`, and `revit.get_rooms`.
- Room support is implemented and covered by the live smoke workflow: `revit.get_rooms` returns compact, paginated room data, and guarded `create_room` preview/apply places a room by level and 2D location with optional name, number, department, and duplicate-number override.
- `npm run smoke:revit` runs a live MCP smoke through the installed launcher against the active Revit project. It checks `revit.status`, `revit.get_views`, `revit.get_sheets`, `revit.get_current_view`, `revit.get_current_view_elements`, `revit.get_selection`, `revit.analyze_model`, `revit.get_model_readiness`, `revit.get_material_quantities`, `revit.get_rooms`, `revit.get_levels`, `revit.catalog`, `revit.query`, `revit.describe_parameters`, plus preview/apply flows for `create_level`, `create_grid`, `create_floor`, `create_wall`, optional `place_family_instance`, `create_room`, `create_sheet`, optional `place_view_on_sheet`, optional `create_text_note`, optional `tag_room`, optional `tag_element`, `set_parameter`, `move_element`, `rotate_element`, `copy_element`, `set_element_pinned`, and guarded `delete_element` cleanup of the copied smoke wall. The room section creates a closed room-bounding wall loop, places a room, verifies positive area, and reads it back through `revit.get_rooms`. Documentation smoke creates a sheet and records bounded skip reasons for view placement, text notes, and tags when the disposable model lacks a suitable view, tag family, room, or visible wall. The placement section uses `revit.catalog kind=familySymbols preset=placement` and either applies a supported door/window/furniture/equipment/fixture placement or records a bounded skip reason when the disposable model has no suitable symbols/hosts. When an alternate compatible wall type exists, it also applies `change_element_type`; release-candidate runs can require this with `-RequireTypeChange`. It can assert the expected Revit major year with `-ExpectedRevitYear` and write `smoke-summary.json` with pass/fail, document, Revit version, tool, and operation evidence through `-SummaryPath`.
- `npm run smoke:release-local` orchestrates a local disposable-machine release smoke: build, install into a stable per-year root under `%APPDATA%\Autodesk\Revit\Addins`, copy a sample RVT, launch Revit when needed, wait for `revit.status` readiness, run doctor, run live smoke, close and relaunch the Revit process it launched for a second status-only no-prompt probe, collect support bundle, and collect release evidence into a short local work root. Evidence and package work directories default to `C:\tmp\revit-mcp-next-smoke` when writable, otherwise a short sibling directory beside the repo, to avoid Windows path-length failures in packaged `node_modules`.
- Unsigned local smoke builds can pause Revit on `Security - Unsigned Add-in` / `Sicherheit - Zusatzmodul ohne Signatur`. The application manifest uses Revit's application `ClientId`, but the reliable no-prompt path is trusted Authenticode signing. The local smoke path creates/trusts a CurrentUser dev code-signing certificate for disposable machines, signs the package before checksums, verifies trusted signatures before launch, and writes `second-startup-readiness.log` when it can prove a second Revit startup reaches `revit.status` without the load prompt blocking the add-in. Production releases must still use a real release certificate so end users do not see any unsigned-add-in prompt.
- Packaged installs include pyRevit and Dynamo status, preview/apply write examples, workflow examples, and host-smoke evidence examples, along with external-stdio and in-process Python helpers under `integrations`. The installer writes token-safe `config\client-discovery.json` for clients.
- `npm run sign:windows` provides optional Authenticode signing and verification for `.dll` and `.ps1` package targets. No release certificate is assumed by this repository.
- `npm run dev-cert:windows -- -StatusOnly` audits the CurrentUser local dev signing certificate state, and `npm run dev-cert:windows -- -Remove` removes this repository's local dev certificate entries from CurrentUser `My`, `Root`, and `TrustedPublisher` after disposable-machine smoke testing. `npm run revit:trust -- -StatusOnly` audits Revit's per-user `Always Load` registry entry for the add-in `ClientId`.
- `.github/workflows/live-revit-smoke.yml` defines a manual self-hosted Windows/Revit smoke workflow. It can package unsigned, local-dev-signed, or release-cert-signed candidates, installs from that package, can optionally launch Revit, exports the add-in auth config for launched Revit sessions, runs doctor and live smoke with expected-year and `smoke-summary.json` evidence, collects a support bundle, fails if release-evidence collection fails, and uploads smoke/package/evidence artifacts.
- Packaging and install currently reject `-RevitYears` other than `2024` so release candidates cannot silently point Revit 2025/2026 at the Revit 2024/net48 add-in.

## Remaining Blockers

- Optional signed artifacts from an available release certificate plus archived verification evidence before any signed-release claim. Unsigned preview releases are acceptable when explicitly labeled as unsigned preview artifacts.
- Successful release-candidate live smoke evidence from a self-hosted Revit runner.
- End-to-end live validation evidence that ties installer behavior, broker/add-in pipe auth, `revit.status`, read tools, and preview/apply write flows to a specific packaged build. Hosted package-contract CI covers package mechanics only; it does not prove Revit can load a release DLL.
- Successful release-candidate pyRevit and Dynamo host-smoke evidence from the installed package, summarized in `host-integrations-summary.json`. Dynamo evidence should come from the packaged `integrations\dynamo\revit_mcp_next_host_smoke.dyn` graph running inside Dynamo for Revit.
- pyRevit CLI evidence can be collected with `npm run smoke:pyrevit-host`. If pyRevit's build table is stale, pass `-SeedHostsCache -Builds <model-build>,<installed-revit-build>` to seed the per-user cache before collecting evidence.
- More real-model write-operation and failure-mode evidence before calling the mutation surface production-complete.
- Multi-version Revit compatibility implementation and validation beyond the current Revit 2024 target. Revit 2025/2026 are blocked intentionally until year-specific .NET 8 add-in artifacts are built, packaged, installed, and smoked.
- Archived release evidence bundle for each release candidate, generated from that exact package, signing state, validation logs, support bundle, live-smoke output, and hosted integration output.

## Release Evidence Gate

Release evidence generation exists, but a production release still needs evidence from a real candidate build. Do not describe artifacts as signed unless a certificate was configured for that build and signature verification output was archived.

Acceptance criteria for this slice:

- Packaging can optionally apply Authenticode signatures before final manifest, checksum, and zip capture when a release certificate is supplied.
- Unsigned local or dry-run packages remain clearly labeled as unsigned staged artifacts.
- Signing happens before final checksum and zip evidence is captured, or the release process regenerates those files after signing.
- Release evidence captures package metadata, checksums, signing status, validation output, install diagnostics, support bundle output, and live-smoke output for one build.
- Live-smoke evidence includes `smoke-summary.json` with `status: "passed"`; failed or missing summaries are rejected by the evidence collector.
- Hosted pyRevit/Dynamo evidence includes `host-integrations-summary.json` with `status: "passed"` and passed `pyrevit` and `dynamo` host entries; failed or missing summaries are rejected by the evidence collector unless an explicit skip reason is supplied. When hosted evidence is captured, release evidence also verifies each host's loaded `RevitMcpNext.Addin.dll` SHA-256 against the package manifest.
- A manual self-hosted Revit live-smoke workflow is run for release candidates and its artifacts are archived with the release evidence.

Recommended release evidence:

- `release-manifest.json`, including package version, commit, dirty status, Revit years, and file inventory.
- `CHECKSUMS.sha256` plus a separately recorded SHA-256 hash of the package `.zip`.
- Command logs for `node scripts\validate-repo.mjs`, packaging, install, doctor, support bundle, and live smoke.
- `smoke-summary.json` from the exact live smoke, including expected and actual Revit version/year, active document fingerprint, covered tools, and covered operations.
- `host-integrations-summary.json` from the exact installed package, including passed pyRevit and Dynamo host-smoke entries and links to each host evidence file.
- Authenticode signer identity, certificate thumbprint, timestamp information, and verification output when signing is enabled.
- A clear skip reason for any unavailable evidence, such as no signing certificate, no Revit host, or no hosted pyRevit/Dynamo smoke.

## Live Smoke Documentation

Run the local live smoke after installing the staged build, opening Revit, loading the add-in, and activating a disposable project document:

```powershell
npm run smoke:revit
```

The smoke mutates the active project. It creates a level, grid, floor, straight wall, room-bounding wall loop, room, and sheet; attempts supported family placement, view placement, text note creation, room tagging, and wall tagging when the model has suitable symbols, hosts, views, and tag types; reads the room and sheet back; sets a writable wall instance parameter; optionally changes the wall type; moves the wall on the Y axis; verifies the reported movement; rotates the wall; copies it by an offset; then pins and unpins the copied wall. It also checks blocked duplicate-grid and pinned-move previews plus a rejected apply with a mismatched `changeSetHash`.

Useful direct wrapper form:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\live-smoke-revit.ps1 -WallLengthMm 4000 -WallHeightMm 3000 -MoveYMm 250
```

Release-candidate evidence form:

```powershell
npm run smoke:revit -- -ExpectedRevitYear 2024 -RequireTypeChange -SummaryPath artifacts\live-revit-smoke\smoke-summary.json
```

Current coverage:

- Exact command: `npm run smoke:revit`.
- Required state: Windows, Node 24, installed Revit MCP Next launcher, Revit running with the add-in loaded, and an active disposable project document.
- Smoke scope: installed launcher, broker/add-in pipe auth via the launcher, `revit.status`, `revit.get_views`, `revit.get_sheets`, `revit.get_current_view`, `revit.get_current_view_elements`, `revit.get_selection`, `revit.analyze_model`, `revit.get_model_readiness`, `revit.get_material_quantities`, `revit.get_rooms`, `revit.get_levels`, `revit.catalog`, `revit.query`, `revit.describe_parameters`, `create_level`, `create_grid`, `create_floor`, `create_wall`, optional `place_family_instance`, `create_room`, `create_sheet`, optional `place_view_on_sheet`, optional `create_text_note`, optional `tag_room`, optional `tag_element`, `set_parameter`, optional or required `change_element_type`, `move_element`, `rotate_element`, `copy_element`, `set_element_pinned`, and `delete_element`.
- Pass/fail artifacts: console output plus add-in logs under `%LOCALAPPDATA%\RevitMcpNext\logs`; pass/fail JSON when `-SummaryPath` is supplied; `npm run smoke:release-local` also writes `bridge-readiness.log` before the destructive smoke starts. Use `npm run support:bundle` after a failure.

Current non-coverage:

- `npm run smoke:revit` does not launch Revit or create a project document. Use `npm run smoke:release-local` on disposable test machines when a local orchestrated run is desired.
- It does not validate signed release artifacts.
- `npm run smoke:revit` does not collect a packaged release evidence bundle by itself; `npm run smoke:release-local` does.
- It does not prove cancellation beyond schema/CLI routing and clean no-op behavior, and it does not cover destructive operations beyond the current bounded preview/apply mutation set.

## Manual Self-Hosted Revit Smoke

Use this workflow for release candidates on a Windows self-hosted machine with Revit installed and an interactive desktop session. The current smoke command requires an already running Revit instance and an active disposable project.

### GitHub Actions Dispatch

The `Live Revit Smoke` workflow is manually dispatched and requires a runner labeled `self-hosted`, `Windows`, and `revit`. Configure the Revit API path, Revit executable path, and a disposable model path in the workflow inputs when the workflow launches Revit.

The workflow has two safe modes:

- `skip_install=false`: the workflow builds a staged package and installs from that package. Revit must be closed before the install step, and `launch_revit=true` requires `revit_model_path`.
- `skip_install=true`: the workflow skips build/install and smokes the already installed add-in in an already running Revit session with an active disposable project document.

The workflow:

- Verifies `RevitAPI.dll`, `RevitAPIUI.dll`, and `Revit.exe` on the runner.
- Builds the current checkout with Node 24 and the configured Revit API path.
- Creates a staged package under `artifacts\release-candidate` and installs from that package.
- Refuses to install over a running Revit process unless `skip_install=true`.
- Uses an existing Revit process or launches Revit with the configured disposable model path.
- Runs `npm run doctor:windows` and `npm run smoke:revit`; by default the workflow requires `change_element_type` coverage through a disposable model with at least two compatible wall types.
- Supports `signing_mode=none`, `signing_mode=local-dev`, and `signing_mode=release-cert`. Local-dev signing is only for disposable smoke runners; production release candidates should use release certificate secrets.
- Collects a redacted support bundle and uploads `artifacts/live-revit-smoke`.
- Runs `npm run evidence:release:windows` for the staged package and uploads `artifacts/release-evidence`.

This workflow validates a live Revit host and staged package install path. Keep the workflow artifact with the package manifest, checksums, signing status, support bundle, live smoke output, and release-evidence bundle.

### Local Equivalent

1. Build and validate the candidate:

```powershell
npm install
npm run build
npm run build:addin
node scripts\validate-repo.mjs
```

2. Create the staged package:

```powershell
npm run package:windows
```

3. Install from the unpacked staged package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts\release\revit-mcp-next-<version>-windows\installer\install-windows.ps1
```

4. Start Revit manually on the same machine, load the add-in, and open a disposable project document. Do not use a production model.

5. Run install diagnostics and the live smoke:

```powershell
npm run doctor:windows
npm run smoke:revit -- -ExpectedRevitYear 2024 -SummaryPath artifacts\live-revit-smoke\smoke-summary.json
```

6. Capture support diagnostics after the smoke, especially after any failure:

```powershell
npm run support:bundle
```

7. Generate the release evidence bundle:

```powershell
npm run evidence:release:windows -- `
  -PackageRoot artifacts\release\revit-mcp-next-<version>-windows `
  -ValidateRepoLogPath artifacts\release-logs\validate-repo.log `
  -PackageLogPath artifacts\release-logs\package-release.log `
  -DoctorLogPath artifacts\release-logs\doctor-windows.log `
  -SigningSkipReason "No release certificate configured for this candidate." `
  -LiveSmokeEvidencePath artifacts\live-revit-smoke `
  -SupportBundlePath artifacts\support\revit-mcp-next-support-<timestamp>.zip `
  -HostedIntegrationEvidencePath artifacts\host-integrations
```

Archive the release evidence bundle with the staged package. Include signing verification output when signing is enabled; otherwise keep the explicit signing skip reason in the evidence manifest. For a production release claim, include hosted pyRevit/Dynamo evidence rather than only the local-smoke skip reason.

For a disposable local machine, the manual sequence above can be replaced with:

```powershell
npm run smoke:release-local -- -RevitYear 2024
```

The script writes a run directory under `C:\tmp\revit-mcp-next-smoke` when writable, otherwise beside the repo, uses a stable per-year install root under `%APPDATA%\Autodesk\Revit\Addins`, copies the default Dynamo sample RVT before launching Revit, performs a second-startup readiness probe when it owns the Revit process, and collects logs/support/evidence for the candidate. Pass `-OutputRoot` to place artifacts elsewhere, `-InstallRoot` to override the stable local install path, `-ModelPath` to use a different disposable model, `-PackageRoot` with `-SkipBuild` to smoke an existing staged package, `-TrustRevitAlwaysLoad` to also seed the disposable-machine Revit trust entry, `-SkipSecondStartupProbe` only when restart ownership is impossible, `-SkipLocalDevSigning` to intentionally test unsigned packages, or `-NoLaunch` to require an already running Revit session.

When smoking an existing signed package with `-SkipBuild -PackageRoot`, pass existing `-ValidateRepoLogPath`, `-PackageLogPath`, or `-SigningLogPath` when available. If the package manifest says signing was requested and no signing log is provided, local smoke runs `npm run sign:windows -- -VerifyOnly -RequireSigned` and uses that verification log for release evidence. Local launcher smoke uses `-HostedIntegrationEvidencePath` when hosted pyRevit/Dynamo evidence already exists, or records an explicit hosted-integration skip reason when it does not. Run `npm run smoke:host-integrations` before final production evidence collection.
