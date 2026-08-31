# Architecture

`revit-mcp-next` uses a two-process architecture.

## Broker

The broker is launched by MCP clients over stdio. It owns:

- MCP `2026-07-28` and supported legacy protocol negotiation through the TypeScript SDK v2 stdio server.
- Tool registration and annotations.
- Input validation.
- Output shaping and token budgets.
- Pagination. MCP resource handles are planned for larger export surfaces but are not part of the current broker contract.
- Client-specific startup and diagnostics.

Modern MCP connections are sessionless at the public protocol layer. The broker still uses explicit request IDs, preview IDs, document fingerprints, cursors, and a private bridge session ID because these values protect Revit writes and recover uncertain local IPC outcomes. Static tool, prompt, and resource discovery is deterministic and privately cacheable for five minutes. Live Revit reads and writes are never cached by this policy.

The broker never references Autodesk DLLs and never calls the Revit API.

## Add-in

The Revit add-in is loaded in-process by Revit. It owns:

- Local IPC listener.
- Request queue.
- One `ExternalEvent` dispatcher.
- Revit API reads/writes.
- Transaction and failure handling.
- Queue, ExternalEvent, and preview-token diagnostics surfaced through `revit.status`.
- A bounded add-in-process request-outcome ledger for at-most-once write handling and response recovery.

The add-in never writes to stdout and never opens modal dialogs for automation paths.

## IPC

The intended IPC is Windows named pipes with length-prefixed JSON frames:

```text
uint32_be payload_length
utf8_json_payload
```

The broker and add-in exchange the canonical camelCase bridge envelope over this framing. The add-in preserves request IDs, validates `bridgeProtocolVersion`, and returns compact structured data or structured bridge errors. The JSON wire key remains `protocolVersion` for compatibility. This private version is independent of the negotiated MCP protocol version.

Each running Revit add-in gets a stable runtime instance ID and unique normal/control pipes named from the configured base pipe. It publishes only safe connection metadata under `%LOCALAPPDATA%\RevitMcpNext\instances`; authentication tokens are never written to these registrations. The broker ignores/removes stale registrations and maintains one bridge client per live instance. If no current registration exists, it retains the legacy base-pipe path for an older single-instance add-in.

`revit.bridge_health`, queued cancellation, and request-result lookup use the selected instance's control pipe, so they remain available when normal workers are occupied or stalled. Mutation requests are fingerprinted and tracked by broker process session ID and request ID. If the broker loses a mutation response, it asks that same instance's ledger for the terminal result and never replays the original write. The guarantee is bounded to the lifetime of the current add-in process and ledger retention window.

The MCP broker owns an in-memory target store scoped to its generated session ID. An explicit target contains runtime `instanceId`, `documentFingerprint`, and document generation. The router injects it into every document-scoped request. Multiple open documents require selection, and a missing/closed target fails without falling back to Revit's active tab. Preview tokens are keyed and validated by MCP session, runtime instance, document fingerprint, generation, and exact change-set hashes.

Queued Revit responses expose optional `queueWaitMs` and `revitExecutionMs` phase metrics. These separate queue pressure from time spent on the Revit external-event thread while preserving the existing operation-local `elapsedMs`.

Windows installs provision a per-install pipe auth token in `%LOCALAPPDATA%\RevitMcpNext\config\auth.env`. The generated MCP launcher reads that file and exports `REVIT_MCP_NEXT_AUTH_TOKEN` before starting the broker. The broker forwards the token in the bridge envelope, and the add-in enforces it when `REVIT_MCP_NEXT_AUTH_TOKEN`, `REVIT_MCP_NEXT_AUTH_CONFIG`, or the default auth config is available. The token must not be logged.

## Safety Boundaries

- All writes must go through preview/apply.
- Preview tokens are bound to the originating broker process session ID, active document generation, and canonical typed operation payload.
- Reads need no transaction.
- Writes use one explicit named transaction or a transaction group.
- No arbitrary code execution in normal mode.
- Large current-contract results are paginated and bounded. MCP resource handles are reserved for future large export surfaces.
- Read/analysis tools return bounded structured data for current views, sheets, schedules, active-view elements, selection, model statistics, model-readiness preflights, material quantities, catalogs, rooms, and custom queries.
- Current end-to-end write handlers cover `set_parameter`, `create_level`, `create_wall`, `create_grid`, `create_floor`, `create_room`, `place_family_instance`, guarded `load_family`, `create_sheet`, `place_view_on_sheet`, `create_schedule`, `add_schedule_field`, `place_schedule_on_sheet`, `create_text_note`, `tag_room`, `tag_element`, `move_element`, `rotate_element`, `copy_element`, `change_element_type`, `rename_element_type`, `duplicate_element_type`, `set_element_pinned`, and guarded `delete_element`. The direct `revit.create_project_from_template` setup tool creates disposable `.rvt` projects from local `.rte` templates for smoke fixtures; it is intentionally outside the preview/apply edit path.
- `revit.cancel_request` uses the reserved control pipe and atomically cancels only requests that are still queued. Running Revit API work is reconciled through the request-outcome ledger because interrupting an active Revit transaction is not safe.

## External Automation Integrations

Claude and Codex enter through the MCP broker over stdio. Plain Python processes can do the same through `integrations/python/revit_mcp_next_client.py`.

The installed `revitctl.cmd` is a lower-level bridge CLI for debugging, support, and scripted CI smoke checks. It reuses the same named-pipe bridge contract and auth config as the MCP broker, but it is not the primary agent interface. MCP remains the "agent brain socket" because it exposes typed tools, descriptions, annotations, and output schemas.

pyRevit and Dynamo run inside Revit, so their examples use the add-in's public in-process bridge through `integrations/python/revit_mcp_next_inprocess.py`. That path shares the same bounded Revit dispatcher as the named-pipe route but does not queue through `ExternalEvent`, avoiding a common deadlock when a Revit-hosted script waits synchronously for work that Revit cannot process until the script returns.

Do not call the add-in named pipe directly from pyRevit or Dynamo. Direct pipe use from an in-process script can deadlock and bypasses the stable integration helpers.
