from __future__ import print_function

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
        for year in ("2024", "2025", "2026"):
            dirs.append(os.path.join(app_data, "Autodesk", "Revit", "Addins", year, "RevitMcpNext", "integrations", "python"))

    return dirs


def add_installed_python_client_to_path():
    for python_dir in candidate_python_dirs():
        if os.path.exists(os.path.join(python_dir, "revit_mcp_next_host_smoke.py")):
            if python_dir not in sys.path:
                sys.path.insert(0, python_dir)
            return python_dir
    raise RuntimeError("Unable to find installed Revit MCP Next Python integration helper.")


add_installed_python_client_to_path()

from revit_mcp_next_host_smoke import run_host_smoke  # noqa: E402


evidence_path = os.environ.get("REVIT_MCP_NEXT_PYREVIT_EVIDENCE")
model_path = os.environ.get("REVIT_MCP_NEXT_PYREVIT_MODEL")
evidence = run_host_smoke(__revit__, "pyrevit", evidence_path=evidence_path, model_path=model_path)

print(json.dumps(evidence, indent=2, sort_keys=True))
