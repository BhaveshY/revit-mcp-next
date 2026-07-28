import test from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import { NamedPipeBridgeClient } from "../src/ipc/NamedPipeBridgeClient.js";
import { makeRequest } from "../src/ipc/RequestFactory.js";

const maxBridgeFrameBytes = 4 * 1024 * 1024;

interface CapturedBridgeRequest {
  requestId: string;
  sessionId: string;
  authToken?: string;
  operation: string;
  operationKind: string;
  payload: Record<string, unknown>;
}

test("named pipe bridge client preserves canonical request and parses framed response", async () => {
  let receivedRequestId = "";

  await withStatusPipeServer(
    (request) => {
      receivedRequestId = request.requestId;
      assert.equal(request.sessionId, "pipe-test");
      assert.equal(request.authToken, undefined);
      assert.equal(request.operation, "status");
      assert.equal(request.operationKind, "read");
      assert.deepEqual(request.payload, {});
    },
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 2000,
      });
      const request = makeRequest("pipe-test", "status", "read", {}, 2000);
      const response = await client.status(request);

      assert.equal(response.ok, true);
      if (!response.ok) return;
      assert.equal(response.requestId, receivedRequestId);
      assert.equal(response.requestId, request.requestId);
      assert.equal(response.data.connected, true);
      assert.deepEqual(response.metrics, { elapsedMs: 1 });
    }
  );
});

test("named pipe bridge client sends auth token from environment when configured", async () => {
  const previousAuthToken = process.env.REVIT_MCP_NEXT_AUTH_TOKEN;
  process.env.REVIT_MCP_NEXT_AUTH_TOKEN = "env-auth-token";

  try {
    await withStatusPipeServer(
      (request) => {
        assert.equal(request.authToken, "env-auth-token");
      },
      async (pipeName) => {
        const client = new NamedPipeBridgeClient({
          pipeName,
          sessionId: "pipe-test",
          defaultTimeoutMs: 2000,
        });
        const response = await client.status(makeRequest("pipe-test", "status", "read", {}, 2000));

        assert.equal(response.ok, true);
      }
    );
  } finally {
    restoreEnvAuthToken(previousAuthToken);
  }
});

test("named pipe bridge client sends auth token from client config over environment", async () => {
  const previousAuthToken = process.env.REVIT_MCP_NEXT_AUTH_TOKEN;
  process.env.REVIT_MCP_NEXT_AUTH_TOKEN = "env-auth-token";

  try {
    await withStatusPipeServer(
      (request) => {
        assert.equal(request.authToken, "configured-auth-token");
      },
      async (pipeName) => {
        const client = new NamedPipeBridgeClient({
          pipeName,
          sessionId: "pipe-test",
          defaultTimeoutMs: 2000,
          authToken: "configured-auth-token",
        });
        const response = await client.status(makeRequest("pipe-test", "status", "read", {}, 2000));

        assert.equal(response.ok, true);
      }
    );
  } finally {
    restoreEnvAuthToken(previousAuthToken);
  }
});

test("named pipe bridge client rejects oversized response frames", async () => {
  await withRawPipeServer(
    () => undefined,
    (socket) => {
      const header = Buffer.allocUnsafe(4);
      header.writeUInt32BE(maxBridgeFrameBytes + 1, 0);
      socket.write(header);
    },
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 2000,
      });
      const response = await client.status(makeRequest("pipe-test", "status", "read", {}, 2000));

      assert.equal(response.ok, false);
      if (response.ok) return;
      assert.equal(response.error.code, "BRIDGE_FRAME_TOO_LARGE");
      assert.match(response.error.message, /above the 4194304 byte limit/);
    }
  );
});

test("named pipe bridge client handles a pre-aborted request without rejecting", async () => {
  const client = new NamedPipeBridgeClient({
    pipeName: uniquePipeName(),
    sessionId: "pipe-test",
    defaultTimeoutMs: 2000,
  });
  const controller = new AbortController();
  controller.abort();

  const response = await client.status(
    makeRequest("pipe-test", "status", "read", {}, 2000),
    { signal: controller.signal }
  );

  assert.equal(response.ok, false);
  if (response.ok) return;
  assert.equal(response.error.code, "REQUEST_CANCELLED");
});

test("named pipe bridge client reports an early peer close immediately and does not retry after connect", async () => {
  let connectionCount = 0;
  const startedAt = Date.now();

  await withRawPipeServer(
    () => undefined,
    (socket) => socket.end(),
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 2000,
      });
      const response = await client.status(makeRequest("pipe-test", "status", "read", {}, 2000));

      assert.equal(response.ok, false);
      if (response.ok) return;
      assert.equal(response.error.code, "BRIDGE_DISCONNECTED");
      assert.ok(Date.now() - startedAt < 1000, "early close should not wait for the bridge timeout");
      await delay(100);
      assert.equal(connectionCount, 1, "a request must not be replayed after the pipe connected");
    },
    () => {
      connectionCount++;
    }
  );
});

test("named pipe bridge client retries transient pre-connect failures inside the original deadline", async () => {
  const pipeName = uniquePipeName();
  const client = new NamedPipeBridgeClient({
    pipeName,
    sessionId: "pipe-test",
    defaultTimeoutMs: 2000,
  });
  const responsePromise = client.status(makeRequest("pipe-test", "status", "read", {}, 2000));

  await delay(100);
  const server = await startRawPipeServer(
    pipeName,
    () => undefined,
    writeStatusResponse
  );

  try {
    const response = await responsePromise;
    assert.equal(response.ok, true);
  } finally {
    await closeServer(server);
  }
});

test("named pipe bridge client parses fragmented response headers and payloads", async () => {
  await withRawPipeServer(
    () => undefined,
    (socket, request) => {
      const frame = statusResponseFrame(request);
      const boundaries = [1, 2, 4, 7, 13, 29, 61, frame.byteLength];
      writeFragments(socket, frame, boundaries);
    },
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 2000,
      });
      const response = await client.status(makeRequest("pipe-test", "status", "read", {}, 2000));

      assert.equal(response.ok, true);
      if (!response.ok) return;
      assert.equal(response.data.connected, true);
    }
  );
});

test("named pipe bridge client validates its default timeout", () => {
  assert.throws(
    () =>
      new NamedPipeBridgeClient({
        pipeName: uniquePipeName(),
        sessionId: "pipe-test",
        defaultTimeoutMs: Number.NaN,
      }),
    /defaultTimeoutMs must be a positive integer/
  );
});

test("named pipe bridge client disposal resolves pending connection attempts", async () => {
  const client = new NamedPipeBridgeClient({
    pipeName: uniquePipeName(),
    sessionId: "pipe-test",
    defaultTimeoutMs: 5000,
  });
  const responsePromise = client.status(makeRequest("pipe-test", "status", "read", {}, 5000));

  await delay(25);
  client.dispose();
  const response = await responsePromise;

  assert.equal(response.ok, false);
  if (response.ok) return;
  assert.equal(response.error.code, "BRIDGE_DISPOSED");
});

async function withStatusPipeServer(
  onRequest: (request: CapturedBridgeRequest) => void,
  runClient: (pipeName: string) => Promise<void>
): Promise<void> {
  await withRawPipeServer(
    onRequest,
    writeStatusResponse,
    runClient
  );
}

async function withRawPipeServer(
  onRequest: (request: CapturedBridgeRequest) => void,
  writeResponse: (socket: net.Socket, request: CapturedBridgeRequest) => void,
  runClient: (pipeName: string) => Promise<void>,
  onConnection?: () => void
): Promise<void> {
  const pipeName = uniquePipeName();
  const server = await startRawPipeServer(pipeName, onRequest, writeResponse, onConnection);

  try {
    await runClient(pipeName);
  } finally {
    await closeServer(server);
  }
}

async function startRawPipeServer(
  pipeName: string,
  onRequest: (request: CapturedBridgeRequest) => void,
  writeResponse: (socket: net.Socket, request: CapturedBridgeRequest) => void,
  onConnection?: () => void
): Promise<net.Server> {
  const pipePath = `\\\\.\\pipe\\${pipeName}`;
  const server = net.createServer((socket) => {
    onConnection?.();
    let buffer = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.byteLength < 4) return;
      const length = buffer.readUInt32BE(0);
      if (buffer.byteLength < length + 4) return;

      const request = JSON.parse(buffer.subarray(4, length + 4).toString("utf8")) as CapturedBridgeRequest;
      onRequest(request);
      writeResponse(socket, request);
    });
  });

  await new Promise<void>((resolve) => server.listen(pipePath, resolve));
  return server;
}

function writeStatusResponse(socket: net.Socket, request: CapturedBridgeRequest): void {
  socket.write(statusResponseFrame(request));
}

function statusResponseFrame(request: CapturedBridgeRequest): Buffer {
  const response = Buffer.from(
    JSON.stringify({
      ok: true,
      requestId: request.requestId,
      data: {
        connected: true,
        brokerVersion: "test",
        protocolVersion: "2026-06-23",
        capabilities: ["status"],
        warnings: [],
      },
      warnings: [],
      metrics: { elapsedMs: 1 },
    }),
    "utf8"
  );
  const header = Buffer.allocUnsafe(4);
  header.writeUInt32BE(response.byteLength, 0);
  return Buffer.concat([header, response]);
}

function writeFragments(socket: net.Socket, frame: Buffer, boundaries: number[]): void {
  let offset = 0;
  const writeNext = () => {
    if (offset >= frame.byteLength || socket.destroyed) return;
    const boundary = boundaries.find((value) => value > offset) ?? frame.byteLength;
    socket.write(frame.subarray(offset, boundary));
    offset = boundary;
    setImmediate(writeNext);
  };
  writeNext();
}

function uniquePipeName(): string {
  return `revit-mcp-next-test-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

async function closeServer(server: net.Server): Promise<void> {
  await new Promise<void>((resolve) => server.close(() => resolve()));
}

async function delay(ms: number): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, ms));
}

function restoreEnvAuthToken(value: string | undefined): void {
  if (value === undefined) {
    delete process.env.REVIT_MCP_NEXT_AUTH_TOKEN;
    return;
  }
  process.env.REVIT_MCP_NEXT_AUTH_TOKEN = value;
}
