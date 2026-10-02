// change_set (SPEC §6.7, broker part): validates every op with its own tool's per-op rules (normalize + signature),
// checks $ref syntax and backward-only indices, refuses ops that cannot run in a change set (cs:false) with a fix that
// names the standalone call, expands r#/last per op, then runs the add-in key `change_set` as one write (one undo step,
// confirmed once for the aggregate). Owner in wave 2: P-CONTROL (hardening).
import { byName, keyOf } from "@revit-mcp-next/contracts/catalog";
import { defaultWrite } from "../framework/defaults.js";
import type { ToolContextImpl } from "../framework/context.js";
import { formatCall } from "../framework/errors.js";
import { normalizeArgs } from "../framework/normalize.js";
import type { ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";
import { resolveOp, validateOp } from "../framework/validate.js";

export const MAX_OPS = 100;
const REF = /^\$(?:(\d+)|prev)(?:\.ids(?:\[(\d+)\])?|\.([a-z_]+))?$/;
/** Write tools a change set can contain (the advertised enum of ops[].tool). */
const WRITE_TOOLS = new Set(["create_elements", "place_family", "modify_elements", "set_parameters", "edit_types", "edit_views", "view_graphics", "edit_sheets", "annotate", "edit_schedules", "edit_family", "mep", "structure", "manage_document", "worksharing", "links"]);

/** Check every "$..." string in an op's args. Returns a problem or null. */
export function checkRefs(value: unknown, index: number, path: string): string | null {
  if (typeof value === "string") {
    if (!value.startsWith("$") || value.startsWith("$$")) return null;
    const m = REF.exec(value);
    if (!m) return `${path}: "${value}" is not a valid reference ($N, $N.ids, $N.ids[k], $N.<output>, $prev, $$ for a literal $)`;
    if (value.startsWith("$prev")) return index === 0 ? `${path}: $prev has no previous op in op [0]` : null;
    const n = Number(m[1]);
    if (n >= index) return `${path}: "${value}" refers to op [${n}], but only earlier ops (0-${index - 1}) can be referenced`;
    return null;
  }
  if (Array.isArray(value)) {
    for (let i = 0; i < value.length; i++) {
      const p = checkRefs(value[i], index, `${path}[${i}]`);
      if (p) return p;
    }
    return null;
  }
  if (value && typeof value === "object") {
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      const p = checkRefs(v, index, `${path}.${k}`);
      if (p) return p;
    }
  }
  return null;
}

async function prepare(ctx: ToolContext): Promise<void | ToolOutcome> {
  if ((ctx as ToolContextImpl).confirmPlan) return; // the stored plan was validated when it was made
  const raw = ctx.args.ops;
  if (!Array.isArray(raw) || raw.length === 0) return ctx.fail("INVALID_ARGS", "change_set needs ops: [{tool, op, ...that op's params}]", { param: "ops", reason: "missing", example: ctx.opSpec.examples[0] });
  if (raw.length > MAX_OPS) return ctx.fail("INVALID_ARGS", `change_set takes at most ${MAX_OPS} ops, got ${raw.length}`, { param: "ops", reason: "too many" });
  const doc = await ctx.resolveDoc();
  const ops: Record<string, unknown>[] = [];
  for (let i = 0; i < raw.length; i++) {
    const prefix = `[${i}] `;
    const item = raw[i];
    if (!item || typeof item !== "object" || Array.isArray(item)) return ctx.fail("INVALID_ARGS", `${prefix}each op must be an object {tool, op, ...params}`, { param: `ops[${i}]`, reason: "not an object", example: ctx.opSpec.examples[0] });
    const { tool, ...rest } = item as Record<string, unknown>;
    const toolName = typeof tool === "string" ? tool.trim() : "";
    const spec = byName.get(toolName);
    if (!spec || !WRITE_TOOLS.has(toolName))
      return ctx.fail("INVALID_ARGS", `${prefix}tool "${toolName}" cannot run in a change set (write tools only)`, { param: `ops[${i}].tool`, reason: "not a write tool", values: [...WRITE_TOOLS] });
    if ("doc" in rest) return ctx.fail("INVALID_ARGS", `${prefix}a change set runs in one doc: put doc on the change_set, not on its ops`, { param: `ops[${i}].doc`, reason: "doc per op" });
    const norm = normalizeArgs(spec, rest, { prefix });
    if (norm.error) return ctx.fail(norm.error.code, norm.error.message, { ...norm.error.details, param: `ops[${i}].${String(norm.error.details.param ?? "")}` });
    for (const w of norm.warnings) ctx.warn(w.split(" ")[0]!, w.slice(w.indexOf(" ") + 1));
    for (const k of ["preview", "confirm"]) {
      if (k in norm.args) {
        delete norm.args[k];
        ctx.warn("PARAM_IGNORED", `${prefix}${k} applies to the whole change set, not to op [${i}]`);
      }
    }
    const opRes = resolveOp(spec, norm.args, prefix);
    if (opRes.error) return ctx.fail(opRes.error.code, opRes.error.message, { ...opRes.error.details, param: `ops[${i}].${spec.discriminator ?? "op"}` });
    const opName = opRes.op;
    const opSpec = spec.ops[opName ?? ""]!;
    if (!opSpec.meta.cs) {
      const standalone = { ...(spec.discriminator && opName ? { [spec.discriminator]: opName } : {}), ...norm.args };
      const out = ctx.fail("INVALID_ARGS", `${prefix}${keyOf(spec.name, opName)} cannot run inside a change set; call it alone`, { param: `ops[${i}]`, reason: "not allowed in change_set" });
      return { ...out, fix: formatCall(spec.name, standalone, spec.discriminator) };
    }
    const v = validateOp(spec, opName, norm.args, { prefix });
    for (const w of v.warnings) ctx.warn(w.split(" ")[0]!, w.slice(w.indexOf(" ") + 1));
    if (v.error) {
      const out = ctx.fail(v.error.code, v.error.message, { ...v.error.details, param: `ops[${i}].${String(v.error.details.param ?? "")}` });
      const example = v.error.details.example as Record<string, unknown> | undefined;
      if (example) {
        const fixedOps = raw.map((o, j) => (j === i ? { tool: toolName, ...example } : o));
        return { ...out, fix: formatCall("change_set", { ...ctx.effectiveArgs, ops: fixedOps }).length < 1_500 ? formatCall("change_set", { ...ctx.effectiveArgs, ops: fixedOps }) : `change_set with op [${i}] = ${formatCall(toolName, example, spec.discriminator)}` };
      }
      return out;
    }
    const refProblem = checkRefs(v.args, i, `ops[${i}]`);
    if (refProblem) return ctx.fail("INVALID_ARGS", `${prefix}${refProblem}`, { param: `ops[${i}]`, reason: "bad reference" });
    const expanded = await ctx.expandArgs(v.args, doc);
    ops.push({ tool: toolName, ...(spec.discriminator && opName ? { [spec.discriminator]: opName } : {}), ...expanded });
  }
  ctx.args = { ...ctx.args, ops };
}

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  return defaultWrite(ctx as ToolContextImpl);
}

export const module: ToolModule = { name: "change_set", prepare, handle };
