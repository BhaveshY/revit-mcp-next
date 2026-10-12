"""Workflow samples for the in-process bridge (pyRevit, Dynamo). Reads always run; writes run as previews unless
apply_writes is true. Use a disposable model.

Each step returns {"key", "ok", "summary"} or {"key", "ok": false, "code", "message"}; one failing step does not stop
the others. The registry keys and args are the same as the MCP tools' (docs/tools.md): "<tool>.<op>" plus the args
without "op"/"kind", lengths in mm.
"""

from __future__ import print_function

import datetime
import os
import traceback

from revit_mcp_next_inprocess import RevitMcpBridgeError, call, status


def env_flag(name):
    return (os.environ.get(name) or "").strip().lower() in ("1", "true", "yes", "on")


def _step(uiapp, key, args=None, mode=None):
    try:
        response = call(uiapp, key, args or {}, mode=mode)
        result = {"key": key, "ok": True, "summary": response.get("summary")}
        if mode:
            result["mode"] = mode
        created = (response.get("changes") or {}).get("created")
        if created:
            result["created"] = created
        return result
    except RevitMcpBridgeError as error:
        return {"key": key, "ok": False, "code": error.code, "message": str(error), "mode": mode}
    except Exception as error:
        return {"key": key, "ok": False, "code": "EXCEPTION", "message": str(error), "trace": traceback.format_exc()}


def _lowest_level(snapshot):
    active = snapshot.get("activeRid")
    for doc in snapshot.get("docs") or []:
        if doc.get("rid") == active and doc.get("levels"):
            return sorted(doc["levels"], key=lambda level: level[2])[0][1]  # [id, name, elevation mm]
    return "Level 1"


def run_workflow_examples(uiapp, apply_writes=False, apply_placement=False, door_type=None):
    """door_type: e.g. "Single-Flush: 0915 x 2134mm" (or env REVIT_MCP_NEXT_EXAMPLE_DOOR_TYPE) for the placement sample."""
    write_mode = "apply" if apply_writes else "preview"
    stamp = datetime.datetime.utcnow().strftime("%H%M%S")
    st = status(uiapp)
    level = _lowest_level((st.get("data") or {}).get("snapshot") or {})
    steps = []

    # Reads: the same keys the MCP tools use.
    steps.append(_step(uiapp, "list.levels"))
    steps.append(_step(uiapp, "list.views", {"view_type": "FloorPlan", "limit": 20}))
    steps.append(_step(uiapp, "list.types", {"category": ["Walls"], "limit": 20}))
    steps.append(_step(uiapp, "list.rooms", {"limit": 20}))
    steps.append(_step(uiapp, "find_elements", {"category": ["Walls"], "count_only": True}))
    steps.append(_step(uiapp, "find_elements", {"category": ["Doors"], "group_by": "type"}))
    steps.append(_step(uiapp, "check_model.stats"))
    steps.append(_step(uiapp, "get_view"))

    # Writes (previews unless apply_writes): a grid, a closed wall rectangle on the lowest level, a room inside it.
    steps.append(_step(uiapp, "create_elements.grid", {"start": [0, -2000], "end": [0, 12000], "name": "MCP-" + stamp}, write_mode))
    steps.append(
        _step(
            uiapp,
            "create_elements.wall",
            {"level": level, "points": [[20000, 0], [26000, 0], [26000, 4000], [20000, 4000]], "closed": True, "height": 3000},
            write_mode,
        )
    )
    steps.append(_step(uiapp, "create_elements.room", {"level": level, "at": [23000, 2000], "name": "MCP Room " + stamp}, write_mode))

    # A refused key shows the bridge's limits: deletes need the MCP broker's confirm flow (UNSUPPORTED_OP, reason inproc).
    steps.append(_step(uiapp, "modify_elements.delete", {"ids": [1]}))

    # Family placement into the rectangle's first wall, only when asked and a door type is named.
    door_type = door_type or os.environ.get("REVIT_MCP_NEXT_EXAMPLE_DOOR_TYPE")
    if apply_placement and door_type:
        steps.append(_step(uiapp, "place_family.place", {"type": door_type, "at": [23000, 0], "level": level}, write_mode))
    elif apply_placement:
        steps.append({"key": "place_family.place", "ok": True, "summary": "skipped: set REVIT_MCP_NEXT_EXAMPLE_DOOR_TYPE"})

    return {
        "addin": (st.get("data") or {}).get("hello"),
        "level": level,
        "writeMode": write_mode,
        "steps": steps,
        "passed": all(s["ok"] for s in steps if s["key"] != "modify_elements.delete"),
    }
