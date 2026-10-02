// cancel_job (SPEC §6.6, D1 §4B): cancels a queued or running job (cooperative, between steps) or a queued request.
// A running Revit step finishes or rolls back safely; partial exports and delivery staging are removed by the op.
import type { ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  const id = String(ctx.args.id ?? "").trim().toLowerCase();
  const reason = typeof ctx.args.reason === "string" ? ctx.args.reason : undefined;
  const session = ctx.session;
  ctx.services.log.info("cancel_job", { id, reason, client: session.clientKey });
  const live = await ctx.instances();

  if (session.jobs.matches(id)) {
    const job = session.jobs.get(id);
    if (!job) return ctx.fail("JOB_UNKNOWN", `job ${id} is unknown to this session`);
    const inst = live.find((i) => i.instanceId === job.instanceId);
    if (!inst) return { status: "ok", summary: `${id} cannot run any more: its Revit exited`, data: { job: id, cancelled: false }, doc: null };
    if (job.jobId) {
      const r = await ctx.control(inst, "job_cancel", { jobId: job.jobId }, 2_000);
      if (!r.ok) return ctx.failFrom(r);
      const cancelled = (r.data as { cancelled?: unknown } | null)?.cancelled;
      return { status: "ok", summary: `cancel requested for ${id} (${job.what}); a running step finishes or rolls back`, data: { job: id, cancelled }, next: `job_status {"id":"${id}"}`, doc: null };
    }
    if (job.requestId) {
      const r = await ctx.control(inst, "cancel_request", { requestId: job.requestId }, 2_000);
      if (!r.ok) return ctx.failFrom(r);
      return { status: "ok", summary: `asked ${id} (${job.what}) to stop`, data: { job: id, cancelled: (r.data as { cancelled?: unknown } | null)?.cancelled }, doc: null };
    }
    return { status: "ok", summary: `${id} has nothing to cancel`, data: { job: id, cancelled: false }, doc: null };
  }

  if (session.writes.matches(id)) {
    const w = session.writes.get(id);
    if (!w) return ctx.fail("JOB_UNKNOWN", `write ${id} is unknown to this session`);
    const inst = live.find((i) => i.instanceId === w.instanceId);
    if (!inst) return { status: "ok", summary: `${id} cannot be cancelled: its Revit exited`, data: { write: id, cancelled: false }, next: `job_status {"id":"${id}"}`, doc: null };
    const r = await ctx.control(inst, "cancel_request", { requestId: w.requestId }, 2_000);
    if (!r.ok) return ctx.failFrom(r);
    const cancelled = String((r.data as { cancelled?: unknown } | null)?.cancelled ?? "not_found");
    if (cancelled === "queued") {
      w.state = "failed";
      w.outstanding = false;
      return { status: "ok", summary: `${id} was cancelled before it ran; nothing changed`, data: { write: id, cancelled }, doc: null };
    }
    if (cancelled === "running_write")
      return { status: "ok", summary: `${id} is already running in Revit; a running write finishes or rolls back`, data: { write: id, cancelled }, next: `job_status {"id":"${id}"} - never repeat the write`, doc: null };
    return { status: "ok", summary: `${id} is not queued any more`, data: { write: id, cancelled }, next: `job_status {"id":"${id}"}`, doc: null };
  }
  return ctx.fail("INVALID_ARGS", `id must be a job id j# or a write id w#, got ${id}`, { param: "id", reason: "not a j# or w#", example: { id: "j1" } });
}

export const module: ToolModule = { name: "cancel_job", handle };
