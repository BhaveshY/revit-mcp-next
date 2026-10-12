import datetime
import json
import os
import sys
import traceback


def helper_dirs(here=None):
    """REVIT_MCP_NEXT_INTEGRATIONS, then integrations/python above this file, then <home>/integrations/python."""
    dirs = []
    explicit = os.environ.get("REVIT_MCP_NEXT_INTEGRATIONS")
    if explicit:
        dirs.append(explicit)
    if here:
        current = os.path.abspath(here)
        for _ in range(8):
            dirs.append(os.path.join(current, "integrations", "python"))
            parent = os.path.dirname(current)
            if parent == current:
                break
            current = parent
    home = os.environ.get("REVIT_MCP_NEXT_HOME") or os.path.join(os.path.expanduser("~"), ".revit-mcp-next")
    dirs.append(os.path.join(home, "integrations", "python"))
    return dirs


def add_helpers_to_path(module, here=None):
    for folder in helper_dirs(here):
        if os.path.exists(os.path.join(folder, module + ".py")):
            if folder not in sys.path:
                sys.path.insert(0, folder)
            return folder
    raise RuntimeError(
        "revit-mcp-next Python helpers not found. Set REVIT_MCP_NEXT_INTEGRATIONS to the integrations/python folder "
        "of the repo or release, or copy that folder to <home>/integrations/python (home: REVIT_MCP_NEXT_HOME or %USERPROFILE%/.revit-mcp-next)."
    )


def current_uiapp():
    import clr

    clr.AddReference("RevitServices")
    from RevitServices.Persistence import DocumentManager

    return DocumentManager.Instance.CurrentUIApplication


def write_failed_evidence(error):
    """The node failed before the shared helper ran: still leave evidence so a runner does not wait for a timeout."""
    path = os.environ.get("REVIT_MCP_NEXT_DYNAMO_EVIDENCE")
    evidence = {
        "schemaVersion": 2,
        "status": "failed",
        "host": "dynamo",
        "completedAtUtc": datetime.datetime.utcnow().replace(microsecond=0).isoformat() + "Z",
        "evidencePath": path,
        "error": {"message": str(error), "type": error.__class__.__name__, "traceback": traceback.format_exc()},
    }
    if path:
        parent = os.path.dirname(path)
        if parent and not os.path.exists(parent):
            os.makedirs(parent)
        with open(path, "w") as handle:
            json.dump(evidence, handle, indent=2, sort_keys=True)
    return evidence


try:
    add_helpers_to_path("revit_mcp_next_host_smoke")
    from revit_mcp_next_host_smoke import run_host_smoke

    OUT = run_host_smoke(
        current_uiapp(),
        "dynamo",
        evidence_path=os.environ.get("REVIT_MCP_NEXT_DYNAMO_EVIDENCE"),
        model_path=os.environ.get("REVIT_MCP_NEXT_DYNAMO_MODEL"),
        raise_on_failure=False,
    )
except Exception as error:
    OUT = write_failed_evidence(error)
