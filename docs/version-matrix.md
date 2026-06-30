# Revit Version Matrix

Initial target:

| Revit | Runtime | Status |
| --- | --- | --- |
| 2024 | .NET Framework 4.8 | Supported staged-package target |
| 2025 | .NET 8 | Blocked until year-specific add-in build/package output exists |
| 2026 | .NET 8 | Blocked until year-specific add-in build/package output exists |

The add-in should not promise one binary across Revit major versions. Revit 2024 and earlier need .NET Framework builds. Revit 2025+ uses modern .NET builds. Release packaging should produce one add-in artifact per supported Revit year.

Current guardrail: `scripts/package-release.ps1` and `installer/install-windows.ps1` reject `-RevitYears` values other than `2024`. This is intentional production hardening so a release candidate cannot silently install the Revit 2024/net48 add-in into Revit 2025 or 2026.

Do not vendor Autodesk API DLLs in this repository.
