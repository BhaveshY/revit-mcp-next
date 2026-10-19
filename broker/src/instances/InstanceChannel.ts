// One channel per live instance (D2 §11.2, SPEC §8.7-§8.10): primary/control pipe exchanges, the concurrency limiter
// (4 primary / 2 control slots, FIFO, BRIDGE_BUSY after min(8 s, remaining − 5 s) with details), the shared
// HealthPoller (1 s, only while a request is in flight), busy classification with fail-fast (§8.8, BusyClassifier),
// read coalescing with refcounted abort, the read-after-write barrier (only while this session's write call is
// outstanding), the D2 §11.4 error mapping, one auto-retry for reads/previews (D2 §11.6), write reconciliation bounded
// by the deadline (§8.10), late results (logged; the framework turns them into LATE_RESULT / READ_STILL_RUNNING) and
// one merged log line per exchange (log.annotate). A channel of an instance that went away is retired, never disposed
// while work is pending: new calls get REVIT_EXITED, in-flight writes end in reconciliation.

import { join } from "node:path";
import {
  BRIDGE_PROTOCOL_VERSION,
  type BridgeRequest,
  type BridgeResponse,
  type ControlOp,
  type DialogInfo,
  type DocSnapshot,
  type ExecutingItem,
  type HealthData,
  type LedgerEntry,
} from "@revit-mcp-next/contracts/protocol";
import type { Job, Kind } from "@revit-mcp-next/contracts/catalog";
import { isTerminalLedgerState } from "@revit-mcp-next/contracts/protocol";
import { BusyClassifier, type BusyDecision } from "../ipc/BusyClassifier.js";
import { ConcurrencyLimiter } from "../ipc/ConcurrencyLimiter.js";
import { failure, TransportError } from "../ipc/errors.js";
import { HealthPoller } from "../ipc/HealthPoller.js";
import { openExchange, type PipeExchange } from "../ipc/PipeClient.js";
import { ReadCoalescer } from "../ipc/ReadCoalescer.js";
import type { AuthStore } from "../runtime/auth.js";
import { BRIDGE_MARGIN_MS, Deadline } from "../runtime/deadline.js";
import { ulid } from "../runtime/ids.js";
import type { BrokerLog } from "../runtime/log.js";
import type { SettingsStore } from "../runtime/settings.js";
import { searchLedgers } from "../state/Ledger.js";
import { lockState, type InstanceInfo, type InstanceRegistry } from "./InstanceRegistry.js";

export interface ChannelSession {
  clientKey: string;
  /** This session's short id (j#/w#) for an executing item, when it is ours. */
  refOf(item: ExecutingItem): string | null;
  /** The executing item is this session's own outstanding write tool call (read-after-write barrier). */
  isOwnOutstandingWrite(item: ExecutingItem): boolean;
}

export interface SendOptions {
  deadline: Deadline;
  session: ChannelSession;
  /** Registry job flag; used to describe late reads. */
  job?: Job;
  progress?: (message: string, done?: number, total?: number) => void;
  /** Coalesce identical reads in flight (reads only). */
  coalesceKey?: string;
}

export interface SendResult {
  response: BridgeResponse;
  /** READ_STILL_RUNNING / WRITE_STILL_RUNNING / cancelled running write: resolves with the late response. */
  late?: Promise<BridgeResponse>;
  retried?: boolean;
  coalesced?: boolean;
  recovered?: boolean;
  limiterMs?: number;
}

export interface ChannelDeps {
  home: string;
  auth: AuthStore;
  settings: SettingsStore;
  log: BrokerLog;
  registry: InstanceRegistry;
}

type Outcome =
  | { kind: "response"; response: BridgeResponse }
  | { kind: "error"; error: TransportError }
  | { kind: "busy"; response: BridgeResponse }
  | { kind: "deadline"; classifier: BusyClassifier }
  | { kind: "aborted" };

interface OutstandingWrite {
  requestId: string;
  clientKey: string;
  docKey: string | null;
  writeTag?: string;
  done: Promise<void>;
}

const WRITE_KINDS: ReadonlySet<Kind> = new Set(["write", "lifecycle", "code"]);
const LATE_READ_MAX_MS = 10 * 60_000;
const LATE_WRITE_MAX_MS = 30 * 60_000;
const MISSING_PIPE_GRACE_MS = 1_500;
const CONTROL_TIMEOUT_MS = 800;
const HEALTH_FRESH_MS = 1_000;
const ADDIN_MAX_PENDING = 32;
const RECONCILE_POLL_MS = 250;
const NOT_RECEIVED_AFTER_MS = 5_000;
const RETRY_MIN_REMAINING_MS = 5_000;

export function isWriteKind(kind: Kind): boolean {
  return WRITE_KINDS.has(kind);
}

/** Apply-mode writes (previews are read-like: retried once, never reconciled through the ledger). */
function isApplyWrite(request: BridgeRequest): boolean {
  return isWriteKind(request.kind) && request.mode !== "preview";
}

export class InstanceChannel {
  info: InstanceInfo;
  private readonly primary: ConcurrencyLimiter;
  private readonly controlSlots = new ConcurrencyLimiter(2);
  readonly poller: HealthPoller;
  private readonly coalescer = new ReadCoalescer<SendResult>();
  private readonly outstanding = new Map<string, OutstandingWrite>();
  private retired = false;
  private retiredAt = 0;
  private pending = 0;
  /** Counters for status detail:full. */
  readonly stats = { sent: 0, coalesced: 0, retried: 0, busyFailFast: 0, bridgeBusy: 0, recovered: 0, late: 0, barrierWaits: 0 };

  constructor(info: InstanceInfo, private readonly deps: ChannelDeps) {
    this.info = info;
    this.primary = new ConcurrencyLimiter(deps.settings.get().perInstancePrimarySlots);
    this.poller = new HealthPoller(() => this.health(CONTROL_TIMEOUT_MS));
  }

  get instanceId(): string {
    return this.info.instanceId;
  }

  /** The runtime home (ledger and job records of this instance live under it). */
  get home(): string {
    return this.deps.home;
  }

  get isRetired(): boolean {
    return this.retired;
  }

  get retiredSince(): number {
    return this.retiredAt;
  }

  /** Primary exchanges in flight (incl. limiter waits) — a retired channel is dropped only at 0. */
  get inFlight(): number {
    return this.pending;
  }

  limiterState(): { slots: number; active: number; waiting: number; stats: ConcurrencyLimiter["stats"] } {
    return { slots: this.primary.capacity, active: this.primary.active, waiting: this.primary.waiting, stats: this.primary.stats };
  }

  update(info: InstanceInfo): void {
    this.info = info;
    this.primary.setSlots(this.deps.settings.get().perInstancePrimarySlots);
  }

  /** The instance is gone: new calls get REVIT_EXITED; in-flight exchanges finish (writes reconcile). */
  retire(): void {
    if (this.retired) return;
    this.retired = true;
    this.retiredAt = Date.now();
    this.poller.stop();
    this.deps.log.info("channel", { change: "retired", instance: this.info.instanceId, inFlight: this.pending });
  }

  // ------------------------------------------------------------------------------------------- control pipe

  async control(op: ControlOp, payload: Record<string, unknown> = {}, timeoutMs = CONTROL_TIMEOUT_MS, clientKey = `broker#${process.pid}`): Promise<BridgeResponse> {
    const requestId = ulid();
    const release = await this.controlSlots.acquire(Math.max(50, Math.floor(timeoutMs / 2)));
    if (!release) return failure(requestId, "BRIDGE_BUSY", `Revit ${this.info.year} control pipe is busy`, { instance: String(this.info.year) });
    try {
      let authRetried = false;
      for (;;) {
        const auth = op === "hello" ? null : this.deps.auth.get();
        if (op !== "hello" && !auth) return failure(requestId, "AUTH_NOT_CONFIGURED", `no auth token yet (${this.deps.auth.file})`, { authFile: this.deps.auth.file });
        const request: BridgeRequest = {
          v: BRIDGE_PROTOCOL_VERSION,
          requestId,
          clientKey,
          ...(auth ? { auth } : {}),
          op,
          kind: "control",
          timeoutMs,
          doc: null,
          args: payload,
        };
        let ex: PipeExchange;
        try {
          ex = openExchange(this.info.controlPipe, request, { connectTimeoutMs: Math.min(3_000, timeoutMs) });
        } catch (error) {
          return failure(requestId, "INTERNAL_ERROR", (error as Error).message, null);
        }
        const outcome = await raceTimeout(ex.response, timeoutMs);
        if (outcome === "timeout") {
          ex.abandon("control timeout");
          return failure(requestId, "BRIDGE_BUSY", `Revit ${this.info.year} control pipe did not answer ${op} within ${timeoutMs} ms`, { instance: String(this.info.year), op });
        }
        if (outcome instanceof TransportError) return this.mapControlError(requestId, outcome);
        if (!outcome.ok && (outcome.code === "AUTH_MISMATCH" || outcome.code === "AUTH_REQUIRED") && !authRetried) {
          authRetried = true;
          if (this.deps.auth.reload()) continue;
        }
        return outcome;
      }
    } finally {
      release();
    }
  }

  hello(timeoutMs = CONTROL_TIMEOUT_MS): Promise<BridgeResponse> {
    return this.control("hello", {}, timeoutMs);
  }

  async health(timeoutMs = CONTROL_TIMEOUT_MS): Promise<HealthData | null> {
    const r = await this.control("health", {}, timeoutMs);
    return r.ok ? ((r.data as HealthData) ?? null) : null;
  }

  /** Fresh control snapshot (D2 §8.4: used before writes); updates the registry cache. */
  async snapshot(timeoutMs = CONTROL_TIMEOUT_MS): Promise<DocSnapshot | null> {
    const r = await this.control("snapshot", {}, timeoutMs);
    if (!r.ok || !r.data || typeof r.data !== "object" || (r.data as { unchanged?: boolean }).unchanged) return null;
    const snapshot = r.data as DocSnapshot;
    if (!Array.isArray(snapshot.docs)) return null;
    this.deps.registry.updateSnapshot(this.info.instanceId, snapshot);
    return snapshot;
  }

  private async mapControlError(requestId: string, error: TransportError): Promise<BridgeResponse> {
    const year = String(this.info.year);
    switch (error.code) {
      case "NOT_FOUND": {
        const lock = await lockState(this.lockPath());
        if (lock !== "held") {
          this.deps.registry.invalidate();
          return this.exitedFailure(requestId);
        }
        if (this.info.state === "starting") return failure(requestId, "REVIT_STARTING", `Revit ${year} is still starting`, { year: this.info.year });
        return failure(requestId, "ADDIN_PIPE_MISSING", `Revit ${year} (pid ${this.info.pid}) is running but the add-in control pipe is missing`, { year: this.info.year });
      }
      case "BUSY":
        return failure(requestId, "BRIDGE_BUSY", `Revit ${year} control pipe has no free instance`, { instance: year });
      case "ACCESS_DENIED":
        return failure(requestId, "BRIDGE_ACCESS_DENIED", `access to the Revit ${year} pipe was denied`, null);
      case "FRAME_TOO_LARGE":
        return failure(requestId, "RESPONSE_TOO_LARGE", error.message, null);
      case "BAD_RESPONSE":
        return failure(requestId, "ADDIN_OUTDATED", `Revit ${year} answered with an unexpected format: ${error.message}`, { year: this.info.year });
      default:
        return failure(requestId, "BRIDGE_BUSY", `Revit ${year} control pipe failed: ${error.message}`, { instance: year });
    }
  }

  // ------------------------------------------------------------------------------------------- primary pipe

  async send(request: BridgeRequest, o: SendOptions): Promise<SendResult> {
    if (this.retired) return { response: this.exitedFailure(request.requestId, request.doc) };
    if (o.coalesceKey && request.kind === "read") {
      // Only a client cancellation detaches a subscriber; the deadline itself ends the shared call (READ_STILL_RUNNING).
      const cancel = clientCancelSignal(o.deadline);
      try {
        const { value, coalesced } = await this.coalescer.run(
          o.coalesceKey,
          (shared) => this.sendTracked(request, { ...o, deadline: Deadline.start(o.deadline.remaining(), shared) }),
          cancel
        );
        if (coalesced) {
          this.stats.coalesced += 1;
          this.deps.log.annotate(request.requestId, { kind: request.kind, op: request.op, coalesced: true, sharedReqId: value.response.requestId || undefined });
        }
        return { ...value, coalesced };
      } catch {
        return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled", null) };
      }
    }
    return this.sendTracked(request, o);
  }

  /** Writes register for the read-after-write barrier while their call is outstanding. */
  private async sendTracked(request: BridgeRequest, o: SendOptions): Promise<SendResult> {
    if (!isApplyWrite(request)) {
      await this.barrier(request, o);
      return this.sendLimited(request, o);
    }
    let finish!: () => void;
    const entry: OutstandingWrite = {
      requestId: request.requestId,
      clientKey: request.clientKey,
      docKey: request.doc?.key ?? null,
      writeTag: request.writeTag,
      done: new Promise<void>((resolve) => (finish = resolve)),
    };
    this.outstanding.set(request.requestId, entry);
    try {
      return await this.sendLimited(request, o);
    } finally {
      this.outstanding.delete(request.requestId);
      finish();
    }
  }

  /**
   * Read-after-write barrier (SPEC §8.8, D2 §16.2): a read of this session on the same doc waits for this session's
   * outstanding write calls (sent earlier) to finish, within its budget, so it reflects the write. A write that became
   * a job or returned WRITE_STILL_RUNNING is no longer outstanding: reads then fail fast (rule 1).
   */
  private async barrier(request: BridgeRequest, o: SendOptions): Promise<void> {
    const docKey = request.doc?.key ?? null;
    if (!docKey || request.kind !== "read") return;
    const waits = [...this.outstanding.values()].filter((w) => w.clientKey === request.clientKey && w.docKey === docKey);
    if (waits.length === 0) return;
    this.stats.barrierWaits += 1;
    o.progress?.(`waiting for your write ${waits.map((w) => w.writeTag).filter(Boolean).join(", ") || "in flight"}`);
    const budget = Math.max(0, o.deadline.remaining() - RETRY_MIN_REMAINING_MS);
    await Promise.race([Promise.all(waits.map((w) => w.done)), o.deadline.sleep(budget)]);
  }

  private sameDocOwnWrite(request: BridgeRequest, o: SendOptions, item: ExecutingItem): boolean {
    if (!o.session.isOwnOutstandingWrite(item)) return false;
    const tracked = this.outstanding.get(item.requestId);
    if (!tracked) return true; // the session says so; the doc is unknown here
    return !request.doc?.key || tracked.docKey === request.doc.key;
  }

  private async sendLimited(request: BridgeRequest, o: SendOptions): Promise<SendResult> {
    const t0 = Date.now();
    const originalId = request.requestId;
    this.pending += 1;
    try {
      const maxWait = ConcurrencyLimiter.maxWaitMs(o.deadline);
      const release = await this.primary.acquire(maxWait, clientCancelSignal(o.deadline));
      if (!release) {
        if (o.deadline.aborted) return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled; nothing was sent", null), limiterMs: Date.now() - t0 };
        return { response: this.saturated(request, "the limiter"), limiterMs: Date.now() - t0 };
      }
      try {
        // Admission: do not connect while the add-in has no waiting listener or a full queue (health ≤1 s old).
        const admitted = await this.waitAdmission(o, t0 + maxWait);
        if (!admitted) return { response: this.saturated(request, "the add-in queue"), limiterMs: Date.now() - t0 };
        const limiterMs = Date.now() - t0;
        this.stats.sent += 1;
        const result = await this.attempts(request, o);
        this.annotate(originalId, request, result, limiterMs);
        return { ...result, limiterMs };
      } finally {
        release();
      }
    } finally {
      this.pending -= 1;
    }
  }

  private async waitAdmission(o: SendOptions, until: number): Promise<boolean> {
    for (;;) {
      const latest = this.poller.latest();
      const h = latest && Date.now() - latest.at <= HEALTH_FRESH_MS ? latest.data : null;
      const full = !!h && ((h.listeners !== undefined && h.listeners.primaryWaiting === 0) || (h.queue?.pending ?? 0) >= ADDIN_MAX_PENDING);
      if (!full) return true;
      if (Date.now() >= until || o.deadline.expired) return false;
      o.progress?.(`Revit ${this.info.year} is saturated (${h!.queue?.pending ?? 0} queued); waiting for a free slot`);
      if (!(await o.deadline.sleep(Math.min(250, Math.max(1, until - Date.now()))))) return false;
    }
  }

  /** BRIDGE_BUSY with the D2 §11.3 details: mine, others by client, the executing op and the add-in queue. */
  private saturated(request: BridgeRequest, where: string): BridgeResponse {
    this.stats.bridgeBusy += 1;
    const latest = this.poller.latest();
    const h = latest && Date.now() - latest.at <= 5_000 ? latest.data : null;
    const mine = this.primary.active + this.primary.waiting;
    const byClient = h?.queue?.byClient ?? {};
    const others = Object.entries(byClient).filter(([k]) => k !== request.clientKey).reduce((n, [, v]) => n + (Number(v) || 0), 0);
    const exec = h?.queue?.executing ?? null;
    const execText = exec ? `; executing ${exec.op} for ${Math.round((exec.elapsedMs ?? 0) / 1000)} s` : "";
    return failure(
      request.requestId,
      "BRIDGE_BUSY",
      `Revit ${this.info.year} is saturated (${where}): ${mine} call${mine === 1 ? "" : "s"} of this session${others ? ` and ${others} from other agent sessions` : ""} are waiting or running${execText}; nothing was sent`,
      {
        instance: String(this.info.year),
        mine,
        others: Object.keys(byClient).length ? Object.fromEntries(Object.entries(byClient).filter(([k]) => k !== request.clientKey)) : undefined,
        executingOp: exec?.op,
        executingForMs: exec?.elapsedMs,
        pending: h?.queue?.pending,
      }
    );
  }

  private async attempts(request: BridgeRequest, o: SendOptions): Promise<SendResult> {
    let firstMissingAt: number | null = null;
    let retried = false;
    let authRetried = false;
    const retryable = !isApplyWrite(request);
    for (;;) {
      if (o.deadline.aborted) return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled; nothing was sent", null) };
      if (o.deadline.expired) return { response: failure(request.requestId, "BRIDGE_BUSY", `Revit ${this.info.year} could not be reached within the time budget; nothing was sent`, { instance: String(this.info.year) }) };
      const auth = this.deps.auth.get();
      if (!auth) return { response: failure(request.requestId, "AUTH_NOT_CONFIGURED", `no auth token yet: Revit has not created ${this.deps.auth.file}`, { authFile: this.deps.auth.file }) };
      request.auth = auth;
      request.timeoutMs = Math.max(1_000, o.deadline.remaining() - BRIDGE_MARGIN_MS);
      let ex: PipeExchange;
      try {
        ex = openExchange(this.info.pipe, request);
      } catch (error) {
        const message = error instanceof TransportError ? error.message : String(error);
        return { response: failure(request.requestId, "INVALID_ARGS", `the arguments are too large to send (${message})`, { reason: "too large" }) };
      }
      const outcome = await this.waitExchange(ex, request, o);
      switch (outcome.kind) {
        case "response": {
          const r = outcome.response;
          if (!r.ok && (r.code === "AUTH_MISMATCH" || r.code === "AUTH_REQUIRED") && !authRetried) {
            // Auth is checked before the ledger or queue is touched, so a resend is safe (D2 §3.2).
            authRetried = true;
            if (this.deps.auth.reload()) {
              request.requestId = ulid();
              continue;
            }
          }
          return { response: r, retried: retried || authRetried };
        }
        case "busy":
          this.stats.busyFailFast += 1;
          return { response: outcome.response };
        case "deadline":
          return this.atDeadline(ex, request, o, outcome.classifier);
        case "aborted":
          return this.onAbort(ex, request);
        case "error": {
          const e = outcome.error;
          if (e.code === "NOT_FOUND") {
            firstMissingAt ??= Date.now();
            if (Date.now() - firstMissingAt < MISSING_PIPE_GRACE_MS) {
              await o.deadline.sleep(Math.min(250, 25 * 2 ** Math.min(4, Math.floor((Date.now() - firstMissingAt) / 200))));
              continue;
            }
            const lock = await lockState(this.lockPath());
            if (lock !== "held") {
              this.deps.registry.invalidate();
              return { response: this.exitedFailure(request.requestId, request.doc) };
            }
            if (this.info.state === "starting") {
              o.progress?.(`Revit ${this.info.year} is still starting`);
              if (await o.deadline.sleep(1_000)) continue;
              return { response: failure(request.requestId, "REVIT_STARTING", `Revit ${this.info.year} is still starting`, { year: this.info.year }) };
            }
            return { response: failure(request.requestId, "ADDIN_PIPE_MISSING", `Revit ${this.info.year} (pid ${this.info.pid}) is running but the add-in pipe is missing (its pipe host failed)`, { year: this.info.year }) };
          }
          if (e.code === "BUSY") {
            this.stats.bridgeBusy += 1;
            return { response: failure(request.requestId, "BRIDGE_BUSY", `every Revit ${this.info.year} pipe instance is busy; nothing was sent`, { instance: String(this.info.year) }) };
          }
          if (e.code === "ACCESS_DENIED") return { response: failure(request.requestId, "BRIDGE_ACCESS_DENIED", `access to the Revit ${this.info.year} pipe was denied`, null) };
          if (e.code === "FRAME_TOO_LARGE") return { response: failure(request.requestId, "RESPONSE_TOO_LARGE", e.message, null) };
          if (!e.written) {
            // The request never reached the add-in: retry within the budget.
            if (await o.deadline.sleep(100)) continue;
            return { response: failure(request.requestId, "BRIDGE_BUSY", `Revit ${this.info.year} closed the pipe before the request was sent; nothing was sent`, { instance: String(this.info.year) }) };
          }
          if (!retryable) return this.reconcile(request, o);
          if (e.code === "BAD_RESPONSE") return { response: failure(request.requestId, "ADDIN_OUTDATED", `Revit ${this.info.year} answered with an unexpected format: ${e.message}`, { year: this.info.year }) };
          const lock = await lockState(this.lockPath());
          if (lock !== "held") {
            this.deps.registry.invalidate();
            return { response: this.exitedFailure(request.requestId, request.doc) };
          }
          // One auto-retry for reads and previews (D2 §11.6), never after a timeout and only with ≥5 s left.
          if (!retried && o.deadline.remaining() >= RETRY_MIN_REMAINING_MS) {
            retried = true;
            this.stats.retried += 1;
            this.deps.log.info("retry", { reqId: request.requestId, op: request.op, inst: this.info.instanceId, reason: e.message });
            request.requestId = ulid();
            continue;
          }
          return { response: failure(request.requestId, "INTERNAL_ERROR", `Revit ${this.info.year} closed the pipe before answering (${e.message})`, { requestId: request.requestId }) };
        }
      }
    }
  }

  private waitExchange(ex: PipeExchange, request: BridgeRequest, o: SendOptions): Promise<Outcome> {
    const settings = this.deps.settings.get();
    const classifier = new BusyClassifier({
      requestId: request.requestId,
      clientKey: request.clientKey,
      year: this.info.year,
      instanceId: this.info.instanceId,
      stall: settings.stall,
      refOf: (item) => o.session.refOf(item),
      isOwnOutstandingWrite: (item) => this.sameDocOwnWrite(request, o, item),
    });
    return new Promise<Outcome>((resolve) => {
      let settled = false;
      let unsubscribe: (() => void) | null = null;
      let deciding = false;
      let lastProgress = "";
      let lastProgressAt = 0;
      const timer = setTimeout(() => finish({ kind: "deadline", classifier }), Math.max(1, o.deadline.remaining()));
      const onAbort = () => {
        if (o.deadline.aborted) finish({ kind: "aborted" });
      };
      const finish = (outcome: Outcome) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        unsubscribe?.();
        o.deadline.signal.removeEventListener("abort", onAbort);
        resolve(outcome);
      };
      o.deadline.signal.addEventListener("abort", onAbort);
      // While a fail-fast decision cancels the queued item, the add-in answers that cancel with REQUEST_CANCELLED on
      // this exchange: hold it, so the call reports the reason (REVIT_DIALOG_OPEN, REVIT_BUSY...) instead of a bare
      // cancel (seen live in e2e R7).
      let heldCancel: BridgeResponse | null = null;
      ex.response.then(
        (response) => {
          if (deciding && !response.ok && response.code === "REQUEST_CANCELLED") {
            heldCancel = response;
            return;
          }
          finish({ kind: "response", response });
        },
        (error) => finish({ kind: "error", error: error instanceof TransportError ? error : new TransportError("DISCONNECTED", String(error), "read", ex.written) })
      );
      unsubscribe = this.poller.subscribe((sample) => {
        if (settled || deciding || !ex.written) return;
        const decision = classifier.push(sample);
        if (decision.action === "wait") {
          // Progress at least every 3 s while waiting (D2 §12.4).
          const now = Date.now();
          const text = decision.progress ?? (sample.data ? "queued in Revit" : "");
          if (text && (text !== lastProgress || now - lastProgressAt >= 3_000)) {
            lastProgress = text;
            lastProgressAt = now;
            o.progress?.(text);
          }
          return;
        }
        deciding = true;
        void this.failFast(ex, request, decision).then((response) => {
          deciding = false;
          if (settled) return;
          if (!response) {
            if (heldCancel) finish({ kind: "response", response: heldCancel });
            return;
          }
          ex.abandon(`busy: ${decision.code}`);
          finish({ kind: "busy", response });
        });
      });
    });
  }

  /**
   * Cancel the queued item first so "nothing ran" is true (§8.8). Returns the error to answer with, or null to keep
   * waiting (the item already started, or a write whose cancel could not be confirmed: never drop a write).
   */
  private async failFast(ex: PipeExchange, request: BridgeRequest, decision: BusyDecision & { action: "fail" }): Promise<BridgeResponse | null> {
    const cancelled = await this.cancelQueued(request);
    if (cancelled === "running_write" || cancelled === "cooperative") return null; // it started; keep waiting
    if (isApplyWrite(request) && cancelled !== "queued") return null; // unconfirmed: retry the cancel on the next sample
    if (cancelled === "not_found") {
      // Possibly finished and answering now: give the response a moment.
      await new Promise((r) => setTimeout(r, 300).unref());
    }
    let details = decision.details;
    if (decision.code === "REVIT_DIALOG_OPEN") details = await this.dialogDetails(details);
    this.deps.log.info("fail_fast", { reqId: request.requestId, op: request.op, inst: this.info.instanceId, code: decision.code, rule: decision.rule, cancelled });
    void ex;
    return failure(request.requestId, decision.code, decision.message, details);
  }

  /** Enrich REVIT_DIALOG_OPEN with the dialog's title, text, id and buttons (control `dialogs`, no UI thread). */
  private async dialogDetails(details: Record<string, unknown>): Promise<Record<string, unknown>> {
    const r = await this.control("dialogs", {}, 2_500);
    if (!r.ok || !Array.isArray(r.data) || r.data.length === 0) return details;
    const dialogs = (r.data as DialogInfo[]).filter((d) => !d.ours);
    const d = dialogs.find((x) => x.title === details.title) ?? dialogs[0] ?? (r.data as DialogInfo[])[0]!;
    return {
      ...details,
      title: d.title || details.title,
      text: d.text ? d.text.slice(0, 400) : details.text,
      dialogId: d.dialogId ?? details.dialogId,
      dialog: d.dialog,
      buttons: (d.buttons ?? []).map((b) => b.name).slice(0, 12),
    };
  }

  private async cancelQueued(request: BridgeRequest): Promise<string> {
    const r = await this.control("cancel_request", { requestId: request.requestId }, CONTROL_TIMEOUT_MS, request.clientKey);
    if (!r.ok) return "unknown";
    const cancelled = (r.data as { cancelled?: string } | null)?.cancelled;
    return typeof cancelled === "string" ? cancelled : "unknown";
  }

  private async executingRequestId(): Promise<string | null> {
    const latest = this.poller.latest();
    if (latest && Date.now() - latest.at < 1_500 && latest.data) return latest.data.queue?.executing?.requestId ?? null;
    const h = await this.health(500);
    return h?.queue?.executing?.requestId ?? null;
  }

  private async atDeadline(ex: PipeExchange, request: BridgeRequest, o: SendOptions, classifier: BusyClassifier): Promise<SendResult> {
    const year = this.info.year;
    if (!ex.written) {
      ex.abandon("budget");
      return { response: failure(request.requestId, "BRIDGE_BUSY", `Revit ${year} could not take the request within the time budget; nothing was sent`, { instance: String(year) }) };
    }
    const executing = (await this.executingRequestId()) === request.requestId;
    if (!isApplyWrite(request)) {
      if (!executing) {
        const cancelled = await this.cancelQueued(request);
        if (cancelled !== "cooperative") {
          ex.abandon("budget");
          const d = classifier.atBudget();
          return { response: failure(request.requestId, d.code, d.message, d.details) };
        }
      }
      const late = this.watchLate(ex, request, LATE_READ_MAX_MS);
      return {
        response: failure(request.requestId, "READ_STILL_RUNNING", `the read is still running in Revit ${year}; its result is kept for 10 minutes`, { instance: String(year) }),
        late,
      };
    }
    if (!executing) {
      const cancelled = await this.cancelQueued(request);
      if (cancelled === "queued") {
        ex.abandon("budget");
        const d = classifier.atBudget();
        return { response: failure(request.requestId, d.code, d.message, d.details) };
      }
    }
    const late = this.watchLate(ex, request, LATE_WRITE_MAX_MS);
    return {
      response: failure(request.requestId, "WRITE_STILL_RUNNING", `the write is still running in Revit ${year}`, { ref: request.writeTag, instance: String(year) }),
      late,
    };
  }

  private async onAbort(ex: PipeExchange, request: BridgeRequest): Promise<SendResult> {
    if (!ex.written) {
      ex.abandon("cancelled");
      return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled; nothing was sent", null) };
    }
    const cancelled = await this.cancelQueued(request);
    if (isApplyWrite(request) && cancelled !== "queued") {
      const late = this.watchLate(ex, request, LATE_WRITE_MAX_MS);
      return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled, but the write was already running; its outcome is recorded", { ref: request.writeTag }), late };
    }
    ex.abandon("cancelled");
    return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled", null) };
  }

  /** Keep reading a detached exchange; log `late_result` when it lands (D2 §14.3). */
  private watchLate(ex: PipeExchange, request: BridgeRequest, maxMs: number): Promise<BridgeResponse> {
    this.stats.late += 1;
    const started = Date.now();
    const late = ex.detach(maxMs);
    late.then(
      (r) => this.deps.log.info("late_result", { reqId: request.requestId, op: request.op, inst: this.info.instanceId, w: request.writeTag, ok: r.ok, code: r.code ?? undefined, afterMs: Date.now() - started }),
      (error) => this.deps.log.info("late_result", { reqId: request.requestId, op: request.op, inst: this.info.instanceId, w: request.writeTag, lost: (error as Error)?.message ?? String(error) })
    );
    return late;
  }

  /** The write's response was lost: ask the add-in ledger until the deadline (D2 §13.2). */
  private async reconcile(request: BridgeRequest, o: SendOptions): Promise<SendResult> {
    const ref = request.writeTag;
    let notFoundSince: number | null = null;
    let polls = 0;
    while (!o.deadline.expired) {
      polls += 1;
      const r = await this.control("get_request_result", { requestId: request.requestId }, CONTROL_TIMEOUT_MS, request.clientKey);
      if (r.ok && r.data && typeof r.data === "object") {
        const entry = r.data as LedgerEntry & { found?: boolean };
        if (entry.found === false || !entry.state) {
          // No ledger entry: the add-in never accepted the request (entries are recorded at acceptance).
          notFoundSince ??= Date.now();
          if (Date.now() - notFoundSince >= NOT_RECEIVED_AFTER_MS && polls >= 3)
            return { response: failure(request.requestId, "BRIDGE_BUSY", `Revit ${this.info.year} did not receive ${ref ?? "the write"} (no ledger entry); nothing ran`, { instance: String(this.info.year), ref }) };
        } else {
          notFoundSince = null;
          if (isTerminalLedgerState(entry.state)) {
            this.stats.recovered += 1;
            return { response: recoveredResponse(request, entry, "BRIDGE_RESPONSE_RECOVERED", "the response was lost on the pipe and recovered from the add-in ledger"), recovered: true };
          }
          o.progress?.(`${ref ?? "the write"} is ${entry.state} in Revit ${this.info.year}`);
        }
      } else if (!r.ok && (r.code === "REVIT_EXITED" || r.code === "ADDIN_PIPE_MISSING")) {
        break;
      }
      if (!(await o.deadline.sleep(RECONCILE_POLL_MS))) break;
    }
    const lock = await lockState(this.lockPath());
    if (lock !== "held") {
      this.deps.registry.invalidate();
      const entry = await searchLedgers(this.deps.home, request.requestId, this.info.instanceId);
      if (entry && isTerminalLedgerState(entry.state)) {
        this.stats.recovered += 1;
        return {
          response: recoveredResponse(request, entry, "BRIDGE_RESPONSE_RECOVERED", `Revit ${this.info.year} exited after the write ended ${entry.state}; not saved unless the doc was saved afterwards`),
          recovered: true,
        };
      }
      return { response: failure(request.requestId, "WRITE_OUTCOME_UNKNOWN", `Revit ${this.info.year} (pid ${this.info.pid}) exited while applying ${ref ?? "the write"}`, { ref, year: this.info.year, pid: this.info.pid }) };
    }
    return { response: failure(request.requestId, "WRITE_STILL_RUNNING", `the write is still running in Revit ${this.info.year}`, { ref, instance: String(this.info.year) }) };
  }

  /** One merged log line per call: transport metrics joined into the framework's `call` line by requestId. */
  private annotate(originalId: string, request: BridgeRequest, result: SendResult, limiterMs: number): void {
    const m = result.response.metrics;
    this.deps.log.annotate(originalId, {
      reqIdFinal: request.requestId !== originalId ? request.requestId : undefined,
      kind: request.kind,
      op: request.op,
      w: request.writeTag,
      limiterMs,
      queueWaitMs: m?.queueWaitMs,
      raiseToExecMs: m?.raiseToExecMs,
      execMs: m?.execMs,
      via: m?.via,
      code: result.response.ok ? undefined : result.response.code,
      retried: result.retried || undefined,
      recovered: result.recovered || undefined,
      partial: result.response.partial ? result.response.partial.reason : undefined,
      late: result.late ? true : undefined,
    });
  }

  private lockPath(): string {
    return join(this.deps.registry.dir, `${this.info.instanceId}.lock`);
  }

  /** REVIT_EXITED naming the request's target doc first (the fix reopens that one), else the first doc it had open. */
  private exitedFailure(requestId: string, target?: { key: string } | null): BridgeResponse {
    const all = this.info.snapshot?.docs ?? [];
    const first = target ? all.find((d) => d.key === target.key) : undefined;
    const docs = [...(first ? [first] : []), ...all.filter((d) => d !== first)].map((d) => d.title);
    const at = this.deps.registry.exitOf(this.info.instanceId)?.at;
    return failure(requestId, "REVIT_EXITED", `Revit ${this.info.year} (pid ${this.info.pid})${docs[0] ? `, which had ${docs[0]} open,` : ""} exited${at ? ` at ${new Date(at).toLocaleTimeString("en-GB")}` : ""}`, { year: this.info.year, pid: this.info.pid, doc: docs[0] });
  }
}

/** A signal that aborts only on client cancellation (not when the budget ends). */
function clientCancelSignal(deadline: Deadline): AbortSignal {
  const controller = new AbortController();
  if (deadline.aborted) controller.abort();
  else
    deadline.signal.addEventListener(
      "abort",
      () => {
        if (deadline.aborted) controller.abort();
      },
      { once: true }
    );
  return controller.signal;
}

function recoveredResponse(request: BridgeRequest, entry: LedgerEntry, code: string, text: string): BridgeResponse {
  const base: BridgeResponse =
    entry.response && typeof entry.response === "object"
      ? { ...entry.response }
      : {
          v: BRIDGE_PROTOCOL_VERSION,
          requestId: request.requestId,
          ok: entry.state === "committed",
          code: entry.state === "committed" ? null : entry.state === "cancelled" ? "REQUEST_CANCELLED" : "REVIT_TRANSACTION_ROLLED_BACK",
          message: entry.state === "committed" ? null : `the write ended ${entry.state}`,
          details: entry.state === "committed" ? null : { failures: [] },
          summary: entry.state === "committed" ? `committed ${request.op}${entry.counts ? ` (created ${entry.counts.created}, modified ${entry.counts.modified}, deleted ${entry.counts.deleted})` : ""}` : null,
        };
  base.warnings = [...(base.warnings ?? []), { code, text }];
  return base;
}

async function raceTimeout<T>(promise: Promise<T>, ms: number): Promise<T | "timeout" | TransportError> {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve("timeout"), Math.max(1, ms));
    timer.unref();
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error) => {
        clearTimeout(timer);
        resolve(error instanceof TransportError ? error : new TransportError("DISCONNECTED", String(error), "read", false));
      }
    );
  });
}

/** Channels for live instances; channels of instances that went away are retired, not disposed (D2 §11.1). */
export class ChannelPool {
  private readonly channels = new Map<string, InstanceChannel>();
  /** Retired channels that still have work in flight (dropped when idle). */
  private readonly retiring = new Set<InstanceChannel>();

  constructor(private readonly deps: ChannelDeps) {
    // Retire as soon as the registry sees an instance go (lock released), not only at the next resolve.
    deps.registry.onChange((event) => {
      if (event.type !== "down") return;
      const channel = this.channels.get(event.info.instanceId);
      if (!channel) return;
      channel.retire();
      this.channels.delete(event.info.instanceId);
      if (channel.inFlight > 0) this.retiring.add(channel);
    });
  }

  get(info: InstanceInfo): InstanceChannel {
    let channel = this.channels.get(info.instanceId);
    if (!channel || channel.isRetired) {
      if (channel?.isRetired && channel.inFlight > 0) this.retiring.add(channel);
      channel = new InstanceChannel(info, this.deps);
      this.channels.set(info.instanceId, channel);
    } else {
      channel.update(info);
    }
    return channel;
  }

  peek(instanceId: string): InstanceChannel | undefined {
    return this.channels.get(instanceId);
  }

  /** Retire channels of instances that are no longer live; drop retired channels once idle. */
  sync(live: InstanceInfo[]): void {
    const ids = new Set(live.map((i) => i.instanceId));
    for (const [id, channel] of this.channels) {
      if (!ids.has(id)) {
        channel.retire();
        if (channel.inFlight === 0) this.channels.delete(id);
        else {
          this.retiring.add(channel);
          this.channels.delete(id);
        }
      }
    }
    for (const channel of this.retiring) if (channel.inFlight === 0) this.retiring.delete(channel);
  }

  all(): InstanceChannel[] {
    return [...this.channels.values()];
  }

  retiredInFlight(): number {
    let n = 0;
    for (const c of this.retiring) n += c.inFlight;
    return n;
  }

  retireAll(): void {
    for (const channel of this.channels.values()) channel.retire();
  }
}
