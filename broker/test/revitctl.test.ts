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

interface PipeResponseOverride {
  ok?: boolean;
  data?: unknown;
  error?: {
    code: string;
    message: string;
    recoverable?: boolean;
    suggestedNextAction?: string;
    details?: unknown;
  };
  warnings?: Array<{ code: string; message: string }>;
  metrics?: Record<string, unknown>;
}

test("revitctl parses compact command payloads and auth token config", () => {
  assert.equal(parseAuthTokenConfig("REVIT_MCP_NEXT_AUTH_TOKEN=abc123\n"), "abc123");
  assert.equal(parseAuthTokenConfig('REVIT_MCP_NEXT_AUTH_TOKEN="quoted-token"\n'), "quoted-token");
  assert.equal(parseAuthTokenConfig('\ufeffREVIT_MCP_NEXT_AUTH_TOKEN="bom-token"\n'), "bom-token");

  const query = parseArgs(["query", "--payload", '{"filter":{"classes":["Wall"]},"limit":2}', "--timeout-ms", "1234"]);
  assert.equal(query.command, "query");
  assert.equal(query.timeoutMs, 1234);
  assert.deepEqual(query.payload, { filter: { classes: ["Wall"] }, limit: 2 });

  const context = parseArgs(["model-context", "--payload", '{"phaseLimit":5}']);
  assert.equal(context.command, "model-context");
  assert.deepEqual(context.payload, { phaseLimit: 5 });

  const readBundle = parseArgs(["read-bundle", "--payload", '{"include":{"warnings":true}}']);
  assert.equal(readBundle.command, "read-bundle");
  assert.deepEqual(readBundle.payload, { include: { warnings: true } });

  const warnings = parseArgs(["warnings", "--payload", '{"preset":"elements","limit":5}']);
  assert.equal(warnings.command, "warnings");
  assert.deepEqual(warnings.payload, { preset: "elements", limit: 5 });

  const call = parseArgs(["call", "get_rooms", "--payload", '{"preset":"schedule"}']);
  assert.equal(call.command, "call");
  assert.equal(call.operation, "get_rooms");
  assert.deepEqual(call.payload, { preset: "schedule" });

  const rawCall = parseArgs(["call", "experimental_probe", "--operation-kind", "write", "--payload", '{"value":1}']);
  assert.equal(rawCall.command, "call");
  assert.equal(rawCall.operation, "experimental_probe");
  assert.equal(rawCall.operationKind, "write");
  assert.deepEqual(rawCall.payload, { value: 1 });

  const createProject = parseArgs([
    "create-project",
    "--payload",
    '{"templatePath":"C:\\\\Templates\\\\DefaultMetric.rte","outputPath":"C:\\\\tmp\\\\fixture.rvt"}',
    "--confirm",
  ]);
  assert.equal(createProject.command, "create-project");
  assert.deepEqual(createProject.payload, {
    templatePath: "C:\\Templates\\DefaultMetric.rte",
    outputPath: "C:\\tmp\\fixture.rvt",
  });
});

test("revitctl calls the named pipe bridge with auth from config", async () => {
  const tempRoot = mkdtempSync(path.join(os.tmpdir(), "revitctl-test-"));
  const authConfig = path.join(tempRoot, "auth.env");
  const discoveryConfig = path.join(tempRoot, "client-discovery.json");
  writeFileSync(authConfig, "\ufeffREVIT_MCP_NEXT_AUTH_TOKEN=test-token\n", "utf8");

  try {
    await withPipeServer(
      (request) => {
        assert.equal(request.authToken, "test-token");
        assert.equal(request.operation, "status");
        assert.equal(request.operationKind, "read");
        assert.deepEqual(request.payload, {});
      },
      async (pipeName) => {
        writeFileSync(
          discoveryConfig,
          `\ufeff${JSON.stringify({ authConfigPath: authConfig, pipeName })}`,
          "utf8"
        );
        const result = await runRevitCtl(parseArgs(["status", "--discovery", discoveryConfig]));
        assert.equal(result.exitCode, 0);
        assert.equal((result.body as { ok?: boolean }).ok, true);
      }
    );
  } finally {
    rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("revitctl routes warnings command as a read operation", async () => {
  await withPipeServer(
    (request) => {
      assert.equal(request.operation, "get_warnings");
      assert.equal(request.operationKind, "read");
      assert.deepEqual(request.payload, { preset: "summary", limit: 3 });
    },
    async (pipeName) => {
      const result = await runRevitCtl(parseArgs(["warnings", "--payload", '{"preset":"summary","limit":3}', "--pipe", pipeName]));
      assert.equal(result.exitCode, 0);
      assert.equal((result.body as { ok?: boolean }).ok, true);
    }
  );
});

test("revitctl routes compact support read aliases as read operations", async () => {
  const cases = [
    {
      argv: ["current-view-elements", "--payload", '{"preset":"summary","limit":5}'],
      operation: "get_current_view_elements",
      payload: { preset: "summary", limit: 5 },
    },
    {
      argv: ["view-elements", "--payload", '{"preset":"geometrySummary","limit":2}'],
      operation: "get_current_view_elements",
      payload: { preset: "geometrySummary", limit: 2 },
    },
    {
      argv: ["analyze", "--payload", '{"bucketLimit":3}'],
      operation: "analyze_model",
      payload: { bucketLimit: 3 },
    },
    {
      argv: ["analyze-model", "--payload", '{"includeClassBreakdown":false}'],
      operation: "analyze_model",
      payload: { includeClassBreakdown: false },
    },
    {
      argv: ["materials", "--payload", '{"limit":4}'],
      operation: "get_material_quantities",
      payload: { limit: 4 },
    },
    {
      argv: ["material-quantities", "--payload", '{"materialNameContains":"Concrete"}'],
      operation: "get_material_quantities",
      payload: { materialNameContains: "Concrete" },
    },
    {
      argv: ["rooms", "--payload", '{"preset":"schedule","limit":6}'],
      operation: "get_rooms",
      payload: { preset: "schedule", limit: 6 },
    },
  ] as const;

  for (const item of cases) {
    await withPipeServer(
      (request) => {
        assert.equal(request.operation, item.operation);
        assert.equal(request.operationKind, "read");
        assert.deepEqual(request.payload, item.payload);
      },
      async (pipeName) => {
        const result = await runRevitCtl(parseArgs([...item.argv, "--pipe", pipeName]));
        assert.equal(result.exitCode, 0);
        assert.equal((result.body as { ok?: boolean }).ok, true);
      }
    );
  }
});

test("revitctl routes model-context command as a read operation", async () => {
  await withPipeServer(
    (request) => {
      assert.equal(request.operation, "get_model_context");
      assert.equal(request.operationKind, "read");
      assert.deepEqual(request.payload, { phaseLimit: 5 });
    },
    async (pipeName) => {
      const result = await runRevitCtl(parseArgs(["context", "--payload", '{"phaseLimit":5}', "--pipe", pipeName]));
      assert.equal(result.exitCode, 0);
      assert.equal((result.body as { ok?: boolean }).ok, true);
    }
  );
});

test("revitctl read-bundle composes compact guarded bridge reads", async () => {
  const requests: CapturedRequest[] = [];
  await withPipeServer(
    (request) => {
      requests.push(request);
      switch (request.operation) {
        case "status":
          return {
            data: {
              connected: true,
              brokerVersion: "test",
              protocolVersion: "2026-06-23",
              activeDocument: { title: "fixture.rvt", fingerprint: "doc-fingerprint", generation: 42 },
              capabilities: ["status"],
              warnings: [],
            },
          };
        case "get_warnings":
          return {
            ok: false,
            error: {
              code: "WARNINGS_UNAVAILABLE",
              message: "Warnings are unavailable in this fixture.",
              recoverable: true,
              suggestedNextAction: "Retry from a live graphical document.",
            },
          };
        default:
          return {
            data: {
              operation: request.operation,
              payload: request.payload,
            },
          };
      }
    },
    async (pipeName) => {
      const result = await runRevitCtl(
        parseArgs([
          "read-bundle",
          "--payload",
          JSON.stringify({
            include: { warnings: true },
            currentViewElements: { limit: 2 },
            catalogs: [{ key: "wallTypes", kind: "elementTypes", preset: "typeChange", limit: 3 }],
            parameters: [{ key: "selectionParams", filter: { selectionOnly: true }, limit: 1 }],
            includeSectionMetrics: true,
          }),
          "--pipe",
          pipeName,
        ])
      );
      assert.equal(result.exitCode, 0);
      const body = result.body as { ok?: boolean; data?: Record<string, unknown> };
      assert.equal(body.ok, true);
      assert.equal(body.data?.source, "revitctl-composed");
      assert.equal(body.data?.documentFingerprint, "doc-fingerprint");
      assert.equal(body.data?.generation, 42);
      assert.deepEqual(body.data?.returnedSections, [
        "status",
        "levels",
        "readiness",
        "currentView",
        "currentViewElements",
        "selection",
        "catalogs.wallTypes",
        "parameters.selectionParams",
      ]);
      assert.deepEqual(body.data?.failedSections, [
        {
          section: "warnings",
          code: "WARNINGS_UNAVAILABLE",
          message: "Warnings are unavailable in this fixture.",
          suggestedNextAction: "Retry from a live graphical document.",
        },
      ]);
    }
  );

  assert.deepEqual(
    requests.map((request) => request.operation),
    [
      "status",
      "get_levels",
      "get_model_readiness",
      "get_current_view",
      "get_current_view_elements",
      "get_selection",
      "get_warnings",
      "catalog",
      "describe_parameters",
    ]
  );
  assert.deepEqual(requests[1].payload, { documentFingerprint: "doc-fingerprint", expectedGeneration: 42 });
  assert.deepEqual(requests[4].payload, {
    documentFingerprint: "doc-fingerprint",
    expectedGeneration: 42,
    filter: {},
    preset: "summary",
    includeHidden: false,
    limit: 2,
    includeTotalCount: false,
  });
  assert.deepEqual(requests[5].payload, {
    documentFingerprint: "doc-fingerprint",
    expectedGeneration: 42,
    filter: { selectionOnly: true },
    preset: "summary",
    limit: 10,
    includeTotalCount: false,
  });
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

    const createProjectPayload = {
      templatePath: "C:\\Templates\\DefaultMetric.rte",
      outputPath: "C:\\tmp\\revit-mcp-next-fixtures\\smoke.rvt",
    };
    await assert.rejects(
      () =>
        runRevitCtl(
          parseArgs([
            "create-project",
            "--pipe",
            "unused-revitctl-test-pipe",
            "--payload",
            JSON.stringify(createProjectPayload),
          ])
        ),
      /requires --confirm/
    );

    await withPipeServer(
      (request) => {
        assert.equal(request.operation, "create_project_from_template");
        assert.equal(request.operationKind, "write");
        assert.deepEqual(request.payload, { ...createProjectPayload, confirm: true });
      },
      async (pipeName) => {
        const result = await runRevitCtl(
          parseArgs(["create-project", "--pipe", pipeName, "--payload", JSON.stringify(createProjectPayload), "--confirm"])
        );
        assert.equal(result.exitCode, 0);
      }
    );

    await assert.rejects(
      () =>
        runRevitCtl(
          parseArgs([
            "call",
            "create_project_from_template",
            "--pipe",
            "unused-revitctl-test-pipe",
            "--payload",
            JSON.stringify(createProjectPayload),
          ])
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
  assert.match(help, /revitctl read-bundle/);
  assert.match(help, /revitctl current-view-elements/);
  assert.match(help, /revitctl analyze/);
  assert.match(help, /revitctl materials/);
  assert.match(help, /revitctl rooms/);
  assert.match(help, /revitctl call/);
  assert.match(help, /--operation-kind/);
});

async function withPipeServer(
  onRequest: (request: CapturedRequest) => PipeResponseOverride | void,
  runClient: (pipeName: string) => Promise<void>
) {
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
      const override = onRequest(request) ?? {};

      const responseBody = Buffer.from(
        JSON.stringify({
          ok: override.ok ?? true,
          requestId: request.requestId,
          data:
            override.data ??
            {
              connected: true,
              brokerVersion: "test",
              protocolVersion: "2026-06-23",
              capabilities: ["status"],
              warnings: [],
            },
          error: override.error,
          warnings: override.warnings ?? [],
          metrics: override.metrics ?? { elapsedMs: 1 },
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
