import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import net from "node:net";
import { parseArgs, parseAuthTokenConfig, runRevitCtl } from "../src/cli/revitctl.js";

interface CapturedRequest {
  authToken?: string;
  operation: string;
  operationKind: string;
  payload: Record<string, unknown>;
  requestId: string;
}

test("revitctl parses compact command payloads and auth token config", () => {
  assert.equal(parseAuthTokenConfig("REVIT_MCP_NEXT_AUTH_TOKEN=abc123\n"), "abc123");
  assert.equal(parseAuthTokenConfig('REVIT_MCP_NEXT_AUTH_TOKEN="quoted-token"\n'), "quoted-token");

  const query = parseArgs(["query", "--payload", '{"filter":{"classes":["Wall"]},"limit":2}', "--timeout-ms", "1234"]);
  assert.equal(query.command, "query");
  assert.equal(query.timeoutMs, 1234);
  assert.deepEqual(query.payload, { filter: { classes: ["Wall"] }, limit: 2 });

  const call = parseArgs(["call", "get_rooms", "--payload", '{"preset":"schedule"}']);
  assert.equal(call.command, "call");
  assert.equal(call.operation, "get_rooms");
  assert.deepEqual(call.payload, { preset: "schedule" });

  const rawCall = parseArgs(["call", "experimental_probe", "--operation-kind", "write", "--payload", '{"value":1}']);
  assert.equal(rawCall.command, "call");
  assert.equal(rawCall.operation, "experimental_probe");
  assert.equal(rawCall.operationKind, "write");
  assert.deepEqual(rawCall.payload, { value: 1 });
});

test("revitctl calls the named pipe bridge with auth from config", async () => {
  const tempRoot = mkdtempSync(path.join(os.tmpdir(), "revitctl-test-"));
  const authConfig = path.join(tempRoot, "auth.env");
  writeFileSync(authConfig, "REVIT_MCP_NEXT_AUTH_TOKEN=test-token\n", "utf8");

  try {
    await withPipeServer(
      (request) => {
        assert.equal(request.authToken, "test-token");
        assert.equal(request.operation, "status");
        assert.equal(request.operationKind, "read");
        assert.deepEqual(request.payload, {});
      },
      async (pipeName) => {
        const result = await runRevitCtl(parseArgs(["status", "--pipe", pipeName, "--auth-config", authConfig]));
        assert.equal(result.exitCode, 0);
        assert.equal((result.body as { ok?: boolean }).ok, true);
      }
    );
  } finally {
    rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("revitctl routes write-control commands through guarded bridge operations", async () => {
  const tempRoot = mkdtempSync(path.join(os.tmpdir(), "revitctl-write-test-"));
  const changeSet = {
    documentFingerprint: "sample-doc-fingerprint",
    transactionName: "CLI write probe",
    operations: [
      {
        type: "set_parameter",
        elementId: "501",
        parameterName: "Mark",
        value: "CLI-1",
      },
    ],
  };
  const applyPayload = {
    ...changeSet,
    previewId: "preview-1",
    baseGeneration: 7,
    changeSetHash: "sha256:test",
    expiresAt: "2099-01-01T00:00:00.000Z",
  };
  const changeSetPath = path.join(tempRoot, "change-set.json");
  const applyPayloadPath = path.join(tempRoot, "apply-payload.json");
  writeFileSync(changeSetPath, JSON.stringify(changeSet), "utf8");
  writeFileSync(applyPayloadPath, JSON.stringify(applyPayload), "utf8");

  try {
    await withPipeServer(
      (request) => {
        assert.equal(request.operation, "preview_change_set");
        assert.equal(request.operationKind, "preview");
        assert.deepEqual(request.payload, changeSet);
      },
      async (pipeName) => {
        const result = await runRevitCtl(parseArgs(["preview", "--pipe", pipeName, changeSetPath]));
        assert.equal(result.exitCode, 0);
      }
    );

    await assert.rejects(
      () => runRevitCtl(parseArgs(["apply", "--pipe", "unused-revitctl-test-pipe", applyPayloadPath])),
      /requires --confirm/
    );

    await withPipeServer(
      (request) => {
        assert.equal(request.operation, "apply_change_set");
        assert.equal(request.operationKind, "write");
        assert.deepEqual(request.payload, { ...applyPayload, confirm: true });
      },
      async (pipeName) => {
        const result = await runRevitCtl(parseArgs(["apply", "--pipe", pipeName, applyPayloadPath, "--confirm"]));
        assert.equal(result.exitCode, 0);
      }
    );

    await assert.rejects(
      () =>
        runRevitCtl(
          parseArgs(["call", "apply_change_set", "--pipe", "unused-revitctl-test-pipe", "--payload", JSON.stringify(applyPayload)])
        ),
      /requires --confirm/
    );

    await withPipeServer(
      (request) => {
        assert.equal(request.operation, "cancel_request");
        assert.equal(request.operationKind, "debug");
        assert.deepEqual(request.payload, { requestId: "request-1", reason: "test cancellation" });
      },
      async (pipeName) => {
        const result = await runRevitCtl(
          parseArgs([
            "cancel",
            "--pipe",
            pipeName,
            "--payload",
            '{"requestId":"request-1","reason":"test cancellation"}',
          ])
        );
        assert.equal(result.exitCode, 0);
      }
    );

    await withPipeServer(
      (request) => {
        assert.equal(request.operation, "cancel_request");
        assert.equal(request.operationKind, "debug");
        assert.deepEqual(request.payload, { requestId: "request-2" });
      },
      async (pipeName) => {
        const result = await runRevitCtl(parseArgs(["cancel-request", "--pipe", pipeName, "--payload", '{"requestId":"request-2"}']));
        assert.equal(result.exitCode, 0);
      }
    );

    await withPipeServer(
      (request) => {
        assert.equal(request.operation, "experimental_probe");
        assert.equal(request.operationKind, "write");
        assert.deepEqual(request.payload, { value: 1 });
      },
      async (pipeName) => {
        const result = await runRevitCtl(
          parseArgs(["call", "experimental_probe", "--pipe", pipeName, "--operation-kind", "write", "--payload", '{"value":1}'])
        );
        assert.equal(result.exitCode, 0);
      }
    );
  } finally {
    rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("revitctl help lists write-control and raw call support", async () => {
  const result = await runRevitCtl(parseArgs(["--help"]));
  assert.equal(result.exitCode, 0);
  const help = String(result.body);
  assert.match(help, /revitctl preview/);
  assert.match(help, /revitctl apply/);
  assert.match(help, /revitctl cancel/);
  assert.match(help, /revitctl call/);
  assert.match(help, /--operation-kind/);
});

async function withPipeServer(onRequest: (request: CapturedRequest) => void, runClient: (pipeName: string) => Promise<void>) {
  const pipeName = `revitctl-test-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const pipePath = `\\\\.\\pipe\\${pipeName}`;
  const server = net.createServer((socket) => {
    let buffer = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.byteLength < 4) return;
      const length = buffer.readUInt32BE(0);
      if (buffer.byteLength < length + 4) return;

      const request = JSON.parse(buffer.subarray(4, length + 4).toString("utf8")) as CapturedRequest;
      onRequest(request);

      const responseBody = Buffer.from(
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
      header.writeUInt32BE(responseBody.byteLength, 0);
      socket.write(Buffer.concat([header, responseBody]));
    });
  });

  await new Promise<void>((resolve) => server.listen(pipePath, resolve));
  try {
    await runClient(pipeName);
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
}
