# Security

Revit MCP Next can inspect and modify active Revit models. Treat it as a local automation tool with write access to valuable project files.

Report security issues privately to the repository owner.

Current security design:

- Local-only broker/add-in communication.
- Named-pipe IPC planned with same-user restrictions.
- No arbitrary code execution in normal mode.
- Destructive operations require preview/apply contracts.
- Automation paths must not show modal dialogs.

