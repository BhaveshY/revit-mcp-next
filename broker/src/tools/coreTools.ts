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
const parameterScalar = z.union([z.string().max(256), z.number(), z.boolean()]);
const parameterEqualsSchema = z
  .record(boundedString, parameterScalar)
  .refine((value) => Object.keys(value).length <= 16, "At most 16 parameter equality filters are allowed.");

const queryFilterSchema = z.object({
  categories: z.array(boundedString).max(16).optional(),
  classes: z.array(boundedString).max(16).optional(),
  viewId: boundedId.optional(),
  selectionOnly: z.boolean().optional(),
  levelIds: z.array(boundedId).max(64).optional(),
  worksetIds: z.array(boundedId).max(64).optional(),
  designOptionIds: z.array(boundedId).max(64).optional(),
  parameterEquals: parameterEqualsSchema.optional(),
});

const querySchema = {
  filter: queryFilterSchema.describe("Revit-native filters to apply before projection."),
  fields: z.array(boundedString).max(32).optional().describe("Fields to return. Prefer explicit fields for token efficiency."),
  preset: z.enum(["idOnly", "summary", "schedule", "geometrySummary"]).optional(),
  limit: z.number().int().min(1).max(500).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(false),
};

const changeScalarSchema = z.union([z.string().max(512), z.number(), z.boolean()]);
const changeUnitValueSchema = z.object({
  value: z.number(),
  unit: z.enum(["mm", "millimeters", "m", "meters", "ft", "feet", "revit-internal"]),
  system: z.enum(["metric", "imperial", "revit-internal"]).default("metric"),
});
const changeSetHashSchema = z.string().min(1).max(128);
const generationSchema = z.number().int().min(0);
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
const changeOperationSchema = z.discriminatedUnion("type", [setParameterOperationSchema, createLevelOperationSchema]);

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
        "Validate a bounded change set without mutating the model. Use this before revit.apply_change_set. Supported operations: set_parameter and create_level.",
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
        destructiveHint: false,
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
