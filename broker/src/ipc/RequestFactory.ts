import type { BridgeRequest } from "@revit-mcp-next/contracts";
import { BRIDGE_PROTOCOL_VERSION } from "@revit-mcp-next/contracts";

export function makeRequest<TPayload>(
  sessionId: string,
  operation: string,
  operationKind: BridgeRequest<TPayload>["operationKind"],
  payload: TPayload,
  timeoutMs: number
): BridgeRequest<TPayload> {
  return {
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
    sessionId,
    operation,
    operationKind,
    timeoutMs,
    payload,
  };
}
