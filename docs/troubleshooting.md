# Troubleshooting

Run:

```powershell
npm run doctor:windows
```

The doctor checks:

- Node is available.
- The staged MCP launcher exists.
- The staged broker imports successfully.
- Revit add-in DLLs are staged.
- The Revit `.addin` manifest is installed.
- Production `node_modules` are present under the staged broker.

Common states:

- `BRIDGE_UNAVAILABLE`: Revit is closed, the add-in did not load, or the named pipe is not listening.
- `PROTOCOL_VERSION_MISMATCH`: rebuild and reinstall so broker and add-in contracts match.
- `PREVIEW_ID_MISMATCH`: rerun `revit.preview_change_set`; the change set or document fingerprint no longer matches.
- `REQUEST_CANCELLED`: the MCP client cancelled or the request exceeded its timeout before Revit processed it.
