# 0.2.1 stabilization validation

The September 8 candidate fixes partial multi-instance discovery, selection reads
across Revit API document wrappers, and stale session generations after an
explicitly guarded apply. A successful edit advances only the matching session
target; delayed responses cannot regress its generation. External document
changes still require fresh target selection.

Native element counts now use GetElementCount without allocating all element
IDs. The installer uses content-addressed runtime folders so a candidate can be
installed while another Revit process still holds an older assembly. Existing
processes retain their loaded assembly until a normal restart. Identical file
copies are skipped, private ACL failures stop installation, and build scripts
locate a runnable .NET SDK rather than accepting a runtime-only dotnet command.

Validation on Windows, September 8, 2026:

- 74 broker tests, 8 Python integration tests, TypeScript build/typecheck, and
  repository validation passed.
- Named-pipe lifecycle, in-process bridge, Revit 2024 parameter, release package,
  and release evidence contracts passed.
- Revit 2024 and 2027 add-ins built against their separate native APIs.
- Revit 2024.3 build 24.3.30.11 passed the full disposable-model live smoke using
  exact instance and document targeting. Coverage includes preview/apply,
  creation, parameter edits, copy/move/pin/delete, sheet placement, readback,
  and rejection of invalid change hashes and identity guards.
- Tested loaded add-in SHA-256:
  `7d4194d31af9ee81c3441c2a5ff0b729f9400e1b235243b49af11bb9e9bc5f93`.

The test package is signed with an existing, locally trusted development
certificate. This is not public production signing. Revit 2021 was not available
for this run; 2027 has build evidence only. Live pyRevit/Dynamo and Model Delivery
acceptance are separate gates and are not established by this smoke.

Machine-readable local evidence: `revit-live-smoke-generation.json` and the
corresponding log in the sibling stabilization `evidence` directory.

MCP uses the official SDK v2 contract; the internal Revit bridge protocol is a
separate version. Do not infer MCP compatibility from the bridge version string.
