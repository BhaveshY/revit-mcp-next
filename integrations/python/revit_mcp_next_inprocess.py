from __future__ import print_function

import json
import os

try:
    STRING_TYPES = (basestring,)  # noqa: F821
except NameError:
    STRING_TYPES = (str,)


def default_addin_path():
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is not set.")
    return os.path.join(local_app_data, "RevitMcpNext", "addin", "RevitMcpNext.Addin.dll")


def load_bridge(addin_path=None):
    import clr

    path = addin_path or default_addin_path()
    if os.path.exists(path):
        try:
            clr.AddReferenceToFileAndPath(path)
        except AttributeError:
            clr.AddReference(path)
    else:
        clr.AddReference("RevitMcpNext.Addin")

    from RevitMcpNext.Addin import RevitMcpInProcessBridge

    return RevitMcpInProcessBridge


def status(uiapp, addin_path=None):
    bridge = load_bridge(addin_path)
    return json.loads(str(bridge.StatusJson(uiapp)))


def execute(uiapp, request, addin_path=None):
    bridge = load_bridge(addin_path)
    if not isinstance(request, STRING_TYPES):
        request = json.dumps(request, separators=(",", ":"))
    return json.loads(str(bridge.ExecuteJson(uiapp, request)))
