// ErrorCatalog rendering (SPEC §4.3, §5.6): the fix: line and the numbered options come from the catalog templates,
// never from add-in free text. Frozen in wave 2.

import { byName, ERRORS } from "@revit-mcp-next/contracts/catalog";
import { canonicalErrorCode, type FixTemplate } from "@revit-mcp-next/contracts/errors";

export interface FixContext {
  code: string;
  tool: string;
  op: string | null;
  key: string;
  /** Effective args of the failing call (confirm/page calls: the stored args). */
  args: Record<string, unknown>;
  details: Record<string, unknown> | null | undefined;
  doc: { n?: number | null; title?: string | null; year?: number | null } | null | undefined;
}

export interface RenderedFix {
  fix: string;
  options?: string[];
}

const PLACEHOLDER = /\$\{([A-Za-z0-9_.]+)\}/g;
const EXACT = /^\$\{([A-Za-z0-9_.]+)\}$/;
const MAX_CALL_CHARS = 1_500;

class Unresolved extends Error {}

function rootValue(ctx: FixContext, root: string): unknown {
  switch (root) {
    case "details":
      return ctx.details ?? {};
    case "args":
      return ctx.args;
    case "doc":
      return ctx.doc ? { n: ctx.doc.n ? `#${ctx.doc.n}` : undefined, title: ctx.doc.title ?? undefined, year: ctx.doc.year ?? undefined } : {};
    case "tool":
      return ctx.tool;
    case "op":
      return ctx.op ?? undefined;
    case "key":
      return ctx.key;
    default:
      return undefined;
  }
}

export function lookup(ctx: FixContext, path: string): unknown {
  const [root, ...rest] = path.split(".");
  let value = rootValue(ctx, root!);
  for (const part of rest) {
    if (value === null || value === undefined) return undefined;
    if (Array.isArray(value) && /^\d+$/.test(part)) value = value[Number(part)];
    else if (typeof value === "object") value = (value as Record<string, unknown>)[part];
    else return undefined;
  }
  // options/candidates entries may be {value,label}: use the value.
  if (value && typeof value === "object" && !Array.isArray(value) && "value" in (value as Record<string, unknown>) && rest.length > 0 && /^\d+$/.test(rest[rest.length - 1]!))
    return (value as Record<string, unknown>).value;
  return value;
}

function interpolateString(text: string, ctx: FixContext): string {
  return text.replace(PLACEHOLDER, (_, path: string) => {
    const v = lookup(ctx, path);
    if (v === undefined || v === null || v === "") throw new Unresolved(path);
    return typeof v === "string" ? v : Array.isArray(v) ? v.map((x) => (typeof x === "object" ? JSON.stringify(x) : String(x))).join(", ") : typeof v === "object" ? JSON.stringify(v) : String(v);
  });
}

/** Deep template application: exact placeholders keep their type (undefined → key dropped). */
function interpolateValue(value: unknown, ctx: FixContext): unknown {
  if (typeof value === "string") {
    const exact = EXACT.exec(value);
    if (exact) return lookup(ctx, exact[1]!);
    return interpolateString(value, ctx);
  }
  if (Array.isArray(value)) return value.map((v) => interpolateValue(v, ctx)).filter((v) => v !== undefined);
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      const key = EXACT.test(k) || k.includes("${") ? interpolateString(k, ctx) : k;
      const resolved = interpolateValue(v, ctx);
      if (resolved !== undefined) out[key] = resolved;
    }
    return out;
  }
  return value;
}

function conditionHolds(template: FixTemplate, ctx: FixContext): boolean {
  if (!template.when) return true;
  const v = lookup(ctx, template.when.path);
  if (template.when.equals === undefined) return v !== undefined && v !== null && v !== "" && !(Array.isArray(v) && v.length === 0);
  if (typeof v === "string" && typeof template.when.equals === "string") return v.toLowerCase() === template.when.equals.toLowerCase();
  return v === template.when.equals;
}

/** `<tool> <json>` with the discriminator first; long arg sets are summarized. */
export function formatCall(tool: string, args: Record<string, unknown>, discriminator?: string | null): string {
  const ordered = discriminator && discriminator in args ? { [discriminator]: args[discriminator], ...Object.fromEntries(Object.entries(args).filter(([k]) => k !== discriminator)) } : args;
  return `${tool} ${JSON.stringify(ordered)}`;
}

function discriminatorOf(tool: string): string | null {
  return byName.get(tool)?.discriminator ?? null;
}

function renderTemplate(template: FixTemplate, ctx: FixContext): RenderedFix | null {
  try {
    if (!conditionHolds(template, ctx)) return null;
    const note = template.note ? ` (${interpolateString(template.note, ctx)})` : "";
    switch (template.kind) {
      case "call": {
        const tool = interpolateString(template.tool, ctx);
        const args = typeof template.args === "string" ? interpolateValue(template.args, ctx) : interpolateValue(template.args, ctx);
        if (!args || typeof args !== "object" || Array.isArray(args)) return null;
        return { fix: `${formatCall(tool, args as Record<string, unknown>, discriminatorOf(tool))}${note}` };
      }
      case "same_call_with": {
        const set = interpolateValue(template.set, ctx) as Record<string, unknown>;
        const call = formatCall(ctx.tool, { ...ctx.args, ...set }, discriminatorOf(ctx.tool));
        if (call.length > MAX_CALL_CHARS) return { fix: `repeat the same ${ctx.tool} call${Object.keys(set).length ? ` with ${JSON.stringify(set)}` : ""}${note}` };
        return { fix: `${call}${note}` };
      }
      case "same_call_without": {
        const args = { ...ctx.args };
        for (const k of template.drop) delete args[k];
        const call = formatCall(ctx.tool, args, discriminatorOf(ctx.tool));
        if (call.length > MAX_CALL_CHARS) return { fix: `repeat the same ${ctx.tool} call without ${template.drop.join(", ")}${note}` };
        return { fix: `${call}${note}` };
      }
      case "choose": {
        const options = optionList(ctx.details);
        if (options.length === 0) return null;
        const base = template.base ? (interpolateValue(template.base, ctx) as Record<string, unknown>) : template.tool === ctx.tool ? { ...ctx.args } : {};
        const call = formatCall(template.tool, { ...base, [template.arg]: options[0]!.value }, discriminatorOf(template.tool));
        return { fix: `${call}${options.length > 1 ? " (or another option)" : ""}${note}`, options: options.map((o, i) => `${i + 1}) ${o.label}`) };
      }
      case "ask":
        return { fix: `ask the user: ${interpolateString(template.text, ctx)}${note}` };
      case "retry_in":
        return { fix: `retry in ${template.seconds} s${note}` };
    }
  } catch (error) {
    if (error instanceof Unresolved) return null;
    throw error;
  }
  return null;
}

export interface OptionItem {
  value: unknown;
  label: string;
}

/** details.options (or candidates/buttons) as {value,label}. */
export function optionList(details: Record<string, unknown> | null | undefined): OptionItem[] {
  if (!details) return [];
  const raw = (details.options ?? details.candidates ?? details.buttons) as unknown;
  if (!Array.isArray(raw)) return [];
  return raw
    .map((o): OptionItem | null => {
      if (o === null || o === undefined) return null;
      if (typeof o === "string" || typeof o === "number") return { value: o, label: String(o) };
      if (typeof o === "object") {
        const r = o as Record<string, unknown>;
        const value = r.value ?? r.name ?? r.id ?? r.title;
        if (value === undefined) return null;
        return { value, label: String(r.label ?? r.name ?? value) };
      }
      return null;
    })
    .filter((o): o is OptionItem => o !== null);
}

function firstRenderable(templates: FixTemplate | FixTemplate[] | undefined, ctx: FixContext): RenderedFix | null {
  if (!templates) return null;
  for (const t of Array.isArray(templates) ? templates : [templates]) {
    const r = renderTemplate(t, ctx);
    if (r) return r;
  }
  return null;
}

/** Render the fix line (and options) for an error code. */
export function renderFix(ctx: FixContext): RenderedFix {
  const code = canonicalErrorCode(ctx.code);
  const spec = ERRORS[code];
  if (!spec) return { fix: formatCall("help", { topic: code }) };
  const main = firstRenderable(spec.fix, { ...ctx, code }) ?? { fix: formatCall("help", { topic: code }) };
  const alt = firstRenderable(spec.or, { ...ctx, code });
  const options = main.options ?? (optionList(ctx.details).length > 0 && ["AMBIGUOUS_NAME", "NOT_FOUND", "TARGET_AMBIGUOUS", "TARGET_CLOSED", "DOC_NOT_OPEN", "PROJECT_DOC_REQUIRED", "FAMILY_DOC_REQUIRED", "BUTTON_NOT_FOUND"].includes(code)
    ? optionList(ctx.details).map((o, i) => `${i + 1}) ${o.label}`)
    : undefined);
  return { fix: alt && alt.fix !== main.fix ? `${main.fix}, or ${alt.fix}` : main.fix, options };
}

export function errorMeaning(code: string): string | undefined {
  return ERRORS[canonicalErrorCode(code)]?.meaning;
}

export function nothingChanged(code: string): boolean | undefined {
  return ERRORS[canonicalErrorCode(code)]?.nothingChanged;
}
