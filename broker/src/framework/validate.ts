// Signature-derived validation (SPEC §5.3 steps 8-9).
//  - The discriminator: missing → the tool's defaultOp or INVALID_ARGS listing the ops; unknown → UNKNOWN_OP with the closest.
//  - req groups: ',' separates groups; '/' = exactly one alternative; '+' = together; "ids/from/filter" is the sel group.
//  - A missing requirement gives INVALID_ARGS whose example is the op's example filled with the user's values.
//  - Params not used by the op give warn PARAM_IGNORED and are dropped.
// Frozen in wave 2.

import { fullProperties, type ToolSpec } from "@revit-mcp-next/contracts/catalog";
import { TAIL_PARAMS } from "@revit-mcp-next/contracts/catalog/params";
import { canonValue, closest, type ArgError } from "./normalize.js";

export interface OpResolution {
  op: string | null;
  error?: { code: "INVALID_ARGS" | "UNKNOWN_OP"; message: string; details: Record<string, unknown> };
}

/** Determine the op from the discriminator (after normalization). */
export function resolveOp(spec: ToolSpec, args: Record<string, unknown>, prefix = ""): OpResolution {
  if (!spec.discriminator) return { op: null };
  const disc = spec.discriminator;
  const ops = Object.keys(spec.ops);
  const value = args[disc];
  if (value === undefined || value === null || value === "") {
    if (spec.defaultOp) {
      args[disc] = spec.defaultOp;
      return { op: spec.defaultOp };
    }
    return {
      op: null,
      error: {
        code: "INVALID_ARGS",
        message: `${prefix}${spec.name} needs ${disc}: one of ${ops.join(", ")}`,
        details: { param: disc, reason: "missing", values: ops, example: spec.ops[ops[0]!]?.examples[0] },
      },
    };
  }
  const name = String(value);
  if (name in spec.ops) return { op: name };
  const hit = ops.find((o) => canonValue(o) === canonValue(name));
  if (hit) {
    args[disc] = hit;
    return { op: hit };
  }
  const near = closest(name, ops);
  return {
    op: null,
    error: {
      code: "UNKNOWN_OP",
      message: `${prefix}${spec.name} has no ${disc} "${name}"${near ? `; closest: ${near.option}` : ""}`,
      details: { param: disc, value: name, ops, closest: near?.option },
    },
  };
}

type Group = string[][];

export function parseGroups(req: string): Group[] {
  if (!req.trim()) return [];
  return req.split(",").map((g) => g.split("/").map((alt) => alt.split("+").map((p) => p.trim()).filter(Boolean)));
}

function paramList(sig: string): string[] {
  return sig.split(/[,/+]/).map((p) => p.trim()).filter(Boolean);
}

/** "level, points (or start+end)" */
export function describeGroup(group: Group): string {
  if (group.length === 1) return group[0]!.join(" and ");
  if (group.length === 3 && group.every((a) => a.length === 1) && group.map((a) => a[0]).join("/") === "ids/from/filter") return "one of ids, from or filter";
  return group.map((alt, i) => (i === 0 ? alt.join("+") : `or ${alt.join("+")}`)).join(" ");
}

export interface ValidateOptions {
  /** Confirm and page calls replay stored args: skip requirement checks. */
  skipRequired?: boolean;
  prefix?: string;
}

export interface ValidateResult {
  args: Record<string, unknown>;
  warnings: string[];
  error?: ArgError;
}

const present = (args: Record<string, unknown>, p: string) => args[p] !== undefined && args[p] !== null && !(Array.isArray(args[p]) && (args[p] as unknown[]).length === 0);

/** Per-op validation from the req/opt signature. */
export function validateOp(spec: ToolSpec, op: string | null, args: Record<string, unknown>, options: ValidateOptions = {}): ValidateResult {
  const warnings: string[] = [];
  const prefix = options.prefix ?? "";
  const opSpec = spec.ops[op ?? ""];
  if (!opSpec) return { args, warnings, error: { code: "INVALID_ARGS", message: `${prefix}${spec.name}: unknown op`, details: { reason: "unknown op" } } };
  const props = fullProperties(spec);
  const allowed = new Set<string>([...paramList(opSpec.req), ...paramList(opSpec.opt)]);
  for (const t of TAIL_PARAMS) if (t in props) allowed.add(t);
  if (spec.discriminator) allowed.add(spec.discriminator);
  const out: Record<string, unknown> = {};
  const label = op ? `${spec.name} ${spec.discriminator}=${op}` : spec.name;
  for (const [k, v] of Object.entries(args)) {
    if (allowed.has(k)) out[k] = v;
    else warnings.push(`PARAM_IGNORED ${prefix}${k} is not used by ${label}`);
  }
  if (options.skipRequired) return { args: out, warnings };

  const groups = parseGroups(opSpec.req);
  const problems: string[] = [];
  let firstParam: string | undefined;
  for (const group of groups) {
    const complete = group.filter((alt) => alt.every((p) => present(out, p)));
    if (complete.length === 1) {
      const chosen = new Set(complete[0]);
      const extra = group.flat().filter((p) => !chosen.has(p) && present(out, p));
      if (extra.length > 0) {
        problems.push(`use ${describeGroup(group)}, not both (${[...chosen].join("+")} and ${extra.join(", ")})`);
        firstParam ??= extra[0];
      }
      continue;
    }
    if (complete.length > 1) {
      problems.push(`use only one of ${group.map((a) => a.join("+")).join(", ")}`);
      firstParam ??= complete[1]![0];
      continue;
    }
    const partial = group.find((alt) => alt.some((p) => present(out, p)));
    if (partial) {
      const missing = partial.filter((p) => !present(out, p));
      problems.push(`${partial.join("+")} go together; missing ${missing.join(", ")}`);
      firstParam ??= missing[0];
    } else {
      problems.push(`missing ${describeGroup(group)}`);
      firstParam ??= group[0]![0];
    }
  }
  if (problems.length > 0) {
    const needs = groups.map(describeGroup).join(", ");
    return {
      args: out,
      warnings,
      error: {
        code: "INVALID_ARGS",
        message: `${prefix}${label} needs ${needs}; ${problems.join("; ")}`,
        details: { param: firstParam, reason: problems.join("; "), example: fillExample(spec, op, out) },
      },
    };
  }
  return { args: out, warnings };
}

/** The op's example with the user's own values filled in (the INVALID_ARGS fix). */
export function fillExample(spec: ToolSpec, op: string | null, userArgs: Record<string, unknown>): Record<string, unknown> | undefined {
  const opSpec = spec.ops[op ?? ""];
  if (!opSpec || opSpec.examples.length === 0) return undefined;
  const userKeys = Object.keys(userArgs).filter((k) => k !== "confirm" && k !== "page");
  const best = [...opSpec.examples].sort((a, b) => overlap(b, userKeys) - overlap(a, userKeys))[0]!;
  const filled: Record<string, unknown> = { ...best };
  for (const k of userKeys) filled[k] = userArgs[k];
  // Keep one alternative per group: the user's choice wins over the example's.
  for (const group of parseGroups(opSpec.req)) {
    if (group.length < 2) continue;
    const userAlt = group.find((alt) => alt.some((p) => userKeys.includes(p)));
    if (!userAlt) continue;
    for (const alt of group) if (alt !== userAlt) for (const p of alt) if (!userKeys.includes(p)) delete filled[p];
  }
  // Discriminator first for readability.
  if (spec.discriminator && op) return { [spec.discriminator]: op, ...Object.fromEntries(Object.entries(filled).filter(([k]) => k !== spec.discriminator)) };
  return filled;
}

function overlap(example: Record<string, unknown>, keys: string[]): number {
  return keys.filter((k) => k in example).length;
}
