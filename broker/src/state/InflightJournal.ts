// Broker in-flight write journal (SPEC §8.10, D2 §13.3): <home>\state\inflight\<brokerPid>-<startMs>.json.
// Written (atomic rename) before a write is sent, entry removed when terminal. At startup, files of dead brokers are
// reconciled through the add-in (get_request_result) or the ledger files and shown once as notice RECOVERED_WRITE.
// Basic implementation; P-REL-BROKER owns hardening.

import { mkdir, readdir, readFile, rename, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { inflightDir, inflightFile } from "@revit-mcp-next/contracts/home";
import type { LedgerEntry } from "@revit-mcp-next/contracts/protocol";
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
  /** Human text for the notice. */
  text: string;
}

export type LedgerLookup = (instanceId: string, requestId: string) => Promise<LedgerEntry | null>;

export class InflightJournal {
  readonly path: string;
  private entries = new Map<string, InflightEntry>();
  private writing: Promise<void> = Promise.resolve();
  private recovered: RecoveredWrite[] = [];
  private shown = false;

  constructor(readonly home: string, private readonly log: BrokerLog, readonly brokerPid = process.pid, readonly startMs = Date.now()) {
    this.path = inflightFile(home, brokerPid, startMs);
  }

  /** Record a write before it is sent. */
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

  /** Recovered writes not yet shown; shown once (first status/tool result). */
  takeRecovered(): RecoveredWrite[] {
    if (this.shown) return [];
    this.shown = true;
    return this.recovered;
  }

  peekRecovered(): RecoveredWrite[] {
    return this.recovered;
  }

  /** Scan journals of dead brokers and reconcile their entries (async, never throws). */
  async recover(lookup: LedgerLookup): Promise<RecoveredWrite[]> {
    const dir = inflightDir(this.home);
    let names: string[];
    try {
      names = await readdir(dir);
    } catch {
      return [];
    }
    const results: RecoveredWrite[] = [];
    for (const name of names) {
      const match = /^(\d+)-(\d+)\.json$/.exec(name);
      if (!match) continue;
      const pid = Number(match[1]);
      if (pid === this.brokerPid || processAlive(pid)) continue;
      const file = join(dir, name);
      let entries: InflightEntry[] = [];
      try {
        const parsed = JSON.parse(await readFile(file, "utf8")) as { entries?: InflightEntry[] };
        entries = Array.isArray(parsed.entries) ? parsed.entries : [];
      } catch {
        entries = [];
      }
      for (const entry of entries) {
        let outcome: LedgerEntry | null = null;
        try {
          outcome = (await lookup(entry.instanceId, entry.requestId)) ?? (await searchLedgers(this.home, entry.requestId, entry.instanceId));
        } catch {
          outcome = null;
        }
        const state = outcome?.state ?? "unknown";
        const text =
          state === "committed"
            ? `a previous session's write ${entry.shortId} (${entry.key}) committed${outcome?.completedAtUtc ? ` at ${outcome.completedAtUtc}` : ""}${outcome?.saved ? "" : "; not saved unless the doc was saved since"}`
            : state === "unknown"
              ? `a previous session's write ${entry.shortId} (${entry.key}) has an unknown outcome; check the model before repeating it`
              : `a previous session's write ${entry.shortId} (${entry.key}) ended ${state}`;
        results.push({ entry, outcome, text });
      }
      await unlink(file).catch(() => undefined);
    }
    this.recovered = results;
    if (results.length > 0) this.log.info("recovered_writes", { count: results.length });
    return results;
  }

  private persist(): Promise<void> {
    const snapshot = { brokerPid: this.brokerPid, startMs: this.startMs, entries: [...this.entries.values()] };
    this.writing = this.writing
      .then(async () => {
        if (snapshot.entries.length === 0) {
          await unlink(this.path).catch(() => undefined);
          return;
        }
        await mkdir(inflightDir(this.home), { recursive: true });
        const tmp = `${this.path}.tmp`;
        await writeFile(tmp, JSON.stringify(snapshot), "utf8");
        await rename(tmp, this.path);
      })
      .catch((error) => this.log.warn("inflight_journal", { error }));
    return this.writing;
  }
}

function processAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}
