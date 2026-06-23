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

The example command imports the in-process helper from the installed path:

```text
%LOCALAPPDATA%\RevitMcpNext\integrations\python\revit_mcp_next_inprocess.py
```

## Safety

pyRevit scripts should call read operations directly and should use
`preview_change_set` before `apply_change_set` for all mutations. Do not call
the named pipe from pyRevit; the in-process bridge shares the same bounded
operation dispatcher without queueing through `ExternalEvent`.
