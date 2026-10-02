// Per-call time budget (SPEC §6.8, D2 §12.1). Every tool call answers within its Deadline; sub-calls get children.

import { CALL_BUDGET_MAX_MS, CALL_BUDGET_MIN_MS } from "@revit-mcp-next/contracts/home";

/** callBudgetMs clamped to 10-55 s (§3.1). */
export function clampBudget(ms: number | undefined): number {
  if (typeof ms !== "number" || !Number.isFinite(ms)) return 50_000;
  return Math.min(CALL_BUDGET_MAX_MS, Math.max(CALL_BUDGET_MIN_MS, Math.round(ms)));
}

/** Margin kept between the bridge timeout and the call budget (D2 §12.2). */
export const BRIDGE_MARGIN_MS = 1_500;

export class Deadline {
  readonly startedAt: number;
  readonly at: number;
  private readonly parent?: AbortSignal;
  private cachedSignal?: AbortSignal;

  private constructor(budgetMs: number, parent?: AbortSignal, startedAt = Date.now()) {
    this.startedAt = startedAt;
    this.at = startedAt + Math.max(0, budgetMs);
    this.parent = parent;
  }

  /** A new deadline `budgetMs` from now, aborted early when `parent` aborts (client cancellation). */
  static start(budgetMs: number, parent?: AbortSignal): Deadline {
    return new Deadline(budgetMs, parent);
  }

  get budgetMs(): number {
    return this.at - this.startedAt;
  }

  remaining(): number {
    return Math.max(0, this.at - Date.now());
  }

  elapsed(): number {
    return Date.now() - this.startedAt;
  }

  get expired(): boolean {
    return this.remaining() <= 0 || this.aborted;
  }

  /** The client cancelled the call. */
  get aborted(): boolean {
    return this.parent?.aborted === true;
  }

  /** Aborts at the deadline or when the client cancels. */
  get signal(): AbortSignal {
    if (!this.cachedSignal) {
      const timeout = AbortSignal.timeout(Math.max(1, this.remaining()));
      this.cachedSignal = this.parent ? AbortSignal.any([this.parent, timeout]) : timeout;
    }
    return this.cachedSignal;
  }

  /** A child deadline capped at `maxMs` (never later than this one). */
  child(maxMs: number): Deadline {
    const budget = Math.max(0, Math.min(this.remaining(), maxMs));
    return new Deadline(budget, this.parent);
  }

  /** Child deadline that ends `marginMs` before this one. */
  withMargin(marginMs: number): Deadline {
    return new Deadline(Math.max(0, this.remaining() - marginMs), this.parent);
  }

  /** read_many share: sub-call i of n gets remaining/(n-i). */
  share(index: number, count: number): Deadline {
    const parts = Math.max(1, count - index);
    return new Deadline(Math.floor(this.remaining() / parts), this.parent);
  }

  /** Sleep up to `ms`, returning early (false) when the deadline passes or the call is cancelled. */
  async sleep(ms: number): Promise<boolean> {
    const wait = Math.min(ms, this.remaining());
    if (wait <= 0 || this.aborted) return false;
    await new Promise<void>((resolve) => {
      const timer = setTimeout(resolve, wait);
      timer.unref?.();
      this.parent?.addEventListener("abort", () => {
        clearTimeout(timer);
        resolve();
      }, { once: true });
    });
    return !this.expired;
  }
}

export function delay(ms: number): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, Math.max(0, ms));
    timer.unref?.();
  });
}
