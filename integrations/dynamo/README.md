# Dynamo Integration

Dynamo Python nodes should use the in-process .NET bridge when the node is
already running inside Revit. This avoids blocking Revit while waiting for an
`ExternalEvent`.

Plain Python processes outside Revit can use the stdlib MCP stdio client in
`integrations\python\revit_mcp_next_client.py`.

## Use In A Python Node

1. Install Revit MCP Next:

```powershell
npm run install:windows
```

2. Open Revit and load the Revit MCP Next add-in.
3. Add a Dynamo Python node.
4. Paste the contents of `status_node.py` for diagnostics or `create_level_node.py` for a preview/apply write example. For release-candidate hosted integration evidence, open `revit_mcp_next_host_smoke.dyn` from the installed package and run the graph inside Dynamo for Revit. The nodes search for the installed in-process helper under the auth-config install root, `%LOCALAPPDATA%\RevitMcpNext`, and `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext`.

Common installed helper path:

```text
%LOCALAPPDATA%\RevitMcpNext\integrations\python
```

The nodes return compact bridge data. For writes, build a change set, call
`preview_change_set`, inspect every change, then call `apply_preview` or
`apply_change_set` only when the preview is ready and explicitly confirmed.

For release candidates, run `revit_mcp_next_host_smoke.dyn` against the installed package and set `REVIT_MCP_NEXT_DYNAMO_EVIDENCE` before launching Revit when you need a specific JSON output path. If Dynamo starts without an active document, set `REVIT_MCP_NEXT_DYNAMO_MODEL` to a disposable RVT. Archive the Dynamo evidence JSON and include it in `host-integrations-summary.json` together with the pyRevit host smoke result.

Use the packaged wrapper to launch Revit with those environment variables, wait for the graph output, and validate the raw JSON:

```powershell
npm run smoke:dynamo-host -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -EvidencePath artifacts\host-integrations\raw\dynamo.json -LaunchRevit
```

For full hosted integration evidence, prefer:

```powershell
npm run smoke:host-integrations -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -OutputRoot artifacts\host-integrations -SeedPyRevitHosts -LaunchRevitForDynamo
```

Headless `DynamoCLI.exe` output is not release evidence for this integration, because it does not load RevitServices.
