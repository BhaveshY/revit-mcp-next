from __future__ import print_function

import datetime
import os
import traceback

from revit_mcp_next_inprocess import apply_preview, execute_operation, preview_change_set, status


def utc_stamp():
    return datetime.datetime.utcnow().strftime("%Y%m%d%H%M%S")


def compact_dict(values):
    result = {}
    for key, value in values.items():
        if value is not None:
            result[key] = value
    return result


def unit_mm(value):
    return {"value": value, "unit": "mm", "system": "metric"}


def point2(x, y):
    return {"x": unit_mm(x), "y": unit_mm(y)}


def point3(x, y, z):
    return {"x": unit_mm(x), "y": unit_mm(y), "z": unit_mm(z)}


def active_document_guard(active_document, payload=None):
    guarded = dict(payload or {})
    if active_document.get("fingerprint"):
        guarded["documentFingerprint"] = active_document.get("fingerprint")
    if active_document.get("generation") is not None:
        guarded["expectedGeneration"] = active_document.get("generation")
    return guarded


def change_set(active_document, transaction_name, operations):
    return active_document_guard(
        active_document,
        {
            "transactionName": transaction_name,
            "operations": operations,
        },
    )


def items_from_page(value):
    if isinstance(value, list):
        return value
    if isinstance(value, dict):
        for key in ("items", "rooms", "levels", "data"):
            items = value.get(key)
            if isinstance(items, list):
                return items
    return []


def first_item(value):
    items = items_from_page(value)
    if items:
        return items[0]
    return None


def format_blocked_preview(preview):
    changes = preview.get("changes") or []
    messages = []
    for change in changes:
        message = change.get("message") or change.get("status") or "blocked"
        messages.append("{0}: {1}".format(change.get("type") or "change", message))
    return "\n".join(messages) or "Preview was not ready."


def safe_execute(evidence, bucket, key, uiapp, operation, payload, addin_path=None):
    try:
        result = execute_operation(uiapp, operation, payload=payload, addin_path=addin_path)
        evidence[bucket][key] = {"ok": True, "result": result}
        return result
    except Exception as error:
        evidence[bucket][key] = {
            "ok": False,
            "error": str(error),
            "type": error.__class__.__name__,
        }
        return None


def preview_and_optionally_apply(evidence, bucket, key, uiapp, active_document, preview_payload, apply_writes, addin_path=None):
    record = {
        "ok": False,
        "applyRequested": bool(apply_writes),
        "previewReady": False,
        "preview": None,
        "apply": None,
        "blockedReason": None,
        "error": None,
    }
    evidence[bucket][key] = record

    try:
        preview = preview_change_set(uiapp, preview_payload, addin_path=addin_path)
        record["preview"] = preview
        record["previewReady"] = bool(preview.get("ready"))
        if not preview.get("ready"):
            record["blockedReason"] = format_blocked_preview(preview)
            record["ok"] = True
            return record

        if apply_writes:
            record["apply"] = apply_preview(uiapp, preview_payload, preview, addin_path=addin_path)

        record["ok"] = True
        return record
    except Exception as error:
        record["error"] = {
            "message": str(error),
            "type": error.__class__.__name__,
            "traceback": traceback.format_exc(),
        }
        return record


def choose_family_symbol(family_catalog):
    items = items_from_page(family_catalog)
    if not items:
        return None

    preferred_terms = ("Furniture", "Generic", "Door", "Window")
    for term in preferred_terms:
        for item in items:
            category = str(item.get("category") or "")
            family = str(item.get("familyName") or "")
            name = str(item.get("name") or "")
            if term.lower() in (category + " " + family + " " + name).lower():
                return item

    return items[0]


def symbol_needs_wall_host(symbol):
    text = " ".join([
        str(symbol.get("category") or ""),
        str(symbol.get("familyName") or ""),
        str(symbol.get("name") or ""),
    ]).lower()
    return "door" in text or "window" in text


def run_workflow_examples(uiapp, apply_writes=False, apply_placement=False, addin_path=None):
    stamp = utc_stamp()
    evidence = {
        "schemaVersion": 1,
        "status": "passed",
        "applyWrites": bool(apply_writes),
        "applyPlacement": bool(apply_placement),
        "statusResponse": None,
        "activeDocument": None,
        "reads": {},
        "writes": {},
        "optionalPlacement": {},
        "blockedPreview": {},
        "notes": [],
    }

    try:
        status_response = status(uiapp, addin_path=addin_path)
        evidence["statusResponse"] = status_response
        status_data = status_response.get("data") or {}
        active_document = status_data.get("activeDocument") or {}
        evidence["activeDocument"] = active_document
    except Exception as error:
        evidence["status"] = "failed"
        evidence["statusResponse"] = {
            "ok": False,
            "error": str(error),
            "type": error.__class__.__name__,
            "traceback": traceback.format_exc(),
        }
        return evidence

    levels = safe_execute(
        evidence,
        "reads",
        "levels",
        uiapp,
        "get_levels",
        active_document_guard(active_document),
        addin_path=addin_path,
    )
    level = first_item(levels)

    safe_execute(
        evidence,
        "reads",
        "currentViewElements",
        uiapp,
        "get_current_view_elements",
        active_document_guard(
            active_document,
            {
                "preset": "summary",
                "fields": ["id", "uniqueId", "category", "class", "name", "typeId", "levelId"],
                "limit": 5,
                "includeTotalCount": True,
            },
        ),
        addin_path=addin_path,
    )

    rooms = safe_execute(
        evidence,
        "reads",
        "rooms",
        uiapp,
        "get_rooms",
        active_document_guard(
            active_document,
            {
                "preset": "schedule",
                "fields": ["id", "number", "name", "levelId", "levelName", "area", "department", "isPlaced", "isEnclosed"],
                "limit": 5,
                "includeTotalCount": True,
                "includeUnplaced": False,
            },
        ),
        addin_path=addin_path,
    )

    wall_types = safe_execute(
        evidence,
        "reads",
        "wallTypeCatalog",
        uiapp,
        "catalog",
        active_document_guard(
            active_document,
            {
                "kind": "elementTypes",
                "filter": {"classes": ["WallType"], "categories": ["OST_Walls"]},
                "preset": "compact",
                "limit": 5,
                "includeTotalCount": True,
            },
        ),
        addin_path=addin_path,
    )
    wall_type = first_item(wall_types)

    floor_types = safe_execute(
        evidence,
        "reads",
        "floorTypeCatalog",
        uiapp,
        "catalog",
        active_document_guard(
            active_document,
            {
                "kind": "elementTypes",
                "filter": {"classes": ["FloorType"], "categories": ["OST_Floors"]},
                "preset": "compact",
                "limit": 5,
                "includeTotalCount": True,
            },
        ),
        addin_path=addin_path,
    )
    floor_type = first_item(floor_types)

    family_symbols = safe_execute(
        evidence,
        "reads",
        "familySymbolCatalog",
        uiapp,
        "catalog",
        active_document_guard(
            active_document,
            {
                "kind": "familySymbols",
                "filter": {"categories": ["OST_Furniture", "OST_Doors", "OST_Windows"]},
                "preset": "placement",
                "limit": 10,
                "includeTotalCount": True,
            },
        ),
        addin_path=addin_path,
    )
    family_symbol = choose_family_symbol(family_symbols)

    walls = safe_execute(
        evidence,
        "reads",
        "wallHostQuery",
        uiapp,
        "query",
        active_document_guard(
            active_document,
            {
                "filter": {"categories": ["OST_Walls"]},
                "fields": ["id", "uniqueId", "category", "class", "name", "levelId"],
                "preset": "summary",
                "limit": 1,
                "includeTotalCount": True,
            },
        ),
        addin_path=addin_path,
    )
    wall_host = first_item(walls)

    if level:
        level_id = str(level.get("id"))
        grid_change_set = change_set(
            active_document,
            "pyRevit/Dynamo MCP grid sample {0}".format(stamp),
            [
                {
                    "id": "workflow-create-grid",
                    "type": "create_grid",
                    "name": "MCP-WF-{0}".format(stamp),
                    "start": point3(0, 12000, 0),
                    "end": point3(6000, 12000, 0),
                }
            ],
        )
        preview_and_optionally_apply(evidence, "writes", "gridPreviewApply", uiapp, active_document, grid_change_set, apply_writes, addin_path=addin_path)

        wall_operations = [
            compact_dict({
                "id": "workflow-create-wall",
                "type": "create_wall",
                "levelId": level_id,
                "wallTypeId": str(wall_type.get("id")) if wall_type else None,
                "start": point3(0, 0, 0),
                "end": point3(5000, 0, 0),
                "height": unit_mm(3000),
            })
        ]
        preview_and_optionally_apply(
            evidence,
            "writes",
            "wallPreview",
            uiapp,
            active_document,
            change_set(active_document, "MCP wall preview sample {0}".format(stamp), wall_operations),
            False,
            addin_path=addin_path,
        )

        floor_operations = [
            compact_dict({
                "id": "workflow-create-floor",
                "type": "create_floor",
                "levelId": level_id,
                "floorTypeId": str(floor_type.get("id")) if floor_type else None,
                "outline": [
                    point3(0, 0, 0),
                    point3(5000, 0, 0),
                    point3(5000, 3500, 0),
                    point3(0, 3500, 0),
                ],
            })
        ]
        preview_and_optionally_apply(
            evidence,
            "writes",
            "floorPreview",
            uiapp,
            active_document,
            change_set(active_document, "MCP floor preview sample {0}".format(stamp), floor_operations),
            False,
            addin_path=addin_path,
        )

        room_operations = [
            {
                "id": "workflow-create-room",
                "type": "create_room",
                "levelId": level_id,
                "location": point2(1500, 1500),
                "name": "MCP Workflow Room {0}".format(stamp),
                "number": "MCP-{0}".format(stamp[-6:]),
                "department": "MCP",
            }
        ]
        preview_and_optionally_apply(
            evidence,
            "writes",
            "roomPreview",
            uiapp,
            active_document,
            change_set(active_document, "MCP room preview sample {0}".format(stamp), room_operations),
            False,
            addin_path=addin_path,
        )

        if family_symbol:
            placement_operation = compact_dict({
                "id": "workflow-place-family-instance",
                "type": "place_family_instance",
                "familySymbolId": str(family_symbol.get("id")),
                "hostElementId": str(wall_host.get("id")) if wall_host and symbol_needs_wall_host(family_symbol) else None,
                "levelId": level_id,
                "location": point3(1800, 500, 0),
                "rotation": {"value": 0, "unit": "degrees"},
            })
            preview_and_optionally_apply(
                evidence,
                "optionalPlacement",
                "familyInstancePreview",
                uiapp,
                active_document,
                change_set(active_document, "MCP family placement sample {0}".format(stamp), [placement_operation]),
                apply_placement,
                addin_path=addin_path,
            )
        else:
            evidence["optionalPlacement"]["familyInstancePreview"] = {
                "ok": True,
                "skipped": True,
                "reason": "No family symbols were returned by revit.catalog.",
            }
    else:
        evidence["notes"].append("No level was available, so write previews that require a level were skipped.")

    blocked_change_set = change_set(
        active_document,
        "MCP blocked preview sample {0}".format(stamp),
        [
            {
                "id": "workflow-blocked-move",
                "type": "move_element",
                "translation": point3(100, 0, 0),
            }
        ],
    )
    preview_and_optionally_apply(
        evidence,
        "blockedPreview",
        "missingElementId",
        uiapp,
        active_document,
        blocked_change_set,
        False,
        addin_path=addin_path,
    )

    if rooms is not None:
        evidence["notes"].append("Room read returned {0} item(s).".format(len(items_from_page(rooms))))

    return evidence


def env_flag(name):
    return str(os.environ.get(name) or "").strip().lower() in ("1", "true", "yes", "on")
