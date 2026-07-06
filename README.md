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
- `broker/`: MCP stdio server with bounded read/write tools, including view/sheet inventory, model warnings, parameter discovery, `revit.get_rooms`, guarded `create_room`, output schemas, structured errors, pipe auth token forwarding, `revitctl`, and bridge tests.
- `addin/`: Revit 2024 add-in with named-pipe IPC, pipe auth token enforcement when configured, cancellation-aware `ExternalEvent` queue, read handlers including rooms, and preview/apply write handlers including room placement.
- `installer/`: Windows installer that stages broker/contracts/add-in artifacts under `%LOCALAPPDATA%\RevitMcpNext`, writes the Revit `.addin` manifest, provisions a per-install pipe auth token under `config\auth.env`, and creates the Claude/Codex MCP launcher plus `revitctl.cmd` for debugging.
- `scripts/package-release.ps1`: staged Windows release package with payload checksums and optional bundled production dependencies.
- `scripts/ensure-dev-signing-certificate.ps1`: CurrentUser local dev code-signing certificate bootstrapper for disposable Revit smoke machines.
- `scripts/ensure-revit-addin-trust.ps1`: supplemental helper that inspects/seeds/removes Revit's per-user `Always Load` trust entry for the add-in `ClientId`.
- `scripts/ensure-pyrevit-hosts-cache.ps1`: optional self-hosted runner helper for pyRevit CLI builds that lag Autodesk Revit build metadata.
- `scripts/print-mcp-config.ps1`: token-safe Claude Code, Claude Desktop, and Codex config snippets from the installed client discovery file.
- `scripts/doctor-clients.ps1`: token-safe client config doctor for generated Claude/Codex snippets, installed config files, launcher quoting, stale roots, and MCP startup/tool-list checks.
- `scripts/collect-support-bundle.ps1`: redacted support bundle for doctor output, logs, install metadata, and file hashes.
- `scripts/collect-host-integration-evidence.ps1`: validates raw pyRevit/Dynamo host-smoke JSON plus Dynamo preflight evidence, copies all three raw files, and writes release-ready `host-integrations-summary.json`.
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
npm run revitctl -- status --pretty
npm run smoke:revit
npm run smoke:release-local
npm run package:windows:dry-run
npm run test:evidence:release:windows
```

`npm run build:addin` expects Revit 2024 API DLLs at `C:\Program Files\Autodesk\Revit 2024`. Pass `-RevitApiPath` to `scripts\build-addin.ps1` if Revit is installed elsewhere.

`npm run smoke:revit` requires Revit to be running with an active project document and mutates that active document through the bounded preview/apply smoke workflow. It checks `revit.cancel_request` no-op behavior, creates test geometry, a room, optional family placement when the model has suitable symbols, optional or required room/element tags when the model has loaded or locally loaded tag families and suitable views, parameter/type changes where possible, movement/rotation/copy/pin operations, and cleanup of the copied wall. Use a disposable model.
`npm run smoke:release-local` is the one-command disposable-machine path: it builds, installs to a stable per-year root under `%APPDATA%\Autodesk\Revit\Addins`, copies a sample or supplied disposable `.rvt` project, launches Revit when needed, waits for `revit.status` readiness, runs doctor/live smoke, closes and relaunches its own Revit process for a second status-only no-prompt probe, collects support output, and attempts release evidence collection. Do not pass `.rte` templates to `-ModelPath`; open the template in Revit and save a disposable `.rvt` first. Evidence and package work directories default to `C:\tmp\revit-mcp-next-smoke` when writable, otherwise a short sibling directory beside the repo, to avoid Windows path-length failures in packaged `node_modules`.

To create a disposable project from an installed `.rte` without copying/renaming the template, install/start the package and run `npm run fixture:revit-project -- -TemplatePath C:\path\template.rte -OutputPath C:\tmp\fixture.rvt -Overwrite`. This uses `revit.create_project_from_template` through the Revit API and leaves the saved `.rvt` open for smoke testing.

Unsigned local add-in builds can pause Revit on the security prompt `Security - Unsigned Add-in` / `Sicherheit - Zusatzmodul ohne Signatur`. The application manifest now uses `<ClientId>6F78E70D-BE13-4E0B-9B11-9E28F876AF71</ClientId>`, but the durable no-prompt path is trusted Authenticode signing. `npm run smoke:release-local` creates/trusts a disposable CurrentUser dev certificate, signs the package, and verifies trusted signatures before launch. For an unsigned external preview, label the package as unsigned and include checksums plus smoke/evidence artifacts; only claim a signed release when signature verification evidence exists for that exact build. See [external-preview.md](docs/external-preview.md) for the concise sharing checklist.

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

Release-candidate smoke runs should use a curated disposable model with at least two compatible wall types, a usable room tag type, a wall or multi-category tag type, a printable plan/section view, a placed room, and a visible wall:

```powershell
npm run smoke:revit -- -RequireTypeChange -RequireTags
```

For disposable smoke/release models that lack loaded tag symbols, a workflow can preview/apply `load_family` from vetted local `.rfa` files before `tag_room` or `tag_element`; keep Autodesk family binaries outside this repo. For deterministic curated runners, pin the loaded tag symbols by id or by a stable name/family substring:

```powershell
npm run smoke:revit -- -RequireTypeChange -RequireTags -RoomTagTypeNameContains "Room Tag" -ElementTagTypeNameContains "Wall Tag"
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

## Internal Debug CLI

MCP remains the main agent interface. The installed `revitctl.cmd` is a lower-level bridge CLI for debugging, support, and CI scripts:

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" status --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" doctor --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" views --payload '{"limit":5}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" parameters --payload '{"filter":{"selectionOnly":true},"preset":"writableEdit","limit":5}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" cancel --payload '{"requestId":"pending-request-id","reason":"operator cancelled smoke run"}' --pretty
```

The CLI reads the same installed discovery and auth config as the MCP launcher. It does not print the raw auth token. Use write-control `revitctl` commands only for support/debug workflows on disposable models; `apply` requires exact preview metadata plus `--confirm`, and `cancel` is best-effort for queued or cancellable work. See [revitctl.md](docs/revitctl.md).

MCP clients can also read `revit://discovery` for compact workflow guidance and `revit://tools/{name}` for per-tool guidance. The broker exposes `revit.start_workflow` and `revit.workflow` prompts for clients that support MCP prompts.

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

After the Dynamo profile has already been warmed manually once, the Dynamo runner can launch Revit with a journal that opens and runs the packaged graph:

```powershell
npm run smoke:host-integrations -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -OutputRoot artifacts\host-integrations -SeedPyRevitHosts -LaunchRevitForDynamo -UseDynamoJournalForDynamo
```

Journal mode refuses to run when `DynamoSettings.xml` is missing or not parseable, unless `-AllowUnwarmedDynamoJournal` is passed for an explicitly supervised local experiment. It does not change Dynamo privacy settings or click startup prompts.

After collecting the raw host JSON files and Dynamo preflight report, build the summary with:

```powershell
npm run evidence:host-integrations -- -PyRevitEvidencePath artifacts\host-integrations\raw\pyrevit.json -DynamoEvidencePath artifacts\host-integrations\raw\dynamo.json -DynamoPreflightReportPath artifacts\host-integrations\raw\dynamo-preflight.json -OutputRoot artifacts\host-integrations
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
- `revit.create_project_from_template`
- `revit.get_levels`
- `revit.get_views`
- `revit.get_sheets`
- `revit.get_current_view`
- `revit.get_current_view_elements`
- `revit.get_selection`
- `revit.analyze_model`
- `revit.get_model_readiness`
- `revit.get_model_context`
- `revit.get_material_quantities`
- `revit.get_warnings`
- `revit.get_rooms`
- `revit.catalog`
- `revit.query`
- `revit.describe_parameters`
- `revit.preview_change_set`
- `revit.apply_change_set`
- `revit.cancel_request`

Read tools are intentionally compact and paginated where results can grow. Use `revit.get_views` and `revit.get_sheets` for view/sheet planning, `revit.get_current_view_elements` and `revit.get_selection` for ergonomic scoped reads, `revit.query` for custom filters or explicit `elementIds`/`uniqueIds`, `revit.describe_parameters` before parameter edits, `revit.analyze_model` for bounded model statistics, `revit.get_model_readiness` for agent preflight checks, `revit.get_model_context` for phase/workset/design-option/link planning IDs, `revit.get_material_quantities` for normalized material takeoffs, `revit.get_warnings` for compact model-health warning lists, and `revit.get_rooms` for compact room export data with room numbers, names, levels, areas, volumes, locations, and schedule fields. `revit.create_project_from_template` is a direct fixture/setup write tool, not a preview/apply model edit; it requires `confirm: true` and creates a disposable `.rvt` from a local `.rte`. For element placement work, use `preset: "geometrySummary"` on `revit.query` or scoped element reads to return compact `location` and model-space `bounds` in millimeters without dumping parameters. Prefer cursor-first reads with `includeTotalCount: false`; exact counts are opt-in because they can require scanning every match in large projects. MCP cursors are opaque continuation tokens: do not parse, increment, shorten, construct, or reuse them after changing any argument. For the next page, repeat the same tool call with the same arguments and only add `cursor` from `structuredContent.data.cursor`. `revit.describe_parameters` defaults to `preset: "writableEdit"` for compact writable instance parameter metadata; use `preset: "namesOnly"` for broader name discovery without values or `preset: "full"` for legacy read-only/type/value detail.

All tools return a strict MCP `structuredContent` envelope: `data`, `warnings`, `metrics`, and optional `generation`. On success, `structuredContent.data` is the typed tool payload; on bridge failure it is `{ "error": ... }`. Core read tools advertise typed output schemas through `tools/list`, including page fields such as `returnedCount`, `truncated`, `cursor`, `items`, `fields`, and `units`. Write-control tools advertise typed preview/apply/cancel output schemas with preview tokens, change-set hash/generation/expiry metadata, risk level, itemized change rows, applied counts, and cancellation status.

Write tools are intentionally bounded. End-to-end preview/apply support currently covers:

For existing-element writes, keep using Revit `elementId` or operation-specific IDs such as `roomId` as the target and pass `expectedUniqueId` when a prior read returned `uniqueId`. For wall-hosted family placement, keep using `hostElementId` and pass `expectedHostUniqueId` when the host wall `uniqueId` is known. Preview blocks mismatches before apply.

- `set_parameter`: set a writable instance parameter by element ID and parameter name, optionally guarded by `expectedUniqueId`.
- `create_level`: create a level by name and elevation.
- `create_wall`: create a straight wall from `levelId`, `start`, `end`, optional `wallTypeId`, optional `height`, optional `structural`, and optional `flip`.
- `create_grid`: create a straight grid line from `start` to `end`, with an optional unique name.
- `create_floor`: create a single-loop floor from `levelId`, ordered `outline` points, optional `floorTypeId`, and optional `structural`.
- `create_room`: place a room by `levelId` and 2D `location`, with optional `name`, `number`, `department`, and `allowDuplicateNumber`.
- `place_family_instance`: place first-case wall-hosted door/window symbols by `familySymbolId`, `hostElementId`, optional `expectedHostUniqueId`, and `location`, or level-based furniture/equipment/fixture symbols by `familySymbolId`, `levelId`, and `location`.
- `load_family`: load a vetted local `.rfa` family file into the active document after a ready preview, optionally guarded by `expectedSha256`; use it to make annotation/tag/family workflows deterministic before `tag_room`, `tag_element`, or placement work.
- `create_sheet`: create a sheet with unique `sheetNumber`, optional `name`, and optional `titleBlockTypeId` from `revit.catalog kind=titleBlocks`.
- `place_view_on_sheet`: place an eligible unplaced view on a sheet by `sheetId`, `viewId`, and sheet-space `center`.
- `create_text_note`: create a text note in a graphical non-template view by `viewId`, `text`, `position`, optional `textNoteTypeId`, optional `width`, and optional `rotation`.
- `tag_room`: create a room tag by `roomId`, plan/section `viewId`, 2D `location`, optional `expectedUniqueId`, optional `tagTypeId`, optional `hasLeader`, and optional `orientation`.
- `tag_element`: create an independent element tag by `elementId`, graphical `viewId`, tag `FamilySymbol` `tagTypeId`, `position`, optional `expectedUniqueId`, optional `hasLeader`, and optional `orientation`.
- `move_element`: move one non-pinned model element by `elementId` and an explicit 3D translation vector, optionally guarded by `expectedUniqueId`.
- `rotate_element`: rotate one non-pinned model element around an explicit axis and angle, optionally guarded by `expectedUniqueId`.
- `copy_element`: copy one model element by an explicit 3D translation vector, optionally guarded by `expectedUniqueId`.
- `change_element_type`: change one non-pinned model element to a compatible Revit type ID discovered through `revit.catalog`, optionally guarded by `expectedUniqueId`.
- `set_element_pinned`: pin or unpin one model element, with optional `expectedUniqueId` and `expectedPinned` guards.
- `delete_element`: delete one non-type element by `elementId`, with optional `expectedUniqueId`, `expectedPinned`, `allowPinned`, dependent-delete preview, `allowDependentDeletes`, `expectedDeletedElementIds`, and `expectedDeletedCount` guards.

`revit.preview_change_set` validates supported operations without mutation and returns `previewId`, `baseGeneration`, `changeSetHash`, and `expiresAt`; `revit.apply_change_set` requires those exact preview fields plus `confirm: true` and applies the full change set in one named Revit transaction.

Use `revit.catalog` before writes that need Revit type IDs. It returns compact, paginated catalog records for `elementTypes`, `familySymbols`, `titleBlocks`, `viewFamilyTypes`, `textNoteTypes`, `dimensionTypes`, and `tagTypes`, including room tag types and independent tag symbols. For type changes, call it with `kind: "elementTypes"` and `filter.forElementId` so Revit's own compatible type list is used. For tag workflows, check `revit.catalog` first; use `load_family` only when compatible symbols are missing and a vetted local `.rfa` path is available, then re-run catalog and use the loaded symbol IDs.

See [agent-workflows.md](docs/agent-workflows.md) for practical agent sequences covering model audit, room/wall/floor creation, family placement preview, selected element updates, and blocked preview recovery.

## Production Readiness And Remaining Blockers

This repository is ready for local development and staged Windows packaging as a Revit 2024-only production candidate, but production release hardening is still in progress.

See [production-readiness.md](docs/production-readiness.md) for the current evidence and blocker audit, and [fork-parity.md](docs/fork-parity.md) for the old-fork capability comparison.

Remaining blockers:

- Optional signed release artifacts if a release certificate is available. Unsigned preview packages are acceptable when clearly labeled, with checksums and smoke/evidence artifacts attached.
- Release-candidate live Revit smoke evidence on a self-hosted Revit runner, including installer, broker/add-in pipe auth, read tools, room read/write support, curated `tag_room`/`tag_element` coverage when required, and preview/apply flows.
- Release-candidate hosted pyRevit and Dynamo evidence from the installed package, summarized in `host-integrations-summary.json` and backed by bundled raw `pyrevit.json`, `dynamo.json`, and `dynamo-preflight.json`.
- Archived release evidence bundle for each release candidate, generated from the exact package, signing state, diagnostics, support bundle, live-smoke output, and hosted integration output for that build.
- More real-model write-operation and failure-mode evidence before calling the mutation surface production-complete.
- Multi-version Revit compatibility validation beyond the current Revit 2024 target. Revit 2025/2026 remain intentionally out of scope until year-specific add-in artifacts are built, packaged, installed, and smoked.
