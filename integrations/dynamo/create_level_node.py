import datetime
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

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        dirs.append(os.path.join(local_app_data, "RevitMcpNext", "integrations", "python"))

    app_data = os.environ.get("APPDATA")
    if app_data:
        for year in ("2024", "2025", "2026"):
            dirs.append(os.path.join(app_data, "Autodesk", "Revit", "Addins", year, "RevitMcpNext", "integrations", "python"))

    return dirs


def add_installed_python_client_to_path():
    for python_dir in candidate_python_dirs():
        if os.path.exists(os.path.join(python_dir, "revit_mcp_next_inprocess.py")):
            if python_dir not in sys.path:
                sys.path.insert(0, python_dir)
            return python_dir
    raise RuntimeError("Unable to find installed Revit MCP Next Python integration helper.")


def add_revit_services_reference():
    try:
        import clr

        clr.AddReference("RevitServices")
    except Exception:
        pass


try:
    add_installed_python_client_to_path()
    add_revit_services_reference()

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
except Exception as error:
    OUT = {"ok": False, "error": str(error)}
