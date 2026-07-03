import { createHash, createHmac, timingSafeEqual } from "node:crypto";
import type { BridgeFailure, BridgeResponse, ProtocolVersion } from "@revit-mcp-next/contracts";

const CURSOR_PREFIX = "rvc1_";

interface CursorContext {
  sessionId: string;
  protocolVersion: ProtocolVersion;
}

interface CursorEnvelope {
  v: 1;
  op: string;
  offset: string;
  binding: string;
  generation?: number;
  documentFingerprint?: string;
  mac: string;
}

type DecodedPageCursor =
  | {
      ok: true;
      cursor?: string;
      generation?: number;
      documentFingerprint?: string;
    }
  | {
      ok: false;
      response: BridgeFailure;
    };

type CursorData = {
  cursor?: unknown;
  document?: unknown;
};

export function decodePageCursor(
  cursor: string | undefined,
  context: CursorContext,
  operation: string,
  bindingPayload: Record<string, unknown>
): DecodedPageCursor {
  if (!cursor) return { ok: true };

  if (!cursor.startsWith(CURSOR_PREFIX)) {
    return cursorFailure("INVALID_CURSOR", "Cursor is not a Revit MCP Next cursor.", {
      expectedPrefix: CURSOR_PREFIX,
    });
  }

  let envelope: CursorEnvelope;
  try {
    envelope = JSON.parse(Buffer.from(cursor.slice(CURSOR_PREFIX.length), "base64url").toString("utf8")) as CursorEnvelope;
  } catch {
    return cursorFailure("INVALID_CURSOR", "Cursor could not be decoded.", {
      expectedPrefix: CURSOR_PREFIX,
    });
  }

  if (
    envelope.v !== 1 ||
    envelope.op !== operation ||
    !/^\d+$/.test(envelope.offset) ||
    typeof envelope.binding !== "string" ||
    typeof envelope.mac !== "string"
  ) {
    return cursorFailure("INVALID_CURSOR", "Cursor has an invalid shape for this tool.", {
      operation,
    });
  }

  const expectedBinding = cursorBinding(operation, bindingPayload);
  if (envelope.binding !== expectedBinding) {
    return cursorFailure(
      "CURSOR_SCOPE_MISMATCH",
      "Cursor was created for different filters, projection, limit, preset, document guard, or count settings.",
      { operation },
      "Retry with the same arguments used for the previous page, or start a new first-page request without cursor."
    );
  }

  const expectedMac = cursorMac(context, envelope);
  if (!safeEquals(envelope.mac, expectedMac)) {
    return cursorFailure("INVALID_CURSOR", "Cursor signature is invalid.", {
      operation,
    });
  }

  return {
    ok: true,
    cursor: envelope.offset,
    generation: envelope.generation,
    documentFingerprint: envelope.documentFingerprint,
  };
}

export function applyDecodedPageCursor<TPayload extends Record<string, unknown>>(
  payload: TPayload,
  decoded: Extract<DecodedPageCursor, { ok: true }>
): TPayload {
  const nextPayload: Record<string, unknown> = { ...payload };
  if (decoded.cursor) nextPayload.cursor = decoded.cursor;
  if (decoded.generation !== undefined && nextPayload.expectedGeneration === undefined) {
    nextPayload.expectedGeneration = decoded.generation;
  }
  if (decoded.documentFingerprint && nextPayload.documentFingerprint === undefined) {
    nextPayload.documentFingerprint = decoded.documentFingerprint;
  }
  return nextPayload as TPayload;
}

export function encodePageCursorResponse<TData>(
  response: BridgeResponse<TData>,
  context: CursorContext,
  operation: string,
  bindingPayload: Record<string, unknown>
): BridgeResponse<TData> {
  if (!response.ok || !isRecord(response.data)) return response;

  const cursor = typeof response.data.cursor === "string" && response.data.cursor.length > 0 ? response.data.cursor : undefined;
  if (!cursor || cursor.startsWith(CURSOR_PREFIX)) return response;

  const documentFingerprint = documentFingerprintFrom(response.data) ?? stringField(bindingPayload, "documentFingerprint");
  const generation = response.generation ?? documentGenerationFrom(response.data) ?? numberField(bindingPayload, "expectedGeneration");
  const opaqueCursor = encodePageCursor(cursor, context, operation, bindingPayload, {
    documentFingerprint,
    generation,
  });

  return {
    ...response,
    data: {
      ...response.data,
      cursor: opaqueCursor,
    } as TData,
  };
}

function encodePageCursor(
  offset: string,
  context: CursorContext,
  operation: string,
  bindingPayload: Record<string, unknown>,
  metadata: { generation?: number; documentFingerprint?: string }
): string {
  const envelope: CursorEnvelope = {
    v: 1,
    op: operation,
    offset,
    binding: cursorBinding(operation, bindingPayload),
    generation: metadata.generation,
    documentFingerprint: metadata.documentFingerprint,
    mac: "",
  };
  envelope.mac = cursorMac(context, envelope);
  return `${CURSOR_PREFIX}${Buffer.from(JSON.stringify(envelope), "utf8").toString("base64url")}`;
}

function cursorBinding(operation: string, payload: Record<string, unknown>): string {
  return createHash("sha256").update(operation).update("\0").update(stableStringify(payload)).digest("base64url");
}

function cursorMac(context: CursorContext, envelope: Omit<CursorEnvelope, "mac">): string {
  return createHmac("sha256", `${context.protocolVersion}:${context.sessionId}`)
    .update(String(envelope.v))
    .update("\0")
    .update(envelope.op)
    .update("\0")
    .update(envelope.offset)
    .update("\0")
    .update(envelope.binding)
    .update("\0")
    .update(envelope.generation === undefined ? "" : String(envelope.generation))
    .update("\0")
    .update(envelope.documentFingerprint ?? "")
    .digest("base64url");
}

function stableStringify(value: unknown): string {
  if (value === null) return "null";
  if (value === undefined) return "undefined";
  if (typeof value === "string") return JSON.stringify(value);
  if (typeof value === "number" || typeof value === "boolean") return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map((item) => stableStringify(item)).join(",")}]`;
  if (isRecord(value)) {
    return `{${Object.keys(value)
      .filter((key) => value[key] !== undefined)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${stableStringify(value[key])}`)
      .join(",")}}`;
  }
  return JSON.stringify(String(value));
}

function cursorFailure(
  code: string,
  message: string,
  details: Record<string, unknown>,
  suggestedNextAction = "Start a new first-page request without cursor."
): DecodedPageCursor {
  return {
    ok: false,
    response: {
      ok: false,
      error: {
        code,
        message,
        recoverable: true,
        details,
        suggestedNextAction,
      },
      warnings: [],
      metrics: { elapsedMs: 0 },
    },
  };
}

function safeEquals(left: string, right: string): boolean {
  const leftBuffer = Buffer.from(left);
  const rightBuffer = Buffer.from(right);
  return leftBuffer.length === rightBuffer.length && timingSafeEqual(leftBuffer, rightBuffer);
}

function isRecord(value: unknown): value is Record<string, unknown> & CursorData {
  return typeof value === "object" && value !== null;
}

function stringField(value: Record<string, unknown>, key: string): string | undefined {
  return typeof value[key] === "string" ? value[key] : undefined;
}

function numberField(value: Record<string, unknown>, key: string): number | undefined {
  return typeof value[key] === "number" ? value[key] : undefined;
}

function documentFingerprintFrom(data: Record<string, unknown>): string | undefined {
  return isRecord(data.document) ? stringField(data.document, "fingerprint") : undefined;
}

function documentGenerationFrom(data: Record<string, unknown>): number | undefined {
  return isRecord(data.document) ? numberField(data.document, "generation") : undefined;
}
