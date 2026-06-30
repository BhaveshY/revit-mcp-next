# Fork Capability Parity

This matrix compares `revit-mcp-next` with the local fork at `mcp-servers-for-revit`. The goal is not one-for-one cloning; direct mutation tools from the fork should become bounded `preview_change_set` / `apply_change_set` operations unless they are intentionally deferred.

## Covered Or Better

| Old fork capability | Revit MCP Next status |
| --- | --- |
| `get_revit_connection_status` | Covered by `revit.status` with broker/add-in versions, active document, capabilities, and warnings. |
| `get_current_view_info` | Covered by `revit.get_current_view` with compact document/view metadata. |
| `get_current_view_elements` | Covered by `revit.get_current_view_elements` with bounded paging and projections. |
| `get_selected_elements` | Covered by `revit.get_selection`. |
| `get_available_family_types` | Covered more generally by `revit.catalog` for element types, family symbols, title blocks, and view family types. |
| `ai_element_filter` | Covered by `revit.query` filters and compact presets. |
| `analyze_model_statistics` | Covered by `revit.analyze_model`. |
| `get_material_quantities` | Covered by `revit.get_material_quantities` with normalized metric units. |
| `export_room_data` | Covered by `revit.get_rooms` for compact, paginated room export/schedule data. Live room smoke evidence is still required before treating this as production-proven. |
| `create_level` | Covered as guarded `create_level` preview/apply operation. |
| `create_grid` | Covered as guarded `create_grid` preview/apply operation. |
| Wall subset of `create_line_based_element` | Covered as guarded `create_wall` preview/apply operation. |
| Floor subset of `create_surface_based_element` | Covered as guarded `create_floor` preview/apply operation. |
| `create_room` | Covered as guarded `create_room` preview/apply operation with level/location input and duplicate-number protection by default. Live room smoke evidence is still required before treating this as production-proven. |
| Parts of `operate_element` | Covered by guarded `set_parameter`, `move_element`, `rotate_element`, `copy_element`, `change_element_type`, and `set_element_pinned`. |
| `delete_element` | Covered as high-risk guarded `delete_element` preview/apply operation with optional `expectedUniqueId`, `expectedPinned`, and `allowPinned` guards. |

## Missing Or Partial

| Old fork capability | Current gap | Recommended production path |
| --- | --- | --- |
| `create_dimensions` | Missing. | Add scoped dimension creation only after reliable reference discovery and preview messages. |
| `create_structural_framing_system` | Missing. | Add as a dedicated structural operation after catalog support for beam symbols and levels is validated. |
| Door/window/furniture subset of `create_point_based_element` | Missing. | Add `place_family_instance` with `familySymbolId`, host guards, activation checks, and level/workplane validation. |
| Beam/pipe/duct/conduit subsets of `create_line_based_element` | Missing. | Add separate operations per domain rather than one overloaded tool. |
| Ceiling/roof subsets of `create_surface_based_element` | Missing. | Add separate `create_ceiling` and `create_roof` operations after type/catalog and host constraints are validated. |
| `tag_walls` / `tag_rooms` | Missing. | Add view-scoped annotation operations with selected/current-view limits. |
| `color_splash` / color parts of `operate_element` | Missing. | Add view override operations guarded by view id and category/element limits. |
| `store_project_data`, `store_room_data`, `query_stored_data` | Missing by design. | Defer unless local persistence is explicitly required; current architecture favors live bounded reads over stale local cache. |
| `say_hello` | Not needed. | Covered by `revit.status` and doctor/smoke diagnostics. |
| `send_code_to_revit` | Deferred as unsafe. | Do not enable by default. If added, gate behind explicit local config, warnings, and isolated audit logging. |

## Current Priority Order

1. Prove signed no-prompt loading, live Revit 2024 smoke, pyRevit smoke, and Dynamo smoke from the exact release package.
2. Capture live room smoke evidence for `revit.get_rooms` and guarded `create_room` from the exact installed package.
3. Add `place_family_instance` for doors/windows/furniture with explicit family symbol catalog discovery.
4. Add dimension and tagging operations after robust view/reference validation.
5. Keep `store_project_data`, `store_room_data`, `query_stored_data`, and `send_code_to_revit` explicitly deferred unless separate designs are approved.
