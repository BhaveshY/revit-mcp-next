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
        for year in ("2024",):
            dirs.append(os.path.join(app_data, "Autodesk", "Revit", "Addins", year, "RevitMcpNext", "integrations", "python"))

    return dirs


def add_installed_python_client_to_path():
    for python_dir in candidate_python_dirs():
        if os.path.exists(os.path.join(python_dir, "revit_mcp_next_workflow_examples.py")):
            if python_dir not in sys.path:
                sys.path.insert(0, python_dir)
            return python_dir
    raise RuntimeError("Unable to find installed Revit MCP Next workflow example helper.")


def add_revit_services_reference():
    try:
        import clr

        clr.AddReference("RevitServices")
    except Exception:
        pass


try:
    add_installed_python_client_to_path()
    add_revit_services_reference()

    from RevitServices.Persistence import DocumentManager
    from revit_mcp_next_workflow_examples import env_flag, run_workflow_examples

    uiapp = DocumentManager.Instance.CurrentUIApplication
    OUT = run_workflow_examples(
        uiapp,
        apply_writes=env_flag("REVIT_MCP_NEXT_EXAMPLE_APPLY_WRITES"),
        apply_placement=env_flag("REVIT_MCP_NEXT_EXAMPLE_APPLY_PLACEMENT"),
    )
except Exception as error:
    OUT = {"ok": False, "error": str(error), "type": error.__class__.__name__}
