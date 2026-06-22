# Release Packaging

Release packaging is Windows-first and stages an offline-ish package for a built repo.

Prerequisites:

- Node 24.x and npm.
- `npm install`
- `npm run build`
- `npm run build:addin`

Dry run:

```powershell
npm run package:windows:dry-run
```

Create a staged package and zip:

```powershell
npm run package:windows
```

The package is written under `artifacts\release\revit-mcp-next-<version>-windows` with a sibling `.zip`. The package contains:

- `payload\broker`, `payload\contracts`, and `payload\addin` runtime files.
- Packaged broker production `node_modules` unless `-SkipDependencyInstall` is used.
- `installer\install-windows.ps1`, `scripts\doctor.ps1`, and `scripts\collect-support-bundle.ps1`.
- `release-manifest.json` with file inventory and build metadata.
- `CHECKSUMS.sha256` for installer-side integrity verification.

Install from an unpacked package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\install-windows.ps1
```

When the installer is run from a package, it auto-detects the sibling `payload` directory, verifies `CHECKSUMS.sha256`, installs from packaged files, and uses packaged `node_modules` without running npm on the target machine. To install from another package directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\install-windows.ps1 -PackageRoot C:\path\to\revit-mcp-next-0.1.0-windows
```

Useful installer switches:

- `-DryRun`: validate sources and print actions without writing install files.
- `-InstallRoot <path>`: override `%LOCALAPPDATA%\RevitMcpNext`.
- `-RevitYears 2024`: install one or more Revit `.addin` manifests.
- `-SkipDependencyInstall`: do not run npm if packaged dependencies are absent.
- `-SkipChecksumVerification`: bypass package checksum verification only for local debugging.

Support bundle:

```powershell
npm run support:bundle
```

The support bundle is written under `artifacts\support`. It collects doctor output, add-in logs, launcher/config metadata, install receipts, file hashes, and environment basics. It does not collect environment variables, and text files are redacted for common secret key names, JWT-shaped tokens, private keys, and local profile paths.
