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
        from revit_mcp_next_inprocess import status

        uiapp = DocumentManager.Instance.CurrentUIApplication
        OUT = status(uiapp)
    except Exception as error:
        OUT = {"ok": False, "error": str(error)}
