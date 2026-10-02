// Lenient input normalization (SPEC §5.3 steps 1-7). Anything unsafe becomes INVALID_ARGS, never a guess.
//  1. camelCase/odd spellings → snake_case when that names a real param (warn PARAM_RENAMED); unknown keys within edit
//     distance ≤2 of a real param → INVALID_ARGS "did you mean"; other unknown keys dropped (warn PARAM_IGNORED).
//  2. arrays: scalar → [scalar], JSON-array strings parsed, comma-separated strings split.
//  3. numbers: numeric strings converted; strings with units converted (lengths mm cm m in ft, 12'6", 900mm; angles °/deg/rad;
//     slope %; areas, volumes, seconds). Plain numbers stay in the call's `units` (default mm) for the add-in to scale.
//  4. booleans: true/yes/1/on and false/no/0/off.
//  5. enums: exact, case/separator-insensitive, then the param's synonyms, else INVALID_ARGS listing the values.
//  6. integer/number ranges stated in descriptions ("1-500") are clamped (warn VALUE_CLAMPED).
//  7. ids: numbers or strings (UniqueIds) accepted; numeric id strings become numbers.
// Frozen in wave 2.

import { byName, fullProperties, type ParamSpec, type ParamUnit, type ToolSpec } from "@revit-mcp-next/contracts/catalog";

export interface ArgError {
  code: "INVALID_ARGS";
  message: string;
  details: Record<string, unknown>;
}

export interface NormalizeResult {
  args: Record<string, unknown>;
  warnings: string[];
  error?: ArgError;
}

const canonKey = (s: string) => s.toLowerCase().replace(/[\s_\-.]/g, "");
export const canonValue = (s: string) => s.trim().toLowerCase().replace(/[\s_\-]/g, "");

/** camelCase / PascalCase / kebab / spaced → snake_case. */
export function toSnake(key: string): string {
  return key
    .trim()
    .replace(/([a-z0-9])([A-Z])/g, "$1_$2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1_$2")
    .replace(/[\s\-.]+/g, "_")
    .toLowerCase();
}

/** Old (v2) and common alternative param names → the catalog param, applied only when the target exists in the tool. */
const PARAM_ALIASES: Record<string, string[]> = {
  ids: ["element_ids", "element_id", "elementid", "unique_ids", "uniqueids", "id", "elements", "element"],
  category: ["categories", "cat"],
  view: ["view_id", "view_name", "viewid"],
  views: ["view_ids", "view_names"],
  level: ["level_id", "level_name", "levelid"],
  levels: ["level_ids", "level_names"],
  type: ["type_id", "type_name", "family_symbol", "family_symbol_id", "symbol", "symbol_id", "family_type", "typeid"],
  at: ["location", "position", "point", "insertion_point", "center_point"],
  by: ["translation", "vector", "delta", "move_by"],
  name: ["new_name", "title"],
  path: ["file", "file_path", "filepath", "output_path", "template_path"],
  template: ["template_path"],
  sheet: ["sheet_id", "sheet_number"],
  schedule: ["schedule_id", "schedule_name"],
  family: ["family_name"],
  host: ["host_id", "host_element_id"],
  confirm: ["token", "confirm_token", "confirmation"],
  page: ["cursor", "next_page", "page_token"],
  limit: ["max", "max_results", "count_limit", "page_size"],
  doc: ["document", "document_title", "target"],
  where: ["parameter_equals", "conditions", "rules_text"],
  allow_pinned: ["allow_pinned_host"],
  values: ["parameters", "params_values"],
  text: ["content", "note"],
  code: ["script", "source"],
};
const ALIAS_LOOKUP: Map<string, string[]> = (() => {
  const map = new Map<string, string[]>();
  for (const [target, aliases] of Object.entries(PARAM_ALIASES)) for (const a of aliases) map.set(canonKey(a), [...(map.get(canonKey(a)) ?? []), target]);
  return map;
})();

/** Array params whose strings are never split on commas (conditions, references, free text). */
const NO_SPLIT = new Set(["where", "rules", "filters", "refs", "scenarios_text", "text", "symbols", "protected_view_names"]);
/** Params whose empty string means "absent". */
const EMPTY_IS_ABSENT = new Set(["doc", "page", "confirm", "from", "instance", "view", "level", "type", "units", "detail"]);

export function editDistance(a: string, b: string): number {
  if (a === b) return 0;
  const m = a.length;
  const n = b.length;
  if (Math.abs(m - n) > 3) return 99;
  const dp = Array.from({ length: m + 1 }, (_, i) => [i, ...new Array<number>(n).fill(0)]);
  for (let j = 1; j <= n; j++) dp[0]![j] = j;
  for (let i = 1; i <= m; i++)
    for (let j = 1; j <= n; j++)
      dp[i]![j] = Math.min(dp[i - 1]![j]! + 1, dp[i]![j - 1]! + 1, dp[i - 1]![j - 1]! + (a[i - 1] === b[j - 1] ? 0 : 1));
  return dp[m]![n]!;
}

export function closest(word: string, options: string[]): { option: string; distance: number } | null {
  let best: { option: string; distance: number } | null = null;
  const w = word.toLowerCase();
  for (const option of options) {
    const o = option.toLowerCase();
    let d = editDistance(w, o);
    if (d > 0 && (o.startsWith(w) || w.startsWith(o)) && Math.min(w.length, o.length) >= 3) d = Math.min(d, 1);
    if (!best || d < best.distance) best = { option, distance: d };
  }
  return best;
}

// ------------------------------------------------------------------------------------------------ units

const LENGTH_TO_MM: Record<string, number> = { mm: 1, millimeter: 1, millimeters: 1, millimetre: 1, millimetres: 1, cm: 10, m: 1000, meter: 1000, meters: 1000, metre: 1000, metres: 1000, in: 25.4, inch: 25.4, inches: 25.4, '"': 25.4, ft: 304.8, foot: 304.8, feet: 304.8, "'": 304.8 };
export const UNIT_FACTORS: Record<string, number> = { mm: 1, cm: 10, m: 1000, in: 25.4, ft: 304.8 };

function num(text: string): number {
  return Number(text.replace(",", "."));
}

/** Parse a string with an optional unit for a param unit. `callUnits` is the unit plain numbers are in (lengths). */
export function parseWithUnit(raw: string, unit: ParamUnit | undefined, callUnits: string): number | null {
  const s = raw.trim().replace(/ /g, " ");
  if (s === "") return null;
  const plain = /^[-+]?(\d+(\.\d*)?|\.\d+)(e[-+]?\d+)?$/i;
  const comma = /^[-+]?\d+,\d+$/;
  if (plain.test(s)) return Number(s);
  if (comma.test(s)) return num(s);
  switch (unit) {
    case "mm": {
      const factor = UNIT_FACTORS[callUnits] ?? 1;
      const feetInches = /^([-+]?\d+(?:[.,]\d+)?)\s*(?:'|ft|feet|foot)\s*(?:(\d+(?:[.,]\d+)?)\s*(?:"|''|in|inch|inches))?$/i.exec(s);
      if (feetInches) {
        const mm = num(feetInches[1]!) * 304.8 + (feetInches[2] ? num(feetInches[2]) * 25.4 : 0) * (num(feetInches[1]!) < 0 ? -1 : 1);
        return round(mm / factor);
      }
      const m = /^([-+]?\d+(?:[.,]\d+)?)\s*([a-z"']+)$/i.exec(s);
      if (m) {
        const f = LENGTH_TO_MM[m[2]!.toLowerCase()];
        if (f !== undefined) return round((num(m[1]!) * f) / factor);
      }
      return null;
    }
    case "deg": {
      const m = /^([-+]?\d+(?:[.,]\d+)?)\s*(°|deg|degs|degree|degrees|rad|rads|radian|radians)?$/i.exec(s);
      if (!m) return null;
      const v = num(m[1]!);
      return m[2] && /^rad/i.test(m[2]) ? round((v * 180) / Math.PI) : v;
    }
    case "pct": {
      const m = /^([-+]?\d+(?:[.,]\d+)?)\s*(%|percent|pct)?$/i.exec(s);
      return m ? num(m[1]!) : null;
    }
    case "m2": {
      const m = /^([-+]?\d+(?:[.,]\d+)?)\s*(m2|m²|sqm|mm2|mm²|cm2|cm²|ft2|ft²|sf|sqft)?$/i.exec(s);
      if (!m) return null;
      const f: Record<string, number> = { mm2: 1e-6, "mm²": 1e-6, cm2: 1e-4, "cm²": 1e-4, ft2: 0.09290304, "ft²": 0.09290304, sf: 0.09290304, sqft: 0.09290304 };
      return num(m[1]!) * (m[2] ? (f[m[2].toLowerCase()] ?? 1) : 1);
    }
    case "m3": {
      const m = /^([-+]?\d+(?:[.,]\d+)?)\s*(m3|m³|cbm|ft3|ft³|cf|l|liters?|litres?)?$/i.exec(s);
      if (!m) return null;
      const f: Record<string, number> = { ft3: 0.0283168466, "ft³": 0.0283168466, cf: 0.0283168466, l: 0.001, liter: 0.001, liters: 0.001, litre: 0.001, litres: 0.001 };
      return num(m[1]!) * (m[2] ? (f[m[2].toLowerCase()] ?? 1) : 1);
    }
    case "s": {
      const m = /^(\d+(?:[.,]\d+)?)\s*(s|sec|secs|second|seconds|ms|min|mins|minute|minutes)?$/i.exec(s);
      if (!m) return null;
      const v = num(m[1]!);
      const u = (m[2] ?? "s").toLowerCase();
      return u === "ms" ? v / 1000 : u.startsWith("min") ? v * 60 : v;
    }
    case "px": {
      const m = /^(\d+(?:[.,]\d+)?)\s*(px|pixels?)?$/i.exec(s);
      return m ? num(m[1]!) : null;
    }
    default:
      return null;
  }
}

function round(v: number): number {
  return Math.round(v * 1e6) / 1e6;
}

/** A range stated in a description, e.g. "Rows per page, 1-500." → [1, 500]. */
export function rangeOf(description: string): [number, number] | null {
  const m = /(?:^|[\s(,])(-?\d+(?:\.\d+)?)\s*-\s*(-?\d+(?:\.\d+)?)(?=[\s.,;)]|$)/.exec(description);
  if (!m) return null;
  const lo = Number(m[1]);
  const hi = Number(m[2]);
  return lo < hi ? [lo, hi] : null;
}

// ------------------------------------------------------------------------------------------------ coercion

interface Ctx {
  tool: string;
  units: string;
  warnings: string[];
}

class CoerceError extends Error {
  constructor(readonly param: string, readonly reason: string, readonly extra: Record<string, unknown> = {}) {
    super(reason);
  }
}

function matchEnum(value: string, values: string[], synonyms: Record<string, string> | undefined): string | null {
  if (values.includes(value)) return value;
  const c = canonValue(value);
  const hit = values.find((v) => canonValue(v) === c);
  if (hit) return hit;
  if (synonyms) for (const [k, v] of Object.entries(synonyms)) if (canonValue(k) === c && values.includes(v)) return v;
  return null;
}

function coerce(value: unknown, p: ParamSpec | { type: string; enum?: string[]; items?: unknown; properties?: Record<string, ParamSpec>; unit?: ParamUnit; synonyms?: Record<string, string>; description?: string }, path: string, ctx: Ctx, unit?: ParamUnit, synonyms?: Record<string, string>): unknown {
  const effUnit = p.unit ?? unit;
  const effSyn = p.synonyms ?? synonyms;
  switch (p.type) {
    case "array":
      return coerceArray(value, p as ParamSpec, path, ctx, effUnit, effSyn);
    case "number":
    case "integer":
      return coerceNumber(value, p as ParamSpec, path, ctx, effUnit);
    case "boolean":
      return coerceBoolean(value, path);
    case "string":
      return coerceString(value, p as ParamSpec, path, effSyn);
    case "object":
      return coerceObject(value, p as ParamSpec, path, ctx, effUnit);
    default:
      return value;
  }
}

function coerceArray(value: unknown, p: ParamSpec, path: string, ctx: Ctx, unit?: ParamUnit, synonyms?: Record<string, string>): unknown[] {
  const items = (p.items ?? { type: "string" }) as ParamSpec;
  let list: unknown[];
  if (Array.isArray(value)) list = value;
  else if (typeof value === "string") {
    const s = value.trim();
    if (s.startsWith("[")) {
      try {
        const parsed = JSON.parse(s);
        list = Array.isArray(parsed) ? parsed : [parsed];
      } catch {
        throw new CoerceError(path, `is not a valid JSON array: ${s.slice(0, 60)}`);
      }
    } else if (items.type === "number" || items.type === "integer") {
      const parts = s.split(/[\s,;]+/).filter(Boolean);
      list = parts;
    } else if (items.type === "string" && !NO_SPLIT.has(path.split(".").pop() ?? "") && s.includes(",")) {
      list = s.split(",").map((x) => x.trim()).filter((x) => x.length > 0);
    } else list = s === "" ? [] : [s];
  } else if (value === undefined || value === null) list = [];
  else list = [value];
  // A single point given for a list of points: [x,y] → [[x,y]].
  if (items.type === "array" && list.length >= 2 && list.length <= 3 && list.every((v) => typeof v === "number" || (typeof v === "string" && v.trim() !== "" && !Number.isNaN(Number(v))))) list = [list];
  return list.map((item, i) => coerce(item, items, `${path}[${i}]`, ctx, unit, synonyms));
}

function coerceNumber(value: unknown, p: ParamSpec, path: string, ctx: Ctx, unit?: ParamUnit): number {
  let n: number | null = null;
  if (typeof value === "number") n = value;
  else if (typeof value === "string") n = parseWithUnit(value, unit, ctx.units);
  else if (Array.isArray(value) && value.length === 1) return coerceNumber(value[0], p, path, ctx, unit);
  if (n === null || !Number.isFinite(n)) throw new CoerceError(path, `must be a number${unit ? ` (${unit})` : ""}, got ${JSON.stringify(value)?.slice(0, 40)}`);
  if (p.type === "integer" && !Number.isInteger(n)) {
    const r = Math.round(n);
    ctx.warnings.push(`VALUE_CLAMPED ${path}=${n} rounded to ${r}`);
    n = r;
  }
  const range = p.description ? rangeOf(p.description) : null;
  if (range && !path.includes("[")) {
    const [lo, hi] = range;
    if (n < lo || n > hi) {
      const c = Math.min(hi, Math.max(lo, n));
      ctx.warnings.push(`VALUE_CLAMPED ${path}=${n} clamped to ${c} (${lo}-${hi})`);
      n = c;
    }
  }
  return n;
}

function coerceBoolean(value: unknown, path: string): boolean {
  if (typeof value === "boolean") return value;
  if (typeof value === "number") {
    if (value === 1) return true;
    if (value === 0) return false;
  }
  if (typeof value === "string") {
    const v = value.trim().toLowerCase();
    if (["true", "yes", "y", "1", "on", "ja"].includes(v)) return true;
    if (["false", "no", "n", "0", "off", "nein"].includes(v)) return false;
  }
  throw new CoerceError(path, `must be true or false, got ${JSON.stringify(value)?.slice(0, 40)}`);
}

function coerceString(value: unknown, p: ParamSpec, path: string, synonyms?: Record<string, string>): unknown {
  let s: string;
  if (typeof value === "string") s = value;
  else if (typeof value === "number" || typeof value === "boolean") s = String(value);
  else if (Array.isArray(value) && value.length === 1 && (typeof value[0] === "string" || typeof value[0] === "number")) s = String(value[0]);
  else if (Array.isArray(value)) throw new CoerceError(path, "must be a single value, not a list");
  else if (value !== null && typeof value === "object") throw new CoerceError(path, "must be a string, not an object");
  else throw new CoerceError(path, `must be a string, got ${JSON.stringify(value)?.slice(0, 40)}`);
  if (p.enum && p.enum.length > 0) {
    const hit = matchEnum(s, p.enum, synonyms);
    if (hit === null) throw new CoerceError(path, `must be one of ${p.enum.join(", ")}`, { values: p.enum, value: s });
    return hit;
  }
  return s;
}

function coerceObject(value: unknown, p: ParamSpec, path: string, ctx: Ctx, unit?: ParamUnit): unknown {
  let obj: unknown = value;
  if (typeof value === "string") {
    const s = value.trim();
    if (!s.startsWith("{")) throw new CoerceError(path, "must be an object");
    try {
      obj = JSON.parse(s);
    } catch {
      throw new CoerceError(path, `is not valid JSON: ${s.slice(0, 60)}`);
    }
  }
  if (obj === null || typeof obj !== "object" || Array.isArray(obj)) throw new CoerceError(path, "must be an object");
  const record = { ...(obj as Record<string, unknown>) };
  if (p.properties) {
    for (const [k, sub] of Object.entries(p.properties)) if (record[k] !== undefined && record[k] !== null) record[k] = coerce(record[k], sub, `${path}.${k}`, ctx);
  } else if (unit === "mm") {
    // e.g. view_range {cut:"1.1 m"}: numeric leaves with units become numbers in the call's units.
    for (const [k, v] of Object.entries(record)) if (typeof v === "string") {
      const n = parseWithUnit(v, "mm", ctx.units);
      if (n !== null) record[k] = n;
    }
  }
  return record;
}

/** Numeric id strings become numbers; UniqueIds and handles stay strings. */
function normalizeIds(list: unknown[]): unknown[] {
  return list.map((v) => (typeof v === "string" && /^\s*-?\d{1,12}\s*$/.test(v) ? Number(v) : v));
}
const ID_ARRAYS = new Set(["ids", "expect_ids", "highlight", "between"]);

// ------------------------------------------------------------------------------------------------ main

export interface NormalizeOptions {
  /** Prefix for warnings/errors of nested calls (change_set "[3] ", filter "filter."). */
  prefix?: string;
  /** Keep keys that are not params (change_set op items carry `tool`). */
  keep?: string[];
}

/** Normalize raw args against a tool's params (steps 1-7). */
export function normalizeArgs(spec: ToolSpec, raw: unknown, options: NormalizeOptions = {}): NormalizeResult {
  const warnings: string[] = [];
  const prefix = options.prefix ?? "";
  let input: Record<string, unknown>;
  if (raw === undefined || raw === null) input = {};
  else if (typeof raw === "string") {
    try {
      const parsed = JSON.parse(raw);
      if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("not an object");
      input = parsed as Record<string, unknown>;
    } catch {
      return { args: {}, warnings, error: { code: "INVALID_ARGS", message: `${spec.name} arguments must be a JSON object`, details: { reason: "not an object" } } };
    }
  } else if (typeof raw !== "object" || Array.isArray(raw)) {
    return { args: {}, warnings, error: { code: "INVALID_ARGS", message: `${spec.name} arguments must be a JSON object`, details: { reason: "not an object" } } };
  } else input = raw as Record<string, unknown>;

  const props = fullProperties(spec);
  const names = Object.keys(props);
  const keep = new Set(options.keep ?? []);

  // Step 1: map keys.
  const mapped = new Map<string, { value: unknown; from: string }>();
  for (const [key, value] of Object.entries(input)) {
    if (keep.has(key)) continue;
    let target: string | null = null;
    if (key in props) target = key;
    else {
      const snake = toSnake(key);
      if (snake in props) target = snake;
      else {
        const ck = canonKey(key);
        target = names.find((n) => canonKey(n) === ck) ?? null;
        if (!target) target = (ALIAS_LOOKUP.get(ck) ?? []).find((t) => t in props && !(t in input)) ?? null;
        if (!target) {
          const near = closest(snake, names);
          const limit = snake.length >= 5 ? 2 : snake.length >= 3 ? 1 : 0;
          if (near && near.distance <= limit && near.distance > 0) {
            return {
              args: {},
              warnings,
              error: {
                code: "INVALID_ARGS",
                message: `${prefix}${spec.name} has no param "${key}"; did you mean "${near.option}"?`,
                details: { param: key, reason: "unknown param", closest: near.option, example: renamedExample(input, key, near.option) },
              },
            };
          }
          if (value !== undefined && value !== null) warnings.push(`PARAM_IGNORED ${prefix}${key} is not a ${spec.name} param`);
          continue;
        }
      }
    }
    if (mapped.has(target)) {
      const existing = mapped.get(target)!;
      // The exact spelling wins over a renamed one.
      if (existing.from === target) {
        warnings.push(`PARAM_IGNORED ${prefix}${key} duplicates ${target}`);
        continue;
      }
      warnings.push(`PARAM_IGNORED ${prefix}${existing.from} duplicates ${target}`);
    }
    if (target !== key) warnings.push(`PARAM_RENAMED ${prefix}${key} -> ${target}`);
    mapped.set(target, { value, from: key });
  }

  // The unit of plain lengths for this call (needed before converting lengths).
  let units = "mm";
  const unitsEntry = mapped.get("units");
  if (unitsEntry && unitsEntry.value !== undefined && unitsEntry.value !== null && props.units) {
    try {
      units = String(coerce(unitsEntry.value, props.units, `${prefix}units`, { tool: spec.name, units: "mm", warnings }));
      unitsEntry.value = units;
    } catch (error) {
      const e = error as CoerceError;
      return { args: {}, warnings, error: invalid(spec, prefix, e) };
    }
  }

  // Steps 2-7: coerce values.
  const out: Record<string, unknown> = {};
  const ctx: Ctx = { tool: spec.name, units, warnings };
  for (const name of names) {
    const entry = mapped.get(name);
    if (!entry) continue;
    let value = entry.value;
    if (value === undefined || value === null) continue;
    if (typeof value === "string" && value.trim() === "" && EMPTY_IS_ABSENT.has(name)) continue;
    try {
      if (name === "filter" && props.filter?.type === "object") value = normalizeFilter(value, `${prefix}filter`, warnings);
      else if (name === spec.discriminator) value = discriminatorValue(value, props[name]!);
      else value = coerce(value, props[name]!, `${prefix}${name}`, ctx);
      if (ID_ARRAYS.has(name) && Array.isArray(value)) value = normalizeIds(value);
    } catch (error) {
      if (error instanceof CoerceError) return { args: {}, warnings, error: invalid(spec, prefix, error) };
      throw error;
    }
    out[name] = value;
  }
  for (const k of keep) if (k in input) out[k] = input[k];
  return { args: out, warnings };
}

/** The discriminator is matched leniently but never rejected here: resolveOp answers UNKNOWN_OP with the closest op. */
function discriminatorValue(value: unknown, p: ParamSpec): unknown {
  const s = Array.isArray(value) && value.length === 1 ? String(value[0]) : typeof value === "string" || typeof value === "number" ? String(value) : value;
  if (typeof s !== "string") return s;
  return (p.enum && matchEnum(s.trim(), p.enum, p.synonyms)) ?? s.trim();
}

/** The caller's args with a misspelled key corrected (the INVALID_ARGS fix). */
function renamedExample(input: Record<string, unknown>, from: string, to: string): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(input)) out[k === from ? to : k] = v;
  return out;
}

function invalid(spec: ToolSpec, prefix: string, e: CoerceError): ArgError {
  const param = e.param.replace(prefix, "");
  return {
    code: "INVALID_ARGS",
    message: `${prefix}${spec.name} ${e.param.replace(prefix, "")} ${e.reason}`,
    details: { param, reason: e.reason, ...e.extra },
  };
}

/** `filter` objects use the find_elements keys and leniency (no per-op validation). */
function normalizeFilter(value: unknown, path: string, warnings: string[]): Record<string, unknown> {
  let obj = value;
  if (typeof value === "string") {
    try {
      obj = JSON.parse(value);
    } catch {
      throw new CoerceError(path, "must be an object like {category:[...], level:...}");
    }
  }
  if (obj === null || typeof obj !== "object" || Array.isArray(obj)) throw new CoerceError(path, "must be an object like {category:[...], level:...}");
  const find = byName.get("find_elements");
  if (!find) return obj as Record<string, unknown>;
  const result = normalizeArgs(find, obj, { prefix: `${path}.` });
  if (result.error) throw new CoerceError(String(result.error.details.param ?? path), result.error.message.replace(/^.*?find_elements\s*/, ""), result.error.details);
  for (const w of result.warnings) warnings.push(w.replace(" is not a find_elements param", " is not a filter key"));
  return result.args;
}
