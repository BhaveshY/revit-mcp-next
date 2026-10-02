// Crash guards (D2 §14.1): stdout carries MCP only; rejections/exceptions are logged and survived; EPIPE on stdout
// means the client is gone (exit 0); more than 20 faults in 60 s exits 70 as the last resort.
// Basic implementation; P-REL-BROKER owns hardening.

import type { BrokerLog } from "./log.js";

let faultTimes: number[] = [];
let totalFaults = 0;

export function faultCount(): number {
  return totalFaults;
}

/** First statement of index.ts: console.log/info/debug go to stderr so stdout stays JSON-RPC only. */
export function redirectConsoleToStderr(): void {
  const toStderr = (...args: unknown[]) => {
    try {
      process.stderr.write(args.map((a) => (typeof a === "string" ? a : safeInspect(a))).join(" ") + "\n");
    } catch {
      // stderr gone: nothing to do
    }
  };
  console.log = toStderr;
  console.info = toStderr;
  console.debug = toStderr;
  console.warn = toStderr;
  console.trace = toStderr;
}

function safeInspect(value: unknown): string {
  try {
    if (value instanceof Error) return `${value.name}: ${value.message}`;
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

export interface CrashGuardOptions {
  log: () => BrokerLog | null;
  /** Called when the process must exit (EPIPE on stdout: 0, fault storm: 70). */
  exit: (code: number) => void;
}

export function installCrashGuards(options: CrashGuardOptions): void {
  const fault = (kind: string, error: unknown) => {
    totalFaults += 1;
    const now = Date.now();
    faultTimes = faultTimes.filter((t) => now - t < 60_000);
    faultTimes.push(now);
    const err = error instanceof Error ? error : new Error(String(error));
    try {
      options.log()?.error("fault", { kind, error: err, faults: totalFaults });
      process.stderr.write(`revit-mcp-next: ${kind}: ${err.message}\n`);
    } catch {
      // ignore
    }
    if (faultTimes.length > 20) options.exit(70);
  };
  process.on("unhandledRejection", (reason) => fault("unhandledRejection", reason));
  process.on("uncaughtException", (error: NodeJS.ErrnoException) => {
    if (error && (error.code === "EPIPE" || error.code === "ERR_STREAM_DESTROYED") && /write|stdout/i.test(`${error.message} ${error.stack ?? ""}`)) {
      options.exit(0);
      return;
    }
    fault("uncaughtException", error);
  });
  process.stdout.on("error", (error: NodeJS.ErrnoException) => {
    if (error.code === "EPIPE" || error.code === "ERR_STREAM_DESTROYED") options.exit(0);
    else fault("stdout", error);
  });
}

/** Wrap a callback so an exception becomes a logged fault instead of a process event. */
export function safe<A extends unknown[]>(fn: (...args: A) => void, log?: () => BrokerLog | null): (...args: A) => void {
  return (...args: A) => {
    try {
      fn(...args);
    } catch (error) {
      totalFaults += 1;
      try {
        log?.()?.error("fault", { kind: "callback", error });
      } catch {
        // ignore
      }
    }
  };
}
