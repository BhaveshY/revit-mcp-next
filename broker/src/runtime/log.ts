// Broker log (D2 §14.3): JSONL at <home>\logs\broker-YYYYMMDD.<n>.jsonl, size rotation, retention, buffered async
// writer flushed every 200 ms and on shutdown. Never writes to stdout. Tokens are never logged (only fingerprints).
// Basic implementation; P-REL-BROKER owns hardening.

import { appendFile, mkdir, readdir, stat, unlink } from "node:fs/promises";
import { join } from "node:path";
import { brokerLogFile, logDateStamp, logsDir, type LogLevel } from "@revit-mcp-next/contracts/home";

export type LogFields = Record<string, unknown>;

const LEVELS: Record<LogLevel, number> = { debug: 10, info: 20, warn: 30, error: 40 };
const REDACT_KEYS = new Set(["token", "authToken", "auth"]);

export interface LogOptions {
  level?: LogLevel;
  maxFileMb?: number;
  retainDays?: number;
}

export class BrokerLog {
  private queue: string[] = [];
  private timer: NodeJS.Timeout | null = null;
  private writing: Promise<void> = Promise.resolve();
  private file: string | null = null;
  private fileDate = "";
  private index = 1;
  private size = 0;
  private dirReady = false;
  private disabled = false;
  private closed = false;
  level: LogLevel;
  private maxBytes: number;
  private retainDays: number;

  constructor(readonly home: string, options: LogOptions = {}) {
    this.level = options.level ?? "info";
    this.maxBytes = Math.max(1, options.maxFileMb ?? 10) * 1024 * 1024;
    this.retainDays = options.retainDays ?? 14;
  }

  configure(options: LogOptions): void {
    if (options.level) this.level = options.level;
    if (options.maxFileMb) this.maxBytes = Math.max(1, options.maxFileMb) * 1024 * 1024;
    if (options.retainDays) this.retainDays = options.retainDays;
  }

  debug(evt: string, fields: LogFields = {}): void {
    this.write("debug", evt, fields);
  }
  info(evt: string, fields: LogFields = {}): void {
    this.write("info", evt, fields);
  }
  warn(evt: string, fields: LogFields = {}): void {
    this.write("warn", evt, fields);
  }
  error(evt: string, fields: LogFields = {}): void {
    this.write("error", evt, fields);
  }

  write(level: LogLevel, evt: string, fields: LogFields): void {
    if (this.closed || this.disabled || LEVELS[level] < LEVELS[this.level]) return;
    let line: string;
    try {
      line = JSON.stringify({ ts: new Date().toISOString(), lvl: level, evt, pid: process.pid, ...redact(fields) });
    } catch {
      line = JSON.stringify({ ts: new Date().toISOString(), lvl: level, evt, pid: process.pid, note: "unserializable fields" });
    }
    this.queue.push(line);
    if (this.queue.length > 5_000) this.queue.splice(0, this.queue.length - 5_000);
    if (!this.timer) {
      this.timer = setTimeout(() => {
        this.timer = null;
        void this.flush();
      }, 200);
      this.timer.unref();
    }
  }

  /** Write everything queued so far. */
  flush(): Promise<void> {
    if (this.queue.length === 0) return this.writing;
    const lines = this.queue.splice(0);
    this.writing = this.writing.then(() => this.append(lines)).catch(() => undefined);
    return this.writing;
  }

  async close(): Promise<void> {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    await this.flush();
    this.closed = true;
  }

  private async append(lines: string[]): Promise<void> {
    if (lines.length === 0 || this.disabled) return;
    try {
      if (!this.dirReady) {
        await mkdir(logsDir(this.home), { recursive: true });
        this.dirReady = true;
        void this.prune();
      }
      const text = lines.join("\n") + "\n";
      const target = await this.target(Buffer.byteLength(text));
      await appendFile(target, text, "utf8");
      this.size += Buffer.byteLength(text);
    } catch (error) {
      // A broken home must never break the MCP session: log once to stderr and stop file logging.
      this.disabled = true;
      process.stderr.write(`revit-mcp-next: broker log disabled (${(error as Error).message})\n`);
    }
  }

  private async target(incoming: number): Promise<string> {
    const date = logDateStamp();
    if (date !== this.fileDate || !this.file) {
      this.fileDate = date;
      this.index = 1;
      this.file = brokerLogFile(this.home, date, this.index);
      this.size = await sizeOf(this.file);
    }
    while (this.size + incoming > this.maxBytes && this.size > 0) {
      this.index += 1;
      this.file = brokerLogFile(this.home, date, this.index);
      this.size = await sizeOf(this.file);
    }
    return this.file;
  }

  private async prune(): Promise<void> {
    try {
      const dir = logsDir(this.home);
      const cutoff = Date.now() - this.retainDays * 86_400_000;
      for (const name of await readdir(dir)) {
        if (!/^broker-\d{8}\.\d+\.jsonl$/.test(name)) continue;
        const path = join(dir, name);
        const info = await stat(path).catch(() => null);
        if (info && info.mtimeMs < cutoff) await unlink(path).catch(() => undefined);
      }
    } catch {
      // best effort
    }
  }
}

async function sizeOf(path: string): Promise<number> {
  try {
    return (await stat(path)).size;
  } catch {
    return 0;
  }
}

function redact(fields: LogFields): LogFields {
  const out: LogFields = {};
  for (const [key, value] of Object.entries(fields)) {
    if (REDACT_KEYS.has(key)) continue;
    if (value instanceof Error) out[key] = { message: value.message, stack: value.stack?.split("\n").slice(0, 6).join("\n") };
    else out[key] = value;
  }
  return out;
}

/** A logger that discards everything (used before the home is known). */
export const NULL_LOG = new BrokerLog("", { level: "error" });
(NULL_LOG as unknown as { disabled: boolean }).disabled = true;
