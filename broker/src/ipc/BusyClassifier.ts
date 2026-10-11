// Busy classification and fail-fast (SPEC §8.8). While a request of this session is queued, control `health` samples
// (1 s) are evaluated in this order; the first match wins:
//   1. an MCP op is executing: this session's own outstanding write tool call on the same doc → keep waiting (the
//      read-after-write barrier); else, when the item is ≥5 s old, is a job step or a native op runs, fail with
//      REVIT_BUSY once this request has waited stall.busyFailFastMs (8 s); else keep waiting;
//   2. native != null (sync, open, save, export, print, reload) for 3 s → REVIT_BUSY with reason and progress;
//   3. mainWindowEnabled == false on two consecutive samples and the popup is not a progress window, for
//      stall.dialogMs → REVIT_DIALOG_OPEN (title, text, id, buttons);
//   4. hung for stall.hungMs → REVIT_NOT_RESPONDING;
//   5. nothing executing, enabled, not hung, no native, still pending, Idling silent for stall.editModeMs →
//      REVIT_EDIT_MODE_OR_COMMAND ("probably");
//   6. otherwise keep waiting; at the budget → REVIT_BUSY reason "not picked up".
// The channel cancels the queued item (control cancel_request) before it returns any of these, so "nothing ran" is true.

import type { ExecutingItem, HealthData } from "@revit-mcp-next/contracts/protocol";
import type { Settings } from "@revit-mcp-next/contracts/home";
import type { HealthSample } from "./HealthPoller.js";

export type BusyDecision =
  | { action: "wait"; progress?: string }
  | { action: "fail"; code: string; message: string; details: Record<string, unknown>; rule: number };

export interface BusyContext {
  requestId: string;
  clientKey: string;
  year: number;
  instanceId: string;
  stall: Settings["stall"];
  /** Session short id for an executing item when it is this session's work (j#/w#), else null. */
  refOf: (item: ExecutingItem) => string | null;
  /** True when the executing item is this session's own outstanding write tool call on the same doc (barrier). */
  isOwnOutstandingWrite: (item: ExecutingItem) => boolean;
  /** When the request was queued (default: construction time). */
  startedAt?: number;
}

const NATIVE_FAIL_MS = 3_000;
const LONG_EXECUTING_MS = 5_000;
const PROGRESS_TITLE = /progress|fortschritt|loading|laden|export|opening|öffnen|offnen|synchron/i;

export class BusyClassifier {
  private readonly startedAt: number;
  private prevDisabled = false;
  private disabledSince: number | null = null;
  private nativeSince: number | null = null;
  private hungSince: number | null = null;
  private lastData: HealthData | null = null;
  private samples = 0;

  constructor(private readonly ctx: BusyContext) {
    this.startedAt = ctx.startedAt ?? Date.now();
  }

  waitedMs(now = Date.now()): number {
    return now - this.startedAt;
  }

  get sampleCount(): number {
    return this.samples;
  }

  /** The last health sample seen (for BRIDGE_BUSY / REVIT_BUSY details). */
  get last(): HealthData | null {
    return this.lastData;
  }

  /** Evaluate one health sample. */
  push(sample: HealthSample): BusyDecision {
    const h = sample.data;
    if (!h) return { action: "wait" };
    this.samples += 1;
    this.lastData = h;
    const now = sample.at;
    const waited = this.waitedMs(now);
    const executing = h.queue?.executing ?? null;
    const disabled = h.ui ? h.ui.mainWindowEnabled === false : false;
    const popup = h.ui?.popup ?? null;
    const isProgress = popup ? popup.isProgress === true || PROGRESS_TITLE.test(popup.title ?? "") : false;
    const consecutiveDisabled = disabled && this.prevDisabled;
    if (disabled) this.disabledSince ??= now;
    else this.disabledSince = null;
    this.prevDisabled = disabled;
    if (h.native) this.nativeSince ??= now;
    else this.nativeSince = null;
    if (h.ui?.hung) this.hungSince ??= now;
    else this.hungSince = null;

    // Our own request is executing: nothing to classify (writes never yield; reads return partial at their deadline).
    if (executing && executing.requestId === this.ctx.requestId) return { action: "wait", progress: "running in Revit" };

    // Rule 1: another MCP op is executing.
    if (executing) {
      const ref = this.ctx.refOf(executing);
      if (this.ctx.isOwnOutstandingWrite(executing)) return { action: "wait", progress: `waiting for your write ${ref ?? executing.writeTag ?? ""}`.trim() };
      const elapsed = executing.elapsedMs ?? 0;
      const long = elapsed >= LONG_EXECUTING_MS || !!executing.jobId || !!h.native;
      if (long && waited >= this.ctx.stall.busyFailFastMs) return this.busy(executing, ref, h, executing.jobId ? "a job step is running" : h.native ? "a native operation is running" : "executing other work");
      return { action: "wait", progress: `waiting behind ${executing.op} (${Math.round(elapsed / 1000)} s)` };
    }

    // Rule 2: native operation (sync, open, save, export, print, reload).
    if (h.native) {
      if (now - this.nativeSince! >= NATIVE_FAIL_MS) {
        const p = h.native.progress;
        const progress = p?.caption ? ` (${p.caption}${p.max ? ` ${p.pos ?? 0}/${p.max}` : ""})` : p?.max ? ` (${p.pos ?? 0}/${p.max})` : "";
        const since = h.native.sinceUtc ? Math.round((Date.parse(h.native.sinceUtc) > 0 ? Date.now() - Date.parse(h.native.sinceUtc) : 0) / 1000) : 0;
        return {
          action: "fail",
          rule: 2,
          code: "REVIT_BUSY",
          message: `Revit ${this.ctx.year} is busy ${nativeText(h.native.kind)}${since > 0 ? ` (started ${since} s ago)` : ""}${progress}; nothing ran`,
          details: { instance: String(this.ctx.year), native: h.native, reason: h.native.kind },
        };
      }
      return { action: "wait", progress: `Revit is ${nativeText(h.native.kind)}` };
    }

    // Rule 3: a modal dialog (two consecutive disabled samples, not a progress window).
    if (consecutiveDisabled && !isProgress) {
      if (now - this.disabledSince! >= this.ctx.stall.dialogMs) {
        const title = popup?.title || h.lastDialog?.title || "a dialog";
        return {
          action: "fail",
          rule: 3,
          code: "REVIT_DIALOG_OPEN",
          message: `Revit ${this.ctx.year} shows "${title}"; nothing ran`,
          details: {
            instance: String(this.ctx.year),
            title,
            text: h.lastDialog && h.lastDialog.title === title ? h.lastDialog.text : undefined,
            dialogId: h.lastDialog && h.lastDialog.title === title ? h.lastDialog.dialogId : undefined,
          },
        };
      }
      return { action: "wait", progress: `Revit shows "${popup?.title ?? "a dialog"}"` };
    }
    if (disabled && isProgress) return { action: "wait", progress: `Revit shows a progress window${popup?.title ? ` ("${popup.title}")` : ""}` };

    // Rule 4: hung main window.
    if (h.ui?.hung) {
      if (now - this.hungSince! >= this.ctx.stall.hungMs)
        return { action: "fail", rule: 4, code: "REVIT_NOT_RESPONDING", message: `Revit ${this.ctx.year} has not responded for ${Math.round((now - this.hungSince!) / 1000)} s; nothing ran`, details: { instance: String(this.ctx.year) } };
      return { action: "wait", progress: "Revit is not responding" };
    }

    // Rule 5: probably in a command or edit mode (enabled, not hung, nothing executing, Idling silent).
    const pending = (h.queue?.pending ?? 0) > 0;
    // Without an Idling timestamp (an add-in that has not idled yet) edit mode cannot be told from a slow wake-up, so
    // the rule stays silent and the budget end reports "not picked up" instead.
    const idleAt = h.lastIdlingAtUtc ? Date.parse(h.lastIdlingAtUtc) : NaN;
    if (!disabled && pending && Number.isFinite(idleAt)) {
      const idleAge = now - idleAt;
      if (idleAge >= this.ctx.stall.editModeMs && waited >= this.ctx.stall.editModeMs)
        return {
          action: "fail",
          rule: 5,
          code: "REVIT_EDIT_MODE_OR_COMMAND",
          message: `Revit ${this.ctx.year} is probably in a command or edit mode (a sketch or placement): it has not been idle for ${Math.round(idleAge / 1000)} s; nothing ran`,
          details: { instance: String(this.ctx.year) },
        };
    }
    return { action: "wait" };
  }

  /** Rule 6: the budget ended and the request was never picked up. */
  atBudget(): BusyDecision & { action: "fail" } {
    const executing = this.lastData?.queue?.executing ?? null;
    if (executing && executing.requestId !== this.ctx.requestId) {
      const d = this.busy(executing, this.ctx.refOf(executing), this.lastData!, "not picked up");
      return { ...d, rule: 6 };
    }
    return {
      action: "fail",
      rule: 6,
      code: "REVIT_BUSY",
      message: `Revit ${this.ctx.year} did not pick up the request within the time budget; nothing ran`,
      details: { instance: String(this.ctx.year), reason: "not picked up" },
    };
  }

  private busy(executing: ExecutingItem, ref: string | null, h: HealthData, reason: string): BusyDecision & { action: "fail" } {
    const elapsedS = Math.round((executing.elapsedMs ?? 0) / 1000);
    const who = ref ? `your ${ref.startsWith("j") ? "job" : "write"} ${ref}` : executing.clientKey === this.ctx.clientKey ? "this session" : "another session";
    return {
      action: "fail",
      rule: 1,
      code: "REVIT_BUSY",
      message: `Revit ${this.ctx.year} is executing ${executing.op} for ${elapsedS} s (${who}); nothing ran`,
      details: { instance: String(this.ctx.year), executing: { op: executing.op, elapsedS, ref: ref ?? undefined }, ref: ref ?? undefined, native: h.native ?? undefined, reason },
    };
  }
}

export function nativeText(kind: string): string {
  switch (kind) {
    case "sync":
      return "synchronizing with central";
    case "opening":
      return "opening a document";
    case "saving":
      return "saving";
    case "exporting":
      return "exporting";
    case "printing":
      return "printing";
    case "reloading":
      return "reloading latest";
    default:
      return `in a native operation (${kind})`;
  }
}
