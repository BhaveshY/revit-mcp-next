import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile, access } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { FakeRevitBridgeClient } from "../src/ipc/FakeRevitBridgeClient.js";
import { FileSystemRevitInstanceDirectory } from "../src/instances/FileSystemRevitInstanceDirectory.js";

test("filesystem discovery enumerates unique live Revit registrations without exposing secrets", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "revit-mcp-instances-"));
  const directory = new FileSystemRevitInstanceDirectory({
    sessionId: "directory-test",
    defaultTimeoutMs: 1000,
    fallbackBridge: new FakeRevitBridgeClient(),
    fallbackPipeName: "legacy-pipe",
    registryRoot: root,
  });
  try {
    await registration(root, "instance-a", "revit.pipe-instance-a", "2024", "secret-that-must-not-escape");
    await registration(root, "instance-b", "revit.pipe-instance-b", "2027", "another-secret");

    const connections = await directory.listConnections();
    assert.deepEqual(connections.map((item) => item.metadata.instanceId), ["instance-a", "instance-b"]);
    assert.deepEqual(connections.map((item) => item.metadata.pipeName), ["revit.pipe-instance-a", "revit.pipe-instance-b"]);
    assert.deepEqual(connections.map((item) => item.metadata.revitVersion), ["2024", "2027"]);
    for (const connection of connections) {
      assert.equal(connection.source, "registry");
      assert.equal("authToken" in connection.metadata, false);
      assert.equal(JSON.stringify(connection.metadata).includes("secret"), false);
    }
  } finally {
    directory.dispose();
    await rm(root, { recursive: true, force: true });
  }
});

test("filesystem discovery removes stale registrations and preserves legacy single-instance fallback", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "revit-mcp-stale-"));
  const stalePath = path.join(root, "stale-instance.json");
  const directory = new FileSystemRevitInstanceDirectory({
    sessionId: "directory-test",
    defaultTimeoutMs: 1000,
    fallbackBridge: new FakeRevitBridgeClient(),
    fallbackPipeName: "legacy-pipe",
    registryRoot: root,
    staleAfterMs: 100,
  });
  try {
    await writeFile(stalePath, JSON.stringify({
      schemaVersion: 1,
      instanceId: "stale-instance",
      pipeName: "stale-pipe",
      controlPipeName: "stale-pipe-control",
      processId: process.pid,
      lastSeenAtUtc: new Date(Date.now() - 60_000).toISOString(),
    }), "utf8");

    const connections = await directory.listConnections();
    assert.equal(connections.length, 1);
    assert.equal(connections[0].source, "legacy");
    assert.equal(connections[0].metadata.instanceId, "legacy-default");
    await assert.rejects(access(stalePath));
  } finally {
    directory.dispose();
    await rm(root, { recursive: true, force: true });
  }
});

test("discovered instance clients inherit pipe authentication without publishing it", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "revit-mcp-auth-instances-"));
  const previousToken = process.env.REVIT_MCP_NEXT_AUTH_TOKEN;
  process.env.REVIT_MCP_NEXT_AUTH_TOKEN = "directory-env-token";
  const directory = new FileSystemRevitInstanceDirectory({
    sessionId: "directory-auth-test",
    defaultTimeoutMs: 1000,
    fallbackBridge: new FakeRevitBridgeClient(),
    fallbackPipeName: "legacy-pipe",
    registryRoot: root,
  });
  try {
    await registration(root, "authenticated-instance", "authenticated-pipe", "2024", "metadata-secret");
    const connections = await directory.listConnections();
    assert.equal(connections.length, 1);
    assert.equal(
      (connections[0].bridge as unknown as { authToken?: string }).authToken,
      "directory-env-token"
    );
    assert.equal("authToken" in connections[0].metadata, false);
  } finally {
    directory.dispose();
    if (previousToken === undefined) delete process.env.REVIT_MCP_NEXT_AUTH_TOKEN;
    else process.env.REVIT_MCP_NEXT_AUTH_TOKEN = previousToken;
    await rm(root, { recursive: true, force: true });
  }
});

test("discovery ignores but does not delete a transiently incomplete heartbeat", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "revit-mcp-partial-instance-"));
  const partialPath = path.join(root, "partial-instance.json");
  const directory = new FileSystemRevitInstanceDirectory({
    sessionId: "directory-partial-test",
    defaultTimeoutMs: 1000,
    fallbackBridge: new FakeRevitBridgeClient(),
    fallbackPipeName: "legacy-pipe",
    registryRoot: root,
  });
  try {
    await writeFile(partialPath, '{"schemaVersion":1,"instanceId":', "utf8");
    const connections = await directory.listConnections();
    assert.equal(connections[0].source, "legacy");
    await access(partialPath);
  } finally {
    directory.dispose();
    await rm(root, { recursive: true, force: true });
  }
});

async function registration(
  root: string,
  instanceId: string,
  pipeName: string,
  revitVersion: string,
  authToken: string
): Promise<void> {
  await writeFile(path.join(root, `${instanceId}.json`), JSON.stringify({
    schemaVersion: 1,
    instanceId,
    pipeName,
    controlPipeName: `${pipeName}-control`,
    processId: process.pid,
    revitVersion,
    revitBuild: `${revitVersion}.test`,
    addinVersion: "test",
    startedAtUtc: new Date(Date.now() - 1000).toISOString(),
    lastSeenAtUtc: new Date().toISOString(),
    authToken,
  }), "utf8");
}
