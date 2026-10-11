// change_set (SPEC §6.7, broker part): validates every op with its own tool's per-op rules (normalize + signature),
// checks $ref syntax and backward-only indices, refuses ops that cannot run in a change set (cs:false) with a fix that
// names the standalone call, applies the change_set's units to every op, expands r#/last per op, answers the static
// blast rules of its ops (always / central_open / multi_doc) with one NOT APPLIED plan for the aggregate, then runs the
// add-in key `change_set` as one write (one undo step, confirmed once for the aggregate). Owner: P-CONTROL.
import { byName, keyOf, type Blast } from "@revit-mcp-next/contracts/catalog";
import { defaultWrite } from "../framework/defaults.js";
import type { ToolContextImpl } from "../framework/context.js";
import { formatCall } from "../framework/errors.js";
import { normalizeArgs } from "../framework/normalize.js";
import type { ResolvedDoc, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";
import { resolveOp, validateOp } from "../framework/validate.js";

export const MAX_OPS = 100;
/** Same grammar as the add-in (ChangeEngine.RefPattern): $N, $N.ids, $N.ids[k], $N.<output>, $prev[...]. */
const REF = /^\$(?:(\d+)|prev)(?:\.ids(?:\[(\d+)\])?|\.([A-Za-z_][A-Za-z0-9_]*))?$/;
/** Blast rules the broker answers itself (§6.3); inside a change set they apply to the aggregate. */
const STATIC_RULES: Blast[] = ["always", "central_open", "multi_doc"];
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
  const setUnits = typeof ctx.args.units === "string" ? ctx.args.units : undefined;
  const ops: Record<string, unknown>[] = [];
  const staticHits: Array<{ index: number; key: string; rule: Blast }> = [];
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
    // One unit for the whole set: the add-in scales bare lengths of every op with the change_set's units.
    const unitsKey = Object.keys(rest).find((k) => k.toLowerCase() === "units");
    if (unitsKey !== undefined) {
      if (String(rest[unitsKey]).trim().toLowerCase() !== (setUnits ?? "mm")) ctx.warn("PARAM_IGNORED", `${prefix}units: the change_set's units (${setUnits ?? "mm"}) apply to every op; put units on the change_set`);
      delete rest[unitsKey];
    }
    if (setUnits && "units" in spec.properties) rest.units = setUnits;
    const norm = normalizeArgs(spec, rest, { prefix });
    if (norm.error) return ctx.fail(norm.error.code, norm.error.message, { ...norm.error.details, param: `ops[${i}].${String(norm.error.details.param ?? "")}` });
    for (const w of norm.warnings) ctx.warn(w.split(" ")[0]!, w.slice(w.indexOf(" ") + 1));
    delete norm.args.units;
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
    const rule = staticRule(opSpec.meta.blast, v.args);
    if (rule) staticHits.push({ index: i, key: keyOf(spec.name, opName), rule });
  }
  ctx.args = { ...ctx.args, ops };
  if (staticHits.length > 0 && ctx.args.preview !== true) return staticPlan(ctx, doc, staticHits);
}

/** The broker-evaluated blast rule an op triggers (defaults.ts staticBlast, per op). */
function staticRule(blast: readonly Blast[], args: Record<string, unknown>): Blast | null {
  for (const rule of STATIC_RULES) {
    if (!blast.includes(rule)) continue;
    if (rule === "always") return rule;
    if (rule === "central_open" && args.central === true) return rule;
    if (rule === "multi_doc" && Array.isArray(args.into) && args.into.length > 1) return rule;
  }
  return null;
}

/**
 * NOT APPLIED for the aggregate when an op needs the user's OK by itself (links remove, coordinates...): the whole set
 * waits for one confirmation. The token stores the normalized ops; its stamp is the doc generation now, so the add-in's
 * per-element stale check (§6.4) still protects the plan.
 */
function staticPlan(ctx: ToolContext, doc: ResolvedDoc | null, hits: Array<{ index: number; key: string; rule: Blast }>): ToolOutcome {
  const args: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(ctx.args)) if (k !== "confirm" && k !== "preview") args[k] = v;
  const what = hits.map((h) => `[${h.index}] ${h.key.replace(".", " ")}`).join(", ");
  const plan = `change_set${typeof ctx.args.name === "string" ? ` ${ctx.args.name}` : ""} with ${(ctx.args.ops as unknown[]).length} ops includes ${what}, which always needs the user's OK`;
  const token = ctx.session.confirms.issue({
    tool: ctx.tool,
    key: ctx.key,
    op: null,
    args,
    docKey: doc?.key ?? null,
    instanceId: doc?.instanceId ?? null,
    stamp: doc?.generation ?? 0,
    deleteSet: [],
    rule: hits[0]!.rule,
    plan,
  });
  return {
    status: "not_applied",
    summary: plan,
    data: { rule: hits[0]!.rule, ops: hits.map((h) => [h.index, h.key]), confirm: token, expires_min: 10 },
    next: `ask the user, then change_set {"confirm":"${token}"}`,
  };
}

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  return defaultWrite(ctx as ToolContextImpl);
}

export const module: ToolModule = { name: "change_set", prepare, handle };
