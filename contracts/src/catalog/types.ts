// Catalog types (SPEC §4.1). The catalog in contracts/ is the single source of truth for the advertised
// tools/list, the op registry (Appendix B), help, examples and tool-specific errors.

import type { ErrorSpec } from "../errors.js";

export type Kind = "read" | "write" | "ui" | "lifecycle" | "control" | "code";
export type Impl = "addin" | "broker" | "control" | "both";
export type Scope = "none" | "any" | "project" | "family" | "project_or_family";
export type Tx = "none" | "in" | "own" | "temp" | "group" | "lifecycle";
export type Job = "never" | "auto" | "always";
export type Blast =
  | "delete"
  | "bulk"
  | "create"
  | "always"
  | "file_overwrite"
  | "unsaved_close"
  | "multi_doc"
  | "central_open"
  | "code_commit"
  | "button";

export interface OpMeta {
  kind: Kind;
  impl: Impl;
  scope: Scope;
  /** Needs the UI-active doc (NEEDS_ACTIVE_DOC otherwise). */
  ui: boolean;
  /** Minimum Revit year (UNSUPPORTED_VERSION below it). */
  min: 2024 | 2027;
  /** May run from the Idling fallback. */
  idle: boolean;
  tx: Tx;
  blast: Blast[];
  /** Warnings roll the op back. */
  strict: boolean;
  job: Job;
  /** Callable from the in-process bridge. */
  inproc: boolean;
  /** Allowed inside change_set. */
  cs: boolean;
}

export type ParamType = "string" | "number" | "integer" | "boolean" | "object" | "array";
/** Non-advertised unit tag driving lenient unit parsing (§5.3). Lengths are "mm". */
export type ParamUnit = "mm" | "deg" | "pct" | "m2" | "m3" | "s" | "px";

export interface ParamItems {
  type: ParamType | string;
  items?: unknown;
  enum?: string[];
  description?: string;
  properties?: Record<string, ParamSpec>;
  required?: string[];
  unit?: ParamUnit;
  synonyms?: Record<string, string>;
}

/** Advertised part = JSON Schema subset: type, description, enum, items, properties, required. */
export interface ParamSpec {
  type: ParamType;
  description: string;
  enum?: string[];
  items?: ParamSpec | ParamItems;
  properties?: Record<string, ParamSpec>;
  required?: string[];
  /** NOT advertised: drives lenient unit parsing (§5.3). For arrays it applies to every numeric leaf. */
  unit?: ParamUnit;
  /** NOT advertised: enum synonyms, e.g. {"3D":"3d","FloorPlan":"floor_plan"}. Keys are matched case- and separator-insensitively. */
  synonyms?: Record<string, string>;
}

export interface OpSpec {
  /** Required params: ',' separates groups, '/' alternatives (exactly one), '+' together. "ids/from/filter" is the sel group. */
  req: string;
  /** Optional params, ',' separated. */
  opt: string;
  meta: OpMeta;
  /** One or two sentences of behaviour for help {tool,op}. */
  help?: string;
  /** Revit API route (documentation). */
  api?: string;
  /** Complete argument objects for this op (the discriminator included). >=1 per op by end of wave 2; used by help, INVALID_ARGS fixes and e2e W4. */
  examples: Record<string, unknown>[];
  /** Codes this op may return (documentation). */
  errors?: string[];
}

export type Discriminator = "op" | "kind" | "check" | "format";
export type Profile = "core" | "full";

export interface ToolAnnotations {
  readOnlyHint: boolean;
  destructiveHint?: boolean;
  idempotentHint?: boolean;
  openWorldHint: boolean;
}

export type ToolGroup = "session" | "read" | "visual" | "ui" | "write" | "control" | "optin";

export interface ToolSpec {
  name: string;
  title: string;
  description: string;
  group: ToolGroup;
  annotations: ToolAnnotations;
  /**
   * Advertised params in tools/list order. For discriminator "op" the `op` param is NOT listed here: it is generated
   * at emit time from `ops` (opParam) and advertised first. For kind/check/format the discriminator is a normal param.
   */
  properties: Record<string, ParamSpec>;
  required?: string[];
  /** null: single implicit op, key = tool name (ops key ""). */
  discriminator: Discriminator | null;
  /** Ops in advertised enum order; key "" when discriminator is null. */
  ops: Record<string, OpSpec>;
  /** Used when the discriminator is omitted (e.g. check_model → "stats"). Without it a missing discriminator is INVALID_ARGS. */
  defaultOp?: string;
  profiles: Profile[];
  optIn?: "enableCodeExecution";
  /** Tool-specific codes, merged into the ErrorCatalog. */
  errors?: Record<string, ErrorSpec>;
}

/** One registry row (Appendix B). */
export interface RegistryEntry extends OpMeta {
  key: string;
  tool: string;
  op: string | null;
}

/** Exactly what tools/list serves per tool. */
export interface AdvertisedTool {
  name: string;
  title: string;
  description: string;
  inputSchema: { type: "object"; properties: Record<string, unknown>; required?: string[] };
  annotations: ToolAnnotations;
}
