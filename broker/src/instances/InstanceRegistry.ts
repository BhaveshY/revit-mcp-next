// Instance registry (SPEC §8.7, D2 §11.1): <home>\instances\r<year>-<pid>-<6hex>.json (+ .lock).
// - Liveness = the lock file is held: fs.open(lock, "r+") fails with EBUSY/EPERM/EACCES while the add-in holds it with
//   FileShare.None. A free or missing lock means dead, except a JSON younger than 10 s whose process is alive (one more
//   cache cycle while the add-in starts). process.kill(pid, 0) is only a secondary check: pids get reused.
// - The scan unions .json and .lock names, so a live instance whose JSON is being replaced (or is unreadable, NUL-filled,
//   truncated, >64 KB) never drops out: it uses the last-good metadata (≤5 min) or a description derived from the file
//   name (pipe names derive from the instanceId) and is marked source "derived".
// - 500 ms cache with single flight. A transient readdir failure keeps the previous list instead of reporting every
//   instance as gone (which would retire live channels).
// - Dead files (.json, .lock, *.tmp) older than 60 s are deleted. There is no legacy/default instance.

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
  /** Most recent of ui.lastForegroundAtUtc / ui.lastViewActivatedAtUtc / doc activations (ms), for auto-resolve recency. */
  lastActiveAt: number;
  /** Registration file age when listed (-1 when there is no readable file). */
  fileAgeMs: number;
  /** Why the registration file could not be used, when source is last-good or derived. */
  fileProblem?: string;
}

export interface ExitedInstance {
  instanceId: string;
  year: number;
  pid: number;
  docs: string[];
  at: number;
}

export type RegistryEvent = { type: "up" | "down"; info: InstanceInfo };

const CACHE_MS = 500;
const YOUNG_FILE_MS = 10_000;
const LAST_GOOD_MAX_MS = 5 * 60_000;
const DEAD_FILE_MIN_AGE_MS = 60_000;
const MAX_EXITED = 10;
const ID_FILE = /^(r\d{4}-\d+-[0-9a-f]{6})\.(json|lock)$/;
const TMP_FILE = /\.tmp$/i;

interface FileFacts {
  json: boolean;
  lock: boolean;
}

export class InstanceRegistry {
  readonly dir: string;
  private cache: { at: number; list: InstanceInfo[] } | null = null;
  private inflight: Promise<InstanceInfo[]> | null = null;
  private readonly lastGood = new Map<string, { snapshot: DocSnapshot; at: number }>();
  private readonly fresh = new Map<string, { snapshot: DocSnapshot; at: number }>();
  private readonly known = new Map<string, InstanceInfo>();
  private exited: ExitedInstance[] = [];
  private readonly listeners = new Set<(event: RegistryEvent) => void>();
  private readonly unreadableLogged = new Set<string>();
  /** Counters for status detail:full. */
  readonly stats = { scans: 0, readdirFailures: 0, derived: 0, lastGood: 0, cleaned: 0 };

  constructor(readonly home: string, private readonly log: BrokerLog) {
    this.dir = instancesDir(home);
  }

  onChange(listener: (event: RegistryEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Live instances (cached 500 ms, single flight). */
  async list(options: { maxAgeMs?: number } = {}): Promise<InstanceInfo[]> {
    const maxAge = options.maxAgeMs ?? CACHE_MS;
    if (this.cache && Date.now() - this.cache.at <= maxAge) return this.cache.list;
    if (!this.inflight) {
      this.inflight = this.scan()
        .catch((error) => {
          // Never let a registry problem become a tool error: keep the previous view.
          this.log.warn("registry", { change: "scan_failed", error });
          return this.cache?.list ?? [];
        })
        .finally(() => {
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

  /** Record a fresh control snapshot (used before writes and by status). */
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
      pipe: snapshot.pipe || info.pipe,
      controlPipe: snapshot.controlPipe || info.controlPipe,
    });
    if (this.cache) this.cache = { at: this.cache.at, list: this.cache.list.map((i) => (i.instanceId === instanceId ? apply(i) : i)) };
    const known = this.known.get(instanceId);
    if (known) this.known.set(instanceId, apply(known));
  }

  /** The most recent instance that exited (for NO_REVIT_RUNNING / REVIT_EXITED details). */
  lastExited(): ExitedInstance | null {
    return this.exited[0] ?? null;
  }

  /** The exit record of one instance, if it exited while this broker watched. */
  exitOf(instanceId: string): ExitedInstance | null {
    return this.exited.find((e) => e.instanceId === instanceId) ?? null;
  }

  /** The most recent exit of a Revit year. */
  lastExitedOfYear(year: number): ExitedInstance | null {
    return this.exited.find((e) => e.year === year) ?? null;
  }

  exitedSince(ms: number): ExitedInstance[] {
    const cutoff = Date.now() - ms;
    return this.exited.filter((e) => e.at >= cutoff);
  }

  /** True when the instance's lock is held (the process is alive). */
  async isLockHeld(instanceId: string): Promise<boolean> {
    return (await lockState(join(this.dir, `${instanceId}.lock`))) === "held";
  }

  // ------------------------------------------------------------------------------------------------ scan

  private async scan(): Promise<InstanceInfo[]> {
    this.stats.scans += 1;
    let names: string[];
    try {
      names = await readdir(this.dir);
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code === "ENOENT") names = [];
      else {
        // Transient (AV, sharing violation): keep the previous view rather than declaring every instance dead.
        this.stats.readdirFailures += 1;
        this.log.warn("registry", { change: "readdir_failed", code });
        const kept = this.cache?.list ?? [...this.known.values()];
        this.cache = { at: Date.now(), list: kept };
        return kept;
      }
    }
    const now = Date.now();
    const ids = new Map<string, FileFacts>();
    const tmps: string[] = [];
    for (const name of names) {
      const m = ID_FILE.exec(name);
      if (m) {
        const facts = ids.get(m[1]!) ?? { json: false, lock: false };
        if (m[2] === "json") facts.json = true;
        else facts.lock = true;
        ids.set(m[1]!, facts);
      } else if (TMP_FILE.test(name)) tmps.push(name);
    }

    const live: InstanceInfo[] = [];
    await Promise.all(
      [...ids.entries()].map(async ([instanceId, facts]) => {
        const parsedId = parseInstanceId(instanceId);
        if (!parsedId) return;
        const jsonPath = join(this.dir, `${instanceId}.json`);
        const lockPath = join(this.dir, `${instanceId}.lock`);
        const lock = facts.lock ? await lockState(lockPath) : "missing";
        let mtimeMs: number | null = null;
        if (facts.json) {
          try {
            mtimeMs = (await stat(jsonPath)).mtimeMs;
          } catch {
            mtimeMs = null; // replaced or removed between readdir and stat
          }
        }
        const age = mtimeMs === null ? Number.POSITIVE_INFINITY : now - mtimeMs;
        let alive = lock === "held";
        if (!alive && lock === "missing" && mtimeMs !== null && age < YOUNG_FILE_MS) {
          // The add-in takes the lock before its first JSON write; a young JSON without a lock gets one more cycle,
          // but only while its process exists (secondary pid check).
          alive = processAlive(parsedId.pid);
        }
        if (!alive) {
          await this.removeDead(instanceId, facts, jsonPath, lockPath, mtimeMs, now);
          return;
        }
        live.push(await this.describe(instanceId, parsedId, jsonPath, mtimeMs, now));
      })
    );
    for (const name of tmps) await this.removeIfOld(join(this.dir, name), now);

    const seen = new Set(live.map((i) => i.instanceId));
    // Instances that disappeared since the last scan.
    for (const [id, info] of this.known) {
      if (seen.has(id)) continue;
      this.known.delete(id);
      this.fresh.delete(id);
      this.lastGood.delete(id);
      this.unreadableLogged.delete(id);
      this.exited = [{ instanceId: id, year: info.year, pid: info.pid, docs: (info.snapshot?.docs ?? []).map((d) => d.title), at: now }, ...this.exited.filter((e) => e.instanceId !== id)].slice(0, MAX_EXITED);
      this.log.info("registry", { change: "down", instance: id, docs: info.snapshot?.docs?.length ?? 0 });
      this.emit({ type: "down", info });
    }
    for (const info of live) {
      if (!this.known.has(info.instanceId)) {
        this.log.info("registry", { change: "up", instance: info.instanceId, source: info.source });
        this.emit({ type: "up", info });
      }
      this.known.set(info.instanceId, info);
    }
    live.sort((a, b) => a.year - b.year || a.pid - b.pid);
    this.cache = { at: Date.now(), list: live };
    return live;
  }

  private emit(event: RegistryEvent): void {
    for (const l of this.listeners) {
      try {
        l(event);
      } catch (error) {
        this.log.warn("registry", { change: "listener_failed", error });
      }
    }
  }

  private async describe(instanceId: string, id: { year: number; pid: number }, jsonPath: string, mtimeMs: number | null, now: number): Promise<InstanceInfo> {
    const base = {
      instanceId,
      pid: id.pid,
      year: id.year,
      pipe: primaryPipeName(instanceId),
      controlPipe: controlPipeName(instanceId),
      fileAgeMs: mtimeMs === null ? -1 : now - mtimeMs,
    };
    const fromSnapshot = (snapshot: DocSnapshot, source: SnapshotSource, at: number, fileProblem?: string): InstanceInfo => ({
      ...base,
      pipe: snapshot.pipe || base.pipe,
      controlPipe: snapshot.controlPipe || base.controlPipe,
      state: snapshot.state ?? "unknown",
      snapshot,
      source,
      snapshotAt: at,
      compat: protocolCompat(snapshot.protocol),
      lastActiveAt: lastActiveOf(snapshot),
      ...(fileProblem ? { fileProblem } : {}),
    });
    let fileSnapshot: DocSnapshot | null = null;
    let problem: string | undefined;
    if (mtimeMs !== null) {
      const read = await readRegistration(jsonPath, instanceId);
      fileSnapshot = read.snapshot;
      problem = read.problem;
    } else problem = "no registration file";
    // A control snapshot newer than the file wins (seq breaks ties).
    const fresh = this.fresh.get(instanceId);
    if (fresh && now - fresh.at < LAST_GOOD_MAX_MS && (!fileSnapshot || (fresh.at > (mtimeMs ?? 0) && (fresh.snapshot.seq ?? 0) >= (fileSnapshot.seq ?? 0)))) {
      return fromSnapshot(fresh.snapshot, "control", fresh.at);
    }
    if (fileSnapshot) {
      this.lastGood.set(instanceId, { snapshot: fileSnapshot, at: mtimeMs ?? now });
      this.unreadableLogged.delete(instanceId);
      return fromSnapshot(fileSnapshot, "file", mtimeMs ?? now);
    }
    if (!this.unreadableLogged.has(instanceId)) {
      this.unreadableLogged.add(instanceId);
      this.log.warn("registry", { change: "unreadable", instance: instanceId, problem });
    }
    const good = this.lastGood.get(instanceId);
    if (good && now - good.at <= LAST_GOOD_MAX_MS) {
      this.stats.lastGood += 1;
      return fromSnapshot(good.snapshot, "last-good", good.at, problem);
    }
    this.stats.derived += 1;
    return { ...base, state: "unknown", snapshot: null, source: "derived", snapshotAt: 0, compat: "unknown", lastActiveAt: 0, fileProblem: problem };
  }

  private async removeDead(instanceId: string, facts: FileFacts, jsonPath: string, lockPath: string, mtimeMs: number | null, now: number): Promise<void> {
    const lockAge = facts.lock ? await ageOf(lockPath, now) : Number.POSITIVE_INFINITY;
    const jsonAge = mtimeMs === null ? Number.POSITIVE_INFINITY : now - mtimeMs;
    if (Math.min(lockAge, jsonAge) < DEAD_FILE_MIN_AGE_MS) return;
    let removed = false;
    if (facts.json) removed = (await unlink(jsonPath).then(() => true, () => false)) || removed;
    if (facts.lock) removed = (await unlink(lockPath).then(() => true, () => false)) || removed;
    if (removed) {
      this.stats.cleaned += 1;
      this.log.info("registry", { change: "cleaned", instance: instanceId });
    }
  }

  private async removeIfOld(path: string, now: number): Promise<void> {
    try {
      const info = await stat(path);
      if (now - info.mtimeMs > DEAD_FILE_MIN_AGE_MS) await unlink(path);
    } catch {
      // in use or gone
    }
  }
}

/** Read and validate a registration file. Unreadable = missing, empty, >64 KB, all NUL, invalid JSON or no docs[]. */
async function readRegistration(path: string, instanceId: string): Promise<{ snapshot: DocSnapshot | null; problem?: string }> {
  let raw: Buffer;
  try {
    raw = await readFile(path);
  } catch (error) {
    return { snapshot: null, problem: `cannot read (${(error as NodeJS.ErrnoException).code ?? "error"})` };
  }
  if (raw.byteLength === 0) return { snapshot: null, problem: "empty" };
  if (raw.byteLength > MAX_REGISTRATION_BYTES) return { snapshot: null, problem: `larger than ${MAX_REGISTRATION_BYTES} bytes` };
  if (!raw.some((b) => b !== 0)) return { snapshot: null, problem: "all NUL bytes" };
  try {
    const parsed = JSON.parse(raw.toString("utf8").replace(/^﻿/, "").replace(/\u0000+$/, "")) as DocSnapshot;
    if (!parsed || typeof parsed !== "object" || !Array.isArray(parsed.docs)) return { snapshot: null, problem: "no docs[]" };
    if (parsed.instanceId && parsed.instanceId !== instanceId) return { snapshot: null, problem: `instanceId ${parsed.instanceId} does not match the file name` };
    return { snapshot: parsed };
  } catch (error) {
    return { snapshot: null, problem: `invalid JSON (${(error as Error).message.slice(0, 80)})` };
  }
}

type LockState = "held" | "free" | "missing";

/** Probe a lock file: held (sharing violation), free (opened → its holder is gone) or missing. */
export async function lockState(lockPath: string): Promise<LockState> {
  try {
    const handle = await open(lockPath, "r+");
    await handle.close().catch(() => undefined);
    return "free";
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === "ENOENT") return "missing";
    if (code === "EBUSY" || code === "EPERM" || code === "EACCES") return "held";
    // Out of handles or a transient I/O error says nothing about the holder: never declare a live Revit dead.
    if (code === "EMFILE" || code === "ENFILE" || code === "EAGAIN" || code === "EIO") return "held";
    return "free";
  }
}

export function processAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}

async function ageOf(path: string, now: number): Promise<number> {
  try {
    return now - (await stat(path)).mtimeMs;
  } catch {
    return Number.POSITIVE_INFINITY;
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
