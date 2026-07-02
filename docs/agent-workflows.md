# Agent Workflows

These workflows assume Revit MCP Next is installed for Revit 2024 and the MCP client was configured with `npm run mcp:config`. Start each workflow with `revit.status`; carry the returned `activeDocument.fingerprint` and `activeDocument.generation` into reads and change sets so stale model state is caught early.

## Model Audit

Use this sequence when the user asks what is in the model, what is selected, or what needs cleanup:

1. `revit.status` for connection, Revit version, active document, and generation.
2. `revit.get_views` and `revit.get_sheets` when the task mentions documentation, sheets, view placement, templates, or drawing organization.
3. `revit.get_current_view` for view type, scale, and crop state.
4. `revit.get_current_view_elements` with `preset: "summary"`, a low `limit`, and `includeTotalCount: true`.
5. `revit.get_selection` when the user references selected elements.
6. `revit.analyze_model` for category, class, and level distribution.
7. `revit.get_material_quantities` for bounded material takeoff.
8. `revit.get_rooms` with `preset: "schedule"` for room numbers, names, levels, areas, and departments.

Keep audit prompts scoped. Prefer current view or selected elements first, then broaden to model analysis only when needed.

## View And Sheet Planning

Use this sequence when the user asks what sheets/views exist, which views are placed, or what documentation setup is available:

1. `revit.status` for active document fingerprint and generation.
2. `revit.get_views` with `filter.isTemplate: false`, a low `limit`, and `includeTotalCount: true`.
3. `revit.get_sheets` with `includePlacedViews: true` when the task involves sheet composition.
4. `revit.catalog` with `kind: "titleBlocks"` and `preset: "sheet"` when sheet creation or title block availability matters.
5. `revit.catalog` with `kind: "viewFamilyTypes"` when plan/section/elevation view creation is being evaluated.

The current release also supports `create_sheet` and `place_view_on_sheet` through `preview_change_set`. Use `titleBlockTypeId` from `revit.catalog kind=titleBlocks`; note that sheet `titleBlockIds` returned by `revit.get_sheets` are placed title block instance IDs, not type IDs. For `place_view_on_sheet`, collect existing `placedViews` first and choose an unplaced printable non-template view. `center` is in sheet-space coordinates.

## Annotation Planning

Use this sequence when the user asks about tags, text notes, or dimensions:

1. `revit.status` and `revit.get_current_view` to confirm the active view is graphical and not a template.
2. `revit.catalog` with `kind: "textNoteTypes"` and `preset: "annotation"` for text note styles.
3. `revit.catalog` with `kind: "dimensionTypes"` and `preset: "annotation"` for dimension styles.
4. `revit.catalog` with `kind: "tagTypes"` and `preset: "annotation"` for available tag symbols.

The current release supports `create_text_note` through `preview_change_set`. Tags and dimensions remain deferred until tag/category validation and reference discovery are added.

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

Then preview `place_family_instance` only when a symbol and any required host exist:

```json
{
  "documentFingerprint": "doc",
  "expectedGeneration": 12,
  "transactionName": "Place furniture sample",
  "operations": [
    {
      "id": "place-chair",
      "type": "place_family_instance",
      "familySymbolId": "12001",
      "levelId": "311",
      "location": {"x":{"value":1800,"unit":"mm"},"y":{"value":500,"unit":"mm"},"z":{"value":0,"unit":"mm"}},
      "rotation": {"value":0,"unit":"degrees"}
    }
  ]
}
```

Treat a blocked placement preview as a normal discovery result: the model may have no compatible symbols, the symbol may need a wall/workplane host, or the installed add-in may not support placement for that family class yet. Do not fall back to guessed IDs.

## Selected Element Parameter Or Type Update

For selected edits:

1. Call `revit.get_selection`.
2. Call `revit.query` with `filter.elementIds` and explicit fields such as `id`, `uniqueId`, `category`, `class`, `name`, `typeId`, and target parameter fields.
3. Call `revit.describe_parameters` for the target element IDs before `set_parameter`; prefer writable instance parameters unless the user explicitly asks for type-level changes.
4. For type changes, call `revit.catalog` with `kind: "elementTypes"`, `filter.forElementId`, and `preset: "typeChange"`.
5. Preview `set_parameter` or `change_element_type`.

```json
{
  "documentFingerprint": "doc",
  "expectedGeneration": 12,
  "transactionName": "Update selected element",
  "operations": [
    {"id":"mark","type":"set_parameter","elementId":"501","parameterName":"Mark","value":"A-101"},
    {"id":"type","type":"change_element_type","elementId":"501","typeId":"9002"}
  ]
}
```

Use `expectedUniqueId` on destructive operations and avoid changing pinned elements unless the user explicitly asks for pin state changes.

## Blocked Preview Recovery

Never apply a blocked preview. Recovery loop:

1. Read every `changes[].message` and `warnings[]`.
2. If the generation changed, rerun `revit.status` and refresh scoped reads.
3. If an element/type/host is missing, rerun `revit.query` or `revit.catalog` instead of guessing.
4. If a room number duplicates, choose a new number or explicitly set `allowDuplicateNumber` only when the user accepts that outcome.
5. If an element is pinned, preview `set_element_pinned` first or ask for confirmation before moving/deleting it.
6. Rerun `revit.preview_change_set`; only then call `revit.apply_change_set` with the new preview metadata.

`PREVIEW_ID_MISMATCH`, `PREVIEW_NOT_READY`, and generation mismatch errors mean the apply payload no longer matches the latest valid preview. Rebuild the apply payload from the current preview response.
