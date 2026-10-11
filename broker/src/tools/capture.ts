// Tool module for `capture` (SPEC §10.1). Reads use the default handler (images become c# ids there); this module
// adds the compare= resolution: the broker turns the session's c# into the earlier image file, refuses a compare
// against another doc, view, size or region (INVALID_ARGS) and passes the file to the add-in as compare_file.
import type { ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

const same = (a: unknown, b: unknown): boolean => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
const viewKey = (v: unknown): string => (v === undefined || v === null || String(v).trim() === "" ? "active" : String(v).trim().toLowerCase());

async function prepare(ctx: ToolContext): Promise<void | ToolOutcome> {
  const compare = ctx.args.compare;
  if (compare === undefined || compare === null || String(compare).trim() === "") return;
  const id = String(compare).trim().toLowerCase();
  const captures = ctx.session.captures;
  if (!captures.matches(id)) {
    return ctx.fail("INVALID_ARGS", `compare takes the id of an earlier capture of this session (like c4), got ${JSON.stringify(compare)}`, {
      param: "compare",
      reason: "not a capture id",
      example: { view: ctx.args.view ?? "active", compare: "c1" },
    });
  }
  const entry = captures.get(id);
  if (!entry) {
    return ctx.fail("CAPTURE_GONE", `capture ${id} is ${captures.wasIssued(id) ? "no longer available (200 per session)" : "unknown in this session"}`, { capture: id });
  }
  const doc = await ctx.resolveDoc();
  if (entry.docKey && doc?.key && entry.docKey !== doc.key) {
    return ctx.fail("INVALID_ARGS", `${id} was captured in another document; compare needs the same doc, view, size and region`, { param: "compare", reason: "other document" });
  }
  const mismatch: string[] = [];
  if (viewKey(entry.view) !== viewKey(ctx.args.view)) mismatch.push(`view ${JSON.stringify(entry.view ?? "active")}`);
  if (!same(entry.size ?? null, ctx.args.size ?? null)) mismatch.push(`size ${JSON.stringify(entry.size ?? "default")}`);
  if (!same(entry.region ?? null, ctx.args.region ?? null)) mismatch.push(`region ${JSON.stringify(entry.region ?? null)}`);
  if (mismatch.length > 0) {
    const example: Record<string, unknown> = { compare: id };
    if (entry.view !== undefined) example.view = entry.view;
    if (entry.size !== undefined) example.size = entry.size;
    if (entry.region !== undefined) example.region = entry.region;
    return ctx.fail("INVALID_ARGS", `${id} used ${mismatch.join(", ")}; compare needs the same view, size and region`, {
      param: "compare",
      reason: "different view, size or region",
      example,
    });
  }
  ctx.args.compare = id;
  ctx.args.compare_file = entry.file;
}

export const module: ToolModule = { name: "capture", prepare };
