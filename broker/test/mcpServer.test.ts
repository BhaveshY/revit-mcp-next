import test from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createBrokerServer } from "../src/server.js";
import { FakeRevitBridgeClient } from "../src/ipc/FakeRevitBridgeClient.js";

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
      "revit.get_current_view_elements",
      "revit.get_selection",
      "revit.analyze_model",
      "revit.get_material_quantities",
      "revit.catalog",
      "revit.preview_change_set",
      "revit.apply_change_set",
      "revit.cancel_request",
    ]) {
      assert.ok(tools.tools.find((tool) => tool.name === expected), `${expected} tool should be listed`);
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

    const viewElements = (await client.callTool({
      name: "revit.get_current_view_elements",
      arguments: { preset: "summary", limit: 1, includeTotalCount: true },
    })) as {
      isError?: boolean;
      structuredContent?: { data?: { scope?: string; returnedCount?: number; totalCount?: number; items?: Array<{ id?: string }> } };
    };
    assert.equal(viewElements.isError, undefined);
    assert.equal(viewElements.structuredContent?.data?.scope, "activeView");
    assert.equal(viewElements.structuredContent?.data?.returnedCount, 1);
    assert.equal(viewElements.structuredContent?.data?.totalCount, 1);
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
    assert.equal(selection.structuredContent?.data?.selection?.count, 1);
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
      "forElementId",
      "typeChange",
      "familyNameContains",
      "nameContains",
      "viewFamily",
    ]) {
      assert.match(catalogSchema, new RegExp(expectedCatalogSchemaTerm));
    }

    const catalog = (await client.callTool({
      name: "revit.catalog",
      arguments: {
        kind: "elementTypes",
        filter: { forElementId: "501" },
        preset: "typeChange",
        limit: 1,
        includeTotalCount: true,
      },
    })) as {
      isError?: boolean;
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
    assert.equal(catalog.structuredContent?.data?.cursor, "1");
    assert.equal(catalog.structuredContent?.data?.items?.[0]?.validForTarget, true);
    assert.equal(catalog.structuredContent?.data?.items?.[0]?.isCurrentType, true);

    const previewTool = tools.tools.find((tool) => tool.name === "revit.preview_change_set");
    assert.ok(previewTool?.inputSchema, "revit.preview_change_set should declare inputSchema");
    const previewSchema = JSON.stringify(previewTool.inputSchema);
    for (const expectedSchemaTerm of [
      "set_parameter",
      "create_level",
      "create_wall",
      "move_element",
      "rotate_element",
      "copy_element",
      "change_element_type",
      "set_element_pinned",
      "create_grid",
      "create_floor",
      "levelId",
      "start",
      "end",
      "wallTypeId",
      "height",
      "structural",
      "flip",
      "translation",
      "axisStart",
      "axisEnd",
      "angle",
      "typeId",
      "pinned",
      "expectedPinned",
      "outline",
      "floorTypeId",
      "changeSetHash",
      "documentFingerprint",
      "expectedGeneration",
      "baseGeneration",
      "expiresAt",
    ]) {
      assert.match(previewSchema, new RegExp(expectedSchemaTerm));
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
          changeSetHash?: string;
          documentFingerprint?: string;
          baseGeneration?: number;
          expiresAt?: string;
        };
      };
    };
    assert.equal(preview.isError, undefined);
    assert.equal(preview.structuredContent?.data?.ready, true);
    assert.ok(preview.structuredContent?.data?.previewId);
    assert.ok(preview.structuredContent?.data?.changeSetHash);

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
      structuredContent?: { data?: { applied?: boolean; changedCount?: number; changeSetHash?: string } };
    };
    assert.equal(apply.isError, undefined);
    assert.equal(apply.structuredContent?.data?.applied, true);
    assert.equal(apply.structuredContent?.data?.changedCount, 9);
    assert.equal(apply.structuredContent?.data?.changeSetHash, preview.structuredContent?.data?.changeSetHash);

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
  } finally {
    await client.close();
    await server.close();
  }
});
