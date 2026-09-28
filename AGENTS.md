# Agent Instructions

This repo contains Revit MCP Next, a Windows/Revit MCP bridge for Claude Code,
Claude Desktop, Codex, and other MCP clients. Revit 2021 and 2024 use separate
.NET Framework 4.8 artifacts; Revit 2027 has a separate .NET 10 artifact. Each
year needs exact-package live-host evidence.

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

For a Revit 2021 or 2027 development install, build and select the matching artifact
explicitly:

```powershell
npm install
npm run build

# Revit 2021
npm run build:addin -- -RevitYear 2021 -RevitApiPath "C:\Program Files\Autodesk\Revit 2021"
npm run install:windows -- -RevitYears 2021 -TrustRevitAlwaysLoad

# Or Revit 2027
npm run build:addin -- -RevitYear 2027 -RevitApiPath "C:\Program Files\Autodesk\Revit 2027"
npm run install:windows -- -RevitYears 2027 -TrustRevitAlwaysLoad
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
npm run typecheck
npm run smoke:revit   # live, disposable .rvt only
```

Live Revit smoke mutates the active model. Use only disposable/test `.rvt` files.

## Revit Safety

- Revit 2021, 2024, and 2027 builds are separate; never point one year's manifest at
  the other year's DLL.
- Revit 2025 and 2026 are not supported.
- Treat Revit 2021 and 2027 as build/package support until live Revit, pyRevit, and
  Dynamo evidence from the exact package has passed.
- MCP is the primary agent interface.
- `revitctl.cmd` is for diagnostics/support and scripted smoke.
- Writes must use preview/apply unless a tool is explicitly documented as setup-only.
- Do not automate Revit or Windows security prompts unless the user explicitly approves that exact prompt and package source.
- Do not claim public production signing unless release evidence proves public-trust signing for the exact package.
