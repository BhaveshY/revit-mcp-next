# Architecture

`revit-mcp-next` uses a two-process architecture.

## Broker

The broker is launched by MCP clients over stdio. It owns:

- MCP protocol.
- Tool registration and annotations.
- Input validation.
- Output shaping and token budgets.
- Pagination and resource handles.
- Client-specific startup and diagnostics.

The broker never references Autodesk DLLs and never calls the Revit API.

## Add-in

The Revit add-in is loaded in-process by Revit. It owns:

- Local IPC listener.
- Request queue.
- One `ExternalEvent` dispatcher.
- Revit API reads/writes.
- Transaction and failure handling.

The add-in never writes to stdout and never opens modal dialogs for automation paths.

## IPC

The intended IPC is Windows named pipes with length-prefixed JSON frames:

```text
uint32_be payload_length
utf8_json_payload
```

The broker and add-in exchange the canonical camelCase bridge envelope over this framing. The add-in preserves request IDs, validates protocol version, and returns compact structured data or structured bridge errors.

Windows installs provision a per-install pipe auth token in `%LOCALAPPDATA%\RevitMcpNext\config\auth.env`. The generated MCP launcher reads that file and exports `REVIT_MCP_NEXT_AUTH_TOKEN` before starting the broker. The broker forwards the token in the bridge envelope, and the add-in enforces it when `REVIT_MCP_NEXT_AUTH_TOKEN`, `REVIT_MCP_NEXT_AUTH_CONFIG`, or the default auth config is available. The token must not be logged.

## Safety Boundaries

- All writes must go through preview/apply.
- Reads need no transaction.
- Writes use one explicit named transaction or a transaction group.
- No arbitrary code execution in normal mode.
- Large results are paginated or exposed as MCP resources.
- Current end-to-end write handlers cover `set_parameter`, `create_level`, `create_wall`, `create_grid`, `create_floor`, `move_element`, `rotate_element`, `copy_element`, `change_element_type`, and `set_element_pinned`.

## External Automation Integrations

Claude and Codex enter through the MCP broker over stdio. Plain Python processes can do the same through `integrations/python/revit_mcp_next_client.py`.

pyRevit and Dynamo run inside Revit, so their examples use the add-in's public in-process bridge through `integrations/python/revit_mcp_next_inprocess.py`. That path shares the same bounded Revit dispatcher as the named-pipe route but does not queue through `ExternalEvent`, avoiding a common deadlock when a Revit-hosted script waits synchronously for work that Revit cannot process until the script returns.

Do not call the add-in named pipe directly from pyRevit or Dynamo. Direct pipe use from an in-process script can deadlock and bypasses the stable integration helpers.
