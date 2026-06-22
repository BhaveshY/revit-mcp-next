# Write Workflow

Write operations use a preview/apply contract.

1. Run `revit.status` and record the active document fingerprint.
2. Run `revit.preview_change_set` with a bounded change set.
3. Inspect every returned change. Do not apply blocked previews.
4. Run `revit.apply_change_set` with the exact same change set, the returned `previewId`, and `confirm: true`.

Supported operations:

- `set_parameter`: set a writable instance parameter by element ID and parameter name.
- `create_level`: create a level by name and elevation.

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

Diagnostics:

- Run `npm run doctor:windows` after install.
- Add-in logs are written to `%LOCALAPPDATA%\RevitMcpNext\logs` after Revit loads the add-in.
