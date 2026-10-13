## Summary

## Verification

- [ ] `npm run build`, `npm run typecheck`
- [ ] `npm run gen:catalog -- --check`, `node scripts/gen-tool-docs.mjs --check`
- [ ] `node e2e/run.mjs --profile surface`
- [ ] e2e scenarios against real Revit on a disposable model (behaviour changes)
- [ ] Both add-in years build with 0 errors (`scripts/build-addin.ps1 -RevitYear 2024` and `-RevitYear 2027`)

## Clean-Room Check

- [ ] I did not copy code or schemas from existing Revit MCP implementations.
- [ ] I did not vendor Autodesk/Revit API binaries.

