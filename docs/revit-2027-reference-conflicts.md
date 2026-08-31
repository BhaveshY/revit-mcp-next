# Revit 2027 reference-conflict policy

Revit 2027's `RevitAPI.dll` and `RevitAPIUI.dll` dependency graph references `Microsoft.VisualBasic` 10.1 and `System.Drawing` 10.0. The .NET 10 Windows reference pack supplies primary `Microsoft.VisualBasic` 10.0 and `System.Drawing` 4.0 reference assemblies. MSBuild therefore reports MSB3277 while choosing the same primary framework assemblies the connector has already built and loaded against.

`scripts/build-addin.ps1` does not blanket-ignore this condition. Before each 2027 build it runs `ResolveReferences`, requires the conflict set to be exactly `Microsoft.VisualBasic` and `System.Drawing`, requires both Autodesk Revit API assemblies to appear in the dependency evidence, and checks the observed Autodesk-side versions. The build stops if the set or origin changes. Only after that gate passes is MSB3277 demoted for the final 2027 compilation.

This is a build-noise policy, not proof of full Revit 2027 production compatibility. The 2027 package still requires the controlled architect-PC pilot and exact-package live evidence.
