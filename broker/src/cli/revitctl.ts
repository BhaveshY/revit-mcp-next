#!/usr/bin/env node
import { existsSync, readFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import type { BridgeRequest, BridgeResponse, OperationKind } from "@revit-mcp-next/contracts";
import { makeRequest } from "../ipc/RequestFactory.js";
import { NamedPipeBridgeClient } from "../ipc/NamedPipeBridgeClient.js";

const DEFAULT_PIPE_NAME = "revit-mcp-next";
const DEFAULT_TIMEOUT_MS = 30000;
const READ_OPERATIONS = new Set([
  "status",
  "list_documents",
  "get_levels",
  "get_views",
  "get_sheets",
  "get_current_view",
  "get_current_view_elements",
  "get_selection",
  "analyze_model",
  "get_model_readiness",
  "get_material_quantities",
  "get_rooms",
  "catalog",
  "query",
  "describe_parameters",
]);

export interface RevitCtlOptions {
  command: string;
  operation?: string;
  payload?: unknown;
  jsonOutput: boolean;
  discoveryPath?: string;
  authConfigPath?: string;
  installRoot?: string;
  pipeName?: string;
  timeoutMs: number;
  confirm: boolean;
}

interface ClientDiscovery {
  installRoot?: string;
  pipeName?: string;
  authConfigPath?: string;
  version?: string;
}

interface RuntimeConfig {
  pipeName: string;
  authToken?: string;
  timeoutMs: number;
  discovery?: ClientDiscovery;
}

export class RevitCtlUsageError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "RevitCtlUsageError";
  }
}

export function parseArgs(argv: string[]): RevitCtlOptions {
  const positional: string[] = [];
  let payload: unknown;
  let discoveryPath: string | undefined;
  let authConfigPath: string | undefined;
  let installRoot: string | undefined;
  let pipeName: string | undefined;
  let timeoutMs = DEFAULT_TIMEOUT_MS;
  let jsonOutput = true;
  let confirm = false;

  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    switch (arg) {
      case "--help":
      case "-h":
        positional.push("help");
        break;
      case "--json":
        jsonOutput = true;
        break;
      case "--pretty":
        jsonOutput = false;
        break;
      case "--confirm":
        confirm = true;
        break;
      case "--payload":
      case "--input":
      case "-i":
        payload = readJsonArgument(requireValue(argv, ++index, arg));
        break;
      case "--discovery":
        discoveryPath = requireValue(argv, ++index, arg);
        break;
      case "--auth-config":
        authConfigPath = requireValue(argv, ++index, arg);
        break;
      case "--install-root":
        installRoot = requireValue(argv, ++index, arg);
        break;
      case "--pipe":
        pipeName = requireValue(argv, ++index, arg);
        break;
      case "--timeout-ms":
        timeoutMs = parsePositiveInt(requireValue(argv, ++index, arg), arg);
        break;
      default:
        if (arg.startsWith("--")) {
          throw new RevitCtlUsageError(`Unknown option: ${arg}`);
        }
        positional.push(arg);
        break;
    }
  }

  const command = positional.shift() ?? "help";
  if (command === "preview" || command === "apply") {
    payload = payload ?? readPositionalJson(positional.shift(), command);
  }

  if (command === "call") {
    const operation = positional.shift();
    if (!operation) throw new RevitCtlUsageError("Usage: revitctl call <operation> --payload <json-or-path>");
    return {
      command,
      operation,
      payload: payload ?? {},
      jsonOutput,
      discoveryPath,
      authConfigPath,
      installRoot,
      pipeName,
      timeoutMs,
      confirm,
    };
  }

  if (positional.length > 0) {
    throw new RevitCtlUsageError(`Unexpected argument: ${positional[0]}`);
  }

  return {
    command,
    payload,
    jsonOutput,
    discoveryPath,
    authConfigPath,
    installRoot,
    pipeName,
    timeoutMs,
    confirm,
  };
}

export async function runRevitCtl(options: RevitCtlOptions): Promise<{ exitCode: number; body: unknown }> {
  if (options.command === "help") {
    return { exitCode: 0, body: helpText() };
  }

  const runtime = resolveRuntimeConfig(options);
  const sessionId = `revitctl-${process.pid}`;
  const bridge = new NamedPipeBridgeClient({
    pipeName: runtime.pipeName,
    sessionId,
    defaultTimeoutMs: runtime.timeoutMs,
    authToken: runtime.authToken,
  });

  try {
    if (options.command === "doctor") {
      return runDoctor(bridge, sessionId, runtime.timeoutMs, runtime.discovery);
    }

    const { operation, operationKind, payload } = resolveCommandOperation(options);
    const request = makeRequest(sessionId, operation, operationKind, payload, runtime.timeoutMs);
    const response = await callBridge(bridge, request);
    return { exitCode: response.ok ? 0 : 2, body: response };
  } finally {
    bridge.dispose();
  }
}

export function resolveRuntimeConfig(options: RevitCtlOptions): RuntimeConfig {
  const discoveryPath = resolveDiscoveryPath(options);
  const discovery = discoveryPath && existsSync(discoveryPath) ? readJsonFile<ClientDiscovery>(discoveryPath) : undefined;
  const authConfigPath =
    options.authConfigPath ??
    process.env.REVIT_MCP_NEXT_AUTH_CONFIG ??
    discovery?.authConfigPath ??
    defaultAuthConfigPath(options.installRoot ?? discovery?.installRoot);
  const authToken = resolveAuthToken(authConfigPath);

  return {
    pipeName: options.pipeName ?? process.env.REVIT_MCP_NEXT_PIPE ?? discovery?.pipeName ?? DEFAULT_PIPE_NAME,
    authToken,
    timeoutMs: options.timeoutMs,
    discovery,
  };
}

export function resolveAuthToken(authConfigPath?: string): string | undefined {
  if (process.env.REVIT_MCP_NEXT_AUTH_TOKEN) return process.env.REVIT_MCP_NEXT_AUTH_TOKEN;
  if (!authConfigPath || !existsSync(authConfigPath)) return undefined;
  return parseAuthTokenConfig(readFileSync(authConfigPath, "utf8"));
}

export function parseAuthTokenConfig(text: string): string | undefined {
  for (const line of text.split(/\r?\n/)) {
    const match = line.match(/^\s*REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*"?([^"\s]+)"?\s*$/i);
    if (match?.[1]) return match[1];
  }
  return undefined;
}

function resolveCommandOperation(options: RevitCtlOptions): {
  operation: string;
  operationKind: OperationKind;
  payload: Record<string, unknown>;
} {
  switch (options.command) {
    case "status":
      return { operation: "status", operationKind: "read", payload: {} };
    case "list-documents":
    case "docs":
      return { operation: "list_documents", operationKind: "read", payload: {} };
    case "levels":
      return { operation: "get_levels", operationKind: "read", payload: payloadObject(options.payload) };
    case "views":
      return { operation: "get_views", operationKind: "read", payload: payloadObject(options.payload) };
    case "sheets":
      return { operation: "get_sheets", operationKind: "read", payload: payloadObject(options.payload) };
    case "current-view":
      return { operation: "get_current_view", operationKind: "read", payload: payloadObject(options.payload) };
    case "selection":
      return { operation: "get_selection", operationKind: "read", payload: payloadObject(options.payload) };
    case "readiness":
      return { operation: "get_model_readiness", operationKind: "read", payload: payloadObject(options.payload) };
    case "query":
      return { operation: "query", operationKind: "read", payload: payloadObject(options.payload) };
    case "catalog":
      return { operation: "catalog", operationKind: "read", payload: payloadObject(options.payload) };
    case "parameters":
    case "describe-parameters":
      return { operation: "describe_parameters", operationKind: "read", payload: payloadObject(options.payload) };
    case "preview":
      return { operation: "preview_change_set", operationKind: "preview", payload: payloadObject(options.payload) };
    case "apply": {
      const payload = payloadObject(options.payload);
      if (!options.confirm && payload.confirm !== true) {
        throw new RevitCtlUsageError("revitctl apply requires --confirm or payload.confirm=true.");
      }
      return { operation: "apply_change_set", operationKind: "write", payload: { ...payload, confirm: true } };
    }
    case "call": {
      const operation = options.operation ?? "";
      return { operation, operationKind: inferOperationKind(operation), payload: payloadObject(options.payload) };
    }
    default:
      throw new RevitCtlUsageError(`Unknown command: ${options.command}`);
  }
}

async function runDoctor(
  bridge: NamedPipeBridgeClient,
  sessionId: string,
  timeoutMs: number,
  discovery?: ClientDiscovery
): Promise<{ exitCode: number; body: unknown }> {
  const request = makeRequest(sessionId, "status", "read", {}, Math.min(timeoutMs, 10000));
  const status = await bridge.status(request);
  const body = {
    schemaVersion: 1,
    product: "revit-mcp-next",
    check: "bridge",
    ok: status.ok && Boolean(status.data?.connected),
    discovery: discovery
      ? {
          installRoot: discovery.installRoot,
          pipeName: discovery.pipeName,
          version: discovery.version,
          authConfigPresent: Boolean(discovery.authConfigPath),
        }
      : null,
    status,
  };
  return { exitCode: body.ok ? 0 : 2, body };
}

async function callBridge(
  bridge: NamedPipeBridgeClient,
  request: BridgeRequest<Record<string, unknown>>
): Promise<BridgeResponse<unknown>> {
  switch (request.operation) {
    case "status":
      return bridge.status(request as Parameters<NamedPipeBridgeClient["status"]>[0]);
    case "list_documents":
      return bridge.listDocuments(request as Parameters<NamedPipeBridgeClient["listDocuments"]>[0]);
    case "get_levels":
      return bridge.getLevels(request as Parameters<NamedPipeBridgeClient["getLevels"]>[0]);
    case "get_views":
      return bridge.getViews(request as unknown as Parameters<NamedPipeBridgeClient["getViews"]>[0]);
    case "get_sheets":
      return bridge.getSheets(request as unknown as Parameters<NamedPipeBridgeClient["getSheets"]>[0]);
    case "get_current_view":
      return bridge.getCurrentView(request as Parameters<NamedPipeBridgeClient["getCurrentView"]>[0]);
    case "get_current_view_elements":
      return bridge.getCurrentViewElements(request as Parameters<NamedPipeBridgeClient["getCurrentViewElements"]>[0]);
    case "get_selection":
      return bridge.getSelection(request as Parameters<NamedPipeBridgeClient["getSelection"]>[0]);
    case "analyze_model":
      return bridge.analyzeModel(request as Parameters<NamedPipeBridgeClient["analyzeModel"]>[0]);
    case "get_model_readiness":
      return bridge.getModelReadiness(request as Parameters<NamedPipeBridgeClient["getModelReadiness"]>[0]);
    case "get_material_quantities":
      return bridge.getMaterialQuantities(request as Parameters<NamedPipeBridgeClient["getMaterialQuantities"]>[0]);
    case "get_rooms":
      return bridge.getRooms(request as Parameters<NamedPipeBridgeClient["getRooms"]>[0]);
    case "query":
      return bridge.query(request as unknown as Parameters<NamedPipeBridgeClient["query"]>[0]);
    case "describe_parameters":
      return bridge.describeParameters(request as unknown as Parameters<NamedPipeBridgeClient["describeParameters"]>[0]);
    case "catalog":
      return bridge.catalog(request as unknown as Parameters<NamedPipeBridgeClient["catalog"]>[0]);
    case "preview_change_set":
      return bridge.previewChange(request as unknown as Parameters<NamedPipeBridgeClient["previewChange"]>[0]);
    case "apply_change_set":
      return bridge.applyChange(request as unknown as Parameters<NamedPipeBridgeClient["applyChange"]>[0]);
    case "cancel_request":
      return bridge.cancel(request as Parameters<NamedPipeBridgeClient["cancel"]>[0]);
    default:
      throw new RevitCtlUsageError(`Unsupported bridge operation: ${request.operation}`);
  }
}

function inferOperationKind(operation: string): OperationKind {
  if (operation === "preview_change_set") return "preview";
  if (operation === "apply_change_set") return "write";
  if (operation === "cancel_request") return "debug";
  if (READ_OPERATIONS.has(operation)) return "read";
  return "debug";
}

function payloadObject(value: unknown): Record<string, unknown> {
  if (value === undefined || value === null) return {};
  if (typeof value === "object" && !Array.isArray(value)) return value as Record<string, unknown>;
  throw new RevitCtlUsageError("Payload must be a JSON object.");
}

function requireValue(argv: string[], index: number, option: string): string {
  const value = argv[index];
  if (!value || value.startsWith("--")) throw new RevitCtlUsageError(`${option} requires a value.`);
  return value;
}

function parsePositiveInt(value: string, option: string): number {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed <= 0) throw new RevitCtlUsageError(`${option} must be a positive integer.`);
  return parsed;
}

function readPositionalJson(value: string | undefined, command: string): unknown {
  if (!value) throw new RevitCtlUsageError(`Usage: revitctl ${command} <json-file>`);
  return readJsonArgument(value);
}

function readJsonArgument(value: string): unknown {
  const trimmed = value.trim();
  if (trimmed === "-") {
    return JSON.parse(readFileSync(0, "utf8"));
  }
  if (trimmed.startsWith("{")) {
    return JSON.parse(trimmed);
  }
  return readJsonFile(trimmed);
}

function readJsonFile<T = unknown>(filePath: string): T {
  return JSON.parse(readFileSync(path.resolve(filePath), "utf8")) as T;
}

function resolveDiscoveryPath(options: RevitCtlOptions): string | undefined {
  if (options.discoveryPath) return options.discoveryPath;
  if (process.env.REVIT_MCP_NEXT_CLIENT_DISCOVERY) return process.env.REVIT_MCP_NEXT_CLIENT_DISCOVERY;
  return path.join(options.installRoot ?? defaultInstallRoot(), "config", "client-discovery.json");
}

function defaultInstallRoot(): string {
  if (process.env.LOCALAPPDATA) return path.join(process.env.LOCALAPPDATA, "RevitMcpNext");
  return path.join(os.homedir(), "AppData", "Local", "RevitMcpNext");
}

function defaultAuthConfigPath(installRoot?: string): string {
  return path.join(installRoot ?? defaultInstallRoot(), "config", "auth.env");
}

function helpText(): string {
  return `revitctl - internal Revit MCP Next bridge CLI

Usage:
  revitctl status [--json]
  revitctl doctor [--json]
  revitctl readiness [--payload <json-or-path>]
  revitctl list-documents
  revitctl levels [--payload <json-or-path>]
  revitctl views [--payload <json-or-path>]
  revitctl sheets [--payload <json-or-path>]
  revitctl current-view [--payload <json-or-path>]
  revitctl selection [--payload <json-or-path>]
  revitctl query --payload <json-or-path>
  revitctl catalog --payload <json-or-path>
  revitctl parameters --payload <json-or-path>
  revitctl preview <change-set.json>
  revitctl apply <apply-payload.json> --confirm
  revitctl call <operation> --payload <json-or-path>

Options:
  --discovery <path>      client-discovery.json path
  --auth-config <path>    auth.env path; token value is never printed
  --install-root <path>   installed Revit MCP Next root
  --pipe <name>           named pipe; default revit-mcp-next
  --timeout-ms <n>        bridge timeout in milliseconds
  --pretty               pretty-print JSON output
`;
}

function isDirectRun(): boolean {
  if (process.execArgv.some((arg) => arg === "-e" || arg === "--eval")) return false;
  if (!process.argv[1]) return false;
  return pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url;
}

if (isDirectRun()) {
  Promise.resolve()
    .then(() => {
      const options = parseArgs(process.argv.slice(2));
      return runRevitCtl(options).then((result) => ({ ...result, options }));
    })
    .then(({ exitCode, body, options }) => {
      if (typeof body === "string") {
        process.stdout.write(`${body}\n`);
      } else {
        process.stdout.write(`${JSON.stringify(body, null, options.jsonOutput ? 0 : 2)}\n`);
      }
      process.exitCode = exitCode;
    })
    .catch((error) => {
      const exitCode = error instanceof RevitCtlUsageError ? 64 : 1;
      process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
      process.exitCode = exitCode;
    });
}
