# Claude Integration

For the complete install flow, use [../../docs/agent-install.md](../../docs/agent-install.md). This page is only the Claude-specific config slice.

Preferred path after running the installer is the generated config command:

```powershell
npm run mcp:config -- -Client claude-code
```

That prints the command for the installed launcher without exposing the local auth token. Treat the generated output as authoritative; do not hand-write release snippets. The command has this shape:

```powershell
claude mcp add --scope user revit-mcp-next -- cmd /c "C:\Users\YOUR_USER\AppData\Local\RevitMcpNext\launch-revit-mcp-next.cmd"
```

The `.mcp.json` file in this folder is an example only. Prefer the generated command because it resolves the actual installed launcher.

For Claude Desktop, generate the JSON entry:

```powershell
npm run mcp:config -- -Client claude-desktop
```

Merge the printed `mcpServers.revit-mcp-next` entry into `claude_desktop_config.json`. Common path:

```text
%APPDATA%\Claude\claude_desktop_config.json
```

The launcher must resolve to an absolute installed path. Do not use a network package runner, a relative command, or a bare runtime command in release snippets.

Validate the generated snippets and any installed Claude Desktop entry:

```powershell
npm run doctor:clients -- -Client claude-code
npm run doctor:clients -- -Client claude-desktop
```

After updating Claude config, restart the client when needed, open Revit 2024 with a disposable project, and call `revit.status` from the MCP client.
