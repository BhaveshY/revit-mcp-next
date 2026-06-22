import test from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import { NamedPipeBridgeClient } from "../src/ipc/NamedPipeBridgeClient.js";
import { makeRequest } from "../src/ipc/RequestFactory.js";

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

async function withStatusPipeServer(
  onRequest: (request: CapturedBridgeRequest) => void,
  runClient: (pipeName: string) => Promise<void>
): Promise<void> {
  const pipeName = `revit-mcp-next-test-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const pipePath = `\\\\.\\pipe\\${pipeName}`;

  const server = net.createServer((socket) => {
    let buffer = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.byteLength < 4) return;
      const length = buffer.readUInt32BE(0);
      if (buffer.byteLength < length + 4) return;

      const request = JSON.parse(buffer.subarray(4, length + 4).toString("utf8")) as CapturedBridgeRequest;
      onRequest(request);

      const response = Buffer.from(
        JSON.stringify({
          ok: true,
          requestId: request.requestId,
          data: {
            connected: true,
            brokerVersion: "test",
            protocolVersion: "2026-06-22",
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
      socket.write(Buffer.concat([header, response]));
    });
  });

  await new Promise<void>((resolve) => server.listen(pipePath, resolve));

  try {
    await runClient(pipeName);
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
}

function restoreEnvAuthToken(value: string | undefined): void {
  if (value === undefined) {
    delete process.env.REVIT_MCP_NEXT_AUTH_TOKEN;
    return;
  }
  process.env.REVIT_MCP_NEXT_AUTH_TOKEN = value;
}
