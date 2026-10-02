// One channel per live instance (D2 §11.2): primary/control pipe exchanges, the concurrency limiter, the shared
// HealthPoller, busy classification with fail-fast (SPEC §8.8), read coalescing, the D2 §11.4 error mapping and
// write reconciliation bounded by the deadline (§8.10). Basic implementation; P-REL-BROKER owns hardening.

import { join } from "node:path";
import {
  BRIDGE_PROTOCOL_VERSION,
  type BridgeRequest,
  type BridgeResponse,
  type ControlOp,
  type DocSnapshot,
  type ExecutingItem,
  type HealthData,
  type LedgerEntry,
} from "@revit-mcp-next/contracts/protocol";
import type { Job, Kind } from "@revit-mcp-next/contracts/catalog";
import { isTerminalLedgerState } from "@revit-mcp-next/contracts/protocol";
import { BusyClassifier } from "../ipc/BusyClassifier.js";
import { ConcurrencyLimiter } from "../ipc/ConcurrencyLimiter.js";
import { failure, TransportError } from "../ipc/errors.js";
import { HealthPoller } from "../ipc/HealthPoller.js";
import { openExchange, type PipeExchange } from "../ipc/PipeClient.js";
import { ReadCoalescer } from "../ipc/ReadCoalescer.js";
import type { AuthStore } from "../runtime/auth.js";
import { BRIDGE_MARGIN_MS, type Deadline } from "../runtime/deadline.js";
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
  /** READ_STILL_RUNNING / WRITE_STILL_RUNNING: the exchange keeps reading; resolves with the late response. */
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

const WRITE_KINDS: ReadonlySet<Kind> = new Set(["write", "lifecycle", "code"]);
const LATE_READ_MAX_MS = 10 * 60_000;
const LATE_WRITE_MAX_MS = 30 * 60_000;
const MISSING_PIPE_GRACE_MS = 1_500;
const CONTROL_TIMEOUT_MS = 800;

export function isWriteKind(kind: Kind): boolean {
  return WRITE_KINDS.has(kind);
}

export class InstanceChannel {
  info: InstanceInfo;
  private readonly primary: ConcurrencyLimiter;
  private readonly controlSlots = new ConcurrencyLimiter(2);
  readonly poller: HealthPoller;
  private readonly coalescer = new ReadCoalescer<SendResult>();
  private retired = false;
  private pending = 0;

  constructor(info: InstanceInfo, private readonly deps: ChannelDeps) {
    this.info = info;
    this.primary = new ConcurrencyLimiter(deps.settings.get().perInstancePrimarySlots);
    this.poller = new HealthPoller(() => this.health(CONTROL_TIMEOUT_MS));
  }

  get instanceId(): string {
    return this.info.instanceId;
  }

  get isRetired(): boolean {
    return this.retired;
  }

  get inFlight(): number {
    return this.pending;
  }

  update(info: InstanceInfo): void {
    this.info = info;
    this.primary.setSlots(this.deps.settings.get().perInstancePrimarySlots);
  }

  /** The instance is gone: new calls get REVIT_EXITED; in-flight exchanges finish (writes reconcile). */
  retire(): void {
    this.retired = true;
    this.poller.stop();
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
        if (!outcome.ok && outcome.code === "AUTH_MISMATCH" && !authRetried) {
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
          return failure(requestId, "REVIT_EXITED", `Revit ${year} (pid ${this.info.pid}) exited`, { year: this.info.year, pid: this.info.pid });
        }
        if (this.info.state === "starting") return failure(requestId, "REVIT_STARTING", `Revit ${year} is still starting`, { year: this.info.year });
        return failure(requestId, "ADDIN_PIPE_MISSING", `Revit ${year} is running but the add-in control pipe is missing`, { year: this.info.year });
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
    if (this.retired) return { response: this.exitedFailure(request.requestId) };
    if (o.coalesceKey && request.kind === "read") {
      try {
        const { value, coalesced } = await this.coalescer.run(o.coalesceKey, () => this.sendLimited(request, o), o.deadline.signal);
        return { ...value, coalesced };
      } catch {
        return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled", null) };
      }
    }
    return this.sendLimited(request, o);
  }

  private async sendLimited(request: BridgeRequest, o: SendOptions): Promise<SendResult> {
    const t0 = Date.now();
    const release = await this.primary.acquire(ConcurrencyLimiter.maxWaitMs(o.deadline), o.deadline.signal);
    const limiterMs = Date.now() - t0;
    if (!release) {
      if (o.deadline.aborted) return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled", null) };
      return {
        response: failure(
          request.requestId,
          "BRIDGE_BUSY",
          `Revit ${this.info.year} is saturated: ${this.primary.active} calls of this session are running and ${this.primary.waiting} waiting; nothing was sent`,
          { instance: String(this.info.year), mine: this.primary.active + this.primary.waiting }
        ),
        limiterMs,
      };
    }
    this.pending += 1;
    try {
      const result = await this.attempts(request, o);
      return { ...result, limiterMs };
    } finally {
      this.pending -= 1;
      release();
    }
  }

  private async attempts(request: BridgeRequest, o: SendOptions): Promise<SendResult> {
    let firstMissingAt: number | null = null;
    let retriedRead = false;
    let authRetried = false;
    for (;;) {
      if (o.deadline.aborted) return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled", null) };
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
          if (!r.ok && r.code === "AUTH_MISMATCH" && !authRetried) {
            authRetried = true;
            if (this.deps.auth.reload()) continue;
          }
          return { response: r, retried: retriedRead || authRetried };
        }
        case "busy":
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
              return { response: this.exitedFailure(request.requestId) };
            }
            if (this.info.state === "starting") {
              o.progress?.(`Revit ${this.info.year} is still starting`);
              if (await o.deadline.sleep(1_000)) continue;
              return { response: failure(request.requestId, "REVIT_STARTING", `Revit ${this.info.year} is still starting`, { year: this.info.year }) };
            }
            return { response: failure(request.requestId, "ADDIN_PIPE_MISSING", `Revit ${this.info.year} is running but the add-in pipe is missing (its pipe host failed)`, { year: this.info.year }) };
          }
          if (e.code === "BUSY")
            return { response: failure(request.requestId, "BRIDGE_BUSY", `every Revit ${this.info.year} pipe instance is busy; nothing was sent`, { instance: String(this.info.year) }) };
          if (e.code === "ACCESS_DENIED")
            return { response: failure(request.requestId, "BRIDGE_ACCESS_DENIED", `access to the Revit ${this.info.year} pipe was denied`, null) };
          if (e.code === "FRAME_TOO_LARGE") return { response: failure(request.requestId, "RESPONSE_TOO_LARGE", e.message, null) };
          if (!e.written) {
            if (await o.deadline.sleep(100)) continue;
            return { response: failure(request.requestId, "BRIDGE_BUSY", `Revit ${this.info.year} closed the pipe before the request was sent; nothing was sent`, { instance: String(this.info.year) }) };
          }
          if (isWriteKind(request.kind)) return this.reconcile(request, o);
          if (e.code === "BAD_RESPONSE")
            return { response: failure(request.requestId, "ADDIN_OUTDATED", `Revit ${this.info.year} answered with an unexpected format: ${e.message}`, { year: this.info.year }) };
          if (!retriedRead && o.deadline.remaining() >= 5_000) {
            retriedRead = true;
            request.requestId = ulid();
            continue;
          }
          const lock = await lockState(this.lockPath());
          if (lock !== "held") return { response: this.exitedFailure(request.requestId) };
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
      isOwnOutstandingWrite: (item) => o.session.isOwnOutstandingWrite(item),
    });
    return new Promise<Outcome>((resolve) => {
      let settled = false;
      let unsubscribe: (() => void) | null = null;
      let deciding = false;
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
      ex.response.then(
        (response) => finish({ kind: "response", response }),
        (error) => finish({ kind: "error", error: error instanceof TransportError ? error : new TransportError("DISCONNECTED", String(error), "read", ex.written) })
      );
      unsubscribe = this.poller.subscribe((sample) => {
        if (settled || deciding || !ex.written) return;
        const decision = classifier.push(sample);
        if (decision.action === "wait") {
          if (decision.progress) o.progress?.(decision.progress);
          return;
        }
        deciding = true;
        void this.cancelQueued(request).then(async (cancelled) => {
          deciding = false;
          if (settled) return;
          if (cancelled === "running_write" || cancelled === "cooperative") return; // it started; keep waiting
          if (cancelled === "not_found") {
            // Possibly finished and answering now: give the response a moment before giving up.
            await new Promise((r) => setTimeout(r, 300).unref());
            if (settled) return;
            if (isWriteKind(request.kind)) return; // never drop a write's response; the deadline path reconciles
          }
          ex.abandon(`busy: ${decision.code}`);
          finish({ kind: "busy", response: failure(request.requestId, decision.code, decision.message, decision.details) });
        });
      });
    });
  }

  private async cancelQueued(request: BridgeRequest): Promise<string> {
    const r = await this.control("cancel_request", { requestId: request.requestId }, CONTROL_TIMEOUT_MS, request.clientKey);
    if (!r.ok) return "unknown";
    const cancelled = (r.data as { cancelled?: string } | null)?.cancelled;
    return typeof cancelled === "string" ? cancelled : "unknown";
  }

  private async executingRequestId(): Promise<string | null> {
    const latest = this.poller.latest();
    if (latest && Date.now() - latest.at < 1_500) return latest.data?.queue?.executing?.requestId ?? null;
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
    if (!isWriteKind(request.kind)) {
      if (!executing) {
        const cancelled = await this.cancelQueued(request);
        if (cancelled !== "cooperative") {
          ex.abandon("budget");
          const d = classifier.atBudget();
          return { response: d.action === "fail" ? failure(request.requestId, d.code, d.message, d.details) : failure(request.requestId, "REVIT_BUSY", `Revit ${year} did not pick up the request; nothing ran`, { reason: "not picked up" }) };
        }
      }
      const late = ex.detach(LATE_READ_MAX_MS);
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
        return { response: d.action === "fail" ? failure(request.requestId, d.code, d.message, d.details) : failure(request.requestId, "REVIT_BUSY", `Revit ${year} did not pick up the write; nothing ran`, { reason: "not picked up" }) };
      }
    }
    const late = ex.detach(LATE_WRITE_MAX_MS);
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
    if (isWriteKind(request.kind) && cancelled !== "queued") {
      const late = ex.detach(LATE_WRITE_MAX_MS);
      return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled, but the write was already running; its outcome is recorded", { ref: request.writeTag }), late };
    }
    ex.abandon("cancelled");
    return { response: failure(request.requestId, "REQUEST_CANCELLED", "the call was cancelled", null) };
  }

  /** The write's response was lost: ask the add-in ledger until the deadline (D2 §13.2). */
  private async reconcile(request: BridgeRequest, o: SendOptions): Promise<SendResult> {
    const ref = request.writeTag;
    while (!o.deadline.expired) {
      const r = await this.control("get_request_result", { requestId: request.requestId }, CONTROL_TIMEOUT_MS, request.clientKey);
      if (r.ok && r.data && typeof r.data === "object") {
        const entry = r.data as LedgerEntry;
        if (isTerminalLedgerState(entry.state)) return { response: recoveredResponse(request, entry, "BRIDGE_RESPONSE_RECOVERED", "the response was lost on the pipe and recovered from the add-in ledger"), recovered: true };
      } else if (!r.ok && (r.code === "REVIT_EXITED" || r.code === "ADDIN_PIPE_MISSING")) {
        break;
      }
      if (!(await o.deadline.sleep(250))) break;
    }
    const lock = await lockState(this.lockPath());
    if (lock !== "held") {
      const entry = await searchLedgers(this.deps.home, request.requestId, this.info.instanceId);
      if (entry && isTerminalLedgerState(entry.state))
        return {
          response: recoveredResponse(request, entry, "BRIDGE_RESPONSE_RECOVERED", `Revit ${this.info.year} exited after the write ended ${entry.state}; not saved unless the doc was saved afterwards`),
          recovered: true,
        };
      return { response: failure(request.requestId, "WRITE_OUTCOME_UNKNOWN", `Revit ${this.info.year} exited while applying ${ref ?? "the write"}`, { ref, year: this.info.year }) };
    }
    return { response: failure(request.requestId, "WRITE_STILL_RUNNING", `the write is still running in Revit ${this.info.year}`, { ref, instance: String(this.info.year) }) };
  }

  private lockPath(): string {
    return join(this.deps.registry.dir, `${this.info.instanceId}.lock`);
  }

  private exitedFailure(requestId: string): BridgeResponse {
    const docs = (this.info.snapshot?.docs ?? []).map((d) => d.title);
    return failure(requestId, "REVIT_EXITED", `Revit ${this.info.year} (pid ${this.info.pid}) exited`, { year: this.info.year, pid: this.info.pid, doc: docs[0] });
  }
}

function recoveredResponse(request: BridgeRequest, entry: LedgerEntry, code: string, text: string): BridgeResponse {
  const base: BridgeResponse =
    entry.response && typeof entry.response === "object"
      ? { ...entry.response }
      : {
          v: BRIDGE_PROTOCOL_VERSION,
          requestId: request.requestId,
          ok: entry.state === "committed",
          code: entry.state === "committed" ? null : "REVIT_TRANSACTION_ROLLED_BACK",
          message: entry.state === "committed" ? null : `the write ended ${entry.state}`,
          details: null,
          summary: entry.state === "committed" ? `committed ${request.op}` : null,
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

  constructor(private readonly deps: ChannelDeps) {}

  get(info: InstanceInfo): InstanceChannel {
    let channel = this.channels.get(info.instanceId);
    if (!channel || channel.isRetired) {
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

  /** Retire channels of instances that are no longer live. */
  sync(live: InstanceInfo[]): void {
    const ids = new Set(live.map((i) => i.instanceId));
    for (const [id, channel] of this.channels) {
      if (!ids.has(id)) {
        channel.retire();
        if (channel.inFlight === 0) this.channels.delete(id);
      }
    }
  }

  all(): InstanceChannel[] {
    return [...this.channels.values()];
  }

  retireAll(): void {
    for (const channel of this.channels.values()) channel.retire();
  }
}
