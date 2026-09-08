import {
  McpServer,
  ProtocolError,
  ProtocolErrorCode,
  ResourceTemplate,
} from "@modelcontextprotocol/server";
import * as z from "zod/v4";
import type { BridgeProtocolVersion } from "@revit-mcp-next/contracts";

interface DiscoveryContext {
  brokerVersion: string;
  bridgeProtocolVersion: BridgeProtocolVersion;
}

interface ToolDiscovery {
  name: string;
  title: string;
  category: "session" | "read" | "analysis" | "catalog" | "write" | "delivery" | "debug";
  description: string;
  readOnly: boolean;
  destructive: boolean;
  idempotent: boolean;
  whenToUse: string;
  compactUse: string;
  related: string[];
}

export const toolDiscoveryCatalog: ToolDiscovery[] = [
{"name": "revit.get_view_details", "title": "Get View Details", "category": "read", "description": "Read exact view settings, crop, view range and sheet placements.", "readOnly": true, "destructive": false, "idempotent": true, "whenToUse": "Read exact view settings, crop, view range and sheet placements.", "compactUse": "Supply exact instanceId and documentFingerprint; use IDs from discovery.", "related": ["revit.get_views", "revit.preview_change_set"]},
{"name": "revit.get_dimensions", "title": "Get Dimensions", "category": "read", "description": "Read explicit dimension references, type, curve, segments and text in mm.", "readOnly": true, "destructive": false, "idempotent": true, "whenToUse": "Read explicit dimension references, type, curve, segments and text in mm.", "compactUse": "Supply exact instanceId and documentFingerprint; use IDs from discovery.", "related": ["revit.get_views", "revit.preview_change_set"]},
{"name": "revit.activate_view", "title": "Activate View", "category": "session", "description": "Activate a view by ID or unique exact name in the targeted active UI document.", "readOnly": false, "destructive": false, "idempotent": true, "whenToUse": "Activate a view by ID or unique exact name in the targeted active UI document.", "compactUse": "Supply exact instanceId and documentFingerprint; use IDs from discovery.", "related": ["revit.get_views", "revit.preview_change_set"]},
  {
    name: "revit.list_instances",
    title: "List Revit Instances",
    category: "session",
    description: "List every live Revit process, version, runtime instance ID, and open document with exact fingerprints and generations.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use first when more than one Revit process or project may be open.",
    compactUse: "Choose an exact instanceId and documentFingerprint, then call revit.set_target.",
    related: ["revit.list_documents", "revit.set_target", "revit.get_target"],
  },
  {
    name: "revit.set_target",
    title: "Set Revit Session Target",
    category: "session",
    description: "Bind this MCP session to one exact Revit runtime instance, document fingerprint, and generation.",
    readOnly: false,
    destructive: false,
    idempotent: true,
    whenToUse: "Use after listing instances/documents and whenever switching projects.",
    compactUse: "Pass exact IDs from discovery; document titles are intentionally insufficient.",
    related: ["revit.list_instances", "revit.get_target", "revit.clear_target"],
  },
  {
    name: "revit.get_target",
    title: "Get Revit Session Target",
    category: "session",
    description: "Return and validate this MCP session's selected Revit instance/document target.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use to confirm the project before a sensitive preview or write.",
    compactUse: "Treat unavailable or generation-changed errors as a requirement to re-list and re-select.",
    related: ["revit.set_target", "revit.list_instances", "revit.clear_target"],
  },
  {
    name: "revit.clear_target",
    title: "Clear Revit Session Target",
    category: "session",
    description: "Clear only this MCP session's selected Revit target without changing another Codex session.",
    readOnly: false,
    destructive: false,
    idempotent: true,
    whenToUse: "Use when ending a project-specific workflow or before selecting another target explicitly.",
    compactUse: "After clearing, multiple open documents block document-scoped calls until a new target is selected.",
    related: ["revit.set_target", "revit.get_target", "revit.list_instances"],
  },
  {
    name: "revit.bridge_health",
    title: "Revit Bridge Health",
    category: "debug",
    description: "Check the reserved control path, listener pool, queue pressure, faults, and request-outcome ledger without entering the Revit work queue.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use when normal Revit calls stall, disconnect, or have an uncertain outcome.",
    compactUse: "Confirm healthy=true and inspect controlPipeName, queue, connections, faults, and requestOutcomes before deciding whether to wait, cancel, or recover a request.",
    related: ["revit.status", "revit.get_request_result", "revit.cancel_request"],
  },
  {
    name: "revit.get_request_result",
    title: "Get Revit Request Result",
    category: "debug",
    description: "Query the reserved control path for the retained state and terminal response of an original bridge request without replaying it.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use after a timeout or disconnect when a write may already have reached Revit and its outcome is uncertain.",
    compactUse: "Pass the exact original requestId from the uncertain call. A missing result can mean the request was not accepted, belongs to another session, expired, was evicted, or the add-in restarted; never treat it as proof that replay is safe.",
    related: ["revit.bridge_health", "revit.status", "revit.cancel_request"],
  },
  {
    name: "revit.status",
    title: "Revit Status",
    category: "session",
    description: "Check bridge health, active document/view, versions, capabilities, generation, selection count, queue diagnostics, and preview-token health.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Call after target selection, or directly when exactly one Revit instance is open.",
    compactUse: "Confirm the response target identity. Inspect diagnostics.queue and diagnostics.previewTokens before retrying stalled workflows.",
    related: ["revit.list_instances", "revit.get_target", "revit.get_current_view"],
  },
  {
    name: "revit.read_bundle",
    title: "Revit Read Bundle",
    category: "read",
    description: "Compose compact guarded reads for common agent preflight workflows in one MCP call.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use after or instead of separate first-step reads when planning a workflow and you need status plus a few small context sections.",
    compactUse:
      "Keep defaults for status/levels/readiness/current view/selection. Add small catalogs or parameter requests only for the next planned edit.",
    related: ["revit.status", "revit.get_model_readiness", "revit.catalog", "revit.describe_parameters"],
  },
  {
    name: "revit.list_documents",
    title: "List Revit Documents",
    category: "session",
    description: "List open Revit documents across all live instances, or filter by exact runtime instance ID.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use when multiple Revit documents are open or document targeting is ambiguous.",
    compactUse: "Use instanceId plus documentFingerprint with revit.set_target; never select duplicate titles by name alone.",
    related: ["revit.list_instances", "revit.set_target", "revit.status"],
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
    name: "revit.get_schedules",
    title: "Get Revit Schedules",
    category: "read",
    description: "Return compact paginated schedule inventory with category and optional field details.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before editing, placing, or auditing schedules.",
    compactUse: "Use preset=summary first; includeFields=true only when field layout is needed.",
    related: ["revit.get_schedule_fields", "revit.preview_change_set"],
  },
  {
    name: "revit.get_schedule_fields",
    title: "Get Revit Schedule Fields",
    category: "read",
    description: "Return existing and available schedulable fields for a schedule or schedule category.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use before create_schedule or add_schedule_field so field names/IDs are exact.",
    compactUse: "Filter by nameContains and keep limit bounded; use category when planning a new schedule.",
    related: ["revit.get_schedules", "revit.preview_change_set"],
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
    whenToUse: "Use before set_parameter to select a stable parameter identity, storage type, spec, and unit.",
    compactUse: "Prefer builtInParameter, sharedParameterGuid, or definitionId from writableEdit results. Use an unambiguous name only when no stable identity exists.",
    related: ["revit.query", "revit.preview_change_set"],
  },
  {
    name: "revit.inspect_model_delivery",
    title: "Inspect Revit Model Delivery",
    category: "delivery",
    description: "Inspect delivery sources and compare them with an explicit or automatically loaded project recipe.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use on first delivery and before repeat deliveries when source files or links may have changed.",
    compactUse: "Pass local, mapped-drive, or UNC RVT paths; pass projectId to auto-load the latest saved recipe.",
    related: ["revit.get_model_delivery_recipe", "revit.preview_model_delivery"],
  },
  {
    name: "revit.save_model_delivery_recipe",
    title: "Save Model Delivery Recipe",
    category: "delivery",
    description: "Persist an approved project recipe in the broker's integrity-checked local recipe library.",
    readOnly: false,
    destructive: false,
    idempotent: true,
    whenToUse: "Use after a successful first delivery or an approved recipe change.",
    compactUse: "Save the full approved recipe; on updates, pass expectedRecipeSha256 from get/list to prevent stale overwrites.",
    related: ["revit.get_model_delivery_recipe", "revit.list_model_delivery_recipes"],
  },
  {
    name: "revit.get_model_delivery_recipe",
    title: "Get Model Delivery Recipe",
    category: "delivery",
    description: "Load the latest integrity-checked recipe for one project without requiring Revit to be open.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use to review a project's saved delivery decisions or obtain its concurrency hash.",
    compactUse: "Pass the stable projectId.",
    related: ["revit.inspect_model_delivery", "revit.save_model_delivery_recipe"],
  },
  {
    name: "revit.list_model_delivery_recipes",
    title: "List Model Delivery Recipes",
    category: "delivery",
    description: "List saved project recipe summaries and integrity hashes without returning full recipe bodies.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use when the architect does not remember the exact stable projectId or wants the current recipe inventory.",
    compactUse: "No arguments for the latest 100 projects; raise limit only when needed.",
    related: ["revit.get_model_delivery_recipe", "revit.save_model_delivery_recipe"],
  },
  {
    name: "revit.preview_model_delivery",
    title: "Preview Revit Model Delivery",
    category: "delivery",
    description: "Validate the exact standalone-model, link, cleanup, export, and QA delivery plan without publishing files.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Use after inspection and before every delivery execution.",
    compactUse: "Resolve every blocker and approve the returned planHash before execution.",
    related: ["revit.inspect_model_delivery", "revit.execute_model_delivery"],
  },
  {
    name: "revit.execute_model_delivery",
    title: "Execute Revit Model Delivery",
    category: "delivery",
    description: "Start an approved, staged, validated, atomic model-delivery job.",
    readOnly: false,
    destructive: true,
    idempotent: false,
    whenToUse: "Use only with a ready preview and explicit architect approval.",
    compactUse: "Echo the exact recipe, previewId, planHash, expiry, and confirm=true; then follow job status.",
    related: ["revit.preview_model_delivery", "revit.get_model_delivery_status", "revit.cancel_model_delivery"],
  },
  {
    name: "revit.get_model_delivery_status",
    title: "Get Revit Model Delivery Status",
    category: "delivery",
    description: "Read phase, progress, per-model QA, errors, and publication state for a delivery job.",
    readOnly: true,
    destructive: false,
    idempotent: true,
    whenToUse: "Poll an active delivery job until it reaches succeeded, failed, or cancelled.",
    compactUse: "Pass the jobId returned by execute_model_delivery.",
    related: ["revit.execute_model_delivery", "revit.cancel_model_delivery"],
  },
  {
    name: "revit.cancel_model_delivery",
    title: "Cancel Revit Model Delivery",
    category: "delivery",
    description: "Request cancellation at the next safe delivery checkpoint without publishing a partial package.",
    readOnly: false,
    destructive: false,
    idempotent: true,
    whenToUse: "Use when the architect stops an active job or a new issue is discovered during processing.",
    compactUse: "Pass jobId and an optional reason, then confirm terminal status.",
    related: ["revit.get_model_delivery_status"],
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
  "create_plan_view",
  "duplicate_view",
  "duplicate_sheet",
  "copy_view_annotations",
  "create_dimension",
  "update_dimension",

  "set_parameter",
  "create_level",
  "create_wall",
  "place_family_instance",
  "create_sheet",
  "place_view_on_sheet",
  "create_schedule",
  "add_schedule_field",
  "place_schedule_on_sheet",
  "create_text_note",
  "load_family",
  "tag_room",
  "tag_element",
  "move_element",
  "rotate_element",
  "copy_element",
  "change_element_type",
  "rename_element_type",
  "duplicate_element_type",
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
    bridgeProtocolVersion: context.bridgeProtocolVersion,
    protocolVersion: context.bridgeProtocolVersion,
    deprecatedAliases: {
      protocolVersion: "Use bridgeProtocolVersion. This is the private Revit bridge version, not the negotiated MCP protocol version.",
    },
    resources: {
      discovery: "revit://discovery",
      toolTemplate: "revit://tools/{name}",
    },
    workflow: [
      "Start with revit.status and keep documentFingerprint/generation for guarded calls.",
      "Inspect revit.status diagnostics when a request stalls: queue depth, ExternalEvent raise state, preview-token counts, and recovery hints are compact.",
      "Use revit.read_bundle for compact workflow preflight when you need status, readiness, current context, small catalogs, and parameter metadata in one MCP call.",
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
    bridgeProtocolVersion: context.bridgeProtocolVersion,
    protocolVersion: context.bridgeProtocolVersion,
    deprecatedAliases: {
      protocolVersion: "Use bridgeProtocolVersion. This is the private Revit bridge version, not the negotiated MCP protocol version.",
    },
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
        throw new ProtocolError(
          ProtocolErrorCode.InvalidParams,
          `Unknown Revit MCP tool resource: ${name}`
        );
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
      argsSchema: z.object({
        workflow: z.enum(["audit", "selection-update", "sheet-planning", "family-placement", "room-layout"]),
      }),
    },
    async ({ workflow }) => {
      const workflows: Record<typeof workflow, string> = {
        audit:
          "Audit workflow: call revit.status, revit.get_model_context with low section limits for phase/workset/design-option/link IDs, revit.get_model_readiness with focused scenarios, revit.analyze_model with bounded bucketLimit, revit.get_warnings with preset=summary for model health, then revit.query with explicit fields for any category or class that needs detail. Use preset=geometrySummary for element location/bounds checks and consume data.units.location/bounds, currently mm.",
        "selection-update":
          "Selection update workflow: call revit.status, revit.get_selection with preset=summary, revit.describe_parameters with preset=writableEdit for target IDs, preview set_parameter/change_element_type, then apply only the matching ready preview.",
        "sheet-planning":
          "Sheet planning workflow: call revit.status, revit.get_views with preset=sheetPlacement, revit.get_sheets with includePlacedViews only when needed, revit.get_schedules and revit.get_schedule_fields when schedule creation or placement is needed, revit.catalog kind=titleBlocks, then preview create_sheet/place_view_on_sheet/create_schedule/add_schedule_field/place_schedule_on_sheet.",
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
