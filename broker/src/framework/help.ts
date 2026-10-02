// help rendering, generated from the catalog (SPEC §5.7, §5.8, D1 §4B help): help {}, help {tool}, help {tool,op},
// help {topic} (topics, error/warning/notice codes). The same content backs the prompts and revit://help resources.
// Frozen in wave 2 (topics.ts content belongs to W3-DOCS).

import { byName, CATALOG, ERRORS, fullProperties, type ParamSpec, type ToolSpec } from "@revit-mcp-next/contracts/catalog";
import { INSTRUCTIONS } from "@revit-mcp-next/contracts/catalog/instructions";
import { findTopic, TOPIC_NAMES } from "@revit-mcp-next/contracts/catalog/topics";
import { TAIL_PARAMS } from "@revit-mcp-next/contracts/catalog/params";
import { canonicalErrorCode, NOTICE_CODES, templatesOf, WARNING_CODES, type FixTemplate } from "@revit-mcp-next/contracts/errors";
import { closest } from "./normalize.js";
import { formatCall } from "./errors.js";
import { parseGroups } from "./validate.js";
import type { ToolOutcome } from "./types.js";

const GROUP_ORDER = ["session", "read", "visual", "ui", "write", "control", "optin"] as const;

/** help {}: the instructions, a one-line index of tools by group and the topics. */
export function helpIndex(listed: ToolSpec[]): ToolOutcome {
  const tools: Record<string, string[]> = {};
  for (const g of GROUP_ORDER) {
    const names = listed.filter((t) => t.group === g).map((t) => t.name);
    if (names.length) tools[g] = names;
  }
  return {
    status: "ok",
    summary: `help for ${listed.length} Revit tools; help {"tool":"<name>"} lists its ops, help {"tool":"<name>","op":"<op>"} gives params and a working example`,
    data: { instructions: INSTRUCTIONS, tools, topics: [...TOPIC_NAMES, "<ERROR_CODE>"] },
    next: `help {"tool":"create_elements","op":"wall"}`,
    doc: null,
    raw: true,
  };
}

function requiredList(req: string): string[] {
  return parseGroups(req).map((group) => group.map((alt) => alt.join("+")).join("|"));
}

function optionalList(opt: string): string[] {
  return opt.split(",").map((s) => s.trim()).filter(Boolean);
}

function opSummary(spec: ToolSpec, op: string): Record<string, unknown> {
  const o = spec.ops[op]!;
  const out: Record<string, unknown> = { required: requiredList(o.req), optional: optionalList(o.opt) };
  if (o.examples[0]) out.example = o.examples[0];
  if (o.meta.min !== 2024) out.min = o.meta.min;
  if (o.meta.blast.length) out.confirm = o.meta.blast;
  if (o.meta.job !== "never") out.job = o.meta.job;
  if (o.meta.ui) out.needs_active_doc = true;
  if (spec.name !== "change_set" && o.meta.kind === "write" && !o.meta.cs) out.change_set = false;
  return out;
}

/** help {tool}: every op with required/optional params and one example. */
export function helpTool(spec: ToolSpec): ToolOutcome {
  const ops = Object.keys(spec.ops);
  const data: Record<string, unknown> = { tool: spec.name, description: spec.description };
  if (spec.discriminator) {
    data.discriminator = spec.discriminator;
    data.ops = Object.fromEntries(ops.map((op) => [op, opSummary(spec, op)]));
  } else {
    Object.assign(data, opSummary(spec, ""));
  }
  const tail = TAIL_PARAMS.filter((p) => p in spec.properties);
  if (tail.length) data.every_op = tail;
  const firstOp = ops[0] ?? "";
  return {
    status: "ok",
    summary: spec.discriminator ? `${spec.name}: ${ops.length} ${spec.discriminator === "op" ? "ops" : `${spec.discriminator} values`}; params after ';' are optional` : `${spec.name}: params and an example`,
    data,
    next: spec.discriminator ? `help {"tool":"${spec.name}","op":"${firstOp}"}` : spec.ops[""]?.examples[0] ? formatCall(spec.name, spec.ops[""]!.examples[0]!) : undefined,
    doc: null,
    raw: true,
  };
}

function paramDoc(p: ParamSpec): Record<string, unknown> {
  const out: Record<string, unknown> = { type: p.type === "array" ? `${(p.items as ParamSpec | undefined)?.type ?? "any"}[]` : p.type };
  if (p.unit) out.unit = p.unit;
  const values = p.enum ?? (p.items as ParamSpec | undefined)?.enum;
  if (values) out.values = values.length > 16 ? [...values.slice(0, 16), `+${values.length - 16} more`] : values;
  out.desc = p.description;
  return out;
}

/** help {tool, op}: full param table, behaviour notes, registry facts and examples. */
export function helpOp(spec: ToolSpec, op: string): ToolOutcome {
  const o = spec.ops[op]!;
  const props = fullProperties(spec);
  const names = new Set([...requiredList(o.req).flatMap((g) => g.split(/[|+]/)), ...optionalList(o.opt), ...TAIL_PARAMS.filter((p) => p in props)]);
  if (spec.discriminator) names.delete(spec.discriminator);
  const params: Record<string, unknown> = {};
  for (const name of names) if (props[name]) params[name] = paramDoc(props[name]!);
  const data: Record<string, unknown> = {
    tool: spec.name,
    ...(spec.discriminator ? { [spec.discriminator]: op } : {}),
    key: op ? `${spec.name}.${op}` : spec.name,
    ...(o.help ? { help: o.help } : {}),
    required: requiredList(o.req),
    optional: optionalList(o.opt),
    params,
    examples: o.examples.slice(0, 3),
    kind: o.meta.kind,
    min: o.meta.min,
    confirm: o.meta.blast.length ? o.meta.blast : null,
    job: o.meta.job,
    needs_active_doc: o.meta.ui,
    change_set: o.meta.cs,
    scope: o.meta.scope,
    ...(o.api ? { api: o.api } : {}),
    ...(o.errors?.length ? { errors: o.errors } : {}),
  };
  const example = o.examples[0];
  return {
    status: "ok",
    summary: `${spec.name}${op ? ` ${spec.discriminator}=${op}` : ""}: ${requiredList(o.req).length ? `needs ${requiredList(o.req).join(", ")}` : "no required params"}`,
    data,
    next: example ? formatCall(spec.name, example, spec.discriminator) : undefined,
    doc: null,
    raw: true,
  };
}

function describeTemplate(t: FixTemplate): string {
  switch (t.kind) {
    case "call":
      return `call ${t.tool} ${typeof t.args === "string" ? t.args : JSON.stringify(t.args)}`;
    case "same_call_with":
      return `repeat the same call with ${JSON.stringify(t.set)}`;
    case "same_call_without":
      return `repeat the same call without ${t.drop.join(", ")}`;
    case "choose":
      return `choose one of the options, then ${t.tool} {${t.arg}: <option>}`;
    case "ask":
      return `ask the user: ${t.text}`;
    case "retry_in":
      return `retry once in ${t.seconds} s`;
  }
}

/** help {topic}: a topic, an error code, or a warning/notice code. */
export function helpTopic(name: string): ToolOutcome | null {
  const topic = findTopic(name);
  if (topic) {
    return {
      status: "ok",
      summary: `help topic ${topic.name}: ${topic.title}`,
      data: { topic: topic.name, title: topic.title, text: topic.text, ...(topic.data !== undefined ? { data: topic.data } : {}) },
      doc: null,
      raw: true,
    };
  }
  const code = canonicalErrorCode(name.trim().toUpperCase());
  const spec = ERRORS[code];
  if (spec) {
    return {
      status: "ok",
      summary: `error ${code}: ${spec.meaning}`,
      data: {
        code,
        meaning: spec.meaning,
        nothing_changed: spec.nothingChanged,
        ...(spec.details ? { details: spec.details } : {}),
        fix: templatesOf(spec).map(describeTemplate),
      },
      doc: null,
      raw: true,
    };
  }
  if (WARNING_CODES[code]) return { status: "ok", summary: `warning ${code}: ${WARNING_CODES[code]}`, data: { code, kind: "warning", meaning: WARNING_CODES[code] }, doc: null, raw: true };
  if (NOTICE_CODES[code]) return { status: "ok", summary: `notice ${code}: ${NOTICE_CODES[code]}`, data: { code, kind: "notice", meaning: NOTICE_CODES[code] }, doc: null, raw: true };
  return null;
}

/** Everything a help resource or prompt can name: tools, tool.op, topics, error codes. */
export function helpNames(listed: ToolSpec[]): string[] {
  const names: string[] = [];
  for (const t of listed) {
    names.push(t.name);
    if (t.discriminator) for (const op of Object.keys(t.ops)) names.push(`${t.name}.${op}`);
  }
  names.push(...TOPIC_NAMES, ...Object.keys(ERRORS));
  return names;
}

/** Resolve "tool", "tool.op", a topic or a code to an outcome (resources). */
export function helpFor(name: string, listed: ToolSpec[]): ToolOutcome | null {
  const trimmed = name.trim();
  if (!trimmed || trimmed === "overview") return helpIndex(listed);
  const dot = trimmed.indexOf(".");
  const toolName = dot > 0 ? trimmed.slice(0, dot) : trimmed;
  const spec = byName.get(toolName);
  if (spec) {
    if (dot > 0) {
      const op = trimmed.slice(dot + 1);
      return op in spec.ops ? helpOp(spec, op) : null;
    }
    return helpTool(spec);
  }
  return helpTopic(trimmed);
}

export function closestTool(name: string): string | undefined {
  return closest(name, CATALOG.map((t) => t.name))?.option;
}
