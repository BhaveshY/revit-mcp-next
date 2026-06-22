# Revit Version Matrix

Initial target:

| Revit | Runtime | Status |
| --- | --- | --- |
| 2024 | .NET Framework 4.8 | MVP target |
| 2025 | .NET 8 | Planned |
| 2026 | .NET 8 | Planned |

The add-in should not promise one binary across Revit major versions. Revit 2024 and earlier need .NET Framework builds. Revit 2025+ uses modern .NET builds. Release packaging should produce one add-in artifact per supported Revit year.

Do not vendor Autodesk API DLLs in this repository.

