import { promises as fs } from "node:fs";
import os from "node:os";
import path from "node:path";
import type { RevitBridgeClient } from "../ipc/RevitBridgeClient.js";
import { NamedPipeBridgeClient } from "../ipc/NamedPipeBridgeClient.js";
import type {
  RevitInstanceConnection,
  RevitInstanceDirectory,
  RuntimeInstanceMetadata,
} from "./RevitInstanceDirectory.js";

const DEFAULT_STALE_AFTER_MS = 90_000;
const MAX_REGISTRATION_BYTES = 64 * 1024;
const SAFE_INSTANCE_TOKEN = /^[A-Za-z0-9_-]{1,128}$/;
const SAFE_PIPE_TOKEN = /^[A-Za-z0-9._-]{1,220}$/;

export interface FileSystemRevitInstanceDirectoryOptions {
  sessionId: string;
  defaultTimeoutMs: number;
  fallbackBridge: RevitBridgeClient;
  fallbackPipeName: string;
  registryRoot?: string;
  staleAfterMs?: number;
  authToken?: string;
}

export class FileSystemRevitInstanceDirectory implements RevitInstanceDirectory {
  private readonly clients = new Map<string, { pipeName: string; client: NamedPipeBridgeClient }>();
  private disposed = false;

  constructor(private readonly options: FileSystemRevitInstanceDirectoryOptions) {}

  async listConnections(): Promise<RevitInstanceConnection[]> {
    if (this.disposed) return [];

    const registrations = await this.readLiveRegistrations();
    if (registrations.length === 0) {
      return [
        {
          metadata: {
            schemaVersion: 1,
            instanceId: "legacy-default",
            pipeName: this.options.fallbackPipeName,
            controlPipeName: `${this.options.fallbackPipeName}-control`,
          },
          bridge: this.options.fallbackBridge,
          source: "legacy",
        },
      ];
    }

    const liveIds = new Set(registrations.map((registration) => registration.instanceId));
    for (const [instanceId, cached] of this.clients) {
      if (liveIds.has(instanceId)) continue;
      cached.client.dispose();
      this.clients.delete(instanceId);
    }

    return registrations.map((metadata) => ({
      metadata,
      bridge: this.clientFor(metadata),
      source: "registry" as const,
    }));
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const cached of this.clients.values()) cached.client.dispose();
    this.clients.clear();
  }

  private clientFor(metadata: RuntimeInstanceMetadata): NamedPipeBridgeClient {
    const cached = this.clients.get(metadata.instanceId);
    if (cached?.pipeName === metadata.pipeName) return cached.client;
    cached?.client.dispose();

    const explicitAuth = Object.prototype.hasOwnProperty.call(this.options, "authToken")
      ? { authToken: this.options.authToken }
      : {};
    const client = new NamedPipeBridgeClient({
      pipeName: metadata.pipeName,
      controlPipeName: metadata.controlPipeName,
      sessionId: this.options.sessionId,
      defaultTimeoutMs: this.options.defaultTimeoutMs,
      ...explicitAuth,
    });
    this.clients.set(metadata.instanceId, { pipeName: metadata.pipeName, client });
    return client;
  }

  private async readLiveRegistrations(): Promise<RuntimeInstanceMetadata[]> {
    const root = this.options.registryRoot ?? defaultRegistryRoot();
    let names: string[];
    try {
      names = await fs.readdir(root);
    } catch (error) {
      if (isNodeError(error, "ENOENT")) return [];
      throw error;
    }

    const registrations: RuntimeInstanceMetadata[] = [];
    for (const name of names.filter((candidate) => candidate.endsWith(".json")).sort()) {
      const registrationPath = path.join(root, name);
      const metadata = await this.readRegistration(registrationPath);
      if (metadata) registrations.push(metadata);
    }
    return registrations.sort((left, right) => left.instanceId.localeCompare(right.instanceId));
  }

  private async readRegistration(registrationPath: string): Promise<RuntimeInstanceMetadata | undefined> {
    try {
      const stat = await fs.stat(registrationPath);
      if (!stat.isFile() || stat.size > MAX_REGISTRATION_BYTES) {
        await removeBestEffort(registrationPath);
        return undefined;
      }
      if (stat.size <= 0) return undefined;

      const parsed: unknown = JSON.parse(await fs.readFile(registrationPath, "utf8"));
      const metadata = parseRegistration(parsed);
      if (!metadata) return undefined;
      if (isStale(metadata, this.options.staleAfterMs ?? DEFAULT_STALE_AFTER_MS) || !isProcessAlive(metadata.processId)) {
        await removeBestEffort(registrationPath);
        return undefined;
      }
      return metadata;
    } catch {
      // A heartbeat may be replacing the file while it is read. Keep the last
      // registration path and retry on the next discovery call.
      return undefined;
    }
  }
}

function defaultRegistryRoot(): string {
  const localAppData = process.env.LOCALAPPDATA;
  return path.join(localAppData && localAppData.length > 0 ? localAppData : path.join(os.homedir(), "AppData", "Local"), "RevitMcpNext", "instances");
}

function parseRegistration(value: unknown): RuntimeInstanceMetadata | undefined {
  if (!isRecord(value) || value.schemaVersion !== 1) return undefined;
  const instanceId = stringField(value, "instanceId");
  const pipeName = stringField(value, "pipeName");
  const controlPipeName = stringField(value, "controlPipeName");
  const processId = integerField(value, "processId");
  if (!instanceId || !pipeName || !controlPipeName || !processId) return undefined;
  if (!SAFE_INSTANCE_TOKEN.test(instanceId) || !SAFE_PIPE_TOKEN.test(pipeName) || !SAFE_PIPE_TOKEN.test(controlPipeName)) return undefined;

  return {
    schemaVersion: 1,
    instanceId,
    pipeName,
    controlPipeName,
    processId,
    revitVersion: stringField(value, "revitVersion"),
    revitBuild: stringField(value, "revitBuild"),
    addinVersion: stringField(value, "addinVersion"),
    startedAtUtc: stringField(value, "startedAtUtc"),
    lastSeenAtUtc: stringField(value, "lastSeenAtUtc"),
  };
}

function isStale(metadata: RuntimeInstanceMetadata, staleAfterMs: number): boolean {
  const lastSeen = Date.parse(metadata.lastSeenAtUtc ?? "");
  return !Number.isFinite(lastSeen) || Date.now() - lastSeen > staleAfterMs;
}

function isProcessAlive(processId: number | undefined): boolean {
  if (!processId || processId <= 0) return false;
  try {
    process.kill(processId, 0);
    return true;
  } catch (error) {
    return isNodeError(error, "EPERM");
  }
}

async function removeBestEffort(filePath: string): Promise<void> {
  try {
    await fs.unlink(filePath);
  } catch {
    // Another broker or the add-in may already have removed/replaced the registration.
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringField(value: Record<string, unknown>, key: string): string | undefined {
  const child = value[key];
  return typeof child === "string" && child.length > 0 ? child : undefined;
}

function integerField(value: Record<string, unknown>, key: string): number | undefined {
  const child = value[key];
  return typeof child === "number" && Number.isInteger(child) ? child : undefined;
}

function isNodeError(error: unknown, code: string): boolean {
  return typeof error === "object" && error !== null && "code" in error && (error as { code?: unknown }).code === code;
}
