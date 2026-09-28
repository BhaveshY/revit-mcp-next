#!/usr/bin/env node
import { serveStdio } from "@modelcontextprotocol/server/stdio";
import { createBrokerServer } from "./server.js";
import { NamedPipeBridgeClient } from "./ipc/NamedPipeBridgeClient.js";
import { FileSystemRevitInstanceDirectory } from "./instances/FileSystemRevitInstanceDirectory.js";

const brokerVersion = process.env.REVIT_MCP_NEXT_VERSION ?? "0.3.0";
const pipeName = process.env.REVIT_MCP_NEXT_PIPE ?? "revit-mcp-next";
const sessionId = process.env.REVIT_MCP_NEXT_SESSION ?? `broker-${process.pid}`;
const defaultTimeoutMs = resolveDefaultTimeoutMs(process.env.REVIT_MCP_NEXT_TIMEOUT_MS);

const bridge = new NamedPipeBridgeClient({
  pipeName,
  sessionId,
  defaultTimeoutMs,
});
const instanceDirectory = new FileSystemRevitInstanceDirectory({
  sessionId,
  defaultTimeoutMs,
  fallbackBridge: bridge,
  fallbackPipeName: pipeName,
  registryRoot: process.env.REVIT_MCP_NEXT_INSTANCE_REGISTRY,
});

const stdio = serveStdio(
  () => createBrokerServer({ bridge, brokerVersion, sessionId, instanceDirectory }),
  { legacy: "serve" }
);
let shuttingDown = false;

async function shutdown(): Promise<void> {
  if (shuttingDown) return;
  shuttingDown = true;
  instanceDirectory.dispose();
  bridge.dispose();
  try {
    await stdio.close();
  } catch (error) {
    process.stderr.write(
      `Revit MCP Next shutdown failed: ${error instanceof Error ? error.message : String(error)}\n`
    );
    process.exitCode = 1;
  }
}

process.once("SIGINT", () => void shutdown());
process.once("SIGTERM", () => void shutdown());
process.stdin.once("end", () => void shutdown());


// A malformed REVIT_MCP_NEXT_TIMEOUT_MS used to throw a RangeError at startup, which MCP
// clients only report as "server failed to start". Fall back to the default and say why.
function resolveDefaultTimeoutMs(raw: string | undefined): number {
  const fallback = 30000;
  if (raw === undefined || raw.trim() === "") return fallback;
  const parsed = Number(raw);
  if (Number.isInteger(parsed) && parsed > 0 && parsed <= 2_147_483_647) return parsed;
  process.stderr.write(
    `Revit MCP Next: ignoring invalid REVIT_MCP_NEXT_TIMEOUT_MS=${JSON.stringify(raw)}; using ${fallback} ms.\n`
  );
  return fallback;
}
