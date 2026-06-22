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

Working local productionization slice:

- `contracts/`: shared protocol and tool-result TypeScript types plus JSON schema.
- `broker/`: MCP stdio server with bounded read/write tools, output schemas, structured errors, and bridge tests.
- `addin/`: Revit 2024 add-in with named-pipe IPC, cancellation-aware `ExternalEvent` queue, read handlers, and preview/apply write handlers.
- `installer/`: Windows installer that stages broker/contracts/add-in artifacts under `%LOCALAPPDATA%\RevitMcpNext`, writes the Revit `.addin` manifest, provisions a per-install pipe auth token under `config\auth.env`, and creates a Claude/Codex launcher.
- `scripts/package-release.ps1`: staged Windows release package with payload checksums and optional bundled production dependencies.
- `scripts/collect-support-bundle.ps1`: redacted support bundle for doctor output, logs, install metadata, and file hashes.

## First Local Commands

```powershell
npm install
npm run build
npm run build:addin
npm test
node scripts\validate-repo.mjs
npm run install:windows
npm run doctor:windows
npm run package:windows:dry-run
```

`npm run build:addin` expects Revit 2024 API DLLs at `C:\Program Files\Autodesk\Revit 2024`. Pass `-RevitApiPath` to `scripts\build-addin.ps1` if Revit is installed elsewhere.

## Packaging And Support

Create a staged Windows release package:

```powershell
npm run package:windows
```

The package lands in `artifacts\release`, includes `release-manifest.json` plus `CHECKSUMS.sha256`, and can be installed from the unpacked package by running `installer\install-windows.ps1`. See [release-packaging.md](docs/release-packaging.md).

Collect a redacted support bundle:

```powershell
npm run support:bundle
```

Windows installs generate a local 256-bit auth token in `%LOCALAPPDATA%\RevitMcpNext\config\auth.env` and restrict the file ACL to the installing user, Administrators, and SYSTEM when Windows allows it. The generated launcher reads that config and exports `REVIT_MCP_NEXT_AUTH_TOKEN` for the broker process. Doctor and support bundle output report token presence/shape only; support bundles redact the token value.

## MVP Tool Surface

- `revit.status`
- `revit.list_documents`
- `revit.get_levels`
- `revit.query`
- `revit.preview_change_set`
- `revit.apply_change_set`
- `revit.cancel_request`

Write tools are intentionally bounded. `revit.preview_change_set` validates `set_parameter` and `create_level` operations without mutation and returns a `previewId`; `revit.apply_change_set` requires that matching `previewId` plus `confirm: true` and applies the full change set in one named Revit transaction.

Production hardening still in progress: signed release artifacts, live Revit integration smoke on a Revit runner, broker/add-in enforcement of the provisioned pipe auth token, and broader write operation coverage.
