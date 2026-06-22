import type {
  BridgeRequest,
  BridgeResponse,
  LevelSummary,
  QueryRequest,
  QueryResult,
  RevitDocumentSummary,
  RevitStatus,
} from "@revit-mcp-next/contracts";
import { PROTOCOL_VERSION } from "@revit-mcp-next/contracts";
import type { BridgeCallOptions, RevitBridgeClient } from "./RevitBridgeClient.js";

const activeDocument: RevitDocumentSummary = {
  documentId: "doc-1",
  title: "Sample.rvt",
  path: "C:\\Projects\\Sample.rvt",
  fingerprint: "sample-doc-fingerprint",
  isActive: true,
  isWorkshared: false,
  isModified: false,
  generation: 7,
  activeView: {
    id: "1024",
    name: "Level 1",
    type: "FloorPlan",
    isGraphical: true,
    scale: 100,
  },
};

const levels: LevelSummary[] = [
  {
    id: "311",
    uniqueId: "level-311",
    name: "Level 1",
    elevation: { value: 0, unit: "mm", system: "metric" },
    isBuildingStory: true,
  },
  {
    id: "312",
    uniqueId: "level-312",
    name: "Level 2",
    elevation: { value: 3500, unit: "mm", system: "metric" },
    isBuildingStory: true,
  },
];

export class FakeRevitBridgeClient implements RevitBridgeClient {
  async status(
    request: BridgeRequest<Record<string, never>>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitStatus>> {
    maybeAbort(options);
    return ok(request, {
      connected: true,
      brokerVersion: "test",
      addinVersion: "fake",
      protocolVersion: PROTOCOL_VERSION,
      revit: {
        version: "2024",
        build: "fake",
        processId: 1234,
      },
      activeDocument,
      selection: { count: 0 },
      capabilities: ["status", "list_documents", "get_levels", "query"],
      warnings: [],
    });
  }

  async listDocuments(
    request: BridgeRequest<Record<string, never>>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitDocumentSummary[]>> {
    maybeAbort(options);
    return ok(request, [activeDocument]);
  }

  async getLevels(
    request: BridgeRequest<{ documentFingerprint?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<LevelSummary[]>> {
    maybeAbort(options);
    return ok(request, levels);
  }

  async query(
    request: BridgeRequest<QueryRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<QueryResult>> {
    maybeAbort(options);
    const limit = Math.min(request.payload.limit ?? 50, 500);
    const items = [
      {
        id: "501",
        uniqueId: "wall-501",
        category: "OST_Walls",
        class: "Wall",
        name: "Basic Wall",
        typeId: "9001",
        levelId: "311",
      },
    ].slice(0, limit);

    return ok(request, {
      items,
      totalCount: 1,
      returnedCount: items.length,
      limit,
      truncated: false,
      fields: request.payload.fields ?? ["id", "category", "class", "name"],
      units: {},
      scope: request.payload.filter.viewId ? `view:${request.payload.filter.viewId}` : "document",
      source: "fake-bridge",
    });
  }

  dispose(): void {
    // No resources.
  }
}

function maybeAbort(options?: BridgeCallOptions): void {
  if (options?.signal?.aborted) {
    throw new Error("Bridge call aborted");
  }
}

function ok<T>(request: BridgeRequest, data: T): BridgeResponse<T> {
  return {
    ok: true,
    requestId: request.requestId,
    data,
    warnings: [],
    metrics: {
      elapsedMs: 1,
    },
    generation: 7,
  };
}
