# Clean-Room Notes

This repo is a from-scratch implementation.

Allowed inspiration:

- Architectural lessons from the previous Revit MCP ecosystem.
- Public API behavior observed from Autodesk and MCP documentation.
- Workflow needs from existing Claude/Codex plugin packages.

Not allowed:

- Copying command handlers from existing Revit MCP projects.
- Copying duplicated tool schemas from existing Revit MCP projects.
- Preserving unsafe semantics such as silent caps, modal dialogs, raw `Success=false` payloads, or arbitrary code execution as a default tool.

The first implementation contracts are new and intentionally small.

