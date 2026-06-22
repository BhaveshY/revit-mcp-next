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
- The Revit `.addin` manifest points at the staged add-in DLL.
- Production `node_modules` are present under the staged broker.
- Optional install receipt, release manifest, and PDB files are present when available.

Collect a redacted support bundle:

```powershell
npm run support:bundle
```

The bundle includes doctor output, add-in logs, launcher and install metadata, file hashes, and basic tool versions. It does not collect environment variables. Text files are redacted for common secret names, JWT-shaped tokens, private keys, and local profile paths.

Installer package checks:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\install-windows.ps1 -PackageRoot C:\path\to\package -DryRun
```

Package installs verify `CHECKSUMS.sha256` before copying payload files unless `-SkipChecksumVerification` is passed.

Common states:

- `BRIDGE_UNAVAILABLE`: Revit is closed, the add-in did not load, or the named pipe is not listening.
- `PROTOCOL_VERSION_MISMATCH`: rebuild and reinstall so broker and add-in contracts match.
- `PREVIEW_ID_MISMATCH`: rerun `revit.preview_change_set`; the change set or document fingerprint no longer matches.
- `REQUEST_CANCELLED`: the MCP client cancelled or the request exceeded its timeout before Revit processed it.
