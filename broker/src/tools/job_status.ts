// job_status (SPEC §6.6, §6.8, §8.10, D1 §4B): j# = an add-in job or a read still running in the background; w# = a
// write whose outcome was unclear (add-in ledger); a raw request id (ULID) is accepted too. wait = seconds to wait (0-45,
// default 20). No id: this session's recent jobs and writes. A finished job renders like the original call's result.
// - j# job: polls the add-in; when its Revit is gone (or forgot the job) the job record the add-in persisted under
//   <home>\jobs answers; without one the job was interrupted (REVIT_EXITED).
// - j# background read: waits on the READ_STILL_RUNNING result cache (10 min).
// - w#: asks the live add-in ledger (get_request_result), then every <home>\ledger\*.jsonl by requestId; a write the
//   add-in never accepted ({found:false} everywhere) did not run; a dead Revit without a ledger line is
//   WRITE_OUTCOME_UNKNOWN. Ids do not survive a broker restart: JOB_UNKNOWN → status {include:["writes"]}.
import { isTerminalLedgerState, type BridgeResponse, type LedgerEntry } from "@revit-mcp-next/contracts/protocol";
import { ULID_PATTERN } from "../runtime/ids.js";
import { pollJob, describeJob, readJobRecord } from "../jobs/JobRunner.js";
import { searchLedgers } from "../state/Ledger.js";
import type { AddinResult, InstanceInfo, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";
import type { WriteEntry } from "../framework/session.js";

const MAX_WAIT_S = 45;
const DEFAULT_WAIT_S = 20;

function waitMs(ctx: ToolContext): number {
  const raw = typeof ctx.args.wait === "number" && Number.isFinite(ctx.args.wait) ? ctx.args.wait : DEFAULT_WAIT_S;
  const wait = Math.max(0, Math.min(MAX_WAIT_S, raw));
  return Math.max(0, Math.min(wait * 1000, ctx.deadline.remaining() - 2_500));
}

/** Render a finished call's response like the original call. */
function asOriginal(ctx: ToolContext, tool: string, args: Record<string, unknown>, key: string, response: BridgeResponse, instance: { instanceId: string; year: number; pid: number } | null): ToolOutcome {
  const original = ctx.forCall(tool, args);
  const r: AddinResult = { ...response, job: null, key, instanceId: instance?.instanceId, year: instance?.year, pid: instance?.pid };
  return original.outcomeFrom(r);
}

function toolOfKey(key: string | undefined, fallback: string): string {
  if (!key) return fallback;
  const tool = key.split(".")[0];
  return tool || fallback;
}

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  const raw = typeof ctx.args.id === "string" ? ctx.args.id.trim() : ctx.args.id !== undefined && ctx.args.id !== null ? String(ctx.args.id).trim() : "";
  const session = ctx.session;
  if (!raw) return listRecent(ctx);
  const id = raw.toLowerCase();
  if (session.jobs.matches(id)) return jobStatus(ctx, id);
  if (session.writes.matches(id)) {
    const w = session.writes.get(id);
    if (!w) return ctx.fail("JOB_UNKNOWN", `write ${id} is unknown to this session (write ids do not survive a broker restart)`);
    return writeStatus(ctx, id, w.requestId, w);
  }
  if (ULID_PATTERN.test(raw.toUpperCase())) return writeStatus(ctx, raw.toUpperCase(), raw.toUpperCase(), null);
  if (/^[jw]\d+$/i.test(raw)) return ctx.fail("JOB_UNKNOWN", `${id} is unknown to this session (ids do not survive a broker restart)`);
  return ctx.fail("INVALID_ARGS", `id must be a job id j# or a write id w#, got ${raw}`, { param: "id", reason: "not a j# or w#", example: { id: "j1" } });
}

function listRecent(ctx: ToolContext): ToolOutcome {
  const session = ctx.session;
  const jobs = session.jobs.values().slice(-10).map(([id, j]) => [id, j.what, j.jobId ? "job" : "read", new Date(j.at).toISOString()]);
  const writes = session.writes.values().slice(-10).map(([id, w]) => [id, w.key, w.state, new Date(w.at).toISOString()]);
  const recovered = ctx.services.journal.peekRecovered().map((r) => [r.entry.shortId, r.entry.key, r.state, r.entry.sentAt]);
  const data: Record<string, unknown> = { jobs: { cols: ["id", "what", "kind", "at"], rows: jobs }, writes: { cols: ["id", "key", "state", "at"], rows: writes } };
  if (recovered.length) data.previousSession = { cols: ["id", "key", "state", "sent"], rows: recovered };
  const open = [...jobs, ...writes.filter((w) => w[2] === "running" || w[2] === "unknown" || w[2] === "sent")];
  return {
    status: "ok",
    summary: `${jobs.length} recent job${jobs.length === 1 ? "" : "s"} and ${writes.length} write${writes.length === 1 ? "" : "s"} in this session`,
    data,
    doc: null,
    next: open.length ? `job_status {"id":"${open.at(-1)![0]}"}` : undefined,
  };
}

// ------------------------------------------------------------------------------------------------ j#

async function jobStatus(ctx: ToolContext, id: string): Promise<ToolOutcome> {
  const session = ctx.session;
  const job = session.jobs.get(id);
  if (!job) return ctx.fail("JOB_UNKNOWN", `job ${id} is unknown to this session (job ids do not survive a broker restart)`);
  const live = await ctx.instances();
  const inst = live.find((i) => i.instanceId === job.instanceId) ?? null;

  // A read that kept running after its budget (READ_STILL_RUNNING).
  if (job.cacheKey) {
    const response = await ctx.services.results.wait(job.cacheKey, waitMs(ctx));
    if (response) {
      ctx.services.results.delete(job.cacheKey);
      return asOriginal(ctx, job.tool, job.args, job.key, response, inst);
    }
    const entry = ctx.services.results.get(job.cacheKey);
    if (!entry) return ctx.fail("JOB_UNKNOWN", `the result of ${id} (${job.what}) is gone (results are kept for 10 minutes); repeat the original read`);
    if (entry.error) {
      if (!inst) return ctx.fail("REVIT_EXITED", `Revit exited while the read ${id} (${job.what}) was running`, exitDetails(ctx, job.instanceId));
      return ctx.fail("JOB_UNKNOWN", `the background read ${id} ended without a result (${entry.error}); repeat the original read`);
    }
    return {
      status: "running",
      summary: `${job.what} is still running${inst ? ` in Revit ${inst.year}` : ""} (${Math.round((Date.now() - job.at) / 1000)} s) as ${id}`,
      data: { job: id, state: "running" },
      next: `job_status {"id":"${id}","wait":30} - or repeat the original call: it returns the cached result`,
      doc: null,
    };
  }

  if (!job.jobId) return ctx.fail("JOB_UNKNOWN", `${id} has no add-in job to follow`);
  if (!inst) return finishedOrInterrupted(ctx, id, job.instanceId, job.jobId, job.tool, job.args, job.key, job.what);
  const channel = ctx.services.channels.get(inst);
  const polled = await pollJob({ channel, jobId: job.jobId, deadline: ctx.deadline.child(waitMs(ctx) + 1_000), clientKey: session.clientKey, reserveMs: 500, progress: (m, d, t) => ctx.progress(m, d, t) });
  if (polled.failure) {
    if (polled.failure.code === "REVIT_EXITED" || polled.failure.code === "JOB_UNKNOWN") return finishedOrInterrupted(ctx, id, job.instanceId, job.jobId, job.tool, job.args, job.key, job.what);
    return ctx.failFrom(polled.failure);
  }
  if (polled.terminal) {
    if (polled.result) return asOriginal(ctx, job.tool, job.args, job.key, polled.result, inst);
    return stateOnly(ctx, id, job.what, polled.status?.state ?? "succeeded");
  }
  return {
    status: "running",
    summary: `${describeJob(polled.status, job.what)} as ${id}`,
    data: { job: id, state: polled.status?.state ?? "running", stage: polled.status?.stage, done: polled.status?.done, total: polled.status?.total },
    next: `job_status {"id":"${id}","wait":30} - do not repeat the original call`,
    doc: null,
  };
}

async function finishedOrInterrupted(ctx: ToolContext, id: string, instanceId: string, jobId: string, tool: string, args: Record<string, unknown>, key: string, what: string): Promise<ToolOutcome> {
  const record = await readJobRecord(ctx.services.home, instanceId, jobId);
  if (record?.result) return asOriginal(ctx, tool, args, key, record.result, null);
  if (record && record.state !== "queued" && record.state !== "running") return stateOnly(ctx, id, what, record.state);
  const live = (await ctx.instances()).some((i) => i.instanceId === instanceId);
  if (live) return ctx.fail("JOB_UNKNOWN", `Revit no longer knows job ${id} (${what}) and kept no record of it`);
  return ctx.fail("REVIT_EXITED", `Revit exited while job ${id} (${what}) was running; the job was interrupted and its partial output removed or left unsaved`, exitDetails(ctx, instanceId));
}

function stateOnly(ctx: ToolContext, id: string, what: string, state: string): ToolOutcome {
  if (state === "succeeded") return { status: "ok", summary: `${what} finished`, data: { job: id, state }, doc: null };
  if (state === "cancelled") return { status: "ok", summary: `${what} was cancelled; nothing of it is kept`, data: { job: id, state }, doc: null };
  if (state === "interrupted") return ctx.fail("REVIT_EXITED", `${what} (${id}) was interrupted: its Revit stopped before it finished`, { job: id, state });
  return ctx.fail("INTERNAL_ERROR", `${what} (${id}) ended ${state}`, { job: id, state });
}

function exitDetails(ctx: ToolContext, instanceId: string): Record<string, unknown> {
  const e = ctx.services.registry.exitOf(instanceId);
  return e ? { year: e.year, pid: e.pid, doc: e.docs[0] } : {};
}

// ------------------------------------------------------------------------------------------------ w# and request ids

async function writeStatus(ctx: ToolContext, id: string, requestId: string, w: WriteEntry | null): Promise<ToolOutcome> {
  const session = ctx.session;
  const live = await ctx.instances();
  const candidates: InstanceInfo[] = w ? live.filter((i) => i.instanceId === w.instanceId) : live;
  const key = w?.key ?? "write";
  const tool = w?.tool ?? "";
  const end = Date.now() + waitMs(ctx);
  let notReceived = 0;
  let lastState: string | null = null;
  for (;;) {
    notReceived = 0;
    let answered = 0;
    for (const inst of candidates) {
      const r = await ctx.control(inst, "get_request_result", { requestId }, 800);
      if (!r.ok || !r.data || typeof r.data !== "object") continue;
      answered += 1;
      const entry = r.data as LedgerEntry & { found?: boolean };
      if (entry.found === false || !entry.state) {
        notReceived += 1;
        continue;
      }
      if (isTerminalLedgerState(entry.state)) return fromLedger(ctx, id, entry, tool, key, inst, w);
      lastState = entry.state;
    }
    if (answered > 0 && notReceived === answered && !lastState) break; // nobody has it
    if (candidates.length === 0 || Date.now() >= end) break;
    if (!(await ctx.deadline.sleep(500))) break;
  }
  if (lastState)
    return { status: "running", summary: `${id} (${key}) is ${lastState} in Revit`, data: { write: id, state: lastState }, next: `job_status {"id":"${id}","wait":30} - never repeat the write`, doc: null };

  // The instance is gone (or never answered): search every ledger file by requestId.
  const entry = await searchLedgers(ctx.services.home, requestId, w?.instanceId);
  if (entry && isTerminalLedgerState(entry.state)) return fromLedger(ctx, id, entry, tool, key, null, w);

  // What this session saw itself.
  if (w && (w.state === "committed" || w.state === "failed" || w.state === "not_applied"))
    return { status: "ok", summary: `${id} (${key}) ${w.state === "committed" ? "committed" : w.state === "not_applied" ? "was not applied (it needed a confirmation)" : "failed; nothing changed"}${w.summary ? `: ${w.summary}` : ""}`, data: { write: id, state: w.state }, doc: null };

  if (notReceived > 0 && candidates.length > 0) {
    if (w) {
      w.state = "failed";
      w.outstanding = false;
    }
    return { status: "ok", summary: `${id} (${key}) never reached Revit; nothing ran`, data: { write: id, state: "not_received" }, doc: null };
  }
  if (!w && candidates.length > 0) return ctx.fail("JOB_UNKNOWN", `no Revit knows request ${id}`);
  if (candidates.length === 0) return ctx.fail("WRITE_OUTCOME_UNKNOWN", `the Revit that ran ${id} (${key}) is gone and its ledger has no outcome`, { ref: w ? id : undefined, ...(w ? exitDetails(ctx, w.instanceId) : {}) });
  void session;
  return { status: "running", summary: `${id} (${key}) has no recorded outcome yet`, data: { write: id, state: "unknown" }, next: `job_status {"id":"${id}","wait":30} - never repeat the write`, doc: null };
}

function fromLedger(ctx: ToolContext, id: string, entry: LedgerEntry, tool: string, key: string, inst: InstanceInfo | null, w: WriteEntry | null): ToolOutcome {
  if (w) {
    w.state = entry.state === "committed" ? "committed" : "failed";
    w.outstanding = false;
  }
  const saved = entry.state === "committed" && entry.saved === false && !inst ? "; not saved unless the doc was saved afterwards" : "";
  if (entry.response) {
    const o = asOriginal(ctx, toolOfKey(entry.key, tool || "job_status"), {}, entry.key ?? key, entry.response, inst);
    return saved ? { ...o, notices: [...(o.notices ?? []), `BRIDGE_RESPONSE_RECOVERED ${id} from the ledger${saved}`] } : o;
  }
  const counts = entry.counts ? `: created ${entry.counts.created}, modified ${entry.counts.modified}, deleted ${entry.counts.deleted}` : "";
  if (entry.state === "committed") return { status: "ok", summary: `${id} committed${counts}${saved}`, data: { write: id, state: entry.state, counts: entry.counts, saved: entry.saved }, doc: null };
  return { status: "ok", summary: `${id} ended ${entry.state}; nothing of it is in the model`, data: { write: id, state: entry.state }, doc: null };
}

export const module: ToolModule = { name: "job_status", handle };
