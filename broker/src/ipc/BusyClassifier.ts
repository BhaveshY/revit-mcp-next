// Busy classification and fail-fast (SPEC §8.8). While a request of this session is queued, health samples are
// evaluated in rule order; the first match wins. The caller cancels the queued item (control cancel_request) before
// returning the error, so "nothing ran" is true. Basic implementation; P-REL-BROKER owns hardening.

import type { ExecutingItem, HealthData } from "@revit-mcp-next/contracts/protocol";
import type { Settings } from "@revit-mcp-next/contracts/home";
import type { HealthSample } from "./HealthPoller.js";

export type BusyDecision =
  | { action: "wait"; progress?: string }
  | { action: "fail"; code: string; message: string; details: Record<string, unknown> };

export interface BusyContext {
  requestId: string;
  clientKey: string;
  year: number;
  instanceId: string;
  stall: Settings["stall"];
  /** Session short id for an executing item when it is this session's work (j#/w#), else null. */
  refOf: (item: ExecutingItem) => string | null;
  /** True when the executing item is this session's own outstanding write tool call (read-after-write barrier). */
  isOwnOutstandingWrite: (item: ExecutingItem) => boolean;
}

const PROGRESS_TITLE = /progress|fortschritt|loading|laden|export|opening|öffnen|synchron/i;

export class BusyClassifier {
  private readonly startedAt = Date.now();
  private prevDisabled = false;
  private dialogSince: number | null = null;
  private nativeSince: number | null = null;
  private hungSince: number | null = null;
  private lastData: HealthData | null = null;

  constructor(private readonly ctx: BusyContext) {}

  waitedMs(now = Date.now()): number {
    return now - this.startedAt;
  }

  /** Evaluate one health sample. */
  push(sample: HealthSample): BusyDecision {
    const h = sample.data;
    if (!h) return { action: "wait" };
    this.lastData = h;
    const now = sample.at;
    const waited = this.waitedMs(now);
    const executing = h.queue?.executing ?? null;

    // Our own request is executing: nothing to classify (writes never yield; reads return partial at their deadline).
    if (executing && executing.requestId === this.ctx.requestId) return { action: "wait", progress: "running in Revit" };

    // Rule 1: another MCP op is executing.
    if (executing) {
      const ref = this.ctx.refOf(executing);
      if (this.ctx.isOwnOutstandingWrite(executing)) return { action: "wait", progress: `waiting for your write ${ref ?? executing.writeTag ?? ""}`.trim() };
      const long = (executing.elapsedMs ?? 0) >= 5_000 || !!executing.jobId || !!h.native;
      if (long && waited >= this.ctx.stall.busyFailFastMs) return this.busy(executing, ref, h, "executing other work");
      return { action: "wait", progress: `waiting behind ${executing.op} (${Math.round((executing.elapsedMs ?? 0) / 1000)} s)` };
    }

    // Rule 2: native operation (sync, open, save, export, print, reload).
    if (h.native) {
      this.nativeSince ??= now;
      if (now - this.nativeSince >= 3_000) {
        const progress = h.native.progress?.caption ? ` (${h.native.progress.caption}${h.native.progress.max ? ` ${h.native.progress.pos ?? 0}/${h.native.progress.max}` : ""})` : "";
        return {
          action: "fail",
          code: "REVIT_BUSY",
          message: `Revit ${this.ctx.year} is busy ${nativeText(h.native.kind)}${progress}; nothing ran`,
          details: { instance: String(this.ctx.year), native: h.native, reason: h.native.kind },
        };
      }
      return { action: "wait", progress: `Revit is ${nativeText(h.native.kind)}` };
    }
    this.nativeSince = null;

    // Rule 3: a modal dialog (two consecutive disabled samples, not a progress window).
    const disabled = h.ui ? h.ui.mainWindowEnabled === false : false;
    const popup = h.ui?.popup ?? null;
    const isProgress = popup ? popup.isProgress === true || PROGRESS_TITLE.test(popup.title ?? "") : false;
    if (disabled && this.prevDisabled && !isProgress) {
      this.dialogSince ??= now;
      if (now - this.dialogSince >= this.ctx.stall.dialogMs) {
        const title = popup?.title ?? h.lastDialog?.title ?? "a dialog";
        return {
          action: "fail",
          code: "REVIT_DIALOG_OPEN",
          message: `Revit ${this.ctx.year} shows "${title}"; nothing ran`,
          details: {
            instance: String(this.ctx.year),
            title,
            text: h.lastDialog?.text,
            dialogId: h.lastDialog?.dialogId,
          },
        };
      }
    } else if (!disabled) {
      this.dialogSince = null;
    }
    this.prevDisabled = disabled;

    // Rule 4: hung main window.
    if (h.ui?.hung) {
      this.hungSince ??= now;
      if (now - this.hungSince >= this.ctx.stall.hungMs)
        return { action: "fail", code: "REVIT_NOT_RESPONDING", message: `Revit ${this.ctx.year} is not responding; nothing ran`, details: { instance: String(this.ctx.year) } };
      return { action: "wait", progress: "Revit is not responding" };
    }
    this.hungSince = null;

    // Rule 5: probably in a command or edit mode (enabled, not hung, nothing executing, Idling silent).
    const pending = (h.queue?.pending ?? 0) > 0;
    if (!disabled && pending && h.lastIdlingAtUtc) {
      const idleAge = now - Date.parse(h.lastIdlingAtUtc);
      if (Number.isFinite(idleAge) && idleAge >= this.ctx.stall.editModeMs && waited >= this.ctx.stall.editModeMs)
        return {
          action: "fail",
          code: "REVIT_EDIT_MODE_OR_COMMAND",
          message: `Revit ${this.ctx.year} is probably in a command or edit mode (no idle time for ${Math.round(idleAge / 1000)} s); nothing ran`,
          details: { instance: String(this.ctx.year) },
        };
    }
    return { action: "wait" };
  }

  /** Rule 6: the budget ended and the request was never picked up. */
  atBudget(): BusyDecision {
    const executing = this.lastData?.queue?.executing ?? null;
    if (executing && executing.requestId !== this.ctx.requestId) return this.busy(executing, this.ctx.refOf(executing), this.lastData!, "not picked up");
    return {
      action: "fail",
      code: "REVIT_BUSY",
      message: `Revit ${this.ctx.year} did not pick up the request within the time budget; nothing ran`,
      details: { instance: String(this.ctx.year), reason: "not picked up" },
    };
  }

  private busy(executing: ExecutingItem, ref: string | null, h: HealthData, reason: string): BusyDecision {
    const elapsedS = Math.round((executing.elapsedMs ?? 0) / 1000);
    const who = ref ? `your ${ref}` : executing.clientKey === this.ctx.clientKey ? "this session" : "another session";
    return {
      action: "fail",
      code: "REVIT_BUSY",
      message: `Revit ${this.ctx.year} is executing ${executing.op} for ${elapsedS} s (${who}); nothing ran`,
      details: { instance: String(this.ctx.year), executing: { op: executing.op, elapsedS, ref: ref ?? undefined }, ref: ref ?? undefined, native: h.native ?? undefined, reason },
    };
  }
}

function nativeText(kind: string): string {
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
