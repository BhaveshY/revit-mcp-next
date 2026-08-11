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
      assert.deepEqual(response.metrics, {
        elapsedMs: 1,
        queueWaitMs: 2,
        revitExecutionMs: 3,
      });
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

test("named pipe bridge client rejects malformed response envelopes", async () => {
  await withRawPipeServer(
    () => undefined,
    (socket, request) => {
      writeJsonResponse(socket, {
        ok: true,
        requestId: request.requestId,
        warnings: [],
        metrics: { elapsedMs: 1 },
      });
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
      assert.equal(response.error.code, "BRIDGE_PROTOCOL_ERROR");
      assert.match(response.error.message, /data/);
    }
  );
});

test("named pipe bridge client rejects invalid queued Revit phase metrics", async () => {
  await withRawPipeServer(
    () => undefined,
    (socket, request) => {
      const response = statusResponse(request.requestId);
      response.metrics = { elapsedMs: 1, queueWaitMs: -1, revitExecutionMs: 3 };
      writeJsonResponse(socket, response);
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
      assert.equal(response.error.code, "BRIDGE_PROTOCOL_ERROR");
      assert.match(response.error.message, /metrics/);
    }
  );
});

test("named pipe bridge client rejects response request IDs from another request", async () => {
  await withRawPipeServer(
    () => undefined,
    (socket, request) => {
      writeJsonResponse(socket, statusResponse(`${request.requestId}-other`));
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
      assert.equal(response.error.code, "BRIDGE_REQUEST_ID_MISMATCH");
    }
  );
});

test("named pipe bridge client sends bridge health to the default control pipe", async () => {
  const pipeName = uniquePipeName();
  const controlOperations: string[] = [];
  const controlServer = await startRawPipeServer(
    `${pipeName}-control`,
    (request) => controlOperations.push(request.operation),
    (socket, request) => writeJsonResponse(socket, bridgeHealthResponse(request.requestId, pipeName))
  );

  try {
    const client = new NamedPipeBridgeClient({
      pipeName,
      sessionId: "pipe-test",
      defaultTimeoutMs: 2000,
    });
    const response = await client.bridgeHealth(
      makeRequest("pipe-test", "bridge_health", "debug", {}, 2000)
    );

    assert.equal(response.ok, true);
    if (!response.ok) return;
    assert.equal(response.data.healthy, true);
    assert.deepEqual(controlOperations, ["bridge_health"]);
  } finally {
    await closeServer(controlServer);
  }
});

test("named pipe bridge client sends explicit control operations to the configured control pipe", async () => {
  const pipeName = uniquePipeName();
  const controlPipeName = uniquePipeName();
  const primaryOperations: string[] = [];
  const controlOperations: string[] = [];
  const primaryServer = await startRawPipeServer(
    pipeName,
    (request) => primaryOperations.push(request.operation),
    writeStatusResponse
  );
  const controlServer = await startRawPipeServer(
    controlPipeName,
    (request) => controlOperations.push(request.operation),
    (socket, request) => {
      if (request.operation === "cancel_request") {
        writeJsonResponse(socket, {
          ok: true,
          requestId: request.requestId,
          data: { cancelled: false, requestId: request.payload.requestId, message: "Nothing queued." },
          warnings: [],
          metrics: { elapsedMs: 1 },
        });
        return;
      }
      writeJsonResponse(socket, {
        ok: true,
        requestId: request.requestId,
        data: { found: false, state: "failed" },
        warnings: [],
        metrics: { elapsedMs: 1 },
      });
    }
  );

  try {
    const client = new NamedPipeBridgeClient({
      pipeName,
      controlPipeName,
      sessionId: "pipe-test",
      defaultTimeoutMs: 2000,
    });
    const cancel = await client.cancel(
      makeRequest("pipe-test", "cancel_request", "debug", { requestId: "target" }, 2000)
    );
    const lookup = await client.getRequestResult(
      makeRequest("pipe-test", "get_request_result", "debug", { requestId: "target" }, 2000)
    );

    assert.equal(cancel.ok, true);
    assert.equal(lookup.ok, true);
    if (lookup.ok) {
      assert.equal(lookup.data.found, false);
      assert.equal(lookup.data.state, "failed");
    }
    assert.deepEqual(primaryOperations, []);
    assert.deepEqual(controlOperations, ["cancel_request", "get_request_result"]);
  } finally {
    await closeServer(primaryServer);
    await closeServer(controlServer);
  }
});

test("named pipe bridge client rejects a request-result entry for another original request", async () => {
  await withRawPipeServer(
    () => undefined,
    (socket, request) => {
      writeJsonResponse(socket, {
        ok: true,
        requestId: request.requestId,
        data: {
          found: true,
          state: "accepted",
          requestId: "different-original-request",
          operation: "apply_change_set",
          acceptedAtUtc: "2026-08-11T12:00:00.000Z",
        },
        warnings: [],
        metrics: { elapsedMs: 1 },
      });
    },
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        controlPipeName: pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 2000,
      });
      const response = await client.getRequestResult(
        makeRequest("pipe-test", "get_request_result", "debug", { requestId: "expected-original-request" }, 2000)
      );

      assert.equal(response.ok, false);
      if (response.ok) return;
      assert.equal(response.error.code, "BRIDGE_REQUEST_ID_MISMATCH");
    }
  );
});

test("named pipe bridge client sends best-effort control cancellation after an in-flight read is aborted", async () => {
  const pipeName = uniquePipeName();
  const controlPipeName = uniquePipeName();
  let markPrimarySeen: (() => void) | undefined;
  let markCancelSeen: (() => void) | undefined;
  const primarySeen = new Promise<void>((resolve) => { markPrimarySeen = resolve; });
  const cancelSeen = new Promise<void>((resolve) => { markCancelSeen = resolve; });
  let cancelledRequestId = "";
  const primaryServer = await startRawPipeServer(
    pipeName,
    () => markPrimarySeen?.(),
    () => undefined
  );
  const controlServer = await startRawPipeServer(
    controlPipeName,
    () => undefined,
    (socket, request) => {
      assert.equal(request.operation, "cancel_request");
      cancelledRequestId = String(request.payload.requestId);
      markCancelSeen?.();
      writeJsonResponse(socket, {
        ok: true,
        requestId: request.requestId,
        data: { cancelled: true, requestId: cancelledRequestId, message: "Cancelled." },
        warnings: [],
        metrics: { elapsedMs: 1 },
      });
    }
  );

  try {
    const client = new NamedPipeBridgeClient({
      pipeName,
      controlPipeName,
      sessionId: "pipe-test",
      defaultTimeoutMs: 2000,
    });
    const controller = new AbortController();
    const request = makeRequest("pipe-test", "status", "read", {}, 2000);
    const responsePromise = client.status(request, { signal: controller.signal });
    await primarySeen;
    controller.abort();

    const response = await responsePromise;
    await cancelSeen;
    assert.equal(response.ok, false);
    if (response.ok) return;
    assert.equal(response.error.code, "REQUEST_CANCELLED");
    assert.equal(cancelledRequestId, request.requestId);
  } finally {
    await closeServer(primaryServer);
    await closeServer(controlServer);
  }
});

test("named pipe bridge client reconciles an aborted mutation instead of reporting a clean cancellation", async () => {
  const pipeName = uniquePipeName();
  const controlPipeName = uniquePipeName();
  const controlOperations: string[] = [];
  let markPrimarySeen: (() => void) | undefined;
  const primarySeen = new Promise<void>((resolve) => { markPrimarySeen = resolve; });
  let originalRequestId = "";
  const primaryServer = await startRawPipeServer(
    pipeName,
    (request) => {
      originalRequestId = request.requestId;
      markPrimarySeen?.();
    },
    () => undefined
  );
  const controlServer = await startRawPipeServer(
    controlPipeName,
    (request) => controlOperations.push(request.operation),
    (socket, request) => {
      assert.equal(request.payload.requestId, originalRequestId);
      if (request.operation === "cancel_request") {
        writeJsonResponse(socket, {
          ok: true,
          requestId: request.requestId,
          data: { cancelled: false, requestId: originalRequestId, message: "Already running." },
          warnings: [],
          metrics: { elapsedMs: 1 },
        });
        return;
      }
      assert.equal(request.operation, "get_request_result");
      writeJsonResponse(socket, {
        ok: true,
        requestId: request.requestId,
        data: {
          found: true,
          state: "committed",
          requestId: originalRequestId,
          operation: "apply_change_set",
          acceptedAtUtc: "2026-08-11T12:00:00.000Z",
          completedAtUtc: "2026-08-11T12:00:01.000Z",
          response: {
            ok: true,
            requestId: originalRequestId,
            data: { applied: true },
            warnings: [],
            metrics: { elapsedMs: 4 },
            generation: 2,
          },
        },
        warnings: [],
        metrics: { elapsedMs: 1 },
      });
    }
  );

  try {
    const client = new NamedPipeBridgeClient({
      pipeName,
      controlPipeName,
      sessionId: "pipe-test",
      defaultTimeoutMs: 2000,
    });
    const controller = new AbortController();
    const request = makeRequest(
      "pipe-test",
      "apply_change_set",
      "write",
      { transactionName: "Abort-safe write", operations: [], previewId: "preview", confirm: true },
      2000
    );
    const responsePromise = client.raw<{ applied?: boolean }>(request, { signal: controller.signal });
    await primarySeen;
    controller.abort();

    const response = await responsePromise;
    assert.equal(response.ok, true);
    if (!response.ok) return;
    assert.equal(response.data.applied, true);
    assert.deepEqual(controlOperations, ["cancel_request", "get_request_result"]);
  } finally {
    await closeServer(primaryServer);
    await closeServer(controlServer);
  }
});

test("named pipe bridge client reconciles a committed write after its response is lost", async () => {
  const receivedOperations: string[] = [];
  let originalRequestId = "";
  let lookupCount = 0;

  await withRawPipeServer(
    (request) => {
      receivedOperations.push(request.operation);
    },
    (socket, request) => {
      if (request.operation === "apply_change_set") {
        originalRequestId = request.requestId;
        socket.destroy();
        return;
      }

      assert.equal(request.operation, "get_request_result");
      assert.equal(request.operationKind, "debug");
      assert.equal(request.payload.requestId, originalRequestId);
      lookupCount += 1;
      if (lookupCount === 1) {
        writeJsonResponse(socket, {
          ok: true,
          requestId: request.requestId,
          data: { found: false, state: "failed" },
          warnings: [],
          metrics: { elapsedMs: 1 },
        });
        return;
      }
      writeJsonResponse(socket, {
        ok: true,
        requestId: request.requestId,
        data: {
          found: true,
          state: "committed",
          requestId: originalRequestId,
          operation: "apply_change_set",
          acceptedAtUtc: "2026-08-11T12:00:00.000Z",
          completedAtUtc: "2026-08-11T12:00:01.000Z",
          response: {
            ok: true,
            requestId: originalRequestId,
            data: {
              previewId: "preview-1",
              documentFingerprint: "doc-1",
              changeSetHash: "sha256:test",
              baseGeneration: 1,
              transactionName: "Recovered write",
              applied: true,
              changedCount: 1,
              changes: [],
            },
            warnings: [],
            metrics: { elapsedMs: 4 },
            generation: 2,
          },
        },
        warnings: [],
        metrics: { elapsedMs: 1 },
      });
    },
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        controlPipeName: pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 2000,
      });
      const request = makeRequest(
        "pipe-test",
        "apply_change_set",
        "write",
        {
          transactionName: "Recovered write",
          operations: [],
          previewId: "preview-1",
          confirm: true,
        },
        2000
      );
      const response = await client.raw<{ applied?: boolean }>(request);

      assert.equal(response.ok, true);
      if (!response.ok) return;
      assert.equal(response.requestId, originalRequestId);
      assert.equal(response.data.applied, true);
      assert.equal(response.warnings.at(-1)?.code, "BRIDGE_RESPONSE_RECOVERED");
      assert.deepEqual(receivedOperations, ["apply_change_set", "get_request_result", "get_request_result"]);
    }
  );
});

test("named pipe bridge client reports an unresolved write without replaying it", async () => {
  const receivedOperations: string[] = [];
  let originalRequestId = "";

  await withRawPipeServer(
    (request) => {
      receivedOperations.push(request.operation);
    },
    (socket, request) => {
      if (request.operation === "apply_change_set") {
        originalRequestId = request.requestId;
        socket.destroy();
        return;
      }

      assert.equal(request.operation, "get_request_result");
      assert.equal(request.payload.requestId, originalRequestId);
      writeJsonResponse(socket, {
        ok: true,
        requestId: request.requestId,
        data: { found: false, state: "failed" },
        warnings: [],
        metrics: { elapsedMs: 1 },
      });
    },
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        controlPipeName: pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 100,
      });
      const request = makeRequest(
        "pipe-test",
        "apply_change_set",
        "write",
        {
          transactionName: "Unknown write",
          operations: [],
          previewId: "preview-2",
          confirm: true,
        },
        2000
      );
      const response = await client.raw(request);

      assert.equal(response.ok, false);
      if (response.ok) return;
      assert.equal(response.error.code, "BRIDGE_WRITE_OUTCOME_UNKNOWN");
      assert.equal(response.error.details?.requestId, originalRequestId);
      assert.match(response.error.suggestedNextAction ?? "", /Do not retry/i);
      assert.deepEqual(receivedOperations, ["apply_change_set", "get_request_result"]);
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

test("named pipe bridge client disposal preserves clean disposal for a sent read", async () => {
  let markRequestSeen: (() => void) | undefined;
  const requestSeen = new Promise<void>((resolve) => { markRequestSeen = resolve; });

  await withRawPipeServer(
    () => markRequestSeen?.(),
    () => undefined,
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 5000,
      });
      const responsePromise = client.status(makeRequest("pipe-test", "status", "read", {}, 5000));

      await requestSeen;
      client.dispose();
      const response = await responsePromise;

      assert.equal(response.ok, false);
      if (response.ok) return;
      assert.equal(response.error.code, "BRIDGE_DISPOSED");
      assert.equal(response.error.recoverable, true);
    }
  );
});

test("named pipe bridge client disposal reports a sent mutation as unknown without replaying it", async () => {
  const receivedOperations: string[] = [];
  let markRequestSeen: (() => void) | undefined;
  const requestSeen = new Promise<void>((resolve) => { markRequestSeen = resolve; });

  await withRawPipeServer(
    (request) => {
      receivedOperations.push(request.operation);
      markRequestSeen?.();
    },
    () => undefined,
    async (pipeName) => {
      const client = new NamedPipeBridgeClient({
        pipeName,
        controlPipeName: pipeName,
        sessionId: "pipe-test",
        defaultTimeoutMs: 5000,
      });
      const request = makeRequest(
        "pipe-test",
        "apply_change_set",
        "write",
        { transactionName: "Dispose-safe write", operations: [], previewId: "preview", confirm: true },
        5000
      );
      const responsePromise = client.raw(request);

      await requestSeen;
      client.dispose();
      const response = await responsePromise;

      assert.equal(response.ok, false);
      if (response.ok) return;
      assert.equal(response.error.code, "BRIDGE_WRITE_OUTCOME_UNKNOWN");
      assert.equal(response.error.recoverable, false);
      assert.equal(response.error.details?.requestId, request.requestId);
      assert.equal(response.error.details?.transportCode, "BRIDGE_DISPOSED");
      assert.match(response.error.suggestedNextAction ?? "", /Do not retry/i);
      await delay(25);
      assert.deepEqual(receivedOperations, ["apply_change_set"]);
    }
  );
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
  const response = Buffer.from(JSON.stringify(statusResponse(request.requestId)), "utf8");
  const header = Buffer.allocUnsafe(4);
  header.writeUInt32BE(response.byteLength, 0);
  return Buffer.concat([header, response]);
}

function statusResponse(requestId: string): Record<string, unknown> {
  return {
    ok: true,
    requestId,
    data: {
      connected: true,
      brokerVersion: "test",
      protocolVersion: "2026-08-11",
      capabilities: ["status"],
      warnings: [],
    },
    warnings: [],
    metrics: { elapsedMs: 1, queueWaitMs: 2, revitExecutionMs: 3 },
  };
}

function bridgeHealthResponse(requestId: string, pipeName: string): Record<string, unknown> {
  return {
    ok: true,
    requestId,
    data: {
      healthy: true,
      pipeName,
      controlPipeName: `${pipeName}-control`,
      waitingListeners: 5,
      activeConnections: 1,
      acceptedConnections: 2,
      completedConnections: 1,
      clientFaults: 0,
      listenerFaults: 0,
      queue: { pendingCount: 0, hasPending: false },
      requestOutcomes: { activeCount: 0, inFlightCount: 0, completedCount: 0, capacity: 256, ttlSeconds: 600 },
    },
    warnings: [],
    metrics: { elapsedMs: 1 },
  };
}

function writeJsonResponse(socket: net.Socket, response: unknown): void {
  const body = Buffer.from(JSON.stringify(response), "utf8");
  const header = Buffer.allocUnsafe(4);
  header.writeUInt32BE(body.byteLength, 0);
  socket.write(Buffer.concat([header, body]));
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
