# Architect-PC controlled pilot — Revit 2024/2027

Use this RC only on a designated pilot PC and copied, non-production RVTs. Never select a live central model, a live local working copy, or an Autodesk Docs production model as a delivery source. Revit MCP Next produces separate standalone issue files; it must not modify the source RVTs.

## 1. Prepare and install

1. Copy the approved RC zip and its `.sha256` file to the pilot PC. Compare the zip SHA-256 with the value supplied by the developer, extract it locally, and keep the extracted folder unchanged.
2. Close Revit. In PowerShell, validate and install the extracted package. The install records the previous connector/add-in state for rollback, verifies every packaged file, opts the known add-in ID into Revit **Always Load** for this pilot, and verifies both Revit-year installations:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\pilot-install.ps1 -Action ValidatePackage -PackageRoot . -RevitYears 2024,2027
   powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\pilot-install.ps1 -Action Install -PackageRoot . -RevitYears 2024,2027
   ```

   This RC is unsigned and not publicly trusted. `Always Load` is an explicit pilot opt-in, not a substitute for production Authenticode signing.
3. Check the recorded version, add-in files, and trust state at any time:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\pilot-install.ps1 -Action Status -RevitYears 2024,2027
   ```

## 2. Select the project safely in Codex

Open only a copied pilot/control RVT in the intended Revit process. Ask: **“Show open Revit instances and projects.”** Then: **“Work on this project”** using the exact instance and document fingerprint Codex lists. Confirm every response names the intended Revit version/process, instance ID, document fingerprint, generation, file path, and—when workshared—central path. Stop if any identity is unexpected.

## 3. First Model Delivery run

Tell Codex which copied source RVTs and delivery folder to use. Codex inspects the files and asks only unresolved project decisions: output names/roles, protected opening views, link exceptions, cleanup, exports, and QA limit. Review the preview before approval. It must show:

- the selected Revit target identity;
- every source path and SHA-256;
- standalone/non-central outputs, link actions and preserved transforms;
- exact cleanup counts, exports, blockers, warnings, staging path, and final package path.

Approve only that preview. Codex executes its single-use token; switching/closing the target, changing a source, changing the target generation, or restarting Revit requires a new preview.

## 4. Inspect the result

Open only the delivered copies. Check that each RVT is standalone and has no central path; links are relative and correctly positioned; protected start views remain; approved sheets/views/templates/filters were removed; required IFC/DWG/NWC files exist; warning limits pass; and `manifest.json` plus `audit.json` identify source/output hashes. The architect—not the connector—accepts the issued package.

## 5. Stop or roll back

Close Revit first. Choose one action: `Uninstall` removes the pilot and trust entry but retains the recorded backup; `Rollback` removes the pilot and immediately restores the exact pre-pilot connector/add-in/trust state:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\pilot-install.ps1 -Action Uninstall -RevitYears 2024,2027
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\pilot-install.ps1 -Action Rollback -RevitYears 2024,2027
```

Do not promote the RC after one successful run. Record the package version/SHA, Revit year/build, target identity, copied inputs, duration, output checks, any manual corrections, failures, audit paths, and architect approval. Production release remains gated on the pilot evidence and a real trusted signing certificate.
