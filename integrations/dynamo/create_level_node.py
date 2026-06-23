import datetime
import os
import sys


local_app_data = os.environ.get("LOCALAPPDATA")
if not local_app_data:
    OUT = {"ok": False, "error": "LOCALAPPDATA is not set."}
else:
    client_dir = os.path.join(local_app_data, "RevitMcpNext", "integrations", "python")
    if client_dir not in sys.path:
        sys.path.insert(0, client_dir)

    try:
        from RevitServices.Persistence import DocumentManager
        from revit_mcp_next_inprocess import apply_preview, preview_change_set, status

        uiapp = DocumentManager.Instance.CurrentUIApplication
        status_response = status(uiapp)
        status_data = status_response.get("data") or {}
        active_document = status_data.get("activeDocument") or {}
        stamp = datetime.datetime.utcnow().strftime("%Y%m%d%H%M%S")
        change_set = {
            "documentFingerprint": active_document.get("fingerprint"),
            "expectedGeneration": active_document.get("generation"),
            "transactionName": "Dynamo MCP create level {0}".format(stamp),
            "operations": [
                {
                    "id": "dynamo-create-level",
                    "type": "create_level",
                    "name": "Dynamo MCP {0}".format(stamp),
                    "elevation": {"value": 9000, "unit": "mm", "system": "metric"},
                }
            ],
        }

        preview = preview_change_set(uiapp, change_set)
        if not preview.get("ready"):
            OUT = {"ok": False, "preview": preview}
        else:
            OUT = {"ok": True, "preview": preview, "apply": apply_preview(uiapp, change_set, preview)}
    except Exception as error:
        OUT = {"ok": False, "error": str(error)}
