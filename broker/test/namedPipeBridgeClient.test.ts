import test from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import { NamedPipeBridgeClient } from "../src/ipc/NamedPipeBridgeClient.js";
import { makeRequest } from "../src/ipc/RequestFactory.js";

test("named pipe bridge client preserves canonical request and parses framed response", async () => {
  const pipeName = `revit-mcp-next-test-${process.pid}-${Date.now()}`;
  const pipePath = `\\\\.\\pipe\\${pipeName}`;
  let receivedRequestId = "";

  const server = net.createServer((socket) => {
    let buffer = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.byteLength < 4) return;
      const length = buffer.readUInt32BE(0);
      if (buffer.byteLength < length + 4) return;

      const request = JSON.parse(buffer.subarray(4, length + 4).toString("utf8")) as {
        requestId: string;
        sessionId: string;
        operation: string;
        operationKind: string;
        payload: Record<string, unknown>;
      };
      receivedRequestId = request.requestId;
      assert.equal(request.sessionId, "pipe-test");
      assert.equal(request.operation, "status");
      assert.equal(request.operationKind, "read");
      assert.deepEqual(request.payload, {});

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
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
});
