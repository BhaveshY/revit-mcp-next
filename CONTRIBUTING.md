# Contributing

This project is a clean-room rewrite. Do not copy code, handlers, schemas, or generated outputs from existing Revit MCP implementations.

Before opening a PR:

```powershell
npm install
npm run build
npm run typecheck
node scripts/validate-repo.mjs
```

This repo does not keep unit tests. Behaviour is verified end-to-end against a real
Revit instance with `npm run smoke:revit` (disposable `.rvt` only) and, for pipe-host
changes, `npm run test:pipe-host`, which exercises the real named-pipe host.

For add-in work, build on a Windows machine with the matching Revit API installed. Do not commit Autodesk DLLs.

