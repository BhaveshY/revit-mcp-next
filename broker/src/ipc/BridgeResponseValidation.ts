import type {
  BridgeError,
  BridgeMetrics,
  BridgeResponse,
  BridgeWarning,
  RequestResultResult,
} from "@revit-mcp-next/contracts";

export type BridgeResponseValidation<T> =
  | { valid: true; response: BridgeResponse<T> }
  | {
      valid: false;
      code: "BRIDGE_PROTOCOL_ERROR" | "BRIDGE_REQUEST_ID_MISMATCH";
      message: string;
    };

export type RequestResultValidation<TData = unknown> =
  | { valid: true; result: RequestResultResult<TData> }
  | {
      valid: false;
      code: "BRIDGE_PROTOCOL_ERROR" | "BRIDGE_REQUEST_ID_MISMATCH";
      message: string;
    };

const requestResultStates = new Set(["accepted", "running", "committed", "rolledBack", "failed"]);

export function validateBridgeResponse<T>(
  value: unknown,
  expectedRequestId: string
): BridgeResponseValidation<T> {
  if (!isRecord(value)) return protocolFailure("Bridge response must be a JSON object.");
  if (typeof value.requestId !== "string") {
    return protocolFailure("Bridge response requestId must be a string.");
  }
  if (value.requestId !== expectedRequestId) {
    return {
      valid: false,
      code: "BRIDGE_REQUEST_ID_MISMATCH",
      message: `Bridge response requestId ${value.requestId} does not match request ${expectedRequestId}.`,
    };
  }

  const warnings = parseWarnings(value.warnings);
  if (!warnings) return protocolFailure("Bridge response warnings must be an array of warning objects.");

  if (value.ok === true) {
    if (!("data" in value)) return protocolFailure("Successful bridge response must include data.");
    const metrics = parseMetrics(value.metrics);
    if (!metrics) return protocolFailure("Successful bridge response metrics are invalid.");
    if (value.generation !== undefined && !isNonNegativeInteger(value.generation)) {
      return protocolFailure("Bridge response generation must be a non-negative integer when present.");
    }
    return {
      valid: true,
      response: {
        ok: true,
        requestId: value.requestId,
        data: value.data as T,
        warnings,
        metrics,
        ...(value.generation === undefined ? {} : { generation: value.generation }),
      },
    };
  }

  if (value.ok === false) {
    const error = parseError(value.error);
    if (!error) return protocolFailure("Failed bridge response error is invalid.");
    const metrics = value.metrics === undefined ? undefined : parseMetrics(value.metrics);
    if (value.metrics !== undefined && !metrics) {
      return protocolFailure("Failed bridge response metrics are invalid.");
    }
    return {
      valid: true,
      response: {
        ok: false,
        requestId: value.requestId,
        error,
        warnings,
        ...(metrics ? { metrics } : {}),
      },
    };
  }

  return protocolFailure("Bridge response ok must be a boolean literal.");
}

export function validateRequestResult<TData = unknown>(
  value: unknown,
  expectedRequestId: string
): RequestResultValidation<TData> {
  if (!isRecord(value)) return requestResultProtocolFailure("Request-result data is not an object.");
  if (typeof value.found !== "boolean") {
    return requestResultProtocolFailure("Request-result data is missing found.");
  }
  if (typeof value.state !== "string" || !requestResultStates.has(value.state)) {
    return requestResultProtocolFailure("Request-result data has an invalid state.");
  }
  if (!value.found) {
    if (value.state !== "failed") {
      return requestResultProtocolFailure("A missing request-result entry must use state=failed.");
    }
    return { valid: true, result: { found: false, state: "failed" } };
  }
  if (typeof value.requestId !== "string") {
    return requestResultProtocolFailure("A found request-result entry must include requestId.");
  }
  if (value.requestId !== expectedRequestId) {
    return {
      valid: false,
      code: "BRIDGE_REQUEST_ID_MISMATCH",
      message: `Request-result requestId ${value.requestId} does not match original request ${expectedRequestId}.`,
    };
  }
  if (typeof value.operation !== "string" || value.operation.length === 0) {
    return requestResultProtocolFailure("A found request-result entry must include operation.");
  }
  if (typeof value.acceptedAtUtc !== "string" || value.acceptedAtUtc.length === 0) {
    return requestResultProtocolFailure("A found request-result entry must include acceptedAtUtc.");
  }
  const base = {
    found: true as const,
    requestId: value.requestId,
    operation: value.operation,
    acceptedAtUtc: value.acceptedAtUtc,
  };

  if (value.state === "accepted" || value.state === "running") {
    if (value.completedAtUtc !== undefined) {
      return requestResultProtocolFailure(`The ${value.state} request-result entry cannot include completedAtUtc.`);
    }
    if (value.response !== undefined) {
      return requestResultProtocolFailure(`The ${value.state} request-result entry cannot include response.`);
    }
    return { valid: true, result: { ...base, state: value.state } };
  }

  if (!("response" in value)) {
    return requestResultProtocolFailure(`The ${value.state} request-result entry has no response.`);
  }
  if (typeof value.completedAtUtc !== "string" || value.completedAtUtc.length === 0) {
    return requestResultProtocolFailure(`The ${value.state} request-result entry must include completedAtUtc.`);
  }
  const response = validateBridgeResponse<TData>(value.response, expectedRequestId);
  if (!response.valid) return response;
  if (value.state === "committed" || value.state === "rolledBack" || value.state === "failed") {
    if (value.state === "committed" && !response.response.ok) {
      return requestResultProtocolFailure("A committed request-result entry must contain a successful response.");
    }
    if (value.state !== "committed" && response.response.ok) {
      return requestResultProtocolFailure(`A ${value.state} request-result entry must contain a failed response.`);
    }
    return {
      valid: true,
      result: { ...base, state: value.state, completedAtUtc: value.completedAtUtc, response: response.response },
    };
  }
  return requestResultProtocolFailure("Request-result data has an unsupported final state.");
}

function parseWarnings(value: unknown): BridgeWarning[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const warnings: BridgeWarning[] = [];
  for (const item of value) {
    if (!isRecord(item) || typeof item.code !== "string" || typeof item.message !== "string") {
      return undefined;
    }
    if (item.details !== undefined && !isRecord(item.details)) return undefined;
    warnings.push({
      code: item.code,
      message: item.message,
      ...(item.details === undefined ? {} : { details: item.details }),
    });
  }
  return warnings;
}

function parseMetrics(value: unknown): BridgeMetrics | undefined {
  if (!isRecord(value) || !isNonNegativeNumber(value.elapsedMs)) return undefined;
  if (value.queueWaitMs !== undefined && !isNonNegativeNumber(value.queueWaitMs)) return undefined;
  if (value.revitExecutionMs !== undefined && !isNonNegativeNumber(value.revitExecutionMs)) return undefined;
  if (value.collectorElapsedMs !== undefined && !isNonNegativeNumber(value.collectorElapsedMs)) return undefined;
  if (value.cacheHit !== undefined && typeof value.cacheHit !== "boolean") return undefined;
  if (value.returnedCount !== undefined && !isNonNegativeInteger(value.returnedCount)) return undefined;
  if (value.totalCount !== undefined && !isNonNegativeInteger(value.totalCount)) return undefined;
  return {
    elapsedMs: value.elapsedMs,
    ...(value.queueWaitMs === undefined ? {} : { queueWaitMs: value.queueWaitMs }),
    ...(value.revitExecutionMs === undefined ? {} : { revitExecutionMs: value.revitExecutionMs }),
    ...(value.collectorElapsedMs === undefined ? {} : { collectorElapsedMs: value.collectorElapsedMs }),
    ...(value.cacheHit === undefined ? {} : { cacheHit: value.cacheHit }),
    ...(value.returnedCount === undefined ? {} : { returnedCount: value.returnedCount }),
    ...(value.totalCount === undefined ? {} : { totalCount: value.totalCount }),
  };
}

function parseError(value: unknown): BridgeError | undefined {
  if (
    !isRecord(value) ||
    typeof value.code !== "string" ||
    typeof value.message !== "string" ||
    typeof value.recoverable !== "boolean"
  ) {
    return undefined;
  }
  if (value.details !== undefined && !isRecord(value.details)) return undefined;
  if (value.suggestedNextAction !== undefined && typeof value.suggestedNextAction !== "string") {
    return undefined;
  }
  return {
    code: value.code,
    message: value.message,
    recoverable: value.recoverable,
    ...(value.details === undefined ? {} : { details: value.details }),
    ...(value.suggestedNextAction === undefined ? {} : { suggestedNextAction: value.suggestedNextAction }),
  };
}

function protocolFailure(message: string): BridgeResponseValidation<never> {
  return { valid: false, code: "BRIDGE_PROTOCOL_ERROR", message };
}

function requestResultProtocolFailure<TData = never>(message: string): RequestResultValidation<TData> {
  return { valid: false, code: "BRIDGE_PROTOCOL_ERROR", message };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isNonNegativeNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0;
}

function isNonNegativeInteger(value: unknown): value is number {
  return isNonNegativeNumber(value) && Number.isInteger(value);
}
