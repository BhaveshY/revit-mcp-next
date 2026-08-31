import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { ModelDeliveryRecipe } from "@revit-mcp-next/contracts";
import { ModelDeliveryRecipeStore } from "../src/recipes/ModelDeliveryRecipeStore.js";
import { Client, InMemoryTransport } from "@modelcontextprotocol/client";
import { createBrokerServer } from "../src/server.js";
import { FakeRevitBridgeClient } from "../src/ipc/FakeRevitBridgeClient.js";

function recipe(projectId: string, packageName = "Issue-01"): ModelDeliveryRecipe {
  return {
    projectId,
    recipeVersion: "1",
    deliveryId: "delivery-01",
    packageName,
    destinationRoot: "C:\\Delivery",
    sourceModels: [
      { id: "architecture", sourcePath: "C:\\Models\\Architecture.rvt", targetFileName: "Architecture.rvt" },
    ],
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

test("recipe store saves idempotently, returns latest, and enforces optimistic concurrency", async () => {
  const root = await mkdtemp(join(tmpdir(), "revit-mcp-next-recipes-"));
  const store = new ModelDeliveryRecipeStore(root);
  try {
    const first = await store.save(recipe("Project / Alpha"));
    assert.equal(first.changed, true);
    assert.match(first.recipeSha256, /^[a-f0-9]{64}$/);

    const retry = await store.save(recipe("Project / Alpha"));
    assert.equal(retry.changed, false);
    assert.equal(retry.recipeSha256, first.recipeSha256);
    assert.equal(retry.savedAtUtc, first.savedAtUtc);

    const updated = await store.save(recipe("Project / Alpha", "Issue-02"), first.recipeSha256.toUpperCase());
    assert.equal(updated.changed, true);
    assert.notEqual(updated.recipeSha256, first.recipeSha256);
    assert.equal((await store.get("Project / Alpha"))?.recipe.packageName, "Issue-02");

    await assert.rejects(
      () => store.save(recipe("Project / Alpha", "Issue-03"), first.recipeSha256),
      /Recipe conflict/
    );

    await store.save(recipe("Project Beta"));
    const listed = await store.list();
    assert.equal(listed.length, 2);
    assert.equal(listed.find((item) => item.projectId === "Project / Alpha")?.packageName, "Issue-02");
    assert.equal(await store.get("Missing Project"), undefined);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("broker recipe tools work without Revit and inspect automatically loads by projectId", async () => {
  const root = await mkdtemp(join(tmpdir(), "revit-mcp-next-recipe-tools-"));
  const server = createBrokerServer({
    bridge: new FakeRevitBridgeClient(),
    brokerVersion: "test",
    sessionId: "recipe-tool-test",
    recipeStore: new ModelDeliveryRecipeStore(root),
  });
  const client = new Client({ name: "recipe-tool-test", version: "1.0.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await server.connect(serverTransport);
  await client.connect(clientTransport);

  try {
    const saved = await client.callTool({ name: "revit.save_model_delivery_recipe", arguments: { recipe: recipe("Office Project") } }) as {
      isError?: boolean;
      structuredContent?: { data?: { changed?: boolean; recipeSha256?: string } };
    };
    assert.equal(saved.isError, undefined, JSON.stringify(saved));
    assert.equal(saved.structuredContent?.data?.changed, true);

    const loaded = await client.callTool({ name: "revit.get_model_delivery_recipe", arguments: { projectId: "Office Project" } }) as {
      isError?: boolean;
      structuredContent?: { data?: { found?: boolean; recipeSha256?: string } };
    };
    assert.equal(loaded.isError, undefined);
    assert.equal(loaded.structuredContent?.data?.found, true);
    assert.equal(loaded.structuredContent?.data?.recipeSha256, saved.structuredContent?.data?.recipeSha256);

    const listed = await client.callTool({ name: "revit.list_model_delivery_recipes", arguments: {} }) as {
      isError?: boolean;
      structuredContent?: { data?: { returnedCount?: number } };
    };
    assert.equal(listed.isError, undefined);
    assert.equal(listed.structuredContent?.data?.returnedCount, 1);

    const inspection = await client.callTool({
      name: "revit.inspect_model_delivery",
      arguments: { projectId: "Office Project", sourcePaths: ["C:\\Models\\Architecture.rvt"] },
    }) as { isError?: boolean; structuredContent?: { data?: { mode?: string } } };
    assert.equal(inspection.isError, undefined);
    assert.equal(inspection.structuredContent?.data?.mode, "repeat");
  } finally {
    await client.close();
    await server.close();
    await rm(root, { recursive: true, force: true });
  }
});
