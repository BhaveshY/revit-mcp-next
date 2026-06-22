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

The initial C# add-in includes framing and queue scaffolding. The JSON serializer and generated contracts are deliberately isolated because the next milestone is canonical contract generation.

## Safety Boundaries

- All writes must go through preview/apply.
- Reads need no transaction.
- Writes use one explicit named transaction or a transaction group.
- No arbitrary code execution in normal mode.
- Large results are paginated or exposed as MCP resources.

