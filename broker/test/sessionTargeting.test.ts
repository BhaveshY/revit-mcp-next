import test from "node:test";
import assert from "node:assert/strict";
import type {
  BridgeRequest,
  BridgeResponse,
  BridgeSuccess,
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
