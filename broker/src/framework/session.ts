// Per-connection session state (SPEC §6.6, §7.1): pin/projectPin, doc numbering (#n, stable, never reused),
// short-id maps (r# p# c# w# j# m#), confirm tokens, last write per doc, TARGET_CHANGED acks, once-only notices,
// the set_target barrier and the read-after-write barrier. Nothing here touches Revit. Frozen in wave 2.

import type { DocKind, ExecutingItem } from "@revit-mcp-next/contracts/protocol";
import { ConfirmTokenStore } from "./confirm.js";

export type PinMode = "auto" | "explicit" | "follow";

export interface Pin {
  key: string;
  rid: number;
  instanceId: string;
  pid: number;
  year: number;
  mode: PinMode;
  title: string;
  kind: DocKind;
  path?: string;
  setAt: number;
}

/** A short-id map: prefix + never-reused counter, TTL and LRU size limit. */
export class ShortIdMap<T> {
  private counter = 0;
  private readonly entries = new Map<string, { value: T; at: number; touched: number }>();

  constructor(readonly prefix: string, readonly ttlMs: number, readonly maxEntries: number) {}

  /** Allocate the next id (e.g. r7) for a value. */
  add(value: T): string {
    this.prune();
    this.counter += 1;
    const id = `${this.prefix}${this.counter}`;
    const now = Date.now();
    this.entries.set(id, { value, at: now, touched: now });
    while (this.entries.size > this.maxEntries) {
      let oldestId: string | null = null;
      let oldest = Infinity;
      for (const [k, e] of this.entries) if (e.touched < oldest) {
        oldest = e.touched;
        oldestId = k;
      }
      if (oldestId === null) break;
      this.entries.delete(oldestId);
    }
    return id;
  }

  /** Store under an explicit id (used when the id was allocated earlier, e.g. w# before sending). */
  set(id: string, value: T): void {
    const now = Date.now();
    this.entries.set(id, { value, at: now, touched: now });
  }

  /** Reserve the next id without storing a value yet. */
  next(): string {
    this.counter += 1;
    return `${this.prefix}${this.counter}`;
  }

  get(id: string): T | undefined {
    const normalized = id.trim().toLowerCase();
    const entry = this.entries.get(normalized);
    if (!entry) return undefined;
    if (this.ttlMs > 0 && Date.now() - entry.at > this.ttlMs) {
      this.entries.delete(normalized);
      return undefined;
    }
    entry.touched = Date.now();
    return entry.value;
  }

  has(id: string): boolean {
    return this.get(id) !== undefined;
  }

  delete(id: string): void {
    this.entries.delete(id.trim().toLowerCase());
  }

  /** Was this id ever allocated by this session (to tell expired from never-existed)? */
  wasIssued(id: string): boolean {
    const m = new RegExp(`^${this.prefix}(\\d+)$`, "i").exec(id.trim());
    return !!m && Number(m[1]) >= 1 && Number(m[1]) <= this.counter;
  }

  values(): Array<[string, T]> {
    this.prune();
    return [...this.entries.entries()].map(([k, e]) => [k, e.value]);
  }

  matches(id: unknown): boolean {
    return typeof id === "string" && new RegExp(`^${this.prefix}\\d+$`, "i").test(id.trim());
  }

  private prune(): void {
    if (this.ttlMs <= 0) return;
    const now = Date.now();
    for (const [k, e] of this.entries) if (now - e.at > this.ttlMs) this.entries.delete(k);
  }
}

export interface HandleEntry {
  docKey: string;
  rid: number;
  instanceId: string;
  ids: number[];
  total: number;
  /** Elements of a linked model (read-only). */
  link?: string;
  tool: string;
  args: Record<string, unknown>;
}

export interface PageEntry {
  tool: string;
  key: string;
  args: Record<string, unknown>;
  handle?: string;
  offset: number;
  total?: number;
  /** Add-in resume state for partial results. */
  resume?: Record<string, unknown>;
  docKey: string | null;
}

export interface CaptureEntry {
  folder?: string;
  file: string;
  mime: string;
  view?: unknown;
  size?: unknown;
  region?: unknown;
  docKey: string | null;
  meta?: Record<string, unknown>;
  w?: number;
  h?: number;
}

export interface WriteEntry {
  instanceId: string;
  requestId: string;
  key: string;
  tool: string;
  docKey: string | null;
  at: number;
  state: "sent" | "committed" | "failed" | "running" | "unknown" | "not_applied";
  /** Still an outstanding tool call (read-after-write barrier). */
  outstanding: boolean;
  summary?: string;
}

export interface JobEntry {
  instanceId: string;
  /** Add-in job id (job:always ops) — never shown. */
  jobId?: string;
  /** READ_STILL_RUNNING: the cache key of the detached read. */
  cacheKey?: string;
  /** Requests whose outcome is in the ledger. */
  requestId?: string;
  key: string;
  tool: string;
  args: Record<string, unknown>;
  docKey: string | null;
  at: number;
  what: string;
}

export interface LastWrite {
  ids: number[];
  write: string;
  at: number;
  rid: number;
  instanceId: string;
}

export interface ClientInfo {
  name: string;
  version: string;
  protocol: string;
  /** Client declared form elicitation. */
  elicitation: boolean;
}

export class Session {
  readonly id: string;
  readonly createdAt = Date.now();
  client: ClientInfo = { name: "unknown-client", version: "", protocol: "", elicitation: false };

  pin: Pin | null = null;
  /** Most recent project pin (edit_family load_into, project-only tools while the pin is a family doc). */
  projectPin: Pin | null = null;

  private docCounter = 0;
  private readonly docNumbersByKey = new Map<string, number>();

  readonly handles = new ShortIdMap<HandleEntry>("r", 60 * 60_000, 64);
  readonly pages = new ShortIdMap<PageEntry>("p", 30 * 60_000, 128);
  readonly captures = new ShortIdMap<CaptureEntry>("c", 0, 200);
  readonly writes = new ShortIdMap<WriteEntry>("w", 0, 1_000);
  readonly jobs = new ShortIdMap<JobEntry>("j", 24 * 60 * 60_000, 256);
  readonly confirms = new ConfirmTokenStore();

  /** docKey → last successful write (selector "last"). */
  readonly lastWrites = new Map<string, LastWrite>();
  /** Key of the doc of this session's previous write (follow-mode gate) and when. */
  lastWriteDocKey: string | null = null;
  lastWriteAt = 0;
  /** instanceId → active doc key seen at this session's last write (active-switch gate). */
  readonly activeAtLastWrite = new Map<string, string | null>();
  /** TARGET_CHANGED acknowledgements: ack key → expiry (10 min). */
  private readonly acks = new Map<string, number>();
  /** Once-only notice keys. */
  private readonly shownNotices = new Set<string>();
  /** Notices to prepend to the next result (late results, recovered writes). */
  readonly pendingNotices: string[] = [];
  /** Per docKey: m# marks issued (mark → generation). */
  readonly marks = new Map<string, { docKey: string; rid: number; generation: number }>();
  private markCounter = 0;

  private barrier: Promise<void> = Promise.resolve();

  constructor(id: string) {
    this.id = id;
  }

  get clientKey(): string {
    return `${this.client.name || "unknown-client"}#${process.pid}`;
  }

  // ------------------------------------------------------------------ doc numbers (#n): stable per session, never reused

  /** Session number of a doc key; aliases (Save As) map to the same number. */
  docNumber(key: string, aliases: string[] = []): number {
    const known = this.docNumbersByKey.get(key);
    if (known !== undefined) {
      for (const a of aliases) if (!this.docNumbersByKey.has(a)) this.docNumbersByKey.set(a, known);
      return known;
    }
    for (const a of aliases) {
      const n = this.docNumbersByKey.get(a);
      if (n !== undefined) {
        this.docNumbersByKey.set(key, n);
        return n;
      }
    }
    this.docCounter += 1;
    this.docNumbersByKey.set(key, this.docCounter);
    return this.docCounter;
  }

  peekDocNumber(key: string): number | undefined {
    return this.docNumbersByKey.get(key);
  }

  // ------------------------------------------------------------------ gates and notices

  ack(key: string, ttlMs = 10 * 60_000): void {
    this.acks.set(key, Date.now() + ttlMs);
  }

  isAcked(key: string): boolean {
    const until = this.acks.get(key);
    if (until === undefined) return false;
    if (Date.now() > until) {
      this.acks.delete(key);
      return false;
    }
    return true;
  }

  /** True the first time a notice key is seen in this session. */
  once(noticeKey: string): boolean {
    if (this.shownNotices.has(noticeKey)) return false;
    this.shownNotices.add(noticeKey);
    return true;
  }

  takePendingNotices(): string[] {
    return this.pendingNotices.splice(0);
  }

  // ------------------------------------------------------------------ marks (m#)

  mark(docKey: string, rid: number, generation: number): string {
    this.markCounter += 1;
    const id = `m${this.markCounter}`;
    this.marks.set(id, { docKey, rid, generation });
    return id;
  }

  // ------------------------------------------------------------------ barriers

  /** set_target is a per-session barrier: calls arriving while it runs wait for it (§5.9). */
  async withBarrier<T>(fn: () => Promise<T>): Promise<T> {
    const previous = this.barrier;
    let release!: () => void;
    this.barrier = new Promise<void>((resolve) => {
      release = resolve;
    });
    try {
      await previous;
      return await fn();
    } finally {
      release();
    }
  }

  /** Wait for an in-flight set_target before resolving targets. */
  async waitBarrier(): Promise<void> {
    await this.barrier;
  }

  /** Outstanding write tool calls by doc (read-after-write barrier, §8.8 rule 1). */
  outstandingWrites(docKey?: string | null): WriteEntry[] {
    return this.writes.values().map(([, w]) => w).filter((w) => w.outstanding && (docKey === undefined || w.docKey === docKey));
  }

  /** Session short id for an executing add-in item when it belongs to this session. */
  refOf(item: ExecutingItem): string | null {
    if (item.clientKey && item.clientKey !== this.clientKey) return null;
    if (item.jobId) {
      for (const [id, job] of this.jobs.values()) if (job.jobId === item.jobId) return id;
    }
    if (item.writeTag && this.writes.has(item.writeTag)) return item.writeTag;
    for (const [id, w] of this.writes.values()) if (w.requestId === item.requestId) return id;
    return null;
  }

  isOwnOutstandingWrite(item: ExecutingItem): boolean {
    if (item.clientKey && item.clientKey !== this.clientKey) return false;
    for (const [, w] of this.writes.values()) if (w.outstanding && (w.requestId === item.requestId || (item.writeTag && this.writes.get(item.writeTag) === w))) return true;
    return false;
  }
}
