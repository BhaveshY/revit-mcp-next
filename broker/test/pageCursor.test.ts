import test from "node:test";
import assert from "node:assert/strict";
import type { BridgeResponse } from "@revit-mcp-next/contracts";
import { PROTOCOL_VERSION } from "@revit-mcp-next/contracts";
import { decodePageCursor, encodePageCursorResponse } from "../src/tools/pageCursor.js";

const cursorOperations = [
  "get_views",
  "get_sheets",
  "get_current_view_elements",
  "get_selection",
  "get_material_quantities",
  "get_rooms",
  "catalog",
  "query",
  "describe_parameters",
];

test("page cursors are opaque and bound for every paged read operation", () => {
  const context = { sessionId: "cursor-test", protocolVersion: PROTOCOL_VERSION };

  for (const operation of cursorOperations) {
    const bindingPayload = {
      documentFingerprint: "doc-1",
      expectedGeneration: 7,
      filter: { categories: ["OST_Walls"] },
      fields: ["id"],
      limit: 1,
      includeTotalCount: false,
    };
    const bridgeResponse: BridgeResponse<Record<string, unknown>> = {
      ok: true,
      requestId: "1",
      data: {
        document: { fingerprint: "doc-1", title: "Sample", generation: 7 },
        items: [{ id: "501" }],
        returnedCount: 1,
        limit: 1,
        truncated: true,
        cursor: "1",
        scope: "test",
        source: "fake",
      },
      warnings: [],
      metrics: { elapsedMs: 1 },
      generation: 7,
    };

    const encoded = encodePageCursorResponse(bridgeResponse, context, operation, bindingPayload);
    assert.equal(encoded.ok, true);
    if (!encoded.ok) return;
    const opaqueCursor = encoded.data.cursor;
    assert.equal(typeof opaqueCursor, "string", operation);
    assert.notEqual(opaqueCursor, "1", operation);
    assert.match(opaqueCursor as string, /^rvc1_/);

    const decoded = decodePageCursor(opaqueCursor as string, context, operation, bindingPayload);
    assert.equal(decoded.ok, true, operation);
    if (!decoded.ok) return;
    assert.equal(decoded.cursor, "1", operation);
    assert.equal(decoded.generation, 7, operation);
    assert.equal(decoded.documentFingerprint, "doc-1", operation);

    const changedArguments = decodePageCursor(opaqueCursor as string, context, operation, {
      ...bindingPayload,
      limit: 2,
    });
    assert.equal(changedArguments.ok, false, operation);
    if (changedArguments.ok) return;
    assert.equal(changedArguments.response.error.code, "CURSOR_SCOPE_MISMATCH", operation);

    const wrongOperation = decodePageCursor(opaqueCursor as string, context, "query", bindingPayload);
    if (operation !== "query") {
      assert.equal(wrongOperation.ok, false, operation);
    }

    const wrongSession = decodePageCursor(opaqueCursor as string, { ...context, sessionId: "other" }, operation, bindingPayload);
    assert.equal(wrongSession.ok, false, operation);

    const rawOffset = decodePageCursor("1", context, operation, bindingPayload);
    assert.equal(rawOffset.ok, false, operation);
    if (rawOffset.ok) return;
    assert.equal(rawOffset.response.error.code, "INVALID_CURSOR", operation);
  }
});
