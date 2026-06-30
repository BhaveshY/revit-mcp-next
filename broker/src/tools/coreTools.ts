import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ProtocolVersion } from "@revit-mcp-next/contracts";
import type { RevitBridgeClient } from "../ipc/RevitBridgeClient.js";
import { makeRequest } from "../ipc/RequestFactory.js";
import { asToolResult } from "./toolResult.js";

interface CoreToolContext {
  bridge: RevitBridgeClient;
  brokerVersion: string;
  sessionId: string;
  protocolVersion: ProtocolVersion;
}

const boundedString = z.string().min(1).max(128);
const boundedId = z.string().min(1).max(64);
const generationSchema = z.number().int().min(0);
const parameterScalar = z.union([z.string().max(256), z.number(), z.boolean()]);
const parameterEqualsSchema = z
  .record(boundedString, parameterScalar)
  .refine((value) => Object.keys(value).length <= 16, "At most 16 parameter equality filters are allowed.");

const queryFilterSchema = z.object({
  elementIds: z
    .array(boundedId)
    .max(256)
    .optional()
    .describe("Explicit Revit element IDs to retrieve before applying any secondary filters."),
  uniqueIds: z
    .array(boundedString)
    .max(256)
    .optional()
    .describe("Explicit Revit UniqueId values to retrieve before applying any secondary filters."),
  categories: z.array(boundedString).max(16).optional(),
  classes: z.array(boundedString).max(16).optional(),
  viewId: boundedId.optional(),
  selectionOnly: z.boolean().optional(),
  levelIds: z.array(boundedId).max(64).optional(),
  worksetIds: z.array(boundedId).max(64).optional(),
  designOptionIds: z.array(boundedId).max(64).optional(),
  parameterEquals: parameterEqualsSchema.optional(),
});
const scopedQueryFilterSchema = queryFilterSchema.omit({ viewId: true, selectionOnly: true });

const documentGuardSchema = {
  documentFingerprint: boundedString.optional().describe("Optional active document fingerprint from revit.status."),
  expectedGeneration: generationSchema.optional().describe("Expected active document generation from revit.status."),
};

const querySchema = {
  filter: queryFilterSchema.describe("Revit-native filters to apply before projection."),
  fields: z.array(boundedString).max(32).optional().describe("Fields to return. Prefer explicit fields for token efficiency."),
  preset: z.enum(["idOnly", "summary", "schedule", "geometrySummary"]).optional(),
  limit: z.number().int().min(1).max(500).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(false),
};

const currentViewSchema = {
  ...documentGuardSchema,
  includeCropBox: z.boolean().default(false).describe("Include active view crop box bounds when Revit exposes them."),
};

const scopedElementListSchema = {
  ...documentGuardSchema,
  filter: scopedQueryFilterSchema.optional().describe("Additional filters within the tool scope. viewId and selectionOnly are set by the tool."),
  fields: z.array(boundedString).max(32).optional().describe("Fields to return. Prefer explicit fields for token efficiency."),
  preset: z.enum(["idOnly", "summary", "schedule", "geometrySummary"]).optional(),
  includeHidden: z.boolean().default(false).describe("Request hidden elements when supported by the Revit view collector."),
  limit: z.number().int().min(1).max(500).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(false),
};

const modelStatisticsSchema = {
  ...documentGuardSchema,
  includeCategoryBreakdown: z.boolean().default(true),
  includeClassBreakdown: z.boolean().default(true),
  includeLevelBreakdown: z.boolean().default(true),
  bucketLimit: z.number().int().min(1).max(200).default(50),
  maxElementsScanned: z
    .number()
    .int()
    .min(100)
    .max(100000)
    .default(50000)
    .describe("Maximum non-type elements to scan for grouped statistics before returning partial results."),
};

const readinessScenarioSchema = z.enum([
  "levels",
  "wallCreation",
  "floorCreation",
  "roomCreation",
  "roomReadback",
  "typeChange",
  "familyPlacement",
  "selection",
  "annotations",
]);

const modelReadinessSchema = {
  ...documentGuardSchema,
  scenarios: z
    .array(readinessScenarioSchema)
    .max(16)
    .optional()
    .describe("Optional subset of readiness scenarios to return. Omit for the common agent workflow set."),
  includeHints: z.boolean().default(true).describe("Include compact candidate IDs and next actions when available."),
};

const materialQuantitiesSchema = {
  ...documentGuardSchema,
  filter: queryFilterSchema.optional().describe("Optional model scope. Use selectionOnly or viewId for focused takeoffs."),
  materialNameContains: z.string().min(1).max(128).optional(),
  includePaint: z.boolean().default(false).describe("Include paint material quantities where Revit reports them."),
  maxElementsScanned: z.number().int().min(1).max(100000).default(20000),
  limit: z.number().int().min(1).max(200).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(false),
};

const roomFilterSchema = z
  .object({
    elementIds: z.array(boundedId).max(256).optional().describe("Explicit room element IDs."),
    uniqueIds: z.array(boundedString).max(256).optional().describe("Explicit room UniqueId values."),
    levelIds: z.array(boundedId).max(64).optional().describe("Optional room level IDs."),
    phaseIds: z.array(boundedId).max(16).optional().describe("Optional created phase IDs."),
    numbers: z.array(z.string().min(1).max(64)).max(128).optional().describe("Exact room numbers to return."),
    numberContains: z.string().min(1).max(64).optional(),
    nameContains: z.string().min(1).max(128).optional(),
    departmentContains: z.string().min(1).max(128).optional(),
  })
  .strict();

const roomsSchema = {
  ...documentGuardSchema,
  filter: roomFilterSchema.optional().describe("Optional room-specific filters. Results remain bounded and paginated."),
  fields: z
    .array(boundedString)
    .max(32)
    .optional()
    .describe("Room fields to return. Use explicit fields for compact room export."),
  preset: z.enum(["idOnly", "summary", "schedule"]).default("summary"),
  limit: z.number().int().min(1).max(500).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(false),
  includeUnplaced: z.boolean().default(false),
};

const catalogFilterSchema = z
  .object({
    forElementId: boundedId
      .optional()
      .describe("Optional instance element ID. When supplied for elementTypes, only Revit-valid replacement type IDs are returned."),
    categories: z
      .array(boundedString)
      .max(16)
      .optional()
      .describe("Optional Revit category filters such as OST_Walls or OST_Floors."),
    classes: z
      .array(boundedString)
      .max(16)
      .optional()
      .describe("Optional ElementType class filters such as WallType, FloorType, or FamilySymbol."),
    familyName: z.string().min(1).max(128).optional(),
    familyNameContains: z.string().min(1).max(128).optional(),
    nameContains: z.string().min(1).max(128).optional(),
    viewFamily: z.array(boundedString).max(16).optional(),
    parameterEquals: parameterEqualsSchema.optional(),
  })
  .strict();

const catalogSchema = {
  kind: z.enum(["elementTypes", "familySymbols", "titleBlocks", "viewFamilyTypes"]),
  documentFingerprint: boundedString.optional().describe("Optional active document fingerprint from revit.status."),
  expectedGeneration: generationSchema.optional().describe("Expected active document generation from revit.status."),
  filter: catalogFilterSchema.optional().describe("Filters for compact Revit catalog discovery."),
  preset: z.enum(["idOnly", "compact", "typeChange", "placement", "sheet"]).default("compact"),
  fields: z.array(boundedString).max(32).optional().describe("Optional catalog fields. Use param:<name> for explicit parameters."),
  limit: z.number().int().min(1).max(200).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(false),
};

const changeScalarSchema = z.union([z.string().max(512), z.number(), z.boolean()]);
const changeUnitValueSchema = z.object({
  value: z.number(),
  unit: z.enum(["mm", "millimeters", "m", "meters", "ft", "feet", "revit-internal"]),
  system: z.enum(["metric", "imperial", "revit-internal"]).default("metric"),
});
const changeAngleValueSchema = z
  .object({
    value: z.number(),
    unit: z.enum(["degrees", "radians"]).default("degrees"),
  })
  .strict();
const changePoint3Schema = z
  .object({
    x: changeUnitValueSchema.describe("X coordinate with explicit units."),
    y: changeUnitValueSchema.describe("Y coordinate with explicit units."),
    z: changeUnitValueSchema.describe("Z coordinate with explicit units."),
  })
  .strict();
const changePoint2Schema = z
  .object({
    x: changeUnitValueSchema.describe("X coordinate with explicit units."),
    y: changeUnitValueSchema.describe("Y coordinate with explicit units."),
  })
  .strict();
const changeSetHashSchema = z.string().min(1).max(128);
const expiresAtSchema = z
  .string()
  .datetime({ offset: true })
  .describe("ISO 8601 expiry timestamp returned by preview_change_set and echoed to apply_change_set.");
const operationBaseSchema = z.object({
  id: boundedString.optional().describe("Optional client-supplied operation identifier for preview/apply correlation."),
});
const setParameterOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("set_parameter"),
    elementId: boundedId.describe("Target Revit element ID."),
    parameterName: boundedString.describe("Exact parameter name to set on the target element."),
    value: changeScalarSchema.describe("New parameter value as a string, number, or boolean."),
  })
  .strict();
const createLevelOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("create_level"),
    name: z.string().min(1).max(256).describe("Name for the new Revit level."),
    elevation: changeUnitValueSchema.describe("Level elevation with explicit units."),
  })
  .strict();
const createWallOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("create_wall"),
    levelId: boundedId.describe("Level ID that hosts the new wall."),
    start: changePoint3Schema.describe("Wall baseline start point."),
    end: changePoint3Schema.describe("Wall baseline end point."),
    wallTypeId: boundedId.optional().describe("Optional wall type ID. Uses the active/default wall type when omitted."),
    height: changeUnitValueSchema.optional().describe("Optional unconnected wall height."),
    structural: z.boolean().optional().describe("Whether to create the wall as structural."),
    flip: z.boolean().optional().describe("Whether to flip the wall orientation after creation."),
  })
  .strict();
const placeFamilyInstanceOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("place_family_instance"),
    familySymbolId: boundedId.describe("FamilySymbol ID from revit.catalog with kind=familySymbols and preset=placement."),
    hostElementId: boundedId
      .optional()
      .describe("Required for wall-hosted doors/windows and other hosted families. Use a valid host element ID."),
    levelId: boundedId.optional().describe("Required for level-based furniture, equipment, and fixture symbols; useful for hosted placement validation."),
    location: changePoint3Schema.describe("Insertion point with explicit units."),
    rotation: changeAngleValueSchema.optional().describe("Optional rotation around the family insertion point vertical axis."),
    flipFacing: z.boolean().optional().describe("Optionally flip facing after placement when supported."),
    flipHand: z.boolean().optional().describe("Optionally flip hand after placement when supported."),
    allowPinnedHost: z.boolean().optional().describe("Allow hosted placement on a pinned wall after explicit review. Defaults to false."),
  })
  .strict();
const moveElementOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("move_element"),
    elementId: boundedId.describe("Target Revit element ID."),
    translation: changePoint3Schema.describe("Translation vector to apply to the target element."),
  })
  .strict();
const rotateElementOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("rotate_element"),
    elementId: boundedId.describe("Target Revit element ID."),
    axisStart: changePoint3Schema.describe("Rotation axis start point."),
    axisEnd: changePoint3Schema.describe("Rotation axis end point."),
    angle: changeAngleValueSchema.describe("Signed rotation angle. Positive follows Revit's axis direction right-hand rule."),
  })
  .strict();
const copyElementOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("copy_element"),
    elementId: boundedId.describe("Source Revit element ID to duplicate."),
    translation: changePoint3Schema.describe("Translation vector from source to copied element."),
  })
  .strict();
const changeElementTypeOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("change_element_type"),
    elementId: boundedId.describe("Target Revit element ID."),
    typeId: boundedId.describe("New Revit element type ID. Use revit.catalog with kind=elementTypes and filter.forElementId first."),
  })
  .strict();
const setElementPinnedOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("set_element_pinned"),
    elementId: boundedId.describe("Target Revit element ID."),
    pinned: z.boolean().describe("Desired pinned state."),
    expectedPinned: z.boolean().optional().describe("Optional current pinned state guard."),
  })
  .strict();
const createGridOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("create_grid"),
    name: z.string().min(1).max(64).optional().describe("Optional unique grid name."),
    start: changePoint3Schema.describe("Grid line start point."),
    end: changePoint3Schema.describe("Grid line end point."),
  })
  .strict();
const createFloorOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("create_floor"),
    levelId: boundedId.describe("Level ID that hosts the floor."),
    outline: z
      .array(changePoint3Schema)
      .min(3)
      .max(64)
      .describe("Single closed floor boundary as ordered points. Repeat of the first point is optional."),
    floorTypeId: boundedId.optional().describe("Optional floor type ID. Uses the first available floor type when omitted."),
    structural: z.boolean().optional().describe("Whether to create a structural floor when the Revit API supports it."),
  })
  .strict();
const createRoomOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("create_room"),
    levelId: boundedId.describe("Level ID on which to place the room."),
    location: changePoint2Schema.describe("2D room placement point in the target level plane."),
    name: z.string().min(1).max(256).optional().describe("Optional room name."),
    number: z.string().min(1).max(64).optional().describe("Optional room number. Duplicate numbers are blocked unless explicitly allowed."),
    department: z.string().min(1).max(128).optional().describe("Optional room department schedule value."),
    allowDuplicateNumber: z.boolean().optional().describe("Allow duplicate room numbers. Defaults to false."),
  })
  .strict();
const deleteElementOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("delete_element"),
    elementId: boundedId.describe("Target Revit element ID to delete."),
    expectedUniqueId: boundedString.optional().describe("Optional uniqueId guard to avoid deleting an unexpected element if ids were reused."),
    expectedPinned: z.boolean().optional().describe("Optional current pinned state guard."),
    allowPinned: z.boolean().optional().describe("Must be true to delete a currently pinned element."),
  })
  .strict();
const changeOperationSchema = z.discriminatedUnion("type", [
  setParameterOperationSchema,
  createLevelOperationSchema,
  createWallOperationSchema,
  placeFamilyInstanceOperationSchema,
  moveElementOperationSchema,
  rotateElementOperationSchema,
  copyElementOperationSchema,
  changeElementTypeOperationSchema,
  setElementPinnedOperationSchema,
  createGridOperationSchema,
  createFloorOperationSchema,
  createRoomOperationSchema,
  deleteElementOperationSchema,
]);

const changeSetSchema = {
  documentFingerprint: boundedString.optional().describe("Active document fingerprint from revit.status or preview output."),
  expectedGeneration: generationSchema.optional().describe("Expected active document generation before previewing/applying."),
  baseGeneration: generationSchema.optional().describe("Document generation captured by preview_change_set and echoed to apply."),
  changeSetHash: changeSetHashSchema.optional().describe("Opaque hash for the exact previewed change set."),
  expiresAt: expiresAtSchema.optional(),
  transactionName: z.string().min(3).max(128).default("Revit MCP Next change"),
  operations: z.array(changeOperationSchema).min(1).max(50),
};

const applyChangeSchema = {
  ...changeSetSchema,
  previewId: boundedString.describe("The previewId returned by revit.preview_change_set for the exact same change set."),
  confirm: z.literal(true).describe("Must be true to apply a previewed change set."),
};

const cancelSchema = {
  requestId: boundedString.optional().describe("Optional bridge request ID to cancel when supported by the add-in."),
  reason: z.string().max(256).optional(),
};

const warningSchema = z.object({
  code: z.string(),
  message: z.string(),
  details: z.record(z.unknown()).optional(),
});

const metricsSchema = z.object({
  elapsedMs: z.number(),
  collectorElapsedMs: z.number().optional(),
  cacheHit: z.boolean().optional(),
  returnedCount: z.number().optional(),
  totalCount: z.number().optional(),
});

const toolOutputSchema = {
  data: z.unknown(),
  warnings: z.array(warningSchema),
  metrics: metricsSchema,
  generation: z.number().optional(),
};

export function registerCoreTools(server: McpServer, context: CoreToolContext): void {
  server.registerTool(
    "revit.status",
    {
      title: "Revit Status",
      description:
        "Check Revit bridge health, active document/view, versions, capabilities, and selection count. Start every Revit workflow here.",
      inputSchema: {},
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (_args, extra) => {
      const request = makeRequest(context.sessionId, "status", "read", {}, 5000);
      const response = await context.bridge.status(request, { signal: extra.signal });
      return asToolResult(response, (data) =>
        data.connected
          ? `Revit bridge connected. Active document: ${data.activeDocument?.title ?? "(none)"}.`
          : "Revit bridge is not connected."
      );
    }
  );

  server.registerTool(
    "revit.list_documents",
    {
      title: "List Revit Documents",
      description: "List open Revit documents with title, path, active flag, fingerprint, active view, and generation.",
      inputSchema: {},
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (_args, extra) => {
      const request = makeRequest(context.sessionId, "list_documents", "read", {}, 10000);
      const response = await context.bridge.listDocuments(request, { signal: extra.signal });
      return asToolResult(response, (docs) => `${docs.length} Revit document(s) open.`);
    }
  );

  server.registerTool(
    "revit.get_levels",
    {
      title: "Get Revit Levels",
      description: "Return exact Revit level IDs and elevations in normalized units.",
      inputSchema: {
        documentFingerprint: z.string().optional().describe("Optional document fingerprint from revit.status."),
      },
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const request = makeRequest(context.sessionId, "get_levels", "read", args, 10000);
      const response = await context.bridge.getLevels(request, { signal: extra.signal });
      return asToolResult(response, (levels) => `${levels.length} level(s) returned with exact IDs and elevations.`);
    }
  );

  server.registerTool(
    "revit.get_current_view",
    {
      title: "Get Current Revit View",
      description:
        "Return the active Revit view with stable IDs, view type, scale, detail metadata, generation, and optional crop box.",
      inputSchema: currentViewSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const request = makeRequest(context.sessionId, "get_current_view", "read", args, 10000);
      const response = await context.bridge.getCurrentView(request, { signal: extra.signal });
      return asToolResult(response, (result) => `Current view: ${result.view.name} (${result.view.type}).`);
    }
  );

  server.registerTool(
    "revit.get_current_view_elements",
    {
      title: "Get Current View Elements",
      description:
        "Return a bounded, paginated element list from the active Revit view. Use fields/preset and filters to keep responses compact.",
      inputSchema: scopedElementListSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const payload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        fields: args.fields,
        preset: args.preset,
        includeHidden: args.includeHidden ?? false,
        limit: args.limit ?? 50,
        cursor: args.cursor,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const request = makeRequest(context.sessionId, "get_current_view_elements", "read", payload, 30000);
      const response = await context.bridge.getCurrentViewElements(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} active-view element(s) returned.`
      );
    }
  );

  server.registerTool(
    "revit.get_selection",
    {
      title: "Get Revit Selection",
      description: "Return currently selected Revit elements as a bounded, paginated structured list.",
      inputSchema: scopedElementListSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const payload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: { ...(args.filter ?? {}), selectionOnly: true },
        fields: args.fields,
        preset: args.preset,
        limit: args.limit ?? 50,
        cursor: args.cursor,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const request = makeRequest(context.sessionId, "get_selection", "read", payload, 30000);
      const response = await context.bridge.getSelection(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} selected element(s) returned.`
      );
    }
  );

  server.registerTool(
    "revit.analyze_model",
    {
      title: "Analyze Revit Model",
      description:
        "Return compact model statistics: totals plus bounded category, class, and level breakdowns for audit and planning workflows.",
      inputSchema: modelStatisticsSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const request = makeRequest(context.sessionId, "analyze_model", "read", args, 60000);
      const response = await context.bridge.analyzeModel(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          `Analyzed ${result.scannedElements} element(s): ${result.totals.elements} total element(s), ${result.totals.materials} material(s).`
      );
    }
  );

  server.registerTool(
    "revit.get_model_readiness",
    {
      title: "Get Revit Model Readiness",
      description:
        "Return compact scenario readiness for common agent workflows: levels, walls, floors, rooms, type changes, family placement, selection, and annotations.",
      inputSchema: modelReadinessSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const payload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        scenarios: args.scenarios,
        includeHints: args.includeHints ?? true,
      };
      const request = makeRequest(context.sessionId, "get_model_readiness", "read", payload, 30000);
      const response = await context.bridge.getModelReadiness(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) => `${result.readyCount} of ${result.totalCount} Revit agent workflow scenario(s) ready.`
      );
    }
  );

  server.registerTool(
    "revit.get_material_quantities",
    {
      title: "Get Material Quantities",
      description:
        "Return bounded material takeoff quantities with normalized m2/m3 units, paging, filters, and scan limits.",
      inputSchema: materialQuantitiesSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const payload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        materialNameContains: args.materialNameContains,
        includePaint: args.includePaint ?? false,
        maxElementsScanned: args.maxElementsScanned ?? 20000,
        limit: args.limit ?? 50,
        cursor: args.cursor,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const request = makeRequest(context.sessionId, "get_material_quantities", "read", payload, 60000);
      const response = await context.bridge.getMaterialQuantities(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} material quantity row(s) returned from ${result.scope}.`
      );
    }
  );

  server.registerTool(
    "revit.get_rooms",
    {
      title: "Get Revit Rooms",
      description:
        "Return compact, paginated room export data with room numbers, names, levels, area/volume units, location, and schedule fields.",
      inputSchema: roomsSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const payload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        fields: args.fields,
        preset: args.preset ?? "summary",
        limit: args.limit ?? 50,
        cursor: args.cursor,
        includeTotalCount: args.includeTotalCount ?? false,
        includeUnplaced: args.includeUnplaced ?? false,
      };
      const request = makeRequest(context.sessionId, "get_rooms", "read", payload, 30000);
      const response = await context.bridge.getRooms(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} room(s) returned from ${result.scope}.`
      );
    }
  );

  server.registerTool(
    "revit.catalog",
    {
      title: "Revit Catalog",
      description:
        "Return compact Revit catalog IDs for safe writes: element types, family symbols, title blocks, and view family types. Use kind=elementTypes with filter.forElementId before change_element_type.",
      inputSchema: catalogSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const payload = {
        kind: args.kind,
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        preset: args.preset ?? "compact",
        fields: args.fields,
        limit: args.limit ?? 50,
        cursor: args.cursor,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const request = makeRequest(context.sessionId, "catalog", "read", payload, 30000);
      const response = await context.bridge.catalog(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} ${result.kind} catalog item(s) returned from ${result.scope}.`
      );
    }
  );

  server.registerTool(
    "revit.query",
    {
      title: "Query Revit Model",
      description:
        "Run a bounded Revit model query with native filters, explicit projection, counts, units, and pagination. Use this instead of broad list dumps.",
      inputSchema: querySchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const payload = {
        filter: args.filter,
        fields: args.fields,
        preset: args.preset,
        limit: args.limit ?? 50,
        cursor: args.cursor,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const request = makeRequest(context.sessionId, "query", "read", payload, 30000);
      const response = await context.bridge.query(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} item(s) returned from ${result.scope}.`
      );
    }
  );

  server.registerTool(
    "revit.preview_change_set",
    {
      title: "Preview Revit Change",
      description:
        "Validate a bounded change set without mutating the model. Use this before revit.apply_change_set. Supported operations: set_parameter, create_level, create_wall, place_family_instance, move_element, rotate_element, copy_element, change_element_type, set_element_pinned, create_grid, create_floor, create_room, and delete_element.",
      inputSchema: changeSetSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const request = makeRequest(context.sessionId, "preview_change_set", "preview", args, 30000);
      const response = await context.bridge.previewChange(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          `${result.ready ? "Ready" : "Blocked"} preview ${result.previewId}: ${result.operationCount} operation(s), ${result.riskLevel} risk.`
      );
    }
  );

  server.registerTool(
    "revit.apply_change_set",
    {
      title: "Apply Revit Change",
      description:
        "Apply a previously previewed bounded change set in one named Revit transaction. Requires confirm=true and the matching previewId.",
      inputSchema: applyChangeSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const request = makeRequest(context.sessionId, "apply_change_set", "write", args, 60000);
      const response = await context.bridge.applyChange(request, { signal: extra.signal });
      return asToolResult(
        response,
        (result) =>
          result.applied
            ? `Applied ${result.changedCount} change(s) in transaction "${result.transactionName}".`
            : `No changes applied for preview ${result.previewId}.`
      );
    }
  );

  server.registerTool(
    "revit.cancel_request",
    {
      title: "Cancel Revit Request",
      description: "Ask the Revit add-in to cancel queued or cancellable work when supported.",
      inputSchema: cancelSchema,
      outputSchema: toolOutputSchema,
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const request = makeRequest(context.sessionId, "cancel_request", "debug", args, 5000);
      const response = await context.bridge.cancel(request, { signal: extra.signal });
      return asToolResult(response, (result) => result.message);
    }
  );
}
