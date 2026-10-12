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


try:
    add_helpers_to_path("revit_mcp_next_workflow_examples")
    from revit_mcp_next_workflow_examples import env_flag, run_workflow_examples

    OUT = run_workflow_examples(
        current_uiapp(),
        apply_writes=env_flag("REVIT_MCP_NEXT_EXAMPLE_APPLY_WRITES"),
        apply_placement=env_flag("REVIT_MCP_NEXT_EXAMPLE_APPLY_PLACEMENT"),
    )
except Exception as error:
    OUT = {"ok": False, "error": str(error), "trace": traceback.format_exc()}
