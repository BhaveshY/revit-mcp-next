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
  CurrentViewRequest,
  CurrentViewResult,
  LevelSummary,
  MaterialQuantitiesRequest,
  MaterialQuantitiesResult,
  ModelStatisticsRequest,
  ModelStatisticsResult,
  QueryRequest,
  QueryResult,
  RevitDocumentSummary,
  RevitStatus,
  RoomSummary,
  RoomsRequest,
  RoomsResult,
  ScopedElementListRequest,
  ScopedElementListResult,
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

const capabilities = [
  "revit.status",
  "revit.list_documents",
  "revit.get_levels",
  "revit.get_current_view",
  "revit.get_current_view_elements",
  "revit.get_selection",
  "revit.analyze_model",
  "revit.get_material_quantities",
  "revit.get_rooms",
  "revit.catalog",
  "revit.query",
  "revit.preview_change_set",
  "revit.apply_change_set",
  "revit.cancel_request",
];

const fakeQueryItems = [
  {
    id: "501",
    uniqueId: "wall-501",
    category: "OST_Walls",
    class: "Wall",
    name: "Basic Wall",
    typeId: "9001",
    levelId: "311",
  },
];

const fakeRooms: RoomSummary[] = [
  {
    id: "601",
    uniqueId: "room-601",
    number: "101",
    name: "Conference",
    levelId: "311",
    levelName: "Level 1",
    phaseId: "801",
    phaseName: "New Construction",
    area: { value: 24.5, unit: "m2", system: "metric" },
    volume: { value: 73.5, unit: "m3", system: "metric" },
    perimeter: { value: 19800, unit: "mm", system: "metric" },
    location: {
      x: { value: 2000, unit: "mm", system: "metric" },
      y: { value: 1500, unit: "mm", system: "metric" },
      z: { value: 0, unit: "mm", system: "metric" },
    },
    isPlaced: true,
    isEnclosed: true,
    department: "Operations",
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
      selection: { count: 1 },
      capabilities,
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

  async getCurrentView(
    request: BridgeRequest<CurrentViewRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CurrentViewResult>> {
    maybeAbort(options);
    return ok(request, {
      document: documentReference(),
      view: {
        ...activeDocument.activeView!,
        uniqueId: "view-1024",
        isTemplate: false,
        canBePrinted: true,
        detailLevel: "Medium",
        discipline: "Coordination",
      },
      source: "fake-bridge",
    });
  }

  async getCurrentViewElements(
    request: BridgeRequest<ScopedElementListRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ScopedElementListResult>> {
    maybeAbort(options);
    const result = buildScopedElementList(request, "activeView");
    result.view = activeDocument.activeView;
    return ok(request, result);
  }

  async getSelection(
    request: BridgeRequest<ScopedElementListRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ScopedElementListResult>> {
    maybeAbort(options);
    const result = buildScopedElementList(request, "selection");
    result.selection = { count: 1, available: true };
    return ok(request, result);
  }

  async analyzeModel(
    request: BridgeRequest<ModelStatisticsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelStatisticsResult>> {
    maybeAbort(options);
    const bucketLimit = Math.min(request.payload.bucketLimit ?? 50, 200);
    return ok(request, {
      document: documentReference(),
      totals: {
        elements: 42,
        modelElements: 18,
        elementTypes: catalogItems.filter((item) => item.kind === "elementTypes").length,
        families: 2,
        views: 3,
        sheets: 1,
        levels: levels.length,
        materials: 2,
      },
      scannedElements: 42,
      bucketLimit,
      truncated: false,
      byCategory: request.payload.includeCategoryBreakdown === false ? undefined : [
        { key: "OST_Walls", name: "Walls", builtInCategory: "OST_Walls", count: 1 },
      ],
      byClass: request.payload.includeClassBreakdown === false ? undefined : [
        { key: "Wall", count: 1 },
      ],
      byLevel: request.payload.includeLevelBreakdown === false ? undefined : [
        { key: "311", name: "Level 1", count: 1 },
      ],
      source: "fake-bridge",
    });
  }

  async getMaterialQuantities(
    request: BridgeRequest<MaterialQuantitiesRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<MaterialQuantitiesResult>> {
    maybeAbort(options);
    const limit = Math.min(request.payload.limit ?? 50, 200);
    const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
    const items = [
      {
        materialId: "7001",
        materialName: "Concrete - Cast-in-Place",
        materialClass: "Concrete",
        elementCount: 2,
        area: { value: 24.5, unit: "m2", system: "metric" as const },
        volume: { value: 3.2, unit: "m3", system: "metric" as const },
        source: "regular" as const,
        categories: [{ name: "OST_Walls", count: 1 }, { name: "OST_Floors", count: 1 }],
      },
    ].filter((item) => !request.payload.materialNameContains || containsIgnoreCase(item.materialName, request.payload.materialNameContains));
    const page = items.slice(offset, offset + limit);
    const truncated = offset + page.length < items.length;
    return ok(request, {
      document: documentReference(),
      scope: request.payload.filter?.selectionOnly ? "selection" : request.payload.filter?.viewId ? `view:${request.payload.filter.viewId}` : "activeDocument",
      items: page,
      elementsScanned: 3,
      elementsWithMaterials: 2,
      returnedCount: page.length,
      totalCount: request.payload.includeTotalCount ? items.length : undefined,
      limit,
      cursor: truncated ? String(offset + page.length) : undefined,
      truncated,
      units: { area: "m2", volume: "m3" },
      source: "fake-bridge",
    });
  }

  async getRooms(
    request: BridgeRequest<RoomsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RoomsResult>> {
    maybeAbort(options);
    const limit = Math.min(request.payload.limit ?? 50, 500);
    const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
    const filter = request.payload.filter ?? {};
    const fields = request.payload.fields ?? roomFieldsForPreset(request.payload.preset);
    let items = fakeRooms.filter((room) => matchesRoomFilter(room, filter, request.payload.includeUnplaced ?? false));
    const page = items.slice(offset, offset + limit).map((room) => projectRoom(room, fields));
    const truncated = offset + page.length < items.length;
    return ok(request, {
      document: documentReference(),
      items: page,
      returnedCount: page.length,
      totalCount: request.payload.includeTotalCount ? items.length : undefined,
      limit,
      cursor: truncated ? String(offset + page.length) : undefined,
      truncated,
      fields,
      units: {
        area: "m2",
        volume: "m3",
        location: "mm",
      },
      scope: "rooms",
      source: "fake-bridge",
    });
  }

  async query(
    request: BridgeRequest<QueryRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<QueryResult>> {
    maybeAbort(options);
    const limit = Math.min(request.payload.limit ?? 50, 500);
    let filteredItems = fakeQueryItems;
    const elementIds = request.payload.filter.elementIds ?? [];
    const uniqueIds = request.payload.filter.uniqueIds ?? [];
    if (elementIds.length > 0) {
      filteredItems = filteredItems.filter((item) => elementIds.includes(item.id));
    }
    if (uniqueIds.length > 0) {
      filteredItems = filteredItems.filter((item) => item.uniqueId && uniqueIds.includes(item.uniqueId));
    }
    const items = filteredItems.slice(0, limit);

    return ok(request, {
      items,
      totalCount: request.payload.includeTotalCount ? filteredItems.length : undefined,
      returnedCount: items.length,
      limit,
      truncated: false,
      fields: request.payload.fields ?? ["id", "category", "class", "name"],
      units: {},
      scope:
        elementIds.length > 0 || uniqueIds.length > 0
          ? "elements"
          : request.payload.filter.viewId
            ? `view:${request.payload.filter.viewId}`
            : "document",
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
      riskLevel: request.payload.operations.some(isHighRiskOperation)
        ? "high"
        : request.payload.operations.some(isMediumRiskOperation)
          ? "medium"
          : "low",
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

function documentReference() {
  return {
    fingerprint: activeDocument.fingerprint,
    title: activeDocument.title,
    path: activeDocument.path,
    generation: activeDocument.generation,
  };
}

function buildScopedElementList(
  request: BridgeRequest<ScopedElementListRequest>,
  scope: string
): ScopedElementListResult {
  const limit = Math.min(request.payload.limit ?? 50, 500);
  const items = fakeQueryItems.slice(0, limit);
  return {
    document: documentReference(),
    items,
    totalCount: request.payload.includeTotalCount ? fakeQueryItems.length : undefined,
    returnedCount: items.length,
    limit,
    truncated: false,
    fields: request.payload.fields ?? ["id", "category", "class", "name"],
    units: {},
    scope,
    source: "fake-bridge",
  };
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

function roomFieldsForPreset(preset?: string): string[] {
  switch (preset) {
    case "idOnly":
      return ["id"];
    case "schedule":
      return ["id", "number", "name", "levelId", "levelName", "area", "volume", "department"];
    default:
      return ["id", "uniqueId", "number", "name", "levelId", "area"];
  }
}

function matchesRoomFilter(room: RoomSummary, filter: RoomsRequest["filter"], includeUnplaced: boolean): boolean {
  if (!includeUnplaced && room.isPlaced === false) return false;
  if (filter?.elementIds?.length && !filter.elementIds.includes(room.id)) return false;
  if (filter?.uniqueIds?.length && (!room.uniqueId || !filter.uniqueIds.includes(room.uniqueId))) return false;
  if (filter?.levelIds?.length && (!room.levelId || !filter.levelIds.includes(room.levelId))) return false;
  if (filter?.phaseIds?.length && (!room.phaseId || !filter.phaseIds.includes(room.phaseId))) return false;
  if (filter?.numbers?.length && (!room.number || !filter.numbers.includes(room.number))) return false;
  if (filter?.numberContains && !containsIgnoreCase(room.number ?? "", filter.numberContains)) return false;
  if (filter?.nameContains && !containsIgnoreCase(room.name ?? "", filter.nameContains)) return false;
  if (filter?.departmentContains && !containsIgnoreCase(room.department ?? "", filter.departmentContains)) return false;
  return true;
}

function projectRoom(room: RoomSummary, fields: string[]): RoomSummary {
  const projected: RoomSummary = {
    id: room.id,
  };

  for (const field of fields) {
    switch (field) {
      case "id":
        break;
      case "uniqueId":
      case "number":
      case "name":
      case "levelId":
      case "levelName":
      case "phaseId":
      case "phaseName":
      case "area":
      case "volume":
      case "perimeter":
      case "location":
      case "isPlaced":
      case "isEnclosed":
      case "department":
        const roomRecord = room as unknown as Record<string, unknown>;
        if (roomRecord[field] !== undefined) {
          (projected as unknown as Record<string, unknown>)[field] = roomRecord[field];
        }
        break;
      default:
        break;
    }
  }

  return projected;
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
    case "create_room":
      return {
        document: activeDocument.title,
        levelId: operation.levelId,
      };
    case "delete_element":
      return {
        elementId: operation.elementId,
        expectedUniqueId: operation.expectedUniqueId,
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
    case "create_room":
      return {
        levelId: operation.levelId,
        location: operation.location,
        name: operation.name,
        number: operation.number,
        department: operation.department,
        allowDuplicateNumber: operation.allowDuplicateNumber,
      };
    case "delete_element":
      return {
        deleted: true,
        expectedPinned: operation.expectedPinned,
        allowPinned: operation.allowPinned,
      };
    default:
      return assertNever(operation);
  }
}

function isHighRiskOperation(operation: ChangeOperation): boolean {
  return operation.type === "delete_element";
}

function isMediumRiskOperation(operation: ChangeOperation): boolean {
  return (
    operation.type === "create_level" ||
    operation.type === "create_wall" ||
    operation.type === "create_grid" ||
    operation.type === "create_floor" ||
    operation.type === "create_room" ||
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
