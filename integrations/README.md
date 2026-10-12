# Integrations: Python, pyRevit, Dynamo

Codex and Claude use the MCP server directly ([docs/clients.md](../docs/clients.md)). These folders are for your own
scripts. There are two ways in, and which one you need depends on where your code runs.

| Your code runs | Use | How it reaches Revit |
|---|---|---|
| Outside Revit (a script, a notebook, a scheduled job) | [`python/revit_mcp_next_client.py`](python/revit_mcp_next_client.py) | Starts `<home>\runtime\node.exe <home>\broker\revit-mcp.mjs` over stdio and calls the 37 MCP tools, exactly like Codex |
| Inside Revit (a pyRevit button, a Dynamo Python node) | [`python/revit_mcp_next_inprocess.py`](python/revit_mcp_next_inprocess.py) | Calls `RevitMcpNext.Addin.RevitMcpInProcessBridge` on Revit's UI thread: no pipe, no broker, no waiting on Revit |

Never start the MCP client from inside Revit: it would wait for Revit's UI thread, which your script is holding.
Never call the add-in's named pipes yourself; the broker owns auth, targeting and the confirm flow.

`<home>` is `REVIT_MCP_NEXT_HOME`, else `%USERPROFILE%\.revit-mcp-next` (the installer's default).

## Outside Revit: the MCP client

Python 3 (or 2.7), standard library only. Install revit-mcp-next first (README), then:

```python
import sys; sys.path.insert(0, r"C:\path\to\integrations\python")
from revit_mcp_next_client import RevitMcpClient

with RevitMcpClient() as revit:                       # home=..., or command=[node, bundle] for a dev build
    print(revit.call("status").text)
    r = revit.call("find_elements", {"category": "Walls", "count_only": True})
    print(r.status, r.code, r.json)                   # "ok" / "error" / "not_applied" / "running"
    if r.fix: print("fix:", r.fix[0])                 # the exact call to send next
```

`python revit_mcp_next_client.py status` or `python revit_mcp_next_client.py help '{"tool":"list"}'` prints one result.
Results are the server's text contract ([docs/tools.md](../docs/tools.md)): follow `next:` and `fix:`, ask a person
before you send `confirm` for a `NOT APPLIED` plan, and use `wait_job("j3")` for jobs.

## Inside Revit: the in-process bridge

```python
from revit_mcp_next_inprocess import call, data, status, create_level
print(status(__revit__)["data"]["snapshot"]["docs"])              # pyRevit: __revit__; Dynamo: see below
print(data(__revit__, "find_elements", {"category": ["Walls"], "count_only": True}))
create_level(__revit__, "Level X", 9000, preview=True)            # plan only; preview=False commits
```

- Requests are registry keys: `"<tool>.<op>"` (`"list.levels"`, `"create_elements.wall"`) or the bare tool for
  single-op tools (`"find_elements"`, `"get_view"`). Args are the tool's args without `op`/`kind`; lengths in mm.
  [docs/tools.md](../docs/tools.md) lists them all.
- Reads, `ui` ops and non-destructive writes work. Deletes, document and file operations, exports, `model_delivery`,
  dialog presses and `run_csharp` are refused with `UNSUPPORTED_OP` (reason `inproc`): they need the broker's confirm
  flow, so use the MCP tools for them.
- The call must come from Revit's UI thread (a pyRevit command or a Dynamo node is); jobs cannot finish
  synchronously and are refused.
- Failures raise `RevitMcpBridgeError` with `.code` (an ErrorCatalog code, see
  [docs/troubleshooting.md](../docs/troubleshooting.md)) and `.details`.
- The helper uses the add-in assembly Revit already loaded; if it is missing it falls back to the home's current
  payload (`<home>\addin\<year>\current.json`).

## Where the helpers are found

The pyRevit buttons and Dynamo nodes look for `integrations\python` in this order: `REVIT_MCP_NEXT_INTEGRATIONS`
(the folder itself), an `integrations\python` folder above the script (a repo checkout), then
`<home>\integrations\python`. The installer does not copy this folder; point the variable at your checkout or copy
the folder into the home.

- pyRevit: [pyrevit/README.md](pyrevit/README.md)
- Dynamo: [dynamo/README.md](dynamo/README.md)

Use disposable models for every sample: the write samples add levels, grids, walls and rooms.
