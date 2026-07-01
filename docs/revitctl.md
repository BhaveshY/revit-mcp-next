# revitctl

`revitctl` is the internal Revit MCP Next bridge CLI for debugging, support, and scripted smoke checks.

MCP remains the main Claude/Codex interface. Use `revitctl` when you want a deterministic shell command that proves whether the installed Revit add-in bridge is reachable without starting an MCP client.

## Installed Usage

After `npm run install:windows`:

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" status --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" doctor --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" readiness --pretty
```

The installed launcher reads:

- `%LOCALAPPDATA%\RevitMcpNext\config\client-discovery.json`
- `%LOCALAPPDATA%\RevitMcpNext\config\auth.env`

It forwards the local pipe auth token to the add-in but does not print the token.

## Common Commands

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" views --payload '{"filter":{"isTemplate":false},"limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" sheets --payload '{"includePlacedViews":true,"limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" query --payload '{"filter":{"selectionOnly":true},"preset":"summary","limit":10}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" parameters --payload '{"filter":{"selectionOnly":true},"includeTypeParameters":true,"limit":5}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" catalog --payload '{"kind":"familySymbols","preset":"placement","limit":10}' --pretty
```

Preview/apply stays guarded:

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" preview .\change-set.json --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" apply .\apply-payload.json --confirm --pretty
```

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

## Boundaries

- Do not use `revitctl` from pyRevit or Dynamo. Revit-hosted Python should use `integrations/python/revit_mcp_next_inprocess.py`.
- Do not bypass preview/apply for writes. `revitctl apply` requires `--confirm` or `confirm: true`.
- Treat `revitctl` as an internal/support interface. Agent-facing automation should still use MCP because MCP exposes typed tools, descriptions, annotations, and output schemas.
