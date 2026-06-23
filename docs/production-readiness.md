# Production Readiness Audit

This project is not yet a signed production release. The current repository state supports local development, staged Windows packaging, install diagnostics, redacted support bundle collection, and release evidence bundle generation.

Use this audit to separate evidence that already exists from blockers that still need release work.

## Current Evidence

- CI builds and tests the broker/contracts workspaces on Windows with Node 24.
- CI runs `node scripts/validate-repo.mjs`.
- CI builds the .NET bridge contracts.
- CI attempts the Revit add-in build only when Revit 2024 API DLLs are present on the runner.
- `npm run package:windows:dry-run` validates package inputs after the broker/contracts/add-in build outputs exist.
- `npm run package:windows` stages a Windows package with `release-manifest.json` and `CHECKSUMS.sha256`; `-Sign` can request Authenticode signing before manifest, checksum, and zip capture when a certificate is supplied.
- `npm run test:release:windows` runs in hosted CI with synthetic add-in DLL placeholders. It validates unsigned package creation, zip creation, package install into temp profile paths, doctor output, support bundle redaction, and checksum-tamper rejection without requiring Revit API DLLs on the runner.
- `npm run evidence:release:windows` creates a release evidence bundle for one staged package. It records package metadata, package zip SHA-256, signing status, named validation logs, support-bundle evidence, live-smoke evidence, explicit skip reasons, and an inventory of copied evidence files.
- `npm run test:evidence:release:windows` runs in hosted CI with synthetic add-in DLL placeholders. It validates release evidence generation, explicit missing-evidence gates, package hash capture, copied package metadata, support/live-smoke evidence capture, validation log capture, and token redaction.
- `npm run test:integrations:python` syntax-checks pyRevit/Dynamo examples and validates the shared stdlib Python MCP client against a fake stdio MCP server.
- `npm run doctor:windows` validates the installed launcher, staged broker files, add-in DLLs, Revit manifest, packaged production dependencies, local pipe auth token shape, and add-in DLL signature status.
- `npm run support:bundle` collects doctor output, install metadata, logs, file hashes, and redacted auth configuration.
- `revit.catalog` provides compact, paginated discovery for element types, family symbols, title blocks, and view family types, including Revit-compatible type IDs for a target element.
- Read parity with the fork now covers compact current-view metadata, current-view elements, selected elements, model statistics, and material quantities through `revit.get_current_view`, `revit.get_current_view_elements`, `revit.get_selection`, `revit.analyze_model`, and `revit.get_material_quantities`.
- `npm run smoke:revit` runs a live MCP smoke through the installed launcher against the active Revit project. It checks `revit.status`, the read/analysis tools above, `revit.get_levels`, `revit.catalog`, `revit.query`, plus preview/apply flows for `create_grid`, `create_floor`, `create_wall`, `move_element`, `rotate_element`, `copy_element`, and `set_element_pinned`. When an alternate compatible wall type exists, it also applies `change_element_type`; release-candidate runs can require this with `-RequireTypeChange`.
- `npm run smoke:release-local` orchestrates a local disposable-machine release smoke: build, package, install, copy a sample RVT, launch Revit when needed, wait for `revit.status` readiness, run doctor, run live smoke, collect support bundle, and collect release evidence into a short local work root. It defaults to `C:\tmp\revit-mcp-next-smoke` when writable, otherwise a short sibling directory beside the repo, to avoid Windows path-length failures in packaged `node_modules`.
- Packaged installs include pyRevit and Dynamo examples plus external-stdio and in-process Python helpers under `integrations`. The installer writes token-safe `config\client-discovery.json` for clients.
- `npm run sign:windows` provides optional Authenticode signing and verification for `.dll` and `.ps1` package targets. No release certificate is assumed by this repository.
- `.github/workflows/live-revit-smoke.yml` defines a manual self-hosted Windows/Revit smoke workflow. It builds a staged package, installs from that package, can optionally launch Revit, runs doctor and live smoke, collects a support bundle, attempts release-evidence bundle collection, and uploads smoke/package/evidence artifacts.

## Remaining Blockers

- Signed artifacts from an available release certificate, plus archived verification evidence before any signed-release claim.
- Successful release-candidate live smoke evidence from a self-hosted Revit runner.
- End-to-end live validation evidence that ties installer behavior, broker/add-in pipe auth, `revit.status`, read tools, and preview/apply write flows to a specific packaged build. Hosted package-contract CI covers package mechanics only; it does not prove Revit can load a release DLL.
- More real-model write-operation and failure-mode evidence before calling the mutation surface production-complete.
- Multi-version Revit compatibility validation beyond the current Revit 2024 target.
- Archived release evidence bundle for each release candidate, generated from that exact package, signing state, validation logs, support bundle, and live-smoke output.

## Release Evidence Gate

Release evidence generation exists, but a production release still needs evidence from a real candidate build. Do not describe artifacts as signed unless a certificate was configured for that build and signature verification output was archived.

Acceptance criteria for this slice:

- Packaging can optionally apply Authenticode signatures before final manifest, checksum, and zip capture when a release certificate is supplied.
- Unsigned local or dry-run packages remain clearly labeled as unsigned staged artifacts.
- Signing happens before final checksum and zip evidence is captured, or the release process regenerates those files after signing.
- Release evidence captures package metadata, checksums, signing status, validation output, install diagnostics, support bundle output, and live-smoke output for one build.
- A manual self-hosted Revit live-smoke workflow is run for release candidates and its artifacts are archived with the release evidence.

Recommended release evidence:

- `release-manifest.json`, including package version, commit, dirty status, Revit years, and file inventory.
- `CHECKSUMS.sha256` plus a separately recorded SHA-256 hash of the package `.zip`.
- Command logs for `node scripts\validate-repo.mjs`, packaging, install, doctor, support bundle, and live smoke.
- Authenticode signer identity, certificate thumbprint, timestamp information, and verification output when signing is enabled.
- A clear skip reason for any unavailable evidence, such as no signing certificate or no Revit host.

## Live Smoke Documentation

Run the local live smoke after installing the staged build, opening Revit, loading the add-in, and activating a disposable project document:

```powershell
npm run smoke:revit
```

The smoke mutates the active project. It creates a grid, floor, and straight wall on the first building-story level, queries created elements back, optionally changes the wall type, moves the wall on the Y axis, verifies the reported movement, rotates the wall, copies it by an offset, then pins and unpins the copied wall. It also checks blocked duplicate-grid and pinned-move previews plus a rejected apply with a mismatched `changeSetHash`.

Useful direct wrapper form:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\live-smoke-revit.ps1 -WallLengthMm 4000 -WallHeightMm 3000 -MoveYMm 250
```

Current coverage:

- Exact command: `npm run smoke:revit`.
- Required state: Windows, Node 24, installed Revit MCP Next launcher, Revit running with the add-in loaded, and an active disposable project document.
- Smoke scope: installed launcher, broker/add-in pipe auth via the launcher, `revit.status`, `revit.get_current_view`, `revit.get_current_view_elements`, `revit.get_selection`, `revit.analyze_model`, `revit.get_material_quantities`, `revit.get_levels`, `revit.catalog`, `revit.query`, `create_grid`, `create_floor`, `create_wall`, optional or required `change_element_type`, `move_element`, `rotate_element`, `copy_element`, and `set_element_pinned`.
- Pass/fail artifacts: console output plus add-in logs under `%LOCALAPPDATA%\RevitMcpNext\logs`; `npm run smoke:release-local` also writes `bridge-readiness.log` before the destructive smoke starts. Use `npm run support:bundle` after a failure.

Current non-coverage:

- `npm run smoke:revit` does not launch Revit or create a project document. Use `npm run smoke:release-local` on disposable test machines when a local orchestrated run is desired.
- It does not validate signed release artifacts.
- `npm run smoke:revit` does not collect a packaged release evidence bundle by itself; `npm run smoke:release-local` does.
- It does not cover cancellation, destructive operations, or Revit versions other than the active installed version.

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
- Collects a redacted support bundle and uploads `artifacts/live-revit-smoke`.
- Attempts `npm run evidence:release:windows` for the staged package and uploads `artifacts/release-evidence`.

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
npm run smoke:revit
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
  -SupportBundlePath artifacts\support\revit-mcp-next-support-<timestamp>.zip
```

Archive the release evidence bundle with the staged package. Include signing verification output when signing is enabled; otherwise keep the explicit signing skip reason in the evidence manifest.

For a disposable local machine, the manual sequence above can be replaced with:

```powershell
npm run smoke:release-local -- -RevitYear 2024
```

The script writes a run directory under `C:\tmp\revit-mcp-next-smoke` when writable, otherwise beside the repo, uses a run-local install root, copies the default Dynamo sample RVT before launching Revit, and collects logs/support/evidence for the candidate. Pass `-OutputRoot` to place artifacts elsewhere, `-ModelPath` to use a different disposable model, `-PackageRoot` with `-SkipBuild` to smoke an existing staged package, or `-NoLaunch` to require an already running Revit session.
