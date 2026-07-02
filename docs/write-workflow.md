# Write Workflow

Write operations use a preview/apply contract.

1. Run `revit.status` and record the active document fingerprint.
2. Run `revit.get_levels`, `revit.catalog`, and, for room changes, `revit.get_rooms` for IDs and existing room numbers that the change set needs.
   For parameter edits, run `revit.describe_parameters` on the target element IDs before choosing `set_parameter`.
3. Run `revit.preview_change_set` with a bounded change set.
4. Inspect every returned change. Do not apply blocked previews.
5. Run `revit.apply_change_set` with the exact same change set, the returned `previewId`, and `confirm: true`.

## Discover Before Writing

Use `revit.catalog` instead of guessing type IDs:

- `create_wall.wallTypeId`: `revit.catalog` with `kind: "elementTypes"` and filters such as `classes: ["WallType"]`, `categories: ["OST_Walls"]`.
- `create_floor.floorTypeId`: `revit.catalog` with `kind: "elementTypes"` and filters such as `classes: ["FloorType"]`, `categories: ["OST_Floors"]`.
- `change_element_type.typeId`: `revit.catalog` with `kind: "elementTypes"`, `filter.forElementId`, and `preset: "typeChange"` so the returned IDs come from Revit's compatible type list.
- Family placement discovery: `revit.catalog` supports `kind: "familySymbols"` with `preset: "placement"`. Only apply placement change sets when preview reports ready; missing symbols, missing hosts, or unsupported placement classes should be treated as blocked previews.

When a previous read, preview, or apply response already returned element identifiers, use `revit.query` with `filter.elementIds` or `filter.uniqueIds` to retrieve just those elements instead of scanning a broader model scope.

pyRevit and Dynamo scripts should use the in-process helper under `integrations/python` so they do not deadlock while waiting on an `ExternalEvent`. Plain external Python processes can use the stdio MCP client in the same folder. Do not call the named pipe directly from pyRevit or Dynamo.

End-to-end supported operations:

- `set_parameter`: set a writable instance parameter by element ID and parameter name.
- `create_level`: create a level by name and elevation.
- `create_wall`: create a straight wall from `levelId`, `start`, `end`, optional `wallTypeId`, optional `height`, optional `structural`, and optional `flip`.
- `create_grid`: create a straight grid line from `start` to `end`, with an optional unique name.
- `create_floor`: create a single-loop floor from `levelId`, ordered `outline` points, optional `floorTypeId`, and optional `structural`.
- `create_room`: place one room by `levelId` and 2D `location`, optionally setting `name`, `number`, and `department`; duplicate room numbers are blocked unless `allowDuplicateNumber` is set.
- `place_family_instance`: place supported `familySymbols` discovered through `revit.catalog`; first supported cases are wall-hosted doors/windows with `hostElementId` and level-based furniture/equipment/fixtures with `levelId`.
- `create_sheet`: create one sheet with unique `sheetNumber`, optional `name`, and optional `titleBlockTypeId`; discover title block type IDs with `revit.catalog kind=titleBlocks preset=sheet`.
- `place_view_on_sheet`: place one eligible unplaced view on a sheet by `sheetId`, `viewId`, and sheet-space `center`; use `revit.get_sheets includePlacedViews=true` to avoid already placed views.
- `create_text_note`: create one text note in a graphical non-template view by `viewId`, `text`, `position`, optional `textNoteTypeId`, optional `width`, and optional `rotation`.
- `tag_room`: create one room tag by `roomId`, plan/section `viewId`, 2D `location`, optional `tagTypeId`, optional `hasLeader`, and optional `orientation`; discover room tag types with `revit.catalog kind=tagTypes filter.categories=["OST_RoomTags"]`.
- `tag_element`: create one independent element tag by `elementId`, graphical `viewId`, tag `FamilySymbol` `tagTypeId`, `position`, optional `hasLeader`, and optional `orientation`; discover wall or multi-category tags with `revit.catalog kind=tagTypes`.
- `move_element`: move one non-pinned model element by `elementId` and a `translation` vector.
- `rotate_element`: rotate one non-pinned model element around `axisStart`/`axisEnd` by an explicit `angle`.
- `copy_element`: copy one model element by a non-zero `translation` vector and return copied element IDs.
- `change_element_type`: change one non-pinned model element to a compatible `typeId`.
- `set_element_pinned`: set one model element's pinned state, optionally guarded by `expectedPinned`.
- `delete_element`: delete one non-type element by `elementId`, optionally guarded by `expectedUniqueId`, `expectedPinned`, and `allowPinned`.

Example preview payload:

```json
{
  "documentFingerprint": "active-document-fingerprint",
  "transactionName": "Update room mark",
  "operations": [
    {
      "id": "op-1",
      "type": "set_parameter",
      "elementId": "501",
      "parameterName": "Mark",
      "value": "A-101"
    }
  ]
}
```

Example apply payload:

```json
{
  "documentFingerprint": "active-document-fingerprint",
  "transactionName": "Update room mark",
  "previewId": "preview-id-from-preview",
  "confirm": true,
  "operations": [
    {
      "id": "op-1",
      "type": "set_parameter",
      "elementId": "501",
      "parameterName": "Mark",
      "value": "A-101"
    }
  ]
}
```

The add-in recomputes the preview hash before applying. If the model, transaction name, or operation list no longer match, apply fails.

Production readiness:

- The write path is suitable for local development and staged packaging, not a signed production release.
- Production release still needs signed artifacts, archived release-candidate live Revit smoke evidence from the exact package, and broader real-model failure-mode validation. Track the current release gate list in [production-readiness.md](production-readiness.md).
- See [agent-workflows.md](agent-workflows.md) for full agent sequences for model audit, wall/floor/room creation, family placement preview, selected element updates, and blocked preview recovery.

Diagnostics:

- Run `npm run doctor:windows` after install.
- Run `npm run smoke:revit` only against a disposable active Revit project; it creates a grid, floor, walls, a room-bounding loop, and a room, reads the room back through `revit.get_rooms`, attempts family placement when suitable symbols and hosts exist, optionally changes a wall type, then moves, rotates, copies, pins, and unpins elements through preview/apply.
- Run `npm run support:bundle` when sharing diagnostics; the bundle redacts common secret shapes and local profile paths.
- Add-in logs are written to `%LOCALAPPDATA%\RevitMcpNext\logs` after Revit loads the add-in.
