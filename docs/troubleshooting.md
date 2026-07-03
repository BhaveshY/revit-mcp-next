# Troubleshooting

Run:

```powershell
npm run doctor:windows
```

The doctor checks:

- Node is available.
- The staged MCP launcher exists.
- The staged `revitctl` launcher exists.
- The local auth token config exists, contains a strong token, and does not print the token.
- The staged MCP launcher exports `REVIT_MCP_NEXT_AUTH_TOKEN` from the local config.
- The staged broker and `revitctl` entry points import successfully.
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

Revit unsigned add-in prompt:

- Application add-in manifests must use `<ClientId>` for the stable add-in identity. `RevitMcpNext.addin` should contain `<ClientId>6F78E70D-BE13-4E0B-9B11-9E28F876AF71</ClientId>`, not `<AddInId>`.
- Revit persists `Always Load` / `Immer laden` under `HKCU:\Software\Autodesk\Revit\Autodesk Revit <year>\CodeSigning`.
- That registry entry is not a signing substitute. On this test setup, Revit still prompted until the add-in DLLs were signed with a trusted CurrentUser certificate.
- Use `npm run smoke:release-local` on disposable smoke machines; it creates/trusts a local dev code-signing certificate, signs the staged package, and verifies trusted signatures before launching Revit.
- Use `npm run revit:trust -- -StatusOnly -RevitYears 2024` only to inspect the Revit trust entry, and `npm run revit:trust -- -Remove -RevitYears 2024` to remove that entry.
- Use `npm run dev-cert:windows -- -StatusOnly` to inspect the local dev certificate state.
- Use `npm run dev-cert:windows -- -Remove -DryRun`, then `npm run dev-cert:windows -- -Remove`, to remove this repo's local dev certificate from CurrentUser `My`, `Root`, and `TrustedPublisher`.
- If the dialog names `mcp-servers-for-revit`, the old fork is still installed. Disable `%APPDATA%\Autodesk\Revit\Addins\2024\mcp-servers-for-revit.addin` before testing Revit MCP Next.
- Unsigned external preview packages should be clearly labeled and shipped with checksums plus smoke/evidence artifacts. Only claim a signed release when signature verification evidence exists for that exact build.

Client config:

```powershell
npm run mcp:config
npm run doctor:clients
```

This prints Claude Code, Claude Desktop, and Codex MCP config snippets from the installed `config\client-discovery.json` without printing the local auth token.
The client doctor validates those generated snippets, launcher paths and quoting, stale install roots, existing Claude Desktop/Codex config entries when present, token leakage risk, and MCP `initialize` plus `tools/list` startup without requiring Revit to be connected.

Bridge debug CLI:

```powershell
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" status --pretty
cmd /c "%LOCALAPPDATA%\RevitMcpNext\revitctl.cmd" doctor --pretty
```

Use this to separate MCP client configuration problems from Revit/add-in/named-pipe problems. `BRIDGE_UNAVAILABLE` from `revitctl status` means Revit is closed, the add-in did not load, the auth config does not match the running add-in environment, or the named pipe is not listening.

Common states:

- `BRIDGE_UNAVAILABLE`: Revit is closed, the add-in did not load, or the named pipe is not listening.
- `PROTOCOL_VERSION_MISMATCH`: rebuild and reinstall so broker and add-in contracts match.
- `PREVIEW_METADATA_REQUIRED` or `CHANGE_SET_HASH_REQUIRED`: call `revit.preview_change_set` again and echo `previewId`, `baseGeneration`, `changeSetHash`, and `expiresAt` into `revit.apply_change_set`.
- `PREVIEW_ID_MISMATCH`: rerun `revit.preview_change_set`; the change set or document fingerprint no longer matches.
- `CHANGE_SET_HASH_MISMATCH`: the apply payload does not match the reviewed preview; rebuild it from the latest preview response.
- `PREVIEW_EXPIRED`: rerun `revit.preview_change_set`; preview tokens are short-lived and single-use.
- `REQUEST_CANCELLED`: the MCP client cancelled or the request exceeded its timeout before Revit processed it.
