import test from "node:test";
import assert from "node:assert/strict";
import {
  PROTOCOL_VERSION,
  type ChangeApplyRequest,
  type ChangeSetRequest,
  type ModelReadinessRequest,
} from "@revit-mcp-next/contracts";
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

test("fake bridge query honors cursor and field projection", async () => {
  const bridge = new FakeRevitBridgeClient();
  const firstPage = await bridge.query(
    makeRequest(
      "test",
      "query",
      "read",
      {
        filter: { elementIds: ["501"] },
        fields: ["id"],
        limit: 1,
        includeTotalCount: true,
      },
      30000
    )
  );

  assert.equal(firstPage.ok, true);
  if (!firstPage.ok) return;
  assert.equal(firstPage.data.returnedCount, 1);
  assert.equal(firstPage.data.totalCount, 1);
  assert.equal(firstPage.data.items[0]?.id, "501");
  assert.equal(firstPage.data.items[0]?.uniqueId, undefined);

  const secondPage = await bridge.query(
    makeRequest(
      "test",
      "query",
      "read",
      {
        filter: { elementIds: ["501"] },
        fields: ["id"],
        limit: 1,
        cursor: "1",
        includeTotalCount: true,
      },
      30000
    )
  );

  assert.equal(secondPage.ok, true);
  if (!secondPage.ok) return;
  assert.equal(secondPage.data.returnedCount, 0);
  assert.equal(secondPage.data.truncated, false);
});

test("fake bridge query geometrySummary returns compact location and bounds", async () => {
  const bridge = new FakeRevitBridgeClient();
  const response = await bridge.query(
    makeRequest(
      "test",
      "query",
      "read",
      {
        filter: { elementIds: ["501"] },
        preset: "geometrySummary",
        limit: 1,
      },
      30000
    )
  );

  assert.equal(response.ok, true);
  if (!response.ok) return;
  assert.deepEqual(response.data.fields, ["id", "uniqueId", "category", "class", "name", "typeId", "levelId", "location", "bounds"]);
  assert.equal(response.data.units.location, "mm");
  assert.equal(response.data.units.bounds, "mm");
  assert.equal(response.data.items[0]?.id, "501");
  assert.equal(response.data.items[0]?.location?.start?.x.value, 0);
  assert.equal(response.data.items[0]?.location?.end?.x.value, 4000);
  assert.equal(response.data.items[0]?.bounds?.max?.z.value, 3000);
});

test("fake bridge describeParameters honors compact presets", async () => {
  const bridge = new FakeRevitBridgeClient();

  const compact = await bridge.describeParameters(
    makeRequest("test", "describe_parameters", "read", { filter: { elementIds: ["501"] } }, 30000)
  );
  assert.equal(compact.ok, true);
  if (!compact.ok) return;
  assert.equal(compact.data.preset, "writableEdit");
  assert.equal(compact.data.limit, 10);
  assert.equal(compact.data.parameterLimit, 40);
  assert.equal(compact.data.items[0]?.parameters.length, 1);
  assert.equal(compact.data.items[0]?.parameters[0]?.name, "Mark");
  assert.equal(compact.data.items[0]?.parameters[0]?.source, "instance");
  assert.equal(compact.data.items[0]?.parameters[0]?.value, undefined);
  assert.equal(compact.data.items[0]?.parameters[0]?.valueString, undefined);

  const full = await bridge.describeParameters(
    makeRequest("test", "describe_parameters", "read", { filter: { elementIds: ["501"] }, preset: "full" }, 30000)
  );
  assert.equal(full.ok, true);
  if (!full.ok) return;
  assert.equal(full.data.preset, "full");
  assert.equal(full.data.limit, 20);
  assert.equal(full.data.parameterLimit, 80);
  assert.ok(full.data.items[0]?.parameters.some((parameter) => parameter.source === "type"));
  assert.ok(full.data.items[0]?.parameters.some((parameter) => parameter.value !== undefined));

  const namesOnly = await bridge.describeParameters(
    makeRequest(
      "test",
      "describe_parameters",
      "read",
      { filter: { elementIds: ["501"] }, preset: "namesOnly", nameContains: "Type" },
      30000
    )
  );
  assert.equal(namesOnly.ok, true);
  if (!namesOnly.ok) return;
  assert.equal(namesOnly.data.preset, "namesOnly");
  assert.equal(namesOnly.data.parameterLimit, 120);
  assert.equal(namesOnly.data.items[0]?.parameters.length, 1);
  assert.equal(namesOnly.data.items[0]?.parameters[0]?.name, "Type Name");
  assert.equal(namesOnly.data.items[0]?.parameters[0]?.source, "type");
  assert.equal(namesOnly.data.items[0]?.parameters[0]?.value, undefined);
});

test("fake bridge returns read and analysis parity result shapes", async () => {
  const bridge = new FakeRevitBridgeClient();

  const currentView = await bridge.getCurrentView(
    makeRequest("test", "get_current_view", "read", { includeCropBox: false }, 10000)
  );
  assert.equal(currentView.ok, true);
  if (!currentView.ok) return;
  assert.equal(currentView.data.document.fingerprint, "sample-doc-fingerprint");
  assert.equal(currentView.data.view.id, "1024");
  assert.equal(currentView.data.view.uniqueId, "view-1024");

  const viewElements = await bridge.getCurrentViewElements(
    makeRequest(
      "test",
      "get_current_view_elements",
      "read",
      { filter: { categories: ["OST_Walls"] }, preset: "geometrySummary", limit: 1, includeTotalCount: true },
      30000
    )
  );
  assert.equal(viewElements.ok, true);
  if (!viewElements.ok) return;
  assert.equal(viewElements.data.scope, "activeView");
  assert.equal(viewElements.data.returnedCount, 1);
  assert.equal(viewElements.data.totalCount, 1);
  assert.equal(viewElements.data.items[0]?.id, "501");
  assert.equal(viewElements.data.units.location, "mm");
  assert.equal(viewElements.data.units.bounds, "mm");
  assert.equal(viewElements.data.items[0]?.location?.length?.value, 4000);
  assert.equal(viewElements.data.items[0]?.bounds?.max?.z.value, 3000);

  const selection = await bridge.getSelection(
    makeRequest("test", "get_selection", "read", { filter: { selectionOnly: true }, limit: 1 }, 30000)
  );
  assert.equal(selection.ok, true);
  if (!selection.ok) return;
  assert.equal(selection.data.scope, "selection");
  assert.equal(selection.data.selection?.available, true);
  assert.equal(selection.data.selection?.count, 2);

  const model = await bridge.analyzeModel(
    makeRequest("test", "analyze_model", "read", { bucketLimit: 10 }, 60000)
  );
  assert.equal(model.ok, true);
  if (!model.ok) return;
  assert.equal(model.data.totals.elements, 42);
  assert.equal(model.data.byCategory?.[0]?.key, "OST_Walls");

  const readiness = await bridge.getModelReadiness(
    makeRequest(
      "test",
      "get_model_readiness",
      "read",
      { scenarios: ["levels", "familyPlacement", "annotations"], includeHints: true } satisfies ModelReadinessRequest,
      30000
    )
  );
  assert.equal(readiness.ok, true);
  if (!readiness.ok) return;
  assert.equal(readiness.data.readyCount, 3);
  assert.equal(readiness.data.totalCount, 3);
  assert.deepEqual(
    readiness.data.scenarios.map((scenario) => scenario.name),
    ["levels", "familyPlacement", "annotations"]
  );
  const familyPlacement = readiness.data.scenarios.find((scenario) => scenario.name === "familyPlacement");
  assert.equal(familyPlacement?.ready, true);
  assert.equal(familyPlacement?.hints?.hostedFamilySymbolId, "9200");
  assert.equal(familyPlacement?.hints?.levelBasedFamilySymbolId, "9201");

  const materials = await bridge.getMaterialQuantities(
    makeRequest(
      "test",
      "get_material_quantities",
      "read",
      { filter: {}, limit: 1, includeTotalCount: true },
      60000
    )
  );
  assert.equal(materials.ok, true);
  if (!materials.ok) return;
  assert.equal(materials.data.returnedCount, 1);
  assert.equal(materials.data.totalCount, 1);
  assert.equal(materials.data.items[0]?.materialId, "7001");
  assert.equal(materials.data.units.area, "m2");
  assert.equal(materials.data.units.volume, "m3");

  const rooms = await bridge.getRooms(
    makeRequest(
      "test",
      "get_rooms",
      "read",
      { filter: { levelIds: ["311"], numberContains: "101" }, preset: "schedule", includeTotalCount: true },
      30000
    )
  );
  assert.equal(rooms.ok, true);
  if (!rooms.ok) return;
  assert.equal(rooms.data.returnedCount, 1);
  assert.equal(rooms.data.totalCount, 1);
  assert.equal(rooms.data.items[0]?.id, "601");
  assert.equal(rooms.data.items[0]?.number, "101");
  assert.equal(rooms.data.units.area, "m2");
  assert.equal(rooms.data.units.location, "mm");
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

  const roomTags = await bridge.catalog(
    makeRequest(
      "test",
      "catalog",
      "read",
      {
        kind: "tagTypes",
        filter: { categories: ["OST_RoomTags"] },
        preset: "annotation",
        includeTotalCount: true,
      },
      30000
    )
  );

  assert.equal(roomTags.ok, true);
  if (!roomTags.ok) return;
  assert.equal(roomTags.data.kind, "tagTypes");
  assert.equal(roomTags.data.returnedCount, 1);
  assert.equal(roomTags.data.totalCount, 1);
  assert.equal(roomTags.data.items[0]?.builtInCategory, "OST_RoomTags");
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
  const roomLocation = {
    x: { value: 2500, unit: "mm", system: "metric" as const },
    y: { value: 1200, unit: "mm", system: "metric" as const },
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
        expectedUniqueId: "wall-501",
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
        type: "place_family_instance" as const,
        familySymbolId: "9200",
        hostElementId: "501",
        expectedHostUniqueId: "wall-501",
        levelId: "311",
        location: {
          x: { value: 1200, unit: "mm", system: "metric" as const },
          y: { value: 0, unit: "mm", system: "metric" as const },
          z: { value: 0, unit: "mm", system: "metric" as const },
        },
        flipFacing: true,
      },
      {
        id: "op-5",
        type: "place_family_instance" as const,
        familySymbolId: "9201",
        levelId: "311",
        location: {
          x: { value: 1600, unit: "mm", system: "metric" as const },
          y: { value: 900, unit: "mm", system: "metric" as const },
          z: { value: 0, unit: "mm", system: "metric" as const },
        },
        rotation: { value: 90, unit: "degrees" as const },
      },
      {
        id: "op-6",
        type: "create_sheet" as const,
        sheetNumber: "A-201",
        name: "Preview Sheet",
        titleBlockTypeId: "9300",
      },
      {
        id: "op-7",
        type: "place_view_on_sheet" as const,
        sheetId: "1101",
        viewId: "1025",
        center: {
          x: { value: 250, unit: "mm", system: "metric" as const },
          y: { value: 180, unit: "mm", system: "metric" as const },
        },
      },
      {
        id: "op-8",
        type: "create_text_note" as const,
        viewId: "1024",
        text: "MCP generated note",
        position: {
          x: { value: 500, unit: "mm", system: "metric" as const },
          y: { value: 500, unit: "mm", system: "metric" as const },
          z: { value: 0, unit: "mm", system: "metric" as const },
        },
        textNoteTypeId: "9400",
        width: { value: 1200, unit: "mm", system: "metric" as const },
        rotation: { value: 0, unit: "degrees" as const },
      },
      {
        id: "op-9",
        type: "move_element" as const,
        elementId: "501",
        expectedUniqueId: "wall-501",
        translation,
      },
      {
        id: "op-10",
        type: "rotate_element" as const,
        elementId: "501",
        expectedUniqueId: "wall-501",
        axisStart: start,
        axisEnd: {
          x: { value: 0, unit: "mm", system: "metric" as const },
          y: { value: 0, unit: "mm", system: "metric" as const },
          z: { value: 1, unit: "m", system: "metric" as const },
        },
        angle: { value: 90, unit: "degrees" as const },
      },
      {
        id: "op-11",
        type: "copy_element" as const,
        elementId: "501",
        expectedUniqueId: "wall-501",
        translation: {
          x: { value: 1200, unit: "mm", system: "metric" as const },
          y: { value: 0, unit: "mm", system: "metric" as const },
          z: { value: 0, unit: "mm", system: "metric" as const },
        },
      },
      {
        id: "op-12",
        type: "change_element_type" as const,
        elementId: "501",
        expectedUniqueId: "wall-501",
        typeId: "9002",
      },
      {
        id: "op-13",
        type: "set_element_pinned" as const,
        elementId: "501",
        expectedUniqueId: "wall-501",
        pinned: true,
        expectedPinned: false,
      },
      {
        id: "op-14",
        type: "create_grid" as const,
        name: "A",
        start,
        end,
      },
      {
        id: "op-15",
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
      {
        id: "op-16",
        type: "create_room" as const,
        levelId: "311",
        location: roomLocation,
        name: "Conference",
        number: "101",
        department: "Operations",
      },
      {
        id: "op-17",
        type: "tag_room" as const,
        roomId: "601",
        expectedUniqueId: "room-601",
        viewId: "1024",
        location: roomLocation,
        tagTypeId: "9700",
        hasLeader: false,
        orientation: "Horizontal" as const,
      },
      {
        id: "op-18",
        type: "tag_element" as const,
        elementId: "501",
        expectedUniqueId: "wall-501",
        viewId: "1024",
        tagTypeId: "9701",
        position: {
          x: { value: 2200, unit: "mm", system: "metric" as const },
          y: { value: 450, unit: "mm", system: "metric" as const },
          z: { value: 0, unit: "mm", system: "metric" as const },
        },
        hasLeader: true,
        orientation: "Horizontal" as const,
      },
      {
        id: "op-19",
        type: "delete_element" as const,
        elementId: "501",
        expectedUniqueId: "wall-501",
        expectedPinned: false,
        allowDependentDeletes: true,
        expectedDeletedElementIds: ["501", "801"],
        expectedDeletedCount: 2,
        dependentDeleteLimit: 10,
      },
    ],
  } satisfies ChangeSetRequest;

  const preview = await bridge.previewChange(makeRequest("session", "preview_change_set", "preview", changeSet, 30000));
  assert.equal(preview.ok, true);
  if (!preview.ok) return;
  assert.equal(preview.data.ready, true);
  assert.equal(preview.data.operationCount, 19);
  assert.equal(preview.data.riskLevel, "high");
  assert.equal(preview.data.documentFingerprint, "sample-doc-fingerprint");
  assert.equal(preview.data.baseGeneration, 7);
  assert.match(preview.data.changeSetHash, /^sha256:/);
  assert.equal(preview.data.expiresAt, "2099-01-01T00:00:00.000Z");
  assert.equal(preview.data.changes[0]?.status, "ready");
  assert.deepEqual(preview.data.changes[2]?.target, {
    document: "Sample.rvt",
    levelId: "311",
    wallTypeId: "9001",
  });
  assert.deepEqual(preview.data.changes[2]?.after?.start, start);
  assert.deepEqual(preview.data.changes[2]?.after?.end, end);
  assert.equal(preview.data.changes[3]?.type, "place_family_instance");
  assert.deepEqual(preview.data.changes[3]?.target, {
    document: "Sample.rvt",
    familySymbolId: "9200",
    hostElementId: "501",
    hostUniqueId: "wall-501",
    levelId: "311",
  });
  assert.deepEqual(preview.data.changes[3]?.after?.location, {
    x: { value: 1200, unit: "mm", system: "metric" },
    y: { value: 0, unit: "mm", system: "metric" },
    z: { value: 0, unit: "mm", system: "metric" },
  });
  assert.equal(preview.data.changes[3]?.after?.flipFacing, true);
  assert.equal(preview.data.changes[4]?.type, "place_family_instance");
  assert.deepEqual(preview.data.changes[4]?.target, {
    document: "Sample.rvt",
    familySymbolId: "9201",
    hostElementId: undefined,
    levelId: "311",
  });
  assert.deepEqual(preview.data.changes[4]?.after?.rotation, { value: 90, unit: "degrees" });
  assert.equal(preview.data.changes[5]?.type, "create_sheet");
  assert.deepEqual(preview.data.changes[5]?.target, {
    document: "Sample.rvt",
    sheetNumber: "A-201",
    titleBlockTypeId: "9300",
  });
  assert.equal(preview.data.changes[5]?.after?.sheetNumber, "A-201");
  assert.equal(preview.data.changes[6]?.type, "place_view_on_sheet");
  assert.deepEqual(preview.data.changes[6]?.target, {
    sheetId: "1101",
    viewId: "1025",
  });
  assert.deepEqual(preview.data.changes[6]?.after?.center, {
    x: { value: 250, unit: "mm", system: "metric" },
    y: { value: 180, unit: "mm", system: "metric" },
  });
  assert.equal(preview.data.changes[7]?.type, "create_text_note");
  assert.deepEqual(preview.data.changes[7]?.target, {
    document: "Sample.rvt",
    viewId: "1024",
    textNoteTypeId: "9400",
  });
  assert.equal(preview.data.changes[7]?.after?.text, "MCP generated note");
  assert.deepEqual(preview.data.changes[8]?.target, {
    elementId: "501",
    uniqueId: "wall-501",
  });
  assert.deepEqual(preview.data.changes[8]?.after, {
    translation,
  });
  assert.equal(preview.data.changes[9]?.type, "rotate_element");
  assert.deepEqual(preview.data.changes[9]?.after?.angle, { value: 90, unit: "degrees" });
  assert.deepEqual(preview.data.changes[10]?.target, {
    sourceElementId: "501",
    sourceUniqueId: "wall-501",
  });
  assert.equal(preview.data.changes[11]?.type, "change_element_type");
  assert.deepEqual(preview.data.changes[11]?.target, {
    elementId: "501",
    uniqueId: "wall-501",
    typeId: "9002",
  });
  assert.equal(preview.data.changes[12]?.type, "set_element_pinned");
  assert.deepEqual(preview.data.changes[12]?.after, {
    pinned: true,
    expectedPinned: false,
  });
  assert.equal(preview.data.changes[13]?.type, "create_grid");
  assert.deepEqual(preview.data.changes[13]?.target, {
    document: "Sample.rvt",
    name: "A",
  });
  assert.equal(preview.data.changes[14]?.type, "create_floor");
  assert.deepEqual(preview.data.changes[14]?.target, {
    document: "Sample.rvt",
    levelId: "311",
    floorTypeId: "9100",
  });
  assert.equal(preview.data.changes[15]?.type, "create_room");
  assert.deepEqual(preview.data.changes[15]?.target, {
    document: "Sample.rvt",
    levelId: "311",
  });
  assert.deepEqual(preview.data.changes[15]?.after?.location, roomLocation);
  assert.equal(preview.data.changes[16]?.type, "tag_room");
  assert.deepEqual(preview.data.changes[16]?.target, {
    document: "Sample.rvt",
    roomId: "601",
    uniqueId: "room-601",
    viewId: "1024",
    tagTypeId: "9700",
  });
  assert.deepEqual(preview.data.changes[16]?.after?.location, roomLocation);
  assert.equal(preview.data.changes[17]?.type, "tag_element");
  assert.deepEqual(preview.data.changes[17]?.target, {
    document: "Sample.rvt",
    elementId: "501",
    uniqueId: "wall-501",
    viewId: "1024",
    tagTypeId: "9701",
  });
  assert.equal(preview.data.changes[17]?.after?.hasLeader, true);
  assert.equal(preview.data.changes[18]?.type, "delete_element");
  assert.deepEqual(preview.data.changes[18]?.after?.deletedElementIds, ["501", "801"]);
  assert.equal(preview.data.changes[18]?.after?.dependentDeletedCount, 1);

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
  assert.equal(applied.data.changedCount, 19);
  assert.equal(applied.data.changeSetHash, preview.data.changeSetHash);
  assert.equal(applied.data.baseGeneration, preview.data.baseGeneration);
  assert.equal(applied.data.changes[2]?.type, "create_wall");
  assert.equal(applied.data.changes[3]?.type, "place_family_instance");
  assert.equal(applied.data.changes[4]?.type, "place_family_instance");
  assert.equal(applied.data.changes[5]?.type, "create_sheet");
  assert.equal(applied.data.changes[6]?.type, "place_view_on_sheet");
  assert.equal(applied.data.changes[7]?.type, "create_text_note");
  assert.equal(applied.data.changes[8]?.type, "move_element");
  assert.equal(applied.data.changes[9]?.type, "rotate_element");
  assert.equal(applied.data.changes[10]?.type, "copy_element");
  assert.equal(applied.data.changes[11]?.type, "change_element_type");
  assert.equal(applied.data.changes[12]?.type, "set_element_pinned");
  assert.equal(applied.data.changes[13]?.type, "create_grid");
  assert.equal(applied.data.changes[14]?.type, "create_floor");
  assert.equal(applied.data.changes[15]?.type, "create_room");
  assert.equal(applied.data.changes[16]?.type, "tag_room");
  assert.equal(applied.data.changes[17]?.type, "tag_element");
  assert.equal(applied.data.changes[18]?.type, "delete_element");

  const cancel = await bridge.cancel(
    makeRequest("session", "cancel_request", "debug", { requestId: "fake-request-id", reason: "test cleanup" }, 5000)
  );
  assert.equal(cancel.ok, true);
  if (!cancel.ok) return;
  assert.equal(cancel.data.cancelled, false);
  assert.equal(cancel.data.requestId, "fake-request-id");
  assert.match(cancel.data.message, /No queued fake request/);
});

test("fake bridge blocks mismatched write identity guards", async () => {
  const bridge = new FakeRevitBridgeClient();
  const point = {
    x: { value: 0, unit: "mm", system: "metric" as const },
    y: { value: 0, unit: "mm", system: "metric" as const },
    z: { value: 0, unit: "mm", system: "metric" as const },
  };
  const roomPoint = {
    x: { value: 0, unit: "mm", system: "metric" as const },
    y: { value: 0, unit: "mm", system: "metric" as const },
  };
  const operations = [
    {
      type: "set_parameter",
      elementId: "501",
      expectedUniqueId: "not-wall-501",
      parameterName: "Mark",
      value: "A-101",
    },
    {
      type: "tag_room",
      roomId: "601",
      expectedUniqueId: "not-room-601",
      viewId: "1024",
      location: roomPoint,
    },
    {
      type: "tag_element",
      elementId: "501",
      expectedUniqueId: "not-wall-501",
      viewId: "1024",
      tagTypeId: "9701",
      position: point,
    },
    {
      type: "move_element",
      elementId: "501",
      expectedUniqueId: "not-wall-501",
      translation: point,
    },
    {
      type: "rotate_element",
      elementId: "501",
      expectedUniqueId: "not-wall-501",
      axisStart: point,
      axisEnd: { ...point, z: { value: 1, unit: "m", system: "metric" as const } },
      angle: { value: 90, unit: "degrees" as const },
    },
    {
      type: "copy_element",
      elementId: "501",
      expectedUniqueId: "not-wall-501",
      translation: point,
    },
    {
      type: "change_element_type",
      elementId: "501",
      expectedUniqueId: "not-wall-501",
      typeId: "9002",
    },
    {
      type: "set_element_pinned",
      elementId: "501",
      expectedUniqueId: "not-wall-501",
      pinned: true,
    },
    {
      type: "delete_element",
      elementId: "501",
      expectedUniqueId: "not-wall-501",
    },
    {
      type: "place_family_instance",
      familySymbolId: "9200",
      hostElementId: "501",
      expectedHostUniqueId: "not-wall-501",
      levelId: "311",
      location: point,
    },
  ] satisfies ChangeSetRequest["operations"];

  const preview = await bridge.previewChange(
    makeRequest(
      "session",
      "preview_change_set",
      "preview",
      {
        transactionName: "Mismatched identity guard",
        operations,
      } satisfies ChangeSetRequest,
      30000
    )
  );

  assert.equal(preview.ok, true);
  if (!preview.ok) return;
  assert.equal(preview.data.ready, false);
  assert.equal(preview.data.changes.length, operations.length);
  for (const [index, change] of preview.data.changes.entries()) {
    assert.equal(change.status, "blocked", `operation ${index} should be blocked`);
  }
  assert.match(preview.data.changes[0]?.message ?? "", /Element 501 uniqueId did not match expectedUniqueId/);
  assert.match(preview.data.changes[1]?.message ?? "", /Room 601 uniqueId did not match expectedUniqueId/);
  assert.match(preview.data.changes[9]?.message ?? "", /Host element 501 uniqueId did not match expectedHostUniqueId/);
});
