// READ_STILL_RUNNING cache (SPEC §6.8): a read that could not finish within the budget keeps running; the broker
// keeps waiting in the background and caches the result for 10 min keyed by (key, canonical args, docKey).
// job_status j# or a repeat of the identical call returns it. Basic implementation; P-REL-BROKER hardens it.

import type { BridgeResponse } from "@revit-mcp-next/contracts/protocol";

const TTL_MS = 10 * 60_000;
const MAX_ENTRIES = 64;

export interface CachedRead {
  key: string;
  promise: Promise<BridgeResponse>;
  response: BridgeResponse | null;
  error: string | null;
  startedAt: number;
  settledAt: number | null;
}

export class ResultCache {
  private readonly entries = new Map<string, CachedRead>();

  /** Track a detached read. */
  put(key: string, promise: Promise<BridgeResponse>): CachedRead {
    this.prune();
    const entry: CachedRead = { key, promise, response: null, error: null, startedAt: Date.now(), settledAt: null };
    promise.then(
      (response) => {
        entry.response = response;
        entry.settledAt = Date.now();
      },
      (error) => {
        entry.error = (error as Error)?.message ?? String(error);
        entry.settledAt = Date.now();
      }
    );
    this.entries.set(key, entry);
    return entry;
  }

  get(key: string): CachedRead | undefined {
    this.prune();
    return this.entries.get(key);
  }

  /** Wait up to `ms` for the cached read; null when still running (or failed). */
  async wait(key: string, ms: number): Promise<BridgeResponse | null> {
    const entry = this.get(key);
    if (!entry) return null;
    if (entry.response) return entry.response;
    if (entry.error) return null;
    return new Promise((resolve) => {
      const timer = setTimeout(() => resolve(entry.response), Math.max(0, ms));
      timer.unref();
      entry.promise.then(
        (response) => {
          clearTimeout(timer);
          resolve(response);
        },
        () => {
          clearTimeout(timer);
          resolve(null);
        }
      );
    });
  }

  delete(key: string): void {
    this.entries.delete(key);
  }

  private prune(): void {
    const now = Date.now();
    for (const [key, entry] of this.entries) if (entry.settledAt !== null && now - entry.settledAt > TTL_MS) this.entries.delete(key);
    while (this.entries.size > MAX_ENTRIES) {
      const oldest = this.entries.keys().next().value;
      if (oldest === undefined) break;
      this.entries.delete(oldest);
    }
  }
}
