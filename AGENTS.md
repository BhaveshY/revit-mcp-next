# Agent Instructions

This repo contains Revit MCP Next, a Windows/Revit 2024 MCP bridge for Claude Code, Claude Desktop, Codex, and other MCP clients.

## Install Or Configure The MCP

Use [docs/agent-install.md](docs/agent-install.md) as the canonical install and AI-client setup guide.

Short version from a source checkout:

```powershell
npm install
npm run build
npm run build:addin
npm run install:windows -- -RevitYears 2024 -TrustRevitAlwaysLoad
npm run mcp:config
npm run doctor:clients
```

Short version from an extracted package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install-windows.ps1 -RevitYears 2024 -TrustRevitAlwaysLoad
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\print-mcp-config.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\doctor-clients.ps1
```

Never copy or print `config\auth.env`. Use the generated launcher/config snippets.

## Development Checks

Prefer the smallest check that covers the change:

```powershell
node scripts\validate-repo.mjs
npm run test:evidence:release:windows
npm run test:release:windows
npm run doctor:clients
```

For add-in or package changes, also run:

```powershell
npm run build
npm run build:addin
npm run test
npm run typecheck
```

Live Revit smoke mutates the active model. Use only disposable/test `.rvt` files.

## Revit Safety

- Revit 2024 is the only supported release target.
- MCP is the primary agent interface.
- `revitctl.cmd` is for diagnostics/support and scripted smoke.
- Writes must use preview/apply unless a tool is explicitly documented as setup-only.
- Do not automate Revit or Windows security prompts unless the user explicitly approves that exact prompt and package source.
- Do not claim public production signing unless release evidence proves public-trust signing for the exact package.
