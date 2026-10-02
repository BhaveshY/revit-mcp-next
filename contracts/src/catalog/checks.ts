// Surface gates (SPEC §15) plus catalog integrity checks. Used by `npm run gen:catalog -- --check` and e2e S00.
// A failure here means the advertised surface or the catalog contract is broken; fix the tool file, or update
// e2e/budgets.json in the same commit when a surface growth is intended (lead approval).

import { templatesOf, type FixTemplate } from "../errors.js";
import { INSTRUCTIONS, INSTRUCTIONS_HEAD, INSTRUCTIONS_HEAD_MAX, INSTRUCTIONS_TOTAL_MAX } from "./instructions.js";
import { advertisedTool, byName, CATALOG, ERRORS, fullProperties, listedTools, REGISTRY, toolsList } from "./index.js";
import type { AdvertisedTool, ParamSpec, ToolSpec } from "./types.js";

export const GATES = {
  totalMaxBytes: 90_000,
  perToolMaxBytes: 6_000,
  /** Growth over the budgets.json baseline that needs a baseline update in the same commit. */
  growthMax: 0.05,
  descriptionMaxChars: 220,
  depthMax: 5,
  namePattern: /^[a-z][a-z0-9_]{1,40}$/,
  qualifiedPrefix: "mcp__revit__",
  qualifiedMax: 64,
  defaultToolCount: 37,
  coreToolCount: 17,
  instructionsHeadMax: INSTRUCTIONS_HEAD_MAX,
  instructionsTotalMax: INSTRUCTIONS_TOTAL_MAX,
  /** Upstream names the revit-mcp-cowork hook acts on (D4 §7.7.1). */
  bannedNames: [
    "ai_element_filter",
    "get_current_view_elements",
    "get_selected_elements",
    "get_available_family_types",
    "say_hello",
    "delete_element",
    "send_code_to_revit",
    "operate_element",
    "color_elements",
    "create_point_based_element",
    "create_line_based_element",
    "create_surface_based_element",
    "tag_all_walls",
    "tag_all_rooms",
    "get_current_view_info",
    "get_material_quantities",
    "analyze_model_statistics",
    "export_room_data",
    "store_project_data",
    "store_room_data",
    "query_stored_data",
  ],
  /** Keywords Codex drops or Claude rejects (D1 §1 C1/C3). */
  forbiddenKeywords: [
    "const", "default", "minimum", "maximum", "pattern", "format", "$schema", "$ref", "$defs", "definitions",
    "anyOf", "oneOf", "allOf", "not", "minLength", "maxLength", "minItems", "maxItems", "additionalProperties",
    "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "uniqueItems",
  ],
  /** Output budgets (§15), asserted by e2e. */
  outputs: {
    statusCompactMaxBytes: 2_048,
    pageMaxBytes: 8_192,
    resultMaxBytes: 12_000,
    resultFullMaxBytes: 40_000,
    imageBase64MaxBytes: 1_500_000,
    readManyMaxImages: 2,
    readManyMaxImageBytes: 2 * 1024 * 1024,
  },
} as const;

/** e2e/budgets.json (committed baseline). */
export interface Budgets {
  toolsListBytes: { default: number; withOptIn: number; core: number };
  perTool?: Record<string, number>;
  updatedAt?: string;
  note?: string;
}

export interface SurfaceStats {
  toolCount: number;
  toolCountWithOptIn: number;
  coreToolCount: number;
  toolsListBytesDefault: number;
  toolsListBytesWithOptIn: number;
  toolsListBytesCore: number;
  maxDescriptionChars: number;
  maxSchemaDepth: number;
  registryKeys: number;
  addinBoundKeys: number;
  instructionsHeadChars: number;
  instructionsTotalChars: number;
  largestTool: [string, number];
  perTool: Record<string, number>;
}

export interface CheckResult {
  ok: boolean;
  failures: string[];
  warnings: string[];
  stats: SurfaceStats;
}

export const bytes = (value: unknown): number => Buffer.byteLength(JSON.stringify(value), "utf8");

function schemaDepth(node: unknown): number {
  if (!node || typeof node !== "object") return 0;
  const n = node as Record<string, unknown>;
  let d = 0;
  if (n.properties && typeof n.properties === "object") for (const v of Object.values(n.properties as Record<string, unknown>)) d = Math.max(d, schemaDepth(v));
  if (n.items) d = Math.max(d, schemaDepth(n.items));
  return 1 + d;
}

function walkSchema(node: unknown, path: string, visit: (node: Record<string, unknown>, path: string) => void): void {
  if (!node || typeof node !== "object" || Array.isArray(node)) return;
  const n = node as Record<string, unknown>;
  visit(n, path);
  if (n.properties && typeof n.properties === "object") for (const [k, v] of Object.entries(n.properties as Record<string, unknown>)) walkSchema(v, `${path}.${k}`, visit);
  if (n.items) walkSchema(n.items, `${path}[]`, visit);
}

/** Parse a req signature into groups → alternatives → params. */
export function parseSignature(req: string): string[][][] {
  if (!req.trim()) return [];
  return req.split(",").map((group) => group.split("/").map((alt) => alt.split("+").map((p) => p.trim()).filter(Boolean)));
}

function paramNamesOf(sig: string): string[] {
  return sig
    .split(/[,/+]/)
    .map((p) => p.trim())
    .filter(Boolean);
}

export function surfaceStats(): SurfaceStats {
  const all = toolsList({ includeOptIn: true }).tools;
  const def = toolsList({}).tools;
  const core = toolsList({ profile: "core" }).tools;
  const perTool: Record<string, number> = {};
  let largest: [string, number] = ["", 0];
  for (const t of all) {
    const b = bytes(t);
    perTool[t.name] = b;
    if (b > largest[1]) largest = [t.name, b];
  }
  return {
    toolCount: def.length,
    toolCountWithOptIn: all.length,
    coreToolCount: core.length,
    toolsListBytesDefault: bytes({ tools: def }),
    toolsListBytesWithOptIn: bytes({ tools: all }),
    toolsListBytesCore: bytes({ tools: core }),
    maxDescriptionChars: Math.max(...all.map((t) => t.description.length)),
    maxSchemaDepth: Math.max(...all.map((t) => schemaDepth(t.inputSchema))),
    registryKeys: REGISTRY.length,
    addinBoundKeys: REGISTRY.filter((r) => r.impl === "addin" || r.impl === "both").length,
    instructionsHeadChars: INSTRUCTIONS_HEAD.length,
    instructionsTotalChars: INSTRUCTIONS.length,
    largestTool: largest,
    perTool,
  };
}

/** Advertised-surface gates on a tools/list payload (also used by S00 on what the broker actually served). */
export function checkAdvertised(tools: AdvertisedTool[], failures: string[], label = "tools/list"): void {
  const names = new Set<string>();
  for (const t of tools) {
    if (names.has(t.name)) failures.push(`${label}: duplicate tool name ${t.name}`);
    names.add(t.name);
    if (!GATES.namePattern.test(t.name)) failures.push(`${label}: name ${t.name} does not match ${GATES.namePattern}`);
    if ((GATES.bannedNames as readonly string[]).includes(t.name)) failures.push(`${label}: banned name ${t.name}`);
    if ((GATES.qualifiedPrefix + t.name).length > GATES.qualifiedMax) failures.push(`${label}: ${GATES.qualifiedPrefix}${t.name} longer than ${GATES.qualifiedMax}`);
    if (t.description.length > GATES.descriptionMaxChars) failures.push(`${label}: ${t.name} description ${t.description.length} > ${GATES.descriptionMaxChars} chars`);
    const b = bytes(t);
    if (b > GATES.perToolMaxBytes) failures.push(`${label}: ${t.name} is ${b} B > ${GATES.perToolMaxBytes} B`);
    const root = t.inputSchema as Record<string, unknown>;
    if (root.type !== "object") failures.push(`${label}: ${t.name} root type must be object`);
    for (const key of Object.keys(root)) if (!["type", "properties", "required"].includes(key)) failures.push(`${label}: ${t.name} root has ${key}`);
    if ("outputSchema" in (t as unknown as Record<string, unknown>)) failures.push(`${label}: ${t.name} has outputSchema`);
    const depth = schemaDepth(root);
    if (depth > GATES.depthMax) failures.push(`${label}: ${t.name} schema depth ${depth} > ${GATES.depthMax}`);
    walkSchema(root, t.name, (node, path) => {
      for (const key of Object.keys(node)) {
        if ((GATES.forbiddenKeywords as readonly string[]).includes(key)) failures.push(`${label}: forbidden keyword ${path}.${key}`);
        else if (!["type", "description", "enum", "items", "properties", "required"].includes(key)) failures.push(`${label}: unexpected keyword ${path}.${key}`);
      }
      if (typeof node.type !== "string") failures.push(`${label}: ${path} has no type`);
      if (node.type === "array" && (!node.items || typeof (node.items as Record<string, unknown>).type !== "string")) failures.push(`${label}: ${path} array without typed items`);
      if (path !== t.name && path.split(".").length >= 2 && !path.endsWith("[]") && typeof node.description !== "string") failures.push(`${label}: ${path} has no description`);
      if (node.enum !== undefined && (!Array.isArray(node.enum) || node.enum.length === 0)) failures.push(`${label}: ${path} enum must be a non-empty array`);
    });
    const required = (root.required as string[] | undefined) ?? [];
    const props = (root.properties as Record<string, unknown>) ?? {};
    for (const r of required) if (!(r in props)) failures.push(`${label}: ${t.name} requires unknown param ${r}`);
  }
}

function checkTemplateTargets(code: string, template: FixTemplate, failures: string[]): void {
  const check = (tool: string, args: unknown) => {
    if (tool.includes("${")) return;
    const spec = byName.get(tool);
    if (!spec) {
      failures.push(`error ${code}: fix names unknown tool ${tool}`);
      return;
    }
    if (args && typeof args === "object" && spec.discriminator) {
      const value = (args as Record<string, unknown>)[spec.discriminator];
      if (typeof value === "string" && !value.includes("${") && !(value in spec.ops)) failures.push(`error ${code}: fix names unknown ${tool} ${spec.discriminator} ${value}`);
    }
    if (args && typeof args === "object") {
      const props = fullProperties(spec);
      for (const key of Object.keys(args as Record<string, unknown>)) if (!key.includes("${") && !(key in props)) failures.push(`error ${code}: fix passes unknown param ${tool}.${key}`);
    }
  };
  if (template.kind === "call") check(template.tool, template.args);
  if (template.kind === "choose") {
    check(template.tool, template.base ?? {});
    const spec = byName.get(template.tool);
    if (spec && !(template.arg in fullProperties(spec))) failures.push(`error ${code}: choose names unknown param ${template.tool}.${template.arg}`);
  }
}

/** Catalog integrity: ops, signatures, examples, discriminators, units, synonyms, error templates. */
export function checkCatalog(failures: string[], warnings: string[]): void {
  const keys = new Set<string>();
  for (const r of REGISTRY) {
    if (keys.has(r.key)) failures.push(`registry: duplicate key ${r.key}`);
    keys.add(r.key);
  }
  for (const tool of CATALOG) checkTool(tool, failures, warnings);
  for (const [code, spec] of Object.entries(ERRORS)) {
    if (code !== spec.code) failures.push(`error ${code}: spec.code is ${spec.code}`);
    if (!spec.meaning) failures.push(`error ${code}: no meaning`);
    for (const template of templatesOf(spec)) checkTemplateTargets(code, template, failures);
  }
  const core = listedTools({ profile: "core" });
  if (core.length !== GATES.coreToolCount) failures.push(`core profile has ${core.length} tools, expected ${GATES.coreToolCount}`);
}

function checkTool(tool: ToolSpec, failures: string[], warnings: string[]): void {
  const props = fullProperties(tool);
  const ops = Object.keys(tool.ops);
  if (ops.length === 0) failures.push(`${tool.name}: no ops`);
  if (tool.discriminator === null && (ops.length !== 1 || ops[0] !== "")) failures.push(`${tool.name}: tools without a discriminator have exactly one op ""`);
  if (tool.discriminator && tool.discriminator !== "op") {
    const disc = tool.properties[tool.discriminator];
    if (!disc || JSON.stringify(disc.enum) !== JSON.stringify(ops)) failures.push(`${tool.name}: ${tool.discriminator} enum must equal the op keys`);
  }
  if (tool.discriminator === "op" && "op" in tool.properties) failures.push(`${tool.name}: op param is generated; do not list it in properties`);
  if (tool.defaultOp !== undefined && !(tool.defaultOp in tool.ops)) failures.push(`${tool.name}: defaultOp ${tool.defaultOp} is not an op`);
  if (!tool.profiles.includes("full")) failures.push(`${tool.name}: every tool is in the full profile`);
  for (const [name, p] of Object.entries(props)) checkParam(`${tool.name}.${name}`, p, failures);
  for (const [opName, spec] of Object.entries(tool.ops)) {
    const label = opName ? `${tool.name}.${opName}` : tool.name;
    for (const p of [...paramNamesOf(spec.req), ...paramNamesOf(spec.opt)]) if (!(p in props)) failures.push(`${label}: signature names unknown param ${p}`);
    if (spec.examples.length === 0) failures.push(`${label}: no example`);
    for (const example of spec.examples) {
      for (const key of Object.keys(example)) if (!(key in props)) failures.push(`${label}: example uses unknown param ${key}`);
      if (tool.discriminator && example[tool.discriminator] !== opName) failures.push(`${label}: example must set ${tool.discriminator}="${opName}"`);
    }
    if (spec.meta.min !== 2024 && spec.meta.min !== 2027) failures.push(`${label}: min must be 2024 or 2027`);
  }
  if (tool.optIn && tool.profiles.includes("core")) warnings.push(`${tool.name}: opt-in tools are not part of the core profile`);
}

function checkParam(path: string, p: ParamSpec, failures: string[]): void {
  if (!p || typeof p !== "object") {
    failures.push(`${path}: not a param`);
    return;
  }
  if (!p.description) failures.push(`${path}: no description`);
  if (p.unit && !["mm", "deg", "pct", "m2", "m3", "s", "px"].includes(p.unit)) failures.push(`${path}: unknown unit ${p.unit}`);
  if (p.synonyms) {
    const values = p.enum ?? (p.items as ParamSpec | undefined)?.enum ?? [];
    if (values.length === 0) failures.push(`${path}: synonyms on a param without enum`);
    for (const [k, v] of Object.entries(p.synonyms)) if (!values.includes(v)) failures.push(`${path}: synonym ${k} -> ${v} is not an enum value`);
  }
}

function checkInstructions(failures: string[]): void {
  if (INSTRUCTIONS_HEAD.length > GATES.instructionsHeadMax) failures.push(`instructions head ${INSTRUCTIONS_HEAD.length} > ${GATES.instructionsHeadMax}`);
  if (INSTRUCTIONS.length > GATES.instructionsTotalMax) failures.push(`instructions ${INSTRUCTIONS.length} > ${GATES.instructionsTotalMax}`);
  for (const must of ["status", "fix:", "capture", "screen automation"]) if (!INSTRUCTIONS_HEAD.includes(must)) failures.push(`instructions head does not mention "${must}"`);
}

/** Run every gate. `budgets` = parsed e2e/budgets.json (null skips the growth gate with a warning). */
export function runChecks(budgets: Budgets | null): CheckResult {
  const failures: string[] = [];
  const warnings: string[] = [];
  const stats = surfaceStats();
  const all = CATALOG.map(advertisedTool);
  checkAdvertised(all, failures, "catalog");
  if (stats.toolCount !== GATES.defaultToolCount) failures.push(`default profile lists ${stats.toolCount} tools, expected ${GATES.defaultToolCount}`);
  if (stats.toolsListBytesWithOptIn > GATES.totalMaxBytes) failures.push(`tools/list ${stats.toolsListBytesWithOptIn} B > ${GATES.totalMaxBytes} B`);
  if (budgets) {
    const growth = (current: number, baseline: number, what: string) => {
      if (baseline > 0 && current > baseline * (1 + GATES.growthMax))
        failures.push(`${what} grew to ${current} B (baseline ${baseline} B, > ${Math.round(GATES.growthMax * 100)} %); update e2e/budgets.json in the same commit`);
      else if (current !== baseline) warnings.push(`${what} is ${current} B (baseline ${baseline} B)`);
    };
    growth(stats.toolsListBytesDefault, budgets.toolsListBytes.default, "tools/list (default)");
    growth(stats.toolsListBytesWithOptIn, budgets.toolsListBytes.withOptIn, "tools/list (with run_csharp)");
    growth(stats.toolsListBytesCore, budgets.toolsListBytes.core, "tools/list (core)");
  } else {
    warnings.push("no e2e/budgets.json baseline; growth gate skipped");
  }
  checkCatalog(failures, warnings);
  checkInstructions(failures);
  return { ok: failures.length === 0, failures, warnings, stats };
}

/** Structural deep equality (object key order ignored, array order kept). */
export function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== "object") return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) {
    const bb = b as unknown[];
    return a.length === bb.length && a.every((v, i) => deepEqual(v, bb[i]));
  }
  const ka = Object.keys(a as object);
  const kb = Object.keys(b as object);
  if (ka.length !== kb.length) return false;
  return ka.every((k) => Object.prototype.hasOwnProperty.call(b, k) && deepEqual((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k]));
}

/** First difference between two JSON values (for readable failure messages). */
export function firstDifference(a: unknown, b: unknown, path = "$"): string | null {
  if (deepEqual(a, b)) return null;
  if (typeof a !== "object" || typeof b !== "object" || a === null || b === null || Array.isArray(a) !== Array.isArray(b)) return `${path}: ${JSON.stringify(a)?.slice(0, 120)} != ${JSON.stringify(b)?.slice(0, 120)}`;
  if (Array.isArray(a)) {
    const bb = b as unknown[];
    if (a.length !== bb.length) return `${path}: length ${a.length} != ${bb.length}`;
    for (let i = 0; i < a.length; i++) {
      const d = firstDifference(a[i], bb[i], `${path}[${i}]`);
      if (d) return d;
    }
    return null;
  }
  const ra = a as Record<string, unknown>;
  const rb = b as Record<string, unknown>;
  for (const k of new Set([...Object.keys(ra), ...Object.keys(rb)])) {
    const d = firstDifference(ra[k], rb[k], `${path}.${k}`);
    if (d) return d;
  }
  return null;
}
