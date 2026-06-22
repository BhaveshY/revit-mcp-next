import type {
  BridgeRequest,
  BridgeResponse,
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
  dispose(): void;
}

export interface BridgeCallOptions {
  signal?: AbortSignal;
}

