# Tooling Roadmap

This roadmap tracks high-value Revit workflow tools that are not yet part of the release-candidate surface. The project should add these as typed, bounded operations rather than broad catch-all tools.

## Release States

- `available`: implemented, documented, and covered by tests or live smoke.
- `rc-backlog`: useful for the next release-candidate hardening slice, but not required for the current Revit 2024 RC claim.
- `research`: needs Revit API investigation or reference-discovery design before implementation.
- `deferred`: intentionally out of scope until a separate design is approved.

## Backlog Matrix

| Area | Tool or operation | Contract owner | Preview/apply behavior | Smoke requirement | State |
| --- | --- | --- | --- | --- | --- |
| Read batching | `revit.read_bundle` for status, readiness, scoped elements, catalogs, and parameter metadata | Broker/add-in/contracts | Read-only, guarded by document fingerprint/generation, per-section limits and scan metrics | Fake bridge test plus live smoke bundle call | rc-backlog |
| Diagnostics | `revit.diagnostics` or expanded `revit.status` lifecycle section | Add-in/broker | Read-only; queue depth, active request, recent errors, preview token counts, redacted log metadata | Fake bridge test plus live no-op diagnostics smoke | rc-backlog |
| Cancellation | Queued request cancellation by `requestId` | Add-in queue | Debug operation; cancels queued work when not yet dispatched, reports no-op for in-flight work | Queue unit test plus live no-op and queued-cancel smoke | rc-backlog |
| Large reads | Native/lazy paging for views, sheets, rooms, warnings, catalogs, and broad query | Add-in/contracts | Read-only; `maxElementsScanned`, `elementsScanned`, `nativeFilterUsed`, `postFilterUsed`, `scanTruncated` metrics | Contract tests plus large synthetic/disposable model smoke | rc-backlog |
| Shared schemas | Per-operation payload schemas for broker, `revitctl`, named pipe, and in-process bridge | Contracts/broker/add-in | Reject invalid payloads before dispatch; keep MCP Zod schemas as source-aligned | Schema contract tests and raw-bridge invalid-payload test | rc-backlog |
| Host parity | Typed Python wrappers for read/query/catalog/preview/apply helpers | Python integrations | Wrapper helpers, no direct pipe use inside Revit | pyRevit and Dynamo smoke cover read/query/catalog plus create-level preview/apply | rc-backlog |
| Dimensions | Reference discovery plus `create_dimensions` | Add-in/contracts | Preview must list references, view, dimension type, line, and blocked reference reasons | Curated model smoke with deterministic references | research |
| View control | `create_view`, duplicate view, apply template, rename view | Add-in/contracts | Preview/apply with view-family type, source view, template, and naming collision guards | Disposable model smoke for one plan/duplicate/template case | rc-backlog |
| Schedules | Schedule inventory/read/export and limited schedule creation | Add-in/contracts | Reads first; creation only after field/category validation | Live read smoke plus curated schedule creation smoke | rc-backlog |
| Navigation/review | Select/open view/zoom/isolate helpers | Add-in/contracts | Debug or write-view-state operations; non-model mutation but user-visible UI state changes | Live smoke verifies selection/open view and bounded reset | rc-backlog |
| Element relationships | Dependency/host/join/group/design-option relationship reads | Add-in/contracts | Read-only, bounded, paginated relationship summaries | Fake bridge plus live targeted relationship smoke | rc-backlog |
| View overrides | Element/category color and hide/isolate overrides | Add-in/contracts | Preview/apply scoped by view id, element/category limits, and reset metadata | Curated graphical-view smoke | research |
| Structural | Beams, framing systems, columns, analytical helpers | Add-in/contracts | Separate domain operations with symbol/level/host validation | Curated structural template smoke | research |
| MEP | Pipe, duct, conduit, equipment placement, connector-aware routing | Add-in/contracts | Separate typed operations, not overloaded line-based creation | Curated MEP template smoke | research |
| Ceilings/roofs | `create_ceiling`, `create_roof` | Add-in/contracts | Preview/apply with type, level, loop, slope/host constraints | Curated architectural template smoke | research |
| Local persistence | Stored project/room data cache | Broker/integrations | Out-of-band local cache with freshness metadata if ever approved | Separate cache consistency tests | deferred |
| Code execution | `send_code_to_revit` style arbitrary execution | Add-in/security | Disabled by default; would require explicit local opt-in and audit logging | Security review before any smoke | deferred |

## Current RC Surface

The current Revit 2024 release-candidate surface remains focused on compact reads, catalog discovery, guarded preview/apply writes, room/tag coverage, hosted pyRevit/Dynamo evidence, support bundles, and release-evidence gates. Features listed as `rc-backlog`, `research`, or `deferred` must not be implied as complete in release notes until their contract and smoke columns are satisfied.
