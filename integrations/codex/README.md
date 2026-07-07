# Codex Integration

For the complete install flow, use [../../docs/agent-install.md](../../docs/agent-install.md). This page is only the Codex-specific config slice.

After running the installer, print the generated Codex config entry:

```powershell
npm run mcp:config -- -Client codex
```

Add the printed TOML to Codex user config. Treat the generated output as authoritative; do not hand-write release snippets. It has this shape:

```toml
[mcp_servers.revit-mcp-next]
command = "cmd"
args = ["/c", "C:\\Users\\YOUR_USER\\AppData\\Local\\RevitMcpNext\\launch-revit-mcp-next.cmd"]
```

The `config.example.toml` file in this folder is an example only. Prefer the generated TOML because it resolves the actual installed launcher.

The generated config points at the absolute installed launcher and does not print the local pipe auth token.

Common config locations checked by the client doctor are:

```text
%CODEX_HOME%\config.toml
%USERPROFILE%\.codex\config.toml
```

Validate the generated TOML, the installed Codex config when present, launcher quoting, and basic MCP startup/tool listing:

```powershell
npm run doctor:clients -- -Client codex
```

After updating Codex config, restart Codex, open Revit 2024 with a disposable project, and call `revit.status` from the MCP client.

The lightweight Codex plugin wrapper lives under `integrations/codex/plugins/revit-mcp-next`. It resolves `REVIT_MCP_NEXT_LAUNCHER`, `REVIT_MCP_NEXT_INSTALL_ROOT`, or the default Windows install roots and then delegates to the installed launcher.
