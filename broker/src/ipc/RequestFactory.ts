import type { BridgeRequest } from "@revit-mcp-next/contracts";
import { BRIDGE_PROTOCOL_VERSION } from "@revit-mcp-next/contracts";

export function makeRequest<TPayload>(
  sessionId: string,
  operation: string,
  operationKind: BridgeRequest<TPayload>["operationKind"],
  payload: TPayload,
  timeoutMs: number
): BridgeRequest<TPayload> {
  const request: BridgeRequest<TPayload> = {
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
    sessionId,
    operation,
    operationKind,
    timeoutMs,
    payload,
  };
  if (typeof payload === "object" && payload !== null && !Array.isArray(payload)) {
    const record = payload as Record<string, unknown>;
    if (typeof record.instanceId === "string") request.instanceId = record.instanceId;
    if (typeof record.documentFingerprint === "string") request.documentFingerprint = record.documentFingerprint;
    if (typeof record.expectedGeneration === "number") request.expectedGeneration = record.expectedGeneration;
  }
  return request;
}
