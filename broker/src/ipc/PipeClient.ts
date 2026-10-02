// Named-pipe client for wire protocol v3 (SPEC §4.6.1, D2 §11.4): one request per connection, 4-byte big-endian
// length + UTF-8 JSON, 4 MiB cap in bytes, own 3 s connect timer (libuv's busy-pipe wait cannot be aborted).
// An exchange never times out by itself: the caller races it against its Deadline and then either abandons it
// (destroy the socket) or detaches it (keep reading in the background for READ_STILL_RUNNING / late results).

import net from "node:net";
import { FRAME_HEADER_BYTES, MAX_FRAME_BYTES, pipePath, type BridgeRequest, type BridgeResponse } from "@revit-mcp-next/contracts/protocol";
import { classifyConnectErrno, TransportError } from "./errors.js";

export const CONNECT_TIMEOUT_MS = 3_000;

export interface ExchangeOptions {
  connectTimeoutMs?: number;
  maxFrameBytes?: number;
}

export interface PipeExchange {
  readonly requestId: string;
  /** Settles with the response, or rejects with a TransportError. */
  readonly response: Promise<BridgeResponse>;
  /** The request frame was fully written. */
  readonly written: boolean;
  /** The socket connected (the pipe exists and had a free instance). */
  readonly connected: boolean;
  /** Stop waiting and destroy the socket. Idempotent. */
  abandon(reason?: string): void;
  /** Keep reading after the caller's deadline; gives up after `maxMs`. */
  detach(maxMs: number): Promise<BridgeResponse>;
}

/** Serialize a request; throws TransportError(REQUEST_TOO_LARGE) above the frame cap. */
export function encodeFrame(request: BridgeRequest, maxFrameBytes = MAX_FRAME_BYTES): Buffer {
  const body = Buffer.from(JSON.stringify(request), "utf8");
  if (body.byteLength > maxFrameBytes) {
    throw new TransportError("REQUEST_TOO_LARGE", `request frame is ${body.byteLength} bytes, above the ${maxFrameBytes} byte limit`, "write", false);
  }
  const header = Buffer.allocUnsafe(FRAME_HEADER_BYTES);
  header.writeUInt32BE(body.byteLength, 0);
  return Buffer.concat([header, body]);
}

export function openExchange(pipeName: string, request: BridgeRequest, options: ExchangeOptions = {}): PipeExchange {
  const connectTimeoutMs = options.connectTimeoutMs ?? CONNECT_TIMEOUT_MS;
  const maxFrameBytes = options.maxFrameBytes ?? MAX_FRAME_BYTES;
  const frame = encodeFrame(request, maxFrameBytes);

  let written = false;
  let connected = false;
  let settled = false;
  let resolveFn!: (response: BridgeResponse) => void;
  let rejectFn!: (error: TransportError) => void;
  const response = new Promise<BridgeResponse>((resolve, reject) => {
    resolveFn = resolve;
    rejectFn = reject;
  });
  // Callers may race and abandon; never let a late rejection become unhandled.
  response.catch(() => undefined);

  const socket = net.createConnection(pipePath(pipeName));
  let connectTimer: NodeJS.Timeout | null = setTimeout(() => {
    connectTimer = null;
    if (!connected) fail(new TransportError("BUSY", `no free pipe instance within ${connectTimeoutMs} ms`, "connect", false, "CONNECT_TIMEOUT"));
  }, connectTimeoutMs);
  connectTimer.unref();
  let detachTimer: NodeJS.Timeout | null = null;

  const cleanup = () => {
    if (connectTimer) {
      clearTimeout(connectTimer);
      connectTimer = null;
    }
    if (detachTimer) {
      clearTimeout(detachTimer);
      detachTimer = null;
    }
  };
  const fail = (error: TransportError) => {
    if (settled) return;
    settled = true;
    cleanup();
    socket.destroy();
    rejectFn(error);
  };
  const succeed = (value: BridgeResponse) => {
    if (settled) return;
    settled = true;
    cleanup();
    socket.end();
    socket.destroy();
    resolveFn(value);
  };

  socket.once("connect", () => {
    connected = true;
    if (connectTimer) {
      clearTimeout(connectTimer);
      connectTimer = null;
    }
    socket.write(frame, (error) => {
      if (error) fail(new TransportError("DISCONNECTED", `pipe closed while writing the request: ${error.message}`, "write", false, (error as NodeJS.ErrnoException).code));
      else written = true;
    });
  });

  const header = Buffer.alloc(FRAME_HEADER_BYTES);
  let headerBytes = 0;
  let expected: number | null = null;
  const chunks: Buffer[] = [];
  let received = 0;

  socket.on("data", (chunk: Buffer) => {
    if (settled) return;
    let offset = 0;
    while (offset < chunk.byteLength && !settled) {
      if (expected === null) {
        const take = Math.min(FRAME_HEADER_BYTES - headerBytes, chunk.byteLength - offset);
        chunk.copy(header, headerBytes, offset, offset + take);
        headerBytes += take;
        offset += take;
        if (headerBytes < FRAME_HEADER_BYTES) return;
        expected = header.readUInt32BE(0);
        if (expected > maxFrameBytes) {
          fail(new TransportError("FRAME_TOO_LARGE", `response frame announced ${expected} bytes, above the ${maxFrameBytes} byte limit`, "read", true));
          return;
        }
      }
      const take = Math.min(expected - received, chunk.byteLength - offset);
      if (take > 0) {
        chunks.push(chunk.subarray(offset, offset + take));
        received += take;
        offset += take;
      }
      if (received === expected) {
        let parsed: unknown;
        try {
          parsed = JSON.parse(Buffer.concat(chunks, expected).toString("utf8"));
        } catch (error) {
          fail(new TransportError("BAD_RESPONSE", `response is not JSON: ${(error as Error).message}`, "read", true));
          return;
        }
        const problem = validateResponse(parsed, request.requestId);
        if (problem) fail(new TransportError("BAD_RESPONSE", problem, "read", true));
        else succeed(parsed as BridgeResponse);
        return;
      }
    }
  });

  socket.once("error", (error: NodeJS.ErrnoException) => {
    if (settled) return;
    if (!connected) fail(new TransportError(classifyConnectErrno(error.code), error.message, "connect", false, error.code));
    else fail(new TransportError("DISCONNECTED", `pipe failed: ${error.message}`, written ? "read" : "write", written, error.code));
  });
  socket.once("end", () => {
    if (!settled) fail(new TransportError("DISCONNECTED", "pipe ended before a complete response", written ? "read" : "write", written));
  });
  socket.once("close", () => {
    if (!settled) {
      if (!connected) fail(new TransportError("NOT_FOUND", "pipe closed before connecting", "connect", false));
      else fail(new TransportError("DISCONNECTED", "pipe closed before a complete response", written ? "read" : "write", written));
    }
  });

  return {
    requestId: request.requestId,
    response,
    get written() {
      return written;
    },
    get connected() {
      return connected;
    },
    abandon(reason = "abandoned by the caller") {
      fail(new TransportError("ABORTED", reason, written ? "read" : connected ? "write" : "connect", written));
    },
    detach(maxMs: number) {
      if (!settled && !detachTimer) {
        detachTimer = setTimeout(() => fail(new TransportError("TIMEOUT", `no response within ${maxMs} ms after the budget`, "read", written)), Math.max(1, maxMs));
        detachTimer.unref();
        socket.unref();
      }
      return response;
    },
  };
}

/** Minimal shape check of a v3 response. Returns a problem description or null. */
export function validateResponse(value: unknown, requestId: string): string | null {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return "response is not a JSON object";
  const r = value as Record<string, unknown>;
  if (typeof r.ok !== "boolean") return "response has no boolean ok";
  if (typeof r.requestId === "string" && r.requestId !== requestId && r.requestId !== "") return `response requestId ${r.requestId} does not match ${requestId}`;
  if (r.ok === false && typeof r.code !== "string") return "failed response has no code";
  return null;
}
