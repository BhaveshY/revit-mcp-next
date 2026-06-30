# Codex Integration

After running the installer, print the generated Codex config entry:

```powershell
npm run mcp:config -- -Client codex
```

Add the printed TOML to Codex user config. Treat the generated output as authoritative; do not hand-write release snippets. It has this shape:

```toml
[mcp_servers.revit-mcp-next]
command = "cmd"
args = ["/c", "%LOCALAPPDATA%\\RevitMcpNext\\launch-revit-mcp-next.cmd"]
```

The generated config points at the absolute installed launcher and does not print the local pipe auth token.

Validate the generated TOML, the installed Codex config when present, launcher quoting, and basic MCP startup/tool listing:

```powershell
npm run doctor:clients -- -Client codex
```

The initial plugin package will live under `integrations/codex/plugins/revit-mcp-next` once the tool contracts stabilize.
