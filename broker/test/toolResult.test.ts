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

test("asToolResult marks bridge failures as MCP tool errors", () => {
  const result = asToolResult(
    {
      ok: false,
      requestId: "1",
      error: {
        code: "BRIDGE_UNAVAILABLE",
        message: "Revit is closed",
        recoverable: true,
      },
      warnings: [],
    },
    () => "unused"
  );

  assert.equal(result.isError, true);
  assert.match(textContent(result), /BRIDGE_UNAVAILABLE/);
  assert.equal((result.structuredContent?.data as { error: { code: string } }).error.code, "BRIDGE_UNAVAILABLE");
  assert.deepEqual(result.structuredContent?.metrics, { elapsedMs: 0 });
});

function textContent(result: CallToolResult): string {
  const first = result.content[0];
  assert.equal(first?.type, "text");
  return first.text;
}
