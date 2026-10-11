// Broker in-flight write journal (SPEC §8.10, D2 §13.3): <home>\state\inflight\<brokerPid>-<startMs>.json holding
// {requestId, shortId, key, tool, instanceId, docKey, sentAt} for every write that was sent and has no terminal outcome.
// - Written before the write is sent (the caller awaits add()), entry removed when terminal. Writes are serialized,
//   always persist the latest state, go through a unique tmp file and a rename retried on Windows sharing errors
//   (AV, indexers); a failed rename keeps the previous file.
// - At startup, journals of dead brokers are claimed by an atomic rename (several brokers may start at once), each
//   entry is reconciled through the live add-in (control get_request_result) or the ledger files, and the outcome is
//   shown once as notice RECOVERED_WRITE on the first tool result after recovery finished (status lists it too).

import { mkdir, readdir, readFile, rename, stat, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { inflightDir, inflightFile } from "@revit-mcp-next/contracts/home";
import { isTerminalLedgerState, type LedgerEntry } from "@revit-mcp-next/contracts/protocol";
import type { BrokerLog } from "../runtime/log.js";
import { searchLedgers } from "./Ledger.js";

export interface InflightEntry {
  requestId: string;
  /** Session short id (w#). */
  shortId: string;
  key: string;
  tool: string;
  instanceId: string;
  docKey: string | null;
  sentAt: string;
}

export interface RecoveredWrite {
  entry: InflightEntry;
  outcome: LedgerEntry | null;
  /** committed | rolledBack | failed | cancelled | running | unknown */
  state: string;
  /** Human text for the notice. */
  text: string;
}

/** Terminal outcome from the live add-in, or null (instance gone, not found, still running). */
export type LedgerLookup = (instanceId: string, requestId: string) => Promise<LedgerEntry | null>;

const RENAME_RETRIES_MS = [50, 100, 200];
const STALE_TMP_MS = 60_000;
/** A journal of a "live" pid untouched for this long belongs to a reused pid. */
const LIVE_PID_MAX_AGE_MS = 24 * 60 * 60_000;
const FILE_NAME = /^(\d+)-(\d+)\.json$/;
const CLAIMED = /^(\d+)-(\d+)\.json\.recovering-(\d+)$/;

export class InflightJournal {
  readonly path: string;
  private readonly entries = new Map<string, InflightEntry>();
  private writing: Promise<void> = Promise.resolve();
  private dirty = false;
  private recovered: RecoveredWrite[] = [];
  private recoveryDone = false;
  private shown = false;
  /** Counters for status detail:full. */
  readonly stats = { persisted: 0, persistFailures: 0, recoveredFiles: 0 };

  constructor(readonly home: string, private readonly log: BrokerLog, readonly brokerPid = process.pid, readonly startMs = Date.now()) {
    this.path = inflightFile(home, brokerPid, startMs);
  }

  /** Record a write before it is sent. Resolves when the journal is on disk (or the write failed and was logged). */
  add(entry: InflightEntry): Promise<void> {
    this.entries.set(entry.requestId, entry);
    return this.persist();
  }

  /** The write reached a terminal state (or was never sent). */
  remove(requestId: string): Promise<void> {
    if (!this.entries.delete(requestId)) return this.writing;
    return this.persist();
  }

  pending(): InflightEntry[] {
    return [...this.entries.values()];
  }

  /** Recovered writes not shown yet: returned once, and only after recovery finished. */
  takeRecovered(): RecoveredWrite[] {
    if (!this.recoveryDone || this.shown) return [];
    this.shown = true;
    return this.recovered;
  }

  peekRecovered(): RecoveredWrite[] {
    return this.recovered;
  }

  get recoveryFinished(): boolean {
    return this.recoveryDone;
  }

  /** Claim and reconcile the journals of dead brokers (async, never throws). */
  async recover(lookup: LedgerLookup): Promise<RecoveredWrite[]> {
    const results: RecoveredWrite[] = [];
    try {
      const dir = inflightDir(this.home);
      let names: string[];
      try {
        names = await readdir(dir);
      } catch {
        names = [];
      }
      const now = Date.now();
      for (const name of names) {
        const file = join(dir, name);
        if (name.endsWith(".tmp")) {
          await removeIfOlder(file, now, STALE_TMP_MS);
          continue;
        }
        let claimed: string | null = null;
        const plain = FILE_NAME.exec(name);
        const orphanClaim = CLAIMED.exec(name);
        if (plain) {
          const pid = Number(plain[1]);
          if (pid === this.brokerPid && Number(plain[2]) === this.startMs) continue;
          if (pid !== this.brokerPid && processAlive(pid) && (await ageOf(file, now)) < LIVE_PID_MAX_AGE_MS) continue;
          claimed = `${file}.recovering-${this.brokerPid}`;
          try {
            await rename(file, claimed);
          } catch {
            continue; // another broker claimed it first, or it is gone
          }
        } else if (orphanClaim) {
          // A broker died while recovering: take it over once its pid is gone.
          const claimer = Number(orphanClaim[3]);
          if (claimer === this.brokerPid || processAlive(claimer)) continue;
          claimed = `${join(dir, `${orphanClaim[1]}-${orphanClaim[2]}.json`)}.recovering-${this.brokerPid}`;
          try {
            await rename(file, claimed);
          } catch {
            continue;
          }
        } else continue;

        let entries: InflightEntry[] = [];
        try {
          const parsed = JSON.parse(stripBom(await readFile(claimed, "utf8"))) as { entries?: InflightEntry[] };
          entries = Array.isArray(parsed.entries) ? parsed.entries.filter((e) => e && typeof e.requestId === "string") : [];
        } catch (error) {
          this.log.warn("inflight_journal", { change: "unreadable", file: name, error });
        }
        for (const entry of entries) results.push(await this.reconcileOne(entry, lookup));
        await unlink(claimed).catch(() => undefined);
        this.stats.recoveredFiles += 1;
      }
    } catch (error) {
      this.log.warn("inflight_journal", { change: "recover_failed", error });
    } finally {
      this.recovered = results;
      this.recoveryDone = true;
    }
    if (results.length > 0) this.log.info("recovered_writes", { count: results.length, states: results.map((r) => `${r.entry.shortId}:${r.state}`) });
    return results;
  }

  private async reconcileOne(entry: InflightEntry, lookup: LedgerLookup): Promise<RecoveredWrite> {
    let outcome: LedgerEntry | null = null;
    try {
      outcome = await lookup(entry.instanceId, entry.requestId);
    } catch {
      outcome = null;
    }
    if (!outcome) {
      try {
        outcome = await searchLedgers(this.home, entry.requestId, entry.instanceId);
      } catch {
        outcome = null;
      }
    }
    const what = `a previous session's write ${entry.shortId} (${entry.key}, sent ${entry.sentAt})`;
    if (!outcome) return { entry, outcome: null, state: "unknown", text: `${what} has no recorded outcome; check the model before repeating it` };
    if (!isTerminalLedgerState(outcome.state))
      return { entry, outcome, state: "running", text: `${what} was ${outcome.state} when last recorded; check status {"include":["writes"]} before repeating it` };
    const counts = outcome.counts ? ` (created ${outcome.counts.created}, modified ${outcome.counts.modified}, deleted ${outcome.counts.deleted})` : "";
    if (outcome.state === "committed")
      return {
        entry,
        outcome,
        state: "committed",
        text: `${what} committed${outcome.completedAtUtc ? ` at ${outcome.completedAtUtc}` : ""}${counts}${outcome.saved ? "" : "; not saved unless the doc was saved since"}; do not repeat it`,
      };
    return { entry, outcome, state: String(outcome.state), text: `${what} ended ${outcome.state}; nothing of it is in the model` };
  }

  private persist(): Promise<void> {
    this.dirty = true;
    this.writing = this.writing.then(() => this.flush());
    return this.writing;
  }

  /** Write the latest state (serialized; a queued flush with nothing new returns at once). */
  private async flush(): Promise<void> {
    if (!this.dirty) return;
    this.dirty = false;
    const entries = [...this.entries.values()];
    try {
      if (entries.length === 0) {
        await unlink(this.path).catch(() => undefined);
        return;
      }
      await mkdir(inflightDir(this.home), { recursive: true });
      const tmp = `${this.path}.${process.pid}.${Date.now().toString(36)}.tmp`;
      await writeFile(tmp, JSON.stringify({ brokerPid: this.brokerPid, startMs: this.startMs, entries }), "utf8");
      let lastError: unknown = null;
      for (let attempt = 0; attempt <= RENAME_RETRIES_MS.length; attempt++) {
        try {
          await rename(tmp, this.path);
          this.stats.persisted += 1;
          return;
        } catch (error) {
          lastError = error;
          const code = (error as NodeJS.ErrnoException).code;
          if (attempt === RENAME_RETRIES_MS.length || !(code === "EPERM" || code === "EBUSY" || code === "EACCES")) break;
          await new Promise((r) => setTimeout(r, RENAME_RETRIES_MS[attempt]).unref());
        }
      }
      await unlink(tmp).catch(() => undefined);
      throw lastError;
    } catch (error) {
      // The previous journal stays; the write itself is still reconciled in-process.
      this.stats.persistFailures += 1;
      this.log.warn("inflight_journal", { change: "persist_failed", error });
    }
  }
}

function stripBom(text: string): string {
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

async function ageOf(path: string, now: number): Promise<number> {
  try {
    return now - (await stat(path)).mtimeMs;
  } catch {
    return Number.POSITIVE_INFINITY;
  }
}

async function removeIfOlder(path: string, now: number, ms: number): Promise<void> {
  if ((await ageOf(path, now)) > ms) await unlink(path).catch(() => undefined);
}

function processAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}
