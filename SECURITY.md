# Security

Revit MCP Next can inspect and modify active Revit models. Treat it as a local automation tool with write access to valuable project files, and run public-release validation on disposable models first.

Report security issues privately to the repository owner.

## Implemented Controls

- Broker/add-in communication is local named-pipe IPC. The add-in creates a per-user pipe with Windows ACLs where available, and the broker connects through the installed launcher or `revitctl`.
- Windows installs generate a per-install 256-bit pipe auth token in `%LOCALAPPDATA%\RevitMcpNext\config\auth.env` and restrict the file ACL to the current user, Administrators, and SYSTEM when Windows allows it.
- The generated MCP launcher reads `auth.env`, exports `REVIT_MCP_NEXT_AUTH_TOKEN` for the broker process, and does not print the raw token. `revitctl` uses the same installed discovery and auth config.
- The add-in enforces the pipe auth token when configured. Requests with missing or incorrect tokens are rejected before Revit operations are dispatched.
- Doctor, support-bundle, and release-evidence paths report token presence/shape only. Support bundles redact auth config values and known local profile paths.
- Write operations use bounded preview/apply contracts. Normal model edits require `revit.preview_change_set` before `revit.apply_change_set`, and apply requires exact preview metadata plus `confirm: true`.
- Existing-element writes support identity guards such as `expectedUniqueId` on supported operations, and high-risk deletes report Revit's dependent delete set before apply.
- Raw bridge ingress validates operation kind so write/debug operations cannot be mislabeled as reads.
- pyRevit and Dynamo examples use the in-process bridge helper instead of calling the named pipe from inside Revit, avoiding deadlocks and keeping hosted scripts on the same bounded dispatcher.
- The Dynamo smoke runners do not change Dynamo privacy settings, preseed consent, or click startup prompts. First-run Autodesk/Dynamo prompts must be handled manually in the target test profile.

## Release Boundaries

- Unsigned or locally dev-signed packages must be labeled as such. Do not claim a signed enterprise production release without archived Authenticode signing and verification evidence.
- Local dev signing certificates are for disposable smoke machines only. Use a real release certificate for public signed-release claims.
- Do not run destructive smoke workflows on production models. Live smoke creates, edits, tags, pins, unpins, and deletes test elements.
- Do not enable arbitrary code execution or a `send_code_to_revit` style feature by default. Any future code-execution capability must require explicit local opt-in, warning text, and separate audit logging.
