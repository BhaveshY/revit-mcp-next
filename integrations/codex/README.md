# Codex Integration

After running the installer, add this MCP server to Codex user config:

```toml
[mcp_servers.revit-mcp-next]
command = "cmd"
args = ["/c", "%LOCALAPPDATA%\\RevitMcpNext\\launch-revit-mcp-next.cmd"]
```

The initial plugin package will live under `integrations/codex/plugins/revit-mcp-next` once the tool contracts stabilize.

