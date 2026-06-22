# Production Readiness Audit

This project is not yet a signed production release. The current repository state supports local development, staged Windows packaging, install diagnostics, and redacted support bundle collection.

Use this audit to separate evidence that already exists from blockers that still need release work.

## Current Evidence

- CI builds and tests the broker/contracts workspaces on Windows with Node 24.
- CI runs `node scripts/validate-repo.mjs`.
- CI builds the .NET bridge contracts.
- CI attempts the Revit add-in build only when Revit 2024 API DLLs are present on the runner.
- `npm run package:windows:dry-run` validates package inputs after the broker/contracts/add-in build outputs exist.
- `npm run package:windows` stages a Windows package with `release-manifest.json` and `CHECKSUMS.sha256`.
- `npm run doctor:windows` validates the installed launcher, staged broker files, add-in DLLs, Revit manifest, packaged production dependencies, and local pipe auth token shape.
- `npm run support:bundle` collects doctor output, install metadata, logs, file hashes, and redacted auth configuration.
- `npm run smoke:revit` runs a live MCP smoke through the installed launcher against the active Revit project. It checks `revit.status`, `revit.get_levels`, `revit.query`, `create_wall` preview/apply, and `move_element` preview/apply.

## Remaining Blockers

- Signed release artifacts and a repeatable release signing process.
- Automated live Revit smoke on a dedicated runner that can provision/install the current build, load Revit, open a disposable model, run `npm run smoke:revit`, and archive logs/support bundles.
- End-to-end live validation evidence that ties installer behavior, broker/add-in pipe auth, `revit.status`, read tools, and preview/apply write flows to a specific packaged build.
- Broader write-operation coverage and failure-mode validation before calling the mutation surface production-complete.
- Multi-version Revit compatibility validation beyond the current Revit 2024 target.
- Release evidence capture that ties a package, checksums, support diagnostics, and live smoke result to the same build.

## Live Smoke Documentation

Run the local live smoke after installing the staged build, opening Revit, loading the add-in, and activating a disposable project document:

```powershell
npm run smoke:revit
```

The smoke mutates the active project. It creates a straight wall on the first building-story level, queries it back, moves it on the Y axis, and verifies the reported movement.

Useful direct wrapper form:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\live-smoke-revit.ps1 -WallLengthMm 4000 -WallHeightMm 3000 -MoveYMm 250
```

Current coverage:

- Exact command: `npm run smoke:revit`.
- Required state: Windows, Node 24, installed Revit MCP Next launcher, Revit running with the add-in loaded, and an active disposable project document.
- Smoke scope: installed launcher, broker/add-in pipe auth via the launcher, `revit.status`, `revit.get_levels`, `revit.query`, `create_wall`, and `move_element`.
- Pass/fail artifacts: console output plus add-in logs under `%LOCALAPPDATA%\RevitMcpNext\logs`; use `npm run support:bundle` after a failure.

Current non-coverage:

- It does not launch Revit or create a project document.
- It does not validate signed release artifacts.
- It does not produce a packaged release evidence bundle by itself.
- It does not cover cancellation, destructive operations, or Revit versions other than the active installed version.
