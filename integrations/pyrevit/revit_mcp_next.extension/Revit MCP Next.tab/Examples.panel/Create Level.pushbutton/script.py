from __future__ import print_function

import datetime
import json
import os
import sys


def candidate_python_dirs():
    dirs = []
    auth_config = os.environ.get("REVIT_MCP_NEXT_AUTH_CONFIG")
    if auth_config:
        dirs.append(os.path.join(os.path.dirname(os.path.dirname(auth_config)), "integrations", "python"))

    install_root = os.environ.get("REVIT_MCP_NEXT_INSTALL_ROOT")
    if install_root:
        dirs.append(os.path.join(install_root, "integrations", "python"))

    current = os.path.abspath(os.path.dirname(__file__))
    for _ in range(8):
        candidate = os.path.join(current, "integrations", "python")
        if os.path.exists(os.path.join(candidate, "revit_mcp_next_inprocess.py")):
            dirs.append(candidate)
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        dirs.append(os.path.join(local_app_data, "RevitMcpNext", "integrations", "python"))

    app_data = os.environ.get("APPDATA")
    if app_data:
        for year in ("2024",):
            dirs.append(os.path.join(app_data, "Autodesk", "Revit", "Addins", year, "RevitMcpNext", "integrations", "python"))

    return dirs


def add_installed_python_client_to_path():
    for python_dir in candidate_python_dirs():
        if os.path.exists(os.path.join(python_dir, "revit_mcp_next_inprocess.py")):
            if python_dir not in sys.path:
                sys.path.insert(0, python_dir)
            return python_dir
    raise RuntimeError("Unable to find installed Revit MCP Next Python integration helper.")


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
