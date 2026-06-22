import net from "node:net";
import type {
  BridgeRequest,
  BridgeResponse,
  LevelSummary,
  QueryRequest,
  QueryResult,
  RevitDocumentSummary,
  RevitStatus,
} from "@revit-mcp-next/contracts";
import type { BridgeCallOptions, RevitBridgeClient } from "./RevitBridgeClient.js";

export interface NamedPipeBridgeClientOptions {
  pipeName: string;
  sessionId: string;
  defaultTimeoutMs: number;
}

export class NamedPipeBridgeClient implements RevitBridgeClient {
  private readonly pipePath: string;

  constructor(private readonly options: NamedPipeBridgeClientOptions) {
    this.pipePath = options.pipeName.startsWith("\\\\")
      ? options.pipeName
      : `\\\\.\\pipe\\${options.pipeName}`;
  }

  status(
    request: BridgeRequest<Record<string, never>>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitStatus>> {
    return this.send(request, options);
  }

  listDocuments(
    request: BridgeRequest<Record<string, never>>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitDocumentSummary[]>> {
    return this.send(request, options);
  }

  getLevels(
    request: BridgeRequest<{ documentFingerprint?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<LevelSummary[]>> {
    return this.send(request, options);
  }

  query(
    request: BridgeRequest<QueryRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<QueryResult>> {
    return this.send(request, options);
  }

  dispose(): void {
    // Connections are per request for now. Persistent multiplexing comes after the add-in queue is live.
  }

  private send<T>(request: BridgeRequest, options?: BridgeCallOptions): Promise<BridgeResponse<T>> {
    const timeoutMs = request.timeoutMs || this.options.defaultTimeoutMs;

    return new Promise((resolve) => {
      const socket = net.createConnection(this.pipePath);
      const chunks: Buffer[] = [];
      let expectedLength: number | null = null;
      let settled = false;

      const finish = (response: BridgeResponse<T>) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        options?.signal?.removeEventListener("abort", abortHandler);
        socket.destroy();
        resolve(response);
      };

      const abortHandler = () => {
        finish(errorResponse<T>(request, "REQUEST_CANCELLED", "The MCP client cancelled the request."));
      };

      if (options?.signal?.aborted) {
        finish(errorResponse<T>(request, "REQUEST_CANCELLED", "The MCP client cancelled the request."));
        return;
      }

      options?.signal?.addEventListener("abort", abortHandler, { once: true });

      const timer = setTimeout(() => {
        finish(errorResponse<T>(request, "BRIDGE_TIMEOUT", `Timed out after ${timeoutMs}ms connecting to Revit add-in.`));
      }, timeoutMs);

      socket.once("connect", () => {
        const body = Buffer.from(JSON.stringify(request), "utf8");
        const header = Buffer.allocUnsafe(4);
        header.writeUInt32BE(body.byteLength, 0);
        socket.write(Buffer.concat([header, body]));
      });

      socket.on("data", (chunk) => {
        chunks.push(chunk);
        const buffer = Buffer.concat(chunks);
        if (expectedLength === null && buffer.byteLength >= 4) {
          expectedLength = buffer.readUInt32BE(0);
        }
        if (expectedLength !== null && buffer.byteLength >= expectedLength + 4) {
          const payload = buffer.subarray(4, expectedLength + 4).toString("utf8");
          try {
            finish(JSON.parse(payload) as BridgeResponse<T>);
          } catch (error) {
            finish(errorResponse<T>(request, "BRIDGE_PARSE_ERROR", error instanceof Error ? error.message : String(error)));
          }
        }
      });

      socket.once("error", (error) => {
        finish(
          errorResponse<T>(
            request,
            "BRIDGE_UNAVAILABLE",
            `Could not connect to Revit add-in pipe ${this.pipePath}: ${error.message}`,
            "Open Revit, load the add-in, and run revit.status again."
          )
        );
      });
    });
  }
}

function errorResponse<T>(
  request: BridgeRequest,
  code: string,
  message: string,
  suggestedNextAction?: string
): BridgeResponse<T> {
  return {
    ok: false,
    requestId: request.requestId,
    error: {
      code,
      message,
      recoverable: true,
      suggestedNextAction,
    },
    warnings: [],
  };
}
