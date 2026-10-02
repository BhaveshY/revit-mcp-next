// Spawn the broker exactly like Codex does (D4 §8.3): `node broker/dist/src/index.js` (or --broker), the Codex env
// whitelist + REVIT_MCP_NEXT_HOME, a neutral cwd, protocol 2025-06-18 and clientInfo codex-mcp-client. Other eras:
// "2025-11-25" (Claude) and "modern" (versionNegotiation pin 2026-07-28, server/discover).

import { spawn } from "node:child_process";
import { mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/client";
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio";
import { codexEnv } from "./codex-env.mjs";

export const REPO = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
export const DEFAULT_BROKER = join(REPO, "broker", "dist", "src", "index.js");
export const MODERN = "2026-07-28";

/**
 * Connect a client session to a fresh broker process.
 * @param {{ home: string, broker?: string, era?: "2025-06-18"|"2025-11-25"|"modern", env?: Record<string,string|undefined>,
 *           clientName?: string, clientVersion?: string, elicitation?: boolean, cwd?: string, timeoutMs?: number }} o
 */
export async function connectBroker(o) {
  const era = o.era ?? "2025-06-18";
  const cwd = o.cwd ?? join(o.home, "runs");
  mkdirSync(cwd, { recursive: true });
  const env = codexEnv(process.env, { REVIT_MCP_NEXT_HOME: o.home }, o.env ?? {});
  const transport = new StdioClientTransport({ command: process.execPath, args: [o.broker ?? DEFAULT_BROKER], env, cwd, stderr: "pipe" });
  let stderr = "";
  transport.stderr?.on("data", (chunk) => {
    stderr += chunk.toString();
    if (stderr.length > 200_000) stderr = stderr.slice(-100_000);
  });
  const options = { capabilities: o.elicitation ? { elicitation: {} } : {} };
  if (era === "modern") options.versionNegotiation = { mode: { pin: MODERN } };
  else options.supportedProtocolVersions = [era];
  const client = new Client({ name: o.clientName ?? (era === "2025-11-25" ? "claude-code" : "codex-mcp-client"), version: o.clientVersion ?? "0.153.4" }, options);
  const t0 = Date.now();
  await client.connect(transport, { timeout: o.timeoutMs ?? 30_000 });
  const initMs = Date.now() - t0;
  return {
    client,
    transport,
    era,
    initMs,
    get pid() {
      return transport.pid;
    },
    stderr: () => stderr,
    protocol: () => client.getNegotiatedProtocolVersion?.(),
    instructions: () => client.getInstructions?.(),
    discover: () => client.getDiscoverResult?.(),
    async close() {
      try {
        await client.close();
      } catch {
        // already closed
      }
    },
  };
}

/**
 * Spawn the broker as a raw child (no SDK client) for the stdout/EOF checks: returns the process and helpers to send
 * JSON-RPC lines and collect every stdout line.
 */
export function spawnRawBroker({ home, broker, env = {} }) {
  const cwd = join(home, "runs");
  mkdirSync(cwd, { recursive: true });
  const child = spawn(process.execPath, [broker ?? DEFAULT_BROKER], { cwd, env: codexEnv(process.env, { REVIT_MCP_NEXT_HOME: home }, env), stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
  const stdoutLines = [];
  let buffer = "";
  let stderr = "";
  const waiters = [];
  child.stdout.on("data", (chunk) => {
    buffer += chunk.toString("utf8");
    let index;
    while ((index = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, index).replace(/\r$/, "");
      buffer = buffer.slice(index + 1);
      if (!line) continue;
      stdoutLines.push(line);
      for (const w of [...waiters]) w();
    }
  });
  child.stderr.on("data", (chunk) => (stderr += chunk.toString()));
  const exited = new Promise((resolveExit) => child.once("exit", (code, signal) => resolveExit({ code, signal, at: Date.now() })));
  return {
    child,
    stdoutLines,
    stderr: () => stderr,
    exited,
    send(message) {
      child.stdin.write(JSON.stringify(message) + "\n");
    },
    /** Wait for a JSON-RPC response with this id. */
    async response(id, timeoutMs = 10_000) {
      const deadline = Date.now() + timeoutMs;
      for (;;) {
        for (const line of stdoutLines) {
          try {
            const parsed = JSON.parse(line);
            if (parsed && parsed.id === id && ("result" in parsed || "error" in parsed)) return parsed;
          } catch {
            // checked by the caller
          }
        }
        if (Date.now() > deadline) throw new Error(`no response for id ${id} within ${timeoutMs} ms`);
        await new Promise((r) => {
          waiters.push(r);
          setTimeout(r, 100);
        });
        waiters.length = 0;
      }
    },
    endStdin() {
      child.stdin.end();
      return Date.now();
    },
    kill() {
      try {
        child.kill();
      } catch {
        // gone
      }
    },
  };
}
