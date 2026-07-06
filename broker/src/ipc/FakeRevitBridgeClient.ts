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
  ChangePreviewItem,
  ChangePreviewResult,
  ChangeSetRequest,
  CreateProjectFromTemplateRequest,
  CreateProjectFromTemplateResult,
  CurrentViewRequest,
  CurrentViewResult,
  LevelSummary,
  MaterialQuantitiesRequest,
  MaterialQuantitiesResult,
  ModelContextRequest,
  ModelContextResult,
  ModelReadinessRequest,
  ModelReadinessResult,
  ModelStatisticsRequest,
  ModelStatisticsResult,
  ParameterDescribeRequest,
  ParameterDescribeResult,
  QueryItem,
  QueryRequest,
  QueryResult,
  RevitDocumentSummary,
  RevitStatus,
  RoomSummary,
  RoomsRequest,
  RoomsResult,
  ScopedElementListRequest,
  ScopedElementListResult,
  SheetSummary,
  SheetsRequest,
  SheetsResult,
  ViewsRequest,
  ViewsResult,
  WarningItem,
  WarningsRequest,
  WarningsResult,
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

const fakeViews: ViewsResult["items"] = [
  {
    id: "1024",
    uniqueId: "view-1024",
    name: "Level 1",
    type: "FloorPlan",
    isGraphical: true,
    isTemplate: false,
    canBePrinted: true,
    scale: 100,
    detailLevel: "Medium",
    discipline: "Coordination",
    associatedLevelId: "311",
    associatedLevelName: "Level 1",
  },
  {
    id: "1025",
    uniqueId: "view-1025",
    name: "Working 3D",
    type: "ThreeD",
    isGraphical: true,
    isTemplate: false,
    canBePrinted: true,
    scale: 100,
  },
];

const fakeSheets: SheetSummary[] = [
  {
    id: "1101",
    uniqueId: "sheet-1101",
    sheetNumber: "A-101",
    name: "Plans",
    titleBlockIds: ["9300"],
    placedViews: [
      {
        viewportId: "1201",
        viewId: "1024",
        viewName: "Level 1",
        viewType: "FloorPlan",
        center: {
          x: { value: 250, unit: "mm", system: "metric" },
          y: { value: 180, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
      },
    ],
  },
];

const capabilities = [
  "revit.status",
  "revit.list_documents",
  "revit.create_project_from_template",
  "revit.get_levels",
  "revit.get_views",
  "revit.get_sheets",
  "revit.get_current_view",
  "revit.get_current_view_elements",
  "revit.get_selection",
  "revit.analyze_model",
  "revit.get_model_readiness",
  "revit.get_model_context",
  "revit.get_material_quantities",
  "revit.get_warnings",
  "revit.get_rooms",
  "revit.catalog",
  "revit.query",
  "revit.describe_parameters",
  "revit.preview_change_set",
  "revit.apply_change_set",
  "revit.cancel_request",
];

const fakeModelContext = {
  projectInfo: {
    id: "100",
    uniqueId: "project-info-100",
    number: "P-001",
    name: "Sample Project",
    clientName: "Example Client",
    status: "Test",
    issueDate: "2026-07-06",
    buildingName: "Sample Building",
    organizationName: "Revit MCP Next",
  },
  phases: [
    {
      id: "201",
      name: "Existing",
      sequence: 0,
    },
    {
      id: "202",
      name: "New Construction",
      sequence: 1,
    },
  ],
  worksets: [
    {
      id: "1",
      uniqueId: "00000000-0000-0000-0000-000000000001",
      name: "Shared Levels and Grids",
      kind: "UserWorkset",
      isOpen: true,
      isEditable: true,
      isVisibleByDefault: true,
      isDefaultWorkset: true,
      owner: "",
    },
  ],
  designOptions: [
    {
      id: "301",
      uniqueId: "design-option-301",
      name: "Option A",
      isPrimary: true,
      isActive: true,
      optionSetId: "300",
      optionSetName: "Entry Layout",
    },
    {
      id: "302",
      uniqueId: "design-option-302",
      name: "Option B",
      isPrimary: false,
      isActive: false,
      optionSetId: "300",
      optionSetName: "Entry Layout",
    },
  ],
  revitLinks: [
    {
      id: "401",
      uniqueId: "revit-link-401",
      name: "Site.rvt",
      typeId: "9401",
      typeName: "Site.rvt",
      isLoaded: true,
      linkedDocumentTitle: "Site.rvt",
      linkedDocumentPath: "C:\\Projects\\Site.rvt",
    },
  ],
};

const fakeQueryItems: QueryItem[] = [
  {
    id: "501",
    uniqueId: "wall-501",
    category: "OST_Walls",
    class: "Wall",
    name: "Basic Wall",
    typeId: "9001",
    levelId: "311",
    location: {
      start: {
        x: { value: 0, unit: "mm", system: "metric" },
        y: { value: 0, unit: "mm", system: "metric" },
        z: { value: 0, unit: "mm", system: "metric" },
      },
      end: {
        x: { value: 4000, unit: "mm", system: "metric" },
        y: { value: 0, unit: "mm", system: "metric" },
        z: { value: 0, unit: "mm", system: "metric" },
      },
      length: { value: 4000, unit: "mm", system: "metric" },
    },
    bounds: {
      min: {
        x: { value: 0, unit: "mm", system: "metric" },
        y: { value: -100, unit: "mm", system: "metric" },
        z: { value: 0, unit: "mm", system: "metric" },
      },
      max: {
        x: { value: 4000, unit: "mm", system: "metric" },
        y: { value: 100, unit: "mm", system: "metric" },
        z: { value: 3000, unit: "mm", system: "metric" },
      },
    },
  },
  {
    id: "502",
    uniqueId: "door-502",
    category: "OST_Doors",
    class: "FamilyInstance",
    name: "Single-Flush",
    typeId: "9200",
    levelId: "311",
  },
];

const fakeWarnings: WarningItem[] = [
  {
    id: "warning-duplicate-mark-501",
    severity: "Warning",
    description: "Elements have duplicate Mark values.",
    failureDefinitionId: "6b45b815-19dd-4a7e-8f5b-51f6f4be7f01",
    defaultResolution: "Edit one of the duplicate Mark values.",
    failingElementIds: ["501", "502"],
    additionalElementIds: [],
    failingElementCount: 2,
    additionalElementCount: 0,
  },
  {
    id: "warning-room-not-enclosed-601",
    severity: "Warning",
    description: "Room is not in a properly enclosed region.",
    failureDefinitionId: "2f88f84c-f1a9-4e14-995e-8c8a5721fd4b",
    defaultResolution: "Adjust room-bounding elements or room placement.",
    failingElementIds: ["601"],
    additionalElementIds: ["311"],
    failingElementCount: 1,
    additionalElementCount: 1,
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
    kind: "familySymbols",
    id: "9201",
    uniqueId: "family-symbol-9201",
    category: "OST_Furniture",
    builtInCategory: "OST_Furniture",
    class: "FamilySymbol",
    name: "1200 x 600mm",
    familyName: "Desk",
    familyId: "9202",
    isActive: false,
    placementType: "OneLevelBased",
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
  {
    kind: "textNoteTypes",
    id: "9500",
    uniqueId: "text-note-type-9500",
    class: "TextNoteType",
    category: "Text Notes",
    name: "2.5mm Arial",
    familyName: "Text",
  },
  {
    kind: "dimensionTypes",
    id: "9600",
    uniqueId: "dimension-type-9600",
    class: "DimensionType",
    category: "Dimensions",
    name: "Linear 2.5mm",
    familyName: "Linear Dimension Style",
  },
  {
    kind: "tagTypes",
    id: "9700",
    uniqueId: "room-tag-type-9700",
    class: "FamilySymbol",
    category: "Room Tags",
    builtInCategory: "OST_RoomTags",
    name: "Room Tag",
    familyName: "Room Tag",
    isActive: true,
  },
  {
    kind: "tagTypes",
    id: "9701",
    uniqueId: "wall-tag-symbol-9701",
    class: "FamilySymbol",
    category: "Wall Tags",
    builtInCategory: "OST_WallTags",
    name: "Wall Tag",
    familyName: "Wall Tag",
    familyId: "9698",
    isActive: true,
  },
];

function resolveParameterDescribeOptions(payload: ParameterDescribeRequest): {
  preset: NonNullable<ParameterDescribeRequest["preset"]>;
  includeTypeParameters: boolean;
  includeReadOnly: boolean;
  includeValues: boolean;
  limit: number;
  parameterLimit: number;
} {
  const preset = payload.preset ?? "writableEdit";
  const defaults =
    preset === "full"
      ? { includeTypeParameters: true, includeReadOnly: true, includeValues: true, limit: 20, parameterLimit: 80 }
      : preset === "namesOnly"
        ? { includeTypeParameters: true, includeReadOnly: true, includeValues: false, limit: 10, parameterLimit: 120 }
        : { includeTypeParameters: false, includeReadOnly: false, includeValues: false, limit: 10, parameterLimit: 40 };

  return {
    preset,
    includeTypeParameters: payload.includeTypeParameters ?? defaults.includeTypeParameters,
    includeReadOnly: payload.includeReadOnly ?? defaults.includeReadOnly,
    includeValues: payload.includeValues ?? defaults.includeValues,
    limit: Math.min(payload.limit ?? defaults.limit, 100),
    parameterLimit: Math.min(payload.parameterLimit ?? defaults.parameterLimit, 200),
  };
}

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
      addinAssembly: {
        assemblyPath: "C:\\fake\\RevitMcpNext.Addin.dll",
        assemblySha256: "f".repeat(64),
        fileVersion: "0.1.0.0",
        productVersion: "0.1.0",
      },
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

  async createProjectFromTemplate(
    request: BridgeRequest<CreateProjectFromTemplateRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CreateProjectFromTemplateResult>> {
    maybeAbort(options);
    if (request.payload.confirm !== true) {
      return fail(request, "CONFIRMATION_REQUIRED", "revit.create_project_from_template requires confirm=true.");
    }

    const outputPath = request.payload.outputPath;
    const document: RevitDocumentSummary = {
      ...activeDocument,
      title: outputPath.split(/[\\/]/).pop() ?? "Disposable.rvt",
      path: outputPath,
      fingerprint: `doc-fixture-${createHash("sha256").update(outputPath).digest("hex").slice(0, 16)}`,
      isActive: true,
      generation: activeDocument.generation + 1,
    };

    return ok(request, {
      templatePath: request.payload.templatePath,
      outputPath,
      overwritten: request.payload.overwrite === true,
      activated: true,
      document,
      source: "revit-api",
    });
  }

  async getLevels(
    request: BridgeRequest<{ documentFingerprint?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<LevelSummary[]>> {
    maybeAbort(options);
    return ok(request, levels);
  }

  async getViews(
    request: BridgeRequest<ViewsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ViewsResult>> {
    maybeAbort(options);
    const limit = Math.min(request.payload.limit ?? 50, 500);
    const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
    const filter = request.payload.filter ?? {};
    const items = fakeViews.filter((view) => matchesViewFilter(view, filter));
    const page = items.slice(offset, offset + limit);
    const truncated = offset + page.length < items.length;
    return ok(request, {
      document: documentReference(),
      items: page,
      returnedCount: page.length,
      totalCount: request.payload.includeTotalCount ? items.length : undefined,
      limit,
      cursor: truncated ? String(offset + page.length) : undefined,
      truncated,
      fields: request.payload.fields ?? ["id", "uniqueId", "name", "type", "isGraphical", "isTemplate", "canBePrinted"],
      scope: "views",
      source: "fake-bridge",
    });
  }

  async getSheets(
    request: BridgeRequest<SheetsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<SheetsResult>> {
    maybeAbort(options);
    const limit = Math.min(request.payload.limit ?? 50, 500);
    const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
    const filter = request.payload.filter ?? {};
    const items = fakeSheets.filter((sheet) => matchesSheetFilter(sheet, filter));
    const page = items.slice(offset, offset + limit).map((sheet) => ({
      ...sheet,
      placedViews: request.payload.includePlacedViews ? sheet.placedViews : undefined,
    }));
    const truncated = offset + page.length < items.length;
    return ok(request, {
      document: documentReference(),
      items: page,
      returnedCount: page.length,
      totalCount: request.payload.includeTotalCount ? items.length : undefined,
      limit,
      cursor: truncated ? String(offset + page.length) : undefined,
      truncated,
      fields: request.payload.fields ?? ["id", "uniqueId", "sheetNumber", "name", "titleBlockIds"],
      scope: "sheets",
      source: "fake-bridge",
    });
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
    result.selection = { count: fakeQueryItems.length, available: true };
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

  async getModelReadiness(
    request: BridgeRequest<ModelReadinessRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelReadinessResult>> {
    maybeAbort(options);
    const requested = request.payload.scenarios;
    const requestedSet = new Set<string>(requested ?? []);
    const scenarios = buildReadinessScenarios(request.payload.includeHints !== false).filter(
      (scenario) => requestedSet.size === 0 || requestedSet.has(scenario.name)
    );
    return ok(request, {
      document: documentReference(),
      activeView: activeDocument.activeView,
      scenarios,
      readyCount: scenarios.filter((scenario) => scenario.ready).length,
      totalCount: scenarios.length,
      source: "fake-bridge",
    });
  }

  async getModelContext(
    request: BridgeRequest<ModelContextRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelContextResult>> {
    maybeAbort(options);
    const payload = request.payload;
    const result: ModelContextResult = {
      document: documentReference(),
      source: "fake-bridge",
    };
    if (payload.includeProjectInfo !== false) result.projectInfo = fakeModelContext.projectInfo;
    if (payload.includePhases !== false) {
      result.phases = section(fakeModelContext.phases, Math.min(payload.phaseLimit ?? 50, 200), payload.includeTotalCount);
    }
    if (payload.includeWorksets !== false) {
      result.worksets = {
        ...section(fakeModelContext.worksets, Math.min(payload.worksetLimit ?? 50, 200), payload.includeTotalCount),
        available: true,
      };
    }
    if (payload.includeDesignOptions !== false) {
      result.designOptions = section(fakeModelContext.designOptions, Math.min(payload.designOptionLimit ?? 50, 200), payload.includeTotalCount);
    }
    if (payload.includeRevitLinks !== false) {
      result.revitLinks = section(fakeModelContext.revitLinks, Math.min(payload.revitLinkLimit ?? 50, 200), payload.includeTotalCount);
    }
    return ok(request, result);
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

  async getWarnings(
    request: BridgeRequest<WarningsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<WarningsResult>> {
    maybeAbort(options);
    const limit = Math.min(request.payload.limit ?? 50, 200);
    const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
    const filter = request.payload.filter ?? {};
    const fields = request.payload.fields ?? warningFieldsForPreset(request.payload.preset);
    const filteredItems = fakeWarnings.filter((warning) => matchesWarningFilter(warning, filter));
    const page = filteredItems.slice(offset, offset + limit).map((warning) => projectWarning(warning, fields));
    const truncated = offset + page.length < filteredItems.length;

    return ok(request, {
      document: documentReference(),
      items: page,
      returnedCount: page.length,
      totalCount: request.payload.includeTotalCount ? filteredItems.length : undefined,
      limit,
      cursor: truncated ? String(offset + page.length) : undefined,
      truncated,
      fields,
      scope: "warnings",
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
    const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
    const filter = request.payload.filter ?? {};
    const fields = request.payload.fields ?? queryFieldsForPreset(request.payload.preset);
    const filteredItems = fakeQueryItems.filter((item) => matchesQueryFilter(item, filter));
    const items = filteredItems.slice(offset, offset + limit).map((item) => projectQueryItem(item, fields));
    const truncated = offset + items.length < filteredItems.length;
    const hasExplicitIdentityFilter = Boolean(filter.elementIds?.length || filter.uniqueIds?.length);

    return ok(request, {
      items,
      totalCount: request.payload.includeTotalCount ? filteredItems.length : undefined,
      returnedCount: items.length,
      limit,
      cursor: truncated ? String(offset + items.length) : undefined,
      truncated,
      fields,
      units: {
        elevation: "mm",
        length: "mm",
        location: "mm",
        bounds: "mm",
      },
      scope:
        hasExplicitIdentityFilter
          ? "elements"
          : filter.viewId
            ? `view:${filter.viewId}`
            : "document",
      source: "fake-bridge",
    });
  }

  async describeParameters(
    request: BridgeRequest<ParameterDescribeRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ParameterDescribeResult>> {
    maybeAbort(options);
    const describeOptions = resolveParameterDescribeOptions(request.payload);
    const limit = describeOptions.limit;
    const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
    let targets = fakeQueryItems;
    const elementIds = request.payload.filter.elementIds ?? [];
    if (elementIds.length > 0) {
      targets = targets.filter((item) => elementIds.includes(item.id));
    }
    const page = targets.slice(offset, offset + limit).map((item) => {
      const parameters = [
        {
          name: "Mark",
          storageType: "String",
          source: "instance" as const,
          isReadOnly: false,
          hasValue: true,
          value: "A-101",
          valueString: "A-101",
        },
        {
          name: "Type Name",
          storageType: "String",
          source: "type" as const,
          isReadOnly: true,
          hasValue: true,
          value: "Generic - 200mm",
          valueString: "Generic - 200mm",
        },
      ].filter((parameter) => {
        if (!describeOptions.includeTypeParameters && parameter.source === "type") return false;
        if (!describeOptions.includeReadOnly && parameter.isReadOnly) return false;
        if (request.payload.nameContains && !containsIgnoreCase(parameter.name, request.payload.nameContains)) return false;
        return true;
      }).map((parameter) => {
        if (describeOptions.includeValues) return parameter;
        const { value: _value, valueString: _valueString, ...compactParameter } = parameter;
        return compactParameter;
      });
      const parameterLimit = describeOptions.parameterLimit;
      return {
        id: item.id,
        uniqueId: item.uniqueId,
        category: item.category,
        class: item.class,
        name: item.name,
        typeId: item.typeId,
        typeName: "Generic - 200mm",
        parameters: parameters.slice(0, parameterLimit),
        parameterCount: parameters.length,
        truncated: parameters.length > parameterLimit,
      };
    });
    const truncated = offset + page.length < targets.length;
    return ok(request, {
      document: documentReference(),
      items: page,
      returnedCount: page.length,
      totalCount: request.payload.includeTotalCount ? targets.length : undefined,
      limit,
      cursor: truncated ? String(offset + page.length) : undefined,
      truncated,
      parameterLimit: describeOptions.parameterLimit,
      preset: describeOptions.preset,
      scope: elementIds.length > 0 ? "elements" : "document",
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
    const changes = request.payload.operations.map((operation, index) => getPreviewChange(operation, index, "ready"));
    const ready = changes.every((change) => change.status === "ready");
    return ok(request, {
      previewId: `fake-preview-${request.payload.operations.length}`,
      documentFingerprint: metadata.documentFingerprint,
      changeSetHash: metadata.changeSetHash,
      baseGeneration: metadata.baseGeneration,
      expiresAt: metadata.expiresAt,
      transactionName: request.payload.transactionName,
      operationCount: request.payload.operations.length,
      ready,
      requiresConfirmation: true,
      riskLevel: request.payload.operations.some(isHighRiskOperation)
        ? "high"
        : request.payload.operations.some(isMediumRiskOperation)
          ? "medium"
          : "low",
      changes,
    });
  }

  async applyChange(
    request: BridgeRequest<ChangeApplyRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ChangeApplyResult>> {
    maybeAbort(options);
    const metadata = getChangeSetMetadata(request.payload);
    const previewChanges = request.payload.operations.map((operation, index) => getPreviewChange(operation, index, "ready"));
    const ready = previewChanges.every((change) => change.status === "ready");
    const applied = Boolean(request.payload.confirm && ready);
    return ok(request, {
      previewId: request.payload.previewId,
      documentFingerprint: metadata.documentFingerprint,
      changeSetHash: metadata.changeSetHash,
      baseGeneration: metadata.baseGeneration,
      transactionName: request.payload.transactionName,
      applied,
      changedCount: applied ? request.payload.operations.length : 0,
      changes: previewChanges.map((change) => (change.status === "ready" && applied ? { ...change, status: "applied" } : change)),
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

function queryFieldsForPreset(preset: QueryRequest["preset"]): string[] {
  switch (preset) {
    case "idOnly":
      return ["id"];
    case "schedule":
      return ["id", "category", "class", "name", "typeId", "levelId"];
    case "geometrySummary":
      return ["id", "uniqueId", "category", "class", "name", "typeId", "levelId", "location", "bounds"];
    case "summary":
    default:
      return ["id", "category", "class", "name"];
  }
}

function matchesQueryFilter(item: QueryItem, filter: QueryRequest["filter"]): boolean {
  if (filter.elementIds?.length && !filter.elementIds.includes(item.id)) return false;
  if (filter.uniqueIds?.length && (!item.uniqueId || !filter.uniqueIds.includes(item.uniqueId))) return false;
  if (filter.categories?.length && !filter.categories.some((category) => equalsCatalogToken(category, item.category))) return false;
  if (filter.classes?.length && !filter.classes.some((className) => equalsCatalogToken(className, item.class))) return false;
  if (filter.levelIds?.length && (!item.levelId || !filter.levelIds.includes(item.levelId))) return false;
  if (filter.viewId && filter.viewId !== activeDocument.activeView?.id) return false;
  return true;
}

function projectQueryItem(item: QueryItem, fields: string[]): QueryItem {
  const projected: QueryItem = { id: item.id };
  const source = item as unknown as Record<string, unknown>;
  const target = projected as unknown as Record<string, unknown>;

  for (const field of fields) {
    if (field === "id") continue;
    if (source[field] !== undefined) target[field] = source[field];
  }

  return projected;
}

function buildScopedElementList(
  request: BridgeRequest<ScopedElementListRequest>,
  scope: string
): ScopedElementListResult {
  const limit = Math.min(request.payload.limit ?? 50, 500);
  const offset = Number.parseInt(request.payload.cursor ?? "0", 10) || 0;
  const filter = request.payload.filter ?? {};
  const fields = request.payload.fields ?? queryFieldsForPreset(request.payload.preset);
  const filteredItems = fakeQueryItems.filter((item) => matchesQueryFilter(item, filter));
  const items = filteredItems.slice(offset, offset + limit).map((item) => projectQueryItem(item, fields));
  const truncated = offset + items.length < filteredItems.length;
  return {
    document: documentReference(),
    items,
    totalCount: request.payload.includeTotalCount ? filteredItems.length : undefined,
    returnedCount: items.length,
    limit,
    cursor: truncated ? String(offset + items.length) : undefined,
    truncated,
    fields,
    units: {
      elevation: "mm",
      length: "mm",
      location: "mm",
      bounds: "mm",
    },
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

function buildReadinessScenarios(includeHints: boolean): ModelReadinessResult["scenarios"] {
  const wallTypeIds = catalogItems
    .filter((item) => item.kind === "elementTypes" && item.class === "WallType")
    .map((item) => item.id);
  const floorTypeIds = catalogItems
    .filter((item) => item.kind === "elementTypes" && item.class === "FloorType")
    .map((item) => item.id);
  const hostedFamilySymbolIds = catalogItems
    .filter((item) => item.kind === "familySymbols" && item.placementType?.includes("Hosted"))
    .map((item) => item.id);
  const levelBasedFamilySymbolIds = catalogItems
    .filter((item) => item.kind === "familySymbols" && item.placementType === "OneLevelBased")
    .map((item) => item.id);
  const wallHostIds = fakeQueryItems.filter((item) => item.class === "Wall").map((item) => item.id);
  const target = buildCatalogTarget("501");
  const activeView = activeDocument.activeView;

  return [
    readinessScenario(
      "levels",
      levels.length > 0,
      levels.length > 0 ? [] : ["level"],
      "Create or open a model with at least one level.",
      { levelCount: levels.length, defaultLevelId: levels[0]?.id },
      includeHints
    ),
    readinessScenario(
      "wallCreation",
      levels.length > 0 && wallTypeIds.length > 0,
      [...(levels.length > 0 ? [] : ["level"]), ...(wallTypeIds.length > 0 ? [] : ["wallType"])],
      "Use revit.get_levels and revit.catalog kind=elementTypes before create_wall.",
      { levelCount: levels.length, wallTypeIds },
      includeHints
    ),
    readinessScenario(
      "floorCreation",
      levels.length > 0 && floorTypeIds.length > 0,
      [...(levels.length > 0 ? [] : ["level"]), ...(floorTypeIds.length > 0 ? [] : ["floorType"])],
      "Use revit.get_levels and revit.catalog kind=elementTypes before create_floor.",
      { levelCount: levels.length, floorTypeIds },
      includeHints
    ),
    readinessScenario(
      "roomCreation",
      levels.length > 0,
      levels.length > 0 ? [] : ["level"],
      "Use revit.get_levels before create_room.",
      { levelCount: levels.length, defaultLevelId: levels[0]?.id },
      includeHints
    ),
    readinessScenario(
      "roomReadback",
      fakeRooms.some((room) => room.isPlaced !== false),
      fakeRooms.some((room) => room.isPlaced !== false) ? [] : ["placedRoom"],
      "Use revit.get_rooms with includeUnplaced=false to verify placed rooms.",
      { placedRoomCount: fakeRooms.filter((room) => room.isPlaced !== false).length },
      includeHints
    ),
    readinessScenario(
      "typeChange",
      target?.canChangeType === true && (target.validTypeCount ?? 0) > 0,
      target?.canChangeType === true && (target.validTypeCount ?? 0) > 0 ? [] : ["typeChangeTarget"],
      "Use revit.catalog kind=elementTypes with filter.forElementId before change_element_type.",
      { targetElementId: target?.elementId, validTypeCount: target?.validTypeCount },
      includeHints
    ),
    readinessScenario(
      "familyPlacement",
      levels.length > 0 && wallHostIds.length > 0 && hostedFamilySymbolIds.length > 0 && levelBasedFamilySymbolIds.length > 0,
      [
        ...(levels.length > 0 ? [] : ["level"]),
        ...(wallHostIds.length > 0 ? [] : ["wallHost"]),
        ...(hostedFamilySymbolIds.length > 0 ? [] : ["hostedFamilySymbol"]),
        ...(levelBasedFamilySymbolIds.length > 0 ? [] : ["levelBasedFamilySymbol"]),
      ],
      "Use revit.catalog kind=familySymbols preset=placement before place_family_instance.",
      {
        hostedFamilySymbolId: hostedFamilySymbolIds[0],
        levelBasedFamilySymbolId: levelBasedFamilySymbolIds[0],
        hostedFamilySymbolIds,
        levelBasedFamilySymbolIds,
        wallHostIds,
        defaultLevelId: levels[0]?.id,
      },
      includeHints
    ),
    readinessScenario(
      "selection",
      fakeQueryItems.length > 0,
      fakeQueryItems.length > 0 ? [] : ["selection"],
      "Select elements in Revit or query by explicit element IDs.",
      { selectionCount: fakeQueryItems.length },
      includeHints
    ),
    readinessScenario(
      "annotations",
      activeView?.isGraphical === true,
      activeView?.isGraphical === true ? [] : ["graphicalActiveView"],
      "Switch to a graphical view before annotation workflows.",
      { activeViewId: activeView?.id, activeViewType: activeView?.type, annotationTypesDetectable: false },
      includeHints
    ),
  ];
}

function readinessScenario(
  name: ModelReadinessResult["scenarios"][number]["name"],
  ready: boolean,
  missing: string[],
  nextAction: string,
  hints: Record<string, unknown>,
  includeHints: boolean
): ModelReadinessResult["scenarios"][number] {
  return {
    name,
    ready,
    missing,
    ...(includeHints ? { nextAction, hints } : {}),
  };
}

function section<T>(items: T[], limit: number, includeTotalCount?: boolean): {
  items: T[];
  returnedCount: number;
  totalCount?: number;
  limit: number;
  truncated: boolean;
} {
  const page = items.slice(0, limit);
  return {
    items: page,
    returnedCount: page.length,
    totalCount: includeTotalCount ? items.length : undefined,
    limit,
    truncated: page.length < items.length,
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

function warningFieldsForPreset(preset?: string): string[] {
  switch (preset) {
    case "idOnly":
      return ["id"];
    case "elements":
      return ["id", "severity", "description", "failingElementIds", "additionalElementIds", "failingElementCount", "additionalElementCount"];
    case "full":
      return [
        "id",
        "severity",
        "description",
        "failureDefinitionId",
        "defaultResolution",
        "failingElementIds",
        "additionalElementIds",
        "failingElementCount",
        "additionalElementCount",
      ];
    default:
      return ["id", "severity", "description", "failingElementCount", "additionalElementCount"];
  }
}

function matchesWarningFilter(warning: WarningItem, filter: WarningsRequest["filter"]): boolean {
  if (!filter) return true;
  if (filter.elementIds?.length) {
    const ids = new Set([...(warning.failingElementIds ?? []), ...(warning.additionalElementIds ?? [])]);
    if (!filter.elementIds.some((id) => ids.has(id))) return false;
  }
  if (filter.failureDefinitionIds?.length && (!warning.failureDefinitionId || !filter.failureDefinitionIds.includes(warning.failureDefinitionId))) {
    return false;
  }
  if (filter.severities?.length && (!warning.severity || !filter.severities.some((severity) => equalsCatalogToken(severity, warning.severity)))) {
    return false;
  }
  if (filter.descriptionContains && !containsIgnoreCase(warning.description ?? "", filter.descriptionContains)) {
    return false;
  }
  return true;
}

function projectWarning(warning: WarningItem, fields: string[]): WarningItem {
  const projected: WarningItem = { id: warning.id };
  for (const field of fields) {
    if (field === "id") continue;
    const value = (warning as unknown as Record<string, unknown>)[field];
    if (value !== undefined) {
      (projected as unknown as Record<string, unknown>)[field] = value;
    }
  }
  return projected;
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

function matchesViewFilter(view: ViewsResult["items"][number], filter: ViewsRequest["filter"]): boolean {
  if (!filter) return true;
  if (filter.viewIds?.length && !filter.viewIds.includes(view.id)) return false;
  if (filter.uniqueIds?.length && (!view.uniqueId || !filter.uniqueIds.includes(view.uniqueId))) return false;
  if (filter.viewTypes?.length && !filter.viewTypes.some((viewType) => equalsCatalogToken(viewType, view.type))) return false;
  if (filter.nameContains && !containsIgnoreCase(view.name, filter.nameContains)) return false;
  if (filter.isTemplate !== undefined && view.isTemplate !== filter.isTemplate) return false;
  if (filter.isGraphical !== undefined && view.isGraphical !== filter.isGraphical) return false;
  if (filter.canBePrinted !== undefined && view.canBePrinted !== filter.canBePrinted) return false;
  return true;
}

function matchesSheetFilter(sheet: SheetSummary, filter: SheetsRequest["filter"]): boolean {
  if (!filter) return true;
  if (filter.sheetIds?.length && !filter.sheetIds.includes(sheet.id)) return false;
  if (filter.uniqueIds?.length && (!sheet.uniqueId || !filter.uniqueIds.includes(sheet.uniqueId))) return false;
  if (filter.numbers?.length && (!sheet.sheetNumber || !filter.numbers.includes(sheet.sheetNumber))) return false;
  if (filter.numberContains && !containsIgnoreCase(sheet.sheetNumber ?? "", filter.numberContains)) return false;
  if (filter.nameContains && !containsIgnoreCase(sheet.name ?? "", filter.nameContains)) return false;
  if (filter.titleBlockIds?.length && !(sheet.titleBlockIds ?? []).some((id) => filter.titleBlockIds?.includes(id))) {
    return false;
  }
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
  if (
    filter.categories?.length &&
    !filter.categories.some(
      (category) =>
        equalsCatalogToken(category, item.category) ||
        equalsCatalogToken(category, item.builtInCategory),
    )
  ) {
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

function getPreviewChange(operation: ChangeOperation, index: number, readyStatus: "ready" | "applied"): ChangePreviewItem {
  const message = getIdentityGuardFailure(operation);
  if (message) {
    return {
      operationIndex: index,
      operationId: operation.id,
      type: operation.type,
      status: "blocked",
      target: getOperationTarget(operation),
      message,
    };
  }

  return {
    operationIndex: index,
    operationId: operation.id,
    type: operation.type,
    status: readyStatus,
    target: getOperationTarget(operation),
    after: getOperationAfter(operation),
  };
}

function getIdentityGuardFailure(operation: ChangeOperation): string | undefined {
  switch (operation.type) {
    case "set_parameter":
    case "tag_element":
    case "move_element":
    case "rotate_element":
    case "copy_element":
    case "change_element_type":
    case "set_element_pinned":
    case "delete_element":
      return getExpectedUniqueIdFailure(operation.elementId, operation.expectedUniqueId, "Element");
    case "tag_room":
      return getExpectedUniqueIdFailure(operation.roomId, operation.expectedUniqueId, "Room");
    case "place_family_instance":
      return getExpectedUniqueIdFailure(
        operation.hostElementId,
        operation.expectedHostUniqueId,
        "Host element",
        "expectedHostUniqueId"
      );
    default:
      return undefined;
  }
}

function getExpectedUniqueIdFailure(
  elementId: string | undefined,
  expectedUniqueId: string | undefined,
  label: string,
  fieldName = "expectedUniqueId"
): string | undefined {
  if (!expectedUniqueId) return undefined;
  if (!elementId) return `${label} uniqueId guard requires an element ID.`;
  const actualUniqueId = getFakeElementUniqueId(elementId);
  if (actualUniqueId === expectedUniqueId) return undefined;
  return `${label} ${elementId} uniqueId did not match ${fieldName}.`;
}

function getFakeElementUniqueId(elementId: string): string | undefined {
  return (
    fakeQueryItems.find((item) => item.id === elementId)?.uniqueId ??
    fakeRooms.find((room) => room.id === elementId)?.uniqueId ??
    levels.find((level) => level.id === elementId)?.uniqueId ??
    fakeViews.find((view) => view.id === elementId)?.uniqueId ??
    fakeSheets.find((sheet) => sheet.id === elementId)?.uniqueId ??
    catalogItems.find((item) => item.id === elementId)?.uniqueId
  );
}

function fakeUniqueIdField(elementId: string | undefined, fieldName = "uniqueId"): Record<string, unknown> {
  if (!elementId) return {};
  const uniqueId = getFakeElementUniqueId(elementId);
  return uniqueId ? { [fieldName]: uniqueId } : {};
}

function getOperationTarget(operation: ChangeOperation): Record<string, unknown> {
  switch (operation.type) {
    case "set_parameter":
      return {
        elementId: operation.elementId,
        ...fakeUniqueIdField(operation.elementId),
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
    case "place_family_instance":
      return {
        document: activeDocument.title,
        familySymbolId: operation.familySymbolId,
        hostElementId: operation.hostElementId,
        ...fakeUniqueIdField(operation.hostElementId, "hostUniqueId"),
        levelId: operation.levelId,
      };
    case "create_sheet":
      return {
        document: activeDocument.title,
        sheetNumber: operation.sheetNumber,
        titleBlockTypeId: operation.titleBlockTypeId,
      };
    case "place_view_on_sheet":
      return {
        sheetId: operation.sheetId,
        viewId: operation.viewId,
      };
    case "create_text_note":
      return {
        document: activeDocument.title,
        viewId: operation.viewId,
        textNoteTypeId: operation.textNoteTypeId,
      };
    case "load_family":
      return {
        document: activeDocument.title,
        familyPath: operation.familyPath,
        expectedSha256: operation.expectedSha256,
      };
    case "tag_room":
      return {
        document: activeDocument.title,
        roomId: operation.roomId,
        ...fakeUniqueIdField(operation.roomId),
        viewId: operation.viewId,
        tagTypeId: operation.tagTypeId,
      };
    case "tag_element":
      return {
        document: activeDocument.title,
        elementId: operation.elementId,
        ...fakeUniqueIdField(operation.elementId),
        viewId: operation.viewId,
        tagTypeId: operation.tagTypeId,
      };
    case "move_element":
      return {
        elementId: operation.elementId,
        ...fakeUniqueIdField(operation.elementId),
      };
    case "rotate_element":
      return {
        elementId: operation.elementId,
        ...fakeUniqueIdField(operation.elementId),
      };
    case "copy_element":
      return {
        sourceElementId: operation.elementId,
        ...fakeUniqueIdField(operation.elementId, "sourceUniqueId"),
      };
    case "change_element_type":
      return {
        elementId: operation.elementId,
        ...fakeUniqueIdField(operation.elementId),
        typeId: operation.typeId,
      };
    case "set_element_pinned":
      return {
        elementId: operation.elementId,
        ...fakeUniqueIdField(operation.elementId),
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
        ...fakeUniqueIdField(operation.elementId),
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
    case "place_family_instance":
      return {
        familySymbolId: operation.familySymbolId,
        hostElementId: operation.hostElementId,
        levelId: operation.levelId,
        location: operation.location,
        rotation: operation.rotation,
        flipFacing: operation.flipFacing,
        flipHand: operation.flipHand,
        allowPinnedHost: operation.allowPinnedHost,
      };
    case "create_sheet":
      return {
        id: "1301",
        uniqueId: "sheet-1301",
        sheetNumber: operation.sheetNumber,
        name: operation.name,
        titleBlockTypeId: operation.titleBlockTypeId,
      };
    case "place_view_on_sheet":
      return {
        viewportId: "1302",
        sheetId: operation.sheetId,
        viewId: operation.viewId,
        center: operation.center,
      };
    case "create_text_note":
      return {
        id: "1303",
        uniqueId: "textnote-1303",
        viewId: operation.viewId,
        text: operation.text,
        textLength: operation.text.length,
        position: operation.position,
        textNoteTypeId: operation.textNoteTypeId,
        width: operation.width,
        rotation: operation.rotation,
      };
    case "load_family":
      return {
        familyPath: operation.familyPath,
        fileSha256: operation.expectedSha256 ?? "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        familyId: "9800",
        familyName: operation.familyPath.split(/[\\/]/).pop()?.replace(/\.rfa$/i, "") ?? "Loaded Family",
        symbolCount: 1,
        symbols: [
          {
            id: "9801",
            class: "FamilySymbol",
            name: "Default",
            familyName: operation.familyPath.split(/[\\/]/).pop()?.replace(/\.rfa$/i, "") ?? "Loaded Family",
          },
        ],
        allowedCategories: operation.allowedCategories,
      };
    case "tag_room":
      return {
        id: "1304",
        uniqueId: "roomtag-1304",
        roomId: operation.roomId,
        viewId: operation.viewId,
        tagTypeId: operation.tagTypeId,
        location: operation.location,
        hasLeader: operation.hasLeader ?? false,
        orientation: operation.orientation ?? "Horizontal",
      };
    case "tag_element":
      return {
        id: "1305",
        uniqueId: "elementtag-1305",
        elementId: operation.elementId,
        viewId: operation.viewId,
        tagTypeId: operation.tagTypeId,
        position: operation.position,
        hasLeader: operation.hasLeader ?? false,
        orientation: operation.orientation ?? "Horizontal",
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
        allowDependentDeletes: operation.allowDependentDeletes,
        expectedDeletedElementIds: operation.expectedDeletedElementIds,
        expectedDeletedCount: operation.expectedDeletedCount,
        dependentDeleteLimit: operation.dependentDeleteLimit,
        deletedCount: operation.expectedDeletedCount ?? operation.expectedDeletedElementIds?.length ?? 1,
        deletedElementIds: operation.expectedDeletedElementIds ?? [operation.elementId],
        dependentDeletedCount: Math.max(0, (operation.expectedDeletedCount ?? operation.expectedDeletedElementIds?.length ?? 1) - 1),
        dependentDeletedElementIds: operation.expectedDeletedElementIds?.filter((id) => id !== operation.elementId),
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
    operation.type === "place_family_instance" ||
    operation.type === "create_sheet" ||
    operation.type === "place_view_on_sheet" ||
    operation.type === "create_text_note" ||
    operation.type === "load_family" ||
    operation.type === "tag_room" ||
    operation.type === "tag_element" ||
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

function fail<T>(request: BridgeRequest, code: string, message: string): BridgeResponse<T> {
  return {
    ok: false,
    requestId: request.requestId,
    error: {
      code,
      message,
      recoverable: true,
    },
    warnings: [],
    metrics: {
      elapsedMs: 1,
    },
  };
}
