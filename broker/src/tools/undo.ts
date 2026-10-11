// undo (SPEC §6.9, broker part). The add-in does the work: mode auto posts Revit's Undo/Redo for this session's newest
// writes (the doc must be UI-active; NEEDS_ACTIVE_DOC otherwise) and confirms each step; mode compensate applies the
// inverse of this session's newest creation write as a new write. The session is identified by the broker's clientKey.
// This module only fixes the follow-up hint: after an undo the useful next call is redo (and vice versa).
import type { AddinResult, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

const isObject = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === "object" && !Array.isArray(v);

async function finish(ctx: ToolContext, _r: AddinResult, o: ToolOutcome): Promise<ToolOutcome> {
  if (o.status !== "ok" || !isObject(o.data)) return o;
  const data = { ...o.data };
  const docArg = typeof data.undo === "string" ? /"doc":"(#\d+)"/.exec(data.undo)?.[1] : undefined;
  const args: Record<string, unknown> = {};
  if (docArg) args.doc = docArg;
  const compensate = String(ctx.args.mode ?? "auto") === "compensate";
  // auto: the inverse is redo (or undo after a redo); compensate: the compensation is an ordinary write, undone by undo {}.
  if (!compensate && ctx.args.redo !== true) args.redo = true;
  data.undo = `undo ${JSON.stringify(args)}`;
  return { ...o, data };
}

export const module: ToolModule = { name: "undo", finish };
