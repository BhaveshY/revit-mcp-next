// Jobs (SPEC §6.8, D2 §12.5): job_start (primary pipe, asJob:true) answers {job:{jobId,state:"queued"}} at once;
// the broker then polls control job_status every 500 ms (backing off to 1 s) until the job is terminal or less than
// 3 s of the budget remain, forwarding progress at least every 3 s. Basic implementation; P-REL-BROKER hardens it.

import { isTerminalJobState, type BridgeResponse, type JobStatusData } from "@revit-mcp-next/contracts/protocol";
import type { InstanceChannel } from "../instances/InstanceChannel.js";
import type { Deadline } from "../runtime/deadline.js";

export interface JobPollOptions {
  channel: InstanceChannel;
  jobId: string;
  deadline: Deadline;
  clientKey: string;
  progress?: (message: string, done?: number, total?: number) => void;
  /** Stop polling when less than this remains (default 3 s). */
  reserveMs?: number;
}

export interface JobPollResult {
  terminal: boolean;
  status: JobStatusData | null;
  /** The job's final response envelope (terminal jobs). */
  result: BridgeResponse | null;
  /** The control pipe failed (e.g. REVIT_EXITED). */
  failure: BridgeResponse | null;
}

export async function pollJob(o: JobPollOptions): Promise<JobPollResult> {
  const reserve = o.reserveMs ?? 3_000;
  let interval = 500;
  let lastProgressAt = 0;
  let lastStage = "";
  let status: JobStatusData | null = null;
  for (;;) {
    const r = await o.channel.control("job_status", { jobId: o.jobId }, 800, o.clientKey);
    if (!r.ok) {
      if (r.code === "BRIDGE_BUSY") {
        // transient: keep polling
      } else {
        return { terminal: false, status, result: null, failure: r };
      }
    } else {
      status = (r.data as JobStatusData) ?? null;
      if (status && isTerminalJobState(status.state)) return { terminal: true, status, result: status.result ?? null, failure: null };
      const stage = status ? `${status.stage ?? status.state}${status.total ? ` ${status.done ?? 0}/${status.total}` : ""}` : "";
      const now = Date.now();
      if (o.progress && status && (stage !== lastStage || now - lastProgressAt >= 3_000)) {
        o.progress(stage || "running", status.done, status.total);
        lastStage = stage;
        lastProgressAt = now;
      }
    }
    if (o.deadline.remaining() <= reserve + interval || o.deadline.aborted) return { terminal: false, status, result: null, failure: null };
    if (!(await o.deadline.sleep(interval))) return { terminal: false, status, result: null, failure: null };
    interval = Math.min(1_000, interval + 250);
  }
}

/** Human summary of a running job for `running: ... as job j#`. */
export function describeJob(status: JobStatusData | null, what: string): string {
  if (!status) return what;
  const parts: string[] = [what];
  if (status.stage) parts.push(status.stage);
  if (status.total) parts.push(`${status.done ?? 0}/${status.total}`);
  if (status.elapsedMs) parts.push(`${Math.round(status.elapsedMs / 1000)} s`);
  return parts.join(", ");
}
