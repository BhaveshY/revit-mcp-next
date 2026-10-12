# pyRevit extension

`revit_mcp_next.extension` adds a **Revit MCP Next** tab whose buttons call the add-in through the in-process bridge
([../README.md](../README.md)). They run on Revit's UI thread like any pyRevit command, so they never wait on the
broker or an ExternalEvent.

## Install

1. Install revit-mcp-next (README) and start Revit once so the add-in loads.
2. pyRevit > Settings > Custom extension directories: add this repo's `integrations\pyrevit` folder (or copy
   `revit_mcp_next.extension` into an existing extensions folder and set `REVIT_MCP_NEXT_INTEGRATIONS` to the
   `integrations\python` folder).
3. Reload pyRevit and open a **disposable** model.

## Buttons

| Panel | Button | What it does |
|---|---|---|
| Diagnostics | Status | Prints the add-in identity and the document snapshot (`status`) |
| Diagnostics | Host Smoke | Counts walls, lists levels, previews and creates one level; writes evidence JSON to `REVIT_MCP_NEXT_PYREVIT_EVIDENCE` or `<home>\logs\pyrevit-host-smoke-<time>.json` |
| Examples | Create Level | Previews, then creates a level "pyRevit MCP <time>" at 9000 mm |
| Examples | Workflow Samples | Reads (levels, views, wall types, rooms, counts, stats, active view) and previews a grid, a wall rectangle and a room. `REVIT_MCP_NEXT_EXAMPLE_APPLY_WRITES=1` commits them; `REVIT_MCP_NEXT_EXAMPLE_APPLY_PLACEMENT=1` with `REVIT_MCP_NEXT_EXAMPLE_DOOR_TYPE="Family: Type"` adds a door |

For an unattended `pyrevit run` of the host smoke, set `REVIT_MCP_NEXT_PYREVIT_MODEL` to a disposable `.rvt`; the
script opens it when no document is active.

## Rules

- Only keys the add-in marks `inproc` run here; deletes, file operations, exports and `run_csharp` are refused with
  `UNSUPPORTED_OP`. Use Codex or Claude (the MCP tools) for those.
- Never call the named pipes or start the MCP broker from a pyRevit command.
