// read_many (SPEC §5.9): up to 8 read calls in one round trip with ONE Deadline; sub-call i gets remaining/(n-i).
// A sub-call that cannot start or finish in its share returns "[i] unavailable: BUDGET - run alone: <tool> <args>".
// At most 2 images and 2 MB of image data; further captures return "[i] image omitted (limit) - run alone: capture {...}".
// One failure does not stop the rest. Owner in wave 2: P-READ (hardening).
import { byName } from "@revit-mcp-next/contracts/catalog";
import { formatCall } from "../framework/errors.js";
import { renderText } from "../framework/render.js";
import type { ImageBlock, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

export const MAX_CALLS = 8;
export const MAX_IMAGES = 2;
export const MAX_IMAGE_BYTES = 2 * 1024 * 1024;
const READ_TOOLS = new Set(["status", "list", "find_elements", "describe_elements", "get_view", "read_schedule", "read_family", "check_model", "get_quantities", "get_changes", "capture", "help"]);
const MIN_SHARE_MS = 1_500;

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  const calls = Array.isArray(ctx.args.calls) ? (ctx.args.calls as unknown[]) : [];
  if (calls.length === 0) return ctx.fail("INVALID_ARGS", "read_many needs calls: [{tool, args}]", { param: "calls", reason: "missing", example: ctx.opSpec.examples[0] });
  if (calls.length > MAX_CALLS) return ctx.fail("INVALID_ARGS", `read_many takes at most ${MAX_CALLS} calls, got ${calls.length}`, { param: "calls", reason: "too many", example: { calls: calls.slice(0, MAX_CALLS) } });
  const blocks: string[] = [];
  const images: ImageBlock[] = [];
  let imageBytes = 0;
  let ok = 0;
  let failed = 0;
  let unavailable = 0;
  let firstDoc: ToolOutcome["doc"] = null;
  for (let i = 0; i < calls.length; i++) {
    const item = calls[i] as { tool?: unknown; args?: unknown } | null;
    const tool = typeof item?.tool === "string" ? item.tool.trim() : "";
    const args = item && item.args && typeof item.args === "object" && !Array.isArray(item.args) ? (item.args as Record<string, unknown>) : {};
    if (!byName.has(tool) || !READ_TOOLS.has(tool)) {
      failed += 1;
      const isWrite = byName.has(tool);
      blocks.push(`[${i}] ${tool || "?"}: ERROR INVALID_ARGS: ${isWrite ? `${tool} is not a read; run writes with change_set or alone` : `unknown read tool "${tool}"`}\nfix: ${isWrite ? `change_set {"ops":[{"tool":"${tool}",...}]} or ${formatCall(tool, args)}` : `read_many with one of ${[...READ_TOOLS].join(", ")}`}`);
      continue;
    }
    const share = ctx.deadline.share(i, calls.length);
    if (share.remaining() < MIN_SHARE_MS) {
      unavailable += 1;
      blocks.push(`[${i}] ${tool}: unavailable: BUDGET - run alone: ${formatCall(tool, args)}`);
      continue;
    }
    const outcome = await ctx.invoke(tool, args, { deadline: share });
    if (outcome.status === "running" && share.expired) {
      unavailable += 1;
      blocks.push(`[${i}] ${tool}: unavailable: BUDGET - run alone: ${formatCall(tool, args)}${outcome.next ? ` (${outcome.next})` : ""}`);
      continue;
    }
    if (outcome.status === "error") failed += 1;
    else ok += 1;
    if (!firstDoc && outcome.doc) firstDoc = outcome.doc;
    const kept: ImageBlock[] = [];
    for (const image of outcome.images ?? []) {
      const size = Buffer.byteLength(image.data, "base64");
      if (images.length + kept.length >= MAX_IMAGES || imageBytes + size > MAX_IMAGE_BYTES) {
        ctx.warn("PARTIAL_RESULT", `[${i}] image omitted (limit) - run alone: ${formatCall(tool, args)}`);
        continue;
      }
      kept.push(image);
      imageBytes += size;
    }
    images.push(...kept);
    const text = renderText({ ...outcome, images: undefined }, { structured: false, detailFull: false });
    blocks.push(`[${i}] ${tool}: ${text}`);
  }
  const parts = [`${ok} ok`, failed ? `${failed} failed` : "", unavailable ? `${unavailable} unavailable (BUDGET)` : ""].filter(Boolean);
  return {
    status: "ok",
    summary: `read_many ${calls.length} call${calls.length === 1 ? "" : "s"}: ${parts.join(", ")}${images.length ? `, ${images.length} image${images.length === 1 ? "" : "s"}` : ""}`,
    blocks,
    images: images.length ? images : undefined,
    doc: firstDoc,
  };
}

export const module: ToolModule = { name: "read_many", handle };
