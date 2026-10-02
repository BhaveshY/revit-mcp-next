// The catalog (SPEC §4.1): every tool file is imported here (pre-populated by W1; lanes edit only their tool files).
// Exports CATALOG (tools/list order), byName, byKey and the derived views: advertised tools/list, the op registry
// (Appendix B), the merged ErrorCatalog and the catalog hash.

import { createHash } from "node:crypto";
import { mergeErrorCatalog, type ErrorSpec } from "../errors.js";
import type { AdvertisedTool, OpSpec, ParamSpec, Profile, RegistryEntry, ToolSpec } from "./types.js";

import { tool as t_status } from "./tools/status.js";
import { tool as t_set_target } from "./tools/set_target.js";
import { tool as t_list } from "./tools/list.js";
import { tool as t_find_elements } from "./tools/find_elements.js";
import { tool as t_describe_elements } from "./tools/describe_elements.js";
import { tool as t_get_view } from "./tools/get_view.js";
import { tool as t_read_schedule } from "./tools/read_schedule.js";
import { tool as t_read_family } from "./tools/read_family.js";
import { tool as t_check_model } from "./tools/check_model.js";
import { tool as t_get_quantities } from "./tools/get_quantities.js";
import { tool as t_get_changes } from "./tools/get_changes.js";
import { tool as t_read_many } from "./tools/read_many.js";
import { tool as t_capture } from "./tools/capture.js";
import { tool as t_ui } from "./tools/ui.js";
import { tool as t_create_elements } from "./tools/create_elements.js";
import { tool as t_place_family } from "./tools/place_family.js";
import { tool as t_modify_elements } from "./tools/modify_elements.js";
import { tool as t_set_parameters } from "./tools/set_parameters.js";
import { tool as t_edit_types } from "./tools/edit_types.js";
import { tool as t_edit_views } from "./tools/edit_views.js";
import { tool as t_view_graphics } from "./tools/view_graphics.js";
import { tool as t_edit_sheets } from "./tools/edit_sheets.js";
import { tool as t_annotate } from "./tools/annotate.js";
import { tool as t_edit_schedules } from "./tools/edit_schedules.js";
import { tool as t_edit_family } from "./tools/edit_family.js";
import { tool as t_mep } from "./tools/mep.js";
import { tool as t_structure } from "./tools/structure.js";
import { tool as t_manage_document } from "./tools/manage_document.js";
import { tool as t_worksharing } from "./tools/worksharing.js";
import { tool as t_links } from "./tools/links.js";
import { tool as t_export } from "./tools/export.js";
import { tool as t_model_delivery } from "./tools/model_delivery.js";
import { tool as t_change_set } from "./tools/change_set.js";
import { tool as t_undo } from "./tools/undo.js";
import { tool as t_job_status } from "./tools/job_status.js";
import { tool as t_cancel_job } from "./tools/cancel_job.js";
import { tool as t_help } from "./tools/help.js";
import { tool as t_run_csharp } from "./tools/run_csharp.js";

export * from "./types.js";

/** Tools in deterministic tools/list order: session, read, visual, ui, write, control, opt-in (§5.1). */
export const CATALOG: readonly ToolSpec[] = Object.freeze([
  t_status,
  t_set_target,
  t_list,
  t_find_elements,
  t_describe_elements,
  t_get_view,
  t_read_schedule,
  t_read_family,
  t_check_model,
  t_get_quantities,
  t_get_changes,
  t_read_many,
  t_capture,
  t_ui,
  t_create_elements,
  t_place_family,
  t_modify_elements,
  t_set_parameters,
  t_edit_types,
  t_edit_views,
  t_view_graphics,
  t_edit_sheets,
  t_annotate,
  t_edit_schedules,
  t_edit_family,
  t_mep,
  t_structure,
  t_manage_document,
  t_worksharing,
  t_links,
  t_export,
  t_model_delivery,
  t_change_set,
  t_undo,
  t_job_status,
  t_cancel_job,
  t_help,
  t_run_csharp,
]);

export const byName: ReadonlyMap<string, ToolSpec> = new Map(CATALOG.map((tool) => [tool.name, tool]));

/** Registry key of an op: `<tool>.<op>`, or `<tool>` for tools without a discriminator. */
export function keyOf(tool: string, op: string | null | undefined): string {
  return op ? `${tool}.${op}` : tool;
}

export interface KeyInfo {
  key: string;
  tool: ToolSpec;
  /** Op name; null for tools without a discriminator. */
  op: string | null;
  spec: OpSpec;
  entry: RegistryEntry;
}

function buildRegistry(): RegistryEntry[] {
  const rows: RegistryEntry[] = [];
  for (const tool of CATALOG) {
    for (const [opName, spec] of Object.entries(tool.ops)) {
      const op = tool.discriminator ? opName : null;
      const m = spec.meta;
      rows.push({
        key: keyOf(tool.name, op),
        tool: tool.name,
        op,
        kind: m.kind,
        impl: m.impl,
        scope: m.scope,
        ui: m.ui,
        min: m.min,
        idle: m.idle,
        tx: m.tx,
        blast: [...m.blast],
        strict: m.strict,
        job: m.job,
        inproc: m.inproc,
        cs: m.cs,
      });
    }
  }
  return rows;
}

/** The op registry (Appendix B): one row per key, in catalog order. */
export const REGISTRY: readonly RegistryEntry[] = Object.freeze(buildRegistry());

export const byKey: ReadonlyMap<string, KeyInfo> = new Map(
  REGISTRY.map((entry) => {
    const tool = byName.get(entry.tool)!;
    const spec = tool.ops[entry.op ?? ""]!;
    return [entry.key, { key: entry.key, tool, op: entry.op, spec, entry }];
  })
);

/** Op names of a tool in advertised order ("" for tools without a discriminator). */
export function opNames(tool: ToolSpec): string[] {
  return Object.keys(tool.ops);
}

// ---------------------------------------------------------------------------------------------
// Advertised schema (tools/list)
// ---------------------------------------------------------------------------------------------

const ADVERTISED_KEYS = new Set(["type", "description", "enum", "items", "properties", "required"]);

/** Advertised-only clone of a schema node: keeps type, description, enum, items, properties, required in source order. */
export function stripParam(node: unknown): unknown {
  if (Array.isArray(node)) return node.map(stripParam);
  if (node === null || typeof node !== "object") return node;
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(node as Record<string, unknown>)) {
    if (!ADVERTISED_KEYS.has(key)) continue;
    if (key === "items") out[key] = stripParam(value);
    else if (key === "properties" && value && typeof value === "object") {
      const props: Record<string, unknown> = {};
      for (const [pk, pv] of Object.entries(value as Record<string, unknown>)) props[pk] = stripParam(pv);
      out[key] = props;
    } else if (key === "enum" || key === "required") out[key] = Array.isArray(value) ? [...value] : value;
    else out[key] = value;
  }
  return out;
}

/** The generated `op` param (identical to opParam() in SPEC-catalog.mjs). */
export function opParam(tool: ToolSpec): ParamSpec {
  const names = Object.keys(tool.ops);
  const sig = names
    .map((name) => {
      const spec = tool.ops[name]!;
      const req = spec.req || "";
      const opt = spec.opt || "";
      return `${name}(${req}${opt ? ";" + opt : ""})`;
    })
    .join(" ");
  const usesSel = sig.includes("ids/from/filter");
  const text = sig.split("ids/from/filter").join("sel");
  return { type: "string", enum: names, description: (usesSel ? "sel = ids, from or filter. " : "") + "Params per op, optional after ';': " + text };
}

/** All params of a tool including the generated `op` param (non-advertised fields kept). */
export function fullProperties(tool: ToolSpec): Record<string, ParamSpec> {
  if (tool.discriminator === "op") return { op: opParam(tool), ...tool.properties };
  return { ...tool.properties };
}

export function advertisedSchema(tool: ToolSpec): AdvertisedTool["inputSchema"] {
  const properties: Record<string, unknown> = {};
  for (const [name, param] of Object.entries(fullProperties(tool))) properties[name] = stripParam(param);
  const schema: AdvertisedTool["inputSchema"] = { type: "object", properties };
  if (tool.required?.length) schema.required = [...tool.required];
  return schema;
}

export function advertisedTool(tool: ToolSpec): AdvertisedTool {
  return {
    name: tool.name,
    title: tool.title,
    description: tool.description,
    inputSchema: advertisedSchema(tool),
    annotations: { ...tool.annotations },
  };
}

export interface ListOptions {
  /** "full" (default) or "core" (REVIT_MCP_NEXT_PROFILE=core). */
  profile?: Profile;
  /** List opt-in tools (run_csharp) — only when enableCodeExecution was true at broker start. */
  includeOptIn?: boolean;
}

/** Tools served for a server configuration (never varies per connection). */
export function listedTools(options: ListOptions = {}): ToolSpec[] {
  const profile = options.profile ?? "full";
  return CATALOG.filter((tool) => tool.profiles.includes(profile) && (!tool.optIn || options.includeOptIn === true));
}

/** Exactly what tools/list serves for a configuration. */
export function toolsList(options: ListOptions = {}): { tools: AdvertisedTool[] } {
  return { tools: listedTools(options).map(advertisedTool) };
}

// ---------------------------------------------------------------------------------------------
// Errors, hash
// ---------------------------------------------------------------------------------------------

/** Global ErrorCatalog merged with every ToolSpec.errors. */
export const ERRORS: Readonly<Record<string, ErrorSpec>> = Object.freeze(mergeErrorCatalog(CATALOG.map((tool) => tool.errors)));

/** JSON with object keys sorted recursively (used for hashing and arg comparison). */
export function canonicalJson(value: unknown): string {
  return JSON.stringify(canonicalize(value));
}

export function canonicalize(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value === null || typeof value !== "object") return value;
  const out: Record<string, unknown> = {};
  for (const key of Object.keys(value as Record<string, unknown>).sort()) {
    const child = (value as Record<string, unknown>)[key];
    if (child !== undefined) out[key] = canonicalize(child);
  }
  return out;
}

/** sha256 of the canonical JSON of tools + registry (SPEC §4.1). */
export function catalogHash(): string {
  return createHash("sha256").update(canonicalJson({ tools: CATALOG, registry: REGISTRY }), "utf8").digest("hex");
}

export const CORE_TOOL_NAMES: readonly string[] = Object.freeze(CATALOG.filter((tool) => tool.profiles.includes("core")).map((tool) => tool.name));
