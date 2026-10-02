// Shared catalog vocabulary: schema helpers, the shared params (identical text everywhere), annotation presets and
// the op-registry bases. Ported from docs/design/SPEC-catalog.mjs and SPEC-registry.mjs; the advertised output of the
// helpers is byte-identical to the SPEC generator (key order matters for tools/list determinism).
//
// Only keywords Codex keeps are advertised: type, description, enum, items, properties, required.
// `unit` and `synonyms` are NOT advertised; they drive lenient input (§5.3) and are stripped at emit time.

import type { OpMeta, OpSpec, ParamSpec, ParamUnit, ToolAnnotations } from "./types.js";

// ---------------------------------------------------------------------------------------------
// Schema helpers
// ---------------------------------------------------------------------------------------------

export const S = (description: string): ParamSpec => ({ type: "string", description });
export const N = (description: string): ParamSpec => ({ type: "number", description });
export const I = (description: string): ParamSpec => ({ type: "integer", description });
export const B = (description: string): ParamSpec => ({ type: "boolean", description });
export const E = (values: string[], description: string): ParamSpec => ({ type: "string", enum: values, description });
export const SA = (description: string): ParamSpec => ({ type: "array", items: { type: "string" }, description });
export const EA = (values: string[], description: string): ParamSpec => ({ type: "array", items: { type: "string", enum: values }, description });
export const PT = (description: string): ParamSpec => ({ type: "array", items: { type: "number" }, description });
export const PTS = (description: string): ParamSpec => ({ type: "array", items: { type: "array", items: { type: "number" } }, description });
export const O = (description: string): ParamSpec => ({ type: "object", description });
export const OA = (description: string): ParamSpec => ({ type: "array", items: { type: "object" }, description });
/** Loops: array of point arrays ([[[x,y],...],...]). */
export const LOOPS = (description: string): ParamSpec => ({
  type: "array",
  items: { type: "array", items: { type: "array", items: { type: "number" } } },
  description,
});

/** Tag a param with a non-advertised unit. */
export function withUnit(param: ParamSpec, unit: ParamUnit): ParamSpec {
  return { ...param, unit };
}
export const mm = (param: ParamSpec): ParamSpec => withUnit(param, "mm");
export const deg = (param: ParamSpec): ParamSpec => withUnit(param, "deg");
export const pct = (param: ParamSpec): ParamSpec => withUnit(param, "pct");
export const sec = (param: ParamSpec): ParamSpec => withUnit(param, "s");
export const px = (param: ParamSpec): ParamSpec => withUnit(param, "px");

/** Attach non-advertised enum synonyms (keys match case- and separator-insensitively). */
export function syn(param: ParamSpec, synonyms: Record<string, string>): ParamSpec {
  return { ...param, synonyms: { ...(param.synonyms ?? {}), ...synonyms } };
}

// ---------------------------------------------------------------------------------------------
// Synonym tables reused by several tools
// ---------------------------------------------------------------------------------------------

export const UNITS_SYN: Record<string, string> = {
  millimeter: "mm", millimeters: "mm", millimetre: "mm", millimetres: "mm",
  centimeter: "cm", centimeters: "cm", centimetre: "cm", centimetres: "cm",
  meter: "m", meters: "m", metre: "m", metres: "m",
  inch: "in", inches: "in", '"': "in",
  foot: "ft", feet: "ft", "'": "ft",
};
export const DETAIL_SYN: Record<string, string> = { summary: "compact", short: "compact", brief: "compact", verbose: "full", all: "full", long: "full" };
export const STYLE_SYN: Record<string, string> = {
  wire: "wireframe", hiddenline: "hidden", hidden_line: "hidden", shading: "shaded", shadedwithedges: "shaded",
  consistentcolors: "consistent", consistent_colors: "consistent", render: "realistic", rendered: "realistic", photo: "realistic",
};
export const ORIENT_SYN: Record<string, string> = {
  se: "iso_se", sw: "iso_sw", ne: "iso_ne", nw: "iso_nw", southeast: "iso_se", southwest: "iso_sw", northeast: "iso_ne", northwest: "iso_nw",
  iso: "iso_se", isometric: "iso_se", default: "iso_se", plan: "top", topdown: "top", south: "front", north: "back", west: "left", east: "right",
};
export const VIEW_KIND_SYN: Record<string, string> = {
  floorplan: "floor_plan", plan: "floor_plan", grundriss: "floor_plan",
  ceilingplan: "ceiling_plan", rcp: "ceiling_plan", reflectedceilingplan: "ceiling_plan",
  structuralplan: "structural_plan", engineeringplan: "structural_plan",
  areaplan: "area_plan",
  sectionview: "section", schnitt: "section",
  elevationview: "elevation", ansicht: "elevation",
  threed: "3d", "3dview": "3d", view3d: "3d", isometric: "3d",
  draftingview: "drafting",
  legendview: "legend",
  calloutview: "callout",
};

// ---------------------------------------------------------------------------------------------
// Shared params (identical text everywhere so they read as one vocabulary)
// ---------------------------------------------------------------------------------------------

export const P = {
  doc: S("Doc title, path or # from status. Default: pinned target."),
  preview: B("true = dry run: returns the plan and a confirm token, changes nothing."),
  confirm: S("Token from a NOT APPLIED or preview result; applies exactly that plan."),
  units: syn(E(["mm", "cm", "m", "in", "ft"], "Unit of input lengths. Default mm."), UNITS_SYN),
  ids: SA("Element ids (numbers ok) or UniqueIds."),
  from: S("Element set: r# handle from find_elements, 'selection', or 'last' (your last write)."),
  filter: O("Select like find_elements: {category,level,type,family,view,where:[\"Mark = D1\"]}."),
  view: S("View name, id, sheet number, or 'active'."),
  views: SA("View/sheet names or ids."),
  level: S("Level name or id."),
  type: S("Type: 'Family: Type', type name, or id."),
  points: mm(PTS("[[x,y],...] mm.")),
  at: mm(PT("Point [x,y] or [x,y,z] mm.")),
  start: mm(PT("Start point [x,y(,z)] mm.")),
  end: mm(PT("End point [x,y(,z)] mm.")),
  by: mm(PT("Offset vector [dx,dy(,dz)] mm.")),
  name: S("Name."),
  limit: I("Rows per page, 1-500. Default 50."),
  page: S("Page token from a 'more:' line, e.g. p2."),
  detail: syn(E(["compact", "full"], "Default compact."), DETAIL_SYN),
  fields: SA("Extra columns: location, bbox, host, room, workset, phase, or any parameter name."),
  category: SA("Categories, e.g. [\"Walls\",\"Doors\"] (a string is fine)."),
  offset: mm(N("Height above level, mm. Default 0.")),
  rotation: deg(N("Degrees, counterclockwise. Default 0.")),
  color: S("Color: #RRGGBB, 'r,g,b' or a name like red."),
} satisfies Record<string, ParamSpec>;

export const WRITE_TAIL = { doc: P.doc, preview: P.preview, confirm: P.confirm, units: P.units };
export const WRITE_TAIL_NOUNITS = { doc: P.doc, preview: P.preview, confirm: P.confirm };

/** Params that are valid for every op of a tool that has them (never reported as PARAM_IGNORED). */
export const TAIL_PARAMS = ["doc", "preview", "confirm", "units", "page"] as const;

// ---------------------------------------------------------------------------------------------
// Annotation presets
// ---------------------------------------------------------------------------------------------

export const RO: ToolAnnotations = { readOnlyHint: true, openWorldHint: false };
export const RO_IDEM: ToolAnnotations = { readOnlyHint: true, idempotentHint: true, openWorldHint: false };
export const W: ToolAnnotations = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };
export const W_IDEM: ToolAnnotations = { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false };
export const WD: ToolAnnotations = { readOnlyHint: false, destructiveHint: true, openWorldHint: false };
export const WD_OPEN: ToolAnnotations = { readOnlyHint: false, destructiveHint: true, openWorldHint: true };

// ---------------------------------------------------------------------------------------------
// Op registry bases (SPEC-registry.mjs). Key order of OpMeta is canonical (kind..cs).
// ---------------------------------------------------------------------------------------------

export function meta(base: OpMeta, overrides: Partial<OpMeta> = {}): OpMeta {
  const m = { ...base, ...overrides };
  return {
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
  };
}

export const READ: OpMeta = { kind: "read", impl: "addin", scope: "any", ui: false, min: 2024, idle: true, tx: "none", blast: [], strict: false, job: "never", inproc: true, cs: false };
export const WRITE: OpMeta = { kind: "write", impl: "addin", scope: "project", ui: false, min: 2024, idle: true, tx: "in", blast: [], strict: false, job: "never", inproc: true, cs: true };
export const LIFE: OpMeta = { kind: "lifecycle", impl: "addin", scope: "project", ui: false, min: 2024, idle: false, tx: "lifecycle", blast: [], strict: false, job: "auto", inproc: false, cs: false };
export const BROKER: OpMeta = { kind: "control", impl: "broker", scope: "none", ui: false, min: 2024, idle: true, tx: "none", blast: [], strict: false, job: "never", inproc: false, cs: false };
export const UI: OpMeta = { kind: "ui", impl: "addin", scope: "project_or_family", ui: true, min: 2024, idle: true, tx: "none", blast: [], strict: false, job: "never", inproc: true, cs: false };
export const CODE: OpMeta = { kind: "code", impl: "addin", scope: "project_or_family", ui: false, min: 2024, idle: false, tx: "temp", blast: ["code_commit"], strict: false, job: "auto", inproc: false, cs: false };

/** Build an OpSpec. `req`/`opt` use the signature grammar of SPEC-catalog.mjs (`/` alternatives, `+` together). */
export function op(req: string, opt: string, opMeta: OpMeta, examples: Record<string, unknown>[] = [], extra: Partial<Pick<OpSpec, "help" | "api" | "errors">> = {}): OpSpec {
  return { req, opt, meta: opMeta, examples, ...extra };
}
