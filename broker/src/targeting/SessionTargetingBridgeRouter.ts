import type {
  BridgeError,
  BridgeRequest,
  BridgeResponse,
  BridgeSuccess,
  RevitDocumentSummary,
  RevitInstanceSummary,
  RevitSessionTargetResult,
  RevitTarget,
} from "@revit-mcp-next/contracts";
import type { BridgeCallOptions, RevitBridgeClient } from "../ipc/RevitBridgeClient.js";
import { makeRequest } from "../ipc/RequestFactory.js";
import type {
  RevitInstanceConnection,
  RevitInstanceDirectory,
} from "../instances/RevitInstanceDirectory.js";
import { SessionTargetStore } from "./SessionTargetStore.js";

const DOCUMENT_SCOPED_OPERATIONS = new Set([
  "get_view_details",
  "get_dimensions",
  "activate_view",
  "get_levels",
  "get_views",
  "get_sheets",
  "get_schedules",
  "get_schedule_fields",
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
  "inspect_model_delivery",
  "preview_model_delivery",
  "execute_model_delivery",
  "preview_change_set",
  "apply_change_set",
]);
const DOCUMENT_GENERATION_ADVANCING_OPERATIONS = new Set(["apply_change_set"]);

type DocumentTargetSummary = RevitDocumentSummary & {
  instanceId: string;
  processId?: number;
  revitVersion?: string;
  revitBuild?: string;
};

type BridgeMethod = Exclude<keyof RevitBridgeClient, "dispose">;

export class SessionTargetingBridgeRouter {
  readonly bridge: RevitBridgeClient;
  private readonly deliveryJobTargets = new Map<string, RevitTarget>();

  constructor(
    private readonly directory: RevitInstanceDirectory,
    private readonly targets: SessionTargetStore,
    private readonly sessionId: string
  ) {
    this.bridge = new Proxy({} as RevitBridgeClient, {
      get: (_target, property) => {
        if (property === "dispose") return () => undefined;
        if (typeof property !== "string") return undefined;
        return (request: BridgeRequest, options?: BridgeCallOptions) =>
          this.invoke(property as BridgeMethod, request, options);
      },
    });
  }

  async listInstances(options?: BridgeCallOptions): Promise<BridgeResponse<RevitInstanceSummary[]>> {
    const requestId = targetRequestId("instances");
    const connections = await this.directory.listConnections();
    const instances = await Promise.all(connections.map((connection) => this.inspectInstance(connection, options)));
    return success(requestId, instances, instances.length, instances.length);
  }

  async setTarget(
    input: { instanceId: string; documentFingerprint: string; generation?: number },
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitSessionTargetResult>> {
    try {
      const target = await this.resolveExplicitTarget(input, options);
      const selected = this.targets.set({ ...target, selectionMode: "explicit" });
      return targetedSuccess(targetRequestId("set-target"), { selected: true, sessionId: this.sessionId, target: selected }, selected);
    } catch (error) {
      return routingFailure(targetRequestId("set-target"), error);
    }
  }

  async getTarget(options?: BridgeCallOptions): Promise<BridgeResponse<RevitSessionTargetResult>> {
    const selected = this.targets.get();
    if (!selected) {
      return success(targetRequestId("get-target"), { selected: false, sessionId: this.sessionId }, 0, 0);
    }

    try {
      const current = await this.resolveExplicitTarget(
        { instanceId: selected.instanceId, documentFingerprint: selected.documentFingerprint },
        options
      );
      if (current.generation !== selected.generation) {
        throw new RoutingError(
          "TARGET_GENERATION_CHANGED",
          `The selected document generation changed from ${selected.generation} to ${current.generation}. Re-select the target before writing.`
        );
      }
      return targetedSuccess(
        targetRequestId("get-target"),
        { selected: true, sessionId: this.sessionId, target: selected },
        selected
      );
    } catch (error) {
      return routingFailure(targetRequestId("get-target"), error, selected);
    }
  }

  clearTarget(): BridgeResponse<RevitSessionTargetResult> {
    const previous = this.targets.clear();
    return success(
      targetRequestId("clear-target"),
      { selected: false, sessionId: this.sessionId, target: previous },
      previous ? 1 : 0,
      previous ? 1 : 0
    );
  }

  async resolveTarget(
    input: { instanceId?: string; documentFingerprint?: string; generation?: number },
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitTarget>> {
    const request = makeRequest(this.sessionId, "query", "read", {
      instanceId: input.instanceId,
      documentFingerprint: input.documentFingerprint,
      expectedGeneration: input.generation,
    }, 10_000);
    request.instanceId = input.instanceId;
    request.documentFingerprint = input.documentFingerprint;
    request.expectedGeneration = input.generation;
    try {
      const resolved = await this.resolveOperationTarget(request, options);
      return targetedSuccess(targetRequestId("resolve-target"), resolved.target, resolved.target);
    } catch (error) {
      return routingFailure(targetRequestId("resolve-target"), error, this.targets.get());
    }
  }

  private async invoke<T>(
    methodName: BridgeMethod,
    request: BridgeRequest,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<T>> {
    try {
      if (methodName === "listDocuments") {
        return (await this.listDocuments(request, options)) as BridgeResponse<T>;
      }

      if (request.operation === "get_model_delivery_status" || request.operation === "cancel_model_delivery") {
        const jobId = recordString(request.payload, "jobId");
        const jobTarget = jobId ? this.deliveryJobTargets.get(jobId) : undefined;
        if (jobTarget) {
          const connection = await this.connectionFor(jobTarget.instanceId);
          const response = await invokeBridge<T>(connection, methodName, { ...request, instanceId: jobTarget.instanceId }, options);
          return this.attachTarget(response, jobTarget, false, false);
        }
      }

      if (DOCUMENT_SCOPED_OPERATIONS.has(request.operation)) {
        const resolved = await this.resolveOperationTarget(request, options);
        const prepared = prepareTargetedRequest(request, resolved.target);
        const response = await invokeBridge<T>(resolved.connection, methodName, prepared, options);
        if (response.ok && request.operation === "execute_model_delivery" && isRecord(response.data)) {
          const jobId = recordString(response.data, "jobId");
          if (jobId) this.deliveryJobTargets.set(jobId, resolved.target);
        }
        const mutationMayAdvanceGeneration = DOCUMENT_GENERATION_ADVANCING_OPERATIONS.has(request.operation);
        return this.attachTarget(
          response,
          resolved.target,
          mutationMayAdvanceGeneration,
          mutationMayAdvanceGeneration
        );
      }

      const selected = this.targets.get();
      const explicitInstanceId = request.instanceId ?? recordString(request.payload, "instanceId");
      if (request.operation === "status" && selected && (!explicitInstanceId || explicitInstanceId === selected.instanceId)) {
        const current = await this.resolveExplicitTarget(
          { instanceId: selected.instanceId, documentFingerprint: selected.documentFingerprint },
          options
        );
        const connection = await this.connectionFor(current.instanceId);
        const response = await invokeBridge<T>(connection, methodName, { ...request, instanceId: current.instanceId }, options);
        return this.attachTarget(response, current, true, false);
      }

      const connection = await this.resolveInstance(request, options);
      const response = await invokeBridge<T>(connection, methodName, { ...request, instanceId: connection.metadata.instanceId }, options);
      if (request.operation === "create_project_from_template" && response.ok) {
        const createdTarget = targetFromCreatedProject(response.data, connection);
        const selected = this.targets.set(createdTarget);
        return { ...response, target: selected, generation: selected.generation };
      }
      return selected?.instanceId === connection.metadata.instanceId
        ? this.attachTarget(response, selected, false, false)
        : response;
    } catch (error) {
      const errorTarget = error instanceof RoutingError ? error.target : undefined;
      return routingFailure<T>(request.requestId, error, errorTarget ?? this.targets.get());
    }
  }

  private async listDocuments(
    request: BridgeRequest,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<DocumentTargetSummary[]>> {
    const explicitInstanceId = request.instanceId ?? recordString(request.payload, "instanceId");
    try {
      const targets = await this.listDocumentTargets(options, explicitInstanceId);
      return success(request.requestId, targets, targets.length, targets.length);
    } catch (error) {
      return routingFailure(request.requestId, error);
    }
  }

  private async inspectInstance(
    connection: RevitInstanceConnection,
    options?: BridgeCallOptions
  ): Promise<RevitInstanceSummary> {
    const statusRequest = makeRequest(this.sessionId, "status", "read", {}, 5_000);
    statusRequest.instanceId = connection.metadata.instanceId;
    const status = await connection.bridge.status(statusRequest, options);
    if (!status.ok) {
      return {
        ...publicInstanceMetadata(connection),
        connected: false,
        documents: [],
        error: status.error,
      };
    }

    const listRequest = makeRequest(this.sessionId, "list_documents", "read", {}, 10_000);
    listRequest.instanceId = connection.metadata.instanceId;
    const documents = await connection.bridge.listDocuments(listRequest, options);
    const revit = status.data.revit;
    return {
      ...publicInstanceMetadata(connection),
      processId: connection.metadata.processId ?? revit?.processId,
      revitVersion: connection.metadata.revitVersion ?? revit?.version,
      revitBuild: connection.metadata.revitBuild ?? revit?.build,
      addinVersion: connection.metadata.addinVersion ?? status.data.addinVersion,
      connected: true,
      documents: documents.ok
        ? documents.data.map((document) => attachInstance(document, connection, status.data.revit))
        : [],
      error: documents.ok ? undefined : documents.error,
    };
  }

  private async resolveOperationTarget(
    request: BridgeRequest,
    options?: BridgeCallOptions
  ): Promise<{ connection: RevitInstanceConnection; target: RevitTarget; usesSessionTarget: boolean }> {
    const explicitInstanceId = request.instanceId ?? recordString(request.payload, "instanceId");
    const explicitFingerprint = request.documentFingerprint ?? recordString(request.payload, "documentFingerprint");
    const explicitGeneration =
      request.expectedGeneration ?? recordInteger(request.payload, "expectedGeneration") ?? recordInteger(request.payload, "baseGeneration");

    if (explicitInstanceId || explicitFingerprint) {
      const target = await this.resolveExplicitTarget(
        { instanceId: explicitInstanceId, documentFingerprint: explicitFingerprint, generation: explicitGeneration },
        options
      );
      return { connection: await this.connectionFor(target.instanceId), target, usesSessionTarget: false };
    }

    const selected = this.targets.get();
    if (selected) {
      const current = await this.resolveExplicitTarget(
        { instanceId: selected.instanceId, documentFingerprint: selected.documentFingerprint },
        options
      );
      if (current.generation !== selected.generation) {
        throw new RoutingError(
          "TARGET_GENERATION_CHANGED",
          `The selected document generation changed from ${selected.generation} to ${current.generation}. Call revit.list_documents and revit.set_target before continuing.`
        );
      }
      return {
        connection: await this.connectionFor(current.instanceId),
        target: { ...current, selectionMode: selected.selectionMode },
        usesSessionTarget: true,
      };
    }

    const openDocuments = await this.listDocumentTargets(options);
    if (openDocuments.length === 0) {
      throw new RoutingError("NO_OPEN_DOCUMENT", "No open Revit project document is available.");
    }
    if (openDocuments.length > 1) {
      throw new RoutingError(
        "TARGET_SELECTION_REQUIRED",
        "Multiple Revit documents are open. Call revit.list_instances or revit.list_documents, then revit.set_target with instanceId and documentFingerprint."
      );
    }

    const target = toTarget(openDocuments[0], "implicit-single-document");
    return { connection: await this.connectionFor(target.instanceId), target, usesSessionTarget: false };
  }

  private async resolveExplicitTarget(
    input: { instanceId?: string; documentFingerprint?: string; generation?: number },
    options?: BridgeCallOptions
  ): Promise<RevitTarget> {
    const documents = await this.listDocumentTargets(options, input.instanceId);
    const matches = documents.filter((document) =>
      input.documentFingerprint ? document.fingerprint === input.documentFingerprint : true
    );

    if (!input.documentFingerprint && matches.length > 1) {
      throw new RoutingError(
        "TARGET_DOCUMENT_AMBIGUOUS",
        "The selected Revit instance has multiple open documents. Provide an exact documentFingerprint; titles are not accepted."
      );
    }
    if (matches.length === 0) {
      throw new RoutingError(
        "TARGET_DOCUMENT_UNAVAILABLE",
        "The requested Revit instance/document is closed, changed, stale, or unavailable. Refresh revit.list_instances and select it again."
      );
    }
    if (matches.length > 1) {
      throw new RoutingError(
        "TARGET_DOCUMENT_AMBIGUOUS",
        "The supplied target matches more than one open document. Provide both instanceId and documentFingerprint."
      );
    }

    const target = toTarget(matches[0], "explicit");
    if (input.generation !== undefined && input.generation !== target.generation) {
      throw new RoutingError(
        "GENERATION_MISMATCH",
        `The target document generation is ${target.generation}, but ${input.generation} was requested.`,
        undefined,
        target
      );
    }
    return target;
  }

  private async listDocumentTargets(
    options?: BridgeCallOptions,
    instanceId?: string
  ): Promise<DocumentTargetSummary[]> {
    const connections = (await this.directory.listConnections()).filter(
      (connection) => !instanceId || connection.metadata.instanceId === instanceId
    );
    if (instanceId && connections.length === 0) {
      throw new RoutingError("TARGET_INSTANCE_UNAVAILABLE", `Revit instance ${instanceId} is not registered or is no longer live.`);
    }

    const results = await Promise.all(
      connections.map(async (connection) => {
        const request = makeRequest(this.sessionId, "list_documents", "read", {}, 10_000);
        request.instanceId = connection.metadata.instanceId;
        const response = await connection.bridge.listDocuments(request, options);
        return { connection, response };
      })
    );
    const successful = results.filter(
      (result): result is { connection: RevitInstanceConnection; response: Extract<BridgeResponse<RevitDocumentSummary[]>, { ok: true }> } =>
        result.response.ok
    );
    if (successful.length === 0 && results.length > 0) {
      const failure = results[0].response;
      if (!failure.ok) throw new RoutingError(failure.error.code, failure.error.message, failure.error.suggestedNextAction);
    }
    if (successful.length !== results.length) {
      throw new RoutingError(
        "TARGET_DISCOVERY_INCOMPLETE",
        "Some registered Revit instances could not list their documents. An incomplete inventory cannot establish a unique target.",
        "Call revit.list_instances and explicitly select a verified instance and document, or restore the unavailable instances."
      );
    }

    return successful.flatMap(({ connection, response }) =>
      response.data.map((document) => attachInstance(document, connection))
    );
  }

  private async resolveInstance(request: BridgeRequest, _options?: BridgeCallOptions): Promise<RevitInstanceConnection> {
    const explicitInstanceId = request.instanceId ?? recordString(request.payload, "instanceId");
    if (explicitInstanceId) return this.connectionFor(explicitInstanceId);

    const selected = this.targets.get();
    if (selected) return this.connectionFor(selected.instanceId);

    const connections = await this.directory.listConnections();
    if (connections.length === 1) return connections[0];
    if (connections.length === 0) throw new RoutingError("NO_REVIT_INSTANCE", "No live Revit instance is registered.");
    throw new RoutingError(
      "INSTANCE_SELECTION_REQUIRED",
      "Multiple Revit instances are open. Call revit.list_instances and revit.set_target before continuing."
    );
  }

  private async connectionFor(instanceId: string): Promise<RevitInstanceConnection> {
    const connections = await this.directory.listConnections();
    const connection = connections.find((candidate) => candidate.metadata.instanceId === instanceId);
    if (!connection) {
      throw new RoutingError("TARGET_INSTANCE_UNAVAILABLE", `Revit instance ${instanceId} is closed, stale, or unavailable.`);
    }
    return connection;
  }

  private attachTarget<T>(
    response: BridgeResponse<T>,
    target: RevitTarget,
    updateSessionTarget: boolean,
    useResponseGeneration = true
  ): BridgeResponse<T> {
    const generation = response.ok && useResponseGeneration
      ? response.generation ?? generationFromData(response.data) ?? target.generation
      : target.generation;
    const resolved = { ...target, generation };
    if (response.ok && updateSessionTarget) {
      this.targets.updateGeneration(resolved.instanceId, resolved.documentFingerprint, resolved.generation);
    }
    return { ...response, target: resolved };
  }
}

class RoutingError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly suggestedNextAction?: string,
    readonly target?: RevitTarget
  ) {
    super(message);
  }
}

async function invokeBridge<T>(
  connection: RevitInstanceConnection,
  methodName: BridgeMethod,
  request: BridgeRequest,
  options?: BridgeCallOptions
): Promise<BridgeResponse<T>> {
  const method = connection.bridge[methodName];
  if (typeof method !== "function") {
    throw new RoutingError("BRIDGE_METHOD_UNAVAILABLE", `The selected Revit bridge does not implement ${String(methodName)}.`);
  }
  return (method as unknown as (request: BridgeRequest, options?: BridgeCallOptions) => Promise<BridgeResponse<T>>).call(
    connection.bridge,
    request,
    options
  );
}

function prepareTargetedRequest<TPayload>(request: BridgeRequest<TPayload>, target: RevitTarget): BridgeRequest<TPayload> {
  const payload = isRecord(request.payload) ? { ...request.payload } : request.payload;
  if (isRecord(payload)) {
    const targetedPayload = payload as Record<string, unknown>;
    targetedPayload.instanceId = target.instanceId;
    targetedPayload.documentFingerprint = target.documentFingerprint;
    if (targetedPayload.expectedGeneration === undefined && targetedPayload.baseGeneration === undefined) {
      targetedPayload.expectedGeneration = target.generation;
    }
  }

  return {
    ...request,
    instanceId: target.instanceId,
    documentFingerprint: target.documentFingerprint,
    expectedGeneration: request.expectedGeneration ?? target.generation,
    payload,
  };
}

function attachInstance(
  document: RevitDocumentSummary,
  connection: RevitInstanceConnection,
  statusRevit?: { version: string; build?: string; processId?: number }
): DocumentTargetSummary {
  return {
    ...document,
    instanceId: connection.metadata.instanceId,
    processId: connection.metadata.processId ?? statusRevit?.processId,
    revitVersion: connection.metadata.revitVersion ?? statusRevit?.version,
    revitBuild: connection.metadata.revitBuild ?? statusRevit?.build,
  };
}

function toTarget(document: DocumentTargetSummary, selectionMode: RevitTarget["selectionMode"]): RevitTarget {
  return {
    instanceId: document.instanceId,
    processId: document.processId,
    revitVersion: document.revitVersion,
    documentFingerprint: document.fingerprint,
    documentTitle: document.title,
    documentPath: document.path,
    isWorkshared: document.isWorkshared,
    centralModelPath: document.centralModelPath,
    generation: document.generation,
    isUiActive: document.isActive,
    selectionMode,
  };
}

function targetFromCreatedProject(data: unknown, connection: RevitInstanceConnection): RevitTarget {
  if (!isRecord(data) || data.activated !== true || !isRecord(data.document) || !isRecord(data.activationConfirmation)) {
    throw new RoutingError(
      "TARGET_ACTIVATION_UNCONFIRMED",
      "Revit created a project but did not return a valid activation confirmation. No session target was changed. Call revit.list_documents and select the exact instance and documentFingerprint."
    );
  }

  const document = data.document;
  const confirmation = data.activationConfirmation;
  const fingerprint = recordString(document, "fingerprint");
  const path = recordString(document, "path");
  const generation = recordInteger(document, "generation");
  const confirmedFingerprint = recordString(confirmation, "documentFingerprint");
  const confirmedPath = recordString(confirmation, "documentPath");
  const centralModelPath = recordString(document, "centralModelPath");
  const confirmedCentralModelPath = recordString(confirmation, "centralModelPath");
  const confirmedInstance = recordString(confirmation, "instanceId");
  const confirmedGeneration = recordInteger(confirmation, "generation");
  if (
    confirmation.confirmed !== true ||
    confirmation.uiActive !== true ||
    document.isActive !== true ||
    !fingerprint ||
    generation === undefined ||
    !path ||
    fingerprint !== confirmedFingerprint ||
    normalizeWindowsPath(path) !== normalizeWindowsPath(confirmedPath) ||
    normalizeWindowsPath(centralModelPath) !== normalizeWindowsPath(confirmedCentralModelPath) ||
    generation !== confirmedGeneration ||
    confirmedInstance !== connection.metadata.instanceId
  ) {
    throw new RoutingError(
      "TARGET_ACTIVATION_UNCONFIRMED",
      "The created project identity did not match Revit's active UI document. No session target was changed. Call revit.list_documents and select the exact instance and documentFingerprint."
    );
  }

  return {
    instanceId: connection.metadata.instanceId,
    processId: connection.metadata.processId,
    revitVersion: connection.metadata.revitVersion,
    documentFingerprint: fingerprint,
    documentTitle: recordString(document, "title") ?? fingerprint,
    documentPath: path,
    isWorkshared: document.isWorkshared === true,
    centralModelPath,
    generation,
    isUiActive: true,
    selectionMode: "explicit",
  };
}

function normalizeWindowsPath(value: string | undefined): string | undefined {
  if (!value?.trim()) return undefined;
  return value.trim().replaceAll("/", "\\").replace(/\\+$/, "").toLocaleLowerCase("en-US");
}

function publicInstanceMetadata(connection: RevitInstanceConnection): Omit<RevitInstanceSummary, "connected" | "documents"> {
  const metadata = connection.metadata;
  return {
    instanceId: metadata.instanceId,
    processId: metadata.processId,
    revitVersion: metadata.revitVersion,
    revitBuild: metadata.revitBuild,
    addinVersion: metadata.addinVersion,
    startedAtUtc: metadata.startedAtUtc,
    lastSeenAtUtc: metadata.lastSeenAtUtc,
  };
}

function targetedSuccess<T>(requestId: string, data: T, target: RevitTarget): BridgeSuccess<T> {
  return { ...success(requestId, data, 1, 1), target, generation: target.generation };
}

function success<T>(requestId: string, data: T, returnedCount: number, totalCount: number): BridgeSuccess<T> {
  return {
    ok: true,
    requestId,
    data,
    warnings: [],
    metrics: { elapsedMs: 0, returnedCount, totalCount },
  };
}

function routingFailure<T>(requestId: string, error: unknown, target?: RevitTarget): BridgeResponse<T> {
  const resolved = error instanceof RoutingError
    ? error
    : new RoutingError("TARGET_ROUTING_FAILED", error instanceof Error ? error.message : String(error));
  return {
    ok: false,
    requestId,
    error: {
      code: resolved.code,
      message: resolved.message,
      recoverable: true,
      suggestedNextAction:
        resolved.suggestedNextAction ?? "Call revit.list_instances or revit.list_documents, then select an exact target with revit.set_target.",
    },
    warnings: [],
    metrics: { elapsedMs: 0 },
    target,
  };
}

function targetRequestId(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function generationFromData(data: unknown): number | undefined {
  if (!isRecord(data)) return undefined;
  const direct = data.generation;
  if (typeof direct === "number" && Number.isInteger(direct) && direct >= 0) return direct;
  const document = data.document;
  if (!isRecord(document)) return undefined;
  return recordInteger(document, "generation");
}

function recordString(value: unknown, key: string): string | undefined {
  return isRecord(value) && typeof value[key] === "string" ? (value[key] as string) : undefined;
}

function recordInteger(value: unknown, key: string): number | undefined {
  if (!isRecord(value)) return undefined;
  const child = value[key];
  return typeof child === "number" && Number.isInteger(child) && child >= 0 ? child : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
