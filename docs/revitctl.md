# revitctl

`revitctl` is the internal Revit MCP Next bridge CLI for debugging, support, and scripted smoke checks.

MCP remains the main Claude/Codex interface. Use `revitctl` when you want a deterministic shell command that proves whether the installed Revit add-in bridge is reachable without starting an MCP client.

## Installed Usage

After `npm run install:windows`:

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" status --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" doctor --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" read-bundle --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" readiness --pretty
```

The installed launcher reads:

- `%LOCALAPPDATA%\RevitMcpNext\config\client-discovery.json`
- `%LOCALAPPDATA%\RevitMcpNext\config\auth.env`

It forwards the local pipe auth token to the add-in but does not print the token.

## Common Commands

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" views --payload '{"filter":{"isTemplate":false},"limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" read-bundle --payload '{"include":{"modelContext":true,"warnings":true},"currentViewElements":{"limit":5},"selection":{"limit":5}}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" sheets --payload '{"includePlacedViews":true,"limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" schedules --payload '{"includeFields":true,"limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" schedule-fields --payload '{"category":"OST_Walls","limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" current-view-elements --payload '{"preset":"geometrySummary","limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" analyze --payload '{"bucketLimit":20,"maxElementsScanned":20000}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" model-context --payload '{"phaseLimit":10,"worksetLimit":10,"designOptionLimit":10,"revitLinkLimit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" materials --payload '{"limit":10,"maxElementsScanned":10000}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" warnings --payload '{"preset":"summary","limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" rooms --payload '{"preset":"schedule","limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" query --payload '{"filter":{"selectionOnly":true},"preset":"summary","limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" query --payload '{"filter":{"selectionOnly":true},"preset":"geometrySummary","limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" parameters --payload '{"filter":{"selectionOnly":true},"preset":"writableEdit","limit":5}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" parameters --payload '{"filter":{"selectionOnly":true},"preset":"full","limit":5}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" catalog --payload '{"kind":"familySymbols","preset":"placement","limit":10}' --pretty
```

To create a disposable project from an installed template for smoke fixtures:

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" create-project --payload '{"templatePath":"C:\\ProgramData\\Autodesk\\RVT 2024\\Templates\\English\\DefaultMetric.rte","outputPath":"C:\\tmp\\revit-mcp-next-template-fixture.rvt","overwrite":true}' --confirm --pretty
```

## Write-Control Commands

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" preview .\change-set.json --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" apply .\apply-payload.json --confirm --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" cancel --payload '{"requestId":"pending-request-id","reason":"operator cancelled smoke run"}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" call preview_change_set --payload .\change-set.json --operation-kind preview --pretty
```

Preview validates without mutation. Apply requires the same change set plus `previewId`, `baseGeneration`, `changeSetHash`, `expiresAt`, and `--confirm` or `confirm: true`. Apply consumes a valid preview token before write execution, so every apply attempt is single-use. Cancel is support/debug only; it can cancel a matching queued request that has not reached the Revit API yet and may return `cancelled: false` when no queued request matches or work is already in-flight.

`revitctl call <operation>` can send lower-level bridge requests for debugging known or experimental add-in operations. Unknown operations default to `operationKind: "debug"` unless `--operation-kind` is supplied; the add-in still validates protocol support and can reject the request.

## Repo Usage

From a built checkout:

```powershell
npm run build
npm run revitctl -- status --pretty
```

For payload-heavy commands through `npm run`, prefer a JSON file to avoid nested PowerShell/npm quote stripping:

```powershell
npm run revitctl -- query --payload .\query.json --pretty
```

Use `--install-root`, `--discovery`, `--auth-config`, `--pipe`, and `--timeout-ms` to target non-default installs.

`read-bundle` is a CLI-composed support probe. It calls existing bridge operations for status, levels, readiness, current view, current-view elements, selection, optional model context, optional warnings, optional catalogs, and optional parameter metadata. It returns `source: "revitctl-composed"`, `returnedSections`, `failedSections`, and optional `sectionMetrics`; it does not send a raw `read_bundle` operation to the Revit add-in.

Compact read aliases map directly to the MCP bridge operation names: `current-view-elements`/`view-elements` to `get_current_view_elements`, `analyze`/`analyze-model` to `analyze_model`, `materials`/`material-quantities` to `get_material_quantities`, `rooms` to `get_rooms`, `schedules` to `get_schedules`, and `schedule-fields` to `get_schedule_fields`. Use `call <operation>` only when a named alias does not exist.

## Boundaries

- Do not use `revitctl` from pyRevit or Dynamo. Revit-hosted Python should use `integrations/python/revit_mcp_next_inprocess.py`.
- Do not bypass preview/apply for normal model edits. `revitctl create-project` is only for disposable fixture setup; `revitctl apply` requires `--confirm` or `confirm: true`.
- Use `revitctl cancel` only for queued bridge work; it is a support/debug command and may return a clean no-op when nothing matches or the target is already in-flight.
- Treat `revitctl` as an internal/support interface. Agent-facing automation should still use MCP because MCP exposes typed tools, descriptions, annotations, and output schemas.
