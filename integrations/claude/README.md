# Claude Integration

Preferred path after running the installer is the generated config command:

```powershell
npm run mcp:config -- -Client claude-code
```

That prints the command for the installed launcher without exposing the local auth token. Treat the generated output as authoritative; do not hand-write release snippets. The command has this shape:

```powershell
claude mcp add --scope user revit-mcp-next -- cmd /c "$env:LOCALAPPDATA\RevitMcpNext\launch-revit-mcp-next.cmd"
```

For Claude Desktop, generate the JSON entry:

```powershell
npm run mcp:config -- -Client claude-desktop
```

Merge the printed `mcpServers.revit-mcp-next` entry into `claude_desktop_config.json`.

The launcher must resolve to an absolute installed path. Do not use a network package runner, a relative command, or a bare runtime command in release snippets.

Validate the generated snippets and any installed Claude Desktop entry:

```powershell
npm run doctor:clients -- -Client claude-desktop
```
