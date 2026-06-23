# revit-mcp-next

Clean-room Revit MCP bridge for Claude Code, Codex, and other MCP clients.

This repo intentionally does **not** fork the existing Revit MCP implementation. It keeps the useful architecture lesson, an external MCP broker plus an in-process Revit add-in, but rebuilds the contracts, transport, result shape, safety model, and installer from scratch.

## Goals

- Token-efficient tools with bounded `structuredContent`, output schemas, pagination, and short text summaries.
- Fast model queries using Revit-native collectors, projections, aggregates, and cursor-backed resources.
- Safe writes through preview/apply contracts, explicit transactions, no modal dialogs, and clear mutation reports.
- Frictionless local setup for Claude Code, Claude Desktop, and Codex Desktop on Windows.
- Correct by construction: no silent caps, no mixed units, no duplicated TS/C# schemas, no `int` element IDs.

## Architecture

```text
MCP client (Claude Code / Codex)
        |
        | stdio MCP
        v
broker/  - tool schemas, result shaping, policy, pagination
        |
        | framed local IPC, named pipe by default
        v
addin/   - Revit add-in, ExternalEvent queue, transactions
        |
        v
Autodesk Revit API
```

## Current Status

Local productionization slice, not yet a signed production release:

- `contracts/`: shared protocol and tool-result TypeScript types plus JSON schema.
- `broker/`: MCP stdio server with bounded read/write tools, output schemas, structured errors, pipe auth token forwarding, and bridge tests.
- `addin/`: Revit 2024 add-in with named-pipe IPC, pipe auth token enforcement when configured, cancellation-aware `ExternalEvent` queue, read handlers, and preview/apply write handlers.
- `installer/`: Windows installer that stages broker/contracts/add-in artifacts under `%LOCALAPPDATA%\RevitMcpNext`, writes the Revit `.addin` manifest, provisions a per-install pipe auth token under `config\auth.env`, and creates a Claude/Codex launcher.
- `scripts/package-release.ps1`: staged Windows release package with payload checksums and optional bundled production dependencies.
- `scripts/collect-support-bundle.ps1`: redacted support bundle for doctor output, logs, install metadata, and file hashes.
- `scripts/collect-release-evidence.ps1`: release evidence bundle that ties one package, checksums, signing status, validation logs, support diagnostics, and live-smoke evidence or skip reasons together.
- `integrations/python`: stdlib MCP client for external Python plus an in-process helper for pyRevit and Dynamo; packaged installs stage both under `%LOCALAPPDATA%\RevitMcpNext\integrations`.
- `integrations/pyrevit` and `integrations/dynamo`: example scripts/nodes that call the installed MCP launcher rather than bypassing broker auth and preview/apply policy.

## First Local Commands

```powershell
npm install
npm run build
npm run build:addin
npm test
node scripts\validate-repo.mjs
npm run test:integrations:python
npm run install:windows
npm run doctor:windows
npm run smoke:revit
npm run smoke:release-local
npm run package:windows:dry-run
npm run test:evidence:release:windows
```

`npm run build:addin` expects Revit 2024 API DLLs at `C:\Program Files\Autodesk\Revit 2024`. Pass `-RevitApiPath` to `scripts\build-addin.ps1` if Revit is installed elsewhere.

`npm run smoke:revit` requires Revit to be running with an active project document and mutates that active document by creating and moving a smoke-test wall. Use a disposable model.
`npm run smoke:release-local` is the one-command disposable-machine path: it builds, packages, installs to a run-local root, copies a sample RVT, launches Revit when needed, runs doctor/live smoke/support collection, and attempts release evidence collection. It defaults to `C:\tmp\revit-mcp-next-smoke` when writable, otherwise a short sibling directory beside the repo, to avoid Windows path-length failures in packaged `node_modules`.

Release-candidate smoke runs should use a disposable model with at least two compatible wall types and require type-change coverage:

```powershell
npm run smoke:revit -- -RequireTypeChange
```

## Packaging And Support

Create a staged Windows release package:

```powershell
npm run package:windows
```

The package lands in `artifacts\release`, includes `release-manifest.json` plus `CHECKSUMS.sha256`, and can be installed from the unpacked package by running `installer\install-windows.ps1`. See [release-packaging.md](docs/release-packaging.md).

Collect a redacted support bundle:

```powershell
npm run support:bundle
```

Collect release evidence for a staged package:

```powershell
npm run evidence:release:windows -- -PackageRoot artifacts\release\revit-mcp-next-<version>-windows -SigningSkipReason "No release certificate configured." -LiveSmokeSkipReason "No Revit host for this local run." -SupportBundleSkipReason "No installed candidate support bundle for this local run."
```

The evidence command refuses to omit signing, live-smoke, or support-bundle evidence silently. Pass the artifact path when evidence exists, or an explicit skip reason when it does not.

Windows installs generate a local 256-bit auth token in `%LOCALAPPDATA%\RevitMcpNext\config\auth.env` and restrict the file ACL to the installing user, Administrators, and SYSTEM when Windows allows it. The generated launcher reads that config and exports `REVIT_MCP_NEXT_AUTH_TOKEN` for the broker process. Doctor and support bundle output report token presence/shape only; support bundles redact the token value.

## pyRevit, Dynamo, And Python

pyRevit and Dynamo run inside Revit, so their examples use `integrations/python/revit_mcp_next_inprocess.py`. That helper calls the add-in's in-process bridge and avoids blocking Revit while waiting for an `ExternalEvent`.

Plain Python processes outside Revit can use `integrations/python/revit_mcp_next_client.py`. It starts the installed MCP launcher and calls normal MCP tools over stdio.

The installer also writes `%LOCALAPPDATA%\RevitMcpNext\config\client-discovery.json` so clients can find the launcher, add-in assembly, schemas, and integration helpers without reading or printing the auth token.

Examples:

- pyRevit extension: `integrations/pyrevit/revit_mcp_next.extension`
- Dynamo status node: `integrations/dynamo/status_node.py`

After install, examples can import the helpers from `%LOCALAPPDATA%\RevitMcpNext\integrations\python`.

## MVP Tool Surface

- `revit.status`
- `revit.list_documents`
- `revit.get_levels`
- `revit.get_current_view`
- `revit.get_current_view_elements`
- `revit.get_selection`
- `revit.analyze_model`
- `revit.get_material_quantities`
- `revit.catalog`
- `revit.query`
- `revit.preview_change_set`
- `revit.apply_change_set`
- `revit.cancel_request`

Read tools are intentionally compact and paginated where results can grow. Use `revit.get_current_view_elements` and `revit.get_selection` for ergonomic scoped reads, `revit.query` for custom filters, `revit.analyze_model` for bounded model statistics, and `revit.get_material_quantities` for normalized material takeoffs.

Write tools are intentionally bounded. End-to-end preview/apply support currently covers:

- `set_parameter`: set a writable instance parameter by element ID and parameter name.
- `create_level`: create a level by name and elevation.
- `create_wall`: create a straight wall from `levelId`, `start`, `end`, optional `wallTypeId`, optional `height`, optional `structural`, and optional `flip`.
- `create_grid`: create a straight grid line from `start` to `end`, with an optional unique name.
- `create_floor`: create a single-loop floor from `levelId`, ordered `outline` points, optional `floorTypeId`, and optional `structural`.
- `move_element`: move one non-pinned model element by `elementId` and an explicit 3D translation vector.
- `rotate_element`: rotate one non-pinned model element around an explicit axis and angle.
- `copy_element`: copy one model element by an explicit 3D translation vector.
- `change_element_type`: change one non-pinned model element to a compatible Revit type ID discovered through `revit.catalog`.
- `set_element_pinned`: pin or unpin one model element, with optional `expectedPinned` guard.

`revit.preview_change_set` validates supported operations without mutation and returns a `previewId`; `revit.apply_change_set` requires that matching `previewId` plus `confirm: true` and applies the full change set in one named Revit transaction.

Use `revit.catalog` before writes that need Revit type IDs. It returns compact, paginated catalog records for `elementTypes`, `familySymbols`, `titleBlocks`, and `viewFamilyTypes`. For type changes, call it with `kind: "elementTypes"` and `filter.forElementId` so Revit's own compatible type list is used.

## Production Readiness And Remaining Blockers

This repository is ready for local development and staged Windows packaging, but production release hardening is still in progress.

See [production-readiness.md](docs/production-readiness.md) for the current evidence and blocker audit.

Remaining blockers:

- Signed release artifacts from an available release certificate, plus archived signing verification evidence.
- Release-candidate live Revit smoke evidence on a self-hosted Revit runner, including installer, broker/add-in pipe auth, read tools, and preview/apply flows.
- Archived release evidence bundle for each release candidate, generated from the exact package, signing state, diagnostics, support bundle, and live-smoke output for that build.
- More real-model write-operation and failure-mode evidence before calling the mutation surface production-complete.
- Multi-version Revit compatibility validation beyond the current Revit 2024 target.
