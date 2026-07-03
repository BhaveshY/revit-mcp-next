import test from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createBrokerServer } from "../src/server.js";
import { FakeRevitBridgeClient } from "../src/ipc/FakeRevitBridgeClient.js";

function assertNoUndefinedValues(value: unknown, path = "value"): void {
  assert.notEqual(value, undefined, `${path} should not be undefined`);
  if (Array.isArray(value)) {
    value.forEach((item, index) => assertNoUndefinedValues(item, `${path}[${index}]`));
    return;
  }
  if (value === null || typeof value !== "object") return;
  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    assertNoUndefinedValues(child, `${path}.${key}`);
  }
}

test("broker exposes annotated tools with output schemas and callable structured results", async () => {
  const server = createBrokerServer({
    bridge: new FakeRevitBridgeClient(),
    brokerVersion: "test",
    sessionId: "mcp-test",
  });

  const client = new Client({ name: "test-client", version: "0.1.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();

  await server.connect(serverTransport);
  await client.connect(clientTransport);

  try {
    const tools = await client.listTools();
    const statusTool = tools.tools.find((tool) => tool.name === "revit.status");
    assert.ok(statusTool, "revit.status tool should be listed");
    assert.equal(statusTool.annotations?.readOnlyHint, true);
    assert.equal(statusTool.annotations?.destructiveHint, false);
    assert.ok(statusTool.outputSchema, "revit.status should declare outputSchema");
    assert.match(JSON.stringify(statusTool.outputSchema), /connected/);

    const result = (await client.callTool({
      name: "revit.status",
      arguments: {},
    })) as {
      isError?: boolean;
      content: Array<{ type: "text"; text: string }>;
      structuredContent?: { data?: { connected?: boolean } };
    };

    assert.equal(result.isError, undefined);
    assert.equal(result.content[0]?.type, "text");
    assert.match(result.content[0].text, /Revit bridge connected/);
    assert.equal(result.structuredContent?.data?.connected, true);

    for (const expected of [
      "revit.get_current_view",
      "revit.list_documents",
      "revit.get_levels",
      "revit.get_views",
      "revit.get_sheets",
      "revit.get_current_view_elements",
      "revit.get_selection",
      "revit.analyze_model",
      "revit.get_model_readiness",
      "revit.get_material_quantities",
      "revit.get_rooms",
      "revit.catalog",
      "revit.query",
      "revit.describe_parameters",
      "revit.preview_change_set",
      "revit.apply_change_set",
      "revit.cancel_request",
    ]) {
      const tool = tools.tools.find((candidate) => candidate.name === expected);
      assert.ok(tool, `${expected} tool should be listed`);
      assertStructuredEnvelopeSchema(tool.outputSchema, expected);
    }

    const currentViewTool = tools.tools.find((tool) => tool.name === "revit.get_current_view");
    assert.ok(currentViewTool?.inputSchema, "revit.get_current_view should declare inputSchema");
    assert.equal(currentViewTool.annotations?.readOnlyHint, true);
    assert.match(JSON.stringify(currentViewTool.inputSchema), /includeCropBox/);

    const currentView = (await client.callTool({
      name: "revit.get_current_view",
      arguments: { includeCropBox: false },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { view?: { id?: string; uniqueId?: string; name?: string } } };
    };
    assert.equal(currentView.isError, undefined);
    assert.equal(currentView.structuredContent?.data?.view?.id, "1024");
    assert.equal(currentView.structuredContent?.data?.view?.uniqueId, "view-1024");

    const viewsTool = tools.tools.find((tool) => tool.name === "revit.get_views");
    assert.ok(viewsTool?.inputSchema, "revit.get_views should declare inputSchema");
    assert.match(JSON.stringify(viewsTool.inputSchema), /sheetPlacement/);
    const viewsOutputSchema = JSON.stringify(viewsTool.outputSchema);
    assert.match(viewsOutputSchema, /returnedCount/);
    assert.match(viewsOutputSchema, /truncated/);
    assert.match(viewsOutputSchema, /cursor/);
    assert.match(viewsOutputSchema, /items/);
    const views = (await client.callTool({
      name: "revit.get_views",
      arguments: { filter: { viewTypes: ["FloorPlan"] }, includeTotalCount: true },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { returnedCount?: number; totalCount?: number; items?: Array<{ id?: string; type?: string }> } };
    };
    assert.equal(views.isError, undefined);
    assert.equal(views.structuredContent?.data?.returnedCount, 1);
    assert.equal(views.structuredContent?.data?.items?.[0]?.id, "1024");
    assert.equal(views.structuredContent?.data?.items?.[0]?.type, "FloorPlan");

    const sheetsTool = tools.tools.find((tool) => tool.name === "revit.get_sheets");
    assert.ok(sheetsTool?.inputSchema, "revit.get_sheets should declare inputSchema");
    assert.match(JSON.stringify(sheetsTool.inputSchema), /includePlacedViews/);
    const sheets = (await client.callTool({
      name: "revit.get_sheets",
      arguments: { includePlacedViews: true, includeTotalCount: true },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: { returnedCount?: number; items?: Array<{ id?: string; sheetNumber?: string; placedViews?: Array<{ viewId?: string }> }> };
      };
    };
    assert.equal(sheets.isError, undefined);
    assert.equal(sheets.structuredContent?.data?.returnedCount, 1);
    assert.equal(sheets.structuredContent?.data?.items?.[0]?.sheetNumber, "A-101");
    assert.equal(sheets.structuredContent?.data?.items?.[0]?.placedViews?.[0]?.viewId, "1024");

    const viewElements = (await client.callTool({
      name: "revit.get_current_view_elements",
      arguments: { preset: "summary", limit: 1, includeTotalCount: true },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { scope?: string; returnedCount?: number; totalCount?: number; truncated?: boolean; cursor?: string; items?: Array<{ id?: string }> } };
    };
    assert.equal(viewElements.isError, undefined);
    assert.equal(viewElements.structuredContent?.data?.scope, "activeView");
    assert.equal(viewElements.structuredContent?.data?.returnedCount, 1);
    assert.equal(viewElements.structuredContent?.data?.totalCount, 2);
    assert.equal(viewElements.structuredContent?.data?.truncated, true);
    assert.match(viewElements.structuredContent?.data?.cursor ?? "", /^rvc1_/);
    assert.equal(viewElements.structuredContent?.data?.items?.[0]?.id, "501");

    const selection = (await client.callTool({
      name: "revit.get_selection",
      arguments: { limit: 1, includeTotalCount: true },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { scope?: string; selection?: { count?: number; available?: boolean } } };
    };
    assert.equal(selection.isError, undefined);
    assert.equal(selection.structuredContent?.data?.scope, "selection");
    assert.equal(selection.structuredContent?.data?.selection?.count, 2);
    assert.equal(selection.structuredContent?.data?.selection?.available, true);

    const modelStats = (await client.callTool({
      name: "revit.analyze_model",
      arguments: { bucketLimit: 10 },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { totals?: { elements?: number; materials?: number }; byCategory?: Array<{ key?: string }> } };
    };
    assert.equal(modelStats.isError, undefined);
    assert.equal(modelStats.structuredContent?.data?.totals?.elements, 42);
    assert.equal(modelStats.structuredContent?.data?.totals?.materials, 2);
    assert.equal(modelStats.structuredContent?.data?.byCategory?.[0]?.key, "OST_Walls");

    const readinessTool = tools.tools.find((tool) => tool.name === "revit.get_model_readiness");
    assert.ok(readinessTool?.inputSchema, "revit.get_model_readiness should declare inputSchema");
    assert.match(JSON.stringify(readinessTool.inputSchema), /familyPlacement/);
    const readiness = (await client.callTool({
      name: "revit.get_model_readiness",
      arguments: { scenarios: ["familyPlacement", "selection"], includeHints: true },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: {
          readyCount?: number;
          totalCount?: number;
          scenarios?: Array<{ name?: string; ready?: boolean; hints?: { hostedFamilySymbolId?: string } }>;
        };
      };
    };
    assert.equal(readiness.isError, undefined);
    assert.equal(readiness.structuredContent?.data?.readyCount, 2);
    assert.equal(readiness.structuredContent?.data?.totalCount, 2);
    assert.equal(readiness.structuredContent?.data?.scenarios?.[0]?.name, "familyPlacement");
    assert.equal(readiness.structuredContent?.data?.scenarios?.[0]?.hints?.hostedFamilySymbolId, "9200");

    const materialQuantities = (await client.callTool({
      name: "revit.get_material_quantities",
      arguments: { limit: 1, includeTotalCount: true },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: { returnedCount?: number; totalCount?: number; units?: { area?: string; volume?: string }; items?: Array<{ materialId?: string }> };
      };
    };
    assert.equal(materialQuantities.isError, undefined);
    assert.equal(materialQuantities.structuredContent?.data?.returnedCount, 1);
    assert.equal(materialQuantities.structuredContent?.data?.totalCount, 1);
    assert.equal(materialQuantities.structuredContent?.data?.units?.area, "m2");
    assert.equal(materialQuantities.structuredContent?.data?.units?.volume, "m3");
    assert.equal(materialQuantities.structuredContent?.data?.items?.[0]?.materialId, "7001");

    const roomsTool = tools.tools.find((tool) => tool.name === "revit.get_rooms");
    assert.ok(roomsTool?.inputSchema, "revit.get_rooms should declare inputSchema");
    assert.equal(roomsTool.annotations?.readOnlyHint, true);
    const roomsSchema = JSON.stringify(roomsTool.inputSchema);
    for (const expectedRoomSchemaTerm of [
      "levelIds",
      "phaseIds",
      "numbers",
      "numberContains",
      "includeUnplaced",
      "schedule",
      "fields",
      "includeTotalCount",
    ]) {
      assert.match(roomsSchema, new RegExp(expectedRoomSchemaTerm));
    }
    const rooms = (await client.callTool({
      name: "revit.get_rooms",
      arguments: {
        filter: { levelIds: ["311"], numberContains: "101" },
        preset: "schedule",
        includeTotalCount: true,
      },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: {
          returnedCount?: number;
          totalCount?: number;
          units?: { area?: string; volume?: string; location?: string };
          items?: Array<{ id?: string; number?: string; name?: string }>;
        };
      };
    };
    assert.equal(rooms.isError, undefined);
    assert.equal(rooms.structuredContent?.data?.returnedCount, 1);
    assert.equal(rooms.structuredContent?.data?.totalCount, 1);
    assert.equal(rooms.structuredContent?.data?.units?.area, "m2");
    assert.equal(rooms.structuredContent?.data?.units?.volume, "m3");
    assert.equal(rooms.structuredContent?.data?.units?.location, "mm");
    assert.equal(rooms.structuredContent?.data?.items?.[0]?.id, "601");
    assert.equal(rooms.structuredContent?.data?.items?.[0]?.number, "101");
    assert.equal(rooms.structuredContent?.data?.items?.[0]?.name, "Conference");

    const queryTool = tools.tools.find((tool) => tool.name === "revit.query");
    assert.ok(queryTool?.inputSchema, "revit.query should declare inputSchema");
    const querySchema = JSON.stringify(queryTool.inputSchema);
    assert.match(querySchema, /elementIds/);
    assert.match(querySchema, /uniqueIds/);
    assert.match(querySchema, /parameterEquals/);
    const queryOutputSchema = JSON.stringify(queryTool.outputSchema);
    assert.match(queryOutputSchema, /returnedCount/);
    assert.match(queryOutputSchema, /truncated/);
    assert.match(queryOutputSchema, /cursor/);
    assert.match(queryOutputSchema, /fields/);
    assert.match(queryOutputSchema, /units/);
    const explicitQuery = (await client.callTool({
      name: "revit.query",
      arguments: {
        filter: { elementIds: ["501"] },
        fields: ["id", "uniqueId", "class"],
        includeTotalCount: true,
      },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: { scope?: string; returnedCount?: number; totalCount?: number; items?: Array<{ id?: string; uniqueId?: string; class?: string }> };
      };
    };
    assert.equal(explicitQuery.isError, undefined);
    assert.equal(explicitQuery.structuredContent?.data?.scope, "elements");
    assert.equal(explicitQuery.structuredContent?.data?.returnedCount, 1);
    assert.equal(explicitQuery.structuredContent?.data?.totalCount, 1);
    assert.equal(explicitQuery.structuredContent?.data?.items?.[0]?.id, "501");
    assert.equal(explicitQuery.structuredContent?.data?.items?.[0]?.uniqueId, "wall-501");
    assert.equal(explicitQuery.structuredContent?.data?.items?.[0]?.class, "Wall");

    try {
      const invalidQuery = (await client.callTool({
        name: "revit.query",
        arguments: {
          filter: { category: "Walls" },
          limit: 1,
        },
      })) as {
        isError?: boolean;
        content?: Array<{ type: "text"; text: string }>;
      };
      assert.equal(invalidQuery.isError, true);
      assert.match(invalidQuery.content?.[0]?.text ?? "", /category|Unrecognized|Invalid/i);
    } catch (error) {
      assert.match(String(error), /category|Unrecognized|Invalid/i);
    }

    const parametersTool = tools.tools.find((tool) => tool.name === "revit.describe_parameters");
    assert.ok(parametersTool?.inputSchema, "revit.describe_parameters should declare inputSchema");
    assert.match(JSON.stringify(parametersTool.inputSchema), /includeTypeParameters/);
    assert.match(JSON.stringify(parametersTool.inputSchema), /writableEdit/);
    const parameters = (await client.callTool({
      name: "revit.describe_parameters",
      arguments: {
        filter: { elementIds: ["501"] },
        preset: "full",
        includeTotalCount: true,
      },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: {
          preset?: string;
          returnedCount?: number;
          parameterLimit?: number;
          items?: Array<{ id?: string; parameters?: Array<{ name?: string; isReadOnly?: boolean; source?: string; value?: unknown }> }>;
        };
      };
    };
    assert.equal(parameters.isError, undefined);
    assert.equal(parameters.structuredContent?.data?.preset, "full");
    assert.equal(parameters.structuredContent?.data?.parameterLimit, 80);
    assert.equal(parameters.structuredContent?.data?.returnedCount, 1);
    assert.equal(parameters.structuredContent?.data?.items?.[0]?.id, "501");
    assert.ok(parameters.structuredContent?.data?.items?.[0]?.parameters?.some((parameter) => parameter.name === "Mark" && parameter.isReadOnly === false));
    assert.ok(parameters.structuredContent?.data?.items?.[0]?.parameters?.some((parameter) => parameter.source === "type"));

    const compactParameters = (await client.callTool({
      name: "revit.describe_parameters",
      arguments: {
        filter: { elementIds: ["501"] },
      },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: {
          preset?: string;
          limit?: number;
          parameterLimit?: number;
          items?: Array<{ parameters?: Array<{ source?: string; isReadOnly?: boolean; value?: unknown; valueString?: string }> }>;
        };
      };
    };
    assert.equal(compactParameters.isError, undefined);
    assert.equal(compactParameters.structuredContent?.data?.preset, "writableEdit");
    assert.equal(compactParameters.structuredContent?.data?.limit, 10);
    assert.equal(compactParameters.structuredContent?.data?.parameterLimit, 40);
    assert.ok(compactParameters.structuredContent?.data?.items?.[0]?.parameters?.every((parameter) => parameter.source === "instance"));
    assert.ok(compactParameters.structuredContent?.data?.items?.[0]?.parameters?.every((parameter) => parameter.isReadOnly === false));
    assert.ok(compactParameters.structuredContent?.data?.items?.[0]?.parameters?.every((parameter) => parameter.value === undefined));
    assert.ok(compactParameters.structuredContent?.data?.items?.[0]?.parameters?.every((parameter) => parameter.valueString === undefined));

    const catalogTool = tools.tools.find((tool) => tool.name === "revit.catalog");
    assert.ok(catalogTool?.inputSchema, "revit.catalog should declare inputSchema");
    assert.equal(catalogTool.annotations?.readOnlyHint, true);
    const catalogSchema = JSON.stringify(catalogTool.inputSchema);
    for (const expectedCatalogSchemaTerm of [
      "kind",
      "elementTypes",
      "familySymbols",
      "titleBlocks",
      "viewFamilyTypes",
      "textNoteTypes",
      "dimensionTypes",
      "tagTypes",
      "forElementId",
      "typeChange",
      "familyNameContains",
      "nameContains",
      "viewFamily",
      "annotation",
    ]) {
      assert.match(catalogSchema, new RegExp(expectedCatalogSchemaTerm));
    }
    const catalogOutputSchema = JSON.stringify(catalogTool.outputSchema);
    assert.match(catalogOutputSchema, /kind/);
    assert.match(catalogOutputSchema, /target/);
    assert.match(catalogOutputSchema, /returnedCount/);
    assert.match(catalogOutputSchema, /cursor/);

    const catalogArgs = {
      kind: "elementTypes",
      filter: { forElementId: "501" },
      preset: "typeChange",
      limit: 1,
      includeTotalCount: true,
    };
    const catalog = (await client.callTool({
      name: "revit.catalog",
      arguments: catalogArgs,
    })) as {
      isError?: boolean;
      content?: Array<{ type: "text"; text: string }>;
      structuredContent?: {
        data?: {
          kind?: string;
          returnedCount?: number;
          totalCount?: number;
          truncated?: boolean;
          cursor?: string;
          target?: { elementId?: string; currentTypeId?: string };
          items?: Array<{ id?: string; isCurrentType?: boolean; validForTarget?: boolean }>;
        };
      };
    };
    assert.equal(catalog.isError, undefined);
    assert.equal(catalog.structuredContent?.data?.kind, "elementTypes");
    assert.equal(catalog.structuredContent?.data?.target?.elementId, "501");
    assert.equal(catalog.structuredContent?.data?.target?.currentTypeId, "9001");
    assert.equal(catalog.structuredContent?.data?.returnedCount, 1);
    assert.equal(catalog.structuredContent?.data?.totalCount, 2);
    assert.equal(catalog.structuredContent?.data?.truncated, true);
    assert.match(catalog.structuredContent?.data?.cursor ?? "", /^rvc1_/);
    assert.notEqual(catalog.structuredContent?.data?.cursor, "1");
    assert.doesNotMatch(catalog.content?.[0]?.text ?? "", /rvc1_/);
    assert.match(catalog.content?.[0]?.text ?? "", /structuredContent\.data\.cursor/);
    assert.equal(catalog.structuredContent?.data?.items?.[0]?.validForTarget, true);
    assert.equal(catalog.structuredContent?.data?.items?.[0]?.isCurrentType, true);

    const catalogSecondPage = (await client.callTool({
      name: "revit.catalog",
      arguments: {
        ...catalogArgs,
        cursor: catalog.structuredContent?.data?.cursor,
      },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { returnedCount?: number; truncated?: boolean; cursor?: string; items?: Array<{ id?: string; isCurrentType?: boolean }> } };
    };
    assert.equal(catalogSecondPage.isError, undefined);
    assert.equal(catalogSecondPage.structuredContent?.data?.returnedCount, 1);
    assert.equal(catalogSecondPage.structuredContent?.data?.truncated, false);
    assert.equal(catalogSecondPage.structuredContent?.data?.cursor, undefined);
    assert.equal(catalogSecondPage.structuredContent?.data?.items?.[0]?.id, "9002");
    assert.equal(catalogSecondPage.structuredContent?.data?.items?.[0]?.isCurrentType, undefined);

    const rawCursor = (await client.callTool({
      name: "revit.catalog",
      arguments: {
        ...catalogArgs,
        cursor: "1",
      },
    })) as {
      isError?: boolean;
      content?: Array<{ type: "text"; text: string }>;
      structuredContent?: { data?: { error?: { code?: string } } };
    };
    assert.equal(rawCursor.isError, true);
    assert.equal(rawCursor.structuredContent?.data?.error?.code, "INVALID_CURSOR");

    const mismatchedCursor = (await client.callTool({
      name: "revit.catalog",
      arguments: {
        ...catalogArgs,
        limit: 2,
        cursor: catalog.structuredContent?.data?.cursor,
      },
    })) as {
      isError?: boolean;
      content?: Array<{ type: "text"; text: string }>;
      structuredContent?: { data?: { error?: { code?: string } } };
    };
    assert.equal(mismatchedCursor.isError, true);
    assert.equal(mismatchedCursor.structuredContent?.data?.error?.code, "CURSOR_SCOPE_MISMATCH");

    const tagCatalog = (await client.callTool({
      name: "revit.catalog",
      arguments: {
        kind: "tagTypes",
        preset: "annotation",
        limit: 1,
        includeTotalCount: true,
      },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { kind?: string; returnedCount?: number; items?: Array<{ id?: string; class?: string }> } };
    };
    assert.equal(tagCatalog.isError, undefined);
    assert.equal(tagCatalog.structuredContent?.data?.kind, "tagTypes");
    assert.equal(tagCatalog.structuredContent?.data?.returnedCount, 1);
    assert.equal(tagCatalog.structuredContent?.data?.items?.[0]?.class, "FamilySymbol");

    const previewTool = tools.tools.find((tool) => tool.name === "revit.preview_change_set");
    assert.ok(previewTool?.inputSchema, "revit.preview_change_set should declare inputSchema");
    const previewSchema = JSON.stringify(previewTool.inputSchema);
    for (const expectedSchemaTerm of [
      "set_parameter",
      "create_level",
      "create_wall",
      "place_family_instance",
      "create_sheet",
      "place_view_on_sheet",
      "create_text_note",
      "tag_room",
      "tag_element",
      "move_element",
      "rotate_element",
      "copy_element",
      "change_element_type",
      "set_element_pinned",
      "create_grid",
      "create_floor",
      "create_room",
      "delete_element",
      "levelId",
      "start",
      "end",
      "wallTypeId",
      "familySymbolId",
      "hostElementId",
      "sheetNumber",
      "titleBlockTypeId",
      "sheetId",
      "viewId",
      "center",
      "text",
      "position",
      "textNoteTypeId",
      "roomId",
      "elementId",
      "tagTypeId",
      "hasLeader",
      "orientation",
      "width",
      "height",
      "structural",
      "flip",
      "translation",
      "axisStart",
      "axisEnd",
      "angle",
      "rotation",
      "typeId",
      "pinned",
      "expectedPinned",
      "expectedUniqueId",
      "allowPinned",
      "allowDependentDeletes",
      "expectedDeletedElementIds",
      "expectedDeletedCount",
      "dependentDeleteLimit",
      "outline",
      "floorTypeId",
      "location",
      "number",
      "department",
      "allowDuplicateNumber",
      "flipFacing",
      "flipHand",
      "allowPinnedHost",
      "changeSetHash",
      "documentFingerprint",
      "expectedGeneration",
      "baseGeneration",
      "expiresAt",
    ]) {
      assert.match(previewSchema, new RegExp(expectedSchemaTerm));
    }
    const previewOutputSchema = JSON.stringify(previewTool.outputSchema);
    for (const expectedSchemaTerm of [
      "previewId",
      "documentFingerprint",
      "changeSetHash",
      "baseGeneration",
      "expiresAt",
      "transactionName",
      "operationCount",
      "ready",
      "requiresConfirmation",
      "riskLevel",
      "changes",
      "operationIndex",
      "status",
      "target",
      "before",
      "after",
      "message",
    ]) {
      assert.match(previewOutputSchema, new RegExp(expectedSchemaTerm));
    }

    const applyTool = tools.tools.find((tool) => tool.name === "revit.apply_change_set");
    assert.ok(applyTool?.outputSchema, "revit.apply_change_set should declare outputSchema");
    const applyOutputSchema = JSON.stringify(applyTool.outputSchema);
    for (const expectedSchemaTerm of [
      "previewId",
      "documentFingerprint",
      "changeSetHash",
      "baseGeneration",
      "transactionName",
      "applied",
      "changedCount",
      "changes",
      "operationIndex",
      "status",
      "target",
      "after",
    ]) {
      assert.match(applyOutputSchema, new RegExp(expectedSchemaTerm));
    }

    const cancelTool = tools.tools.find((tool) => tool.name === "revit.cancel_request");
    assert.ok(cancelTool?.outputSchema, "revit.cancel_request should declare outputSchema");
    const cancelOutputSchema = JSON.stringify(cancelTool.outputSchema);
    for (const expectedSchemaTerm of ["cancelled", "requestId", "message"]) {
      assert.match(cancelOutputSchema, new RegExp(expectedSchemaTerm));
    }

    const wallStart = {
      x: { value: 0, unit: "mm", system: "metric" },
      y: { value: 0, unit: "mm", system: "metric" },
      z: { value: 0, unit: "mm", system: "metric" },
    };
    const wallEnd = {
      x: { value: 4200, unit: "mm", system: "metric" },
      y: { value: 0, unit: "mm", system: "metric" },
      z: { value: 0, unit: "mm", system: "metric" },
    };
    const roomLocation = {
      x: { value: 2000, unit: "mm", system: "metric" },
      y: { value: 1500, unit: "mm", system: "metric" },
    };
    const operations = [
      {
        type: "set_parameter",
        elementId: "501",
        parameterName: "Mark",
        value: "A-101",
      },
      {
        type: "create_wall",
        levelId: "311",
        start: wallStart,
        end: wallEnd,
        wallTypeId: "9001",
        height: { value: 3000, unit: "mm", system: "metric" },
        structural: false,
        flip: true,
      },
      {
        type: "place_family_instance",
        familySymbolId: "9200",
        levelId: "311",
        location: {
          x: { value: 1200, unit: "mm", system: "metric" },
          y: { value: 0, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
        rotation: { value: 0, unit: "degrees" },
        flipFacing: true,
        flipHand: false,
      },
      {
        type: "create_sheet",
        sheetNumber: "A-201",
        name: "Preview Sheet",
        titleBlockTypeId: "9300",
      },
      {
        type: "place_view_on_sheet",
        sheetId: "1101",
        viewId: "1025",
        center: {
          x: { value: 250, unit: "mm", system: "metric" },
          y: { value: 180, unit: "mm", system: "metric" },
        },
      },
      {
        type: "create_text_note",
        viewId: "1024",
        text: "MCP generated note",
        position: {
          x: { value: 500, unit: "mm", system: "metric" },
          y: { value: 500, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
        textNoteTypeId: "9400",
        width: { value: 1200, unit: "mm", system: "metric" },
        rotation: { value: 0, unit: "degrees" },
      },
      {
        type: "tag_room",
        roomId: "601",
        viewId: "1024",
        location: roomLocation,
        tagTypeId: "9700",
        hasLeader: false,
        orientation: "Horizontal",
      },
      {
        type: "tag_element",
        elementId: "501",
        viewId: "1024",
        tagTypeId: "9701",
        position: {
          x: { value: 2200, unit: "mm", system: "metric" },
          y: { value: 450, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
        hasLeader: true,
        orientation: "Horizontal",
      },
      {
        type: "move_element",
        elementId: "501",
        translation: {
          x: { value: 0, unit: "mm", system: "metric" },
          y: { value: 250, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
      },
      {
        type: "rotate_element",
        elementId: "501",
        axisStart: {
          x: { value: 0, unit: "mm", system: "metric" },
          y: { value: 0, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
        axisEnd: {
          x: { value: 0, unit: "mm", system: "metric" },
          y: { value: 0, unit: "mm", system: "metric" },
          z: { value: 1, unit: "m", system: "metric" },
        },
        angle: { value: 90, unit: "degrees" },
      },
      {
        type: "copy_element",
        elementId: "501",
        translation: {
          x: { value: 1200, unit: "mm", system: "metric" },
          y: { value: 0, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
      },
      {
        type: "change_element_type",
        elementId: "501",
        typeId: "9002",
      },
      {
        type: "set_element_pinned",
        elementId: "501",
        pinned: true,
        expectedPinned: false,
      },
      {
        type: "create_grid",
        name: "A",
        start: {
          x: { value: 0, unit: "mm", system: "metric" },
          y: { value: 0, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
        end: {
          x: { value: 5000, unit: "mm", system: "metric" },
          y: { value: 0, unit: "mm", system: "metric" },
          z: { value: 0, unit: "mm", system: "metric" },
        },
      },
      {
        type: "create_floor",
        levelId: "311",
        floorTypeId: "9100",
        structural: false,
        outline: [
          {
            x: { value: 0, unit: "mm", system: "metric" },
            y: { value: 0, unit: "mm", system: "metric" },
            z: { value: 0, unit: "mm", system: "metric" },
          },
          {
            x: { value: 4000, unit: "mm", system: "metric" },
            y: { value: 0, unit: "mm", system: "metric" },
            z: { value: 0, unit: "mm", system: "metric" },
          },
          {
            x: { value: 4000, unit: "mm", system: "metric" },
            y: { value: 3000, unit: "mm", system: "metric" },
            z: { value: 0, unit: "mm", system: "metric" },
          },
          {
            x: { value: 0, unit: "mm", system: "metric" },
            y: { value: 3000, unit: "mm", system: "metric" },
            z: { value: 0, unit: "mm", system: "metric" },
          },
        ],
      },
      {
        type: "create_room",
        levelId: "311",
        location: roomLocation,
        name: "Conference",
        number: "101",
        department: "Operations",
      },
      {
        type: "delete_element",
        elementId: "501",
        expectedUniqueId: "wall-501",
        expectedPinned: false,
      },
    ];
    const preview = (await client.callTool({
      name: "revit.preview_change_set",
      arguments: {
        transactionName: "Update Mark Wall Move",
        documentFingerprint: "sample-doc-fingerprint",
        expectedGeneration: 7,
        operations,
      },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: {
          previewId?: string;
          ready?: boolean;
          riskLevel?: string;
          changeSetHash?: string;
          documentFingerprint?: string;
          baseGeneration?: number;
          expiresAt?: string;
          transactionName?: string;
          operationCount?: number;
          requiresConfirmation?: boolean;
          changes?: Array<{ operationIndex?: number; type?: string; status?: string; target?: Record<string, unknown>; after?: Record<string, unknown> }>;
        };
      };
    };
    assert.equal(preview.isError, undefined);
    assertNoUndefinedValues(preview.structuredContent, "preview.structuredContent");
    assert.equal(preview.structuredContent?.data?.ready, true);
    assert.equal(preview.structuredContent?.data?.riskLevel, "high");
    assert.ok(preview.structuredContent?.data?.previewId);
    assert.equal(preview.structuredContent?.data?.documentFingerprint, "sample-doc-fingerprint");
    assert.equal(preview.structuredContent?.data?.baseGeneration, 7);
    assert.ok(preview.structuredContent?.data?.changeSetHash);
    assert.ok(preview.structuredContent?.data?.expiresAt);
    assert.equal(preview.structuredContent?.data?.transactionName, "Update Mark Wall Move");
    assert.equal(preview.structuredContent?.data?.operationCount, 17);
    assert.equal(preview.structuredContent?.data?.requiresConfirmation, true);
    assert.equal(preview.structuredContent?.data?.changes?.length, 17);
    const firstPreviewChange = preview.structuredContent?.data?.changes?.[0];
    assert.equal(Object.hasOwn(firstPreviewChange ?? {}, "operationId"), false);
    assert.deepEqual(firstPreviewChange, {
      operationIndex: 0,
      type: "set_parameter",
      status: "ready",
      target: { elementId: "501", parameterName: "Mark" },
      after: { value: "A-101" },
    });
    const familyPreviewChange = preview.structuredContent?.data?.changes?.find((change) => change.type === "place_family_instance");
    assert.deepEqual(familyPreviewChange?.target, {
      document: "Sample.rvt",
      familySymbolId: "9200",
      levelId: "311",
    });
    assert.equal(Object.hasOwn(familyPreviewChange?.target ?? {}, "hostElementId"), false);

    try {
      const missingMetadataApply = (await client.callTool({
        name: "revit.apply_change_set",
        arguments: {
          transactionName: "Update Mark Wall Move",
          documentFingerprint: preview.structuredContent?.data?.documentFingerprint,
          operations,
          previewId: preview.structuredContent?.data?.previewId,
          confirm: true,
        },
      })) as {
        isError?: boolean;
        content?: Array<{ type: "text"; text: string }>;
      };
      assert.equal(missingMetadataApply.isError, true);
      assert.match(missingMetadataApply.content?.[0]?.text ?? "", /baseGeneration|changeSetHash|expiresAt|Invalid/i);
    } catch (error) {
      assert.match(String(error), /baseGeneration|changeSetHash|expiresAt|Invalid/i);
    }

    const apply = (await client.callTool({
      name: "revit.apply_change_set",
      arguments: {
        transactionName: "Update Mark Wall Move",
        documentFingerprint: preview.structuredContent?.data?.documentFingerprint,
        baseGeneration: preview.structuredContent?.data?.baseGeneration,
        changeSetHash: preview.structuredContent?.data?.changeSetHash,
        expiresAt: preview.structuredContent?.data?.expiresAt,
        operations,
        previewId: preview.structuredContent?.data?.previewId,
        confirm: true,
      },
    })) as {
      isError?: boolean;
      structuredContent?: {
        data?: {
          applied?: boolean;
          changedCount?: number;
          changeSetHash?: string;
          previewId?: string;
          documentFingerprint?: string;
          baseGeneration?: number;
          transactionName?: string;
          changes?: Array<{ operationIndex?: number; type?: string; status?: string; target?: Record<string, unknown>; after?: Record<string, unknown> }>;
        };
      };
    };
    assert.equal(apply.isError, undefined);
    assertNoUndefinedValues(apply.structuredContent, "apply.structuredContent");
    assert.equal(apply.structuredContent?.data?.applied, true);
    assert.equal(apply.structuredContent?.data?.changedCount, 17);
    assert.equal(apply.structuredContent?.data?.previewId, preview.structuredContent?.data?.previewId);
    assert.equal(apply.structuredContent?.data?.documentFingerprint, preview.structuredContent?.data?.documentFingerprint);
    assert.equal(apply.structuredContent?.data?.baseGeneration, preview.structuredContent?.data?.baseGeneration);
    assert.equal(apply.structuredContent?.data?.changeSetHash, preview.structuredContent?.data?.changeSetHash);
    assert.equal(apply.structuredContent?.data?.transactionName, "Update Mark Wall Move");
    assert.equal(apply.structuredContent?.data?.changes?.length, 17);
    assert.equal(Object.hasOwn(apply.structuredContent?.data?.changes?.[0] ?? {}, "operationId"), false);
    assert.equal(apply.structuredContent?.data?.changes?.[0]?.status, "applied");
    assert.deepEqual(apply.structuredContent?.data?.changes?.[0]?.after, { value: "A-101" });

    const invalidPreview = (await client.callTool({
      name: "revit.preview_change_set",
      arguments: {
        transactionName: "Bad Preview",
        operations: [
          {
            type: "set_parameter",
            parameterName: "Mark",
            value: "A-101",
          },
        ],
      },
    })) as {
      isError?: boolean;
      content: Array<{ type: "text"; text: string }>;
    };
    assert.equal(invalidPreview.isError, true);
    assert.match(invalidPreview.content[0]?.text ?? "", /elementId/);

    const cancel = (await client.callTool({
      name: "revit.cancel_request",
      arguments: { requestId: "fake-pending-request", reason: "schema test" },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { cancelled?: boolean; requestId?: string; message?: string } };
    };
    assert.equal(cancel.isError, undefined);
    assertNoUndefinedValues(cancel.structuredContent, "cancel.structuredContent");
    assert.equal(cancel.structuredContent?.data?.cancelled, false);
    assert.equal(cancel.structuredContent?.data?.requestId, "fake-pending-request");
    assert.match(cancel.structuredContent?.data?.message ?? "", /No queued fake request/);
  } finally {
    await client.close();
    await server.close();
  }
});

test("broker exposes MCP discovery resources and workflow prompts", async () => {
  const server = createBrokerServer({
    bridge: new FakeRevitBridgeClient(),
    brokerVersion: "test",
    sessionId: "mcp-discovery-test",
  });

  const client = new Client({ name: "test-client", version: "0.1.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();

  await server.connect(serverTransport);
  await client.connect(clientTransport);

  try {
    const capabilities = client.getServerCapabilities() as {
      resources?: { listChanged?: boolean };
      prompts?: { listChanged?: boolean };
    };
    assert.equal(capabilities.resources?.listChanged, true);
    assert.equal(capabilities.prompts?.listChanged, true);

    const resources = await client.listResources();
    assert.ok(resources.resources.find((resource) => resource.uri === "revit://discovery"));
    assert.ok(resources.resources.find((resource) => resource.uri === "revit://tools/revit.query"));

    const templates = await client.listResourceTemplates();
    assert.ok(templates.resourceTemplates.find((template) => template.uriTemplate === "revit://tools/{name}"));

    const discoveryResource = await client.readResource({ uri: "revit://discovery" });
    assert.equal(discoveryResource.contents[0]?.mimeType, "application/json");
    assert.equal(discoveryResource.contents[0]?.uri, "revit://discovery");
    assert.ok("text" in discoveryResource.contents[0], "discovery resource should be text JSON");
    const discovery = JSON.parse(discoveryResource.contents[0].text) as {
      brokerVersion?: string;
      protocolVersion?: string;
      tools?: Array<{ name?: string; resource?: string }>;
      workflow?: string[];
      writeOperations?: string[];
    };
    assert.equal(discovery.brokerVersion, "test");
    assert.ok(discovery.protocolVersion);
    assert.ok(discovery.workflow?.some((step) => step.includes("revit.status")));
    assert.ok(discovery.tools?.some((tool) => tool.name === "revit.apply_change_set" && tool.resource === "revit://tools/revit.apply_change_set"));
    assert.ok(discovery.writeOperations?.includes("tag_element"));
    assert.doesNotMatch(JSON.stringify(discovery), /auth\.env|pipeToken|Bhavesh/i);

    const queryResource = await client.readResource({ uri: "revit://tools/revit.query" });
    assert.ok("text" in queryResource.contents[0], "tool resource should be text JSON");
    const queryDoc = JSON.parse(queryResource.contents[0].text) as {
      name?: string;
      title?: string;
      readOnly?: boolean;
      compactUse?: string;
    };
    assert.equal(queryDoc.name, "revit.query");
    assert.equal(queryDoc.title, "Query Revit Model");
    assert.equal(queryDoc.readOnly, true);
    assert.match(queryDoc.compactUse ?? "", /fields|preset|cursor/i);

    await assert.rejects(
      () => client.readResource({ uri: "revit://tools/not-a-tool" }),
      /Unknown Revit MCP tool resource|Invalid/
    );

    const prompts = await client.listPrompts();
    assert.ok(prompts.prompts.find((prompt) => prompt.name === "revit.start_workflow"));
    assert.ok(prompts.prompts.find((prompt) => prompt.name === "revit.workflow"));

    const startPrompt = await client.getPrompt({ name: "revit.start_workflow" });
    assert.match(startPrompt.messages[0]?.content.type === "text" ? startPrompt.messages[0].content.text : "", /revit\.status/);

    const workflowPrompt = await client.getPrompt({
      name: "revit.workflow",
      arguments: { workflow: "selection-update" },
    });
    assert.match(workflowPrompt.messages[0]?.content.type === "text" ? workflowPrompt.messages[0].content.text : "", /describe_parameters/);
  } finally {
    await client.close();
    await server.close();
  }
});

function assertStructuredEnvelopeSchema(outputSchema: unknown, toolName: string): void {
  assert.ok(outputSchema && typeof outputSchema === "object", `${toolName} should expose an object output schema`);
  const schema = outputSchema as {
    type?: string;
    required?: string[];
    additionalProperties?: unknown;
    properties?: Record<string, unknown>;
  };
  assert.equal(schema.type, "object", `${toolName} output schema root should be an object`);
  assert.equal(schema.additionalProperties, false, `${toolName} output schema root should be strict`);
  for (const required of ["data", "warnings", "metrics"]) {
    assert.ok(schema.required?.includes(required), `${toolName} output schema should require ${required}`);
    assert.ok(schema.properties?.[required], `${toolName} output schema should define ${required}`);
  }
}
