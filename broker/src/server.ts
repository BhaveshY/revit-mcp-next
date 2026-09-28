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
      instructions: [
        "Use this server for safe Autodesk Revit inspection and automation.",
        "Start: call revit.status (one Revit/project open) or revit.list_instances then revit.set_target (several open). Document-scoped calls inherit this session-only target; do not repeat instanceId/documentFingerprint and do not pass expectedGeneration to reads.",
        "Reads: prefer revit.read_bundle for preflight, then revit.query / revit.catalog / revit.describe_parameters with tight filters and fields. Results are bounded pages; never infer totals from array length; follow structuredContent.data.cursor with identical arguments.",
        "Writes: revit.preview_change_set, then revit.apply_change_set echoing previewId, baseGeneration, changeSetHash, expiresAt and confirm=true. Never apply a blocked preview.",
        "Failures: BRIDGE_UNAVAILABLE means Revit is not running or the add-in is not loaded; REVIT_BUSY, BRIDGE_BUSY or REVIT_EXTERNAL_EVENT_TIMEOUT mean Revit is not idle (open dialog, edit mode, long command). In those cases stop, tell the user what to fix, and retry at most once afterwards instead of looping. BRIDGE_WRITE_OUTCOME_UNKNOWN: never repeat the write; use revit.get_request_result.",
      ].join(" "),
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
