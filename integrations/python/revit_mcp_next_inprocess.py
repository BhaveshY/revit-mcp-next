"""In-process access to revit-mcp-next from code that already runs inside Revit (pyRevit commands, Dynamo nodes).

It calls RevitMcpNext.Addin.RevitMcpInProcessBridge directly on the Revit UI thread, with protocol v3 requests:
{"op": "<registry key>", "args": {...}, "mode": "apply" | "preview"}. No pipe, no token, no ExternalEvent wait.

Only registry keys with inproc:true are reachable (reads, ui ops and non-destructive writes such as
create_elements.level). Deletes, document/file operations, exports and run_csharp are refused with UNSUPPORTED_OP
(reason inproc): use the MCP tools through Codex or Claude, or revit_mcp_next_client.py from outside Revit.

Registry keys are "<tool>.<op>" ("list.levels", "create_elements.level") or the bare tool for single-op tools
("find_elements"); docs/tools.md lists them. Args are the tool's args without "op"/"kind"; lengths are mm.

Works on IronPython 2.7 (pyRevit) and CPython 3 (Dynamo, pyRevit CPython engine).
"""

from __future__ import print_function

import json
import os
import uuid

try:
    STRING_TYPES = (basestring,)  # noqa: F821
except NameError:
    STRING_TYPES = (str,)


BRIDGE_PROTOCOL_VERSION = "2026-10-01"
CLIENT_KEY = "in-process-python"


class RevitMcpBridgeError(RuntimeError):
    """A bridge response with ok=false. code, details and response keep the add-in's answer."""

    def __init__(self, key, response):
        self.key = key
        self.response = response or {}
        self.code = self.response.get("code") or "INTERNAL_ERROR"
        self.details = self.response.get("details") or {}
        message = self.response.get("message") or ("revit-mcp-next call failed: " + key)
        RuntimeError.__init__(self, "{0} ({1}): {2}".format(self.code, key, message))


def home_dir():
    """The runtime home: REVIT_MCP_NEXT_HOME, else %USERPROFILE%\\.revit-mcp-next."""
    explicit = os.environ.get("REVIT_MCP_NEXT_HOME")
    if explicit:
        return explicit
    return os.path.join(os.path.expanduser("~"), ".revit-mcp-next")


def revit_year():
    """2027 when running on .NET 10 (Revit 2027), else 2024."""
    try:
        import clr  # noqa: F401
        from System import Environment

        return 2027 if Environment.Version.Major >= 10 else 2024
    except Exception:
        return 2024


def installed_addin_path(home=None, year=None):
    """<home>\\addin\\<year>\\<payloadId>\\RevitMcpNext.Addin.dll from <home>\\addin\\<year>\\current.json."""
    home = home or home_dir()
    year = str(year or revit_year())
    current = os.path.join(home, "addin", year, "current.json")
    if not os.path.exists(current):
        return None
    with open(current, "r") as handle:
        payload_id = (json.load(handle) or {}).get("payloadId")
    if not payload_id:
        return None
    path = os.path.join(home, "addin", year, payload_id, "RevitMcpNext.Addin.dll")
    return path if os.path.exists(path) else None


def _loaded_bridge_type():
    """The bridge type from the add-in assembly Revit already loaded, or None."""
    try:
        from System import AppDomain

        for assembly in AppDomain.CurrentDomain.GetAssemblies():
            if assembly.GetName().Name == "RevitMcpNext.Addin":
                bridge = assembly.GetType("RevitMcpNext.Addin.RevitMcpInProcessBridge")
                if bridge is not None:
                    return bridge
    except Exception:
        pass
    return None


_BRIDGE = None


def load_bridge(addin_path=None):
    """RevitMcpInProcessBridge of the add-in loaded in this Revit (it must be the same assembly Revit runs)."""
    global _BRIDGE
    if _BRIDGE is not None and addin_path is None:
        return _BRIDGE

    import clr

    if addin_path is None and _loaded_bridge_type() is not None:
        clr.AddReference("RevitMcpNext.Addin")
    else:
        path = addin_path or installed_addin_path()
        if not path:
            raise RuntimeError(
                "revit-mcp-next is not loaded in this Revit and no payload was found under "
                + os.path.join(home_dir(), "addin")
                + ". Install it (installer\\install.ps1) and restart Revit."
            )
        try:
            clr.AddReferenceToFileAndPath(path)
        except AttributeError:
            clr.AddReference(path)

    from RevitMcpNext.Addin import RevitMcpInProcessBridge

    _BRIDGE = RevitMcpInProcessBridge
    return _BRIDGE


def status(uiapp, addin_path=None):
    """{"ok", "data": {"hello": add-in identity, "snapshot": open documents, active view, selection, ...}}."""
    bridge = load_bridge(addin_path)
    return json.loads(str(bridge.StatusJson(uiapp)))


def make_request(key, args=None, mode=None, doc=None):
    request = {
        "v": BRIDGE_PROTOCOL_VERSION,
        "requestId": uuid.uuid4().hex,
        "clientKey": CLIENT_KEY,
        "op": key,
        "args": args or {},
    }
    if mode:
        request["mode"] = mode
    if doc:
        request["doc"] = doc
    return request


def execute(uiapp, request, addin_path=None):
    """Sends one raw v3 request (dict or JSON text) and returns the response dict (never raises for ok=false)."""
    bridge = load_bridge(addin_path)
    if not isinstance(request, STRING_TYPES):
        request = json.dumps(request, separators=(",", ":"))
    return json.loads(str(bridge.ExecuteJson(uiapp, request)))


def call(uiapp, key, args=None, mode=None, doc=None, addin_path=None):
    """Runs a registry key and returns the full response; raises RevitMcpBridgeError when ok is false.

    mode: "preview" runs a write in a rolled-back scope and returns the plan; "apply" (default for writes) commits.
    doc: {"rid": <n>, "key": "<key>"} of one entry in status()["data"]["snapshot"]["docs"]; omitted = the active
    (or only) document.
    """
    response = execute(uiapp, make_request(key, args, mode, doc), addin_path=addin_path)
    if not response.get("ok"):
        raise RevitMcpBridgeError(key, response)
    return response


def data(uiapp, key, args=None, mode=None, doc=None, addin_path=None):
    """call() and return only response["data"]."""
    return call(uiapp, key, args, mode, doc, addin_path).get("data") or {}


# ------------------------------------------------------------------------------------------------ convenience reads


def levels(uiapp, **kwargs):
    return data(uiapp, "list.levels", {}, **kwargs)


def views(uiapp, view_type=None, **kwargs):
    return data(uiapp, "list.views", {"view_type": view_type} if view_type else {}, **kwargs)


def rooms(uiapp, **kwargs):
    return data(uiapp, "list.rooms", {}, **kwargs)


def count(uiapp, category, **kwargs):
    """Exact element count of one category ("Walls", "Doors", ...)."""
    return data(uiapp, "find_elements", {"category": [category], "count_only": True}, **kwargs)


def find(uiapp, category, limit=50, **kwargs):
    return data(uiapp, "find_elements", {"category": [category], "limit": limit}, **kwargs)


def model_stats(uiapp, **kwargs):
    return data(uiapp, "check_model.stats", {}, **kwargs)


# ------------------------------------------------------------------------------------------------ an example write


def create_level(uiapp, name, elevation_mm, preview=False, **kwargs):
    """Creates one level (with its plans). preview=True only plans it; nothing is committed."""
    return call(
        uiapp,
        "create_elements.level",
        {"elevation": elevation_mm, "name": name},
        mode="preview" if preview else "apply",
        **kwargs
    )
