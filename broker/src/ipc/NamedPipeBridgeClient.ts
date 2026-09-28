import net from "node:net";
import type {
  BridgeHealthResult,
  BridgeQueueDiagnostics,
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
  CreateProjectFromTemplateRequest,
  CreateProjectFromTemplateResult,
  CurrentViewRequest,
  CurrentViewResult,
  LevelSummary,
  MaterialQuantitiesRequest,
  MaterialQuantitiesResult,
  ModelDeliveryExecuteRequest,
  ModelDeliveryExecuteResult,
  ModelDeliveryFixtureRequest,
  ModelDeliveryFixtureResult,
  ModelDeliveryInspectRequest,
  ModelDeliveryInspectResult,
  ModelDeliveryCancelRequest,
  ModelDeliveryCancelResult,
  ModelDeliveryPreviewRequest,
  ModelDeliveryPreviewResult,
  ModelDeliveryStatusRequest,
  ModelDeliveryStatusResult,
  ModelContextRequest,
  ModelContextResult,
  ModelReadinessRequest,
  ModelReadinessResult,
  ModelStatisticsRequest,
  ModelStatisticsResult,
  ParameterDescribeRequest,
  ParameterDescribeResult,
  QueryRequest,
  QueryResult,
  RevitDocumentSummary,
  RevitStatus,
  RequestResultRequest,
  RequestResultResult,
  RoomsRequest,
  RoomsResult,
  ScheduleFieldsRequest,
  ScheduleFieldsResult,
  SchedulesRequest,
  SchedulesResult,
  ScopedElementListRequest,
  ScopedElementListResult,
  SheetsRequest,
  SheetsResult,
  ViewsRequest,
  ViewsResult,
  WarningsRequest,
  WarningsResult,
} from "@revit-mcp-next/contracts";
import type { BridgeCallOptions, RevitBridgeClient } from "./RevitBridgeClient.js";
import {
  validateBridgeResponse,
  validateRequestResult,
} from "./BridgeResponseValidation.js";

const MAX_BRIDGE_FRAME_BYTES = 4 * 1024 * 1024;
const MAX_TIMER_MS = 2_147_483_647;
const INITIAL_CONNECT_RETRY_DELAY_MS = 25;
const MAX_CONNECT_RETRY_DELAY_MS = 250;
const CONNECT_RETRY_JITTER_RATIO = 0.2;
const MAX_WRITE_RECOVERY_MS = 15_000;
const WRITE_RECOVERY_POLL_MS = 100;
// A missing pipe means Revit is not running or the add-in did not load. Retrying for the
// full request timeout (up to 300 s) only made agents wait; allow a short grace for a
// listener being recreated and then fail with an actionable error.
const MISSING_PIPE_GRACE_MS = 1_500;
// Every listener busy means earlier requests are still waiting on Revit. Report that
// explicitly instead of surfacing a generic "could not connect" after the full timeout.
const BUSY_PIPE_GRACE_MS = 8_000;
const MISSING_PIPE_ERROR_CODES = new Set(["ENOENT"]);
const BUSY_PIPE_ERROR_CODES = new Set(["EBUSY", "ERROR_PIPE_BUSY", "EAGAIN"]);
// Stall detection: while a queued request waits for Revit, probe the control pipe. When
// the add-in reports that nothing is executing but queued work has not been picked up for
// stallDetectionMs, Revit is not servicing the ExternalEvent (modal dialog, edit mode, long
// native command). The broker then cancels the still-queued request and fails fast.
const DEFAULT_STALL_DETECTION_MS = 8_000;
const STALL_PROBE_START_MS = 3_000;
const STALL_PROBE_INTERVAL_MS = 2_000;
const STALL_PROBE_TIMEOUT_MS = 1_500;
const RECENT_DIALOG_WINDOW_MS = 10 * 60_000;
const RETRYABLE_CONNECT_ERROR_CODES = new Set([
  "EAGAIN",
  "EBUSY",
  "ECONNREFUSED",
  "ENOENT",
  "ENXIO",
  "EPIPE",
  "ERROR_PIPE_BUSY",
]);

export interface NamedPipeBridgeClientOptions {
  pipeName: string;
  controlPipeName?: string;
  sessionId: string;
  defaultTimeoutMs: number;
  authToken?: string;
  /**
   * Fail a queued request after Revit has not serviced queued work for this many
   * milliseconds while nothing is executing. 0 disables. Defaults to
   * REVIT_MCP_NEXT_STALL_MS or 8000.
   */
  stallDetectionMs?: number;
}

export class NamedPipeBridgeClient implements RevitBridgeClient {
  private readonly pipePath: string;
  private readonly controlPipePath: string;
  private readonly authToken?: string;
  private readonly defaultTimeoutMs: number;
  private readonly stallDetectionMs: number;
  private readonly pendingRequests = new Set<() => void>();
  private disposed = false;

  constructor(private readonly options: NamedPipeBridgeClientOptions) {
    this.pipePath = resolvePipePath(options.pipeName);
    this.controlPipePath = resolvePipePath(options.controlPipeName ?? `${options.pipeName}-control`);
    this.authToken = resolveAuthToken(options);
    this.defaultTimeoutMs = validateTimeoutMs(options.defaultTimeoutMs, "defaultTimeoutMs");
    this.stallDetectionMs = resolveStallDetectionMs(options.stallDetectionMs);
  }

  bridgeHealth(
    request: BridgeRequest<{ instanceId?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<BridgeHealthResult>> {
    return this.send(request, options);
  }

  async getRequestResult<TData = unknown>(
    request: BridgeRequest<RequestResultRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RequestResultResult<TData>>> {
    const response = await this.send<unknown>(request, options);
    if (!response.ok) return response;

    const validation = validateRequestResult<TData>(response.data, request.payload.requestId);
    if (!validation.valid) {
      return errorResponse(
        request,
        validation.code,
        validation.message,
        "Restart the MCP client after confirming the installed broker and add-in versions match."
      );
    }
    return { ...response, data: validation.result };
  }

  status(
    request: BridgeRequest<{ instanceId?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitStatus>> {
    return this.send(request, options);
  }

  listDocuments(
    request: BridgeRequest<{ instanceId?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitDocumentSummary[]>> {
    return this.send(request, options);
  }

  createProjectFromTemplate(
    request: BridgeRequest<CreateProjectFromTemplateRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CreateProjectFromTemplateResult>> {
    return this.send(request, options);
  }

  createModelDeliveryFixture(
    request: BridgeRequest<ModelDeliveryFixtureRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryFixtureResult>> {
    return this.send(request, options);
  }

  previewModelDelivery(
    request: BridgeRequest<ModelDeliveryPreviewRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryPreviewResult>> {
    return this.send(request, options);
  }

  inspectModelDelivery(
    request: BridgeRequest<ModelDeliveryInspectRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryInspectResult>> {
    return this.send(request, options);
  }

  executeModelDelivery(
    request: BridgeRequest<ModelDeliveryExecuteRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryExecuteResult>> {
    return this.send(request, options);
  }

  getModelDeliveryStatus(
    request: BridgeRequest<ModelDeliveryStatusRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryStatusResult>> {
    return this.send(request, options);
  }

  cancelModelDelivery(
    request: BridgeRequest<ModelDeliveryCancelRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelDeliveryCancelResult>> {
    return this.send(request, options);
  }

  getLevels(
    request: BridgeRequest<{ documentFingerprint?: string }>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<LevelSummary[]>> {
    return this.send(request, options);
  }

  getViews(
    request: BridgeRequest<ViewsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ViewsResult>> {
    return this.send(request, options);
  }

  getSheets(
    request: BridgeRequest<SheetsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<SheetsResult>> {
    return this.send(request, options);
  }

  getSchedules(
    request: BridgeRequest<SchedulesRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<SchedulesResult>> {
    return this.send(request, options);
  }

  getScheduleFields(
    request: BridgeRequest<ScheduleFieldsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ScheduleFieldsResult>> {
    return this.send(request, options);
  }

  viewWorkflow(request: BridgeRequest<Record<string, unknown>>, options?: BridgeCallOptions): Promise<BridgeResponse<Record<string, unknown>>> { return this.send(request, options); }

  getCurrentView(
    request: BridgeRequest<CurrentViewRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CurrentViewResult>> {
    return this.send(request, options);
  }

  getCurrentViewElements(
    request: BridgeRequest<ScopedElementListRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ScopedElementListResult>> {
    return this.send(request, options);
  }

  getSelection(
    request: BridgeRequest<ScopedElementListRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ScopedElementListResult>> {
    return this.send(request, options);
  }

  analyzeModel(
    request: BridgeRequest<ModelStatisticsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelStatisticsResult>> {
    return this.send(request, options);
  }

  getModelReadiness(
    request: BridgeRequest<ModelReadinessRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelReadinessResult>> {
    return this.send(request, options);
  }

  getModelContext(
    request: BridgeRequest<ModelContextRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ModelContextResult>> {
    return this.send(request, options);
  }

  getMaterialQuantities(
    request: BridgeRequest<MaterialQuantitiesRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<MaterialQuantitiesResult>> {
    return this.send(request, options);
  }

  getWarnings(
    request: BridgeRequest<WarningsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<WarningsResult>> {
    return this.send(request, options);
  }

  getRooms(
    request: BridgeRequest<RoomsRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RoomsResult>> {
    return this.send(request, options);
  }

  query(
    request: BridgeRequest<QueryRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<QueryResult>> {
    return this.send(request, options);
  }

  describeParameters(
    request: BridgeRequest<ParameterDescribeRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ParameterDescribeResult>> {
    return this.send(request, options);
  }

  catalog(
    request: BridgeRequest<CatalogRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CatalogResult>> {
    return this.send(request, options);
  }

  previewChange(
    request: BridgeRequest<ChangeSetRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ChangePreviewResult>> {
    return this.send(request, options);
  }

  applyChange(
    request: BridgeRequest<ChangeApplyRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<ChangeApplyResult>> {
    return this.send(request, options);
  }

  cancel(
    request: BridgeRequest<CancelRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CancelResult>> {
    return this.send(request, options);
  }

  raw<T = unknown>(
    request: BridgeRequest<Record<string, unknown>>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<T>> {
    return this.send(request, options);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const cancel of [...this.pendingRequests]) cancel();
  }

  private send<T>(request: BridgeRequest, options?: BridgeCallOptions): Promise<BridgeResponse<T>> {
    const timeoutMs = validTimeoutMs(request.timeoutMs) ? request.timeoutMs : this.defaultTimeoutMs;
    const pipePath = isControlOperation(request.operation) ? this.controlPipePath : this.pipePath;
    if (this.disposed) {
      return Promise.resolve(
        errorResponse<T>(request, "BRIDGE_DISPOSED", "The Revit bridge client is shutting down.")
      );
    }

    return new Promise((resolve) => {
      const responseHeader = Buffer.alloc(4);
      const responseChunks: Buffer[] = [];
      const deadline = Date.now() + timeoutMs;
      let headerBytes = 0;
      let payloadBytes = 0;
      let expectedLength: number | null = null;
      let settled = false;
      let timer: NodeJS.Timeout | undefined;
      let retryTimer: NodeJS.Timeout | undefined;
      let currentSocket: net.Socket | undefined;
      let connectAttempt = 0;
      let phase = "connecting to Revit add-in pipe";
      let requestBytesMayHaveBeenSent = false;
      let recoveryStarted = false;
      let firstMissingPipeAt: number | undefined;
      let firstBusyPipeAt: number | undefined;
      let stallTimer: NodeJS.Timeout | undefined;
      let requestSentAt: number | undefined;
      let lastQueue: BridgeQueueDiagnostics | undefined;
      let stallObservations = 0;

      const finish = (response: BridgeResponse<T>) => {
        if (settled) return;
        settled = true;
        if (timer) clearTimeout(timer);
        if (retryTimer) clearTimeout(retryTimer);
        if (stallTimer) clearTimeout(stallTimer);
        options?.signal?.removeEventListener("abort", abortHandler);
        this.pendingRequests.delete(disposeHandler);
        currentSocket?.destroy();
        resolve(response);
      };

      const disposeHandler = () => {
        const failure = errorResponse<T>(
          request,
          "BRIDGE_DISPOSED",
          "The Revit bridge client is shutting down."
        );
        if (requestBytesMayHaveBeenSent && isMutationRequest(request)) {
          finish(
            writeOutcomeUnknownResponse(
              request,
              failure,
              "The bridge client was disposed before the sent mutation could be reconciled."
            )
          );
          return;
        }
        finish(failure);
      };

      const finishAmbiguous = (failure: BridgeResponse<T>) => {
        if (!requestBytesMayHaveBeenSent || !isMutationRequest(request)) {
          finish(failure);
          return;
        }
        if (recoveryStarted) return;
        recoveryStarted = true;
        if (timer) clearTimeout(timer);
        if (retryTimer) clearTimeout(retryTimer);
        currentSocket?.destroy();
        void this.reconcileAmbiguousWrite(request, failure).then(finish);
      };

      const abortHandler = () => {
        const failure = errorResponse<T>(request, "REQUEST_CANCELLED", "The MCP client cancelled the request.");
        if (!requestBytesMayHaveBeenSent) {
          finish(failure);
          return;
        }

        if (isMutationRequest(request)) {
          if (recoveryStarted) return;
          recoveryStarted = true;
          if (timer) clearTimeout(timer);
          if (retryTimer) clearTimeout(retryTimer);
          currentSocket?.destroy();
          void this.cancelThenReconcileMutation(request, failure).then(finish);
          return;
        }

        currentSocket?.destroy();
        void this.sendBestEffortCancel(request);
        finish(failure);
      };

      const disconnected = (message: string) => {
        finishAmbiguous(
          errorResponse<T>(
            request,
            "BRIDGE_DISCONNECTED",
            message,
            "Confirm Revit is responsive and retry the request."
          )
        );
      };

      const scheduleConnectRetry = (error?: NodeJS.ErrnoException) => {
        if (settled) return;
        const now = Date.now();
        const remainingMs = deadline - now;
        const delayMs = connectRetryDelayMs(connectAttempt);
        const code = error?.code;
        if (code && MISSING_PIPE_ERROR_CODES.has(code)) {
          firstMissingPipeAt ??= now;
          if (now - firstMissingPipeAt >= MISSING_PIPE_GRACE_MS) {
            finish(
              errorResponse<T>(
                request,
                "BRIDGE_UNAVAILABLE",
                `Revit MCP pipe ${pipePath} does not exist. Revit is not running, the Revit MCP Next add-in did not load, or Revit was restarted.`,
                "Ask the user to start Revit, open the project, and confirm the add-in loaded without an error dialog, then call revit.list_instances. Do not keep retrying this call until Revit is running."
              )
            );
            return;
          }
        } else if (code && BUSY_PIPE_ERROR_CODES.has(code)) {
          firstBusyPipeAt ??= now;
          if (now - firstBusyPipeAt >= BUSY_PIPE_GRACE_MS) {
            finish(
              errorResponse<T>(
                request,
                "BRIDGE_BUSY",
                `Every Revit MCP pipe connection on ${pipePath} stayed busy for ${Math.round((now - firstBusyPipeAt) / 1000)}s. Earlier requests are still waiting for Revit, which usually means Revit is blocked by a modal dialog, an edit/sketch mode, or a long command.`,
                "Call revit.bridge_health: check queue.executing, queue.pendingCount, and queue.lastDialogId. Ask the user to close the dialog or finish the command, then retry once."
              )
            );
            return;
          }
        }
        if (
          this.disposed ||
          options?.signal?.aborted ||
          !isRetryableConnectError(error) ||
          remainingMs <= delayMs
        ) {
          finish(
            errorResponse<T>(
              request,
              "BRIDGE_UNAVAILABLE",
              `Could not connect to Revit add-in pipe ${pipePath}: ${error?.message ?? "connection closed"}`,
              "Open Revit, load the add-in, and run revit.status again."
            )
          );
          return;
        }

        phase = "retrying connection to Revit add-in pipe";
        retryTimer = setTimeout(connect, delayMs);
      };

      const scheduleStallProbe = (delayMs: number) => {
        if (settled || recoveryStarted || this.stallDetectionMs <= 0 || isControlOperation(request.operation)) return;
        if (deadline - Date.now() <= delayMs + STALL_PROBE_TIMEOUT_MS) return;
        stallTimer = setTimeout(() => void probeForStall(), delayMs);
      };

      const probeForStall = async () => {
        if (settled || recoveryStarted) return;
        const health = await this.send<BridgeHealthResult>(
          controlRequest(request, "bridge_health", {}, STALL_PROBE_TIMEOUT_MS)
        );
        if (settled || recoveryStarted) return;
        if (!health.ok || !isRecord(health.data) || !isRecord(health.data.queue)) {
          scheduleStallProbe(STALL_PROBE_INTERVAL_MS);
          return;
        }

        const queue = health.data.queue as BridgeQueueDiagnostics;
        lastQueue = queue;
        // Older add-ins do not report execution state; never guess a stall for them.
        if (typeof queue.executing !== "boolean") return;
        // Our request is running inside Revit; it cannot be interrupted, so just wait.
        if (queue.executing && queue.executingRequestId === request.requestId) return;

        const now = Date.now();
        const waitedMs = now - (requestSentAt ?? now);
        const lastDequeuedAt = queue.lastDequeuedAtUtc ? Date.parse(queue.lastDequeuedAtUtc) : Number.NaN;
        const sinceLastDequeueMs = Number.isFinite(lastDequeuedAt) ? now - lastDequeuedAt : Number.POSITIVE_INFINITY;
        const stalled =
          !queue.executing &&
          queue.pendingCount > 0 &&
          waitedMs >= this.stallDetectionMs &&
          (queue.oldestPendingAgeMs ?? 0) >= this.stallDetectionMs &&
          sinceLastDequeueMs >= this.stallDetectionMs;
        // Require two consecutive stalled observations so the gap between two queued items
        // (or two model-delivery steps) is never mistaken for a stall.
        stallObservations = stalled ? stallObservations + 1 : 0;
        if (stallObservations < 2) {
          scheduleStallProbe(STALL_PROBE_INTERVAL_MS);
          return;
        }

        const cancellation = await this.send<CancelResult>(
          controlRequest(
            request,
            "cancel_request",
            { requestId: request.requestId, reason: "Revit did not service the ExternalEvent (broker stall detection)." },
            STALL_PROBE_TIMEOUT_MS
          )
        );
        if (settled || recoveryStarted) return;
        if (!cancellation.ok || cancellation.data.cancelled !== true) {
          // It started (or finished) in the meantime; keep waiting for the real response.
          scheduleStallProbe(STALL_PROBE_INTERVAL_MS);
          return;
        }

        finish(revitBusyResponse<T>(request, queue, waitedMs));
      };

      const connect = () => {
        if (settled) return;
        connectAttempt++;
        phase = "connecting to Revit add-in pipe";
        const socket = net.createConnection(pipePath);
        currentSocket = socket;
        let attemptFinished = false;
        let connected = false;
        const finishAttempt = () => {
          if (attemptFinished) return false;
          attemptFinished = true;
          return true;
        };

        socket.once("connect", () => {
          if (settled || connected || attemptFinished) return;
          connected = true;
          phase = "writing bridge request";

          let body: Buffer;
          try {
            body = Buffer.from(JSON.stringify(this.prepareRequest(request)), "utf8");
          } catch (error) {
            finish(
              errorResponse<T>(
                request,
                "BRIDGE_SERIALIZE_ERROR",
                error instanceof Error ? error.message : String(error)
              )
            );
            return;
          }

          if (body.byteLength > MAX_BRIDGE_FRAME_BYTES) {
            finish(
              errorResponse<T>(
                request,
                "BRIDGE_REQUEST_TOO_LARGE",
                `Bridge request frame is ${body.byteLength} bytes, above the ${MAX_BRIDGE_FRAME_BYTES} byte limit.`
              )
            );
            return;
          }

          const header = Buffer.allocUnsafe(4);
          header.writeUInt32BE(body.byteLength, 0);
          requestBytesMayHaveBeenSent = true;
          socket.write(Buffer.concat([header, body]), (error) => {
            if (error && !settled) {
              disconnected(`The Revit add-in pipe closed while writing the request: ${error.message}`);
              return;
            }
            if (!settled) {
              phase = "waiting for Revit ExternalEvent response";
              requestSentAt = Date.now();
              scheduleStallProbe(STALL_PROBE_START_MS);
            }
          });
        });

        socket.on("data", (chunk) => {
          if (settled) return;
          phase = "reading bridge response";

          let offset = 0;
          while (offset < chunk.byteLength && !settled) {
            if (expectedLength === null) {
              const headerRemaining = responseHeader.byteLength - headerBytes;
              const headerTake = Math.min(headerRemaining, chunk.byteLength - offset);
              chunk.copy(responseHeader, headerBytes, offset, offset + headerTake);
              headerBytes += headerTake;
              offset += headerTake;

              if (headerBytes < responseHeader.byteLength) {
                return;
              }

              expectedLength = responseHeader.readUInt32BE(0);
              if (expectedLength > MAX_BRIDGE_FRAME_BYTES) {
                finishAmbiguous(
                  errorResponse<T>(
                    request,
                    "BRIDGE_FRAME_TOO_LARGE",
                    `Bridge response frame announced ${expectedLength} bytes, above the ${MAX_BRIDGE_FRAME_BYTES} byte limit.`
                  )
                );
                return;
              }
            }

            const payloadRemaining = expectedLength - payloadBytes;
            const payloadTake = Math.min(payloadRemaining, chunk.byteLength - offset);
            if (payloadTake > 0) {
              responseChunks.push(chunk.subarray(offset, offset + payloadTake));
              payloadBytes += payloadTake;
              offset += payloadTake;
            }

            if (payloadBytes === expectedLength) {
              const payload = Buffer.concat(responseChunks, expectedLength).toString("utf8");
              try {
                const parsed: unknown = JSON.parse(payload);
                const validation = validateBridgeResponse<T>(parsed, request.requestId);
                if (validation.valid) {
                  finish(validation.response);
                } else {
                  finishAmbiguous(
                    errorResponse<T>(
                      request,
                      validation.code,
                      validation.message,
                      "Restart the MCP client after confirming the installed broker and add-in versions match."
                    )
                  );
                }
              } catch (error) {
                finishAmbiguous(
                  errorResponse<T>(
                    request,
                    "BRIDGE_PARSE_ERROR",
                    error instanceof Error ? error.message : String(error)
                  )
                );
              }
            }
          }
        });

        socket.once("end", () => {
          if (settled) return;
          disconnected("The Revit add-in pipe ended before a complete response was received.");
        });

        socket.once("close", () => {
          if (settled) return;
          if (!connected && !requestBytesMayHaveBeenSent) {
            if (finishAttempt()) scheduleConnectRetry();
            return;
          }
          disconnected("The Revit add-in pipe closed before a complete response was received.");
        });

        socket.once("error", (error: NodeJS.ErrnoException) => {
          if (settled) return;
          if (!connected && !requestBytesMayHaveBeenSent && finishAttempt()) {
            socket.destroy();
            scheduleConnectRetry(error);
            return;
          }
          disconnected(`The Revit add-in pipe failed after connecting: ${error.message}`);
        });
      };

      options?.signal?.addEventListener("abort", abortHandler, { once: true });
      this.pendingRequests.add(disposeHandler);
      timer = setTimeout(() => {
        finishAmbiguous(
          errorResponse<T>(
            request,
            "BRIDGE_TIMEOUT",
            `Timed out after ${timeoutMs}ms while ${phase}.${describeQueueForTimeout(lastQueue, request.requestId)}`,
            phase.includes("response")
              ? "Bring Revit to the foreground and close any modal dialogs, then retry. If this happened during smoke, inspect the Revit journal for TaskDialog entries."
              : "Open Revit, load the add-in, and run revit.status again."
          )
        );
      }, timeoutMs);

      if (options?.signal?.aborted) abortHandler();
      else if (this.disposed) disposeHandler();
      else connect();
    });
  }

  private async reconcileAmbiguousWrite<T>(
    request: BridgeRequest,
    transportFailure: BridgeResponse<T>
  ): Promise<BridgeResponse<T>> {
    const recoveryDeadline = Date.now() + Math.min(MAX_WRITE_RECOVERY_MS, this.defaultTimeoutMs);
    let lastReason = "The request-result lookup did not return a final response.";

    while (!this.disposed && Date.now() < recoveryDeadline) {
      const timeoutMs = Math.max(1, recoveryDeadline - Date.now());
      const lookupRequest: BridgeRequest<RequestResultRequest> = {
        protocolVersion: request.protocolVersion,
        requestId: `${request.requestId}-result-${Date.now()}-${Math.random().toString(36).slice(2)}`,
        sessionId: request.sessionId,
        operation: "get_request_result",
        operationKind: "debug",
        timeoutMs,
        payload: { requestId: request.requestId },
      };
      const lookupResponse = await this.getRequestResult<T>(lookupRequest);
      if (!lookupResponse.ok) {
        lastReason = `${lookupResponse.error.code}: ${lookupResponse.error.message}`;
        break;
      }

      const lookup = lookupResponse.data;
      if (!lookup.found) {
        lastReason = "The add-in has not recorded the original request yet.";
      } else if (lookup.state === "committed" || lookup.state === "rolledBack" || lookup.state === "failed") {
        return appendRecoveryWarning(lookup.response, lookup.state);
      } else {
        lastReason = `The original request is still ${lookup.state}.`;
      }
      const remainingMs = recoveryDeadline - Date.now();
      if (remainingMs <= WRITE_RECOVERY_POLL_MS) break;
      await delay(WRITE_RECOVERY_POLL_MS);
    }

    return writeOutcomeUnknownResponse(request, transportFailure, lastReason);
  }

  private async cancelThenReconcileMutation<T>(
    request: BridgeRequest,
    cancellationFailure: BridgeResponse<T>
  ): Promise<BridgeResponse<T>> {
    await this.sendBestEffortCancel(request);
    return this.reconcileAmbiguousWrite(request, cancellationFailure);
  }

  private async sendBestEffortCancel(request: BridgeRequest): Promise<void> {
    const timeoutMs = Math.min(1000, this.defaultTimeoutMs);
    const cancellationRequest: BridgeRequest<CancelRequest> = {
      protocolVersion: request.protocolVersion,
      requestId: `${request.requestId}-cancel-${Date.now()}-${Math.random().toString(36).slice(2)}`,
      sessionId: request.sessionId,
      operation: "cancel_request",
      operationKind: "debug",
      timeoutMs,
      payload: {
        requestId: request.requestId,
        reason: "The MCP client aborted the original request.",
      },
    };
    await this.send<CancelResult>(cancellationRequest);
  }

  private prepareRequest<TPayload>(request: BridgeRequest<TPayload>): BridgeRequest<TPayload> {
    if (!this.authToken) return request;
    return {
      ...request,
      authToken: this.authToken,
    };
  }
}

function controlRequest<TPayload>(
  original: BridgeRequest,
  operation: "bridge_health" | "cancel_request",
  payload: TPayload,
  timeoutMs: number
): BridgeRequest<TPayload> {
  return {
    protocolVersion: original.protocolVersion,
    requestId: `${original.requestId}-${operation}-${Date.now()}-${Math.random().toString(36).slice(2)}`,
    sessionId: original.sessionId,
    operation,
    operationKind: "debug",
    timeoutMs,
    payload,
  };
}

function resolveStallDetectionMs(explicit: number | undefined): number {
  const raw = explicit ?? (process.env.REVIT_MCP_NEXT_STALL_MS ? Number(process.env.REVIT_MCP_NEXT_STALL_MS) : undefined);
  if (raw === undefined) return DEFAULT_STALL_DETECTION_MS;
  if (!Number.isFinite(raw) || raw <= 0) return 0;
  return Math.max(STALL_PROBE_START_MS, Math.min(MAX_TIMER_MS, Math.round(raw)));
}

function describeRecentDialog(queue: BridgeQueueDiagnostics | undefined): string {
  if (!queue?.lastDialogId || typeof queue.lastDialogAgeMs !== "number" || queue.lastDialogAgeMs > RECENT_DIALOG_WINDOW_MS) {
    return "";
  }
  const message = queue.lastDialogMessage ? `: "${queue.lastDialogMessage}"` : "";
  return ` Last Revit dialog seen ${Math.round(queue.lastDialogAgeMs / 1000)}s ago: ${queue.lastDialogId}${message}.`;
}

function describeQueueForTimeout(queue: BridgeQueueDiagnostics | undefined, requestId: string): string {
  if (!queue || typeof queue.executing !== "boolean") return "";
  if (queue.executing && queue.executingRequestId === requestId) {
    return " Revit was still executing this request; in-flight Revit API work cannot be interrupted.";
  }
  if (queue.executing) {
    const forSeconds = typeof queue.executingForMs === "number" ? ` for ${Math.round(queue.executingForMs / 1000)}s` : "";
    return ` Revit was busy executing another request (${queue.executingOperation ?? "unknown operation"})${forSeconds}.`;
  }
  return describeRecentDialog(queue);
}

function revitBusyResponse<T>(request: BridgeRequest, queue: BridgeQueueDiagnostics, waitedMs: number): BridgeResponse<T> {
  const seconds = Math.round(waitedMs / 1000);
  return {
    ok: false,
    requestId: request.requestId,
    error: {
      code: "REVIT_BUSY",
      message:
        `Revit has not picked up queued API work for ${seconds}s and nothing is executing, so Revit is not idle ` +
        `(a modal dialog is open, an edit/sketch mode or command is active, or Revit is busy with a native operation such as sync or load). ` +
        `The request was cancelled before it ran; nothing was changed.${describeRecentDialog(queue)}`,
      recoverable: true,
      details: compactRecord({
        operation: request.operation,
        waitedMs,
        pendingCount: queue.pendingCount,
        oldestPendingAgeMs: queue.oldestPendingAgeMs,
        lastRaiseResult: queue.lastRaiseResult,
        lastDialogId: queue.lastDialogId,
        lastDialogMessage: queue.lastDialogMessage,
        lastDialogAgeMs: queue.lastDialogAgeMs,
      }),
      suggestedNextAction:
        "Ask the user to close any open Revit dialog, press Esc to leave edit/sketch mode, or wait for the running command to finish; then retry this call once. Set REVIT_MCP_NEXT_STALL_MS=0 to disable this fail-fast check.",
    },
    warnings: [],
    metrics: { elapsedMs: waitedMs },
  };
}

function compactRecord(value: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(value).filter(([, child]) => child !== undefined));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function validTimeoutMs(value: number): boolean {
  return Number.isFinite(value) && Number.isInteger(value) && value > 0 && value <= MAX_TIMER_MS;
}

function resolvePipePath(pipeName: string): string {
  return pipeName.startsWith("\\\\") ? pipeName : `\\\\.\\pipe\\${pipeName}`;
}

function isControlOperation(operation: string): boolean {
  return operation === "bridge_health" || operation === "cancel_request" || operation === "get_request_result";
}

function validateTimeoutMs(value: number, name: string): number {
  if (!validTimeoutMs(value)) {
    throw new RangeError(`${name} must be a positive integer no greater than ${MAX_TIMER_MS}.`);
  }
  return value;
}

function isRetryableConnectError(error?: NodeJS.ErrnoException): boolean {
  return error?.code === undefined || RETRYABLE_CONNECT_ERROR_CODES.has(error.code);
}

function connectRetryDelayMs(attempt: number): number {
  const exponentialDelay = Math.min(
    MAX_CONNECT_RETRY_DELAY_MS,
    INITIAL_CONNECT_RETRY_DELAY_MS * 2 ** Math.max(0, attempt - 1)
  );
  const jitter = exponentialDelay * CONNECT_RETRY_JITTER_RATIO * (Math.random() * 2 - 1);
  return Math.max(1, Math.round(exponentialDelay + jitter));
}

function resolveAuthToken(options: NamedPipeBridgeClientOptions): string | undefined {
  const value = Object.prototype.hasOwnProperty.call(options, "authToken")
    ? options.authToken
    : process.env.REVIT_MCP_NEXT_AUTH_TOKEN;
  return value && value.length > 0 ? value : undefined;
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
    metrics: {
      elapsedMs: 0,
    },
  };
}

function writeOutcomeUnknownResponse<T>(
  request: BridgeRequest,
  transportFailure: BridgeResponse<T>,
  recoveryReason: string
): BridgeResponse<T> {
  const transportCode = transportFailure.ok ? "UNKNOWN" : transportFailure.error.code;
  return {
    ok: false,
    requestId: request.requestId,
    error: {
      code: "BRIDGE_WRITE_OUTCOME_UNKNOWN",
      message: `The bridge lost the response after sending write request ${request.requestId}, and reconciliation could not prove its outcome.`,
      recoverable: false,
      details: {
        requestId: request.requestId,
        operation: request.operation,
        transportCode,
        recoveryReason,
      },
      suggestedNextAction:
        "Do not retry this write. Inspect the model and run revit.status before creating a new preview.",
    },
    warnings: [],
    metrics: { elapsedMs: 0 },
  };
}

function appendRecoveryWarning<T>(
  response: BridgeResponse<T>,
  state: "committed" | "rolledBack" | "failed"
): BridgeResponse<T> {
  return {
    ...response,
    warnings: [
      ...response.warnings,
      {
        code: "BRIDGE_RESPONSE_RECOVERED",
        message: `The original pipe response was lost. The broker recovered the recorded ${state} result without replaying the write.`,
      },
    ],
  };
}

function isMutationRequest(request: BridgeRequest): boolean {
  return request.operationKind === "write" || request.operationKind === "destructive";
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, milliseconds));
}
