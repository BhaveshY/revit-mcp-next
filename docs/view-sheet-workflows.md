# Views, sheets, annotations and dimensions

Revit MCP Next 0.3.0 adds native documentation workflows. Model writes use
`revit.preview_change_set` and `revit.apply_change_set`, with the existing exact
instance, document, generation, hash and expiring preview token checks. A failed
operation rolls back the entire change set. New warnings also cause rollback for
these workflows, including warnings that would otherwise discard annotations.

## Operations

| Request | Tool or change operation | Behavior |
| --- | --- | --- |
| Activate a view | `revit.activate_view` | Supply `viewId` or an exact, unambiguous `viewName`, plus `instanceId` and `documentFingerprint`. Changes UI context outside a model transaction. |
| Create an OKF plan | `create_plan_view` | Explicit `levelId`, FloorPlan `viewFamilyTypeId`, and `name`; optional `templateId`. No elevation matching or first-type fallback. |
| Duplicate with detailing | `duplicate_view` | Native `View.Duplicate(WithDetailing)`, optional new `name`, optional `sheetId` and `center` together. Omit `name` to retain Revit's generated name. |
| Duplicate a sheet | `duplicate_sheet` | Native `ViewSheet.Duplicate(DuplicateSheetWithViewsAndDetailing)`, explicit new `sheetNumber`, `viewNamePrefix`, optional sheet `name`. Preserves title blocks, sheet details and native view/schedule relationships. |
| Paste aligned annotations | `copy_view_annotations` | Explicit `sourceViewId`, `targetViewId`, and 1–500 distinct `elementIds`. Native view-to-view copy with identity additional transform. |
| Inspect settings and placements | `revit.get_view_details` | View template, scale, crop loops/box, annotation crop, scope box, phase/filter, discipline, view range; sheet viewports, schedules, parameters and bounded owned-element inventory. |
| Read dimensions | `revit.get_dimensions` | 1–100 explicit IDs; stable references, reference element IDs, curve, type/style, segment text, positions, locks and witness-line type parameters. |
| Create a dimension | `create_dimension` | Explicit linear `dimensionTypeId`, `viewId`, 2–256 stable references, `start` and `end`. Optional text edits. |
| Edit a dimension | `update_dimension` | `elementId`, optional `expectedUniqueId`, type, translation and segment text edits. To replace references, also supply `references`, `start`, and `end`. Atomic recreation returns a **new element ID**. |

`duplicate_view` checks the resulting template, scale, crop, phase/filter,
discipline, scope box and view range against the source. `duplicate_sheet` copies
writable sheet parameters, checks viewport and schedule positions, and preserves
viewport type, rotation and label placement. Native shared legend/schedule behavior
is retained; inspect returned view IDs before editing content shared by sheets.

Use `revit.query` with `fields: ["id", "ownerViewId", "viewSpecific", "class"]`
to identify view-owned annotations. Use `revit.catalog` with
`kind: "viewFamilyTypes"` and `filter.viewFamily: ["FloorPlan"]` for plan types.
Dimension catalogs accept the `dimensionStyle` projection, avoiding localized
family-name guesses.

## Units and examples

Write coordinates use a unit value for **each axis**, as in the existing connector:

```json
{
  "type": "duplicate_view",
  "viewId": "12345",
  "name": "Level 01 OKF - coordination",
  "sheetId": "67890",
  "center": {
    "x": { "value": 200, "unit": "mm" },
    "y": { "value": 150, "unit": "mm" }
  }
}
```

```json
{
  "type": "update_dimension",
  "elementId": "23456",
  "text": [{ "segmentIndex": 0, "above": "VERIFY ON SITE", "valueOverride": "" }]
}
```

Omit `segmentIndex` for a single-segment dimension. Empty strings clear overrides.
Positions and geometry lengths use mm; direction vectors are unitless. Generic
sheet parameter snapshots retain the existing parameter API's internal numeric
values and spec metadata. Witness-line length values include explicit unit objects.
Witness-line type parameters can be edited through `set_parameter` against the
dimension **type** ID; this affects every dimension using that type. Duplicate the
type first when an isolated style change is intended.

## Native API boundaries

- Whole-sheet duplication requires Revit 2024 or 2027 in this connector. Revit
  2021 does not expose the required sheet-duplication API and is explicitly blocked.
- Annotation copy is within one document between compatible 2D views. Room tags,
  dimensions, text, detail lines and annotation symbols go through Revit's native
  rehosting/copy mechanism. Missing hosts/references, dependent-view restrictions,
  duplicate types and unsupported annotation families can block the transaction.
  Model elements cannot be smuggled into this annotation operation.
- Dimension reference arrays are read-only in the native API. Linear reference
  editing therefore recreates the dimension atomically. Locked/equality-constrained
  dimensions and dependent deletions are rejected. A changed segment count requires
  explicit text edits to avoid moving overrides to the wrong segment.
- The project API creates linear dimensions. Existing angular/radial dimensions
  can be inspected and copied using native view/annotation duplication, with edits
  limited to properties their native classes support. This is not a claim of
  arbitrary dimension creation or arbitrary witness-line endpoint editing.
- Some native text/leader positions are unavailable even when Revit reports the
  position adjustable. Reads return a null value and an explicit reason for that
  field instead of discarding the dimension's other data.
- Sheet writes are bounded to 100 viewports. Owned sheet inventory returns at most
  1,000 IDs with a native total count and explicit truncation flag.

API references: Autodesk's [view-to-view copy documentation](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Editing_Elements/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Editing_Elements_Copying_Elements_html.html),
[sheet duplication](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Views/View_Types/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Views_View_Types_ViewSheet_html.html),
and [dimensions](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Revit_Geometric_Elements/Annotation_Elements/Revit_API_Revit_API_Developers_Guide_Revit_Geometric_Elements_Annotation_Elements_Dimensions_and_Constraints_html.html).
