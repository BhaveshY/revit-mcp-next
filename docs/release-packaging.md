# Release Packaging

Release packaging is Windows-first and stages an offline-ish package for a built repo.

Packaging output is a staged package artifact, not a signed production release. Track remaining release blockers in [production-readiness.md](production-readiness.md).

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
- `installer\install-windows.ps1`, `scripts\doctor.ps1`, `scripts\doctor-clients.ps1`, and `scripts\collect-support-bundle.ps1`.
- `integrations\python`, `integrations\pyrevit`, and `integrations\dynamo` examples for external Revit automation clients.
- `release-manifest.json` with file inventory, build metadata, and signing status.
- `CHECKSUMS.sha256` for installer-side integrity verification.

Hosted CI also runs a release package contract test:

```powershell
npm run test:release:windows
```

That contract packages the built broker/contracts with synthetic add-in DLL placeholders, installs the package into temporary profile paths, runs doctor and support bundle collection, verifies support redaction for the generated auth token, and confirms a tampered package fails checksum verification. It proves package mechanics on `windows-latest`; it does not prove Revit can load the synthetic DLLs or replace the manual/live Revit smoke gate.

The contract also verifies that unsupported Revit years fail instead of writing misleading manifests. Until the per-year .NET 8 add-in build/package path exists, release packaging and install support `-RevitYears 2024` only.

`npm test` also runs `npm run test:integrations:python`, which syntax-checks the pyRevit/Dynamo Python examples and exercises the shared Python MCP client against a fake stdio MCP server.

Hosted CI also runs the release evidence contract:

```powershell
npm run test:evidence:release:windows
```

That contract packages with synthetic add-in DLLs, installs into temporary profile paths, runs doctor/support collection, creates synthetic live-smoke and pyRevit/Dynamo hosted-smoke artifacts, verifies missing evidence requires explicit skip reasons, and verifies the release evidence manifest, summary, zip, hashes, and token redaction.

Live-smoke evidence must include `smoke-summary.json` with `status: "passed"`. The collector rejects a live-smoke directory whose summary is missing or failed.

## Signing Status

The default packaging command produces unsigned staged artifacts. No certificate is committed or assumed.

When a release certificate is available, packaging can invoke optional Authenticode signing before it writes `release-manifest.json`, `CHECKSUMS.sha256`, and the `.zip`:

```powershell
$env:REVIT_MCP_NEXT_SIGN_CERT_THUMBPRINT = "<thumbprint>"
$env:REVIT_MCP_NEXT_TIMESTAMP_URL = "http://timestamp.digicert.com"
npm run package:windows -- -Sign -RequireSigned
```

Use `-RequireTrustedSignatures` only on a release machine where the certificate chain is expected to validate. Signing can also use `REVIT_MCP_NEXT_SIGN_CERT_PATH` plus `REVIT_MCP_NEXT_SIGN_CERT_PASSWORD`, or the matching PowerShell parameters.

For disposable local Revit smoke machines, use trusted signing as the prompt-removal path. `npm run smoke:release-local` creates or reuses a local dev code-signing certificate, trusts it in CurrentUser `Root` and `TrustedPublisher`, signs the staged package before checksums, and verifies trusted signatures before Revit launch. This is a local test identity only; do not treat that self-signed certificate as a production release identity.

The Revit `Always Load` registry helper is only supplemental/diagnostic:

```powershell
npm run revit:trust -- -RevitYears 2024
npm run revit:trust -- -StatusOnly -RevitYears 2024
npm run revit:trust -- -Remove -RevitYears 2024
```

Audit and clean up the local dev certificate after testing:

```powershell
npm run dev-cert:windows -- -StatusOnly
npm run dev-cert:windows -- -Remove -DryRun
npm run dev-cert:windows -- -Remove
```

The cleanup command only targets the Revit MCP Next local dev certificate subject in CurrentUser `My`, `Root`, and `TrustedPublisher`. Pass `-Thumbprint <thumbprint>` to narrow cleanup to one certificate.

When signing with a local or offline certificate without a timestamp service, pass `-NoTimestamp`:

```powershell
npm run package:windows -- -Sign -RequireSigned -SigningCertificateThumbprint "<thumbprint>" -NoTimestamp
```

Inspect the package plan without signing:

```powershell
npm run package:windows:dry-run -- -Sign
```

The separate `npm run sign:windows` helper can verify an existing package or inspect direct targets. Use it for verification after packaging, not as a post-package signing step for publishable artifacts:

```powershell
npm run sign:windows -- -PackageRoot artifacts\release\revit-mcp-next-<version>-windows -VerifyOnly -RequireSigned
```

If signing is done manually after package creation, regenerate `release-manifest.json`, `CHECKSUMS.sha256`, and the `.zip` before publishing. Do not publish a package whose checksums were captured before signing.

Treat signing as a release-time hardening step rather than a local development prerequisite. Release notes and docs should only call a package signed when verification evidence exists for that exact build. Unsigned packages remain staged artifacts.

Capture signing evidence for signed builds:

- Whether signing was enabled or skipped.
- Signer subject, certificate thumbprint, and timestamp service when signing is enabled.
- The exact files signed by the implementation.
- Verification output from `Get-AuthenticodeSignature` or `signtool verify` for each signed artifact class.
- Any files intentionally left unsigned.

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
- `-RevitYears 2024`: install the Revit 2024 `.addin` manifest. Other years are rejected until year-specific add-in artifacts exist.
- `-SkipDependencyInstall`: do not run npm if packaged dependencies are absent.
- `-SkipChecksumVerification`: bypass package checksum verification only for local debugging.

Each install generates or reuses a local pipe auth token at `%LOCALAPPDATA%\RevitMcpNext\config\auth.env`. The token is generated on the target machine, is not included in release packages, and is loaded by the generated `launch-revit-mcp-next.cmd` as `REVIT_MCP_NEXT_AUTH_TOKEN`. The installer attempts to restrict the auth config and launcher ACLs to the installing user, Administrators, and SYSTEM.

The installer also writes `%LOCALAPPDATA%\RevitMcpNext\config\client-discovery.json`. It includes install paths, launcher path, schema path, integration helper paths, tool names, catalog kinds, and write-operation names. Tool discovery includes compact read/analysis tools for current view, active-view elements, selection, model statistics, model readiness, rooms, material quantities, catalogs, and bounded preview/apply write operations including `place_family_instance`. It does not include the auth token.

Print Claude Code, Claude Desktop, or Codex MCP config snippets from that discovery file:

```powershell
npm run mcp:config
npm run mcp:config -- -Client claude-code
npm run mcp:config -- -Client codex
npm run doctor:clients
```

`npm run doctor:clients` checks generated Claude/Codex snippets, existing user config entries when present, stale launcher roots, launcher quoting, token leakage risk, and MCP `initialize` plus `tools/list` startup without requiring an active Revit connection.

Support bundle:

```powershell
npm run support:bundle
```

The support bundle is written under `artifacts\support`. It collects doctor output, add-in logs, launcher/config metadata, the redacted auth config, install receipts, file hashes, and environment basics. It does not collect environment variables, and text files are redacted for the installer auth token, common secret key names, JWT-shaped tokens, private keys, and local profile paths.

## Release Evidence Capture

Use the release evidence collector after creating and installing a candidate package. The evidence should identify one build, not a mix of local attempts.

Local unsigned package example:

```powershell
npm run evidence:release:windows -- `
  -PackageRoot artifacts\release\revit-mcp-next-<version>-windows `
  -ValidateRepoLogPath artifacts\release-logs\validate-repo.log `
  -PackageLogPath artifacts\release-logs\package-release.log `
  -DoctorLogPath artifacts\release-logs\doctor-windows.log `
  -SigningSkipReason "No release certificate configured for this local candidate." `
  -LiveSmokeSkipReason "No self-hosted Revit runner evidence for this local candidate." `
  -SupportBundleSkipReason "No installed candidate support bundle collected for this local candidate." `
  -HostedIntegrationSkipReason "No pyRevit/Dynamo host smoke collected for this local candidate."
```

Release-candidate example with live smoke and support artifacts:

```powershell
npm run evidence:release:windows -- `
  -PackageRoot artifacts\release\revit-mcp-next-<version>-windows `
  -ValidateRepoLogPath artifacts\release-logs\validate-repo.log `
  -PackageLogPath artifacts\release-logs\package-release.log `
  -DoctorLogPath artifacts\release-logs\doctor-windows.log `
  -SigningLogPath artifacts\release-logs\signing.log `
  -LiveSmokeEvidencePath artifacts\live-revit-smoke `
  -SupportBundlePath artifacts\support\revit-mcp-next-support-<timestamp>.zip `
  -HostedIntegrationEvidencePath artifacts\host-integrations
```

Build hosted pyRevit/Dynamo summary evidence from raw host-smoke JSON before passing `-HostedIntegrationEvidencePath`.

Preferred release-candidate path:

```powershell
npm run smoke:host-integrations -- `
  -RevitYear 2024 `
  -ModelPath C:\tmp\disposable.rvt `
  -OutputRoot artifacts\host-integrations `
  -SeedPyRevitHosts `
  -LaunchRevitForDynamo
```

This command writes `raw\pyrevit.json`, waits for `raw\dynamo.json`, composes `host-integrations-summary.json`, and leaves host-smoke logs under `logs`.

For pyRevit CLI runs, use the packaged runner. It stages a temporary
`RevitMcpNext.addin` into pyRevit's unattended runner with `--import`, pins
`REVIT_MCP_NEXT_INSTALL_ROOT` and `REVIT_MCP_NEXT_AUTH_CONFIG`, sets
`REVIT_MCP_NEXT_PYREVIT_EVIDENCE`, sets `REVIT_MCP_NEXT_PYREVIT_MODEL` when a
model is supplied, optionally seeds pyRevit's per-user host cache, runs the
packaged Host Smoke command, and rejects failed raw evidence. Keep the runner
add-in import enabled for release evidence; the evidence must report
`inProcessBridge.handler=configuredAddin`, not the Python direct fallback:

```powershell
npm run smoke:pyrevit-host -- `
  -RevitYear 2024 `
  -ModelPath C:\tmp\disposable.rvt `
  -EvidencePath artifacts\host-integrations\raw\pyrevit.json `
  -SeedHostsCache
```

For Dynamo, run the packaged graph inside Dynamo for Revit so the evidence is produced by the installed package. Do not use headless `DynamoCLI.exe` as release evidence; it does not provide RevitServices. The wrapper below launches Revit with the required environment variables, waits for `dynamo.json`, and validates the result after you run the graph:

```powershell
npm run smoke:dynamo-host -- `
  -RevitYear 2024 `
  -ModelPath C:\tmp\disposable.rvt `
  -EvidencePath artifacts\host-integrations\raw\dynamo.json `
  -LaunchRevit
```

Open and run this graph in Dynamo for Revit:

```text
%LOCALAPPDATA%\RevitMcpNext\integrations\dynamo\revit_mcp_next_host_smoke.dyn
```

```powershell
npm run evidence:host-integrations -- `
  -PyRevitEvidencePath artifacts\host-integrations\raw\pyrevit.json `
  -DynamoEvidencePath artifacts\host-integrations\raw\dynamo.json `
  -OutputRoot artifacts\host-integrations
```

The command writes `artifacts\release-evidence\revit-mcp-next-<version>-windows-evidence-<timestamp>-<id>` plus a sibling `.zip`. The bundle includes:

- `release-evidence-manifest.json` with package metadata, package zip SHA-256, signing status, validation logs, support-bundle evidence, live-smoke evidence, hosted pyRevit/Dynamo evidence, and an inventory of copied evidence files.
- `release-evidence-summary.md` with the release evidence headline facts.
- Copies of `release-manifest.json`, `CHECKSUMS.sha256`, and `package-zip.sha256`.
- Named validation logs when paths are provided.
- Live-smoke artifacts when `smoke-summary.json` reports `status: "passed"`, hosted pyRevit/Dynamo artifacts when `host-integrations-summary.json` reports both hosts passed, plus support-bundle artifacts when paths are provided.

Minimum evidence for a release candidate:

- Git commit and dirty/clean status from `release-manifest.json`.
- The staged package directory and sibling `.zip`.
- `release-manifest.json` and `CHECKSUMS.sha256`.
- The `release-manifest.json` `signing` section, including whether signing was requested and the signature status of `.dll` and `.ps1` targets.
- SHA-256 hash of the package `.zip`.
- Output from `node scripts\validate-repo.mjs`.
- Output from `npm run package:windows` or `npm run package:windows -- -Sign -RequireSigned`, plus `npm run doctor:windows`.
- Output from the manual live smoke, or the uploaded `Live Revit Smoke` workflow artifact, when a Revit host is available. Live-smoke evidence must include `smoke-summary.json` with `status: "passed"`.
- `npm run support:bundle` output after install or after any failed smoke.
- pyRevit and Dynamo hosted-smoke output from the installed package. Hosted integration evidence must include `host-integrations-summary.json` with `status: "passed"` and passed `pyrevit` and `dynamo` host entries.
- Authenticode signing and verification output when signing is enabled.

If signing, live smoke, support bundle, or hosted integration capture is skipped, pass the corresponding skip reason. The collector refuses to create evidence when those evidence classes are absent without an explicit reason.
