# Revit Version Matrix

Revit MCP Next produces a separate add-in artifact for each supported Revit
major version. A successful compile/package is not, by itself, evidence that a
release is ready for production use in that Revit host.

| Revit | Runtime / target | Build and package status | Host-validation status |
| --- | --- | --- | --- |
| 2021 | .NET Framework 4.8 / `net48` | First-class staged-package target | Compatibility build passed against 2021.1 reference assemblies; exact-package live-host evidence is still required |
| 2024 | .NET Framework 4.8 / `net48` | First-class staged-package target | Live-smoke workflow; exact-package release evidence is required for each published package |
| 2025 | .NET 8 / `net8.0-windows` | Not supported | Not validated |
| 2026 | .NET 8 / `net8.0-windows` | Not supported | Not validated |
| 2027 | .NET 10 / `net10.0-windows` | First-class staged-package target | Live-smoke workflow; exact-package release evidence is required for each published package |

All supported years use the same private Revit bridge protocol, tool surface, safety model,
live-smoke requirements, and release-readiness gates. Do not label a package
production-ready until its year-specific DLL and hosted integrations have passed
the self-hosted workflows and the evidence has been archived.

## Build Inputs And Outputs

Use the API assemblies from the matching installed Revit release. Autodesk API
DLLs are not vendored in this repository.

```powershell
# Revit 2021
npm run build:addin -- -RevitYear 2021 `
  -RevitApiPath "C:\Program Files\Autodesk\Revit 2021"

# Revit 2024
npm run build:addin -- -RevitYear 2024 `
  -RevitApiPath "C:\Program Files\Autodesk\Revit 2024"

# Revit 2027
npm run build:addin -- -RevitYear 2027 `
  -RevitApiPath "C:\Program Files\Autodesk\Revit 2027"
```

Year-specific outputs are written below `artifacts\addin\<year>\`. Release
packages preserve that boundary below `payload\addin\<year>\`, and the installer
selects the matching artifact for each requested `-RevitYears` value. Never
reuse a DLL from any other Revit year in a manifest.

The 2027 development baseline used for this compatibility work was Autodesk
Revit 2027.2:

- `RevitAPI.dll` and `RevitAPIUI.dll` file version `27.2.0.39`
- API assembly version `27.2.0.0`
- .NET 10 SDK available on the build machine

Patch numbers are evidence, not a hardcoded compatibility check. The important
build rule is that `-RevitYear`, the API directory, the target framework, and
the packaged artifact year must agree.

## Installation Locations

The installer uses the supported per-user manifest location:

```text
%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin
```

That location is valid for 2021, 2024, and 2027. Revit 2027 no longer loads
third-party all-user manifests from `%ProgramData%`. If an all-user installation
mode is added later, its 2027 manifest must be placed below
`%ProgramFiles%\Autodesk\Revit\Addins\2027`.

## Validation Required Before Expanding Claims

For every supported year:

1. Build against that year's installed `RevitAPI.dll` and `RevitAPIUI.dll`.
2. Verify the package contains `payload\addin\<year>` and its manifest records
   only years with matching payloads.
3. Install from the staged package and verify client discovery maps the year to
   the same installed DLL.
4. Cold-start the matching Revit release with a disposable `.rvt` file.
5. Run broker/add-in status, read, preview/apply, restart, support-bundle, and
   release-evidence checks.
6. Run the hosted pyRevit and Dynamo evidence paths, or record explicit skip
   reasons for an external preview. Release-candidate and production claims
   require passed hosted-integration evidence.

Revit 2025 and 2026 remain excluded even though both use .NET 8. They need their
own API references, artifacts, installer/package coverage, and host evidence;
the existence of a modern-.NET build for 2027 does not imply compatibility with
either release.
