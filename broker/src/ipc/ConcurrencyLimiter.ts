// Per-instance concurrency limiter (D2 §11.3, SPEC §8.9): FIFO slots (primary = settings.perInstancePrimarySlots,
// default 4; control = 2). The wait counts against the call deadline and ends after min(8 s, remaining − 5 s); the
// channel then answers BRIDGE_BUSY with {mine, others, executingOp, executingForMs, pending}. Nothing is sent while a
// call waits here. Slot changes from settings apply to the next acquire; waiters are never reordered.

import type { Deadline } from "../runtime/deadline.js";

interface Waiter {
  resolve: (release: (() => void) | null) => void;
  timer: NodeJS.Timeout;
  cleanup: () => void;
}

export const LIMITER_MAX_WAIT_MS = 8_000;
export const LIMITER_RESERVE_MS = 5_000;

export class ConcurrencyLimiter {
  private activeCount = 0;
  private readonly queue: Waiter[] = [];
  private slots: number;
  /** Counters for status detail:full. */
  readonly stats = { acquired: 0, waited: 0, timedOut: 0, maxWaitMs: 0 };

  constructor(slots: number) {
    this.slots = Math.max(1, Math.floor(slots) || 1);
  }

  setSlots(slots: number): void {
    const next = Math.max(1, Math.floor(slots) || 1);
    if (next === this.slots) return;
    this.slots = next;
    this.drain();
  }

  get capacity(): number {
    return this.slots;
  }

  get active(): number {
    return this.activeCount;
  }

  get waiting(): number {
    return this.queue.length;
  }

  /** The longest a call may wait for a slot: min(8 s, remaining − 5 s), at least 0. */
  static maxWaitMs(deadline: Deadline): number {
    return Math.max(0, Math.min(LIMITER_MAX_WAIT_MS, deadline.remaining() - LIMITER_RESERVE_MS));
  }

  /** Resolves with a release function, or null when no slot freed up within `maxWaitMs` (or the signal aborted). */
  acquire(maxWaitMs: number, signal?: AbortSignal): Promise<(() => void) | null> {
    if (this.activeCount < this.slots && this.queue.length === 0) return Promise.resolve(this.take(0));
    if (maxWaitMs <= 0 || signal?.aborted) {
      this.stats.timedOut += 1;
      return Promise.resolve(null);
    }
    const queuedAt = Date.now();
    this.stats.waited += 1;
    return new Promise((resolve) => {
      const onAbort = () => remove(false);
      const remove = (timedOut: boolean) => {
        const i = this.queue.indexOf(waiter);
        if (i < 0) return;
        this.queue.splice(i, 1);
        waiter.cleanup();
        if (timedOut) this.stats.timedOut += 1;
        resolve(null);
      };
      const waiter: Waiter = {
        resolve: (release) => {
          waiter.cleanup();
          if (release) this.noteWait(Date.now() - queuedAt);
          resolve(release);
        },
        timer: setTimeout(() => remove(true), maxWaitMs),
        cleanup: () => {
          clearTimeout(waiter.timer);
          signal?.removeEventListener("abort", onAbort);
        },
      };
      waiter.timer.unref();
      this.queue.push(waiter);
      signal?.addEventListener("abort", onAbort, { once: true });
    });
  }

  private noteWait(ms: number): void {
    if (ms > this.stats.maxWaitMs) this.stats.maxWaitMs = ms;
  }

  private take(waitedMs: number): () => void {
    this.activeCount += 1;
    this.stats.acquired += 1;
    this.noteWait(waitedMs);
    let released = false;
    return () => {
      if (released) return;
      released = true;
      this.activeCount -= 1;
      this.drain();
    };
  }

  private drain(): void {
    while (this.activeCount < this.slots && this.queue.length > 0) {
      const waiter = this.queue.shift()!;
      this.activeCount += 1;
      this.stats.acquired += 1;
      let released = false;
      waiter.resolve(() => {
        if (released) return;
        released = true;
        this.activeCount -= 1;
        this.drain();
      });
    }
  }
}
