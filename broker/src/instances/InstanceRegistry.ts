// Instance registry (SPEC §8.7, D2 §11.1): <home>\instances\r<year>-<pid>-<6hex>.json (+ .lock).
// - Liveness = the lock file is held (fs.open r+ fails with EBUSY/EPERM). A JSON without a lock is dead, except one
//   younger than 10 s (given one more cache cycle while the add-in starts).
// - 500 ms cache with single flight; unreadable files of live instances use the last-good metadata (≤5 min) or a
//   description derived from the file name (the pipe names are derived from the instanceId).
// - Dead files (.json/.lock/.tmp older than 60 s) are cleaned up. There is no legacy/default instance.
// Basic implementation; P-REL-BROKER owns hardening.

import { open, readdir, readFile, stat, unlink } from "node:fs/promises";
import { join } from "node:path";
import { instancesDir } from "@revit-mcp-next/contracts/home";
import {
  controlPipeName,
  MAX_REGISTRATION_BYTES,
  parseInstanceId,
  primaryPipeName,
  protocolCompat,
  type DocSnapshot,
  type InstanceState,
} from "@revit-mcp-next/contracts/protocol";
import type { BrokerLog } from "../runtime/log.js";

export type SnapshotSource = "file" | "control" | "last-good" | "derived";
export type Compat = "ok" | "ADDIN_OUTDATED" | "ADDIN_NEWER_THAN_BROKER" | "unknown";

export interface InstanceInfo {
  instanceId: string;
  pid: number;
  year: number;
  state: InstanceState | "unknown";
  pipe: string;
  controlPipe: string;
  snapshot: DocSnapshot | null;
  source: SnapshotSource;
  /** When the snapshot was obtained (file mtime or control round trip). */
  snapshotAt: number;
  compat: Compat;
  /** Most recent of ui.lastForegroundAtUtc / ui.lastViewActivatedAtUtc (ms), for auto-resolve recency. */
  lastActiveAt: number;
  /** Registration file age when listed. */
  fileAgeMs: number;
}

export interface ExitedInstance {
  instanceId: string;
  year: number;
  pid: number;
  docs: string[];
  at: number;
}

const CACHE_MS = 500;
const YOUNG_FILE_MS = 10_000;
const LAST_GOOD_MAX_MS = 5 * 60_000;
const DEAD_FILE_MIN_AGE_MS = 60_000;
const FILE_PATTERN = /^(r\d{4}-\d+-[0-9a-f]{6})\.json$/;

export class InstanceRegistry {
  readonly dir: string;
  private cache: { at: number; list: InstanceInfo[] } | null = null;
  private inflight: Promise<InstanceInfo[]> | null = null;
  private readonly lastGood = new Map<string, { snapshot: DocSnapshot; at: number }>();
  private readonly fresh = new Map<string, { snapshot: DocSnapshot; at: number }>();
  private readonly known = new Map<string, InstanceInfo>();
  private exited: ExitedInstance[] = [];
  private listeners = new Set<(event: { type: "up" | "down"; info: InstanceInfo }) => void>();

  constructor(readonly home: string, private readonly log: BrokerLog) {
    this.dir = instancesDir(home);
  }

  onChange(listener: (event: { type: "up" | "down"; info: InstanceInfo }) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Live instances (cached 500 ms, single flight). */
  async list(options: { maxAgeMs?: number } = {}): Promise<InstanceInfo[]> {
    const maxAge = options.maxAgeMs ?? CACHE_MS;
    if (this.cache && Date.now() - this.cache.at <= maxAge) return this.cache.list;
    if (!this.inflight) {
      this.inflight = this.scan().finally(() => {
        this.inflight = null;
      });
    }
    return this.inflight;
  }

  async find(instanceId: string): Promise<InstanceInfo | undefined> {
    return (await this.list()).find((i) => i.instanceId === instanceId);
  }

  invalidate(): void {
    this.cache = null;
  }

  /** Record a fresh control snapshot (used before writes). */
  updateSnapshot(instanceId: string, snapshot: DocSnapshot): void {
    const now = Date.now();
    this.fresh.set(instanceId, { snapshot, at: now });
    this.lastGood.set(instanceId, { snapshot, at: now });
    const apply = (info: InstanceInfo): InstanceInfo => ({
      ...info,
      snapshot,
      source: "control",
      snapshotAt: now,
      state: snapshot.state ?? info.state,
      compat: protocolCompat(snapshot.protocol),
      lastActiveAt: lastActiveOf(snapshot),
    });
    if (this.cache) this.cache = { at: this.cache.at, list: this.cache.list.map((i) => (i.instanceId === instanceId ? apply(i) : i)) };
    const known = this.known.get(instanceId);
    if (known) this.known.set(instanceId, apply(known));
  }

  /** The most recent instance that exited (for NO_REVIT_RUNNING / REVIT_EXITED details). */
  lastExited(): ExitedInstance | null {
    return this.exited[0] ?? null;
  }

  exitedSince(ms: number): ExitedInstance[] {
    const cutoff = Date.now() - ms;
    return this.exited.filter((e) => e.at >= cutoff);
  }

  /** True when the instance's lock is held (the process is alive). */
  async isLockHeld(instanceId: string): Promise<boolean> {
    return (await lockState(join(this.dir, `${instanceId}.lock`))) === "held";
  }

  private async scan(): Promise<InstanceInfo[]> {
    let names: string[];
    try {
      names = await readdir(this.dir);
    } catch {
      names = [];
    }
    const now = Date.now();
    const seen = new Set<string>();
    const live: InstanceInfo[] = [];
    await Promise.all(
      names.map(async (name) => {
        const match = FILE_PATTERN.exec(name);
        if (!match) {
          if (name.endsWith(".tmp")) await this.removeIfOld(join(this.dir, name), now);
          return;
        }
        const instanceId = match[1]!;
        const parsedId = parseInstanceId(instanceId);
        if (!parsedId) return;
        const jsonPath = join(this.dir, name);
        const lockPath = join(this.dir, `${instanceId}.lock`);
        let mtimeMs = now;
        try {
          mtimeMs = (await stat(jsonPath)).mtimeMs;
        } catch {
          return;
        }
        const lock = await lockState(lockPath);
        const age = now - mtimeMs;
        if (lock !== "held" && !(lock === "missing" && age < YOUNG_FILE_MS)) {
          await this.removeDead(instanceId, jsonPath, lockPath, age);
          return;
        }
        seen.add(instanceId);
        const info = await this.describe(instanceId, parsedId, jsonPath, mtimeMs, now);
        live.push(info);
      })
    );
    // Instances that disappeared since the last scan.
    for (const [id, info] of this.known) {
      if (!seen.has(id)) {
        this.known.delete(id);
        this.fresh.delete(id);
        this.exited.unshift({ instanceId: id, year: info.year, pid: info.pid, docs: (info.snapshot?.docs ?? []).map((d) => d.title), at: now });
        this.exited = this.exited.slice(0, 10);
        this.log.info("registry", { change: "down", instance: id });
        for (const l of this.listeners) l({ type: "down", info });
      }
    }
    for (const info of live) {
      if (!this.known.has(info.instanceId)) {
        this.log.info("registry", { change: "up", instance: info.instanceId, source: info.source });
        for (const l of this.listeners) l({ type: "up", info });
      }
      this.known.set(info.instanceId, info);
    }
    live.sort((a, b) => a.year - b.year || a.pid - b.pid);
    this.cache = { at: Date.now(), list: live };
    return live;
  }

  private async describe(instanceId: string, id: { year: number; pid: number }, jsonPath: string, mtimeMs: number, now: number): Promise<InstanceInfo> {
    const base = {
      instanceId,
      pid: id.pid,
      year: id.year,
      pipe: primaryPipeName(instanceId),
      controlPipe: controlPipeName(instanceId),
      fileAgeMs: now - mtimeMs,
    };
    const fromSnapshot = (snapshot: DocSnapshot, source: SnapshotSource, at: number): InstanceInfo => ({
      ...base,
      pipe: snapshot.pipe || base.pipe,
      controlPipe: snapshot.controlPipe || base.controlPipe,
      state: snapshot.state ?? "unknown",
      snapshot,
      source,
      snapshotAt: at,
      compat: protocolCompat(snapshot.protocol),
      lastActiveAt: lastActiveOf(snapshot),
    });
    const fresh = this.fresh.get(instanceId);
    let fileSnapshot: DocSnapshot | null = null;
    try {
      const raw = await readFile(jsonPath);
      if (raw.byteLength > 0 && raw.byteLength <= MAX_REGISTRATION_BYTES && raw.some((b) => b !== 0)) {
        const parsed = JSON.parse(raw.toString("utf8").replace(/^﻿/, "")) as DocSnapshot;
        if (parsed && typeof parsed === "object" && Array.isArray(parsed.docs)) fileSnapshot = parsed;
      }
    } catch {
      fileSnapshot = null;
    }
    // A control snapshot newer than the file wins.
    if (fresh && (!fileSnapshot || fresh.at > mtimeMs) && now - fresh.at < LAST_GOOD_MAX_MS) {
      if (!fileSnapshot || (fresh.snapshot.seq ?? 0) >= (fileSnapshot.seq ?? 0)) return fromSnapshot(fresh.snapshot, "control", fresh.at);
    }
    if (fileSnapshot) {
      this.lastGood.set(instanceId, { snapshot: fileSnapshot, at: mtimeMs });
      return fromSnapshot(fileSnapshot, "file", mtimeMs);
    }
    const good = this.lastGood.get(instanceId);
    if (good && now - good.at <= LAST_GOOD_MAX_MS) return fromSnapshot(good.snapshot, "last-good", good.at);
    this.log.warn("registry", { change: "unreadable", instance: instanceId });
    return { ...base, state: "unknown", snapshot: null, source: "derived", snapshotAt: 0, compat: "unknown", lastActiveAt: 0 };
  }

  private async removeDead(instanceId: string, jsonPath: string, lockPath: string, age: number): Promise<void> {
    if (age < DEAD_FILE_MIN_AGE_MS) return;
    await unlink(jsonPath).catch(() => undefined);
    await unlink(lockPath).catch(() => undefined);
    this.log.info("registry", { change: "cleaned", instance: instanceId });
  }

  private async removeIfOld(path: string, now: number): Promise<void> {
    try {
      const info = await stat(path);
      if (now - info.mtimeMs > DEAD_FILE_MIN_AGE_MS) await unlink(path);
    } catch {
      // ignore
    }
  }
}

type LockState = "held" | "free" | "missing";

/** Probe a lock file: held (sharing violation), free (opened → dead holder) or missing. */
export async function lockState(lockPath: string): Promise<LockState> {
  try {
    const handle = await open(lockPath, "r+");
    await handle.close().catch(() => undefined);
    return "free";
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === "ENOENT") return "missing";
    if (code === "EBUSY" || code === "EPERM" || code === "EACCES") return "held";
    return "free";
  }
}

function lastActiveOf(snapshot: DocSnapshot): number {
  const times = [snapshot.ui?.lastForegroundAtUtc, snapshot.ui?.lastViewActivatedAtUtc].map((t) => (t ? Date.parse(t) : 0)).filter((t) => Number.isFinite(t));
  let best = Math.max(0, ...times);
  for (const doc of snapshot.docs ?? []) {
    const t = doc.lastActivatedAtUtc ? Date.parse(doc.lastActivatedAtUtc) : 0;
    if (Number.isFinite(t) && t > best) best = t;
  }
  return best;
}
