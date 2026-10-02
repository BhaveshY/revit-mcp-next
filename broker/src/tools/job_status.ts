// job_status (SPEC §6.6, §6.8, D1 §4B): j# = an add-in job or a read still running in the background; w# = a write
// whose outcome was unclear (add-in ledger). wait = seconds to wait (0-45, default 20). No id: this session's recent
// jobs and writes. A finished job renders like the original call's result. P-REL-BROKER hardens this.
import { isTerminalJobState, isTerminalLedgerState, type BridgeResponse, type LedgerEntry } from "@revit-mcp-next/contracts/protocol";
import { ULID_PATTERN } from "../runtime/ids.js";
import { pollJob, describeJob } from "../jobs/JobRunner.js";
import { searchLedgers } from "../state/Ledger.js";
import type { AddinResult, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

function waitMs(ctx: ToolContext): number {
  const wait = typeof ctx.args.wait === "number" ? ctx.args.wait : 20;
  return Math.max(0, Math.min(wait * 1000, ctx.deadline.remaining() - 2_500));
}

/** Render a finished call's response like the original call. */
function asOriginal(ctx: ToolContext, tool: string, args: Record<string, unknown>, key: string, response: BridgeResponse, instance: { instanceId: string; year: number; pid: number } | null): ToolOutcome {
  const original = ctx.forCall(tool, args);
  const r: AddinResult = { ...response, key, instanceId: instance?.instanceId, year: instance?.year, pid: instance?.pid };
  return original.outcomeFrom(r);
}

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  const raw = typeof ctx.args.id === "string" ? ctx.args.id.trim() : "";
  const session = ctx.session;
  if (!raw) {
    const jobs = session.jobs.values().slice(-10).map(([id, j]) => [id, j.what, j.jobId ? "job" : "read", new Date(j.at).toISOString()]);
    const writes = session.writes.values().slice(-10).map(([id, w]) => [id, w.key, w.state, new Date(w.at).toISOString()]);
    return {
      status: "ok",
      summary: `${jobs.length} recent job${jobs.length === 1 ? "" : "s"} and ${writes.length} write${writes.length === 1 ? "" : "s"} in this session`,
      data: { jobs: { cols: ["id", "what", "kind", "at"], rows: jobs }, writes: { cols: ["id", "key", "state", "at"], rows: writes } },
      doc: null,
      next: jobs.length || writes.length ? `job_status {"id":"${(jobs.at(-1) ?? writes.at(-1))![0]}"}` : undefined,
    };
  }
  const id = raw.toLowerCase();

  // ---------------------------------------------------------------- j#
  if (session.jobs.matches(id)) {
    const job = session.jobs.get(id);
    if (!job) return ctx.fail("JOB_UNKNOWN", `job ${id} is unknown to this session (job ids do not survive a broker restart)`);
    const live = await ctx.instances();
    const inst = live.find((i) => i.instanceId === job.instanceId) ?? null;
    if (job.cacheKey) {
      const response = await ctx.services.results.wait(job.cacheKey, waitMs(ctx));
      if (response) {
        ctx.services.results.delete(job.cacheKey);
        return asOriginal(ctx, job.tool, job.args, job.key, response, inst);
      }
      const entry = ctx.services.results.get(job.cacheKey);
      if (!entry || entry.error) return ctx.fail("JOB_UNKNOWN", `the background read ${id} ended without a result${entry?.error ? ` (${entry.error})` : ""}`);
      return {
        status: "running",
        summary: `${job.what} is still running in Revit ${inst?.year ?? ""} (${Math.round((Date.now() - job.at) / 1000)} s) as ${id}`.replace("  ", " "),
        data: { job: id, state: "running" },
        next: `job_status {"id":"${id}"} - or repeat the original call: it returns the cached result`,
        doc: null,
      };
    }
    if (!inst) return ctx.fail("REVIT_EXITED", `Revit exited while job ${id} (${job.what}) was running`, {});
    const channel = ctx.services.channels.get(inst);
    const polled = await pollJob({ channel, jobId: job.jobId!, deadline: ctx.deadline.child(waitMs(ctx) + 1_000), clientKey: session.clientKey, reserveMs: 500, progress: (m, d, t) => ctx.progress(m, d, t) });
    if (polled.failure) return ctx.failFrom(polled.failure);
    if (polled.terminal) {
      if (polled.result) return asOriginal(ctx, job.tool, job.args, job.key, polled.result, inst);
      const state = polled.status?.state ?? "succeeded";
      if (state === "succeeded") return { status: "ok", summary: `${job.what} finished`, data: { job: id, state }, doc: null };
      if (state === "cancelled") return { status: "ok", summary: `${job.what} was cancelled`, data: { job: id, state }, doc: null };
      return ctx.fail("INTERNAL_ERROR", `${job.what} ended ${state}`, { job: id, state });
    }
    return {
      status: "running",
      summary: `${describeJob(polled.status, job.what)} as ${id}`,
      data: { job: id, state: polled.status?.state ?? "running", stage: polled.status?.stage, done: polled.status?.done, total: polled.status?.total },
      next: `job_status {"id":"${id}","wait":30} - do not repeat the original call`,
      doc: null,
    };
  }

  // ---------------------------------------------------------------- w# and raw request ids
  let requestId: string | null = null;
  let instanceId: string | null = null;
  let key = "write";
  let tool = "";
  if (session.writes.matches(id)) {
    const w = session.writes.get(id);
    if (!w) return ctx.fail("JOB_UNKNOWN", `write ${id} is unknown to this session (write ids do not survive a broker restart)`);
    requestId = w.requestId;
    instanceId = w.instanceId;
    key = w.key;
    tool = w.tool;
  } else if (ULID_PATTERN.test(raw.toUpperCase())) {
    requestId = raw.toUpperCase();
  } else {
    return ctx.fail("INVALID_ARGS", `id must be a job id j# or a write id w#, got ${raw}`, { param: "id", reason: "not a j# or w#", example: { id: "j1" } });
  }
  const live = await ctx.instances();
  const candidates = instanceId ? live.filter((i) => i.instanceId === instanceId) : live;
  const end = Date.now() + waitMs(ctx);
  for (;;) {
    for (const inst of candidates) {
      const r = await ctx.control(inst, "get_request_result", { requestId }, 800);
      if (r.ok && r.data && typeof r.data === "object") {
        const entry = r.data as LedgerEntry;
        if (isTerminalLedgerState(entry.state)) {
          if (session.writes.has(id)) {
            const w = session.writes.get(id)!;
            w.state = entry.state === "committed" ? "committed" : "failed";
            w.outstanding = false;
          }
          if (entry.response) return asOriginal(ctx, tool || entry.key?.split(".")[0] || "job_status", {}, entry.key ?? key, entry.response, inst);
          return { status: "ok", summary: `${id} ${entry.state}${entry.counts ? `: created ${entry.counts.created}, modified ${entry.counts.modified}, deleted ${entry.counts.deleted}` : ""}`, data: { write: id, state: entry.state, counts: entry.counts, saved: entry.saved }, doc: null };
        }
        if (Date.now() >= end) return { status: "running", summary: `${id} (${key}) is still running`, data: { write: id, state: entry.state }, next: `job_status {"id":"${id}","wait":30} - never repeat the write`, doc: null };
      }
    }
    if (candidates.length === 0 || Date.now() >= end) break;
    if (!(await ctx.deadline.sleep(500))) break;
  }
  // The instance is gone (or never answered): search the ledger files.
  const entry = await searchLedgers(ctx.services.home, requestId, instanceId ?? undefined);
  if (entry && isTerminalLedgerState(entry.state)) {
    if (entry.response) return asOriginal(ctx, tool || "job_status", {}, entry.key ?? key, entry.response, null);
    return { status: "ok", summary: `${id} ${entry.state} (from the ledger)${entry.saved === false ? "; not saved unless the doc was saved afterwards" : ""}`, data: { write: id, state: entry.state, counts: entry.counts }, doc: null };
  }
  if (candidates.length === 0) return ctx.fail("WRITE_OUTCOME_UNKNOWN", `Revit is gone and the ledger has no outcome for ${id} (${key})`, { ref: id });
  return { status: "running", summary: `${id} (${key}) has no recorded outcome yet`, data: { write: id, state: "unknown" }, next: `job_status {"id":"${id}","wait":30} - never repeat the write`, doc: null };
}

export const module: ToolModule = { name: "job_status", handle };
void isTerminalJobState;
