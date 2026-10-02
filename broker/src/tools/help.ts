// help: {} (instructions + index), {tool} (ops with params and an example), {tool, op} (full params, registry facts,
// examples), {topic} (topics and error/warning/notice codes). Generated from the catalog; no Revit needed.
import { byName, CATALOG } from "@revit-mcp-next/contracts/catalog";
import { closestTool, helpIndex, helpOp, helpTool, helpTopic } from "../framework/help.js";
import { closest } from "../framework/normalize.js";
import type { ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  const tool = typeof ctx.args.tool === "string" ? ctx.args.tool.trim() : "";
  const op = typeof ctx.args.op === "string" ? ctx.args.op.trim() : "";
  const topic = typeof ctx.args.topic === "string" ? ctx.args.topic.trim() : "";
  const listed = ctx.services.tools.listed();
  if (!tool && !topic) {
    if (op) return ctx.fail("INVALID_ARGS", "help op needs tool", { param: "tool", reason: "missing", example: { tool: "create_elements", op } });
    return helpIndex(listed);
  }
  if (tool) {
    const name = tool.replace(/^mcp__revit__/, "");
    const spec = byName.get(name);
    if (!spec) {
      const near = closestTool(name);
      return ctx.fail("NOT_FOUND", `there is no tool "${tool}"`, { param: "tool", kind: "tool", value: tool, candidates: near ? [near] : CATALOG.map((t) => t.name).slice(0, 8) });
    }
    if (!op) return helpTool(spec);
    if (!spec.discriminator) return helpOp(spec, "");
    if (!(op in spec.ops)) {
      const near = closest(op, Object.keys(spec.ops));
      return ctx.fail("NOT_FOUND", `${spec.name} has no ${spec.discriminator} "${op}"`, { param: "op", kind: spec.discriminator, value: op, candidates: near ? [near.option] : Object.keys(spec.ops) });
    }
    return helpOp(spec, op);
  }
  const found = helpTopic(topic);
  if (found) return found;
  return ctx.fail("NOT_FOUND", `there is no help topic "${topic}"`, { param: "topic", kind: "topic", value: topic, candidates: ["targeting", "confirm", "units", "selectors", "paging", "jobs", "images", "errors"] });
}

export const module: ToolModule = { name: "help", handle };
