import test from "node:test";
import assert from "node:assert/strict";
import { asToolResult } from "../src/tools/toolResult.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

test("asToolResult returns short text plus structured data on success", () => {
  const result = asToolResult(
    {
      ok: true,
      requestId: "1",
      data: { connected: true },
      warnings: [],
      metrics: { elapsedMs: 2 },
      generation: 4,
    },
    () => "connected"
  );

  assert.equal(result.isError, undefined);
  assert.equal(result.content[0]?.type, "text");
  assert.equal(textContent(result), "connected");
  assert.deepEqual(result.structuredContent?.data, { connected: true });
});

test("asToolResult appends cursor hints for truncated result pages", () => {
  const result = asToolResult(
    {
      ok: true,
      requestId: "1",
      data: { returnedCount: 1, limit: 1, truncated: true, cursor: "1" },
      warnings: [],
      metrics: { elapsedMs: 2 },
    },
    (data) => `${data.returnedCount} item returned`
  );

  assert.equal(
    textContent(result),
    "1 item returned. More results available; call the same tool with the same arguments and structuredContent.data.cursor."
  );
});

test("asToolResult omits undefined object properties from structured content", () => {
  const result = asToolResult(
    {
      ok: true,
      requestId: "1",
      data: {
        keep: "value",
        omit: undefined,
        nested: { keep: 1, omit: undefined },
        array: [undefined, { keep: true, omit: undefined }],
      },
      warnings: [],
      metrics: { elapsedMs: 2 },
    },
    () => "sanitized"
  );

  assert.deepEqual(result.structuredContent?.data, {
    keep: "value",
    nested: { keep: 1 },
    array: [null, { keep: true }],
  });
});

test("asToolResult marks bridge failures as MCP tool errors", () => {
  const result = asToolResult(
    {
      ok: false,
      requestId: "1",
      error: {
        code: "BRIDGE_UNAVAILABLE",
        message: "Revit is closed",
        recoverable: true,
        suggestedNextAction: "Start Revit, then call revit.status.",
      },
      warnings: [],
    },
    () => "unused"
  );

  assert.equal(result.isError, true);
  assert.match(textContent(result), /BRIDGE_UNAVAILABLE/);
  assert.match(textContent(result), /Start Revit/);
  assert.equal((result.structuredContent?.data as { error: { code: string } }).error.code, "BRIDGE_UNAVAILABLE");
  assert.deepEqual(result.structuredContent?.metrics, { elapsedMs: 0 });
});

function textContent(result: CallToolResult): string {
  const first = result.content[0];
  assert.equal(first?.type, "text");
  return first.text;
}
