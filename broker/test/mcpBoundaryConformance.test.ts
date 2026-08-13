import test from "node:test";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import {
  Client,
  InMemoryTransport,
  type ClientOptions,
} from "@modelcontextprotocol/client";
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio";
import {
  ReadBuffer,
  STDIO_DEFAULT_MAX_BUFFER_SIZE,
  serializeMessage,
} from "@modelcontextprotocol/server";
import type { BridgeResponse, RevitStatus } from "@revit-mcp-next/contracts";
import {
  BROKER_DISCOVERY_CACHE_TTL_MS,
  BROKER_MCP_PROTOCOL_VERSIONS,
  createBrokerServer,
} from "../src/server.js";
import { FakeRevitBridgeClient } from "../src/ipc/FakeRevitBridgeClient.js";

const MODERN_PROTOCOL_VERSION = "2026-07-28";
const LEGACY_PROTOCOL_VERSION = "2025-11-25";

type SurfaceSnapshot = {
  tools: string[];
  resources: string[];
  resourceTemplates: string[];
  prompts: string[];
};

async function connectClient(
  sessionId: string,
  options: ClientOptions,
  bridge = new FakeRevitBridgeClient()
) {
  const server = createBrokerServer({
    bridge,
    brokerVersion: "boundary-test",
    sessionId,
  });
  const client = new Client(
    { name: `boundary-client-${sessionId}`, version: "1.0.0" },
    options
  );
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();

  await server.connect(serverTransport);
  await client.connect(clientTransport);

  return { bridge, client, server };
}

async function closeConnection(connection: Awaited<ReturnType<typeof connectClient>>) {
  await connection.client.close();
  await connection.server.close();
}

async function connectModernStdio() {
  const client = new Client(
    { name: "boundary-client-modern-stdio", version: "1.0.0" },
    {
      supportedProtocolVersions: BROKER_MCP_PROTOCOL_VERSIONS,
      versionNegotiation: { mode: { pin: MODERN_PROTOCOL_VERSION } },
    }
  );
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL("../src/index.js", import.meta.url))],
    cwd: process.cwd(),
    stderr: "pipe",
  });

  await client.connect(transport, { timeout: 10_000 });
  return { client, transport };
}

async function readSurface(client: Client): Promise<SurfaceSnapshot> {
  const [tools, resources, resourceTemplates, prompts] = await Promise.all([
    client.listTools(undefined, { cacheMode: "bypass" }),
    client.listResources(undefined, { cacheMode: "bypass" }),
    client.listResourceTemplates(undefined, { cacheMode: "bypass" }),
    client.listPrompts(undefined, { cacheMode: "bypass" }),
  ]);

  return {
    tools: tools.tools.map((tool) => tool.name),
    resources: resources.resources.map((resource) => resource.uri),
    resourceTemplates: resourceTemplates.resourceTemplates.map(
      (resourceTemplate) => resourceTemplate.uriTemplate
    ),
    prompts: prompts.prompts.map((prompt) => prompt.name),
  };
}

function assertUnique(values: string[], label: string): void {
  assert.equal(new Set(values).size, values.length, `${label} must not contain duplicates`);
}

function assertAdvertisedSurfaceCapabilities(client: Client): void {
  const capabilities = client.getServerCapabilities();
  assert.ok(capabilities?.tools, "server must advertise tools");
  assert.ok(capabilities?.resources, "server must advertise resources");
  assert.ok(capabilities?.prompts, "server must advertise prompts");
  assert.ok(capabilities?.completions, "server must advertise completions");
  assert.equal(capabilities.tools.listChanged, true);
  assert.equal(capabilities.resources.listChanged, true);
  assert.equal(capabilities.prompts.listChanged, true);
}

test("broker negotiates both modern 2026-07-28 and legacy MCP eras", async (t) => {
  const modern = await connectModernStdio();
  t.after(() => modern.client.close());

  assert.equal(modern.client.getProtocolEra(), "modern");
  assert.equal(modern.client.getNegotiatedProtocolVersion(), MODERN_PROTOCOL_VERSION);
  assert.ok(
    modern.client.getDiscoverResult()?.supportedVersions.includes(MODERN_PROTOCOL_VERSION),
    "server/discover must advertise the stable modern version"
  );
  assertAdvertisedSurfaceCapabilities(modern.client);
  const modernTools = await modern.client.listTools(undefined, { cacheMode: "bypass" });
  assert.equal(modernTools.ttlMs, BROKER_DISCOVERY_CACHE_TTL_MS);
  assert.equal(modernTools.cacheScope, "private");

  const legacy = await connectClient("legacy-era", {
    supportedProtocolVersions: [LEGACY_PROTOCOL_VERSION],
    versionNegotiation: { mode: "legacy" },
  });
  t.after(() => closeConnection(legacy));

  assert.equal(legacy.client.getProtocolEra(), "legacy");
  assert.equal(legacy.client.getNegotiatedProtocolVersion(), LEGACY_PROTOCOL_VERSION);
  assert.equal(legacy.client.getDiscoverResult(), undefined);
  assertAdvertisedSurfaceCapabilities(legacy.client);

  assert.deepEqual(await readSurface(modern.client), await readSurface(legacy.client));
});

test("tool, resource, template, and prompt ordering is deterministic across fresh servers", async (t) => {
  const first = await connectClient("surface-first", {
    supportedProtocolVersions: [LEGACY_PROTOCOL_VERSION],
    versionNegotiation: { mode: "legacy" },
  });
  t.after(() => closeConnection(first));
  const second = await connectClient("surface-second", {
    supportedProtocolVersions: [LEGACY_PROTOCOL_VERSION],
    versionNegotiation: { mode: "legacy" },
  });
  t.after(() => closeConnection(second));

  const firstSnapshot = await readSurface(first.client);
  const secondSnapshot = await readSurface(second.client);

  assert.deepEqual(secondSnapshot, firstSnapshot);
  assert.ok(firstSnapshot.tools.includes("revit.status"));
  assert.ok(firstSnapshot.resources.includes("revit://discovery"));
  assert.ok(firstSnapshot.resourceTemplates.includes("revit://tools/{name}"));
  assert.deepEqual(firstSnapshot.prompts, ["revit.start_workflow", "revit.workflow"]);
  assertUnique(firstSnapshot.tools, "tools");
  assertUnique(firstSnapshot.resources, "resources");
  assertUnique(firstSnapshot.resourceTemplates, "resource templates");
  assertUnique(firstSnapshot.prompts, "prompts");
});

test("MCP cancellation reaches the bridge AbortSignal without replaying the tool", async (t) => {
  const bridge = new FakeRevitBridgeClient();
  let statusCallCount = 0;
  let explicitCancelToolCallCount = 0;
  let bridgeSignal: AbortSignal | undefined;
  let markStatusStarted!: () => void;
  let markBridgeAborted!: () => void;
  const statusStarted = new Promise<void>((resolve) => {
    markStatusStarted = resolve;
  });
  const bridgeAborted = new Promise<void>((resolve) => {
    markBridgeAborted = resolve;
  });

  bridge.status = async (_request, options) => {
    statusCallCount += 1;
    bridgeSignal = options?.signal;
    markStatusStarted();

    return await new Promise<BridgeResponse<RevitStatus>>((_resolve, reject) => {
      const rejectAborted = () => {
        markBridgeAborted();
        reject(new Error("bridge observed MCP cancellation"));
      };
      if (bridgeSignal?.aborted) {
        rejectAborted();
        return;
      }
      bridgeSignal?.addEventListener("abort", rejectAborted, { once: true });
    });
  };

  const originalCancel = bridge.cancel.bind(bridge);
  bridge.cancel = async (...args) => {
    explicitCancelToolCallCount += 1;
    return await originalCancel(...args);
  };

  const connection = await connectClient(
    "cancel-propagation",
    {
      supportedProtocolVersions: [LEGACY_PROTOCOL_VERSION],
      versionNegotiation: { mode: "legacy" },
    },
    bridge
  );
  t.after(() => closeConnection(connection));

  const controller = new AbortController();
  const call = connection.client.callTool(
    { name: "revit.status", arguments: {} },
    { signal: controller.signal }
  );

  await statusStarted;
  assert.equal(statusCallCount, 1);
  assert.equal(bridgeSignal?.aborted, false);

  controller.abort(new Error("cancel boundary test"));
  await assert.rejects(call);
  await bridgeAborted;
  await new Promise<void>((resolve) => setImmediate(resolve));

  assert.equal(bridgeSignal?.aborted, true);
  assert.equal(statusCallCount, 1, "the cancelled status operation must not be replayed");
  assert.equal(
    explicitCancelToolCallCount,
    0,
    "protocol cancellation must not be confused with revit.cancel_request"
  );
});

test("stdio framing preserves a valid two MiB JSON-RPC message", () => {
  const text = "x".repeat(2 * 1024 * 1024);
  const encoded = Buffer.from(
    serializeMessage({
      jsonrpc: "2.0",
      id: 1,
      method: "tools/call",
      params: {
        name: "revit.query",
        arguments: { boundaryPayload: text },
      },
    })
  );

  assert.ok(
    STDIO_DEFAULT_MAX_BUFFER_SIZE >= encoded.byteLength,
    "SDK default stdio buffer must accommodate the regression frame"
  );
  const reader = new ReadBuffer();
  reader.append(encoded.subarray(0, 913_777));
  assert.equal(reader.readMessage(), null, "a partial frame must not be parsed");
  reader.append(encoded.subarray(913_777));

  const parsed = reader.readMessage();
  assert.ok(parsed && "params" in parsed);
  const params = parsed.params as {
    arguments?: { boundaryPayload?: string };
  };
  assert.equal(params.arguments?.boundaryPayload?.length, text.length);
  assert.equal(reader.readMessage(), null, "the frame must be consumed exactly once");
});
