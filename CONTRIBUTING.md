# Contributing

revit-mcp-next is a clean-room implementation. Do not copy code, command handlers, tool schemas or generated output
from other Revit MCP projects. Public Autodesk and MCP documentation and general ideas are fine.

## Setup

Windows 10/11 x64, Node 24+, the .NET 10 SDK, and Revit 2024 and/or 2027 installed (the build references their API
assemblies; never commit Autodesk DLLs).

```powershell
npm ci
npm run build:all
```

[AGENTS.md](AGENTS.md) has the full build notes (dotnet discovery, the Revit 2027 MSB3277 policy), dev installs into
your own home and the hard rules. They apply to people as much as to coding agents.

## Where changes go

- **A tool, op, parameter, example or error code:** the catalog in `contracts/src/catalog/tools/<tool>.ts` (and
  `contracts/src/errors.ts` for codes), then the broker module `broker/src/tools/<tool>.ts` and the add-in handler under
  `addin/RevitMcpNext.Addin/Domains/`. Run `npm run build`, `npm run gen:catalog -- --check` and
  `node scripts/gen-tool-docs.mjs` (it rewrites `docs/tools.md`, the README tool table, the error table in
  `docs/troubleshooting.md` and the skill's tool map). Keep the token budgets in `contracts/src/catalog/checks.ts`.
- **Help topics:** `contracts/src/catalog/topics.ts`; keep them in line with the docs.
- **Install and clients:** `installer/` and `broker/src/cli/`; describe user-visible changes in
  [docs/clients.md](docs/clients.md) and the README.
- **Skills:** `plugins/*/revit-mcp-next/skills/`; run `npm run validate:plugins`. Quote tool names, error codes and
  `next:`/`fix:` wording verbatim from the catalog so small models can follow them.

## Tests

No unit tests and no test doubles. Every behaviour is checked end to end against real Revit with disposable models
([docs/e2e.md](docs/e2e.md)). A change ships with the e2e scenario that exercises it (`e2e/scenarios/<ID>-<slug>.mjs`).

Before a pull request or push:

```powershell
npm run build
npm run typecheck
npm run gen:catalog -- --check
node scripts/gen-tool-docs.mjs --check
node e2e/run.mjs --profile surface
node e2e/run.mjs --home "$env:USERPROFILE\.revit-mcp-next-dev-<name>" --years 2024 --launch --only <your scenario IDs>
```

Add-in, IPC or loader changes also run on Revit 2027. Both add-in years must build with 0 errors.

## Style

- Small commits with a message that says what changed for the user or the model.
- Errors carry a code, the names and values involved, and a `fix:` the model can send verbatim.
- Results stay compact text; no silent caps (totals are exact, lists are paged).
- No hard-coded user names or machine paths.
