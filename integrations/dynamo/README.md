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
4. Paste the contents of `status_node.py` for diagnostics, `create_level_node.py` for a preview/apply write example, or `workflow_examples_node.py` for status, scoped read, catalog, room read, wall/floor/room preview, blocked preview, and optional family-placement preview examples. For release-candidate hosted integration evidence, open `revit_mcp_next_host_smoke.dyn` from the installed package and run the graph inside Dynamo for Revit. The nodes search for the installed in-process helper under the auth-config install root, `%LOCALAPPDATA%\RevitMcpNext`, and `%APPDATA%\Autodesk\Revit\Addins\2024\RevitMcpNext`.

Common installed helper path:

```text
%LOCALAPPDATA%\RevitMcpNext\integrations\python
```

The nodes return compact bridge data. For writes, build a change set, call
`preview_change_set`, inspect every change, then call `apply_preview` or
`apply_change_set` only when the preview is ready and explicitly confirmed.
`workflow_examples_node.py` previews model-changing operations without applying
them by default. Set `REVIT_MCP_NEXT_EXAMPLE_APPLY_WRITES=1` to apply the grid
sample and `REVIT_MCP_NEXT_EXAMPLE_APPLY_PLACEMENT=1` to apply family placement
when the model and add-in return a ready preview.

For release candidates, run `revit_mcp_next_host_smoke.dyn` against the installed package and set `REVIT_MCP_NEXT_DYNAMO_EVIDENCE` before launching Revit when you need a specific JSON output path. If Dynamo starts without an active document, set `REVIT_MCP_NEXT_DYNAMO_MODEL` to a disposable RVT. Archive the Dynamo evidence JSON, the adjacent `dynamo-preflight.json`, and `host-integrations-summary.json` together with the pyRevit host smoke result.

The packaged runner also writes a bounded preflight report next to the Dynamo evidence as `dynamo-preflight.json` during a normal host-smoke collection. The report records the Revit year, Dynamo version and `DynamoSettings.xml` path when discoverable, graph path, install root, evidence path, model path, and whether an existing settings file appears warmed. "Warmed" is only a read-only signal that an existing `DynamoSettings.xml` is present and parseable; it does not mean privacy consent was approved.

To collect only the report without launching Revit or running the graph:

```powershell
npm run smoke:dynamo-host -- -RevitYear 2024 -EvidencePath artifacts\host-integrations\raw\dynamo.json -PreflightOnly
```

The preflight and smoke runners do not change Dynamo privacy settings, preseed consent, or click Autodesk/Dynamo prompts. If Dynamo displays privacy or startup prompts, answer them manually in the intended dedicated test profile before collecting release evidence.

Use the packaged wrapper to launch Revit with those environment variables, wait for the graph output, and validate the raw JSON:

```powershell
npm run smoke:dynamo-host -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -EvidencePath artifacts\host-integrations\raw\dynamo.json -LaunchRevit
```

After Dynamo has been opened once in the target test profile and `DynamoSettings.xml` exists, the wrapper can run the packaged graph through a temporary Revit journal:

```powershell
npm run smoke:dynamo-host -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -EvidencePath artifacts\host-integrations\raw\dynamo.json -LaunchRevit -UseDynamoJournal
```

Journal mode refuses to run on an unwarmed Dynamo profile by default. It does not change privacy settings, preseed consent, or click startup prompts.
Use `-RequireWarmedDynamo` for unattended runs to fail fast when `DynamoSettings.xml` is missing or not parseable:

```powershell
npm run smoke:dynamo-host -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -EvidencePath artifacts\host-integrations\raw\dynamo.json -LaunchRevit -UseDynamoJournal -RequireWarmedDynamo
```

`-AllowUnwarmedDynamoJournal` is only for supervised disposable-machine experiments. It does not make a blocked first-run prompt acceptable release evidence.
If this node starts and then fails before the shared helper runs, it writes a failed `dynamo.json` to the configured evidence path so the wrapper can report the real setup error instead of waiting until timeout.

For full hosted integration evidence, prefer:

```powershell
npm run smoke:host-integrations -- -RevitYear 2024 -ModelPath C:\tmp\disposable.rvt -OutputRoot artifacts\host-integrations -SeedPyRevitHosts -LaunchRevitForDynamo
```

Headless `DynamoCLI.exe` output is not release evidence for this integration, because it does not load RevitServices.
