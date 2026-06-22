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

test("fake bridge returns bounded catalog result shape with compatibility paging", async () => {
  const bridge = new FakeRevitBridgeClient();
  const firstPage = await bridge.catalog(
    makeRequest(
      "test",
      "catalog",
      "read",
      {
        kind: "elementTypes",
        filter: { forElementId: "501" },
        preset: "typeChange",
        limit: 1,
        includeTotalCount: true,
      },
      30000
    )
  );

  assert.equal(firstPage.ok, true);
  if (!firstPage.ok) return;
  assert.equal(firstPage.data.kind, "elementTypes");
  assert.equal(firstPage.data.target?.elementId, "501");
  assert.equal(firstPage.data.target?.currentTypeId, "9001");
  assert.equal(firstPage.data.target?.canChangeType, true);
  assert.equal(firstPage.data.returnedCount, 1);
  assert.equal(firstPage.data.totalCount, 2);
  assert.equal(firstPage.data.truncated, true);
  assert.equal(firstPage.data.cursor, "1");
  assert.equal(firstPage.data.items[0]?.validForTarget, true);
  assert.equal(firstPage.data.items[0]?.isCurrentType, true);

  const secondPage = await bridge.catalog(
    makeRequest(
      "test",
      "catalog",
      "read",
      {
        kind: "elementTypes",
        filter: { forElementId: "501" },
        preset: "typeChange",
        limit: 1,
        cursor: firstPage.data.cursor,
        includeTotalCount: true,
      },
      30000
    )
  );

  assert.equal(secondPage.ok, true);
  if (!secondPage.ok) return;
  assert.equal(secondPage.data.returnedCount, 1);
  assert.equal(secondPage.data.truncated, false);
  assert.equal(secondPage.data.items[0]?.id, "9002");
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
  const start = {
    x: { value: 0, unit: "mm", system: "metric" as const },
    y: { value: 0, unit: "mm", system: "metric" as const },
    z: { value: 0, unit: "mm", system: "metric" as const },
  };
  const end = {
    x: { value: 5000, unit: "mm", system: "metric" as const },
    y: { value: 0, unit: "mm", system: "metric" as const },
    z: { value: 0, unit: "mm", system: "metric" as const },
  };
  const translation = {
    x: { value: 0, unit: "mm", system: "metric" as const },
    y: { value: 250, unit: "mm", system: "metric" as const },
    z: { value: 0, unit: "mm", system: "metric" as const },
  };
  const changeSet = {
    documentFingerprint: "sample-doc-fingerprint",
    expectedGeneration: 7,
    transactionName: "Update Mark Level Wall Move",
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
      {
        id: "op-3",
        type: "create_wall" as const,
        levelId: "311",
        start,
        end,
        wallTypeId: "9001",
        height: { value: 3000, unit: "mm", system: "metric" as const },
        structural: true,
        flip: false,
      },
      {
        id: "op-4",
        type: "move_element" as const,
        elementId: "501",
        translation,
      },
      {
        id: "op-5",
        type: "rotate_element" as const,
        elementId: "501",
        axisStart: start,
        axisEnd: {
          x: { value: 0, unit: "mm", system: "metric" as const },
          y: { value: 0, unit: "mm", system: "metric" as const },
          z: { value: 1, unit: "m", system: "metric" as const },
        },
        angle: { value: 90, unit: "degrees" as const },
      },
      {
        id: "op-6",
        type: "copy_element" as const,
        elementId: "501",
        translation: {
          x: { value: 1200, unit: "mm", system: "metric" as const },
          y: { value: 0, unit: "mm", system: "metric" as const },
          z: { value: 0, unit: "mm", system: "metric" as const },
        },
      },
      {
        id: "op-7",
        type: "change_element_type" as const,
        elementId: "501",
        typeId: "9002",
      },
      {
        id: "op-8",
        type: "set_element_pinned" as const,
        elementId: "501",
        pinned: true,
        expectedPinned: false,
      },
      {
        id: "op-9",
        type: "create_grid" as const,
        name: "A",
        start,
        end,
      },
      {
        id: "op-10",
        type: "create_floor" as const,
        levelId: "311",
        floorTypeId: "9100",
        structural: false,
        outline: [
          start,
          end,
          {
            x: { value: 5000, unit: "mm", system: "metric" as const },
            y: { value: 3000, unit: "mm", system: "metric" as const },
            z: { value: 0, unit: "mm", system: "metric" as const },
          },
          {
            x: { value: 0, unit: "mm", system: "metric" as const },
            y: { value: 3000, unit: "mm", system: "metric" as const },
            z: { value: 0, unit: "mm", system: "metric" as const },
          },
        ],
      },
    ],
  } satisfies ChangeSetRequest;

  const preview = await bridge.previewChange(makeRequest("session", "preview_change_set", "preview", changeSet, 30000));
  assert.equal(preview.ok, true);
  if (!preview.ok) return;
  assert.equal(preview.data.ready, true);
  assert.equal(preview.data.operationCount, 10);
  assert.equal(preview.data.riskLevel, "medium");
  assert.equal(preview.data.documentFingerprint, "sample-doc-fingerprint");
  assert.equal(preview.data.baseGeneration, 7);
  assert.match(preview.data.changeSetHash ?? "", /^sha256:/);
  assert.equal(preview.data.expiresAt, "2099-01-01T00:00:00.000Z");
  assert.equal(preview.data.changes[0]?.status, "ready");
  assert.deepEqual(preview.data.changes[2]?.target, {
    document: "Sample.rvt",
    levelId: "311",
    wallTypeId: "9001",
  });
  assert.deepEqual(preview.data.changes[2]?.after?.start, start);
  assert.deepEqual(preview.data.changes[2]?.after?.end, end);
  assert.deepEqual(preview.data.changes[3]?.target, {
    elementId: "501",
  });
  assert.deepEqual(preview.data.changes[3]?.after, {
    translation,
  });
  assert.equal(preview.data.changes[4]?.type, "rotate_element");
  assert.deepEqual(preview.data.changes[4]?.after?.angle, { value: 90, unit: "degrees" });
  assert.deepEqual(preview.data.changes[5]?.target, {
    sourceElementId: "501",
  });
  assert.equal(preview.data.changes[6]?.type, "change_element_type");
  assert.deepEqual(preview.data.changes[6]?.target, {
    elementId: "501",
    typeId: "9002",
  });
  assert.equal(preview.data.changes[7]?.type, "set_element_pinned");
  assert.deepEqual(preview.data.changes[7]?.after, {
    pinned: true,
    expectedPinned: false,
  });
  assert.equal(preview.data.changes[8]?.type, "create_grid");
  assert.deepEqual(preview.data.changes[8]?.target, {
    document: "Sample.rvt",
    name: "A",
  });
  assert.equal(preview.data.changes[9]?.type, "create_floor");
  assert.deepEqual(preview.data.changes[9]?.target, {
    document: "Sample.rvt",
    levelId: "311",
    floorTypeId: "9100",
  });

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
  assert.equal(applied.data.changedCount, 10);
  assert.equal(applied.data.changeSetHash, preview.data.changeSetHash);
  assert.equal(applied.data.baseGeneration, preview.data.baseGeneration);
  assert.equal(applied.data.changes[2]?.type, "create_wall");
  assert.equal(applied.data.changes[3]?.type, "move_element");
  assert.equal(applied.data.changes[4]?.type, "rotate_element");
  assert.equal(applied.data.changes[5]?.type, "copy_element");
  assert.equal(applied.data.changes[6]?.type, "change_element_type");
  assert.equal(applied.data.changes[7]?.type, "set_element_pinned");
  assert.equal(applied.data.changes[8]?.type, "create_grid");
  assert.equal(applied.data.changes[9]?.type, "create_floor");
});
