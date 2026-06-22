# Write Workflow

Write operations use a preview/apply contract.

1. Run `revit.status` and record the active document fingerprint.
2. Run `revit.preview_change_set` with a bounded change set.
3. Inspect every returned change. Do not apply blocked previews.
4. Run `revit.apply_change_set` with the exact same change set, the returned `previewId`, and `confirm: true`.

End-to-end supported operations:

- `set_parameter`: set a writable instance parameter by element ID and parameter name.
- `create_level`: create a level by name and elevation.
- `create_wall`: create a straight wall from `levelId`, `start`, `end`, optional `wallTypeId`, optional `height`, optional `structural`, and optional `flip`.
- `move_element`: move one non-pinned model element by `elementId` and a `translation` vector.
- `rotate_element`: rotate one non-pinned model element around `axisStart`/`axisEnd` by an explicit `angle`.
- `copy_element`: copy one model element by a non-zero `translation` vector and return copied element IDs.
- `change_element_type`: change one non-pinned model element to a compatible `typeId`.
- `set_element_pinned`: set one model element's pinned state, optionally guarded by `expectedPinned`.

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
- Production release still needs automated live Revit smoke coverage, signed artifacts, and broader failure-mode validation. Track the current blocker list in [production-readiness.md](production-readiness.md).

Diagnostics:

- Run `npm run doctor:windows` after install.
- Run `npm run smoke:revit` only against a disposable active Revit project; it creates, moves, rotates, copies, pins, and unpins walls through preview/apply.
- Run `npm run support:bundle` when sharing diagnostics; the bundle redacts common secret shapes and local profile paths.
- Add-in logs are written to `%LOCALAPPDATA%\RevitMcpNext\logs` after Revit loads the add-in.
