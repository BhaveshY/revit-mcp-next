# Dynamo nodes

Dynamo Python nodes run inside Revit, so they use the in-process bridge ([../README.md](../README.md)), never the MCP
broker. Use disposable models.

## Use in a Python node

1. Install revit-mcp-next (README) and start Revit so the add-in loads.
2. Set `REVIT_MCP_NEXT_INTEGRATIONS` to this repo's `integrations\python` folder before starting Revit, or copy that
   folder to `<home>\integrations\python` (`<home>` = `%USERPROFILE%\.revit-mcp-next` by default). Pasted node code
   has no file location, so it cannot search the repo.
3. Add a Python Script node (CPython3 or IronPython2) and paste one of:

| File | Output (`OUT`) |
|---|---|
| `status_node.py` | Add-in identity and document snapshot |
| `create_level_node.py` | Previews, then creates a level "Dynamo MCP <time>" at 9000 mm |
| `workflow_examples_node.py` | Read samples and previewed grid/wall/room writes (`REVIT_MCP_NEXT_EXAMPLE_APPLY_WRITES=1` commits them) |
| `host_smoke_node.py` | The host smoke (as in pyRevit); evidence JSON to `REVIT_MCP_NEXT_DYNAMO_EVIDENCE` or `<home>\logs\dynamo-host-smoke-<time>.json` |

`revit_mcp_next_host_smoke.dyn` is a ready graph with `host_smoke_node.py` in its single Python node. Set
`REVIT_MCP_NEXT_DYNAMO_MODEL` to a disposable `.rvt` if Dynamo starts without an open document.

In your own nodes:

```python
import clr
clr.AddReference("RevitServices")
from RevitServices.Persistence import DocumentManager
from revit_mcp_next_inprocess import data

uiapp = DocumentManager.Instance.CurrentUIApplication
OUT = data(uiapp, "list.levels")
```

Only keys the add-in marks `inproc` run in process; deletes, file operations, exports and `run_csharp` are refused
with `UNSUPPORTED_OP`. Dynamo's first-run privacy and trust prompts are yours to answer; nothing here answers them.
