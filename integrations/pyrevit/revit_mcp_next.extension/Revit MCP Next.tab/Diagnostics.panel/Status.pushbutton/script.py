from __future__ import print_function

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


add_installed_python_client_to_path()

from revit_mcp_next_inprocess import status  # noqa: E402


response = status(__revit__)

print(json.dumps(response, indent=2, sort_keys=True))
