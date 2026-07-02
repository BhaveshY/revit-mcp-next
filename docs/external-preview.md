# External Preview Sharing

This repo can be shared as an unsigned Revit 2024 external preview when the package is clearly labeled and shipped with evidence. Do not describe a build as a signed production release unless Authenticode verification evidence exists for that exact package.

## What To Share

For each preview build, attach:

- `revit-mcp-next-<version>-windows.zip`
- `CHECKSUMS.sha256`
- `release-manifest.json`
- release evidence bundle from `npm run evidence:release:windows`
- live Revit smoke output when available
- hosted pyRevit/Dynamo evidence when available, or the explicit skip reason from the evidence bundle

The preview is Revit 2024-only. Revit 2025/2026 support is intentionally blocked until year-specific add-in artifacts are built, packaged, and smoked.

## Recipient Install

1. Extract the package zip.
2. Run the packaged installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install-windows.ps1
```

3. Open Revit 2024 with a disposable or test project first.
4. If Revit shows an unsigned add-in prompt, choose the trust option only if the package and checksum came from the expected preview source.
5. Run diagnostics:

```powershell
npm run doctor:windows
npm run doctor:clients
```

If the recipient is installing from the packaged artifact without the repo checkout, they can run the installed launchers directly from the install root shown by the installer.

## Claude And Codex Setup

Generate token-safe client config from the installed discovery file:

```powershell
npm run mcp:config
npm run mcp:config -- -Client claude-code
npm run mcp:config -- -Client codex
```

Use the generated launcher path. Do not copy the local pipe auth token into Claude or Codex config.

## Debug CLI

The installed `revitctl.cmd` is the quick support check:

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" status --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" doctor --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" views --payload '{"limit":5}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" sheets --payload '{"limit":5}' --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" parameters --payload '{"filter":{"selectionOnly":true},"limit":5}' --pretty
```

If the installer used a non-default `-InstallRoot`, use that installed `revitctl.cmd` path instead.

## Current Useful Surface

The preview is useful for agent workflows that need:

- compact Revit status, document, level, view, sheet, room, selection, and active-view reads
- model statistics and material quantity reads
- parameter discovery before edits
- type/family/title-block/view-family/annotation catalog discovery
- guarded preview/apply writes for levels, grids, walls, floors, rooms, family placement, sheets, view placement, text notes, room tags, element tags, parameter changes, movement, rotation, copy, type changes, pinning, and guarded deletes
- pyRevit and Dynamo in-process examples for hosted automation

## Known Preview Limits

- No public CA/release certificate is assumed.
- Hosted pyRevit and Dynamo evidence should be collected for production release claims.
- Dimension creation, MEP line-based elements, structural framing systems, ceilings, roofs, and view override workflows are not implemented yet. Annotation support currently covers text notes, room tags, and independent element tags.
- Real-model destructive workflow coverage is still intentionally conservative. Use disposable models for preview testing.
