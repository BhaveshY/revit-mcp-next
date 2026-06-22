import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { PROTOCOL_VERSION } from "@revit-mcp-next/contracts";
import type { RevitBridgeClient } from "./ipc/RevitBridgeClient.js";
import { registerCoreTools } from "./tools/coreTools.js";

export interface BrokerServerOptions {
  bridge: RevitBridgeClient;
  brokerVersion: string;
  sessionId: string;
}

export function createBrokerServer(options: BrokerServerOptions): McpServer {
  const server = new McpServer(
    {
      name: "revit-mcp-next",
      version: options.brokerVersion,
    },
    {
      instructions:
        "Use this server for safe Autodesk Revit inspection and automation. Start with revit.status. Query tools return bounded structuredContent; never infer totals from returned array length. Use preview/apply for mutations.",
    }
  );

  registerCoreTools(server, {
    ...options,
    protocolVersion: PROTOCOL_VERSION,
  });

  return server;
}

