export const PROTOCOL_VERSION = "2026-06-22" as const;

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
  name: string;
  type: string;
  isGraphical: boolean;
  scale?: number;
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
  categories?: string[];
  classes?: string[];
  viewId?: ElementId;
  selectionOnly?: boolean;
  levelIds?: ElementId[];
  worksetIds?: ElementId[];
  designOptionIds?: ElementId[];
  parameterEquals?: Record<string, string | number | boolean>;
}

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
  units: Record<string, string>;
  scope: string;
  source: string;
}

export interface ToolSummary<TStructured> {
  text: string;
  structured: TStructured;
}

