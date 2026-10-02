// Per-instance concurrency limiter (D2 §11.3): FIFO slots; the wait counts against the call deadline and ends with
// BRIDGE_BUSY after min(8 s, remaining − 5 s) (SPEC §8.9). Basic implementation; P-REL-BROKER owns hardening.

import type { Deadline } from "../runtime/deadline.js";

interface Waiter {
  resolve: (release: (() => void) | null) => void;
  timer: NodeJS.Timeout;
}

export class ConcurrencyLimiter {
  private activeCount = 0;
  private readonly queue: Waiter[] = [];

  constructor(private slots: number) {}

  setSlots(slots: number): void {
    this.slots = Math.max(1, slots);
    this.drain();
  }

  get active(): number {
    return this.activeCount;
  }

  get waiting(): number {
    return this.queue.length;
  }

  /** The longest a call may wait for a slot: min(8 s, remaining − 5 s), at least 0. */
  static maxWaitMs(deadline: Deadline): number {
    return Math.max(0, Math.min(8_000, deadline.remaining() - 5_000));
  }

  /** Resolves with a release function, or null when no slot freed up within `maxWaitMs`. */
  acquire(maxWaitMs: number, signal?: AbortSignal): Promise<(() => void) | null> {
    if (this.activeCount < this.slots && this.queue.length === 0) return Promise.resolve(this.take());
    if (maxWaitMs <= 0 || signal?.aborted) return Promise.resolve(null);
    return new Promise((resolve) => {
      const waiter: Waiter = {
        resolve,
        timer: setTimeout(() => {
          const i = this.queue.indexOf(waiter);
          if (i >= 0) this.queue.splice(i, 1);
          resolve(null);
        }, maxWaitMs),
      };
      waiter.timer.unref();
      this.queue.push(waiter);
      signal?.addEventListener(
        "abort",
        () => {
          const i = this.queue.indexOf(waiter);
          if (i >= 0) {
            this.queue.splice(i, 1);
            clearTimeout(waiter.timer);
            resolve(null);
          }
        },
        { once: true }
      );
    });
  }

  private take(): () => void {
    this.activeCount += 1;
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
      clearTimeout(waiter.timer);
      waiter.resolve(this.take());
    }
  }
}
