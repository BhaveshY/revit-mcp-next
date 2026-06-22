# Claude Integration

Preferred Claude Code command after running the installer:

```powershell
claude mcp add --scope user revit-mcp-next -- cmd /c "$env:LOCALAPPDATA\RevitMcpNext\launch-revit-mcp-next.cmd"
```

For Claude Desktop, merge the server entry from `.mcp.json` into `claude_desktop_config.json`.

The launcher must resolve to an absolute installed path. Do not use a network package runner, a relative command, or a bare runtime command in release snippets.
