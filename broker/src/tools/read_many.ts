// read_many (SPEC §5.9): up to 8 read calls in one round trip with ONE Deadline; sub-call i gets remaining/(n-i).
// A sub-call that cannot start or finish in its share returns "[i] unavailable: BUDGET - run alone: <tool> <args>".
// At most 2 images and 2 MB of image data; further captures return "[i] image omitted (limit) - run alone: capture {...}".
// One failure does not stop the rest. Hardening (P-READ):
//   - lenient items: {tool, args}, args as a JSON string, or flattened {tool:"list", kind:"levels"}; "tool.op" names
//     (e.g. "list.levels", "check_model.warnings") put the op into the tool's discriminator;
//   - unknown tools get the closest read tool in the fix; writes are pointed at change_set;
//   - the combined text stays inside the 12 KB result budget: each sub-result gets an equal share and its largest
//     arrays are halved until it fits (its own more: page token still continues the full list).
import { byName } from "@revit-mcp-next/contracts/catalog";
import { formatCall } from "../framework/errors.js";
import { closest } from "../framework/normalize.js";
import { bytesOf, renderText, TEXT_CAP_BYTES } from "../framework/render.js";
import type { ImageBlock, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

export const MAX_CALLS = 8;
export const MAX_IMAGES = 2;
export const MAX_IMAGE_BYTES = 2 * 1024 * 1024;
const READ_TOOLS = ["status", "list", "find_elements", "describe_elements", "get_view", "read_schedule", "read_family", "check_model", "get_quantities", "get_changes", "capture", "help"];
const READ_SET = new Set(READ_TOOLS);
const MIN_SHARE_MS = 1_500;
/** Text budget for all sub-results together (the head line of read_many itself is small). */
const TOTAL_BLOCK_BYTES = TEXT_CAP_BYTES - 1_000;
const MIN_BLOCK_BYTES = 1_200;

interface CallItem {
  tool: string;
  args: Record<string, unknown>;
}

const isObject = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === "object" && !Array.isArray(v);

/** Accepts {tool, args}, args as a JSON string, flattened args, and "tool.op" names. */
export function parseItem(item: unknown): CallItem | null {
  let value = item;
  if (typeof value === "string") {
    const text = value.trim();
    if (text.startsWith("{")) {
      try {
        value = JSON.parse(text);
      } catch {
        return { tool: text, args: {} };
      }
    } else {
      return { tool: text, args: {} };
    }
  }
  if (!isObject(value)) return null;
  let tool = typeof value.tool === "string" ? value.tool.trim() : typeof value.name === "string" ? value.name.trim() : "";
  let args: Record<string, unknown> = {};
  if (typeof value.args === "string") {
    try {
      const parsed = JSON.parse(value.args);
      if (isObject(parsed)) args = parsed;
    } catch {
      args = {};
    }
  } else if (isObject(value.args)) {
    args = { ...value.args };
  } else if (isObject(value.arguments)) {
    args = { ...value.arguments };
  } else {
    for (const [k, v] of Object.entries(value)) if (k !== "tool" && k !== "name") args[k] = v;
  }
  const dot = tool.indexOf(".");
  if (dot > 0) {
    const base = tool.slice(0, dot);
    const op = tool.slice(dot + 1);
    const spec = byName.get(base);
    const disc = spec?.discriminator;
    if (spec && disc && op && args[disc] === undefined) args[disc] = op;
    if (spec) tool = base;
  }
  return { tool, args };
}

/** Halve the largest arrays of an outcome's data until its rendered text fits `budget` bytes. */
function fitBlock(outcome: ToolOutcome, budget: number): { text: string; cut: boolean } {
  let text = renderText(outcome, { structured: false, detailFull: false });
  if (bytesOf(text) <= budget || outcome.data === undefined || outcome.status === "error") return { text, cut: false };
  const data = structuredClone(outcome.data);
  for (let round = 0; round < 24; round++) {
    const target = largestArray(data, 0);
    if (!target || target.length <= 1) break;
    target.splice(Math.max(1, Math.floor(target.length / 2)));
    text = renderText({ ...outcome, data }, { structured: false, detailFull: false });
    if (bytesOf(text) <= budget) break;
  }
  return { text, cut: true };
}

function largestArray(node: unknown, depth: number): unknown[] | null {
  if (depth > 5 || node === null || typeof node !== "object") return null;
  let best: unknown[] | null = null;
  let bestSize = 0;
  const consider = (candidate: unknown[] | null) => {
    if (!candidate || candidate.length <= 1) return;
    const size = bytesOf(JSON.stringify(candidate));
    if (size > bestSize) {
      best = candidate;
      bestSize = size;
    }
  };
  if (Array.isArray(node)) {
    consider(node);
    for (const child of node.slice(0, 8)) consider(largestArray(child, depth + 1));
  } else {
    for (const v of Object.values(node as Record<string, unknown>)) consider(Array.isArray(v) ? v : largestArray(v, depth + 1));
  }
  return best;
}

function unknownToolBlock(i: number, tool: string, args: Record<string, unknown>): string {
  const spec = byName.get(tool);
  if (spec) {
    return `[${i}] ${tool}: ERROR INVALID_ARGS: ${tool} is not a read; run writes alone or in change_set\nfix: ${formatCall("change_set", { ops: [{ tool, ...args }] })} or ${formatCall(tool, args)}`;
  }
  const near = tool ? closest(tool, READ_TOOLS) : null;
  const suggestion = near && near.distance <= 3 ? near.option : null;
  return `[${i}] ${tool || "?"}: ERROR INVALID_ARGS: unknown read tool "${tool}"\nfix: ${suggestion ? `use ${formatCall(suggestion, args)} inside read_many` : `read_many calls must use one of ${READ_TOOLS.join(", ")}`}`;
}

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  let calls: unknown = ctx.args.calls;
  if (typeof calls === "string") {
    try {
      calls = JSON.parse(calls);
    } catch {
      calls = [];
    }
  }
  if (isObject(calls)) calls = [calls];
  const list = Array.isArray(calls) ? calls : [];
  if (list.length === 0) return ctx.fail("INVALID_ARGS", "read_many needs calls: [{tool, args}]", { param: "calls", reason: "missing", example: ctx.opSpec.examples[0] });
  if (list.length > MAX_CALLS) return ctx.fail("INVALID_ARGS", `read_many takes at most ${MAX_CALLS} calls, got ${list.length}`, { param: "calls", reason: "too many", example: { calls: list.slice(0, MAX_CALLS) } });

  const blocks: string[] = [];
  const images: ImageBlock[] = [];
  const blockBudget = Math.max(MIN_BLOCK_BYTES, Math.floor(TOTAL_BLOCK_BYTES / list.length));
  let imageBytes = 0;
  let ok = 0;
  let failed = 0;
  let unavailable = 0;
  let cut = 0;
  let firstDoc: ToolOutcome["doc"] = null;
  for (let i = 0; i < list.length; i++) {
    const item = parseItem(list[i]);
    const tool = item?.tool ?? "";
    const args = item?.args ?? {};
    if (!item || !READ_SET.has(tool) || !byName.has(tool)) {
      failed += 1;
      blocks.push(unknownToolBlock(i, tool, args));
      continue;
    }
    const share = ctx.deadline.share(i, list.length);
    if (share.remaining() < MIN_SHARE_MS || ctx.signal.aborted) {
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
    const fitted = fitBlock({ ...outcome, images: undefined }, blockBudget);
    if (fitted.cut) {
      cut += 1;
      ctx.warn("PARTIAL_RESULT", `[${i}] ${tool} was shortened to fit read_many - run alone for every row: ${formatCall(tool, args)}`);
    }
    blocks.push(`[${i}] ${tool}: ${fitted.text}`);
  }
  const parts = [`${ok} ok`, failed ? `${failed} failed` : "", unavailable ? `${unavailable} unavailable (BUDGET)` : "", cut ? `${cut} shortened` : ""].filter(Boolean);
  return {
    status: "ok",
    summary: `read_many ${list.length} call${list.length === 1 ? "" : "s"}: ${parts.join(", ")}${images.length ? `, ${images.length} image${images.length === 1 ? "" : "s"}` : ""}`,
    blocks,
    images: images.length ? images : undefined,
    doc: firstDoc,
  };
}

export const module: ToolModule = { name: "read_many", handle };
