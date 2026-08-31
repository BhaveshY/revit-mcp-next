import type { RevitBridgeClient } from "../ipc/RevitBridgeClient.js";

export interface RuntimeInstanceMetadata {
  schemaVersion: 1;
  instanceId: string;
  pipeName: string;
  controlPipeName: string;
  processId?: number;
  revitVersion?: string;
  revitBuild?: string;
  addinVersion?: string;
  startedAtUtc?: string;
  lastSeenAtUtc?: string;
}

export interface RevitInstanceConnection {
  metadata: RuntimeInstanceMetadata;
  bridge: RevitBridgeClient;
  source: "registry" | "legacy" | "test";
}

export interface RevitInstanceDirectory {
  listConnections(): Promise<RevitInstanceConnection[]>;
  dispose(): void;
}

export class SingleRevitInstanceDirectory implements RevitInstanceDirectory {
  private readonly connection: RevitInstanceConnection;

  constructor(bridge: RevitBridgeClient, instanceId = "legacy-default") {
    this.connection = {
      metadata: {
        schemaVersion: 1,
        instanceId,
        pipeName: "revit-mcp-next",
        controlPipeName: "revit-mcp-next-control",
      },
      bridge,
      source: "test",
    };
  }

  async listConnections(): Promise<RevitInstanceConnection[]> {
    return [this.connection];
  }

  dispose(): void {
    // The caller owns the injected bridge lifecycle.
  }
}
