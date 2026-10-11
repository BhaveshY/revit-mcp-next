// Broker log (D2 §14.3): JSONL at <home>\logs\broker-YYYYMMDD.<n>.jsonl, rotation at log.maxFileMb (10 MB), retention
// log.retainDays (14 days, pruned at start and at each day change), buffered async writer flushed every 200 ms and on
// shutdown. Never writes to stdout. Tokens are never logged (keys token/authToken/auth are dropped at any depth; only
// fingerprints appear).
// One line per tool call: the framework writes evt "call" with reqId; the transport adds its metrics beforehand with
// annotate(reqId, {...}) (limiterMs, queueWaitMs, raiseToExecMs, execMs, via, retried, coalesced, partial...), which are
// merged into that call line. Annotations nobody claims within 60 s are written as evt "exchange".

import { appendFile, mkdir, readdir, stat, unlink } from "node:fs/promises";
import { join } from "node:path";
import { brokerLogFile, logDateStamp, logsDir, type LogLevel } from "@revit-mcp-next/contracts/home";

export type LogFields = Record<string, unknown>;

const LEVELS: Record<LogLevel, number> = { debug: 10, info: 20, warn: 30, error: 40 };
const REDACT_KEYS = new Set(["token", "authtoken", "auth", "password", "secret"]);
const MAX_QUEUE = 5_000;
const FLUSH_MS = 200;
const ANNOTATION_TTL_MS = 60_000;
const MAX_ANNOTATIONS = 2_000;
const MAX_DEPTH = 6;

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
  private dropped = 0;
  private readonly annotations = new Map<string, { fields: LogFields; at: number }>();
  level: LogLevel;
  private maxBytes: number;
  private retainDays: number;

  constructor(readonly home: string, options: LogOptions = {}) {
    this.level = options.level ?? "info";
    this.maxBytes = Math.max(1, options.maxFileMb ?? 10) * 1024 * 1024;
    this.retainDays = Math.max(1, options.retainDays ?? 14);
  }

  configure(options: LogOptions): void {
    if (options.level && options.level in LEVELS) this.level = options.level;
    if (options.maxFileMb && options.maxFileMb > 0) this.maxBytes = Math.max(1, options.maxFileMb) * 1024 * 1024;
    if (options.retainDays && options.retainDays > 0) this.retainDays = options.retainDays;
  }

  /** The current log file (status detail:full). */
  get currentFile(): string | null {
    return this.file;
  }

  get isDisabled(): boolean {
    return this.disabled;
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

  /** Attach transport metrics to the coming `call` line of this request id (merged, one line per call). */
  annotate(reqId: string, fields: LogFields): void {
    if (!reqId || this.closed || this.disabled) return;
    const now = Date.now();
    const existing = this.annotations.get(reqId);
    this.annotations.set(reqId, { fields: { ...(existing?.fields ?? {}), ...stripUndefined(fields) }, at: existing?.at ?? now });
    if (this.annotations.size > MAX_ANNOTATIONS) this.expireAnnotations(now, true);
    this.schedule();
  }

  write(level: LogLevel, evt: string, fields: LogFields): void {
    if (this.closed || this.disabled || LEVELS[level] < LEVELS[this.level]) return;
    let merged = fields;
    if (evt === "call" && typeof fields.reqId === "string") {
      const a = this.annotations.get(fields.reqId);
      if (a) {
        this.annotations.delete(fields.reqId);
        merged = { ...a.fields, ...stripUndefined(fields) };
      }
    }
    this.push(level, evt, merged);
  }

  private push(level: LogLevel, evt: string, fields: LogFields): void {
    let line: string;
    try {
      line = JSON.stringify({ ts: new Date().toISOString(), lvl: level, evt, pid: process.pid, ...(redact(fields, 0) as LogFields) });
    } catch {
      line = JSON.stringify({ ts: new Date().toISOString(), lvl: level, evt, pid: process.pid, note: "unserializable fields" });
    }
    this.queue.push(line);
    if (this.queue.length > MAX_QUEUE) {
      const drop = this.queue.length - MAX_QUEUE;
      this.queue.splice(0, drop);
      this.dropped += drop;
    }
    this.schedule();
  }

  private schedule(): void {
    if (this.timer || this.closed) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.expireAnnotations(Date.now(), false);
      void this.flush();
    }, FLUSH_MS);
    this.timer.unref();
  }

  /** Annotations without a call line: written as "exchange" lines (control-only paths, nested helpers). */
  private expireAnnotations(now: number, force: boolean): void {
    for (const [reqId, a] of this.annotations) {
      if (!force && now - a.at < ANNOTATION_TTL_MS) continue;
      this.annotations.delete(reqId);
      if (LEVELS.info >= LEVELS[this.level]) this.push("info", "exchange", { reqId, ...a.fields });
      if (force && this.annotations.size <= MAX_ANNOTATIONS / 2) break;
    }
    if (this.annotations.size > 0) this.schedule();
  }

  /** Write everything queued so far. */
  flush(): Promise<void> {
    if (this.dropped > 0) {
      const n = this.dropped;
      this.dropped = 0;
      this.queue.unshift(JSON.stringify({ ts: new Date().toISOString(), lvl: "warn", evt: "log_dropped", pid: process.pid, lines: n }));
    }
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
    this.expireAnnotations(Date.now(), true);
    for (const [reqId, a] of this.annotations) this.push("info", "exchange", { reqId, ...a.fields });
    this.annotations.clear();
    await this.flush();
    this.closed = true;
  }

  private async append(lines: string[]): Promise<void> {
    if (lines.length === 0 || this.disabled) return;
    try {
      if (!this.dirReady) {
        await mkdir(logsDir(this.home), { recursive: true });
        this.dirReady = true;
      }
      const text = lines.join("\n") + "\n";
      const bytes = Buffer.byteLength(text);
      const target = await this.target(bytes);
      await appendFile(target, text, "utf8");
      this.size += bytes;
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
      // Retention runs at the first write and at every day change (long-running brokers).
      void this.prune();
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

function stripUndefined(fields: LogFields): LogFields {
  const out: LogFields = {};
  for (const [k, v] of Object.entries(fields)) if (v !== undefined) out[k] = v;
  return out;
}

/** Drop secrets at any depth; errors become {message, stack}; bound depth and string sizes. */
function redact(value: unknown, depth: number): unknown {
  if (value instanceof Error) return { message: value.message, code: (value as NodeJS.ErrnoException).code, stack: value.stack?.split("\n").slice(0, 6).join("\n") };
  if (typeof value === "string") return value.length > 4_000 ? `${value.slice(0, 4_000)}...(${value.length} chars)` : value;
  if (value === null || typeof value !== "object") return value;
  if (depth >= MAX_DEPTH) return "[depth]";
  if (Array.isArray(value)) return value.slice(0, 200).map((v) => redact(v, depth + 1));
  const out: LogFields = {};
  for (const [key, v] of Object.entries(value as Record<string, unknown>)) {
    if (REDACT_KEYS.has(key.toLowerCase())) continue;
    out[key] = redact(v, depth + 1);
  }
  return out;
}

/** A logger that discards everything (used before the home is known). */
export const NULL_LOG = new BrokerLog("", { level: "error" });
(NULL_LOG as unknown as { disabled: boolean }).disabled = true;
