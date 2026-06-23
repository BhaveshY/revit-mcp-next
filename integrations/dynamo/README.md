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
4. Paste the contents of `status_node.py`, or import the installed in-process helper from:

```text
%LOCALAPPDATA%\RevitMcpNext\integrations\python
```

The node returns compact bridge data. For writes, build a change set, call
`preview_change_set`, inspect every change, then call `apply_change_set` only
when the preview is ready and explicitly confirmed.
