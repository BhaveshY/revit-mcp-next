import type { BridgeRequest } from "@revit-mcp-next/contracts";
import { PROTOCOL_VERSION } from "@revit-mcp-next/contracts";

export function makeRequest<TPayload>(
  sessionId: string,
  operation: string,
  operationKind: BridgeRequest<TPayload>["operationKind"],
  payload: TPayload,
  timeoutMs: number
): BridgeRequest<TPayload> {
  return {
    protocolVersion: PROTOCOL_VERSION,
    requestId: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
    sessionId,
    operation,
    operationKind,
    timeoutMs,
    payload,
  };
}
