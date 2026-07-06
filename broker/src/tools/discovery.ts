import { McpServer, ResourceTemplate } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ErrorCode, McpError } from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import type { ProtocolVersion } from "@revit-mcp-next/contracts";

interface DiscoveryContext {
  brokerVersion: string;
  protocolVersion: ProtocolVersion;
}

interface ToolDiscovery {
  name: string;
  title: string;
  category: "session" | "read" | "analysis" | "catalog" | "write" | "debug";
  description: string;
  readOnly: boolean;
  destructive: boolean;
  idempotent: boolean;
  whenToUse: string;
  compactUse: string;
  related: string[];
}

export const toolDiscoveryCatalog: ToolDiscovery[] = [
  {
    name: "revit.status",
    title: "Revit Status",
    category: "session",
    description: "Check bridge health, active document/view, versions, capabilities, generation, and selection count.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Call first in every workflow.",
    compactUse: "Use the returned documentFingerprint and generation as guards on later reads and previews.",
    related: ["revit.list_documents", "revit.get_current_view"],
  },
  {
    name: "revit.list_documents",
    title: "List Revit Documents",
    category: "session",
    description: "List open Revit documents with fingerprints, active state, paths, active views, and generations.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use when multiple Revit documents are open or document targeting is ambiguous.",
    compactUse: "Prefer documentFingerprint instead of document title/path in follow-up calls.",
    related: ["revit.status"],
  },
  {
    name: "revit.create_project_from_template",
    title: "Create Project From Template",
    category: "write",
    description: "Create and save a disposable RVT project from a local RTE template through the Revit API.",
    readOnly: false,
    destructive: true,
    idempotent: false,
    whenToUse: "Use for fixture setup when live smoke needs a real .rvt created from an installed template.",
    compactUse: "Pass local .rte templatePath, disposable .rvt outputPath, overwrite=false unless replacing a known fixture, and confirm=true.",
    related: ["revit.status", "revit.list_documents", "revit.get_model_readiness"],
  },
  {
    name: "revit.get_levels",
    title: "Get Revit Levels",
    category: "read",
    description: "Return exact Revit level IDs and normalized elevations.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before creating walls, floors, rooms, or level-based families.",
    compactUse: "Read levels once and reuse IDs in preview payloads.",
    related: ["revit.preview_change_set", "revit.catalog"],
  },
  {
    name: "revit.get_views",
    title: "Get Revit Views",
    category: "read",
    description: "Return compact paginated view inventory for view/sheet planning.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use for finding graphical views, drafting views, templates, and sheet-placement candidates.",
    compactUse: "Use preset=summary or sheetPlacement, tight filters, and opaque cursor paging from structuredContent.data.cursor.",
    related: ["revit.get_sheets", "revit.catalog"],
  },
  {
    name: "revit.get_sheets",
    title: "Get Revit Sheets",
    category: "read",
    description: "Return compact paginated sheet inventory with optional placed-view details.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before creating sheets or placing views.",
    compactUse: "Only set includePlacedViews=true when checking placement conflicts.",
    related: ["revit.get_views", "revit.preview_change_set"],
  },
  {
    name: "revit.get_current_view",
    title: "Get Current Revit View",
    category: "read",
    description: "Return the active view with stable IDs, type, scale, detail metadata, generation, and optional crop box.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use when an operation depends on the active view context.",
    compactUse: "Leave includeCropBox=false unless placement bounds are needed.",
    related: ["revit.get_current_view_elements", "revit.status"],
  },
  {
    name: "revit.get_current_view_elements",
    title: "Get Current View Elements",
    category: "read",
    description: "Return a bounded paginated element list from the active Revit view.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use for visible-context edits, tags, and view-local audits.",
    compactUse:
      "Use preset=idOnly/summary, preset=geometrySummary when placement/bounds are needed, or explicit fields; page with the returned opaque cursor and unchanged arguments.",
    related: ["revit.query", "revit.get_current_view"],
  },
  {
    name: "revit.get_selection",
    title: "Get Revit Selection",
    category: "read",
    description: "Return selected Revit elements as a bounded paginated structured list.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use when the user points you at elements through Revit selection.",
    compactUse: "Use small limits and explicit fields before describing parameters.",
    related: ["revit.query", "revit.describe_parameters"],
  },
  {
    name: "revit.analyze_model",
    title: "Analyze Revit Model",
    category: "analysis",
    description: "Return bounded model totals and category/class/level breakdowns.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use for high-level model audits and planning.",
    compactUse: "Lower bucketLimit and disable breakdowns that are not needed.",
    related: ["revit.get_model_readiness", "revit.query"],
  },
  {
    name: "revit.get_model_readiness",
    title: "Get Revit Model Readiness",
    category: "analysis",
    description: "Return compact readiness for common workflows such as rooms, sheets, annotations, family placement, and type changes.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before deciding which automated workflow is feasible in a model.",
    compactUse: "Pass only the scenarios you care about.",
    related: ["revit.catalog", "revit.preview_change_set"],
  },
  {
    name: "revit.get_model_context",
    title: "Get Revit Model Context",
    category: "analysis",
    description: "Return compact planning context: project info, phases, worksets, design options, and Revit links.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before filtered reads or writes that depend on phase, workset, design option, or linked model context.",
    compactUse: "Keep section limits low and disable sections that are not relevant to the workflow.",
    related: ["revit.query", "revit.get_model_readiness", "revit.catalog"],
  },
  {
    name: "revit.get_material_quantities",
    title: "Get Material Quantities",
    category: "analysis",
    description: "Return bounded material takeoff quantities with normalized area and volume units.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use for material audits and quantity takeoff.",
    compactUse: "Use materialNameContains, maxElementsScanned, limit, and the returned opaque cursor to bound work.",
    related: ["revit.query"],
  },
  {
    name: "revit.get_warnings",
    title: "Get Revit Warnings",
    category: "analysis",
    description: "Return compact paginated model warnings with descriptions, severities, counts, and optional element IDs.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use for model health audits, cleanup planning, and before risky write workflows.",
    compactUse: "Start with preset=summary and low limits; use preset=elements only when element IDs are needed for follow-up revit.query calls.",
    related: ["revit.query", "revit.analyze_model", "revit.get_model_readiness"],
  },
  {
    name: "revit.get_rooms",
    title: "Get Revit Rooms",
    category: "read",
    description: "Return compact paginated room data with numbers, names, levels, area/volume, location, and schedule fields.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before room creation, room tagging, and room schedule edits.",
    compactUse: "Use preset=summary or explicit fields; include unplaced rooms only when needed.",
    related: ["revit.get_levels", "revit.preview_change_set"],
  },
  {
    name: "revit.catalog",
    title: "Revit Catalog",
    category: "catalog",
    description: "Return compact ID catalogs for safe writes: element types, family symbols, title blocks, view family types, text types, dimension types, and tags.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before any write requiring a Revit type, symbol, view family type, tag type, or title block ID.",
    compactUse: "Use kind, preset, filter.forElementId, category/class filters, fields, limit, and opaque cursor paging.",
    related: ["revit.preview_change_set"],
  },
  {
    name: "revit.query",
    title: "Query Revit Model",
    category: "read",
    description: "Run bounded Revit-native element queries with filters, projections, presets, counts, units, and pagination.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use instead of broad model dumps whenever you need element IDs or compact metadata.",
    compactUse:
      "Prefer explicit fields, preset=idOnly/summary/schedule, preset=geometrySummary for location/bounds, includeTotalCount=false, and opaque cursor paging.",
    related: ["revit.describe_parameters", "revit.get_current_view_elements", "revit.get_selection"],
  },
  {
    name: "revit.describe_parameters",
    title: "Describe Revit Parameters",
    category: "read",
    description: "Return bounded parameter metadata for targeted elements before parameter edits.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before set_parameter to confirm the exact writable parameter name and storage type.",
    compactUse: "Default preset is writableEdit; use namesOnly for discovery and full only when read-only/type/value details are needed.",
    related: ["revit.query", "revit.preview_change_set"],
  },
  {
    name: "revit.preview_change_set",
    title: "Preview Revit Change",
    category: "write",
    description: "Validate a bounded change set without mutating the model.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before every write operation.",
    compactUse: "Keep operations bounded, include documentFingerprint and expectedGeneration, and inspect blocked changes.",
    related: ["revit.apply_change_set", "revit.catalog", "revit.describe_parameters"],
  },
  {
    name: "revit.apply_change_set",
    title: "Apply Revit Change",
    category: "write",
    description: "Apply an already previewed change set in one named Revit transaction.",
    readOnly: false,
    destructive: true,
    idempotent: false,
    whenToUse: "Use only after a ready preview and user-confirmed intent.",
    compactUse: "Echo the exact operations plus previewId, baseGeneration, changeSetHash, expiresAt, and confirm=true.",
    related: ["revit.preview_change_set"],
  },
  {
    name: "revit.cancel_request",
    title: "Cancel Revit Request",
    category: "debug",
    description: "Ask the add-in to cancel queued or cancellable work when supported.",
    readOnly: false,
    destructive: false,
    idempotent: true,
    whenToUse: "Use for recovery when long-running work needs to be cancelled.",
    compactUse: "Prefer cancelling by requestId when available.",
    related: ["revit.status"],
  },
];

const catalogKinds = [
  "elementTypes",
  "familySymbols",
  "titleBlocks",
  "viewFamilyTypes",
  "textNoteTypes",
  "dimensionTypes",
  "tagTypes",
];

const writeOperations = [
  "set_parameter",
  "create_level",
  "create_wall",
  "place_family_instance",
  "create_sheet",
  "place_view_on_sheet",
  "create_text_note",
  "load_family",
  "tag_room",
  "tag_element",
  "move_element",
  "rotate_element",
  "copy_element",
  "change_element_type",
  "set_element_pinned",
  "create_grid",
  "create_floor",
  "create_room",
  "delete_element",
];

function toolUri(name: string): string {
  return `revit://tools/${encodeURIComponent(name)}`;
}

function jsonText(value: unknown): string {
  return JSON.stringify(value, null, 2);
}

function discoveryDocument(context: DiscoveryContext): Record<string, unknown> {
  return {
    name: "revit-mcp-next",
    brokerVersion: context.brokerVersion,
    protocolVersion: context.protocolVersion,
    resources: {
      discovery: "revit://discovery",
      toolTemplate: "revit://tools/{name}",
    },
    workflow: [
      "Start with revit.status and keep documentFingerprint/generation for guarded calls.",
      "Use revit.query, revit.catalog, and revit.describe_parameters with tight filters instead of broad dumps.",
      "Use revit.preview_change_set before every mutation and apply only a ready preview with matching token metadata.",
      "Treat blocked previews as useful model evidence; do not guess Revit IDs or force unsupported operations.",
    ],
    tokenEfficiency: {
      paging:
        "Prefer includeTotalCount=false. Cursors are opaque; repeat the same tool call with the same arguments and structuredContent.data.cursor, and stop when cursor is absent.",
      projection: "Use fields or presets whenever available.",
      parameters: "revit.describe_parameters defaults to preset=writableEdit; use full only when needed.",
    },
    catalogKinds,
    writeOperations,
    tools: toolDiscoveryCatalog.map((tool) => ({
      ...tool,
      resource: toolUri(tool.name),
    })),
  };
}

function toolDocument(tool: ToolDiscovery, context: DiscoveryContext): Record<string, unknown> {
  return {
    ...tool,
    brokerVersion: context.brokerVersion,
    protocolVersion: context.protocolVersion,
    resource: toolUri(tool.name),
    safety: tool.destructive
      ? "This tool can mutate Revit through guarded apply. Use only after preview evidence and user intent."
      : "This tool is safe for bounded discovery when arguments are scoped.",
  };
}

function findTool(name: string): ToolDiscovery | undefined {
  return toolDiscoveryCatalog.find((tool) => tool.name === name);
}

function stringVariable(value: string | string[] | undefined): string {
  return Array.isArray(value) ? value[0] ?? "" : value ?? "";
}

export function registerDiscovery(server: McpServer, context: DiscoveryContext): void {
  server.registerResource(
    "revit-discovery",
    "revit://discovery",
    {
      title: "Revit MCP Discovery",
      description: "Compact server capabilities, workflow guidance, catalog kinds, write operations, and tool resources.",
      mimeType: "application/json",
    },
    async (uri) => ({
      contents: [
        {
          uri: uri.href,
          mimeType: "application/json",
          text: jsonText(discoveryDocument(context)),
        },
      ],
    })
  );

  server.registerResource(
    "revit-tool-discovery",
    new ResourceTemplate("revit://tools/{name}", {
      list: async () => ({
        resources: toolDiscoveryCatalog.map((tool) => ({
          name: tool.name,
          title: tool.title,
          uri: toolUri(tool.name),
          description: tool.description,
          mimeType: "application/json",
        })),
      }),
      complete: {
        name: async (value) =>
          toolDiscoveryCatalog
            .map((tool) => tool.name)
            .filter((name) => name.toLowerCase().includes(value.toLowerCase()))
            .slice(0, 20),
      },
    }),
    {
      title: "Revit Tool Discovery",
      description: "Per-tool compact guidance for Revit MCP clients.",
      mimeType: "application/json",
    },
    async (uri, variables) => {
      const name = stringVariable(variables.name);
      const tool = findTool(name);
      if (!tool) {
        throw new McpError(ErrorCode.InvalidParams, `Unknown Revit MCP tool resource: ${name}`);
      }

      return {
        contents: [
          {
            uri: uri.href,
            mimeType: "application/json",
            text: jsonText(toolDocument(tool, context)),
          },
        ],
      };
    }
  );

  server.registerPrompt(
    "revit.start_workflow",
    {
      title: "Start Revit Workflow",
      description: "Create a concise plan for a safe, token-efficient Revit MCP workflow.",
    },
    async () => ({
      messages: [
        {
          role: "user",
          content: {
            type: "text",
            text:
              "Plan a safe Revit MCP workflow. Start with revit.status, use bounded reads with fields/presets and opaque cursors from structuredContent.data.cursor, discover IDs with revit.query/revit.catalog/revit.describe_parameters, preview every mutation with revit.preview_change_set, and only apply a ready preview with matching preview token metadata.",
          },
        },
      ],
    })
  );

  server.registerPrompt(
    "revit.workflow",
    {
      title: "Revit Workflow",
      description: "Workflow-specific Revit MCP call sequence guidance.",
      argsSchema: {
        workflow: z.enum(["audit", "selection-update", "sheet-planning", "family-placement", "room-layout"]),
      },
    },
    async ({ workflow }) => {
      const workflows: Record<typeof workflow, string> = {
        audit:
          "Audit workflow: call revit.status, revit.get_model_context with low section limits for phase/workset/design-option/link IDs, revit.get_model_readiness with focused scenarios, revit.analyze_model with bounded bucketLimit, revit.get_warnings with preset=summary for model health, then revit.query with explicit fields for any category or class that needs detail. Use preset=geometrySummary for element location/bounds checks and consume data.units.location/bounds, currently mm.",
        "selection-update":
          "Selection update workflow: call revit.status, revit.get_selection with preset=summary, revit.describe_parameters with preset=writableEdit for target IDs, preview set_parameter/change_element_type, then apply only the matching ready preview.",
        "sheet-planning":
          "Sheet planning workflow: call revit.status, revit.get_views with preset=sheetPlacement, revit.get_sheets with includePlacedViews only when needed, revit.catalog kind=titleBlocks, then preview create_sheet/place_view_on_sheet.",
        "family-placement":
          "Family placement workflow: call revit.status, revit.catalog kind=familySymbols preset=placement with tight filters, query candidate hosts/levels, preview place_family_instance, and treat blocked previews as discovery evidence.",
        "room-layout":
          "Room layout workflow: call revit.status, revit.get_levels, revit.get_rooms to avoid duplicate numbers, query room-bounding context when needed, then preview create_wall/create_room/tag_room before apply.",
      };

      return {
        messages: [
          {
            role: "user",
            content: {
              type: "text",
              text: workflows[workflow],
            },
          },
        ],
      };
    }
  );
}
