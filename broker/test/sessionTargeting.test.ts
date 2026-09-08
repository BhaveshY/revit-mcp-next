import test from "node:test";
import assert from "node:assert/strict";
import type {
  ChangeApplyRequest,
  ChangeApplyResult,
  BridgeRequest,
  BridgeResponse,
  BridgeSuccess,
  CreateProjectFromTemplateRequest,
  CreateProjectFromTemplateResult,
  ModelDeliveryExecuteRequest,
  ModelDeliveryPreviewRequest,
  ModelDeliveryRecipe,
  QueryRequest,
  QueryResult,
  RevitDocumentSummary,
} from "@revit-mcp-next/contracts";
import { BRIDGE_PROTOCOL_VERSION } from "@revit-mcp-next/contracts";
import { FakeRevitBridgeClient } from "../src/ipc/FakeRevitBridgeClient.js";
import type { BridgeCallOptions } from "../src/ipc/RevitBridgeClient.js";
import type {
  RevitInstanceConnection,
  RevitInstanceDirectory,
} from "../src/instances/RevitInstanceDirectory.js";
import { SessionTargetStore } from "../src/targeting/SessionTargetStore.js";
import { SessionTargetingBridgeRouter } from "../src/targeting/SessionTargetingBridgeRouter.js";

class TargetTestBridge extends FakeRevitBridgeClient {
  readonly requests: BridgeRequest[] = [];

  constructor(public documents: RevitDocumentSummary[]) {
    super();
  }

  override async listDocuments(
    request: BridgeRequest<{ instanceId?: string }>,
    _options?: BridgeCallOptions
  ): Promise<BridgeResponse<RevitDocumentSummary[]>> {
    return success(request, this.documents.map((document) => ({ ...document })));
  }

  override async query(
    request: BridgeRequest<QueryRequest>,
    _options?: BridgeCallOptions
  ): Promise<BridgeResponse<QueryResult>> {
    this.requests.push(structuredClone(request));
    const fingerprint = request.documentFingerprint ?? request.payload.documentFingerprint;
    const document = this.documents.find((candidate) => candidate.fingerprint === fingerprint);
    if (!document) return failure(request, "TARGET_DOCUMENT_UNAVAILABLE", "The targeted document is no longer open.");
    const expected = request.expectedGeneration ?? request.payload.expectedGeneration;
    if (expected !== undefined && expected !== document.generation) {
      return failure(request, "GENERATION_MISMATCH", "The targeted document generation changed.");
    }
    const response = success(request, {
        items: [],
        returnedCount: 0,
        totalCount: 0,
        limit: request.payload.limit ?? 50,
        truncated: false,
        fields: request.payload.fields ?? [],
        units: {},
        scope: "document",
        source: "target-test",
      });
    return { ...response, generation: document.generation };
  }

  override async applyChange(request: BridgeRequest<ChangeApplyRequest>): Promise<BridgeResponse<ChangeApplyResult>> {
    const response = await super.applyChange(request);
    const document = this.documents.find((item) => item.fingerprint === request.documentFingerprint);
    if (response.ok && response.data.applied && document) {
      document.generation += 1;
      return { ...response, generation: document.generation };
    }
    return response;
  }
}

class ProjectCreationBridge extends TargetTestBridge {
  constructor(
    documents: RevitDocumentSummary[],
    private readonly confirmActivation = true,
    private readonly confirmedCentralModelPath?: string
  ) {
    super(documents);
  }

  override async createProjectFromTemplate(
    request: BridgeRequest<CreateProjectFromTemplateRequest>,
    options?: BridgeCallOptions
  ): Promise<BridgeResponse<CreateProjectFromTemplateResult>> {
    const response = await super.createProjectFromTemplate(request, options);
    if (!response.ok) return response;
    if (!this.confirmActivation) {
      return success(request, {
        ...response.data,
        activated: false,
      } as CreateProjectFromTemplateResult);
    }
    if (this.confirmedCentralModelPath !== undefined) {
      response.data.activationConfirmation.centralModelPath = this.confirmedCentralModelPath;
    }
    this.documents = [response.data.document];
    return response;
  }
}

class TargetTestDirectory implements RevitInstanceDirectory {
  constructor(readonly connections: RevitInstanceConnection[]) {}
  async listConnections(): Promise<RevitInstanceConnection[]> {
    return this.connections;
  }
  dispose(): void {}
}

const project1 = document("Project.rvt", "C:\\Projects\\One\\Project.rvt", "fingerprint-one", 4, true);
const project2 = document("Project.rvt", "C:\\Projects\\Two\\Project.rvt", "fingerprint-two", 9, false);

test("session target switches safely between two projects and injects exact guards", async () => {
  const bridge = new TargetTestBridge([project1, project2]);
  const router = routerFor("session-a", [connection("instance-2024", 2401, "2024", bridge)]);

  const blocked = await router.bridge.query(queryRequest("session-a"));
  assert.equal(blocked.ok, false);
  if (!blocked.ok) assert.equal(blocked.error.code, "TARGET_SELECTION_REQUIRED");
  assert.equal(bridge.requests.length, 0, "an ambiguous request must not reach Revit");

  const first = await router.setTarget({
    instanceId: "instance-2024",
    documentFingerprint: project1.fingerprint,
    generation: project1.generation,
  });
  assert.equal(first.ok, true);

  const firstQuery = await router.bridge.query(queryRequest("session-a", "write"));
  assert.equal(firstQuery.ok, true);
  assert.equal(firstQuery.target?.documentFingerprint, project1.fingerprint);
  assert.equal(bridge.requests.at(-1)?.instanceId, "instance-2024");
  assert.equal(bridge.requests.at(-1)?.documentFingerprint, project1.fingerprint);
  assert.equal(bridge.requests.at(-1)?.expectedGeneration, project1.generation);
  assert.equal((bridge.requests.at(-1)?.payload as QueryRequest).documentFingerprint, project1.fingerprint);

  const second = await router.setTarget({
    instanceId: "instance-2024",
    documentFingerprint: project2.fingerprint,
    generation: project2.generation,
  });
  assert.equal(second.ok, true);
  const secondQuery = await router.bridge.query(queryRequest("session-a", "write"));
  assert.equal(secondQuery.ok, true);
  assert.equal(secondQuery.target?.documentFingerprint, project2.fingerprint);
  assert.equal(bridge.requests.at(-1)?.documentFingerprint, project2.fingerprint);
  assert.equal(bridge.requests.at(-1)?.expectedGeneration, project2.generation);

  const status = await router.bridge.status({
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: "status-after-switch",
    sessionId: "session-a",
    operation: "status",
    operationKind: "read",
    timeoutMs: 1000,
    payload: {},
  });
  assert.equal(status.target?.generation, project2.generation, "active-tab status must not overwrite the selected target generation");
  assert.equal((await router.getTarget()).target?.generation, project2.generation);
});

test("two MCP sessions keep independent targets across two Revit processes", async () => {
  const bridge2024 = new TargetTestBridge([project1]);
  const bridge2027 = new TargetTestBridge([project2]);
  const connections = [
    connection("instance-2024", 2401, "2024", bridge2024),
    connection("instance-2027", 2701, "2027", bridge2027),
  ];
  const sessionA = routerFor("session-a", connections);
  const sessionB = routerFor("session-b", connections);

  const instances = await sessionA.listInstances();
  assert.equal(instances.ok, true);
  if (instances.ok) {
    assert.deepEqual(instances.data.map((item) => item.instanceId), ["instance-2024", "instance-2027"]);
    assert.deepEqual(instances.data.map((item) => item.revitVersion), ["2024", "2027"]);
  }

  assert.equal((await sessionA.setTarget({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint })).ok, true);
  assert.equal((await sessionB.setTarget({ instanceId: "instance-2027", documentFingerprint: project2.fingerprint })).ok, true);
  assert.equal((await sessionA.getTarget()).target?.instanceId, "instance-2024");
  assert.equal((await sessionB.getTarget()).target?.instanceId, "instance-2027");

  await sessionA.bridge.query(queryRequest("session-a"));
  await sessionB.bridge.query(queryRequest("session-b"));
  assert.equal(bridge2024.requests.length, 1);
  assert.equal(bridge2027.requests.length, 1);
});

test("single open document is implicit, but closing a selected target fails explicitly", async () => {
  const bridge = new TargetTestBridge([project1]);
  const router = routerFor("session-a", [connection("instance-2024", 2401, "2024", bridge)]);

  const implicit = await router.bridge.query(queryRequest("session-a"));
  assert.equal(implicit.ok, true);
  assert.equal(implicit.target?.selectionMode, "implicit-single-document");

  assert.equal((await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint })).ok, true);
  bridge.documents = [];
  const unavailable = await router.getTarget();
  assert.equal(unavailable.ok, false);
  if (!unavailable.ok) assert.equal(unavailable.error.code, "TARGET_DOCUMENT_UNAVAILABLE");

  const readAfterClose = await router.bridge.query(queryRequest("session-a"));
  assert.equal(readAfterClose.ok, false);
  if (!readAfterClose.ok) assert.equal(readAfterClose.error.code, "TARGET_DOCUMENT_UNAVAILABLE");
  assert.equal(readAfterClose.target?.documentFingerprint, project1.fingerprint);

  const statusAfterClose = await router.bridge.status({
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: "status-after-close",
    sessionId: "session-a",
    operation: "status",
    operationKind: "read",
    timeoutMs: 1000,
    payload: {},
  });
  assert.equal(statusAfterClose.ok, false);
  if (!statusAfterClose.ok) assert.equal(statusAfterClose.error.code, "TARGET_DOCUMENT_UNAVAILABLE");
  assert.equal(statusAfterClose.target?.documentFingerprint, project1.fingerprint);
});

test("partial instance discovery cannot select an implicit target or return a complete document list", async () => {
  const healthy = new TargetTestBridge([project1]);
  const unavailable = new TargetTestBridge([project2]);
  unavailable.listDocuments = async (request) => failure(request, "TIMEOUT", "Instance unavailable");
  const router = routerFor("partial", [
    connection("instance-2024", 2401, "2024", healthy),
    connection("instance-2027", 2701, "2027", unavailable),
  ]);
  const implicit = await router.bridge.query(queryRequest("partial"));
  assert.equal(implicit.ok, false);
  if (!implicit.ok) assert.equal(implicit.error.code, "TARGET_DISCOVERY_INCOMPLETE");
  assert.equal(healthy.requests.length, 0, "no document operation may be dispatched on incomplete discovery");
  const listing = await router.bridge.listDocuments({ ...queryRequest("partial"), operation: "list_documents", payload: {} });
  assert.equal(listing.ok, false);
  if (!listing.ok) assert.equal(listing.error.code, "TARGET_DISCOVERY_INCOMPLETE");
  const selected = await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint });
  assert.equal(selected.ok, true, "explicit healthy target must remain usable");
  assert.equal((await router.bridge.query(queryRequest("partial"))).ok, true);
  assert.equal(healthy.requests.length, 1);
});

test("duplicate project titles remain distinguishable only by instance and fingerprint", async () => {
  const bridge = new TargetTestBridge([project1, project2]);
  const router = routerFor("session-a", [connection("instance-2024", 2401, "2024", bridge)]);
  const documents = await router.bridge.listDocuments({
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: "list-documents",
    sessionId: "session-a",
    operation: "list_documents",
    operationKind: "read",
    timeoutMs: 1000,
    payload: {},
  });
  assert.equal(documents.ok, true);
  if (documents.ok) {
    assert.equal(documents.data[0].title, documents.data[1].title);
    assert.notEqual(documents.data[0].fingerprint, documents.data[1].fingerprint);
    assert.equal((documents.data[0] as RevitDocumentSummary & { instanceId: string }).instanceId, "instance-2024");
  }
});

test("created project becomes the session target only after exact activation confirmation", async () => {
  const bridge = new ProjectCreationBridge([project1]);
  const router = routerFor("session-create", [connection("instance-2024", 2401, "2024", bridge)]);
  const created = await router.bridge.createProjectFromTemplate({
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: "create-project",
    sessionId: "session-create",
    operation: "create_project_from_template",
    operationKind: "write",
    timeoutMs: 1000,
    payload: {
      templatePath: "C:\\Templates\\Pilot.rte",
      outputPath: "C:\\Pilot\\Created.rvt",
      confirm: true,
    },
  });
  assert.equal(created.ok, true);
  if (!created.ok) return;
  assert.equal(created.data.activated, true);
  assert.equal(created.target?.documentFingerprint, created.data.document.fingerprint);
  assert.equal(created.target?.documentPath, created.data.outputPath);
  assert.equal((await router.getTarget()).target?.documentFingerprint, created.data.document.fingerprint);

  const unconfirmedBridge = new ProjectCreationBridge([project1], false);
  const unconfirmedRouter = routerFor("session-unconfirmed", [connection("instance-2024", 2401, "2024", unconfirmedBridge)]);
  const rejected = await unconfirmedRouter.bridge.createProjectFromTemplate({
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: "create-project-unconfirmed",
    sessionId: "session-unconfirmed",
    operation: "create_project_from_template",
    operationKind: "write",
    timeoutMs: 1000,
    payload: { templatePath: "C:\\Templates\\Pilot.rte", outputPath: "C:\\Pilot\\Rejected.rvt", confirm: true },
  });
  assert.equal(rejected.ok, false);
  if (!rejected.ok) assert.equal(rejected.error.code, "TARGET_ACTIVATION_UNCONFIRMED");
  assert.equal((await unconfirmedRouter.getTarget()).ok, true);
  assert.equal((await unconfirmedRouter.getTarget()).target, undefined);

  const mismatchedBridge = new ProjectCreationBridge([project1], true, "C:\\Central\\Wrong.rvt");
  const mismatchedRouter = routerFor("session-central-mismatch", [connection("instance-2024", 2401, "2024", mismatchedBridge)]);
  const mismatched = await mismatchedRouter.bridge.createProjectFromTemplate({
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: "create-project-central-mismatch",
    sessionId: "session-central-mismatch",
    operation: "create_project_from_template",
    operationKind: "write",
    timeoutMs: 1000,
    payload: { templatePath: "C:\\Templates\\Pilot.rte", outputPath: "C:\\Pilot\\Mismatch.rvt", confirm: true },
  });
  assert.equal(mismatched.ok, false);
  if (!mismatched.ok) assert.equal(mismatched.error.code, "TARGET_ACTIVATION_UNCONFIRMED");
});

test("model delivery preview is bound to session target and rejected after switch, close, or restart", async () => {
  const bridge = new TargetTestBridge([project1, project2]);
  const router = routerFor("delivery-session", [connection("instance-2024", 2401, "2024", bridge)]);
  await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint });
  const previewRequest = deliveryPreviewRequest("delivery-session");
  const preview = await router.bridge.previewModelDelivery(previewRequest);
  assert.equal(preview.ok, true);
  if (!preview.ok) return;
  assert.equal(preview.target?.documentFingerprint, project1.fingerprint);
  assert.equal(preview.data.targetBinding.documentFingerprint, project1.fingerprint);

  await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project2.fingerprint });
  const switched = await router.bridge.executeModelDelivery(deliveryExecuteRequest("delivery-session", previewRequest.payload, preview.data));
  assert.equal(switched.ok, false);
  if (!switched.ok) assert.equal(switched.error.code, "DELIVERY_TARGET_CHANGED");

  await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint });
  bridge.documents = [{ ...project1, generation: project1.generation + 1 }, project2];
  const stale = await router.bridge.executeModelDelivery(deliveryExecuteRequest("delivery-session", previewRequest.payload, preview.data));
  assert.equal(stale.ok, false);
  if (!stale.ok) assert.equal(stale.error.code, "TARGET_GENERATION_CHANGED");

  bridge.documents = [project1, project2];
  await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint });
  bridge.documents = [project2];
  const closed = await router.bridge.executeModelDelivery(deliveryExecuteRequest("delivery-session", previewRequest.payload, preview.data));
  assert.equal(closed.ok, false);
  if (!closed.ok) assert.equal(closed.error.code, "TARGET_DOCUMENT_UNAVAILABLE");

  const restartedBridge = new TargetTestBridge([project1]);
  const restarted = routerFor("delivery-session", [connection("instance-2024-restarted", 2402, "2024", restartedBridge)]);
  await restarted.setTarget({ instanceId: "instance-2024-restarted", documentFingerprint: project1.fingerprint });
  const afterRestart = await restarted.bridge.executeModelDelivery(deliveryExecuteRequest("delivery-session", previewRequest.payload, preview.data));
  assert.equal(afterRestart.ok, false);
  if (!afterRestart.ok) assert.equal(afterRestart.error.code, "DELIVERY_PREVIEW_NOT_FOUND");
});

test("model delivery job status remains routed to its original target after the session switches", async () => {
  const bridge = new TargetTestBridge([project1, project2]);
  const router = routerFor("delivery-job-session", [connection("instance-2024", 2401, "2024", bridge)]);
  await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint });
  const previewRequest = deliveryPreviewRequest("delivery-job-session");
  const preview = await router.bridge.previewModelDelivery(previewRequest);
  assert.equal(preview.ok, true);
  if (!preview.ok) return;
  const execution = await router.bridge.executeModelDelivery(
    deliveryExecuteRequest("delivery-job-session", previewRequest.payload, preview.data)
  );
  assert.equal(execution.ok, true);
  if (!execution.ok) return;

  await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project2.fingerprint });
  const statusRequest = {
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: "delivery-status-after-switch",
    sessionId: "delivery-job-session",
    operation: "get_model_delivery_status" as const,
    operationKind: "read" as const,
    timeoutMs: 1000,
    payload: { jobId: execution.data.jobId },
  };
  const firstStatus = await router.bridge.getModelDeliveryStatus(statusRequest);
  const repeatedStatus = await router.bridge.getModelDeliveryStatus({ ...statusRequest, requestId: "delivery-status-repeated" });
  assert.equal(firstStatus.ok, true);
  assert.equal(repeatedStatus.ok, true);
  assert.equal(firstStatus.target?.documentFingerprint, project1.fingerprint);
  assert.equal(repeatedStatus.target?.documentFingerprint, project1.fingerprint);
  assert.equal((await router.getTarget()).target?.documentFingerprint, project2.fingerprint);
});

test("explicit apply guards advance only the matching selected target", async () => {
  const bridge = new TargetTestBridge([{ ...project1 }, { ...project2 }]);
  const router = routerFor("apply-session", [connection("instance-2024", 2401, "2024", bridge)]);
  await router.setTarget({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint });
  const apply = (fingerprint: string): BridgeRequest<ChangeApplyRequest> => ({
    ...queryRequest("apply-session"), operation: "apply_change_set", operationKind: "write",
    payload: { documentFingerprint: fingerprint, transactionName: "Test", operations: [], previewId: "preview", confirm: true },
  });
  const applied = await router.bridge.applyChange(apply(project1.fingerprint));
  assert.equal(applied.ok, true);
  assert.equal((await router.getTarget()).target?.generation, project1.generation + 1);
  assert.equal((await router.bridge.query(queryRequest("apply-session"))).ok, true);
  assert.equal((await router.bridge.applyChange(apply(project2.fingerprint))).ok, true);
  assert.equal((await router.getTarget()).target?.documentFingerprint, project1.fingerprint);
  assert.equal((await router.getTarget()).target?.generation, project1.generation + 1);
});

test("out-of-order mutation responses cannot regress selected generation", () => {
  const store = new SessionTargetStore("ordered");
  store.set({ instanceId: "instance-2024", documentFingerprint: project1.fingerprint, documentTitle: project1.title, generation: 4, selectionMode: "explicit" });
  store.updateGeneration("instance-2024", project1.fingerprint, 6);
  store.updateGeneration("instance-2024", project1.fingerprint, 5);
  store.updateGeneration("different-instance", project1.fingerprint, 99);
  assert.equal(store.get()?.generation, 6);
});

function routerFor(sessionId: string, connections: RevitInstanceConnection[]): SessionTargetingBridgeRouter {
  return new SessionTargetingBridgeRouter(
    new TargetTestDirectory(connections),
    new SessionTargetStore(sessionId),
    sessionId
  );
}

function connection(
  instanceId: string,
  processId: number,
  revitVersion: string,
  bridge: TargetTestBridge
): RevitInstanceConnection {
  return {
    metadata: {
      schemaVersion: 1,
      instanceId,
      pipeName: `revit-mcp-next-${instanceId}`,
      controlPipeName: `revit-mcp-next-${instanceId}-control`,
      processId,
      revitVersion,
      revitBuild: `${revitVersion}.test`,
    },
    bridge,
    source: "test",
  };
}

function document(
  title: string,
  path: string,
  fingerprint: string,
  generation: number,
  isActive: boolean
): RevitDocumentSummary {
  return {
    documentId: fingerprint,
    title,
    path,
    fingerprint,
    isActive,
    isWorkshared: false,
    isModified: false,
    generation,
  };
}

function queryRequest(sessionId: string, operationKind: "read" | "write" = "read"): BridgeRequest<QueryRequest> {
  return {
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: `query-${sessionId}-${operationKind}-${Date.now()}`,
    sessionId,
    operation: "query",
    operationKind,
    timeoutMs: 1000,
    payload: { filter: {}, limit: 10 },
  };
}

function deliveryPreviewRequest(sessionId: string): BridgeRequest<ModelDeliveryPreviewRequest> {
  return {
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: `delivery-preview-${sessionId}`,
    sessionId,
    operation: "preview_model_delivery",
    operationKind: "preview",
    timeoutMs: 1000,
    payload: { recipe: deliveryRecipe() },
  };
}

function deliveryExecuteRequest(
  sessionId: string,
  previewPayload: ModelDeliveryPreviewRequest,
  preview: { previewId: string; planHash: string; expiresAt: string }
): BridgeRequest<ModelDeliveryExecuteRequest> {
  return {
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    requestId: `delivery-execute-${sessionId}-${Date.now()}`,
    sessionId,
    operation: "execute_model_delivery",
    operationKind: "destructive",
    timeoutMs: 1000,
    payload: { ...previewPayload, ...preview, confirm: true },
  };
}

function deliveryRecipe(): ModelDeliveryRecipe {
  return {
    projectId: "pilot-project",
    recipeVersion: "1",
    deliveryId: "pilot-delivery",
    packageName: "Pilot-Issue",
    destinationRoot: "C:\\Pilot\\Delivery",
    sourceModels: [{ id: "architecture", sourcePath: "C:\\Pilot\\Architecture.rvt", targetFileName: "Architecture-Issue.rvt" }],
    linkRules: [],
    coordinates: { preserveLinkTransforms: true, packagedLinkPathType: "relative" },
    cleanup: {
      deleteSheets: false,
      deleteViews: false,
      deleteSchedules: false,
      deleteLegends: false,
      deleteDraftingViews: false,
      deleteViewTemplates: false,
      deleteUnusedFilters: false,
      removeUnmappedLinks: false,
      purgeUnusedPasses: 0,
      protectedViewNames: [],
    },
    exports: [],
    qa: {
      requireStandalone: true,
      requireNoCentralPath: true,
      requireSourceHashUnchanged: true,
      requireCleanupMatchesPreview: true,
      requireAllRequiredExports: true,
    },
  };
}

function success<T>(request: BridgeRequest, data: T): BridgeSuccess<T> {
  return {
    ok: true,
    requestId: request.requestId,
    data,
    warnings: [],
    metrics: { elapsedMs: 0 },
  };
}

function failure<T>(request: BridgeRequest, code: string, message: string): BridgeResponse<T> {
  return {
    ok: false,
    requestId: request.requestId,
    error: { code, message, recoverable: true },
    warnings: [],
    metrics: { elapsedMs: 0 },
  };
}
