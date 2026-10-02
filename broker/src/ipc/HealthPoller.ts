// Health poller (D2 §11.2): control `health` every 1 s, only while this broker has a request in flight on the
// instance; one poller is shared by all waiters of a channel. Basic implementation; P-REL-BROKER owns hardening.

import type { HealthData } from "@revit-mcp-next/contracts/protocol";

export interface HealthSample {
  at: number;
  data: HealthData | null;
  /** Set when the control pipe did not answer. */
  error?: string;
}

export type HealthFetch = () => Promise<HealthData | null>;

export class HealthPoller {
  private listeners = new Set<(sample: HealthSample) => void>();
  private timer: NodeJS.Timeout | null = null;
  private polling = false;
  private last: HealthSample | null = null;

  constructor(private readonly fetch: HealthFetch, private readonly intervalMs = 1_000) {}

  latest(): HealthSample | null {
    return this.last;
  }

  /** Subscribe; polling runs while at least one subscriber exists. Returns the unsubscribe function. */
  subscribe(listener: (sample: HealthSample) => void): () => void {
    this.listeners.add(listener);
    if (!this.timer) this.schedule(0);
    return () => {
      this.listeners.delete(listener);
      if (this.listeners.size === 0 && this.timer) {
        clearTimeout(this.timer);
        this.timer = null;
      }
    };
  }

  /** One immediate sample (also used by status). */
  async sample(): Promise<HealthSample> {
    let sample: HealthSample;
    try {
      sample = { at: Date.now(), data: await this.fetch() };
    } catch (error) {
      sample = { at: Date.now(), data: null, error: (error as Error).message };
    }
    this.last = sample;
    return sample;
  }

  stop(): void {
    this.listeners.clear();
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  private schedule(delayMs: number): void {
    this.timer = setTimeout(() => void this.tick(), delayMs);
    this.timer.unref();
  }

  private async tick(): Promise<void> {
    if (this.polling) return;
    this.polling = true;
    try {
      const sample = await this.sample();
      for (const listener of [...this.listeners]) {
        try {
          listener(sample);
        } catch {
          // a listener must never break the poller
        }
      }
    } finally {
      this.polling = false;
      if (this.listeners.size > 0) this.schedule(this.intervalMs);
      else this.timer = null;
    }
  }
}
