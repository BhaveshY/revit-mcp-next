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

process.once("SIGINT", () => {
  bridge.dispose();
  process.exit(0);
});

process.once("SIGTERM", () => {
  bridge.dispose();
  process.exit(0);
});

await server.connect(transport);

