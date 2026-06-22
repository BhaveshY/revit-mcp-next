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

const queryFilterSchema = z.object({
  categories: z.array(z.string()).optional(),
  classes: z.array(z.string()).optional(),
  viewId: z.string().optional(),
  selectionOnly: z.boolean().optional(),
  levelIds: z.array(z.string()).optional(),
  worksetIds: z.array(z.string()).optional(),
  designOptionIds: z.array(z.string()).optional(),
  parameterEquals: z.record(z.union([z.string(), z.number(), z.boolean()])).optional(),
});

const querySchema = {
  filter: queryFilterSchema.describe("Revit-native filters to apply before projection."),
  fields: z.array(z.string()).optional().describe("Fields to return. Prefer explicit fields for token efficiency."),
  preset: z.enum(["idOnly", "summary", "schedule", "geometrySummary"]).optional(),
  limit: z.number().int().min(1).max(500).default(50),
  cursor: z.string().optional(),
  includeTotalCount: z.boolean().default(true),
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
        includeTotalCount: args.includeTotalCount ?? true,
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
}
