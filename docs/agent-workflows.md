# Agent Workflows

These workflows assume Revit MCP Next is installed for Revit 2024 and the MCP client was configured with `npm run mcp:config`. Start most workflows with `revit.read_bundle` when the agent needs a compact preflight packet, or `revit.status` when it only needs connection and document guards. Carry the returned `activeDocument.fingerprint`/`documentFingerprint` and generation into reads and change sets so stale model state is caught early. If a workflow stalls, inspect `structuredContent.data.diagnostics.queue`, `diagnostics.previewTokens`, and `diagnostics.recovery` before retrying or cancelling queued work.

## Model Audit

Use this sequence when the user asks what is in the model, what is selected, or what needs cleanup:

1. `revit.read_bundle` for connection, Revit version, active document, generation, levels, readiness, current view, current-view elements, selection, queue diagnostics, and preview-token health.
2. `revit.get_views`, `revit.get_sheets`, and `revit.get_schedules` when the task mentions documentation, sheets, schedules, view placement, templates, or drawing organization.
3. `revit.get_current_view` for view type, scale, and crop state.
4. `revit.get_current_view_elements` with `preset: "summary"` and a low `limit`; use `preset: "geometrySummary"` instead when the workflow needs compact element `location` and model-space `bounds` in millimeters. When more results are available, repeat the same call and add the opaque `cursor` from `structuredContent.data.cursor`.
5. `revit.get_selection` when the user references selected elements.
6. `revit.get_model_context` with low section limits when the audit depends on phases, worksets, design options, or linked models.
7. `revit.analyze_model` for category, class, and level distribution.
8. `revit.get_warnings` with `preset: "summary"` for compact model-health issues; switch to `preset: "elements"` only when warning element IDs are needed.
9. `revit.get_material_quantities` for bounded material takeoff.
10. `revit.get_rooms` with `preset: "schedule"` for room numbers, names, levels, areas, and departments.
11. `revit.get_schedule_fields` when the task needs schedule columns, exact field IDs, or a new schedule for a known category.

Keep audit prompts scoped. Prefer current view or selected elements first, then broaden to model analysis only when needed. Leave `includeTotalCount` false unless the user needs an exact total for reporting; cursor-first reads avoid full model counts on large projects. Cursors are opaque and bound to the same tool arguments/session/document state; do not parse, increment, construct, or reuse a cursor after changing filters, fields, presets, limits, document guards, or count settings.

## View And Sheet Planning

Use this sequence when the user asks what sheets/views exist, which views are placed, or what documentation setup is available:

1. `revit.read_bundle` with a small `catalogs` request for `titleBlocks` or `viewFamilyTypes`, or `revit.status` when only active document fingerprint and generation are needed.
2. `revit.get_views` with `filter.isTemplate: false` and a low `limit`; use `structuredContent.data.cursor` with the same arguments for additional pages.
3. `revit.get_sheets` with `includePlacedViews: true` when the task involves sheet composition.
4. `revit.get_schedules` with `includeFields: true` when schedules may need to be created, edited, or placed.
5. `revit.get_schedule_fields` with `scheduleId` or `category` before adding fields or creating a schedule.
6. `revit.catalog` with `kind: "titleBlocks"` and `preset: "sheet"` when sheet creation or title block availability matters.
7. `revit.catalog` with `kind: "viewFamilyTypes"` when plan/section/elevation view creation is being evaluated.

The current release also supports `create_sheet`, `place_view_on_sheet`, `create_schedule`, `add_schedule_field`, and `place_schedule_on_sheet` through `preview_change_set`. Use `titleBlockTypeId` from `revit.catalog kind=titleBlocks`; note that sheet `titleBlockIds` returned by `revit.get_sheets` are placed title block instance IDs, not type IDs. For `place_view_on_sheet`, collect existing `placedViews` first and choose an unplaced printable non-template view. `center` is in sheet-space coordinates. For schedule work, use `revit.get_schedule_fields` before composing `fields` or `fieldId`, and place schedules with sheet-space `point`.

## Annotation Planning

Use this sequence when the user asks about tags, text notes, or dimensions:

1. `revit.status` and `revit.get_current_view` to confirm the active view is graphical and not a template.
2. `revit.catalog` with `kind: "textNoteTypes"` and `preset: "annotation"` for text note styles.
3. `revit.catalog` with `kind: "dimensionTypes"` and `preset: "annotation"` for dimension styles.
4. `revit.catalog` with `kind: "tagTypes"` and `preset: "annotation"` for available tag symbols.

The current release supports `create_text_note`, `load_family`, `tag_room`, and `tag_element` through `preview_change_set`. For room tags, use a placed room from `revit.get_rooms`, keep targeting by `roomId`, pass `expectedUniqueId` when the room `uniqueId` is known, choose a plan or section view from `revit.get_views`, and choose a room tag type from `revit.catalog kind=tagTypes filter.categories=["OST_RoomTags"]`. For element tags, query elements visible in the target view, then use a matching wall or multi-category tag `FamilySymbol` from `tagTypes`. If compatible tag symbols are missing and a vetted local `.rfa` path is available, preview/apply `load_family` with `expectedSha256` when known, then re-run `revit.catalog`. Dimension creation remains deferred until robust reference discovery is added.

## Walls, Floor, And Room

Discover IDs before building a change set:

```json
{"documentFingerprint":"doc","kind":"elementTypes","filter":{"classes":["WallType"],"categories":["OST_Walls"]},"preset":"compact","limit":5}
```

Also call `revit.get_levels`, `revit.catalog` for `FloorType`, and `revit.get_rooms` to avoid duplicate room numbers.

Preview a bounded room shell and room placement before applying:

```json
{
  "documentFingerprint": "doc",
  "expectedGeneration": 12,
  "transactionName": "Create small room shell",
  "operations": [
    {"id":"wall-a","type":"create_wall","levelId":"311","wallTypeId":"9001","start":{"x":{"value":0,"unit":"mm"},"y":{"value":0,"unit":"mm"},"z":{"value":0,"unit":"mm"}},"end":{"x":{"value":5000,"unit":"mm"},"y":{"value":0,"unit":"mm"},"z":{"value":0,"unit":"mm"}},"height":{"value":3000,"unit":"mm"}},
    {"id":"floor-a","type":"create_floor","levelId":"311","floorTypeId":"9101","outline":[{"x":{"value":0,"unit":"mm"},"y":{"value":0,"unit":"mm"},"z":{"value":0,"unit":"mm"}},{"x":{"value":5000,"unit":"mm"},"y":{"value":0,"unit":"mm"},"z":{"value":0,"unit":"mm"}},{"x":{"value":5000,"unit":"mm"},"y":{"value":3500,"unit":"mm"},"z":{"value":0,"unit":"mm"}},{"x":{"value":0,"unit":"mm"},"y":{"value":3500,"unit":"mm"},"z":{"value":0,"unit":"mm"}}]},
    {"id":"room-a","type":"create_room","levelId":"311","location":{"x":{"value":1500,"unit":"mm"},"y":{"value":1500,"unit":"mm"}},"name":"Office","number":"A-101","department":"Admin"}
  ]
}
```

If the preview is ready, apply the exact same change set with `previewId`, `changeSetHash`, `baseGeneration`, `expiresAt`, and `confirm: true`. If the room is blocked because the boundary is not enclosed, apply the wall/floor shell first, refresh reads, then preview room placement again.

## Door, Window, Or Furniture Placement

Use `revit.catalog` with `kind: "familySymbols"` and `preset: "placement"`. For doors and windows, query a wall host first:

```json
{"filter":{"categories":["OST_Walls"]},"fields":["id","uniqueId","category","class","name","levelId"],"limit":5}
```

Then preview `place_family_instance` only when a symbol and any required host exist. For hosted doors/windows, keep targeting the wall by `hostElementId` and echo the wall `uniqueId` as `expectedHostUniqueId`:

```json
{
  "documentFingerprint": "doc",
  "expectedGeneration": 12,
  "transactionName": "Place hosted door sample",
  "operations": [
    {
      "id": "place-door",
      "type": "place_family_instance",
      "familySymbolId": "12001",
      "hostElementId": "501",
      "expectedHostUniqueId": "wall-501-unique-id",
      "levelId": "311",
      "location": {"x":{"value":1800,"unit":"mm"},"y":{"value":500,"unit":"mm"},"z":{"value":0,"unit":"mm"}},
      "rotation": {"value":0,"unit":"degrees"}
    }
  ]
}
```

For level-based furniture/equipment/fixture placement without a host, use `levelId` and omit `expectedHostUniqueId`.

Treat a blocked placement preview as a normal discovery result: the model may have no compatible symbols, the symbol may need a wall/workplane host, or the installed add-in may not support placement for that family class yet. Do not fall back to guessed IDs.

## Selected Element Parameter Or Type Update

For selected edits:

1. Call `revit.get_selection`.
2. Call `revit.query` with `filter.elementIds` and explicit fields such as `id`, `uniqueId`, `category`, `class`, `name`, `typeId`, and target parameter fields.
3. Call `revit.describe_parameters` for the target element IDs before `set_parameter`; the default `preset: "writableEdit"` returns compact writable instance parameters. Use `preset: "full"` only when the user explicitly asks for type-level, read-only, or current-value details.
4. For type changes, call `revit.catalog` with `kind: "elementTypes"`, `filter.forElementId`, and `preset: "typeChange"`.
5. Preview `set_parameter` or `change_element_type`.

```json
{
  "documentFingerprint": "doc",
  "expectedGeneration": 12,
  "transactionName": "Update selected element",
  "operations": [
    {"id":"mark","type":"set_parameter","elementId":"501","expectedUniqueId":"wall-501-unique-id","parameterName":"Mark","value":"A-101"},
    {"id":"type","type":"change_element_type","elementId":"501","expectedUniqueId":"wall-501-unique-id","typeId":"9002"}
  ]
}
```

Use `expectedUniqueId` on every existing-element write when a prior read returned `uniqueId`, and avoid changing pinned elements unless the user explicitly asks for pin state changes. For `delete_element`, inspect the previewed `deletedElementIds`; if Revit reports dependents, only continue with `allowDependentDeletes` or exact `expectedDeletedElementIds` after review.

## Blocked Preview Recovery

Never apply a blocked preview. Recovery loop:

1. Read every `changes[].message` and `warnings[]`.
2. If the generation changed, rerun `revit.status` and refresh scoped reads.
3. If an element/type/host is missing, rerun `revit.query` or `revit.catalog` instead of guessing.
4. If a room number duplicates, choose a new number or explicitly set `allowDuplicateNumber` only when the user accepts that outcome.
5. If an element is pinned, preview `set_element_pinned` first or ask for confirmation before moving/deleting it.
6. Rerun `revit.preview_change_set`; only then call `revit.apply_change_set` with the new preview metadata.

`PREVIEW_ID_MISMATCH`, `PREVIEW_NOT_READY`, and generation mismatch errors mean the apply payload no longer matches the latest valid preview. Rebuild the apply payload from the current preview response.
