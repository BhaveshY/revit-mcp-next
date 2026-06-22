import test from "node:test";
import assert from "node:assert/strict";
import { PROTOCOL_VERSION, type ChangeApplyRequest, type ChangeSetRequest } from "@revit-mcp-next/contracts";
import { FakeRevitBridgeClient } from "../src/ipc/FakeRevitBridgeClient.js";
import { makeRequest } from "../src/ipc/RequestFactory.js";

test("fake bridge returns bounded query result shape", async () => {
  const bridge = new FakeRevitBridgeClient();
  const response = await bridge.query(
    makeRequest(
      "test",
      "query",
      "read",
      {
        filter: { categories: ["OST_Walls"] },
        preset: "summary",
        limit: 50,
        includeTotalCount: true,
      },
      30000
    )
  );

  assert.equal(response.ok, true);
  if (!response.ok) return;
  assert.equal(response.data.returnedCount, 1);
  assert.equal(response.data.totalCount, 1);
  assert.equal(response.data.truncated, false);
  assert.equal(response.data.items[0]?.id, "501");
});

test("request builder stamps current protocol and operation kind", () => {
  const request = makeRequest("session", "status", "read", {}, 5000);
  assert.equal(request.protocolVersion, PROTOCOL_VERSION);
  assert.equal(request.operation, "status");
  assert.equal(request.operationKind, "read");
  assert.equal(request.timeoutMs, 5000);
  assert.equal(request.authToken, undefined);
});

test("fake bridge previews and applies a bounded change set", async () => {
  const bridge = new FakeRevitBridgeClient();
  const changeSet = {
    documentFingerprint: "sample-doc-fingerprint",
    expectedGeneration: 7,
    transactionName: "Update Mark And Level",
    operations: [
      {
        id: "op-1",
        type: "set_parameter" as const,
        elementId: "501",
        parameterName: "Mark",
        value: "A-101",
      },
      {
        id: "op-2",
        type: "create_level" as const,
        name: "Level 3",
        elevation: { value: 7000, unit: "mm", system: "metric" as const },
      },
    ],
  } satisfies ChangeSetRequest;

  const preview = await bridge.previewChange(makeRequest("session", "preview_change_set", "preview", changeSet, 30000));
  assert.equal(preview.ok, true);
  if (!preview.ok) return;
  assert.equal(preview.data.ready, true);
  assert.equal(preview.data.operationCount, 2);
  assert.equal(preview.data.riskLevel, "medium");
  assert.equal(preview.data.documentFingerprint, "sample-doc-fingerprint");
  assert.equal(preview.data.baseGeneration, 7);
  assert.match(preview.data.changeSetHash ?? "", /^sha256:/);
  assert.equal(preview.data.expiresAt, "2099-01-01T00:00:00.000Z");
  assert.equal(preview.data.changes[0]?.status, "ready");

  const applyPayload = {
    ...changeSet,
    previewId: preview.data.previewId,
    confirm: true,
    changeSetHash: preview.data.changeSetHash,
    baseGeneration: preview.data.baseGeneration,
    expiresAt: preview.data.expiresAt,
  } satisfies ChangeApplyRequest;

  const applied = await bridge.applyChange(
    makeRequest("session", "apply_change_set", "write", applyPayload, 60000)
  );
  assert.equal(applied.ok, true);
  if (!applied.ok) return;
  assert.equal(applied.data.applied, true);
  assert.equal(applied.data.changedCount, 2);
  assert.equal(applied.data.changeSetHash, preview.data.changeSetHash);
  assert.equal(applied.data.baseGeneration, preview.data.baseGeneration);
});
