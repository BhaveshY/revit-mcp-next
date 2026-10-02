// The tool-call pipeline and the ToolContext implementation (SPEC §9B):
//   normalize (§5.3) → page/confirm expansion → op resolution → per-op validation → set_target barrier → prepare →
//   handle (module or default) → fix/doc/notice completion → log. Handlers never throw to the SDK.
// Frozen in wave 2.

import { byKey, byName, canonicalJson, keyOf, type Kind, type OpMeta, type OpSpec, type ToolSpec } from "@revit-mcp-next/contracts/catalog";
import { canonicalErrorCode } from "@revit-mcp-next/contracts/errors";
import { BRIDGE_PROTOCOL_VERSION, type BridgeRequest, type BridgeResponse, type ControlOp, type ExecutingItem } from "@revit-mcp-next/contracts/protocol";
import type { InstanceInfo } from "../instances/InstanceRegistry.js";
import { failure } from "../ipc/errors.js";
import { pollJob } from "../jobs/JobRunner.js";
import { clampBudget, Deadline } from "../runtime/deadline.js";
import { ulid } from "../runtime/ids.js";
import type { StoredPlan } from "./confirm.js";
import { renderFix } from "./errors.js";
import { normalizeArgs } from "./normalize.js";
import type { PageEntry, Session } from "./session.js";
import type { AddinResult, BrokerServices, CallOptions, InstanceRef, ResolvedDoc, ToolContext, ToolModule, ToolOutcome } from "./types.js";
import { resolveOp, validateOp } from "./validate.js";

/** Thrown inside handlers to answer with an outcome (e.g. a resolve error); the pipeline renders it. */
export class OutcomeError extends Error {
  constructor(readonly outcome: ToolOutcome) {
    super(outcome.summary);
    this.name = "OutcomeError";
  }
}

export const isOutcomeError = (e: unknown): e is OutcomeError => e instanceof OutcomeError;

const WRITE_KINDS: ReadonlySet<Kind> = new Set(["write", "lifecycle", "code"]);
/** Params the broker consumes; never sent to the add-in. */
const BROKER_ONLY = ["doc", "preview", "confirm", "page"];
/** Params that may carry r#/last selectors. */
const SELECTOR_SCALARS = ["from", "with"];
const SELECTOR_ARRAYS = ["ids", "a", "b", "highlight", "expect_ids", "between"];

export interface ProgressSink {
  (message: string, done?: number, total?: number): void;
}

export interface RunOptions {
  /** MCP request signal (client cancellation). */
  signal?: AbortSignal;
  progress?: ProgressSink;
  /** Use this deadline (sub-calls of read_many / status include). */
  deadline?: Deadline;
  /** Nested call (read_many, status include): no barrier wait, no pending notices. */
  nested?: boolean;
}

export interface PipelineDeps {
  services: BrokerServices;
  modules: (tool: string) => ToolModule | undefined;
  defaults: (ctx: ToolContext) => Promise<ToolOutcome>;
}

/** Run one tool call end to end and return its outcome (never throws). */
export async function runTool(deps: PipelineDeps, session: Session, tool: string, rawArgs: unknown, options: RunOptions = {}): Promise<{ outcome: ToolOutcome; ctx: ToolContextImpl | null; detailFull: boolean }> {
  const started = Date.now();
  const { services } = deps;
  const spec = byName.get(tool);
  const raw = (rawArgs && typeof rawArgs === "object" && !Array.isArray(rawArgs) ? rawArgs : {}) as Record<string, unknown>;
  const detailFull = String(raw.detail ?? "").toLowerCase() === "full";
  if (!spec) {
    return { outcome: errorOutcome("UNKNOWN_OP", `unknown tool ${tool}`, { tools: [...byName.keys()] }, tool, null, {}), ctx: null, detailFull };
  }
  let ctx: ToolContextImpl | null = null;
  try {
    const settings = services.settings.get();
    const budget = options.deadline ?? Deadline.start(tool === "status" ? 5_000 : clampBudget(settings.callBudgetMs), options.signal);

    // 1-7: normalize
    const norm = normalizeArgs(spec, rawArgs);
    if (norm.error) return finishPlain(deps, session, spec, null, raw, errorOutcome(norm.error.code, norm.error.message, norm.error.details, tool, null, raw, norm.warnings), started, detailFull);
    let args = norm.args;
    const warnings = [...norm.warnings];

    // Page call: replay the stored args.
    let pageEntry: (PageEntry & { id: string }) | null = null;
    if (typeof args.page === "string" && args.page.trim() && "page" in spec.properties) {
      const id = args.page.trim().toLowerCase();
      const entry = session.pages.get(id);
      if (!entry) {
        const repeat = undefined;
        return finishPlain(deps, session, spec, null, raw, errorOutcome("PAGE_EXPIRED", `page token ${id} is ${session.pages.wasIssued(id) ? "expired" : "unknown"}`, { repeat }, tool, null, args, warnings), started, detailFull);
      }
      if (entry.tool !== tool) return finishPlain(deps, session, spec, null, raw, errorOutcome("INVALID_ARGS", `page token ${id} belongs to ${entry.tool}`, { param: "page", reason: "wrong tool", example: { page: id } }, tool, null, args, warnings), started, detailFull);
      const extra = Object.keys(args).filter((k) => k !== "page" && k !== "limit" && k !== "detail");
      if (extra.length) warnings.push(`PARAM_IGNORED ${extra.join(", ")} (a page token replays the original call)`);
      args = { ...entry.args, ...(args.limit !== undefined ? { limit: args.limit } : {}) };
      pageEntry = { ...entry, id };
    }

    // Confirm call: apply exactly the stored plan.
    let confirmPlan: StoredPlan | null = null;
    if (typeof args.confirm === "string" && args.confirm.trim() && "confirm" in spec.properties && args.preview !== true && !pageEntry) {
      const token = args.confirm.trim();
      const sent = Object.fromEntries(Object.entries(args).filter(([k]) => k !== "confirm"));
      const discValue = spec.discriminator ? (args[spec.discriminator] as string | undefined) ?? null : null;
      const check = session.confirms.check(token, tool, sent, discValue);
      if (!check.ok) {
        const retry = check.plan ? { ...(spec.discriminator && check.plan.op ? { [spec.discriminator]: check.plan.op } : {}), confirm: check.plan.token } : undefined;
        return finishPlain(deps, session, spec, null, raw, errorOutcome(check.code, check.message, { retry }, tool, discValue, args, warnings), started, detailFull);
      }
      confirmPlan = check.plan;
      args = { ...check.plan.args };
    }

    // 8-9: op + per-op validation.
    const opRes = resolveOp(spec, args);
    if (opRes.error) return finishPlain(deps, session, spec, null, raw, errorOutcome(opRes.error.code, opRes.error.message, opRes.error.details, tool, null, args, warnings), started, detailFull);
    const op = opRes.op;
    const opSpec = spec.ops[op ?? ""]!;
    const key = keyOf(tool, op);
    const v = validateOp(spec, op, args, { skipRequired: !!pageEntry || !!confirmPlan });
    warnings.push(...v.warnings);
    if (v.error) return finishPlain(deps, session, spec, op, raw, errorOutcome(v.error.code, v.error.message, v.error.details, tool, op, v.args, warnings), started, detailFull);
    args = v.args;
    if (!services.tools.isCallable(tool)) {
      return finishPlain(deps, session, spec, op, raw, errorOutcome(tool === "run_csharp" ? "CODE_EXECUTION_DISABLED" : "UNSUPPORTED_OP", tool === "run_csharp" ? "run_csharp is not enabled on this computer" : `${tool} is not available in this profile`, {}, tool, op, args, warnings), started, detailFull);
    }

    ctx = new ToolContextImpl({
      deps,
      session,
      spec,
      opSpec,
      op,
      key,
      meta: opSpec.meta,
      args,
      rawArgs: raw,
      deadline: budget,
      signal: options.signal ?? budget.signal,
      progressSink: options.progress,
      pageEntry,
      confirmPlan,
      nested: options.nested === true,
    });
    for (const w of warnings) ctx.warnLine(w);

    if (!options.nested && tool !== "set_target") await session.waitBarrier();

    const module = deps.modules(tool);
    let outcome: ToolOutcome | void = undefined;
    if (module?.prepare) outcome = await module.prepare(ctx);
    if (!outcome) outcome = module?.handle ? await module.handle(ctx) : await deps.defaults(ctx);
    return finishCtx(deps, ctx, outcome, started, detailFull, options.nested === true);
  } catch (error) {
    if (isOutcomeError(error)) {
      if (ctx) return finishCtx(deps, ctx, error.outcome, started, detailFull, options.nested === true);
      return finishPlain(deps, session, spec, null, raw, error.outcome, started, detailFull);
    }
    const requestId = ulid();
    services.log.error("handler_exception", { tool, requestId, error });
    const outcome = errorOutcome("INTERNAL_ERROR", `unexpected broker error: ${(error as Error)?.message ?? String(error)}`, { requestId }, tool, ctx?.op ?? null, ctx?.effectiveArgs ?? raw);
    if (ctx) return finishCtx(deps, ctx, outcome, started, detailFull, options.nested === true);
    return finishPlain(deps, session, spec, null, raw, outcome, started, detailFull);
  }
}

function errorOutcome(code: string, message: string, details: Record<string, unknown> | null | undefined, tool: string, op: string | null, args: Record<string, unknown>, warnings: string[] = []): ToolOutcome {
  const canonical = canonicalErrorCode(code);
  const rendered = renderFix({ code: canonical, tool, op, key: keyOf(tool, op), args, details: details ?? null, doc: null });
  return { status: "error", code: canonical, summary: message, details: cleanDetails(details), fix: rendered.fix, options: rendered.options, warnings };
}

/** Drop helper fields used only for fix rendering from the shown details. */
function cleanDetails(details: Record<string, unknown> | null | undefined): Record<string, unknown> | undefined {
  if (!details) return undefined;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(details)) {
    if (v === undefined || k === "example" || k === "retry" || k === "repeat") continue;
    if (k === "options" && Array.isArray(v)) continue;
    out[k] = v;
  }
  return Object.keys(out).length ? out : undefined;
}

function finishPlain(deps: PipelineDeps, session: Session, spec: ToolSpec, op: string | null, raw: Record<string, unknown>, outcome: ToolOutcome, started: number, detailFull: boolean) {
  deps.services.log.info("call", { tool: spec.name, op, client: session.clientKey, status: outcome.status, code: outcome.code, totalMs: Date.now() - started });
  void raw;
  return { outcome, ctx: null, detailFull };
}

function finishCtx(deps: PipelineDeps, ctx: ToolContextImpl, outcome: ToolOutcome, started: number, detailFull: boolean, nested: boolean) {
  const final = ctx.complete(outcome, nested);
  deps.services.log.info("call", {
    tool: ctx.tool,
    op: ctx.op,
    client: ctx.session.clientKey,
    status: final.status,
    code: final.code,
    inst: ctx.lastInstanceId,
    docKey: ctx.resolvedCache?.key ?? undefined,
    reqId: ctx.lastRequestId,
    totalMs: Date.now() - started,
  });
  return { outcome: final, ctx, detailFull };
}

interface ContextInit {
  deps: PipelineDeps;
  session: Session;
  spec: ToolSpec;
  opSpec: OpSpec;
  op: string | null;
  key: string;
  meta: OpMeta;
  args: Record<string, unknown>;
  rawArgs: Record<string, unknown>;
  deadline: Deadline;
  signal: AbortSignal;
  progressSink?: ProgressSink;
  pageEntry: (PageEntry & { id: string }) | null;
  confirmPlan: StoredPlan | null;
  nested: boolean;
}

export class ToolContextImpl implements ToolContext {
  readonly tool: string;
  readonly op: string | null;
  readonly key: string;
  readonly meta: OpMeta;
  args: Record<string, unknown>;
  readonly session: Session;
  readonly deadline: Deadline;
  readonly signal: AbortSignal;
  readonly spec: ToolSpec;
  readonly opSpec: OpSpec;
  readonly rawArgs: Record<string, unknown>;
  readonly services: BrokerServices;
  readonly pageEntry: (PageEntry & { id: string }) | null;
  readonly confirmPlan: StoredPlan | null;
  readonly nested: boolean;
  readonly notices: string[] = [];
  readonly warnings: string[] = [];
  resolvedCache: ResolvedDoc | null = null;
  private resolving: Promise<ResolvedDoc | null> | null = null;
  lastInstanceId?: string;
  lastRequestId?: string;
  private progressCount = 0;
  private readonly deps: PipelineDeps;
  private readonly progressSink?: ProgressSink;
  private readonly initialArgs: Record<string, unknown>;

  constructor(init: ContextInit) {
    this.deps = init.deps;
    this.services = init.deps.services;
    this.session = init.session;
    this.spec = init.spec;
    this.opSpec = init.opSpec;
    this.tool = init.spec.name;
    this.op = init.op;
    this.key = init.key;
    this.meta = init.meta;
    this.args = init.args;
    this.initialArgs = { ...init.args };
    this.rawArgs = init.rawArgs;
    this.deadline = init.deadline;
    this.signal = init.signal;
    this.progressSink = init.progressSink;
    this.pageEntry = init.pageEntry;
    this.confirmPlan = init.confirmPlan;
    this.nested = init.nested;
  }

  get settings() {
    return this.services.settings.get();
  }

  get effectiveArgs(): Record<string, unknown> {
    return { ...this.initialArgs };
  }

  progress(message: string, done?: number, total?: number): void {
    this.progressCount += 1;
    try {
      this.progressSink?.(message, done ?? this.progressCount, total);
    } catch {
      // progress is a courtesy
    }
  }

  notice(code: string, text: string): void {
    const line = `${code} ${text}`.trim();
    if (!this.notices.includes(line)) this.notices.push(line);
  }

  warn(code: string, text: string, ids?: number[]): void {
    this.warnLine(`${code} ${text}${ids && ids.length ? ` ids ${ids.slice(0, 20).join(",")}${ids.length > 20 ? ` +${ids.length - 20}` : ""}` : ""}`);
  }

  warnLine(line: string): void {
    if (!this.warnings.includes(line)) this.warnings.push(line);
  }

  fail(code: string, message: string, details?: Record<string, unknown>): ToolOutcome {
    const canonical = canonicalErrorCode(code);
    const rendered = renderFix({ code: canonical, tool: this.tool, op: this.op, key: this.key, args: this.effectiveArgs, details: details ?? null, doc: this.resolvedCache });
    return {
      status: "error",
      code: canonical,
      summary: message,
      details: cleanDetails(details),
      fix: rendered.fix,
      options: rendered.options,
      doc: this.resolvedCache?.title ? { title: this.resolvedCache.title, year: this.resolvedCache.year, n: this.resolvedCache.n } : undefined,
    };
  }

  /** Fail from a broker/add-in failure envelope. */
  failFrom(r: BridgeResponse): ToolOutcome {
    return this.fail(r.code ?? "INTERNAL_ERROR", r.message ?? "failed", (r.details ?? undefined) as Record<string, unknown> | undefined);
  }

  async resolveDoc(): Promise<ResolvedDoc | null> {
    if (this.resolvedCache) return this.resolvedCache;
    if (!this.resolving) {
      this.resolving = (async () => {
        const hasInstance = "instance" in this.spec.properties;
        const outcome = await this.services.resolver.resolve({
          session: this.session,
          doc: this.args.doc,
          instance: hasInstance ? this.args.instance : undefined,
          scope: this.meta.scope,
          kind: this.meta.kind,
          key: this.key,
          tool: this.tool,
          deadline: this.deadline,
          fresh: WRITE_KINDS.has(this.meta.kind),
          familyHint: typeof this.args.family === "string" ? this.args.family : undefined,
          progress: (m) => this.progress(m),
        });
        for (const n of outcome.notices) this.noticeLine(n);
        if (!outcome.ok) {
          this.resolving = null;
          throw new OutcomeError(this.failFrom(outcome.error));
        }
        this.resolvedCache = outcome.doc;
        return outcome.doc;
      })();
    }
    return this.resolving;
  }

  /** Forget the resolved doc (TARGET_STALE retry). */
  resetResolve(): void {
    this.resolvedCache = null;
    this.resolving = null;
  }

  noticeLine(line: string): void {
    if (!this.notices.includes(line)) this.notices.push(line);
  }

  async instances(): Promise<InstanceInfo[]> {
    return this.services.registry.list();
  }

  private async instanceInfo(ref: InstanceRef): Promise<InstanceInfo | null> {
    const live = await this.services.registry.list();
    if (typeof ref === "string") {
      const s = ref.trim();
      return live.find((i) => i.instanceId === s) ?? live.find((i) => String(i.year) === s) ?? live.find((i) => String(i.pid) === s) ?? null;
    }
    return live.find((i) => i.instanceId === ref.instanceId) ?? null;
  }

  async control(instance: InstanceRef, op: ControlOp, payload: object = {}, timeoutMs = 800): Promise<AddinResult> {
    const info = await this.instanceInfo(instance);
    if (!info) return { ...failure(ulid(), "REVIT_EXITED", "that Revit instance is not running", null), key: this.key };
    const channel = this.services.channels.get(info);
    const r = await channel.control(op, payload as Record<string, unknown>, timeoutMs, this.session.clientKey);
    return { ...r, key: this.key, instanceId: info.instanceId, year: info.year, pid: info.pid };
  }

  /** Expand r#/last selectors (§6.5) and drop broker-only params. */
  async expandArgs(args: Record<string, unknown>, doc: ResolvedDoc | null): Promise<Record<string, unknown>> {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(args)) if (!BROKER_ONLY.includes(k)) out[k] = v;
    const writeLike = WRITE_KINDS.has(this.meta.kind);
    const expand = (token: string, param: string): unknown[] | string => {
      const t = token.trim().toLowerCase();
      if (this.session.handles.matches(t)) {
        const h = this.session.handles.get(t);
        if (!h) throw new OutcomeError(this.fail("HANDLE_EXPIRED", `handle ${t} is ${this.session.handles.wasIssued(t) ? "expired (60 min)" : "unknown"}`, { param }));
        if (doc?.key && h.docKey !== doc.key) throw new OutcomeError(this.fail("INVALID_ARGS", `handle ${t} belongs to another document; pass doc or use a handle from ${doc.title ?? "the target"}`, { param, reason: "handle of another doc" }));
        if (h.link && writeLike) throw new OutcomeError(this.fail("LINK_READ_ONLY", `handle ${t} holds elements of the linked model ${h.link}; links are read-only`, { link: h.link }));
        return [...h.ids];
      }
      if (t === "last") {
        const last = doc?.key ? this.session.lastWrites.get(doc.key) : undefined;
        if (!last) throw new OutcomeError(this.fail("INVALID_ARGS", `no write of this session in ${doc?.title ?? "the target doc"} yet, so 'last' is empty`, { param, reason: "no last write" }));
        return [...last.ids];
      }
      return token;
    };
    for (const p of SELECTOR_SCALARS) {
      const v = out[p];
      if (typeof v === "string") out[p] = expand(v, p);
    }
    for (const p of SELECTOR_ARRAYS) {
      const v = out[p];
      if (Array.isArray(v)) {
        const flat: unknown[] = [];
        for (const item of v) {
          if (typeof item === "string") {
            const e = expand(item, p);
            if (Array.isArray(e)) flat.push(...e);
            else flat.push(e);
          } else flat.push(item);
        }
        out[p] = flat;
      }
    }
    return out;
  }

  async call(key?: string, args?: object, o: CallOptions = {}): Promise<AddinResult> {
    const callKey = key ?? this.key;
    const info = byKey.get(callKey);
    if (!info) return { ...failure(ulid(), "INTERNAL_ERROR", `unknown registry key ${callKey}`, null), key: callKey };
    const meta = info.entry;
    if (meta.impl === "broker") return { ...failure(ulid(), "INTERNAL_ERROR", `${callKey} is handled by the broker only`, null), key: callKey };
    const deadline = o.deadline ?? this.deadline;
    const doc = o.doc !== undefined ? o.doc : await this.resolveDoc();
    if (!doc) return { ...failure(ulid(), "NO_REVIT_RUNNING", "no Revit with the add-in is running", null), key: callKey };
    const inst = await this.instanceInfo(doc);
    if (!inst) return { ...failure(ulid(), "REVIT_EXITED", `Revit ${doc.year} (pid ${doc.pid}) exited`, { year: doc.year, pid: doc.pid, doc: doc.title ?? undefined }), key: callKey };
    const kind = o.kind ?? meta.kind;
    const baseArgs = (args ?? this.args) as Record<string, unknown>;
    const sendArgs = await this.expandArgs(baseArgs, doc);
    let mode: "apply" | "preview" | undefined;
    if (WRITE_KINDS.has(kind)) {
      if (o.mode) mode = o.mode;
      else if (kind === "code") mode = baseArgs.mode === "commit" ? "apply" : "preview";
      else mode = baseArgs.preview === true || this.args.preview === true ? "preview" : "apply";
    }
    const writeTag = WRITE_KINDS.has(kind) && mode === "apply" ? this.session.writes.next() : undefined;
    const request: BridgeRequest = {
      v: BRIDGE_PROTOCOL_VERSION,
      requestId: ulid(),
      clientKey: this.session.clientKey,
      op: callKey,
      kind,
      ...(mode ? { mode } : {}),
      timeoutMs: Math.max(1_000, deadline.remaining()),
      doc: doc.rid !== null && doc.key ? { rid: doc.rid, key: doc.key } : null,
      ...(writeTag ? { writeTag } : {}),
      ...(o.confirmed ? { confirmed: o.confirmed } : {}),
      ...(o.asJob ? { asJob: true } : {}),
      ...(o.wantIds ? { wantIds: true } : {}),
      args: sendArgs,
    };
    this.lastInstanceId = inst.instanceId;
    this.lastRequestId = request.requestId;
    const started = Date.now();
    if (writeTag) {
      this.session.writes.set(writeTag, { instanceId: inst.instanceId, requestId: request.requestId, key: callKey, tool: info.tool.name, docKey: doc.key, at: Date.now(), state: "sent", outstanding: true });
      await this.services.journal.add({ requestId: request.requestId, shortId: writeTag, key: callKey, tool: info.tool.name, instanceId: inst.instanceId, docKey: doc.key, sentAt: new Date().toISOString() });
    }
    const channel = this.services.channels.get(inst);
    const session = this.session;
    const channelSession = {
      clientKey: session.clientKey,
      refOf: (item: ExecutingItem) => session.refOf(item),
      isOwnOutstandingWrite: (item: ExecutingItem) => session.isOwnOutstandingWrite(item),
    };
    const coalesceKey = kind === "read" && !o.asJob ? `${inst.instanceId}|${callKey}|${canonicalJson(sendArgs)}|${doc.rid}|${o.wantIds ? 1 : 0}` : undefined;
    let sent = await channel.send(request, { deadline, session: channelSession, job: meta.job, progress: (m, d, t) => this.progress(m, d, t), coalesceKey });
    // TARGET_STALE: refresh the snapshot, re-resolve once and retry (D2 §9.1).
    if (!sent.response.ok && sent.response.code === "TARGET_STALE" && o.doc === undefined && deadline.remaining() > 3_000) {
      this.services.registry.invalidate();
      this.resetResolve();
      const again = await this.resolveDoc();
      if (again && again.rid !== null && again.key) {
        request.requestId = ulid();
        request.doc = { rid: again.rid, key: again.key };
        sent = await channel.send(request, { deadline, session: channelSession, job: meta.job, progress: (m, d, t) => this.progress(m, d, t) });
      }
    }
    let response = sent.response;
    const result: AddinResult = {
      ...response,
      key: callKey,
      instanceId: inst.instanceId,
      year: inst.year,
      pid: inst.pid,
      writeId: writeTag,
      coalesced: sent.coalesced,
      recovered: sent.recovered,
      retried: sent.retried,
      elapsedMs: Date.now() - started,
    };

    // Job start: wait inline until terminal or the budget (§6.8).
    if (o.asJob && response.ok && response.job && response.job.jobId) {
      const jobId = response.job.jobId;
      const polled = await pollJob({ channel, jobId, deadline, clientKey: session.clientKey, progress: (m, d, t) => this.progress(m, d, t) });
      if (polled.terminal && polled.result) {
        response = polled.result;
        Object.assign(result, polled.result, { job: null });
      } else if (polled.terminal && polled.status) {
        Object.assign(result, {
          ok: polled.status.state === "succeeded",
          code: polled.status.state === "succeeded" ? null : polled.status.state === "cancelled" ? "REQUEST_CANCELLED" : "INTERNAL_ERROR",
          message: polled.status.state === "succeeded" ? null : `the job ended ${polled.status.state}`,
          job: null,
        });
      } else if (polled.failure) {
        Object.assign(result, polled.failure);
      } else {
        const jobRef = session.jobs.add({ instanceId: inst.instanceId, jobId, key: callKey, tool: info.tool.name, args: baseArgs, docKey: doc.key, at: Date.now(), what: describeKey(callKey) });
        result.jobRef = jobRef;
        result.job = { jobId, state: polled.status?.state ?? "running", stage: polled.status?.stage, done: polled.status?.done, total: polled.status?.total };
      }
    }

    // Late results.
    if (sent.late) {
      if (WRITE_KINDS.has(kind)) {
        const tag = writeTag;
        sent.late.then(
          (late) => {
            if (tag) {
              const w = session.writes.get(tag);
              if (w) {
                w.state = late.ok ? "committed" : "failed";
                w.outstanding = false;
                w.summary = late.summary ?? late.message ?? undefined;
              }
              void this.services.journal.remove(request.requestId);
            }
            session.pendingNotices.push(`LATE_RESULT ${tag ?? "a write"} (${callKey}) ${late.ok ? `completed: ${late.summary ?? "ok"}` : `ended ${late.code}: ${late.message ?? ""}`}`);
          },
          () => undefined
        );
      } else {
        const cacheKey = readCacheKey(callKey, sendArgs, doc.key);
        this.services.results.put(cacheKey, sent.late);
        const jobRef = session.jobs.add({ instanceId: inst.instanceId, cacheKey, requestId: request.requestId, key: callKey, tool: info.tool.name, args: baseArgs, docKey: doc.key, at: Date.now(), what: describeKey(callKey) });
        result.jobRef = jobRef;
        result.details = { ...(result.details ?? {}), ref: jobRef };
      }
    }

    // Write bookkeeping.
    if (writeTag) {
      const w = session.writes.get(writeTag);
      const still = result.code === "WRITE_STILL_RUNNING" || (result.code === "REQUEST_CANCELLED" && !!sent.late) || result.code === "WRITE_OUTCOME_UNKNOWN";
      if (w) {
        w.outstanding = false;
        w.state = result.ok ? (result.needsConfirm ? "not_applied" : "committed") : still ? (result.code === "WRITE_OUTCOME_UNKNOWN" ? "unknown" : "running") : "failed";
        w.summary = result.summary ?? result.message ?? undefined;
      }
      if (result.code === "WRITE_STILL_RUNNING" || result.code === "WRITE_OUTCOME_UNKNOWN") result.details = { ...(result.details ?? {}), ref: writeTag };
      if (!still) await this.services.journal.remove(request.requestId);
    }
    return result;
  }

  outcomeFrom(r: AddinResult): ToolOutcome {
    return this.services.tools.outcomeFrom(this, r);
  }

  /** A context describing another call of this session (job results render like the original call). */
  forCall(tool: string, args: Record<string, unknown>, o: { op?: string | null; doc?: ResolvedDoc | null } = {}): ToolContext {
    const spec = byName.get(tool) ?? this.spec;
    const op = o.op !== undefined ? o.op : spec.discriminator ? ((args[spec.discriminator] as string | undefined) ?? null) : null;
    const opSpec = spec.ops[op ?? ""] ?? Object.values(spec.ops)[0]!;
    const derived = new ToolContextImpl({
      deps: this.deps,
      session: this.session,
      spec,
      opSpec,
      op,
      key: keyOf(spec.name, op),
      meta: opSpec.meta,
      args: { ...args },
      rawArgs: { ...args },
      deadline: this.deadline,
      signal: this.signal,
      progressSink: this.progressSink,
      pageEntry: null,
      confirmPlan: null,
      nested: true,
    });
    if (o.doc !== undefined) derived.resolvedCache = o.doc;
    return derived;
  }

  async invoke(tool: string, args: Record<string, unknown>, o: { deadline?: Deadline } = {}): Promise<ToolOutcome> {
    const run = await runTool(this.deps, this.session, tool, args, { deadline: o.deadline ?? this.deadline, nested: true, signal: this.signal, progress: (m, d, t) => this.progress(m, d, t) });
    return run.outcome;
  }

  /** Final touches: fix lines, doc suffix, notices/warnings merged, pending session notices. */
  complete(outcome: ToolOutcome, nested: boolean): ToolOutcome {
    const o: ToolOutcome = { ...outcome };
    if (o.status === "error") {
      o.code = canonicalErrorCode(o.code ?? "INTERNAL_ERROR");
      if (!o.fix) {
        const rendered = renderFix({ code: o.code, tool: this.tool, op: this.op, key: this.key, args: this.effectiveArgs, details: (o.details ?? null) as Record<string, unknown> | null, doc: this.resolvedCache });
        o.fix = rendered.fix;
        o.options ??= rendered.options;
      }
    }
    if (o.doc === undefined && this.resolvedCache?.title) o.doc = { title: this.resolvedCache.title, year: this.resolvedCache.year, n: this.resolvedCache.n };
    const pending = nested ? [] : [...this.session.takePendingNotices(), ...(this.services.journal.takeRecovered().map((r) => `RECOVERED_WRITE ${r.text}`))];
    o.notices = dedupe([...pending, ...this.notices, ...(o.notices ?? [])]);
    o.warnings = dedupe([...this.warnings, ...(o.warnings ?? [])]);
    return o;
  }
}

export function readCacheKey(key: string, args: Record<string, unknown>, docKey: string | null): string {
  return `${key}|${canonicalJson(args)}|${docKey ?? ""}`;
}

export function describeKey(key: string): string {
  return key.replace(".", " ").replace(/_/g, " ");
}

function dedupe(lines: string[]): string[] {
  return [...new Set(lines.filter((l) => l && l.trim()))];
}

export { failure };
