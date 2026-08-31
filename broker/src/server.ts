import { McpServer, SUPPORTED_PROTOCOL_VERSIONS } from "@modelcontextprotocol/server";
import { BRIDGE_PROTOCOL_VERSION } from "@revit-mcp-next/contracts";
import type { RevitBridgeClient } from "./ipc/RevitBridgeClient.js";
import { registerCoreTools } from "./tools/coreTools.js";
import { registerDiscovery } from "./tools/discovery.js";
import { ModelDeliveryRecipeStore } from "./recipes/ModelDeliveryRecipeStore.js";
import type { RevitInstanceDirectory } from "./instances/RevitInstanceDirectory.js";
import { SingleRevitInstanceDirectory } from "./instances/RevitInstanceDirectory.js";
import { SessionTargetStore } from "./targeting/SessionTargetStore.js";
import { SessionTargetingBridgeRouter } from "./targeting/SessionTargetingBridgeRouter.js";

export interface BrokerServerOptions {
  bridge: RevitBridgeClient;
  brokerVersion: string;
  sessionId: string;
  recipeStore?: ModelDeliveryRecipeStore;
  instanceDirectory?: RevitInstanceDirectory;
  targetStore?: SessionTargetStore;
}

export const BROKER_MCP_PROTOCOL_VERSIONS = [
  "2026-07-28",
  ...SUPPORTED_PROTOCOL_VERSIONS,
];

export const BROKER_DISCOVERY_CACHE_TTL_MS = 300_000;

export function createBrokerServer(options: BrokerServerOptions): McpServer {
  const instanceDirectory = options.instanceDirectory ?? new SingleRevitInstanceDirectory(options.bridge);
  const targetStore = options.targetStore ?? new SessionTargetStore(options.sessionId);
  const targeting = new SessionTargetingBridgeRouter(instanceDirectory, targetStore, options.sessionId);
  const server = new McpServer(
    {
      name: "revit-mcp-next",
      version: options.brokerVersion,
    },
    {
      supportedProtocolVersions: BROKER_MCP_PROTOCOL_VERSIONS,
      cacheHints: {
        "server/discover": { ttlMs: BROKER_DISCOVERY_CACHE_TTL_MS, cacheScope: "private" },
        "tools/list": { ttlMs: BROKER_DISCOVERY_CACHE_TTL_MS, cacheScope: "private" },
        "prompts/list": { ttlMs: BROKER_DISCOVERY_CACHE_TTL_MS, cacheScope: "private" },
        "resources/list": { ttlMs: BROKER_DISCOVERY_CACHE_TTL_MS, cacheScope: "private" },
        "resources/templates/list": { ttlMs: BROKER_DISCOVERY_CACHE_TTL_MS, cacheScope: "private" },
        "resources/read": { ttlMs: BROKER_DISCOVERY_CACHE_TTL_MS, cacheScope: "private" },
      },
      instructions:
        "Use this server for safe Autodesk Revit inspection and automation. When more than one project or Revit process may be open, start with revit.list_instances and revit.set_target. Document-scoped calls inherit this session-only target. Query tools return bounded structuredContent; never infer totals from returned array length. Use preview/apply for mutations.",
    }
  );

  registerCoreTools(server, {
    ...options,
    bridge: targeting.bridge,
    targeting,
    bridgeProtocolVersion: BRIDGE_PROTOCOL_VERSION,
    recipeStore: options.recipeStore ?? new ModelDeliveryRecipeStore(),
  });
  registerDiscovery(server, {
    brokerVersion: options.brokerVersion,
    bridgeProtocolVersion: BRIDGE_PROTOCOL_VERSION,
  });

  return server;
}
