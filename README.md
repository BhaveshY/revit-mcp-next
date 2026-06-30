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

Revit 2024-only production-candidate slice, not yet a signed production release:

- `contracts/`: shared protocol and tool-result TypeScript types plus JSON schema.
- `broker/`: MCP stdio server with bounded read/write tools, including `revit.get_rooms` and guarded `create_room`, output schemas, structured errors, pipe auth token forwarding, and bridge tests.
- `addin/`: Revit 2024 add-in with named-pipe IPC, pipe auth token enforcement when configured, cancellation-aware `ExternalEvent` queue, read handlers including rooms, and preview/apply write handlers including room placement.
- `installer/`: Windows installer that stages broker/contracts/add-in artifacts under `%LOCALAPPDATA%\RevitMcpNext`, writes the Revit `.addin` manifest, provisions a per-install pipe auth token under `config\auth.env`, and creates a Claude/Codex launcher.
- `scripts/package-release.ps1`: staged Windows release package with payload checksums and optional bundled production dependencies.
- `scripts/ensure-dev-signing-certificate.ps1`: CurrentUser local dev code-signing certificate bootstrapper for disposable Revit smoke machines.
- `scripts/ensure-revit-addin-trust.ps1`: supplemental helper that inspects/seeds/removes Revit's per-user `Always Load` trust entry for the add-in `ClientId`.
- `scripts/ensure-pyrevit-hosts-cache.ps1`: optional self-hosted runner helper for pyRevit CLI builds that lag Autodesk Revit build metadata.
- `scripts/print-mcp-config.ps1`: token-safe Claude Code, Claude Desktop, and Codex config snippets from the installed client discovery file.
- `scripts/doctor-clients.ps1`: token-safe client config doctor for generated Claude/Codex snippets, installed config files, launcher quoting, stale roots, and MCP startup/tool-list checks.
- `scripts/collect-support-bundle.ps1`: redacted support bundle for doctor output, logs, install metadata, and file hashes.
- `scripts/collect-host-integration-evidence.ps1`: validates raw pyRevit/Dynamo host-smoke JSON and writes release-ready `host-integrations-summary.json`.
- `scripts/collect-release-evidence.ps1`: release evidence bundle that ties one package, checksums, signing status, validation logs, support diagnostics, live-smoke evidence, and hosted pyRevit/Dynamo evidence or skip reasons together.
- `integrations/python`: stdlib MCP client for external Python plus in-process helpers for pyRevit and Dynamo; packaged installs stage them under the chosen install root and the examples search common `%APPDATA%` and `%LOCALAPPDATA%` locations.
- `integrations/pyrevit` and `integrations/dynamo`: in-process status, preview/apply, and host-smoke evidence examples for Revit-hosted automation without deadlocking on an external event.

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
npm run mcp:config
npm run doctor:clients
npm run smoke:revit
npm run smoke:release-local
npm run package:windows:dry-run
npm run test:evidence:release:windows
```

`npm run build:addin` expects Revit 2024 API DLLs at `C:\Program Files\Autodesk\Revit 2024`. Pass `-RevitApiPath` to `scripts\build-addin.ps1` if Revit is installed elsewhere.

`npm run smoke:revit` requires Revit to be running with an active project document and mutates that active document through the bounded preview/apply smoke workflow. It creates test geometry, a room, optional family placement when the model has suitable symbols, parameter/type changes where possible, movement/rotation/copy/pin operations, and cleanup of the copied wall. Use a disposable model.
`npm run smoke:release-local` is the one-command disposable-machine path: it builds, installs to a stable per-year root under `%APPDATA%\Autodesk\Revit\Addins`, copies a sample RVT, launches Revit when needed, waits for `revit.status` readiness, runs doctor/live smoke, closes and relaunches its own Revit process for a second status-only no-prompt probe, collects support output, and attempts release evidence collection. Evidence and package work directories default to `C:\tmp\revit-mcp-next-smoke` when writable, otherwise a short sibling directory beside the repo, to avoid Windows path-length failures in packaged `node_modules`.

Unsigned local add-in builds can pause Revit on the security prompt `Security - Unsigned Add-in` / `Sicherheit - Zusatzmodul ohne Signatur`. The application manifest now uses `<ClientId>6F78E70D-BE13-4E0B-9B11-9E28F876AF71</ClientId>`, but the durable no-prompt path is trusted Authenticode signing. `npm run smoke:release-local` creates/trusts a disposable CurrentUser dev certificate, signs the package, and verifies trusted signatures before launch. Production releases still need a real release certificate and archived signature verification evidence.

Inspect or remove the local dev signing certificate after disposable-machine testing:

```powershell
npm run dev-cert:windows -- -StatusOnly
npm run dev-cert:windows -- -Remove -DryRun
npm run dev-cert:windows -- -Remove
```

If `pyrevit run` fails before a hosted smoke script runs because pyRevit does not recognize a newer or pre-FCS Revit build, seed the per-user pyRevit host cache instead of editing pyRevit under Program Files:

```powershell
npm run pyrevit:hosts -- -Builds 20230106_1515,20241105_1515
```

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
npm run evidence:release:windows -- -PackageRoot artifacts\release\revit-mcp-next-<version>-windows -SigningSkipReason "No release certificate configured." -LiveSmokeSkipReason "No Revit host for this local run." -SupportBundleSkipReason "No installed candidate support bundle for this local run." -HostedIntegrationSkipReason "No pyRevit/Dynamo host smoke for this local run."
```

The evidence command refuses to omit signing, live-smoke, support-bundle, or hosted pyRevit/Dynamo evidence silently. Pass the artifact path when evidence exists, or an explicit skip reason when it does not.

Windows installs generate a local 256-bit auth token in `%LOCALAPPDATA%\RevitMcpNext\config\auth.env` and restrict the file ACL to the installing user, Administrators, and SYSTEM when Windows allows it. The generated launcher reads that config and exports `REVIT_MCP_NEXT_AUTH_TOKEN` for the broker process. Doctor and support bundle output report token presence/shape only; support bundles redact the token value.

After install, print ready-to-use MCP client entries without exposing the auth token:

```powershell
npm run mcp:config
npm run mcp:config -- -Client claude-code
npm run mcp:config -- -Client codex
npm run doctor:clients
```

Use these generated snippets as the source of truth for Claude and Codex config. They point at the installed launcher and avoid printing or copying the local pipe auth token. `npm run doctor:clients` verifies the generated snippets, existing Claude Desktop/Codex config files when present, launcher quoting, stale install roots, raw token leakage risk, and basic MCP startup plus `tools/list` without requiring a Revit connection.

## pyRevit, Dynamo, And Python

pyRevit and Dynamo run inside Revit, so their examples use `integrations/python/revit_mcp_next_inprocess.py`. That helper calls the add-in's in-process bridge and avoids blocking Revit while waiting for an `ExternalEvent`. It exposes `status`, `execute_operation`, `preview_change_set`, `apply_change_set`, and `apply_preview` for compact hosted scripts.

Plain Python processes outside Revit can use `integrations/python/revit_mcp_next_client.py`. It starts the installed MCP launcher and calls normal MCP tools over stdio.

The installer also writes `%LOCALAPPDATA%\RevitMcpNext\config\client-discovery.json` so clients can find the launcher, add-in assembly, schemas, and integration helpers without reading or printing the auth token.

Release candidates should also run the hosted smoke examples and archive a `host-integrations-summary.json` that records both pyRevit and Dynamo with `status: "passed"`.

For unattended pyRevit evidence, set `REVIT_MCP_NEXT_PYREVIT_EVIDENCE` to the raw JSON output path and `REVIT_MCP_NEXT_PYREVIT_MODEL` to the disposable RVT before running the packaged Host Smoke command with `pyrevit run`. The command opens the model when pyRevitRunner starts without an active document.

The packaged runner wraps that setup and validates the raw JSON before it can be used as release evidence:

```powershell
npm run smoke:pyrevit-host -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -EvidencePath artifacts\host-integrations\raw\pyrevit.json -SeedHostsCache
```

Dynamo evidence must come from Dynamo running inside Revit, not from headless `DynamoCLI.exe`, because headless CLI runs do not provide RevitServices. The helper below launches Revit with the required environment variables, waits while you open and run the packaged graph in Dynamo for Revit, then validates `dynamo.json`:

```powershell
npm run smoke:dynamo-host -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -EvidencePath artifacts\host-integrations\raw\dynamo.json -LaunchRevit
```

Run graph: `%LOCALAPPDATA%\RevitMcpNext\integrations\dynamo\revit_mcp_next_host_smoke.dyn`.

The aggregate runner creates the raw evidence files and composes the summary in one release-oriented command. Dynamo still requires opening and running the graph in Dynamo for Revit while the command waits:

```powershell
npm run smoke:host-integrations -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -OutputRoot artifacts\host-integrations -SeedPyRevitHosts -LaunchRevitForDynamo
```

After collecting the raw host JSON files, build the summary with:

```powershell
npm run evidence:host-integrations -- -PyRevitEvidencePath artifacts\host-integrations\raw\pyrevit.json -DynamoEvidencePath artifacts\host-integrations\raw\dynamo.json -OutputRoot artifacts\host-integrations
```

Examples:

- Python stdio client: `integrations/python/revit_mcp_next_client.py`
- Python in-process helper: `integrations/python/revit_mcp_next_inprocess.py`
- Python hosted-smoke helper: `integrations/python/revit_mcp_next_host_smoke.py`
- Python workflow examples helper: `integrations/python/revit_mcp_next_workflow_examples.py`
- pyRevit extension: `integrations/pyrevit/revit_mcp_next.extension`
- pyRevit safe write command: `integrations/pyrevit/revit_mcp_next.extension/Revit MCP Next.tab/Examples.panel/Create Level.pushbutton/script.py`
- pyRevit workflow examples command: `integrations/pyrevit/revit_mcp_next.extension/Revit MCP Next.tab/Examples.panel/Workflow Samples.pushbutton/script.py`
- pyRevit host-smoke command: `integrations/pyrevit/revit_mcp_next.extension/Revit MCP Next.tab/Diagnostics.panel/Host Smoke.pushbutton/script.py`
- Dynamo status node: `integrations/dynamo/status_node.py`
- Dynamo safe write node: `integrations/dynamo/create_level_node.py`
- Dynamo workflow examples node: `integrations/dynamo/workflow_examples_node.py`
- Dynamo host-smoke node: `integrations/dynamo/host_smoke_node.py`
- Dynamo host-smoke graph: `integrations/dynamo/revit_mcp_next_host_smoke.dyn`

After install, examples can import the helpers from the package install root. The included scripts search the auth-config install root, `%LOCALAPPDATA%\RevitMcpNext`, and `%APPDATA%\Autodesk\Revit\Addins\2024\RevitMcpNext`.

## MVP Tool Surface

- `revit.status`
- `revit.list_documents`
- `revit.get_levels`
- `revit.get_current_view`
- `revit.get_current_view_elements`
- `revit.get_selection`
- `revit.analyze_model`
- `revit.get_model_readiness`
- `revit.get_material_quantities`
- `revit.get_rooms`
- `revit.catalog`
- `revit.query`
- `revit.preview_change_set`
- `revit.apply_change_set`
- `revit.cancel_request`

Read tools are intentionally compact and paginated where results can grow. Use `revit.get_current_view_elements` and `revit.get_selection` for ergonomic scoped reads, `revit.query` for custom filters or explicit `elementIds`/`uniqueIds`, `revit.analyze_model` for bounded model statistics, `revit.get_model_readiness` for agent preflight checks, `revit.get_material_quantities` for normalized material takeoffs, and `revit.get_rooms` for compact room export data with room numbers, names, levels, areas, volumes, locations, and schedule fields.

Write tools are intentionally bounded. End-to-end preview/apply support currently covers:

- `set_parameter`: set a writable instance parameter by element ID and parameter name.
- `create_level`: create a level by name and elevation.
- `create_wall`: create a straight wall from `levelId`, `start`, `end`, optional `wallTypeId`, optional `height`, optional `structural`, and optional `flip`.
- `create_grid`: create a straight grid line from `start` to `end`, with an optional unique name.
- `create_floor`: create a single-loop floor from `levelId`, ordered `outline` points, optional `floorTypeId`, and optional `structural`.
- `create_room`: place a room by `levelId` and 2D `location`, with optional `name`, `number`, `department`, and `allowDuplicateNumber`.
- `place_family_instance`: place first-case wall-hosted door/window symbols by `familySymbolId`, `hostElementId`, and `location`, or level-based furniture/equipment/fixture symbols by `familySymbolId`, `levelId`, and `location`.
- `move_element`: move one non-pinned model element by `elementId` and an explicit 3D translation vector.
- `rotate_element`: rotate one non-pinned model element around an explicit axis and angle.
- `copy_element`: copy one model element by an explicit 3D translation vector.
- `change_element_type`: change one non-pinned model element to a compatible Revit type ID discovered through `revit.catalog`.
- `set_element_pinned`: pin or unpin one model element, with optional `expectedPinned` guard.
- `delete_element`: delete one non-type element by `elementId`, with optional `expectedUniqueId`, `expectedPinned`, and `allowPinned` guards.

`revit.preview_change_set` validates supported operations without mutation and returns a `previewId`; `revit.apply_change_set` requires that matching `previewId` plus `confirm: true` and applies the full change set in one named Revit transaction.

Use `revit.catalog` before writes that need Revit type IDs. It returns compact, paginated catalog records for `elementTypes`, `familySymbols`, `titleBlocks`, and `viewFamilyTypes`. For type changes, call it with `kind: "elementTypes"` and `filter.forElementId` so Revit's own compatible type list is used.

See [agent-workflows.md](docs/agent-workflows.md) for practical agent sequences covering model audit, room/wall/floor creation, family placement preview, selected element updates, and blocked preview recovery.

## Production Readiness And Remaining Blockers

This repository is ready for local development and staged Windows packaging as a Revit 2024-only production candidate, but production release hardening is still in progress.

See [production-readiness.md](docs/production-readiness.md) for the current evidence and blocker audit, and [fork-parity.md](docs/fork-parity.md) for the old-fork capability comparison.

Remaining blockers:

- Signed release artifacts from an available release certificate, plus archived signing verification evidence.
- Release-candidate live Revit smoke evidence on a self-hosted Revit runner, including installer, broker/add-in pipe auth, read tools, room read/write support, and preview/apply flows.
- Release-candidate hosted pyRevit and Dynamo evidence from the installed package, summarized in `host-integrations-summary.json`.
- Archived release evidence bundle for each release candidate, generated from the exact package, signing state, diagnostics, support bundle, live-smoke output, and hosted integration output for that build.
- More real-model write-operation and failure-mode evidence before calling the mutation surface production-complete.
- Multi-version Revit compatibility validation beyond the current Revit 2024 target. Revit 2025/2026 remain intentionally out of scope until year-specific add-in artifacts are built, packaged, installed, and smoked.
