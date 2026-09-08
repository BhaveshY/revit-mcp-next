import type {
  BridgeHealthResult,
  BridgeRequest,
  BridgeResponse,
  CancelRequest,
  CancelResult,
  CatalogRequest,
  CatalogResult,
  ChangeApplyRequest,
  ChangeApplyResult,
  ChangePreviewResult,
  ChangeSetRequest,
  CreateProjectFromTemplateRequest,
  CreateProjectFromTemplateResult,
  CurrentViewRequest,
  CurrentViewResult,
  LevelSummary,
  MaterialQuantitiesRequest,
  MaterialQuantitiesResult,
  ModelDeliveryExecuteRequest,
  ModelDeliveryExecuteResult,
  ModelDeliveryFixtureRequest,
  ModelDeliveryFixtureResult,
  ModelDeliveryInspectRequest,
  ModelDeliveryInspectResult,
  ModelDeliveryCancelRequest,
  ModelDeliveryCancelResult,
  ModelDeliveryPreviewRequest,
  ModelDeliveryPreviewResult,
  ModelDeliveryStatusRequest,
  ModelDeliveryStatusResult,
  ModelContextRequest,
  ModelContextResult,
  ModelReadinessRequest,
  ModelReadinessResult,
  ModelStatisticsRequest,
  ModelStatisticsResult,
  ParameterDescribeRequest,
  ParameterDescribeResult,
  QueryRequest,
  QueryResult,
  RevitDocumentSummary,
  RevitStatus,
  RequestResultRequest,
  RequestResultResult,
  RoomsRequest,
  RoomsResult,
  ScheduleFieldsRequest,
  ScheduleFieldsResult,
  SchedulesRequest,
  SchedulesResult,
  ScopedElementListRequest,
  ScopedElementListResult,
  SheetsRequest,
  SheetsResult,
  ViewsRequest,
  ViewsResult,
  WarningsRequest,
  WarningsResult,
} from "@revit-mcp-next/contracts";

export interface RevitBridgeClient {
  viewWorkflow(request: BridgeRequest<Record<string, unknown>>, options?: BridgeCallOptions): Promise<BridgeResponse<Record<string, unknown>>>;
  bridgeHealth(
    request: BridgeRequest<{ instanceId?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<BridgeHealthResult>>;
  getRequestResult<TData = unknown>(
    request: BridgeRequest<RequestResultRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RequestResultResult<TData>>>;
  status(
    request: BridgeRequest<{ instanceId?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitStatus>>;
  listDocuments(
    request: BridgeRequest<{ instanceId?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitDocumentSummary[]>>;
  createProjectFromTemplate(
    request: BridgeRequest<CreateProjectFromTemplateRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CreateProjectFromTemplateResult>>;
  createModelDeliveryFixture(
    request: BridgeRequest<ModelDeliveryFixtureRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryFixtureResult>>;
  previewModelDelivery(
    request: BridgeRequest<ModelDeliveryPreviewRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryPreviewResult>>;
  inspectModelDelivery(
    request: BridgeRequest<ModelDeliveryInspectRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryInspectResult>>;
  executeModelDelivery(
    request: BridgeRequest<ModelDeliveryExecuteRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryExecuteResult>>;
  getModelDeliveryStatus(
    request: BridgeRequest<ModelDeliveryStatusRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryStatusResult>>;
  cancelModelDelivery(
    request: BridgeRequest<ModelDeliveryCancelRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryCancelResult>>;
  getLevels(
    request: BridgeRequest<{ documentFingerprint?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<LevelSummary[]>>;
  getViews(
    request: BridgeRequest<ViewsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ViewsResult>>;
  getSheets(
    request: BridgeRequest<SheetsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<SheetsResult>>;
  getSchedules(
    request: BridgeRequest<SchedulesRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<SchedulesResult>>;
  getScheduleFields(
    request: BridgeRequest<ScheduleFieldsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ScheduleFieldsResult>>;
  getCurrentView(
    request: BridgeRequest<CurrentViewRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CurrentViewResult>>;
  getCurrentViewElements(
    request: BridgeRequest<ScopedElementListRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ScopedElementListResult>>;
  getSelection(
    request: BridgeRequest<ScopedElementListRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ScopedElementListResult>>;
  analyzeModel(
    request: BridgeRequest<ModelStatisticsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelStatisticsResult>>;
  getModelReadiness(
    request: BridgeRequest<ModelReadinessRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelReadinessResult>>;
  getModelContext(
    request: BridgeRequest<ModelContextRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelContextResult>>;
  getMaterialQuantities(
    request: BridgeRequest<MaterialQuantitiesRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<MaterialQuantitiesResult>>;
  getWarnings(
    request: BridgeRequest<WarningsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<WarningsResult>>;
  getRooms(
    request: BridgeRequest<RoomsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RoomsResult>>;
  query(
    request: BridgeRequest<QueryRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<QueryResult>>;
  describeParameters(
    request: BridgeRequest<ParameterDescribeRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ParameterDescribeResult>>;
  catalog(
    request: BridgeRequest<CatalogRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CatalogResult>>;
  previewChange(
    request: BridgeRequest<ChangeSetRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ChangePreviewResult>>;
  applyChange(
    request: BridgeRequest<ChangeApplyRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ChangeApplyResult>>;
  cancel(
    request: BridgeRequest<CancelRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CancelResult>>;
  dispose(): void;
}

export interface BridgeCallOptions {
  signal?: AbortSignal;
}
