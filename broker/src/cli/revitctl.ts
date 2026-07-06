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
  "get_model_context",
  "get_material_quantities",
  "get_warnings",
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
  operationKind?: OperationKind;
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
  let operationKind: OperationKind | undefined;

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
      case "--operation-kind":
        operationKind = parseOperationKind(requireValue(argv, ++index, arg), arg);
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
      operationKind,
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
    operationKind,
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
    if (options.command === "read-bundle" || options.command === "bundle") {
      return runReadBundle(bridge, sessionId, runtime.timeoutMs, payloadObject(options.payload));
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
  for (const line of stripUtf8Bom(text).split(/\r?\n/)) {
    const match = line.match(/^\s*REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*"?([^"\s]+)"?\s*$/i);
    if (match?.[1]) return match[1];
  }
  return undefined;
}

export function stripUtf8Bom(text: string): string {
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

function parseJsonText<T = unknown>(text: string): T {
  return JSON.parse(stripUtf8Bom(text)) as T;
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
    case "create-project":
    case "create-project-from-template": {
      const payload = payloadObject(options.payload);
      if (!options.confirm && payload.confirm !== true) {
        throw new RevitCtlUsageError("revitctl create-project requires --confirm or payload.confirm=true.");
      }
      return { operation: "create_project_from_template", operationKind: "write", payload: options.confirm ? { ...payload, confirm: true } : payload };
    }
    case "levels":
      return { operation: "get_levels", operationKind: "read", payload: payloadObject(options.payload) };
    case "views":
      return { operation: "get_views", operationKind: "read", payload: payloadObject(options.payload) };
    case "sheets":
      return { operation: "get_sheets", operationKind: "read", payload: payloadObject(options.payload) };
    case "current-view":
      return { operation: "get_current_view", operationKind: "read", payload: payloadObject(options.payload) };
    case "current-view-elements":
    case "view-elements":
      return { operation: "get_current_view_elements", operationKind: "read", payload: payloadObject(options.payload) };
    case "selection":
      return { operation: "get_selection", operationKind: "read", payload: payloadObject(options.payload) };
    case "analyze":
    case "analyze-model":
      return { operation: "analyze_model", operationKind: "read", payload: payloadObject(options.payload) };
    case "readiness":
      return { operation: "get_model_readiness", operationKind: "read", payload: payloadObject(options.payload) };
    case "model-context":
    case "context":
      return { operation: "get_model_context", operationKind: "read", payload: payloadObject(options.payload) };
    case "materials":
    case "material-quantities":
      return { operation: "get_material_quantities", operationKind: "read", payload: payloadObject(options.payload) };
    case "warnings":
      return { operation: "get_warnings", operationKind: "read", payload: payloadObject(options.payload) };
    case "rooms":
      return { operation: "get_rooms", operationKind: "read", payload: payloadObject(options.payload) };
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
    case "cancel":
    case "cancel-request":
      return { operation: "cancel_request", operationKind: "debug", payload: payloadObject(options.payload) };
    case "call": {
      const operation = options.operation ?? "";
      const payload = payloadObject(options.payload);
      if (operation === "apply_change_set" && !options.confirm && payload.confirm !== true) {
        throw new RevitCtlUsageError("revitctl call apply_change_set requires --confirm or payload.confirm=true.");
      }
      if (operation === "create_project_from_template" && !options.confirm && payload.confirm !== true) {
        throw new RevitCtlUsageError("revitctl call create_project_from_template requires --confirm or payload.confirm=true.");
      }
      const confirmedPayload =
        options.confirm && (operation === "apply_change_set" || operation === "create_project_from_template")
          ? { ...payload, confirm: true }
          : payload;
      return {
        operation,
        operationKind: options.operationKind ?? inferOperationKind(operation),
        payload: confirmedPayload,
      };
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

async function runReadBundle(
  bridge: NamedPipeBridgeClient,
  sessionId: string,
  timeoutMs: number,
  args: Record<string, unknown>
): Promise<{ exitCode: number; body: unknown }> {
  const startedAt = Date.now();
  const aggregateRequest = makeRequest(sessionId, "read_bundle", "read", args, timeoutMs);
  const includeArgs = optionalRecord(args.include, "include");
  const include = {
    status: optionalBoolean(includeArgs.status, true, "include.status"),
    levels: optionalBoolean(includeArgs.levels, true, "include.levels"),
    readiness: optionalBoolean(includeArgs.readiness, true, "include.readiness"),
    currentView: optionalBoolean(includeArgs.currentView, true, "include.currentView"),
    currentViewElements: optionalBoolean(includeArgs.currentViewElements, true, "include.currentViewElements"),
    selection: optionalBoolean(includeArgs.selection, true, "include.selection"),
    modelContext: optionalBoolean(includeArgs.modelContext, false, "include.modelContext"),
    warnings: optionalBoolean(includeArgs.warnings, false, "include.warnings"),
  };
  const continueOnError = optionalBoolean(args.continueOnError, true, "continueOnError");
  const includeSectionMetrics = optionalBoolean(args.includeSectionMetrics, false, "includeSectionMetrics");
  const sections: Record<string, unknown> = {};
  const returnedSections: string[] = [];
  const failedSections: Array<Record<string, unknown>> = [];
  const warnings: Array<{ code: string; message: string }> = [];
  const sectionMetrics: Record<string, unknown> = {};

  const record = (section: string, response: BridgeResponse<unknown>, returnSection = true): BridgeResponse<unknown> | null => {
    for (const warning of response.warnings ?? []) {
      warnings.push({
        code: `${section}.${warning.code}`,
        message: warning.message,
      });
    }
    if (includeSectionMetrics && response.metrics) sectionMetrics[section] = response.metrics;

    if (response.ok) {
      if (returnSection) {
        sections[section] = response.data;
        returnedSections.push(section);
      }
      return null;
    }

    const failure = {
      section,
      code: response.error?.code ?? "SECTION_FAILED",
      message: response.error?.message ?? `${section} failed.`,
      suggestedNextAction: response.error?.suggestedNextAction,
    };
    failedSections.push(failure);
    if (continueOnError) return null;
    return {
      ok: false,
      requestId: aggregateRequest.requestId,
      error: {
        code: "READ_BUNDLE_SECTION_FAILED",
        message: `${section} failed: ${failure.message}`,
        recoverable: true,
        details: { section: failure },
      },
      warnings,
      metrics: { elapsedMs: Date.now() - startedAt },
    };
  };

  const statusRequest = makeRequest(sessionId, "status", "read", {}, Math.min(timeoutMs, 10000));
  const statusResponse = await bridge.status(statusRequest);
  const statusFailure = record("status", statusResponse as BridgeResponse<unknown>, include.status);
  if (statusFailure) return { exitCode: 2, body: statusFailure };
  if (!statusResponse.ok) return { exitCode: 2, body: statusResponse };

  const documentFingerprint = optionalString(args.documentFingerprint, "documentFingerprint") ?? statusResponse.data.activeDocument?.fingerprint;
  const expectedGeneration =
    optionalNumber(args.expectedGeneration, "expectedGeneration") ?? statusResponse.data.activeDocument?.generation;
  const guard = compactObject({
    documentFingerprint,
    expectedGeneration,
  });

  if (include.levels) {
    const payload = compactObject({ documentFingerprint, expectedGeneration });
    const request = makeRequest(sessionId, "get_levels", "read", payload, timeoutMs);
    const failure = record("levels", await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  if (include.readiness) {
    const readiness = optionalRecord(args.readiness, "readiness");
    const payload = compactObject({
      ...guard,
      scenarios: readiness.scenarios,
      includeHints: optionalBoolean(readiness.includeHints, true, "readiness.includeHints"),
    });
    const request = makeRequest(sessionId, "get_model_readiness", "read", payload, timeoutMs);
    const failure = record("readiness", await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  if (include.currentView) {
    const currentView = optionalRecord(args.currentView, "currentView");
    const payload = compactObject({
      ...guard,
      includeCropBox: optionalBoolean(currentView.includeCropBox, false, "currentView.includeCropBox"),
    });
    const request = makeRequest(sessionId, "get_current_view", "read", payload, timeoutMs);
    const failure = record("currentView", await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  if (include.currentViewElements) {
    const currentViewElements = optionalRecord(args.currentViewElements, "currentViewElements");
    const payload = compactObject({
      ...guard,
      filter: optionalRecord(currentViewElements.filter, "currentViewElements.filter"),
      fields: currentViewElements.fields,
      preset: currentViewElements.preset ?? "summary",
      includeHidden: optionalBoolean(currentViewElements.includeHidden, false, "currentViewElements.includeHidden"),
      limit: currentViewElements.limit ?? 10,
      includeTotalCount: optionalBoolean(currentViewElements.includeTotalCount, false, "currentViewElements.includeTotalCount"),
    });
    const request = makeRequest(sessionId, "get_current_view_elements", "read", payload, timeoutMs);
    const failure = record("currentViewElements", await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  if (include.selection) {
    const selection = optionalRecord(args.selection, "selection");
    const selectionFilter = optionalRecord(selection.filter, "selection.filter");
    const payload = compactObject({
      ...guard,
      filter: { ...selectionFilter, selectionOnly: true },
      fields: selection.fields,
      preset: selection.preset ?? "summary",
      limit: selection.limit ?? 10,
      includeTotalCount: optionalBoolean(selection.includeTotalCount, false, "selection.includeTotalCount"),
    });
    const request = makeRequest(sessionId, "get_selection", "read", payload, timeoutMs);
    const failure = record("selection", await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  if (include.modelContext) {
    const modelContext = optionalRecord(args.modelContext, "modelContext");
    const payload = compactObject({
      ...guard,
      includeProjectInfo: optionalBoolean(modelContext.includeProjectInfo, true, "modelContext.includeProjectInfo"),
      includePhases: optionalBoolean(modelContext.includePhases, true, "modelContext.includePhases"),
      includeWorksets: optionalBoolean(modelContext.includeWorksets, true, "modelContext.includeWorksets"),
      includeDesignOptions: optionalBoolean(modelContext.includeDesignOptions, true, "modelContext.includeDesignOptions"),
      includeRevitLinks: optionalBoolean(modelContext.includeRevitLinks, true, "modelContext.includeRevitLinks"),
      phaseLimit: modelContext.phaseLimit ?? 10,
      worksetLimit: modelContext.worksetLimit ?? 10,
      designOptionLimit: modelContext.designOptionLimit ?? 10,
      revitLinkLimit: modelContext.revitLinkLimit ?? 10,
      includeTotalCount: optionalBoolean(modelContext.includeTotalCount, false, "modelContext.includeTotalCount"),
    });
    const request = makeRequest(sessionId, "get_model_context", "read", payload, timeoutMs);
    const failure = record("modelContext", await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  if (include.warnings) {
    const warningArgs = optionalRecord(args.warnings, "warnings");
    const payload = compactObject({
      ...guard,
      filter: optionalRecord(warningArgs.filter, "warnings.filter"),
      fields: warningArgs.fields,
      preset: warningArgs.preset ?? "summary",
      limit: warningArgs.limit ?? 10,
      includeTotalCount: optionalBoolean(warningArgs.includeTotalCount, false, "warnings.includeTotalCount"),
    });
    const request = makeRequest(sessionId, "get_warnings", "read", payload, timeoutMs);
    const failure = record("warnings", await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  for (const [index, catalog] of recordArray(args.catalogs, "catalogs", 8).entries()) {
    const key = sectionKey("catalog", index, optionalString(catalog.key, `catalogs[${index}].key`));
    const payload = compactObject({
      ...guard,
      kind: requiredString(catalog.kind, `catalogs[${index}].kind`),
      filter: optionalRecord(catalog.filter, `catalogs[${index}].filter`),
      preset: catalog.preset ?? "compact",
      fields: catalog.fields,
      limit: catalog.limit ?? 20,
      includeTotalCount: optionalBoolean(catalog.includeTotalCount, false, `catalogs[${index}].includeTotalCount`),
    });
    const request = makeRequest(sessionId, "catalog", "read", payload, timeoutMs);
    const failure = record(`catalogs.${key}`, await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  for (const [index, parameterRequest] of recordArray(args.parameters, "parameters", 4).entries()) {
    const key = sectionKey("parameters", index, optionalString(parameterRequest.key, `parameters[${index}].key`));
    const payload = compactObject({
      ...guard,
      filter: requiredRecord(parameterRequest.filter, `parameters[${index}].filter`),
      preset: parameterRequest.preset ?? "writableEdit",
      includeTypeParameters: parameterRequest.includeTypeParameters,
      includeReadOnly: parameterRequest.includeReadOnly,
      includeValues: parameterRequest.includeValues,
      nameContains: parameterRequest.nameContains,
      limit: parameterRequest.limit,
      parameterLimit: parameterRequest.parameterLimit,
      includeTotalCount: optionalBoolean(parameterRequest.includeTotalCount, false, `parameters[${index}].includeTotalCount`),
    });
    const request = makeRequest(sessionId, "describe_parameters", "read", payload, timeoutMs);
    const failure = record(`parameters.${key}`, await callBridge(bridge, request));
    if (failure) return { exitCode: 2, body: failure };
  }

  const data = compactObject({
    documentFingerprint,
    generation: expectedGeneration,
    returnedSections,
    failedSections,
    sections,
    sectionMetrics: includeSectionMetrics ? sectionMetrics : undefined,
    source: "revitctl-composed",
  });
  const response: BridgeResponse<typeof data> = {
    ok: true,
    requestId: aggregateRequest.requestId,
    data,
    warnings,
    metrics: {
      elapsedMs: Date.now() - startedAt,
      returnedCount: returnedSections.length,
      totalCount: returnedSections.length + failedSections.length,
    },
    generation: typeof expectedGeneration === "number" ? expectedGeneration : undefined,
  };
  return { exitCode: 0, body: response };
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
    case "create_project_from_template":
      return bridge.createProjectFromTemplate(request as unknown as Parameters<NamedPipeBridgeClient["createProjectFromTemplate"]>[0]);
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
    case "get_model_context":
      return bridge.getModelContext(request as Parameters<NamedPipeBridgeClient["getModelContext"]>[0]);
    case "get_material_quantities":
      return bridge.getMaterialQuantities(request as Parameters<NamedPipeBridgeClient["getMaterialQuantities"]>[0]);
    case "get_warnings":
      return bridge.getWarnings(request as Parameters<NamedPipeBridgeClient["getWarnings"]>[0]);
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
      return bridge.raw(request);
  }
}

function inferOperationKind(operation: string): OperationKind {
  if (operation === "preview_change_set") return "preview";
  if (operation === "apply_change_set") return "write";
  if (operation === "create_project_from_template") return "write";
  if (operation === "cancel_request") return "debug";
  if (READ_OPERATIONS.has(operation)) return "read";
  return "debug";
}

function payloadObject(value: unknown): Record<string, unknown> {
  if (value === undefined || value === null) return {};
  if (typeof value === "object" && !Array.isArray(value)) return value as Record<string, unknown>;
  throw new RevitCtlUsageError("Payload must be a JSON object.");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function optionalRecord(value: unknown, name: string): Record<string, unknown> {
  if (value === undefined || value === null) return {};
  if (isRecord(value)) return value;
  throw new RevitCtlUsageError(`${name} must be a JSON object.`);
}

function requiredRecord(value: unknown, name: string): Record<string, unknown> {
  if (isRecord(value)) return value;
  throw new RevitCtlUsageError(`${name} must be a JSON object.`);
}

function recordArray(value: unknown, name: string, max: number): Record<string, unknown>[] {
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value)) throw new RevitCtlUsageError(`${name} must be a JSON array.`);
  if (value.length > max) throw new RevitCtlUsageError(`${name} can include at most ${max} item(s).`);
  return value.map((item, index) => requiredRecord(item, `${name}[${index}]`));
}

function optionalBoolean(value: unknown, defaultValue: boolean, name: string): boolean {
  if (value === undefined || value === null) return defaultValue;
  if (typeof value === "boolean") return value;
  throw new RevitCtlUsageError(`${name} must be a boolean.`);
}

function optionalString(value: unknown, name: string): string | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value === "string") return value;
  throw new RevitCtlUsageError(`${name} must be a string.`);
}

function requiredString(value: unknown, name: string): string {
  const text = optionalString(value, name);
  if (!text) throw new RevitCtlUsageError(`${name} is required.`);
  return text;
}

function optionalNumber(value: unknown, name: string): number | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value === "number" && Number.isFinite(value)) return value;
  throw new RevitCtlUsageError(`${name} must be a number.`);
}

function compactObject<T extends Record<string, unknown>>(value: T): T {
  const compact: Record<string, unknown> = {};
  for (const [key, entry] of Object.entries(value)) {
    if (entry !== undefined) compact[key] = entry;
  }
  return compact as T;
}

function sectionKey(prefix: string, index: number, key?: string): string {
  const trimmed = key?.trim();
  if (!trimmed) return `${prefix}${index + 1}`;
  return trimmed.replace(/[^A-Za-z0-9_.-]+/g, "_").slice(0, 80) || `${prefix}${index + 1}`;
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

function parseOperationKind(value: string, option: string): OperationKind {
  switch (value) {
    case "read":
    case "preview":
    case "write":
    case "destructive":
    case "debug":
      return value;
    default:
      throw new RevitCtlUsageError(`${option} must be one of: read, preview, write, destructive, debug.`);
  }
}

function readPositionalJson(value: string | undefined, command: string): unknown {
  if (!value) throw new RevitCtlUsageError(`Usage: revitctl ${command} <json-file>`);
  return readJsonArgument(value);
}

function readJsonArgument(value: string): unknown {
  const trimmed = stripUtf8Bom(value).trim();
  if (trimmed === "-") {
    return parseJsonText(readFileSync(0, "utf8"));
  }
  if (trimmed.startsWith("{")) {
    return parseJsonText(trimmed);
  }
  return readJsonFile(trimmed);
}

function readJsonFile<T = unknown>(filePath: string): T {
  return parseJsonText(readFileSync(path.resolve(filePath), "utf8"));
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
  revitctl read-bundle [--payload <json-or-path>]
  revitctl readiness [--payload <json-or-path>]
  revitctl model-context [--payload <json-or-path>]
  revitctl warnings [--payload <json-or-path>]
  revitctl list-documents
  revitctl create-project --payload <json-or-path> --confirm
  revitctl levels [--payload <json-or-path>]
  revitctl views [--payload <json-or-path>]
  revitctl sheets [--payload <json-or-path>]
  revitctl current-view [--payload <json-or-path>]
  revitctl current-view-elements [--payload <json-or-path>]
  revitctl selection [--payload <json-or-path>]
  revitctl analyze [--payload <json-or-path>]
  revitctl materials [--payload <json-or-path>]
  revitctl rooms [--payload <json-or-path>]
  revitctl query --payload <json-or-path>
  revitctl catalog --payload <json-or-path>
  revitctl parameters --payload <json-or-path>
  revitctl preview <change-set.json>
  revitctl apply <apply-payload.json> --confirm
  revitctl cancel [--payload <json-or-path>]
  revitctl call <operation> --payload <json-or-path> [--operation-kind <kind>]

Options:
  --discovery <path>      client-discovery.json path
  --auth-config <path>    auth.env path; token value is never printed
  --install-root <path>   installed Revit MCP Next root
  --pipe <name>           named pipe; default revit-mcp-next
  --timeout-ms <n>        bridge timeout in milliseconds
  --operation-kind <kind> override revitctl call kind; read, preview, write, destructive, or debug
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
