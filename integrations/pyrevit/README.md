# pyRevit Integration

The pyRevit path uses the in-process .NET bridge when a script is already
running inside Revit. This avoids deadlocking Revit by spawning the MCP broker
from a pyRevit command and waiting for an `ExternalEvent`.

Plain Python processes outside Revit can still use
`integrations\python\revit_mcp_next_client.py` to call the installed MCP
launcher over stdio.

## Install

1. Install Revit MCP Next normally:

```powershell
npm run install:windows
```

2. Add or copy `integrations\pyrevit\revit_mcp_next.extension` into a pyRevit
   extensions folder.

3. Reload pyRevit. Open Revit with the Revit MCP Next add-in loaded and an
   active project document.

The example commands search for the in-process helper under the auth-config install root, `%LOCALAPPDATA%\RevitMcpNext`, and `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext`.

Common installed helper path:

```text
%LOCALAPPDATA%\RevitMcpNext\integrations\python\revit_mcp_next_inprocess.py
```

Included commands:

- `Diagnostics.panel\Status.pushbutton`: calls `status` through the in-process bridge.
- `Diagnostics.panel\Host Smoke.pushbutton`: runs a compact hosted integration smoke, previews and applies a `create_level` operation, and writes JSON evidence. Set `REVIT_MCP_NEXT_PYREVIT_EVIDENCE` to choose the output path; otherwise evidence is written under the install root logs when discoverable.
- `Examples.panel\Create Level.pushbutton`: builds a `create_level` change set, previews it, checks that the preview is ready, then applies the exact preview token.

For release candidates, archive the pyRevit evidence JSON and include it in `host-integrations-summary.json` together with the Dynamo host smoke result.

When using `pyrevit run` for unattended evidence, set `REVIT_MCP_NEXT_PYREVIT_EVIDENCE` to the JSON output path. If the runner starts without an active document, also set `REVIT_MCP_NEXT_PYREVIT_MODEL` to the disposable RVT; the host-smoke command opens and activates it before running preview/apply. If pyRevit rejects a valid Revit 2024 model because its bundled host metadata is stale, seed the per-user host cache first:

```powershell
npm run pyrevit:hosts -- -Builds 20230106_1515,20241105_1515
```

The packaged release runner wraps those steps and validates the raw JSON:

```powershell
npm run smoke:pyrevit-host -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -EvidencePath artifacts\host-integrations\raw\pyrevit.json -SeedHostsCache
```

For full hosted integration evidence, prefer:

```powershell
npm run smoke:host-integrations -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -OutputRoot artifacts\host-integrations -SeedPyRevitHosts -LaunchRevitForDynamo
```

## Safety

pyRevit scripts should call read operations directly and should use
`preview_change_set` before `apply_change_set` for all mutations. Do not call
the named pipe from pyRevit; the in-process bridge shares the same bounded
operation dispatcher without queueing through `ExternalEvent`.
