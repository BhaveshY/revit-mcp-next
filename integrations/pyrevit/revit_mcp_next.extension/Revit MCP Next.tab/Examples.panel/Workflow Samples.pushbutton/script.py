from __future__ import print_function

import json
import os
import sys


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


add_helpers_to_path("revit_mcp_next_workflow_examples", os.path.dirname(__file__))

from revit_mcp_next_workflow_examples import env_flag, run_workflow_examples  # noqa: E402

evidence = run_workflow_examples(
    __revit__,  # noqa: F821
    apply_writes=env_flag("REVIT_MCP_NEXT_EXAMPLE_APPLY_WRITES"),
    apply_placement=env_flag("REVIT_MCP_NEXT_EXAMPLE_APPLY_PLACEMENT"),
)
print(json.dumps(evidence, indent=2, sort_keys=True))
