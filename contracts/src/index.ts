export const PROTOCOL_VERSION = "2026-06-23" as const;

export type ProtocolVersion = typeof PROTOCOL_VERSION;
export type ElementId = string;
export type UniqueId = string;

export type UnitSystem = "metric" | "imperial" | "revit-internal";
export type OperationKind = "read" | "preview" | "write" | "destructive" | "debug";

export interface UnitValue {
  value: number;
  unit: string;
  system: UnitSystem;
}

export interface Point3 {
  x: UnitValue;
  y: UnitValue;
  z: UnitValue;
}

export interface Point2 {
  x: UnitValue;
  y: UnitValue;
}

/**
 * Compact element placement snapshot. `geometrySummary` returns this when Revit exposes
 * a point, curve, or bounding fallback; read QueryResult.units.location for the normalized unit.
 */
export interface ElementLocationSummary {
  point?: Point3;
  rotation?: number;
  start?: Point3;
  end?: Point3;
  length?: UnitValue;
  min?: Point3;
  max?: Point3;
  available?: boolean;
}

/**
 * Compact model-space bounding extents. `geometrySummary` reports the normalized unit
 * through QueryResult.units.bounds, currently "mm" for the Revit add-in.
 */
export interface ElementBoundsSummary {
  min?: Point3;
  max?: Point3;
  available?: boolean;
}

export interface AngleValue {
  value: number;
  unit: "degrees" | "radians";
}

export interface BridgeWarning {
  code: string;
  message: string;
  details?: Record<string, unknown>;
}

export interface BridgeError {
  code: string;
  message: string;
  recoverable: boolean;
  details?: Record<string, unknown>;
  suggestedNextAction?: string;
}

export interface BridgeMetrics {
  elapsedMs: number;
  collectorElapsedMs?: number;
  cacheHit?: boolean;
  returnedCount?: number;
  totalCount?: number;
}

export interface BridgeRequest<TPayload = unknown> {
  protocolVersion: ProtocolVersion;
  requestId: string;
  sessionId: string;
  authToken?: string;
  operation: string;
  operationKind: OperationKind;
  timeoutMs: number;
  documentFingerprint?: string;
  expectedGeneration?: number;
  payload: TPayload;
}

export interface BridgeSuccess<TData = unknown> {
  ok: true;
  requestId: string;
  data: TData;
  warnings: BridgeWarning[];
  metrics: BridgeMetrics;
  generation?: number;
}

export interface BridgeFailure {
  ok: false;
  requestId?: string;
  error: BridgeError;
  warnings: BridgeWarning[];
  metrics?: BridgeMetrics;
}

export type BridgeResponse<TData = unknown> = BridgeSuccess<TData> | BridgeFailure;

export interface RevitViewSummary {
  id: ElementId;
  uniqueId?: UniqueId;
  name: string;
  type: string;
  isGraphical: boolean;
  isTemplate?: boolean;
  canBePrinted?: boolean;
  scale?: number;
  detailLevel?: string;
  discipline?: string;
}

export interface RevitDocumentSummary {
  documentId: string;
  title: string;
  path?: string;
  fingerprint: string;
  isActive: boolean;
  isWorkshared?: boolean;
  isModified?: boolean;
  activeView?: RevitViewSummary;
  generation: number;
}

export interface RevitStatus {
  connected: boolean;
  brokerVersion: string;
  addinVersion?: string;
  addinAssembly?: {
    assemblyPath?: string;
    assemblySha256?: string;
    fileVersion?: string;
    productVersion?: string;
    assemblyIdentityError?: string;
  };
  protocolVersion: ProtocolVersion;
  revit?: {
    version: string;
    build?: string;
    processId?: number;
  };
  activeDocument?: RevitDocumentSummary;
  selection?: {
    count: number;
  };
  capabilities: string[];
  warnings: BridgeWarning[];
}

export interface LevelSummary {
  id: ElementId;
  uniqueId?: UniqueId;
  name: string;
  elevation: UnitValue;
  isBuildingStory?: boolean;
}

export interface QueryFilter {
  elementIds?: ElementId[];
  uniqueIds?: UniqueId[];
  categories?: string[];
  classes?: string[];
  viewId?: ElementId;
  selectionOnly?: boolean;
  levelIds?: ElementId[];
  worksetIds?: ElementId[];
  designOptionIds?: ElementId[];
  parameterEquals?: Record<string, string | number | boolean>;
}

/**
 * Query projection preset. Use `geometrySummary` for compact element location/bounds
 * without parameter dumps; units are advertised through QueryResult.units.location/bounds.
 */
export type QueryPreset = "idOnly" | "summary" | "schedule" | "geometrySummary";

export interface QueryRequest {
  filter: QueryFilter;
  fields?: string[];
  preset?: QueryPreset;
  limit?: number;
  cursor?: string;
  includeTotalCount?: boolean;
}

export interface QueryItem {
  id: ElementId;
  uniqueId?: UniqueId;
  category?: string;
  class?: string;
  name?: string;
  typeId?: ElementId;
  levelId?: ElementId;
  location?: ElementLocationSummary;
  bounds?: ElementBoundsSummary;
  fields?: Record<string, unknown>;
}

export interface QueryResult {
  items: QueryItem[];
  totalCount?: number;
  returnedCount: number;
  limit: number;
  cursor?: string;
  truncated: boolean;
  fields: string[];
  /** Unit labels for normalized result fields, including location/bounds for geometrySummary. */
  units: Record<string, string>;
  scope: string;
  source: string;
}

export type ParameterSource = "instance" | "type";

export interface ParameterSummary {
  name: string;
  storageType: "Double" | "Integer" | "String" | "ElementId" | "None" | string;
  source: ParameterSource;
  isReadOnly: boolean;
  hasValue?: boolean;
  value?: string | number | boolean | null;
  valueString?: string;
  elementIdValue?: ElementId;
  definitionId?: string;
  isShared?: boolean;
  guid?: string;
}

export interface ParameterTargetSummary {
  id: ElementId;
  uniqueId?: UniqueId;
  category?: string;
  class?: string;
  name?: string;
  typeId?: ElementId;
  typeName?: string;
  parameters: ParameterSummary[];
  parameterCount: number;
  truncated: boolean;
}

export type ParameterDescribePreset = "writableEdit" | "namesOnly" | "full";

export interface ParameterDescribeRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  filter: QueryFilter;
  preset?: ParameterDescribePreset;
  includeTypeParameters?: boolean;
  includeReadOnly?: boolean;
  includeValues?: boolean;
  nameContains?: string;
  limit?: number;
  cursor?: string;
  parameterLimit?: number;
  includeTotalCount?: boolean;
}

export interface ParameterDescribeResult {
  document: DocumentReference;
  items: ParameterTargetSummary[];
  returnedCount: number;
  totalCount?: number;
  limit: number;
  cursor?: string;
  truncated: boolean;
  parameterLimit: number;
  preset?: ParameterDescribePreset;
  scope: string;
  source: string;
}

export interface DocumentReference {
  fingerprint: string;
  title: string;
  path?: string;
  generation: number;
}

export interface CurrentViewRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  includeCropBox?: boolean;
}

export interface CurrentViewResult {
  document: DocumentReference;
  view: RevitViewSummary & {
    viewTemplateId?: ElementId;
    viewTemplateName?: string;
    associatedLevelId?: ElementId;
    associatedLevelName?: string;
    cropBoxActive?: boolean;
    cropBoxVisible?: boolean;
    cropBox?: {
      min: Point3;
      max: Point3;
    };
  };
  source: string;
}

export type ViewPreset = "idOnly" | "summary" | "sheetPlacement";

export interface ViewsFilter {
  viewIds?: ElementId[];
  uniqueIds?: UniqueId[];
  viewTypes?: string[];
  nameContains?: string;
  isTemplate?: boolean;
  isGraphical?: boolean;
  canBePrinted?: boolean;
}

export interface ViewsRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  filter?: ViewsFilter;
  fields?: string[];
  preset?: ViewPreset;
  limit?: number;
  cursor?: string;
  includeTotalCount?: boolean;
  includeCropBox?: boolean;
}

export interface ViewsResult {
  document: DocumentReference;
  items: Array<
    RevitViewSummary & {
      viewTemplateId?: ElementId;
      viewTemplateName?: string;
      associatedLevelId?: ElementId;
      associatedLevelName?: string;
      cropBoxActive?: boolean;
      cropBoxVisible?: boolean;
      cropBox?: {
        min: Point3;
        max: Point3;
      };
    }
  >;
  returnedCount: number;
  totalCount?: number;
  limit: number;
  cursor?: string;
  truncated: boolean;
  fields: string[];
  scope: string;
  source: string;
}

export type SheetPreset = "idOnly" | "summary" | "placement";

export interface SheetsFilter {
  sheetIds?: ElementId[];
  uniqueIds?: UniqueId[];
  numbers?: string[];
  numberContains?: string;
  nameContains?: string;
  titleBlockIds?: ElementId[];
}

export interface SheetPlacedView {
  viewportId: ElementId;
  viewId: ElementId;
  viewName?: string;
  viewType?: string;
  center?: Point3;
}

export interface SheetSummary {
  id: ElementId;
  uniqueId?: UniqueId;
  sheetNumber?: string;
  name?: string;
  titleBlockIds?: ElementId[];
  placedViews?: SheetPlacedView[];
}

export interface SheetsRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  filter?: SheetsFilter;
  fields?: string[];
  preset?: SheetPreset;
  limit?: number;
  cursor?: string;
  includeTotalCount?: boolean;
  includePlacedViews?: boolean;
}

export interface SheetsResult {
  document: DocumentReference;
  items: SheetSummary[];
  returnedCount: number;
  totalCount?: number;
  limit: number;
  cursor?: string;
  truncated: boolean;
  fields: string[];
  scope: string;
  source: string;
}

export interface ScopedElementListRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  filter?: QueryFilter;
  fields?: string[];
  preset?: QueryPreset;
  includeHidden?: boolean;
  limit?: number;
  cursor?: string;
  includeTotalCount?: boolean;
}

export interface ScopedElementListResult extends QueryResult {
  document: DocumentReference;
  view?: RevitViewSummary;
  selection?: {
    count: number;
    available: boolean;
  };
}

export interface ModelStatisticsRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  includeCategoryBreakdown?: boolean;
  includeClassBreakdown?: boolean;
  includeLevelBreakdown?: boolean;
  bucketLimit?: number;
  maxElementsScanned?: number;
}

export interface ModelStatisticsBucket {
  key: string;
  name?: string;
  builtInCategory?: string;
  count: number;
}

export interface ModelStatisticsResult {
  document: DocumentReference;
  totals: {
    elements: number;
    modelElements: number;
    elementTypes: number;
    families: number;
    views: number;
    sheets: number;
    levels: number;
    materials: number;
  };
  scannedElements: number;
  bucketLimit: number;
  truncated: boolean;
  byCategory?: ModelStatisticsBucket[];
  byClass?: ModelStatisticsBucket[];
  byLevel?: ModelStatisticsBucket[];
  source: string;
}

export interface MaterialQuantitiesRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  filter?: QueryFilter;
  materialNameContains?: string;
  includePaint?: boolean;
  maxElementsScanned?: number;
  limit?: number;
  cursor?: string;
  includeTotalCount?: boolean;
}

export interface MaterialQuantityItem {
  materialId: ElementId;
  materialName: string;
  materialClass?: string;
  elementCount: number;
  area: UnitValue;
  volume: UnitValue;
  source: "regular" | "paint" | "mixed";
  categories?: Array<{ name: string; count: number }>;
}

export interface MaterialQuantitiesResult {
  document: DocumentReference;
  scope: string;
  items: MaterialQuantityItem[];
  elementsScanned: number;
  elementsWithMaterials: number;
  returnedCount: number;
  totalCount?: number;
  limit: number;
  cursor?: string;
  truncated: boolean;
  units: {
    area: "m2";
    volume: "m3";
  };
  source: string;
}

export type RoomPreset = "idOnly" | "summary" | "schedule";

export interface RoomFilter {
  elementIds?: ElementId[];
  uniqueIds?: UniqueId[];
  levelIds?: ElementId[];
  phaseIds?: ElementId[];
  numbers?: string[];
  numberContains?: string;
  nameContains?: string;
  departmentContains?: string;
}

export interface RoomsRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  filter?: RoomFilter;
  fields?: string[];
  preset?: RoomPreset;
  limit?: number;
  cursor?: string;
  includeTotalCount?: boolean;
  includeUnplaced?: boolean;
}

export interface RoomSummary {
  id: ElementId;
  uniqueId?: UniqueId;
  number?: string;
  name?: string;
  levelId?: ElementId;
  levelName?: string;
  phaseId?: ElementId;
  phaseName?: string;
  area?: UnitValue;
  volume?: UnitValue;
  perimeter?: UnitValue;
  location?: Point3;
  isPlaced?: boolean;
  isEnclosed?: boolean;
  department?: string;
  fields?: Record<string, unknown>;
}

export interface RoomsResult {
  document: DocumentReference;
  items: RoomSummary[];
  returnedCount: number;
  totalCount?: number;
  limit: number;
  cursor?: string;
  truncated: boolean;
  fields: string[];
  units: {
    area: "m2";
    volume: "m3";
    location: "mm";
  };
  scope: string;
  source: string;
}

export type CatalogKind =
  | "elementTypes"
  | "familySymbols"
  | "titleBlocks"
  | "viewFamilyTypes"
  | "textNoteTypes"
  | "dimensionTypes"
  | "tagTypes";
export type CatalogPreset = "idOnly" | "compact" | "typeChange" | "placement" | "sheet" | "annotation";

export type CatalogScalar = string | number | boolean;

export interface CatalogFilter {
  forElementId?: ElementId;
  categories?: string[];
  classes?: string[];
  familyName?: string;
  familyNameContains?: string;
  nameContains?: string;
  viewFamily?: string[];
  parameterEquals?: Record<string, CatalogScalar>;
}

export interface CatalogRequest {
  kind: CatalogKind;
  documentFingerprint?: string;
  expectedGeneration?: number;
  filter?: CatalogFilter;
  preset?: CatalogPreset;
  fields?: string[];
  limit?: number;
  cursor?: string;
  includeTotalCount?: boolean;
}

export interface CatalogTarget {
  elementId: ElementId;
  uniqueId?: UniqueId;
  category?: string;
  class: string;
  name?: string;
  currentTypeId?: ElementId;
  currentTypeName?: string;
  pinned?: boolean;
  canChangeType: boolean;
  validTypeCount?: number;
}

export interface CatalogItem {
  id: ElementId;
  uniqueId?: UniqueId;
  class: string;
  category?: string;
  builtInCategory?: string;
  name: string;
  familyName?: string;
  familyId?: ElementId;
  isCurrentType?: boolean;
  validForTarget?: boolean;
  isActive?: boolean;
  placementType?: string;
  viewFamily?: string;
  fields?: Record<string, unknown>;
}

export interface CatalogResult {
  kind: CatalogKind;
  target?: CatalogTarget;
  items: CatalogItem[];
  totalCount?: number;
  returnedCount: number;
  limit: number;
  cursor?: string;
  truncated: boolean;
  fields: string[];
  scope: string;
  source: string;
  units: Record<string, string>;
}

export type ModelReadinessScenarioName =
  | "levels"
  | "wallCreation"
  | "floorCreation"
  | "roomCreation"
  | "roomReadback"
  | "typeChange"
  | "familyPlacement"
  | "selection"
  | "annotations";

export interface ModelReadinessRequest {
  documentFingerprint?: string;
  expectedGeneration?: number;
  scenarios?: ModelReadinessScenarioName[];
  includeHints?: boolean;
}

export interface ModelReadinessScenario {
  name: ModelReadinessScenarioName | string;
  ready: boolean;
  missing: string[];
  nextAction?: string;
  hints?: Record<string, unknown>;
}

export interface ModelReadinessResult {
  document: DocumentReference;
  activeView?: RevitDocumentSummary["activeView"];
  scenarios: ModelReadinessScenario[];
  readyCount: number;
  totalCount: number;
  source: string;
}

export type ChangeRiskLevel = "low" | "medium" | "high";
export type ChangeOperationType =
  | "set_parameter"
  | "create_level"
  | "create_wall"
  | "place_family_instance"
  | "create_sheet"
  | "place_view_on_sheet"
  | "create_text_note"
  | "tag_room"
  | "tag_element"
  | "move_element"
  | "rotate_element"
  | "copy_element"
  | "change_element_type"
  | "set_element_pinned"
  | "create_grid"
  | "create_floor"
  | "create_room"
  | "delete_element";
export type ChangeOperationStatus = "ready" | "warning" | "blocked" | "applied";

export type ChangeScalar = string | number | boolean;
export type RoomTagOrientation = "Horizontal" | "Vertical" | "Model";
export type ElementTagOrientation = "Horizontal" | "Vertical" | "AnyModelDirection";

export interface ChangeOperationBase {
  id?: string;
  type: ChangeOperationType;
}

export interface SetParameterChangeOperation extends ChangeOperationBase {
  type: "set_parameter";
  elementId: ElementId;
  expectedUniqueId?: UniqueId;
  parameterName: string;
  value: ChangeScalar;
}

export interface CreateLevelChangeOperation extends ChangeOperationBase {
  type: "create_level";
  name: string;
  elevation: UnitValue;
}

export interface CreateWallChangeOperation extends ChangeOperationBase {
  type: "create_wall";
  levelId: ElementId;
  start: Point3;
  end: Point3;
  wallTypeId?: ElementId;
  height?: UnitValue;
  structural?: boolean;
  flip?: boolean;
}

export interface PlaceFamilyInstanceOperation extends ChangeOperationBase {
  type: "place_family_instance";
  familySymbolId: ElementId;
  hostElementId?: ElementId;
  expectedHostUniqueId?: UniqueId;
  levelId?: ElementId;
  location: Point3;
  rotation?: AngleValue;
  flipFacing?: boolean;
  flipHand?: boolean;
  allowPinnedHost?: boolean;
}

export interface CreateSheetOperation extends ChangeOperationBase {
  type: "create_sheet";
  sheetNumber: string;
  name?: string;
  titleBlockTypeId?: ElementId;
}

export interface PlaceViewOnSheetOperation extends ChangeOperationBase {
  type: "place_view_on_sheet";
  sheetId: ElementId;
  viewId: ElementId;
  center: Point2;
}

export interface CreateTextNoteOperation extends ChangeOperationBase {
  type: "create_text_note";
  viewId: ElementId;
  text: string;
  position: Point3;
  textNoteTypeId?: ElementId;
  width?: UnitValue;
  rotation?: AngleValue;
}

export interface TagRoomOperation extends ChangeOperationBase {
  type: "tag_room";
  roomId: ElementId;
  expectedUniqueId?: UniqueId;
  viewId: ElementId;
  location: Point2;
  tagTypeId?: ElementId;
  hasLeader?: boolean;
  orientation?: RoomTagOrientation;
}

export interface TagElementOperation extends ChangeOperationBase {
  type: "tag_element";
  elementId: ElementId;
  expectedUniqueId?: UniqueId;
  viewId: ElementId;
  tagTypeId: ElementId;
  position: Point3;
  hasLeader?: boolean;
  orientation?: ElementTagOrientation;
}

export interface MoveElementChangeOperation extends ChangeOperationBase {
  type: "move_element";
  elementId: ElementId;
  expectedUniqueId?: UniqueId;
  translation: Point3;
}

export interface RotateElementChangeOperation extends ChangeOperationBase {
  type: "rotate_element";
  elementId: ElementId;
  expectedUniqueId?: UniqueId;
  axisStart: Point3;
  axisEnd: Point3;
  angle: AngleValue;
}

export interface CopyElementChangeOperation extends ChangeOperationBase {
  type: "copy_element";
  elementId: ElementId;
  expectedUniqueId?: UniqueId;
  translation: Point3;
}

export interface ChangeElementTypeOperation extends ChangeOperationBase {
  type: "change_element_type";
  elementId: ElementId;
  expectedUniqueId?: UniqueId;
  typeId: ElementId;
}

export interface SetElementPinnedOperation extends ChangeOperationBase {
  type: "set_element_pinned";
  elementId: ElementId;
  expectedUniqueId?: UniqueId;
  pinned: boolean;
  expectedPinned?: boolean;
}

export interface CreateGridOperation extends ChangeOperationBase {
  type: "create_grid";
  name?: string;
  start: Point3;
  end: Point3;
}

export interface CreateFloorOperation extends ChangeOperationBase {
  type: "create_floor";
  levelId: ElementId;
  outline: Point3[];
  floorTypeId?: ElementId;
  structural?: boolean;
}

export interface CreateRoomOperation extends ChangeOperationBase {
  type: "create_room";
  levelId: ElementId;
  location: Point2;
  name?: string;
  number?: string;
  department?: string;
  allowDuplicateNumber?: boolean;
}

export interface DeleteElementOperation extends ChangeOperationBase {
  type: "delete_element";
  elementId: ElementId;
  expectedUniqueId?: UniqueId;
  expectedPinned?: boolean;
  allowPinned?: boolean;
  allowDependentDeletes?: boolean;
  expectedDeletedElementIds?: ElementId[];
  expectedDeletedCount?: number;
  dependentDeleteLimit?: number;
}

export type ChangeOperation =
  | SetParameterChangeOperation
  | CreateLevelChangeOperation
  | CreateWallChangeOperation
  | PlaceFamilyInstanceOperation
  | CreateSheetOperation
  | PlaceViewOnSheetOperation
  | CreateTextNoteOperation
  | TagRoomOperation
  | TagElementOperation
  | MoveElementChangeOperation
  | RotateElementChangeOperation
  | CopyElementChangeOperation
  | ChangeElementTypeOperation
  | SetElementPinnedOperation
  | CreateGridOperation
  | CreateFloorOperation
  | CreateRoomOperation
  | DeleteElementOperation;

export interface ChangeSetSafetyFields {
  documentFingerprint?: string;
  expectedGeneration?: number;
  baseGeneration?: number;
  changeSetHash?: string;
  expiresAt?: string;
}

export interface ChangeSetRequest extends ChangeSetSafetyFields {
  transactionName: string;
  operations: ChangeOperation[];
}

export interface ChangePreviewItem {
  operationIndex: number;
  operationId?: string;
  type: ChangeOperationType | string;
  status: ChangeOperationStatus;
  target?: Record<string, unknown>;
  before?: Record<string, unknown>;
  after?: Record<string, unknown>;
  message?: string;
}

export interface ChangePreviewResult {
  previewId: string;
  documentFingerprint: string;
  changeSetHash: string;
  baseGeneration: number;
  expiresAt: string;
  transactionName: string;
  operationCount: number;
  ready: boolean;
  requiresConfirmation: boolean;
  riskLevel: ChangeRiskLevel;
  changes: ChangePreviewItem[];
}

export interface ChangeApplyRequest extends ChangeSetRequest {
  previewId: string;
  confirm: boolean;
}

export interface ChangeApplyResult {
  previewId: string;
  documentFingerprint: string;
  changeSetHash: string;
  baseGeneration: number;
  transactionName: string;
  applied: boolean;
  changedCount: number;
  changes: ChangePreviewItem[];
}

export interface CancelRequest {
  requestId?: string;
  reason?: string;
}

export interface CancelResult {
  cancelled: boolean;
  requestId?: string;
  message: string;
}

export interface ToolSummary<TStructured> {
  text: string;
  structured: TStructured;
}
