# Contributing

This project is a clean-room rewrite. Do not copy code, handlers, schemas, or generated outputs from existing Revit MCP implementations.

Before opening a PR:

```powershell
npm install
npm run build
npm test
node scripts/validate-repo.mjs
```

For add-in work, build on a Windows machine with the matching Revit API installed. Do not commit Autodesk DLLs.

