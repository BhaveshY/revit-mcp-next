import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import type { BridgeResponse, ProtocolVersion } from "@revit-mcp-next/contracts";
import type { RevitBridgeClient } from "../ipc/RevitBridgeClient.js";
import { makeRequest } from "../ipc/RequestFactory.js";
import { applyDecodedPageCursor, decodePageCursor, encodePageCursorResponse } from "./pageCursor.js";
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

const queryFilterSchema = z
  .object({
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
  })
  .strict();
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

const parameterDescribePresetSchema = z
  .enum(["writableEdit", "namesOnly", "full"])
  .default("writableEdit")
  .describe(
    "writableEdit returns compact writable instance parameter metadata for edits; namesOnly returns names without values; full preserves legacy read-only/type/value detail."
  );

type ParameterDescribePreset = z.infer<typeof parameterDescribePresetSchema>;

const parameterDescribeSchema = {
  ...documentGuardSchema,
  filter: queryFilterSchema.describe("Revit-native filters for elements whose parameters should be described. Prefer explicit elementIds, selectionOnly, or tight category/class filters."),
  preset: parameterDescribePresetSchema,
  includeTypeParameters: z.boolean().optional().describe("Override the preset and include or omit type parameters."),
  includeReadOnly: z.boolean().optional().describe("Override the preset and include or omit read-only parameters."),
  includeValues: z.boolean().optional().describe("Override the preset and include or omit current parameter values."),
  nameContains: z.string().min(1).max(128).optional(),
  limit: z.number().int().min(1).max(100).optional(),
  cursor: z.string().optional(),
  parameterLimit: z.number().int().min(1).max(200).optional(),
  includeTotalCount: z.boolean().default(false),
};

function resolveParameterDescribeOptions(args: {
  preset?: ParameterDescribePreset;
  includeTypeParameters?: boolean;
  includeReadOnly?: boolean;
  includeValues?: boolean;
  limit?: number;
  parameterLimit?: number;
}): {
  preset: ParameterDescribePreset;
  includeTypeParameters: boolean;
  includeReadOnly: boolean;
  includeValues: boolean;
  limit: number;
  parameterLimit: number;
} {
  const preset = args.preset ?? "writableEdit";
  const defaults =
    preset === "full"
      ? { includeTypeParameters: true, includeReadOnly: true, includeValues: true, limit: 20, parameterLimit: 80 }
      : preset === "namesOnly"
        ? { includeTypeParameters: true, includeReadOnly: true, includeValues: false, limit: 10, parameterLimit: 120 }
        : { includeTypeParameters: false, includeReadOnly: false, includeValues: false, limit: 10, parameterLimit: 40 };

  return {
    preset,
    includeTypeParameters: args.includeTypeParameters ?? defaults.includeTypeParameters,
    includeReadOnly: args.includeReadOnly ?? defaults.includeReadOnly,
    includeValues: args.includeValues ?? defaults.includeValues,
    limit: args.limit ?? defaults.limit,
    parameterLimit: args.parameterLimit ?? defaults.parameterLimit,
  };
}

function preparePagedPayload<TPayload extends Record<string, unknown>>(
  context: CoreToolContext,
  operation: string,
  cursor: string | undefined,
  basePayload: TPayload
): { ok: true; payload: TPayload } | { ok: false; result: CallToolResult } {
  const decoded = decodePageCursor(
    cursor,
    { sessionId: context.sessionId, protocolVersion: context.protocolVersion },
    operation,
    basePayload
  );
  if (!decoded.ok) {
    return { ok: false, result: asToolResult(decoded.response, () => "") };
  }

  return { ok: true, payload: applyDecodedPageCursor(basePayload, decoded) };
}

function withOpaqueCursor<TData>(
  response: BridgeResponse<TData>,
  context: CoreToolContext,
  operation: string,
  basePayload: Record<string, unknown>
): BridgeResponse<TData> {
  return encodePageCursorResponse(response, { sessionId: context.sessionId, protocolVersion: context.protocolVersion }, operation, basePayload);
}

const currentViewSchema = {
  ...documentGuardSchema,
  includeCropBox: z.boolean().default(false).describe("Include active view crop box bounds when Revit exposes them."),
};

const viewFilterSchema = z
  .object({
    viewIds: z.array(boundedId).max(256).optional(),
    uniqueIds: z.array(boundedString).max(256).optional(),
    viewTypes: z.array(boundedString).max(32).optional().describe("Revit ViewType names such as FloorPlan, CeilingPlan, ThreeD, Section, or DraftingView."),
    nameContains: z.string().min(1).max(128).optional(),
    isTemplate: z.boolean().optional(),
    isGraphical: z.boolean().optional(),
    canBePrinted: z.boolean().optional(),
  })
  .strict();

const viewsSchema = {
  ...documentGuardSchema,
  filter: viewFilterSchema.optional(),
  fields: z.array(boundedString).max(32).optional(),
  preset: z.enum(["idOnly", "summary", "sheetPlacement"]).default("summary"),
  includeCropBox: z.boolean().default(false),
  limit: z.number().int().min(1).max(500).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(false),
};

const sheetFilterSchema = z
  .object({
    sheetIds: z.array(boundedId).max(256).optional(),
    uniqueIds: z.array(boundedString).max(256).optional(),
    numbers: z.array(z.string().min(1).max(64)).max(256).optional(),
    numberContains: z.string().min(1).max(64).optional(),
    nameContains: z.string().min(1).max(128).optional(),
    titleBlockIds: z.array(boundedId).max(256).optional(),
  })
  .strict();

const sheetsSchema = {
  ...documentGuardSchema,
  filter: sheetFilterSchema.optional(),
  fields: z.array(boundedString).max(32).optional(),
  preset: z.enum(["idOnly", "summary", "placement"]).default("summary"),
  includePlacedViews: z.boolean().default(false),
  limit: z.number().int().min(1).max(500).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(false),
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
  kind: z.enum(["elementTypes", "familySymbols", "titleBlocks", "viewFamilyTypes", "textNoteTypes", "dimensionTypes", "tagTypes"]),
  documentFingerprint: boundedString.optional().describe("Optional active document fingerprint from revit.status."),
  expectedGeneration: generationSchema.optional().describe("Expected active document generation from revit.status."),
  filter: catalogFilterSchema.optional().describe("Filters for compact Revit catalog discovery."),
  preset: z.enum(["idOnly", "compact", "typeChange", "placement", "sheet", "annotation"]).default("compact"),
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
const createSheetOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("create_sheet"),
    sheetNumber: z.string().min(1).max(64).describe("Unique Revit sheet number, such as A-101."),
    name: z.string().min(1).max(256).optional().describe("Optional sheet name/title."),
    titleBlockTypeId: boundedId.optional().describe("Optional title block type ID from revit.catalog kind=titleBlocks preset=sheet."),
  })
  .strict();
const placeViewOnSheetOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("place_view_on_sheet"),
    sheetId: boundedId.describe("Target ViewSheet element ID."),
    viewId: boundedId.describe("View ID to place. Use revit.get_views and avoid templates, schedules, legends, or views already placed."),
    center: changePoint2Schema.describe("Viewport center on the sheet in sheet coordinates with explicit units."),
  })
  .strict();
const createTextNoteOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("create_text_note"),
    viewId: boundedId.describe("Graphical target view ID. Use revit.get_views first."),
    text: z.string().min(1).max(2048).describe("Text note body."),
    position: changePoint3Schema.describe("Text note insertion point with explicit units."),
    textNoteTypeId: boundedId.optional().describe("Optional text note type ID from revit.catalog kind=textNoteTypes preset=annotation."),
    width: changeUnitValueSchema.optional().describe("Optional wrapping width. Omit for an unwrapped note."),
    rotation: changeAngleValueSchema.optional().describe("Optional rotation in the target view."),
  })
  .strict();
const tagRoomOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("tag_room"),
    roomId: boundedId.describe("Room element ID to tag. Use revit.get_rooms first."),
    viewId: boundedId.describe("Plan-like graphical view ID that can display the room. Use revit.get_views first."),
    location: changePoint2Schema.describe("Room tag head location in the target view's level plane."),
    tagTypeId: boundedId.optional().describe("Optional room tag type ID from revit.catalog kind=tagTypes preset=annotation."),
    hasLeader: z.boolean().optional().describe("Whether to create the room tag with a leader. Defaults to false."),
    orientation: z.enum(["Horizontal", "Vertical", "Model"]).optional().describe("Optional room tag orientation."),
  })
  .strict();
const tagElementOperationSchema = operationBaseSchema
  .extend({
    type: z.literal("tag_element"),
    elementId: boundedId.describe("Model element ID to tag."),
    viewId: boundedId.describe("Graphical target view ID that can display the element. Use revit.get_views and revit.query with viewId first."),
    tagTypeId: boundedId.describe("Tag FamilySymbol ID from revit.catalog kind=tagTypes preset=annotation."),
    position: changePoint3Schema.describe("Tag head position in the target view."),
    hasLeader: z.boolean().optional().describe("Whether to create the element tag with a leader. Defaults to false."),
    orientation: z.enum(["Horizontal", "Vertical", "AnyModelDirection"]).optional().describe("Optional independent tag orientation."),
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
    allowDependentDeletes: z.boolean().optional().describe("Must be true to delete dependent elements discovered by the preview probe unless expectedDeletedElementIds exactly matches the delete set."),
    expectedDeletedElementIds: z
      .array(boundedId)
      .max(256)
      .optional()
      .describe("Optional exact set of Revit element IDs expected to be deleted, including the target and any dependents."),
    expectedDeletedCount: z.number().int().min(1).max(256).optional().describe("Optional exact count guard for the delete set."),
    dependentDeleteLimit: z
      .number()
      .int()
      .min(1)
      .max(256)
      .optional()
      .describe("Maximum delete-set size the preview may approve without exact expectedDeletedElementIds. Defaults to a conservative add-in limit."),
  })
  .strict();
const changeOperationSchema = z.discriminatedUnion("type", [
  setParameterOperationSchema,
  createLevelOperationSchema,
  createWallOperationSchema,
  placeFamilyInstanceOperationSchema,
  createSheetOperationSchema,
  placeViewOnSheetOperationSchema,
  createTextNoteOperationSchema,
  tagRoomOperationSchema,
  tagElementOperationSchema,
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
  documentFingerprint: boundedString.describe("Active document fingerprint returned by preview_change_set."),
  baseGeneration: generationSchema.describe("Document generation captured by preview_change_set and echoed to apply."),
  changeSetHash: changeSetHashSchema.describe("Opaque hash for the exact previewed change set."),
  expiresAt: expiresAtSchema,
  previewId: boundedString.describe("The previewId returned by revit.preview_change_set for the exact same change set."),
  confirm: z.literal(true).describe("Must be true to apply a previewed change set."),
};

const cancelSchema = {
  requestId: boundedString.optional().describe("Optional bridge request ID to cancel when supported by the add-in."),
  reason: z.string().max(256).optional(),
};

const warningSchema = z
  .object({
    code: z.string(),
    message: z.string(),
    details: z.record(z.unknown()).optional(),
  })
  .strict();

const metricsSchema = z
  .object({
    elapsedMs: z.number(),
    collectorElapsedMs: z.number().optional(),
    cacheHit: z.boolean().optional(),
    returnedCount: z.number().optional(),
    totalCount: z.number().optional(),
  })
  .strict();

const bridgeErrorSchema = z
  .object({
    code: z.string(),
    message: z.string(),
    recoverable: z.boolean(),
    details: z.record(z.unknown()).optional(),
    suggestedNextAction: z.string().optional(),
  })
  .passthrough();

const errorDataSchema = z
  .object({
    error: bridgeErrorSchema,
  })
  .passthrough();

function toolOutputSchema(dataSchema: z.ZodTypeAny) {
  return z
    .object({
      data: z.union([dataSchema, errorDataSchema]),
      warnings: z.array(warningSchema),
      metrics: metricsSchema,
      generation: z.number().optional(),
    })
    .strict();
}

const jsonValueSchema: z.ZodTypeAny = z.lazy(() =>
  z.union([z.string(), z.number(), z.boolean(), z.null(), z.array(jsonValueSchema), z.record(jsonValueSchema)])
);

const unitValueSchema = z
  .object({
    value: z.number(),
    unit: z.string(),
    system: z.string(),
  })
  .passthrough();

const point3Schema = z
  .object({
    x: unitValueSchema,
    y: unitValueSchema,
    z: unitValueSchema,
  })
  .passthrough();

const documentReferenceSchema = z
  .object({
    fingerprint: z.string(),
    title: z.string(),
    path: z.string().optional(),
    generation: z.number(),
  })
  .passthrough();

const viewSummarySchema = z
  .object({
    id: z.string(),
    uniqueId: z.string().nullable().optional(),
    name: z.string(),
    type: z.string(),
    isGraphical: z.boolean().optional(),
    isTemplate: z.boolean().optional(),
    canBePrinted: z.boolean().optional(),
    scale: z.number().optional(),
    detailLevel: z.string().optional(),
    discipline: z.string().optional(),
  })
  .passthrough();

const documentSummarySchema = documentReferenceSchema
  .extend({
    documentId: z.string().optional(),
    isActive: z.boolean().optional(),
    isWorkshared: z.boolean().optional(),
    isModified: z.boolean().optional(),
    activeView: viewSummarySchema.nullable().optional(),
  })
  .passthrough();

const pageBaseSchema = z
  .object({
    returnedCount: z.number(),
    totalCount: z.number().optional(),
    limit: z.number(),
    cursor: z.string().optional(),
    truncated: z.boolean(),
    scope: z.string(),
    source: z.string(),
  })
  .passthrough();

const queryItemSchema = z
  .object({
    id: z.string(),
    uniqueId: z.string().nullable().optional(),
    category: z.string().nullable().optional(),
    class: z.string().nullable().optional(),
    name: z.string().nullable().optional(),
    typeId: z.string().nullable().optional(),
    levelId: z.string().nullable().optional(),
    fields: z.record(z.unknown()).optional(),
  })
  .passthrough();

const queryResultSchema = pageBaseSchema
  .extend({
    items: z.array(queryItemSchema),
    fields: z.array(z.string()),
    units: z.record(z.string()),
  })
  .passthrough();

const statusDataSchema = z
  .object({
    connected: z.boolean(),
    brokerVersion: z.string().optional(),
    addinVersion: z.string().optional(),
    protocolVersion: z.string().optional(),
    activeDocument: documentSummarySchema.optional(),
    selection: z.object({ count: z.number() }).passthrough().optional(),
    capabilities: z.array(z.string()).optional(),
    warnings: z.array(warningSchema).optional(),
  })
  .passthrough();

const levelSummarySchema = z
  .object({
    id: z.string(),
    uniqueId: z.string().nullable().optional(),
    name: z.string(),
    elevation: unitValueSchema,
    isBuildingStory: z.boolean().optional(),
  })
  .passthrough();

const currentViewDataSchema = z
  .object({
    document: documentReferenceSchema,
    view: viewSummarySchema,
    source: z.string(),
  })
  .passthrough();

const viewsResultSchema = pageBaseSchema
  .extend({
    document: documentReferenceSchema,
    items: z.array(viewSummarySchema),
    fields: z.array(z.string()),
  })
  .passthrough();

const sheetSummarySchema = z
  .object({
    id: z.string(),
    uniqueId: z.string().nullable().optional(),
    sheetNumber: z.string().nullable().optional(),
    name: z.string().nullable().optional(),
    titleBlockIds: z.array(z.string()).optional(),
    placedViews: z.array(z.object({ viewId: z.string() }).passthrough()).optional(),
  })
  .passthrough();

const sheetsResultSchema = pageBaseSchema
  .extend({
    document: documentReferenceSchema,
    items: z.array(sheetSummarySchema),
    fields: z.array(z.string()),
  })
  .passthrough();

const scopedElementListResultSchema = queryResultSchema
  .extend({
    document: documentReferenceSchema,
    view: viewSummarySchema.nullable().optional(),
    selection: z.object({ count: z.number(), available: z.boolean() }).passthrough().optional(),
  })
  .passthrough();

const modelStatisticsResultSchema = z
  .object({
    document: documentReferenceSchema,
    totals: z.object({ elements: z.number() }).passthrough(),
    scannedElements: z.number(),
    bucketLimit: z.number(),
    truncated: z.boolean(),
    byCategory: z.array(z.object({ key: z.string(), count: z.number() }).passthrough()).optional(),
    byClass: z.array(z.object({ key: z.string(), count: z.number() }).passthrough()).optional(),
    byLevel: z.array(z.object({ key: z.string(), count: z.number() }).passthrough()).optional(),
    source: z.string(),
  })
  .passthrough();

const modelReadinessResultSchema = z
  .object({
    document: documentReferenceSchema,
    activeView: viewSummarySchema.nullable().optional(),
    scenarios: z.array(z.object({ name: z.string(), ready: z.boolean(), missing: z.array(z.string()) }).passthrough()),
    readyCount: z.number(),
    totalCount: z.number(),
    source: z.string(),
  })
  .passthrough();

const materialQuantityItemSchema = z
  .object({
    materialId: z.string(),
    materialName: z.string(),
    elementCount: z.number(),
    area: unitValueSchema,
    volume: unitValueSchema,
    source: z.string(),
  })
  .passthrough();

const materialQuantitiesResultSchema = pageBaseSchema
  .extend({
    document: documentReferenceSchema,
    items: z.array(materialQuantityItemSchema),
    elementsScanned: z.number(),
    elementsWithMaterials: z.number(),
    units: z.object({ area: z.string(), volume: z.string() }).passthrough(),
  })
  .passthrough();

const roomSummarySchema = z
  .object({
    id: z.string(),
    uniqueId: z.string().nullable().optional(),
    number: z.string().nullable().optional(),
    name: z.string().nullable().optional(),
    levelId: z.string().nullable().optional(),
    levelName: z.string().nullable().optional(),
    phaseId: z.string().nullable().optional(),
    phaseName: z.string().nullable().optional(),
    area: unitValueSchema.optional(),
    volume: unitValueSchema.optional(),
    location: point3Schema.optional(),
    fields: z.record(z.unknown()).optional(),
  })
  .passthrough();

const roomsResultSchema = pageBaseSchema
  .extend({
    document: documentReferenceSchema,
    items: z.array(roomSummarySchema),
    fields: z.array(z.string()),
    units: z.object({ area: z.string(), volume: z.string(), location: z.string() }).passthrough(),
  })
  .passthrough();

const catalogItemSchema = z
  .object({
    id: z.string(),
    uniqueId: z.string().nullable().optional(),
    class: z.string(),
    category: z.string().nullable().optional(),
    builtInCategory: z.string().nullable().optional(),
    name: z.string(),
    familyName: z.string().nullable().optional(),
    fields: z.record(z.unknown()).optional(),
  })
  .passthrough();

const catalogTargetSchema = z
  .object({
    elementId: z.string(),
    class: z.string(),
    canChangeType: z.boolean(),
    currentTypeId: z.string().nullable().optional(),
    currentTypeName: z.string().nullable().optional(),
  })
  .passthrough();

const catalogResultSchema = pageBaseSchema
  .extend({
    kind: z.string(),
    target: catalogTargetSchema.optional(),
    items: z.array(catalogItemSchema),
    fields: z.array(z.string()),
    units: z.record(z.string()),
  })
  .passthrough();

const parameterSummarySchema = z
  .object({
    name: z.string(),
    storageType: z.string(),
    source: z.string(),
    isReadOnly: z.boolean(),
  })
  .passthrough();

const parameterTargetSchema = queryItemSchema
  .extend({
    typeName: z.string().nullable().optional(),
    parameters: z.array(parameterSummarySchema),
    parameterCount: z.number(),
    truncated: z.boolean(),
  })
  .passthrough();

const parameterDescribeResultSchema = pageBaseSchema
  .extend({
    document: documentReferenceSchema,
    items: z.array(parameterTargetSchema),
    parameterLimit: z.number(),
    preset: z.string().optional(),
  })
  .passthrough();

const outputSchemas = {
  unknown: z
    .object({
      data: jsonValueSchema,
      warnings: z.array(warningSchema),
      metrics: metricsSchema,
      generation: z.number().optional(),
    })
    .strict(),
  status: toolOutputSchema(statusDataSchema),
  documents: toolOutputSchema(z.array(documentSummarySchema)),
  levels: toolOutputSchema(z.array(levelSummarySchema)),
  currentView: toolOutputSchema(currentViewDataSchema),
  views: toolOutputSchema(viewsResultSchema),
  sheets: toolOutputSchema(sheetsResultSchema),
  scopedElements: toolOutputSchema(scopedElementListResultSchema),
  modelStatistics: toolOutputSchema(modelStatisticsResultSchema),
  modelReadiness: toolOutputSchema(modelReadinessResultSchema),
  materialQuantities: toolOutputSchema(materialQuantitiesResultSchema),
  rooms: toolOutputSchema(roomsResultSchema),
  catalog: toolOutputSchema(catalogResultSchema),
  query: toolOutputSchema(queryResultSchema),
  parameters: toolOutputSchema(parameterDescribeResultSchema),
};

export function registerCoreTools(server: McpServer, context: CoreToolContext): void {
  server.registerTool(
    "revit.status",
    {
      title: "Revit Status",
      description:
        "Check Revit bridge health, active document/view, versions, capabilities, and selection count. Start every Revit workflow here.",
      inputSchema: {},
      outputSchema: outputSchemas.status,
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
      outputSchema: outputSchemas.documents,
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
      outputSchema: outputSchemas.levels,
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
    "revit.get_views",
    {
      title: "Get Revit Views",
      description:
        "Return compact, paginated Revit view inventory for view/sheet planning. Filter by view type, name, template state, graphical state, or exact IDs.",
      inputSchema: viewsSchema,
      outputSchema: outputSchemas.views,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const basePayload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        fields: args.fields,
        preset: args.preset ?? "summary",
        includeCropBox: args.includeCropBox ?? false,
        limit: args.limit ?? 50,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const page = preparePagedPayload(context, "get_views", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "get_views", "read", payload, 30000);
      const response = await context.bridge.getViews(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "get_views", basePayload),
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} Revit view(s) returned.`
      );
    }
  );

  server.registerTool(
    "revit.get_sheets",
    {
      title: "Get Revit Sheets",
      description:
        "Return compact, paginated Revit sheet inventory with sheet numbers, names, title block IDs, and optional placed views.",
      inputSchema: sheetsSchema,
      outputSchema: outputSchemas.sheets,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const basePayload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        fields: args.fields,
        preset: args.preset ?? "summary",
        includePlacedViews: args.includePlacedViews ?? false,
        limit: args.limit ?? 50,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const page = preparePagedPayload(context, "get_sheets", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "get_sheets", "read", payload, 30000);
      const response = await context.bridge.getSheets(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "get_sheets", basePayload),
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} Revit sheet(s) returned.`
      );
    }
  );

  server.registerTool(
    "revit.get_current_view",
    {
      title: "Get Current Revit View",
      description:
        "Return the active Revit view with stable IDs, view type, scale, detail metadata, generation, and optional crop box.",
      inputSchema: currentViewSchema,
      outputSchema: outputSchemas.currentView,
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
      outputSchema: outputSchemas.scopedElements,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const basePayload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        fields: args.fields,
        preset: args.preset,
        includeHidden: args.includeHidden ?? false,
        limit: args.limit ?? 50,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const page = preparePagedPayload(context, "get_current_view_elements", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "get_current_view_elements", "read", payload, 30000);
      const response = await context.bridge.getCurrentViewElements(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "get_current_view_elements", basePayload),
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
      outputSchema: outputSchemas.scopedElements,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const basePayload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: { ...(args.filter ?? {}), selectionOnly: true },
        fields: args.fields,
        preset: args.preset,
        limit: args.limit ?? 50,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const page = preparePagedPayload(context, "get_selection", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "get_selection", "read", payload, 30000);
      const response = await context.bridge.getSelection(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "get_selection", basePayload),
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
      outputSchema: outputSchemas.modelStatistics,
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
      outputSchema: outputSchemas.modelReadiness,
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
      outputSchema: outputSchemas.materialQuantities,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const basePayload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        materialNameContains: args.materialNameContains,
        includePaint: args.includePaint ?? false,
        maxElementsScanned: args.maxElementsScanned ?? 20000,
        limit: args.limit ?? 50,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const page = preparePagedPayload(context, "get_material_quantities", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "get_material_quantities", "read", payload, 60000);
      const response = await context.bridge.getMaterialQuantities(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "get_material_quantities", basePayload),
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
      outputSchema: outputSchemas.rooms,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const basePayload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        fields: args.fields,
        preset: args.preset ?? "summary",
        limit: args.limit ?? 50,
        includeTotalCount: args.includeTotalCount ?? false,
        includeUnplaced: args.includeUnplaced ?? false,
      };
      const page = preparePagedPayload(context, "get_rooms", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "get_rooms", "read", payload, 30000);
      const response = await context.bridge.getRooms(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "get_rooms", basePayload),
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
        "Return compact Revit catalog IDs for safe writes and discovery: element types, family symbols, title blocks, view family types, and annotation type catalogs. Use kind=elementTypes with filter.forElementId before change_element_type.",
      inputSchema: catalogSchema,
      outputSchema: outputSchemas.catalog,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const basePayload = {
        kind: args.kind,
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter ?? {},
        preset: args.preset ?? "compact",
        fields: args.fields,
        limit: args.limit ?? 50,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const page = preparePagedPayload(context, "catalog", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "catalog", "read", payload, 30000);
      const response = await context.bridge.catalog(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "catalog", basePayload),
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
      outputSchema: outputSchemas.query,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const basePayload = {
        filter: args.filter,
        fields: args.fields,
        preset: args.preset,
        limit: args.limit ?? 50,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const page = preparePagedPayload(context, "query", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "query", "read", payload, 30000);
      const response = await context.bridge.query(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "query", basePayload),
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} item(s) returned from ${result.scope}.`
      );
    }
  );

  server.registerTool(
    "revit.describe_parameters",
    {
      title: "Describe Revit Parameters",
      description:
        "Return bounded parameter metadata for targeted elements, including writable/read-only state, storage type, values, and optional type parameters. Use before set_parameter.",
      inputSchema: parameterDescribeSchema,
      outputSchema: outputSchemas.parameters,
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => {
      const options = resolveParameterDescribeOptions(args);
      const basePayload = {
        documentFingerprint: args.documentFingerprint,
        expectedGeneration: args.expectedGeneration,
        filter: args.filter,
        preset: options.preset,
        includeTypeParameters: options.includeTypeParameters,
        includeReadOnly: options.includeReadOnly,
        includeValues: options.includeValues,
        nameContains: args.nameContains,
        limit: options.limit,
        parameterLimit: options.parameterLimit,
        includeTotalCount: args.includeTotalCount ?? false,
      };
      const page = preparePagedPayload(context, "describe_parameters", args.cursor, basePayload);
      if (!page.ok) return page.result;
      const payload = page.payload;
      const request = makeRequest(context.sessionId, "describe_parameters", "read", payload, 30000);
      const response = await context.bridge.describeParameters(request, { signal: extra.signal });
      return asToolResult(
        withOpaqueCursor(response, context, "describe_parameters", basePayload),
        (result) =>
          `${result.returnedCount}${result.totalCount === undefined ? "" : ` of ${result.totalCount}`} element parameter set(s) returned from ${result.scope}.`
      );
    }
  );

  server.registerTool(
    "revit.preview_change_set",
    {
      title: "Preview Revit Change",
      description:
        "Validate a bounded change set without mutating the model. Use this before revit.apply_change_set. Supported operations: set_parameter, create_level, create_wall, place_family_instance, create_sheet, place_view_on_sheet, create_text_note, tag_room, tag_element, move_element, rotate_element, copy_element, change_element_type, set_element_pinned, create_grid, create_floor, create_room, and delete_element.",
      inputSchema: changeSetSchema,
      outputSchema: outputSchemas.unknown,
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
      outputSchema: outputSchemas.unknown,
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
      outputSchema: outputSchemas.unknown,
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
