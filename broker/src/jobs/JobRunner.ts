// Jobs (SPEC §6.8, D2 §12.5): job_start (primary pipe, asJob:true) answers {job:{jobId,state:"queued"}} at once;
// the broker then polls control job_status every 500 ms, backing off to 1 s, until the job is terminal or less than
// 3 s of the budget remain, forwarding progress on every stage change and at least every 3 s (SDK progress API via
// ctx.progress). Transient control failures (a busy control pipe) are retried; when the instance is gone or no longer
// knows the job, the job record the add-in persisted at <home>\jobs\<instanceId>\<jobId>.json answers instead, and a
// job without a terminal record on a dead instance is reported as interrupted.

import { readFile } from "node:fs/promises";
import { jobFile } from "@revit-mcp-next/contracts/home";
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
  /** The control pipe failed for good (e.g. an add-in error) and no persisted record exists. */
  failure: BridgeResponse | null;
}

const POLL_START_MS = 500;
const POLL_MAX_MS = 1_000;
const PROGRESS_EVERY_MS = 3_000;
const CONTROL_TIMEOUT_MS = 800;
/** Codes that mean "ask again": the control pipe was momentarily busy or the token was being re-read. */
const TRANSIENT = new Set(["BRIDGE_BUSY", "REVIT_QUEUE_FULL", "AUTH_MISMATCH", "REVIT_STARTING"]);
const MAX_TRANSIENT_IN_A_ROW = 8;

export async function pollJob(o: JobPollOptions): Promise<JobPollResult> {
  const reserve = o.reserveMs ?? 3_000;
  let interval = POLL_START_MS;
  let lastProgressAt = 0;
  let lastStage = "";
  let status: JobStatusData | null = null;
  let transientInARow = 0;
  for (;;) {
    const r = await o.channel.control("job_status", { jobId: o.jobId }, CONTROL_TIMEOUT_MS, o.clientKey);
    if (!r.ok) {
      const code = r.code ?? "INTERNAL_ERROR";
      if (TRANSIENT.has(code) && transientInARow < MAX_TRANSIENT_IN_A_ROW) {
        transientInARow += 1;
      } else {
        // Gone or forgotten: the persisted record is the truth.
        const persisted = await readJobRecord(o.channel.home, o.channel.instanceId, o.jobId);
        if (persisted && isTerminalJobState(persisted.state)) return { terminal: true, status: persisted, result: persisted.result ?? null, failure: null };
        // REVIT_EXITED without a terminal record: the job was interrupted (job_status reports it that way).
        return { terminal: false, status: persisted ?? status, result: null, failure: r };
      }
    } else {
      transientInARow = 0;
      status = (r.data as JobStatusData) ?? null;
      if (status && isTerminalJobState(status.state)) return { terminal: true, status, result: status.result ?? null, failure: null };
      const stage = status ? `${status.stage ?? status.state}${status.total ? ` ${status.done ?? 0}/${status.total}` : ""}` : "";
      const now = Date.now();
      if (o.progress && status && (stage !== lastStage || now - lastProgressAt >= PROGRESS_EVERY_MS)) {
        o.progress(stage || "running", status.done, status.total);
        lastStage = stage;
        lastProgressAt = now;
      }
    }
    if (o.deadline.remaining() <= reserve + interval || o.deadline.aborted) return { terminal: false, status, result: null, failure: null };
    if (!(await o.deadline.sleep(interval))) return { terminal: false, status, result: null, failure: null };
    interval = Math.min(POLL_MAX_MS, interval + 250);
  }
}

/** The job record the add-in persisted when the job reached a terminal state (null when absent or unreadable). */
export async function readJobRecord(home: string, instanceId: string, jobId: string): Promise<JobStatusData | null> {
  if (!home || !instanceId || !/^[A-Za-z0-9_.-]{1,80}$/.test(jobId) || jobId.includes("..")) return null;
  try {
    const text = await readFile(jobFile(home, instanceId, jobId), "utf8");
    const parsed = JSON.parse(text.charCodeAt(0) === 0xfeff ? text.slice(1) : text) as JobStatusData;
    if (!parsed || typeof parsed !== "object" || typeof parsed.state !== "string") return null;
    return parsed;
  } catch {
    return null;
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
