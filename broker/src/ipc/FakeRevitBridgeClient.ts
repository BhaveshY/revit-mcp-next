import { createHash } from "node:crypto";
import type {
  BridgeRequest,
  BridgeResponse,
  CancelRequest,
  CancelResult,
  ChangeApplyRequest,
  ChangeApplyResult,
  ChangeOperation,
  ChangePreviewResult,
  ChangeSetRequest,
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
      capabilities: [
        "revit.status",
        "revit.list_documents",
        "revit.get_levels",
        "revit.query",
        "revit.preview_change_set",
        "revit.apply_change_set",
        "revit.cancel_request",
      ],
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

  async previewChange(
    request: BridgeRequest<ChangeSetRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ChangePreviewResult>> {
    maybeAbort(options);
    const metadata = getChangeSetMetadata(request.payload);
    return ok(request, {
      previewId: `fake-preview-${request.payload.operations.length}`,
      documentFingerprint: metadata.documentFingerprint,
      changeSetHash: metadata.changeSetHash,
      baseGeneration: metadata.baseGeneration,
      expiresAt: metadata.expiresAt,
      transactionName: request.payload.transactionName,
      operationCount: request.payload.operations.length,
      ready: true,
      requiresConfirmation: true,
      riskLevel: request.payload.operations.some(isMediumRiskOperation) ? "medium" : "low",
      changes: request.payload.operations.map((operation, index) => ({
        operationIndex: index,
        operationId: operation.id,
        type: operation.type,
        status: "ready",
        target: getOperationTarget(operation),
        after: getOperationAfter(operation),
      })),
    });
  }

  async applyChange(
    request: BridgeRequest<ChangeApplyRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ChangeApplyResult>> {
    maybeAbort(options);
    const metadata = getChangeSetMetadata(request.payload);
    return ok(request, {
      previewId: request.payload.previewId,
      documentFingerprint: metadata.documentFingerprint,
      changeSetHash: metadata.changeSetHash,
      baseGeneration: metadata.baseGeneration,
      transactionName: request.payload.transactionName,
      applied: request.payload.confirm,
      changedCount: request.payload.operations.length,
      changes: request.payload.operations.map((operation, index) => ({
        operationIndex: index,
        operationId: operation.id,
        type: operation.type,
        status: "applied",
        target: getOperationTarget(operation),
        after: getOperationAfter(operation),
      })),
    });
  }

  async cancel(
    request: BridgeRequest<CancelRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CancelResult>> {
    maybeAbort(options);
    return ok(request, {
      cancelled: false,
      requestId: request.payload.requestId,
      message: "No queued fake request matched the cancellation request.",
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

function getChangeSetMetadata(payload: ChangeSetRequest): {
  documentFingerprint: string;
  changeSetHash: string;
  baseGeneration: number;
  expiresAt: string;
} {
  const documentFingerprint = payload.documentFingerprint ?? activeDocument.fingerprint;
  const baseGeneration = payload.baseGeneration ?? payload.expectedGeneration ?? activeDocument.generation;
  return {
    documentFingerprint,
    changeSetHash: payload.changeSetHash ?? hashChangeSet(payload, documentFingerprint, baseGeneration),
    baseGeneration,
    expiresAt: payload.expiresAt ?? "2099-01-01T00:00:00.000Z",
  };
}

function hashChangeSet(payload: ChangeSetRequest, documentFingerprint: string, baseGeneration: number): string {
  const canonicalPayload = {
    documentFingerprint,
    baseGeneration,
    transactionName: payload.transactionName,
    operations: payload.operations,
  };
  return `sha256:${createHash("sha256").update(JSON.stringify(canonicalPayload)).digest("hex")}`;
}

function getOperationTarget(operation: ChangeOperation): Record<string, unknown> {
  switch (operation.type) {
    case "set_parameter":
      return {
        elementId: operation.elementId,
        parameterName: operation.parameterName,
      };
    case "create_level":
      return {
        document: activeDocument.title,
      };
    case "create_wall":
      return {
        document: activeDocument.title,
        levelId: operation.levelId,
        wallTypeId: operation.wallTypeId,
      };
    case "move_element":
      return {
        elementId: operation.elementId,
      };
    default:
      return assertNever(operation);
  }
}

function getOperationAfter(operation: ChangeOperation): Record<string, unknown> {
  switch (operation.type) {
    case "set_parameter":
      return {
        value: operation.value,
      };
    case "create_level":
      return {
        name: operation.name,
        elevation: operation.elevation,
      };
    case "create_wall":
      return {
        start: operation.start,
        end: operation.end,
        wallTypeId: operation.wallTypeId,
        height: operation.height,
        structural: operation.structural,
        flip: operation.flip,
      };
    case "move_element":
      return {
        translation: operation.translation,
      };
    default:
      return assertNever(operation);
  }
}

function isMediumRiskOperation(operation: ChangeOperation): boolean {
  return operation.type === "create_level" || operation.type === "create_wall";
}

function assertNever(value: never): never {
  throw new Error(`Unsupported change operation: ${JSON.stringify(value)}`);
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
