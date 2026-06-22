# revit-mcp-next

Clean-room Revit MCP bridge for Claude Code, Codex, and other MCP clients.

This repo intentionally does **not** fork the existing Revit MCP implementation. It keeps the useful architecture lesson, an external MCP broker plus an in-process Revit add-in, but rebuilds the contracts, transport, result shape, safety model, and installer from scratch.

## Goals

- Token-efficient tools with bounded `structuredContent`, output schemas, pagination, and short text summaries.
- Fast model queries using Revit-native collectors, projections, aggregates, and cursor-backed resources.
- Safe writes through preview/apply contracts, explicit transactions, no modal dialogs, and clear mutation reports.
- Frictionless local setup for Claude Code, Claude Desktop, and Codex Desktop on Windows.
- Correct by construction: no silent caps, no mixed units, no duplicated TS/C# schemas, no `int` element IDs.

## Architecture

```text
MCP client (Claude Code / Codex)
        |
        | stdio MCP
        v
broker/  - tool schemas, result shaping, policy, pagination
        |
        | framed local IPC, named pipe by default
        v
addin/   - Revit add-in, ExternalEvent queue, transactions
        |
        v
Autodesk Revit API
```

## Current Status

Initial scaffold:

- `contracts/`: shared protocol and tool-result TypeScript types.
- `broker/`: MCP server skeleton with first safe read tools and fake bridge tests.
- `addin/`: Revit add-in skeleton showing the intended queue/ExternalEvent/transaction boundaries.
- `installer/`: Windows bootstrap placeholder and integration docs.

`dotnet` is not currently available on this machine, so the C# add-in is scaffolded but not locally compiled yet. The TypeScript packages are intended to build and test locally.

## First Local Commands

```powershell
npm install
npm run build
npm test
```

## MVP Tool Surface

- `revit.status`
- `revit.list_documents`
- `revit.get_levels`
- `revit.query`
- `revit.get_elements`
- `revit.preview_change`
- `revit.apply_change`
- `revit.cancel`

The initial broker implements the first four against a bridge interface and fake bridge. Revit-backed behavior lands as the add-in IPC comes online.

