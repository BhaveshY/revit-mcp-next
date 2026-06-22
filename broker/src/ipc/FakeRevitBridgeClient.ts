import { createHash } from "node:crypto";
import type {
  BridgeRequest,
  BridgeResponse,
  CancelRequest,
  CancelResult,
  CatalogItem,
  CatalogKind,
  CatalogRequest,
  CatalogResult,
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

type FakeCatalogItem = CatalogItem & { kind: CatalogKind };

const catalogItems: FakeCatalogItem[] = [
  {
    kind: "elementTypes",
    id: "9001",
    uniqueId: "wall-type-9001",
    category: "OST_Walls",
    builtInCategory: "OST_Walls",
    class: "WallType",
    name: "Generic - 200mm",
    familyName: "Basic Wall",
    isCurrentType: true,
    validForTarget: true,
  },
  {
    kind: "elementTypes",
    id: "9002",
    uniqueId: "wall-type-9002",
    category: "OST_Walls",
    builtInCategory: "OST_Walls",
    class: "WallType",
    name: "Generic - 300mm",
    familyName: "Basic Wall",
    validForTarget: true,
  },
  {
    kind: "elementTypes",
    id: "9100",
    uniqueId: "floor-type-9100",
    category: "OST_Floors",
    builtInCategory: "OST_Floors",
    class: "FloorType",
    name: "Generic 150mm",
    familyName: "Floor",
  },
  {
    kind: "familySymbols",
    id: "9200",
    uniqueId: "family-symbol-9200",
    category: "OST_Doors",
    builtInCategory: "OST_Doors",
    class: "FamilySymbol",
    name: "0915 x 2134mm",
    familyName: "Single-Flush",
    familyId: "9199",
    isActive: true,
    placementType: "OneLevelBasedHosted",
  },
  {
    kind: "titleBlocks",
    id: "9300",
    uniqueId: "titleblock-9300",
    category: "OST_TitleBlocks",
    builtInCategory: "OST_TitleBlocks",
    class: "FamilySymbol",
    name: "A1 metric",
    familyName: "Titleblock",
    familyId: "9299",
    isActive: true,
    placementType: "ViewBased",
  },
  {
    kind: "viewFamilyTypes",
    id: "9400",
    uniqueId: "view-family-type-9400",
    class: "ViewFamilyType",
    name: "Floor Plan",
    familyName: "FloorPlan",
    viewFamily: "FloorPlan",
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
        "revit.catalog",
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

  async catalog(
    request: BridgeRequest<CatalogRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CatalogResult>> {
    maybeAbort(options);
    const limit = Math.min(request.payload.limit ?? 50, 200);
    const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
    const filter = request.payload.filter ?? {};
    let items = catalogItems.filter((item) => item.kind === request.payload.kind && matchesCatalogFilter(item, filter));
    const target = buildCatalogTarget(filter.forElementId);
    if (filter.forElementId === "501") {
      items = items.filter((item) => item.id === "9001" || item.id === "9002");
    } else if (filter.forElementId) {
      items = [];
    }

    const page = items.slice(offset, offset + limit);
    const truncated = offset + page.length < items.length;

    return ok(request, {
      kind: request.payload.kind,
      target,
      items: page,
      totalCount: request.payload.includeTotalCount ? items.length : undefined,
      returnedCount: page.length,
      limit,
      cursor: truncated ? String(offset + page.length) : undefined,
      truncated,
      fields: request.payload.fields ?? ["id", "class", "category", "name", "familyName"],
      scope: filter.forElementId ? `typeChange:${filter.forElementId}` : "activeDocument",
      source: "fake-bridge",
      units: {},
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

function buildCatalogTarget(elementId?: string) {
  if (!elementId) return undefined;
  if (elementId !== "501") {
    return {
      elementId,
      class: "Unknown",
      canChangeType: false,
      validTypeCount: 0,
    };
  }

  return {
    elementId: "501",
    uniqueId: "wall-501",
    category: "OST_Walls",
    class: "Wall",
    name: "Basic Wall",
    currentTypeId: "9001",
    currentTypeName: "Generic - 200mm",
    pinned: false,
    canChangeType: true,
    validTypeCount: 2,
  };
}

function matchesCatalogFilter(item: CatalogItem, filter: CatalogRequest["filter"]): boolean {
  if (!filter) return true;
  if (filter.categories?.length && !filter.categories.some((category) => equalsCatalogToken(category, item.category))) {
    return false;
  }
  if (filter.classes?.length && !filter.classes.some((className) => equalsCatalogToken(className, item.class))) {
    return false;
  }
  if (filter.familyName && !equalsCatalogToken(filter.familyName, item.familyName)) {
    return false;
  }
  if (
    filter.familyNameContains &&
    !containsIgnoreCase(item.familyName ?? "", filter.familyNameContains)
  ) {
    return false;
  }
  if (filter.nameContains && !containsIgnoreCase(item.name, filter.nameContains)) {
    return false;
  }
  if (filter.viewFamily?.length && !filter.viewFamily.some((viewFamily) => equalsCatalogToken(viewFamily, item.viewFamily))) {
    return false;
  }
  return true;
}

function equalsCatalogToken(expected: string, actual?: string): boolean {
  if (!actual) return false;
  return expected.localeCompare(actual, undefined, { sensitivity: "accent" }) === 0;
}

function containsIgnoreCase(value: string, needle: string): boolean {
  return value.toLocaleLowerCase().includes(needle.toLocaleLowerCase());
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
    case "rotate_element":
      return {
        elementId: operation.elementId,
      };
    case "copy_element":
      return {
        sourceElementId: operation.elementId,
      };
    case "change_element_type":
      return {
        elementId: operation.elementId,
        typeId: operation.typeId,
      };
    case "set_element_pinned":
      return {
        elementId: operation.elementId,
      };
    case "create_grid":
      return {
        document: activeDocument.title,
        name: operation.name,
      };
    case "create_floor":
      return {
        document: activeDocument.title,
        levelId: operation.levelId,
        floorTypeId: operation.floorTypeId,
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
    case "rotate_element":
      return {
        axisStart: operation.axisStart,
        axisEnd: operation.axisEnd,
        angle: operation.angle,
      };
    case "copy_element":
      return {
        translation: operation.translation,
      };
    case "change_element_type":
      return {
        typeId: operation.typeId,
      };
    case "set_element_pinned":
      return {
        pinned: operation.pinned,
        expectedPinned: operation.expectedPinned,
      };
    case "create_grid":
      return {
        name: operation.name,
        start: operation.start,
        end: operation.end,
      };
    case "create_floor":
      return {
        levelId: operation.levelId,
        outline: operation.outline,
        floorTypeId: operation.floorTypeId,
        structural: operation.structural,
      };
    default:
      return assertNever(operation);
  }
}

function isMediumRiskOperation(operation: ChangeOperation): boolean {
  return (
    operation.type === "create_level" ||
    operation.type === "create_wall" ||
    operation.type === "create_grid" ||
    operation.type === "create_floor" ||
    operation.type === "copy_element" ||
    operation.type === "change_element_type"
  );
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
