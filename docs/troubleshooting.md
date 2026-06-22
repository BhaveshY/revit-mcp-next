# Troubleshooting

Run:

```powershell
npm run doctor:windows
```

The doctor checks:

- Node is available.
- The staged MCP launcher exists.
- The local auth token config exists, contains a strong token, and does not print the token.
- The staged MCP launcher exports `REVIT_MCP_NEXT_AUTH_TOKEN` from the local config.
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

The bundle includes doctor output, add-in logs, launcher and install metadata, the redacted local auth config, file hashes, and basic tool versions. It does not collect environment variables. Text files are redacted for the installer auth token, common secret names, JWT-shaped tokens, private keys, and local profile paths.

Pipe auth config:

- Windows installs create `%LOCALAPPDATA%\RevitMcpNext\config\auth.env`.
- The file contains `REVIT_MCP_NEXT_AUTH_TOKEN=<redacted>` and is ACL-restricted to the installing user, Administrators, and SYSTEM when possible.
- Rerunning the installer preserves an existing strong token and reapplies the restrictive ACL. Delete the config before reinstalling only when you intentionally want to rotate the token.

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
