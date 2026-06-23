from __future__ import print_function

import datetime
import json
import os
import sys


def add_installed_python_client_to_path():
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is not set.")
    python_dir = os.path.join(local_app_data, "RevitMcpNext", "integrations", "python")
    if python_dir not in sys.path:
        sys.path.insert(0, python_dir)


def format_blocked_preview(preview):
    changes = preview.get("changes") or []
    messages = []
    for change in changes:
        message = change.get("message") or change.get("status") or "blocked"
        messages.append("{0}: {1}".format(change.get("type") or "change", message))
    return "\n".join(messages) or "Preview was not ready."


add_installed_python_client_to_path()

from revit_mcp_next_inprocess import apply_preview, preview_change_set, status  # noqa: E402


stamp = datetime.datetime.utcnow().strftime("%Y%m%d%H%M%S")
status_response = status(__revit__)
status_data = status_response.get("data") or {}
active_document = status_data.get("activeDocument") or {}
document_fingerprint = active_document.get("fingerprint")
expected_generation = active_document.get("generation")

change_set = {
    "documentFingerprint": document_fingerprint,
    "expectedGeneration": expected_generation,
    "transactionName": "pyRevit MCP create level {0}".format(stamp),
    "operations": [
        {
            "id": "pyrevit-create-level",
            "type": "create_level",
            "name": "pyRevit MCP {0}".format(stamp),
            "elevation": {"value": 9000, "unit": "mm", "system": "metric"},
        }
    ],
}

preview = preview_change_set(__revit__, change_set)
if not preview.get("ready"):
    raise RuntimeError(format_blocked_preview(preview))

apply_result = apply_preview(__revit__, change_set, preview)

print(json.dumps({
    "preview": preview,
    "apply": apply_result,
}, indent=2, sort_keys=True))
