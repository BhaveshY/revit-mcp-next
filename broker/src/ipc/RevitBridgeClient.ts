import type {
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
  LevelSummary,
  QueryRequest,
  QueryResult,
  RevitDocumentSummary,
  RevitStatus,
} from "@revit-mcp-next/contracts";

export interface RevitBridgeClient {
  status(
    request: BridgeRequest<Record<string, never>>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitStatus>>;
  listDocuments(
    request: BridgeRequest<Record<string, never>>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitDocumentSummary[]>>;
  getLevels(
    request: BridgeRequest<{ documentFingerprint?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<LevelSummary[]>>;
  query(
    request: BridgeRequest<QueryRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<QueryResult>>;
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
