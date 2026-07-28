#!/usr/bin/env node
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { createBrokerServer } from "./server.js";
import { NamedPipeBridgeClient } from "./ipc/NamedPipeBridgeClient.js";

const brokerVersion = process.env.REVIT_MCP_NEXT_VERSION ?? "0.1.0";
const pipeName = process.env.REVIT_MCP_NEXT_PIPE ?? "revit-mcp-next";
const sessionId = process.env.REVIT_MCP_NEXT_SESSION ?? `broker-${process.pid}`;

const bridge = new NamedPipeBridgeClient({
  pipeName,
  sessionId,
  defaultTimeoutMs: Number(process.env.REVIT_MCP_NEXT_TIMEOUT_MS ?? 30000),
});

const server = createBrokerServer({ bridge, brokerVersion, sessionId });
const transport = new StdioServerTransport();
let shuttingDown = false;

async function shutdown(): Promise<void> {
  if (shuttingDown) return;
  shuttingDown = true;
  bridge.dispose();
  try {
    await server.close();
  } catch (error) {
    process.stderr.write(
      `Revit MCP Next shutdown failed: ${error instanceof Error ? error.message : String(error)}\n`
    );
    process.exitCode = 1;
  }
}

process.once("SIGINT", () => void shutdown());
process.once("SIGTERM", () => void shutdown());
process.stdin.once("end", () => bridge.dispose());

await server.connect(transport);

