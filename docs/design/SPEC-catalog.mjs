// SPEC catalog (normative for the revit-mcp-next overhaul). Derived from D1-catalog.mjs plus the SPEC.md critic resolutions.
// W1-BROKER ports this file into contracts/src/catalog/tools/*.ts (one file per tool) together with the op metadata (OPMETA).
// Generates (1) a tools/list JSON exactly as the broker would advertise it (no outputSchema,
// no $schema, no const/default/min/max), (2) size statistics, (3) markdown catalog tables.
// Run: node D1-catalog.mjs  -> writes D1-tools-list.json and D1-catalog.generated.md next to it.
import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

// ---------- schema helpers (only keywords Codex keeps: type, description, enum, items, properties, required) ----------
const S = (description) => ({ type: "string", description });
const N = (description) => ({ type: "number", description });
const I = (description) => ({ type: "integer", description });
const B = (description) => ({ type: "boolean", description });
const E = (values, description) => (description ? { type: "string", enum: values, description } : { type: "string", enum: values });
const SA = (description) => ({ type: "array", items: { type: "string" }, description });
const EA = (values, description) => ({ type: "array", items: { type: "string", enum: values }, description });
const PT = (description) => ({ type: "array", items: { type: "number" }, description });
const PTS = (description) => ({ type: "array", items: { type: "array", items: { type: "number" } }, description });
const O = (description) => ({ type: "object", description });
const OA = (description) => ({ type: "array", items: { type: "object" }, description });

// ---------- shared params (identical text everywhere so they read as one vocabulary) ----------
const P = {
  doc: S("Doc title, path or # from status. Default: pinned target."),
  preview: B("true = dry run: returns the plan and a confirm token, changes nothing."),
  confirm: S("Token from a NOT APPLIED or preview result; applies exactly that plan."),
  units: E(["mm", "cm", "m", "in", "ft"], "Unit of input lengths. Default mm."),
  ids: SA("Element ids (numbers ok) or UniqueIds."),
  from: S("Element set: r# handle from find_elements, 'selection', or 'last' (your last write)."),
  filter: O("Select like find_elements: {category,level,type,family,view,where:[\"Mark = D1\"]}."),
  view: S("View name, id, sheet number, or 'active'."),
  views: SA("View/sheet names or ids."),
  level: S("Level name or id."),
  type: S("Type: 'Family: Type', type name, or id."),
  points: PTS("[[x,y],...] mm."),
  at: PT("Point [x,y] or [x,y,z] mm."),
  start: PT("Start point [x,y(,z)] mm."),
  end: PT("End point [x,y(,z)] mm."),
  by: PT("Offset vector [dx,dy(,dz)] mm."),
  name: S("Name."),
  limit: I("Rows per page, 1-500. Default 50."),
  page: S("Page token from a 'more:' line, e.g. p2."),
  detail: E(["compact", "full"], "Default compact."),
  fields: SA("Extra columns: location, bbox, host, room, workset, phase, or any parameter name."),
  category: SA("Categories, e.g. [\"Walls\",\"Doors\"] (a string is fine)."),
  offset: N("Height above level, mm. Default 0."),
  rotation: N("Degrees, counterclockwise. Default 0."),
  color: S("Color: #RRGGBB, 'r,g,b' or a name like red."),
};

const WRITE_TAIL = { doc: P.doc, preview: P.preview, confirm: P.confirm, units: P.units };
const WRITE_TAIL_NOUNITS = { doc: P.doc, preview: P.preview, confirm: P.confirm };

// ops: name -> [required, optional, note]. Signature text "op(req;opt)" goes into the op param description.
function opParam(ops) {
  const names = Object.keys(ops);
  const sig = names
    .map((n) => {
      const [r, o] = ops[n];
      const rr = r || "";
      const oo = o || "";
      return `${n}(${rr}${oo ? ";" + oo : ""})`;
    })
    .join(" ");
  const usesSel = sig.includes("ids/from/filter");
  const text = sig.split("ids/from/filter").join("sel");
  return { type: "string", enum: names, description: (usesSel ? "sel = ids, from or filter. " : "") + "Params per op, optional after ';': " + text };
}

const RO = { readOnlyHint: true, openWorldHint: false };
const RO_IDEM = { readOnlyHint: true, idempotentHint: true, openWorldHint: false };
const W = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };
const WD = { readOnlyHint: false, destructiveHint: true, openWorldHint: false };
const WD_OPEN = { readOnlyHint: false, destructiveHint: true, openWorldHint: true };

const WRITE_TOOLS = [
  "create_elements", "place_family", "modify_elements", "set_parameters", "edit_types", "edit_views",
  "view_graphics", "edit_sheets", "annotate", "edit_schedules", "edit_family", "mep", "structure",
  "manage_document", "worksharing", "links",
];
const READ_TOOLS_FOR_MANY = [
  "status", "list", "find_elements", "describe_elements", "get_view", "read_schedule", "read_family",
  "check_model", "get_quantities", "get_changes", "capture", "help",
];

export const tools = [];
const add = (t) => tools.push(t);

// =====================================================================================
// SESSION / TARGETING / HEALTH
// =====================================================================================
add({
  group: "session",
  name: "status",
  title: "Revit status",
  description:
    "Start here. Lists running Revit versions and open docs (#1, #2...), the pinned target doc, active view, selection count, levels and busy/dialog state. include= adds sections; detail=full adds health.",
  annotations: RO_IDEM,
  properties: {
    include: EA(["views", "selection", "view_elements", "readiness", "context", "warnings", "writes"], "Extra sections for the target doc (writes = recent writes by any session)."),
    detail: E(["compact", "full"], "full adds versions, paths, queue and pipe health."),
    instance: S("Only this Revit: year (2024) or process id."),
    doc: P.doc,
  },
});
add({
  group: "session",
  name: "set_target",
  title: "Pin target document",
  description:
    "Pin the doc that every call uses this session: 'active', a # from status, a title or a path. 'follow' tracks the active tab; 'none' unpins. Family docs work too; links are read with find_elements link=.",
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  properties: {
    doc: S("'active', 'follow', 'none', #, title or path."),
    instance: S("Revit year or process id when the same file is open twice."),
  },
  required: ["doc"],
});

// =====================================================================================
// READS
// =====================================================================================
const LIST_KINDS = [
  "levels", "grids", "views", "sheets", "schedules", "rooms", "areas", "spaces", "types", "families",
  "materials", "worksets", "phases", "phase_filters", "design_options", "links", "revisions",
  "view_templates", "view_filters", "view_types", "viewport_types", "title_blocks", "text_types",
  "dimension_types", "tag_types", "line_styles", "line_patterns", "fill_patterns", "scope_boxes",
  "groups", "systems", "system_types", "project_info", "units",
];
add({
  group: "read",
  name: "list",
  title: "List model items",
  description:
    "List items of one kind (levels, views, sheets, schedules, rooms, types, families, materials, worksets, phases, links, revisions, filters, templates...) as compact rows. name= filters; for_element= valid types.",
  annotations: RO_IDEM,
  properties: {
    kind: E(LIST_KINDS, "What to list."),
    name: S("Name contains (case-insensitive, * wildcard)."),
    category: P.category,
    level: S("Level name or id (rooms, areas, spaces, views)."),
    view_type: S("For views/view_types: FloorPlan, CeilingPlan, Section, Elevation, ThreeD, Drafting, Legend, AreaPlan, Sheet..."),
    family: S("For types: family name."),
    for_element: S("For types: element id; returns only types it can switch to."),
    placed: B("Views: on a sheet or not. Rooms/areas: placed or not."),
    graphical: B("Views: only graphical views (no schedules or browser-only views)."),
    printable: B("Views: only views that can be printed or exported."),
    class: S("Types: Revit API class, e.g. WallType, FloorType, FamilySymbol."),
    where: SA("Conditions 'Param op value' like find_elements."),
    ids: SA("Only these ids."),
    fields: P.fields,
    detail: P.detail,
    limit: P.limit,
    page: P.page,
    doc: P.doc,
  },
  required: ["kind"],
});
add({
  group: "read",
  name: "find_elements",
  title: "Find elements",
  description:
    "Find elements by category, level, type, view, selection, parameter values, box or link. Returns the exact total, compact rows and a handle r# for from= in later calls. count_only/group_by for fast counts.",
  annotations: RO_IDEM,
  properties: {
    category: P.category,
    class: S("Revit API class, e.g. Wall, FamilyInstance, Floor."),
    level: SA("Level names or ids (a string is fine)."),
    type: S("Type name, 'Family: Type' or id; * wildcard."),
    family: S("Family name; * wildcard."),
    view: S("Only elements visible in this view ('active' ok)."),
    from: S("Restrict to 'selection' or an r# handle."),
    ids: P.ids,
    where: SA("Conditions 'Param op value', op: = != > >= < <= contains startswith empty notempty. Lengths mm."),
    box: PT("Intersects box [x0,y0,x1,y1] (plan) or [x0,y0,z0,x1,y1,z1] mm."),
    link: S("Search inside this linked model (name or id); read-only, host coordinates."),
    workset: SA("Workset names."),
    phase: S("Phase created name."),
    design_option: SA("Design option names, or 'main'."),
    hidden: B("With view=: include elements hidden in the view."),
    types: B("Find element types instead of instances."),
    fields: P.fields,
    group_by: S("Count per category, type, family, level, workset or a parameter name."),
    count_only: B("Only the total (and group counts)."),
    detail: P.detail,
    limit: P.limit,
    page: P.page,
    doc: P.doc,
  },
});
add({
  group: "read",
  name: "describe_elements",
  title: "Describe elements",
  description:
    "Details for up to 50 elements: parameters with values, units and writability, type parameters, geometry, host/hosted, room, joins, group, MEP connectors; dimensions show witness refs. Use before set_parameters.",
  annotations: RO_IDEM,
  properties: {
    ids: P.ids,
    from: P.from,
    params: E(["writable", "all", "names", "none"], "Instance parameters: writable (default) values, all values, names only, none."),
    include: EA(["type", "geometry", "relations", "connectors"], "Extra blocks. type = type parameters."),
    match: S("Only parameters whose name contains this."),
    link: S("Elements are inside this linked model."),
    page: P.page,
    doc: P.doc,
  },
});
add({
  group: "read",
  name: "get_view",
  title: "Get view details",
  description:
    "One view or sheet: type, scale, detail level, template, crop, view range, phase, discipline; include= overrides, filters, hidden categories, viewports, revisions. Default: the active view.",
  annotations: RO_IDEM,
  properties: {
    view: S("View name, id, sheet number, or 'active' (default)."),
    include: EA(["overrides", "filters", "categories", "viewports", "revisions"], "Extra blocks."),
    doc: P.doc,
  },
});
add({
  group: "read",
  name: "read_schedule",
  title: "Read schedule",
  description:
    "Read a schedule as a table (paged) with its fields, filters and sorting. category= without schedule= lists the fields available for a new schedule of that category.",
  annotations: RO_IDEM,
  properties: {
    schedule: S("Schedule name or id."),
    category: S("Category for available fields before edit_schedules create."),
    rows: B("Include body rows. Default true."),
    available: B("Include fields that could be added. Default false."),
    name: S("available: field name contains."),
    key: S("Field unique per row that maps rows to element ids. Default Mark; no id column if not unique."),
    limit: I("Rows per page, 1-1000. Default 100."),
    page: P.page,
    doc: P.doc,
  },
});
add({
  group: "read",
  name: "read_family",
  title: "Read family",
  description:
    "Family parameters (name, type/instance, group, data type, formula, shared), family types with per-type values, category and nested families. family= loaded family, path= an .rfa file, or doc= an open family doc.",
  annotations: RO_IDEM,
  properties: {
    family: S("Loaded family name or id (read without opening the UI)."),
    path: S("Path to an .rfa file (opened hidden, read-only)."),
    types: SA("Only these family types' values. Default all (up to 30)."),
    detail: P.detail,
    doc: P.doc,
  },
});
add({
  group: "read",
  name: "check_model",
  title: "Check model",
  description:
    "Model health: stats (counts by category/level), warnings (grouped, element ids), readiness for common tasks, purgeable (unused items), clashes (set a vs b, links ok). Default check=stats.",
  annotations: RO_IDEM,
  properties: {
    check: E(["stats", "warnings", "readiness", "purgeable", "clashes"], "What to check. Default stats."),
    a: SA("Clashes: categories or an r# handle."),
    b: SA("Clashes: categories or an r# handle. Default = a."),
    link: S("Clashes: set b is inside this linked model."),
    tolerance: N("Clashes: ignore overlaps smaller than this, mm. Default 0."),
    match: S("Warnings: text contains."),
    severity: E(["warning", "error"], "Warnings: only this severity."),
    ids: SA("Warnings involving these elements."),
    group_by: S("Stats: category, level, type, workset."),
    scenarios: SA("Readiness: walls, floors, rooms, families, sheets, tags... Default common set."),
    detail: P.detail,
    limit: P.limit,
    page: P.page,
    doc: P.doc,
  },
});
add({
  group: "read",
  name: "get_quantities",
  title: "Get quantities",
  description:
    "Quantity takeoff: count, length (m), area (m2), volume (m3) summed by material, type, level, category or family, for all or filtered elements. Default by=material.",
  annotations: RO_IDEM,
  properties: {
    by: E(["material", "type", "level", "category", "family"], "Group rows by. Default material."),
    category: P.category,
    level: P.level,
    view: S("Only elements visible in this view."),
    from: P.from,
    material: S("Material name contains."),
    paint: B("Include painted areas. Default false."),
    limit: P.limit,
    page: P.page,
    doc: P.doc,
  },
});
add({
  group: "read",
  name: "get_changes",
  title: "Get changes",
  description:
    "What changed since a point: added, modified and deleted elements (id, category) and transaction names, by you or the user. since='session' (default), 'last' (your last write) or a mark m#.",
  annotations: RO_IDEM,
  properties: {
    since: S("'session', 'last', or a mark like m12 from an earlier result."),
    limit: P.limit,
    doc: P.doc,
  },
});
add({
  group: "read",
  name: "read_many",
  title: "Run several reads",
  description:
    "Run up to 8 read calls in one round trip, sharing one time budget (max 2 images): calls=[{tool:'list',args:{kind:'levels'}},{tool:'capture',args:{}}]. One failure does not stop the rest.",
  annotations: RO_IDEM,
  properties: {
    calls: {
      type: "array",
      description: "Up to 8 {tool, args}; args as for that tool.",
      items: {
        type: "object",
        properties: { tool: E(READ_TOOLS_FOR_MANY, "A read tool."), args: { type: "object", description: "That tool's args." } },
        required: ["tool"],
      },
    },
  },
  required: ["calls"],
});

// =====================================================================================
// VISUAL
// =====================================================================================
add({
  group: "visual",
  name: "capture",
  title: "Capture image",
  description:
    "See the model: returns an image of a view or sheet, or of elements (ids/from) in a temporary 3D box. region= zooms; size small|medium|large; compare= c# shows what changed. Nothing is kept in the model.",
  annotations: RO,
  properties: {
    view: S("View name, id, sheet number, or 'active' (default)."),
    ids: SA("Focus these elements: temp 3D view boxed around them."),
    from: S("Focus an element set: r# handle, 'selection' or 'last'."),
    region: PT("Zoom to [x0,y0,x1,y1] mm (model coords; sheet mm on sheets)."),
    orient: E(["iso_se", "iso_sw", "iso_ne", "iso_nw", "top", "front", "back", "left", "right"], "3D direction. Default iso_se."),
    style: E(["wireframe", "hidden", "shaded", "consistent", "realistic"], "Default: view's own; temp 3D shaded."),
    size: E(["small", "medium", "large"], "Long edge at most 768, 1280 or 1568 px. Default medium."),
    margin: N("Space around focused elements, mm. Default 1000."),
    highlight: SA("Tint these element ids red in the image."),
    annotations: B("Show annotations. Default true for views, false for focus."),
    compare: S("Earlier capture id (c#): marks changed pixels."),
    format: E(["auto", "png", "jpg"], "Default auto (png drawings, jpg shaded)."),
    doc: P.doc,
  },
});

// =====================================================================================
// UI
// =====================================================================================
const UI_OPS = {
  select: ["ids/from/filter", "mode,zoom"],
  zoom_to: ["ids/from/region/fit", "view"],
  isolate: ["ids/from/category", "view"],
  hide: ["ids/from/category", "view"],
  reset: ["", "view"],
  activate_view: ["view", ""],
  activate_doc: ["doc", ""],
  open_views: ["", ""],
  close_views: ["views", ""],
  dialogs: ["", "instance"],
  press: ["button", "dialog,instance,confirm"],
};
add({
  group: "ui",
  name: "ui",
  title: "Control Revit UI",
  description:
    "Drive the Revit window without changing the model: select, zoom_to, temporary isolate/hide, reset, activate_view, activate_doc, list/close open views, list dialogs and press a dialog button.",
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  properties: {
    op: opParam(UI_OPS),
    ids: P.ids,
    from: P.from,
    filter: P.filter,
    category: P.category,
    view: P.view,
    views: SA("close_views: names/ids, or ['all_but_active']."),
    region: PT("[x0,y0,x1,y1] mm."),
    fit: B("zoom_to: zoom to fit the whole view."),
    mode: E(["replace", "add", "remove"], "select: default replace."),
    zoom: B("select: also zoom to the selection."),
    dialog: S("Dialog id from op=dialogs. Default: the open one."),
    button: S("Button text or id. Cancel/Close apply at once; other buttons need the user's OK (confirm)."),
    instance: S("dialogs/press: Revit year or process id. Default: every Revit."),
    confirm: P.confirm,
    doc: P.doc,
  },
  required: ["op"],
});

// =====================================================================================
// DOMAIN WRITES
// =====================================================================================
const CREATE_OPS = {
  level: ["elevation", "name,plans,count,spacing"],
  grid: ["start,end", "through,name,count,spacing"],
  wall: ["level,points/start+end", "type,height,top,offset,top_offset,line,structural,flip,closed,through"],
  floor: ["level,points/from", "type,holes,offset,slope,structural"],
  roof: ["level,points", "type,slope,slope_edges,overhang,offset"],
  roof_extrusion: ["level,start,end,profile,depth", "type"],
  ceiling: ["level,points/from", "type,holes,offset"],
  room: ["level,at/all", "name,number,department,allow_duplicate"],
  room_separator: ["view,points", "closed"],
  area: ["view,at", "name,number"],
  area_boundary: ["view,points", "closed"],
  opening: ["host,points/start+end", "offset,height"],
  shaft: ["level,top,points", "offset,top_offset"],
  ref_plane: ["start,end", "name,view"],
  model_line: ["points", "level,line_style,closed,through"],
  stairs: ["level,top,points", "type,width,risers"],
  railing: ["points/host", "level,type"],
  curtain_grid: ["host,direction,positions/spacing", "mullion"],
  toposolid: ["points", "type,level,survey"],
};
add({
  group: "write",
  name: "create_elements",
  title: "Create elements",
  description:
    "Create levels, grids, walls, floors, roofs, ceilings, rooms, areas, separators, openings, shafts, ref planes, model lines, stairs, railings, curtain grids, toposolids. mm; applies now, returns ids and undo.",
  annotations: W,
  properties: {
    op: opParam(CREATE_OPS),
    level: S("Base level name or id."),
    top: S("Top level (wall top constraint, shaft, stairs)."),
    elevation: N("Level elevation mm."),
    name: P.name,
    number: S("Room/area number."),
    department: S("Room department."),
    plans: B("level: also create floor and ceiling plans. Default true."),
    count: I("level/grid: how many to create. Default 1."),
    spacing: N("level/grid: distance between copies mm; curtain_grid: grid spacing."),
    start: P.start,
    end: P.end,
    through: PT("Point on the arc: makes an arc (single segment)."),
    points: PTS("[[x,y],...] mm: wall path, outline, stair path; toposolid [x,y,z] (z above level if set, else absolute)."),
    profile: PTS("roof_extrusion: profile [[s,z],...] mm; s along start->end, z above level."),
    holes: { type: "array", items: { type: "array", items: { type: "array", items: { type: "number" } } }, description: "Inner loops [[[x,y],...],...]." },
    closed: B("Close the loop back to the first point."),
    from: S("floor/ceiling: follow room boundaries: r# handle or 'selection' of rooms."),
    type: P.type,
    height: N("wall: unconnected height mm when no top (default 3000); opening: opening height mm."),
    offset: N("Height above level mm (wall opening: sill height). Default 0."),
    top_offset: N("Offset from top level mm."),
    line: E(["center", "core_center", "finish_ext", "finish_int", "core_ext", "core_int"], "wall location line. Default center."),
    structural: B("Structural wall/floor."),
    flip: B("wall: flip orientation."),
    slope: N("Degrees (floor, roof)."),
    slope_edges: { type: "array", items: { type: "integer" }, description: "roof: edge indexes that slope. Default all when slope set." },
    overhang: N("roof: overhang mm."),
    depth: N("roof_extrusion: extrusion length mm (left of start->end)."),
    at: P.at,
    all: B("room: place rooms in every enclosed area of the level."),
    allow_duplicate: B("room: allow a duplicate number."),
    view: S("Plan view (separators, areas, ref plane)."),
    host: S("Host id: wall/floor/roof/ceiling (opening), stairs (railing), curtain wall (curtain_grid)."),
    width: N("stairs: run width mm."),
    risers: I("stairs: riser count. Default from height."),
    direction: E(["vertical", "horizontal"], "curtain_grid: direction of the new grid lines."),
    positions: { type: "array", items: { type: "number" }, description: "curtain_grid: offsets mm from the wall start (vertical) or base (horizontal)." },
    mullion: S("curtain_grid: mullion type to add on new lines."),
    line_style: S("Line style name."),
    survey: PTS("toposolid: extra [[x,y,z]] points inside the outline."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const PLACE_OPS = {
  place: ["type,at/points/start+end", "level,host,offset,rotation,flip_hand,flip_facing,allow_pinned"],
  load: ["path", "symbols,overwrite,categories,sha256,allow_network"],
};
add({
  group: "write",
  name: "place_family",
  title: "Place family instances",
  description:
    "Place instances of any loadable family (doors, windows, furniture, fixtures, equipment, generic, detail-free) at points, on hosts or along lines; load .rfa families. Hosts are found automatically when omitted.",
  annotations: W,
  properties: {
    op: opParam(PLACE_OPS),
    type: S("Family type: 'Family: Type', type name, or id."),
    at: P.at,
    points: PTS("Several insertion points [[x,y(,z)],...]: one instance each."),
    start: P.start,
    end: P.end,
    level: S("Level for z; z of points is height above it."),
    host: S("Host id (wall, floor, ceiling, roof, face). 'auto' (default) finds the nearest valid host."),
    offset: P.offset,
    rotation: P.rotation,
    flip_hand: B("Flip hand."),
    flip_facing: B("Flip facing."),
    allow_pinned: B("Allow a pinned host."),
    path: S("load: .rfa path visible to Revit."),
    symbols: SA("load: only these types. Default all."),
    overwrite: B("load: overwrite existing parameter values. Default false."),
    categories: SA("load: refuse unless the family is one of these categories."),
    sha256: S("load: refuse unless the file hash matches."),
    allow_network: B("load: allow UNC/network paths."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const MODIFY_OPS = {
  move: ["ids/from/filter,by/to", ""],
  copy: ["ids/from/filter,by/to/levels", "count"],
  rotate: ["ids/from/filter,angle", "center,axis"],
  mirror: ["ids/from/filter,start,end", "keep"],
  array: ["ids/from/filter,count,by/angle", "center,kind"],
  align: ["ids,target", "lock"],
  delete: ["ids/from/filter", "allow_pinned,expect_count,expect_ids"],
  pin: ["ids/from/filter", "pinned"],
  change_type: ["ids/from/filter,type", ""],
  join: ["ids,with", ""],
  unjoin: ["ids,with", ""],
  switch_join: ["ids,with", ""],
  cut: ["ids,with", ""],
  uncut: ["ids,with", ""],
  attach: ["ids,with", "side"],
  detach: ["ids", "with,side"],
  flip: ["ids/from/filter,side", ""],
  split: ["ids,at", ""],
  reshape: ["ids,start+end/points", "through"],
  group: ["ids/from/filter", "name"],
  ungroup: ["ids/from", ""],
  place_group: ["name,at", "level"],
};
add({
  group: "write",
  name: "modify_elements",
  title: "Modify elements",
  description:
    "Edit existing elements chosen by ids, from= or filter=: move, copy, rotate, mirror, array, align, delete, pin, change_type, join/cut/attach, flip, split, reshape, group/ungroup. Deleting >20 asks to confirm.",
  annotations: WD,
  properties: {
    op: opParam(MODIFY_OPS),
    ids: P.ids,
    from: P.from,
    filter: P.filter,
    by: P.by,
    to: PT("Move/copy so the element's location point lands here [x,y(,z)] mm."),
    levels: SA("copy: copy to these levels, aligned (vertical offset only)."),
    count: I("copy/array: number of copies (array includes original)."),
    angle: N("Degrees counterclockwise (rotate, radial array)."),
    center: PT("Rotation/radial center [x,y(,z)]. Default: element center."),
    axis: PT("Rotation axis direction. Default [0,0,1]."),
    start: PT("mirror: axis start; reshape: new curve start."),
    end: PT("mirror: axis end; reshape: new curve end."),
    points: PTS("reshape: new path for curve elements."),
    through: PT("reshape: point on arc."),
    keep: B("mirror: keep original (mirror a copy). Default true."),
    kind: E(["linear", "radial"], "array: default linear."),
    target: S("align: grid, level, ref plane or element id to align to."),
    lock: B("align: lock the alignment."),
    with: S("join/cut/attach: the other element id or r# handle."),
    side: E(["top", "base", "hand", "facing", "wall"], "attach/detach: top|base; flip: hand|facing|wall."),
    at: PT("split: split point; place_group: insertion point."),
    type: P.type,
    pinned: B("pin: true pins (default), false unpins."),
    allow_pinned: B("Allow changing pinned elements."),
    expect_count: I("delete: abort unless exactly this many elements (incl. dependents) would be deleted."),
    expect_ids: SA("delete: abort unless the delete set (incl. dependents) is exactly these ids."),
    name: S("group/place_group: group name."),
    level: S("place_group: level."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

add({
  group: "write",
  name: "set_parameters",
  title: "Set parameters",
  description:
    "Set parameter values on elements, their types, views, sheets or project info: values={Mark:'D1',Width:900} for every target or rows=[{id,values}] each. Lengths mm, areas m2, angles deg; '{Param}' copies values.",
  annotations: W,
  properties: {
    ids: SA("Element, type, view or sheet ids; ['project_info'] for project info."),
    from: P.from,
    filter: P.filter,
    values: O("{param name: value}. Text may use '{Other Param}' and '{n}' (1,2,3...)."),
    rows: OA("Per element: [{id, values:{...}}]."),
    on: E(["instance", "type"], "type = set on the targets' types. Default instance."),
    ...WRITE_TAIL,
  },
});

const TYPE_OPS = {
  duplicate_type: ["type,name", ""],
  rename_type: ["type,name", ""],
  delete_type: ["type", ""],
  set_layers: ["type,layers", ""],
  set_default: ["category,type", ""],
  material_create: ["name", "color,transparency,surface_pattern,cut_pattern,material"],
  material_edit: ["material", "name,color,transparency,surface_pattern,cut_pattern"],
  rename_family: ["family,name", ""],
};
add({
  group: "write",
  name: "edit_types",
  title: "Edit types and materials",
  description:
    "Types and materials: duplicate, rename or delete a type; set wall/floor/roof/ceiling layers; default type per category; create or edit materials (color, transparency, patterns); rename a family.",
  annotations: WD,
  properties: {
    op: opParam(TYPE_OPS),
    type: P.type,
    name: S("New name."),
    category: S("set_default: category."),
    family: S("Family name."),
    material: S("Material name (material_create: copy from this one)."),
    layers: OA("[{function:structure|substrate|thermal|finish1|finish2|membrane, material, thickness}] exterior first."),
    color: P.color,
    transparency: I("0-100."),
    surface_pattern: S("Fill pattern name for surfaces."),
    cut_pattern: S("Fill pattern name for cut."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const VIEW_OPS = {
  create: ["kind", "level,name,view_type,template,scale,start,end,depth,height,at,direction,orient,section_box,ids,from,region,view,area_scheme,perspective"],
  duplicate: ["view", "mode,name,sheet,at"],
  rename: ["view,name", ""],
  set: ["view/views", "scale,detail_level,style,discipline,phase,phase_filter,template,crop,crop_box,crop_visible,view_range,far_clip,scope_box,orient,section_box,underlay"],
  create_template: ["view,name", ""],
  delete: ["views", ""],
};
add({
  group: "write",
  name: "edit_views",
  title: "Create and edit views",
  description:
    "Create views (floor, ceiling, structural, area plan, section, elevation, 3D, callout, drafting, legend copy), duplicate, rename, delete, templates; set scale, detail, crop, view range, phase, 3D orientation, section box.",
  annotations: WD,
  properties: {
    op: opParam(VIEW_OPS),
    kind: E(["floor_plan", "ceiling_plan", "structural_plan", "area_plan", "section", "elevation", "3d", "callout", "drafting", "legend"], "create: view kind."),
    view: S("View name or id (callout/legend: the parent/source view)."),
    views: P.views,
    level: P.level,
    name: P.name,
    view_type: S("View family type name. Default: first of the kind."),
    template: S("View template name; 'none' removes it."),
    scale: I("Scale denominator: 100 = 1:100."),
    detail_level: E(["coarse", "medium", "fine"], "Detail level."),
    style: E(["wireframe", "hidden", "shaded", "consistent", "realistic"], "Visual style."),
    discipline: E(["architectural", "structural", "mechanical", "electrical", "plumbing", "coordination"], "View discipline."),
    phase: S("Phase name."),
    phase_filter: S("Phase filter name."),
    crop: B("Crop view on/off."),
    crop_box: PT("Crop [x0,y0,x1,y1] mm (plan) or [x0,y0,z0,x1,y1,z1]."),
    crop_visible: B("Show crop region."),
    view_range: O("Plans: {top,cut,bottom,depth} mm above the view level; or {cut:1200}."),
    far_clip: N("Sections/elevations: far clip depth mm."),
    scope_box: S("Scope box name; 'none' removes."),
    orient: E(["iso_se", "iso_sw", "iso_ne", "iso_nw", "top", "front", "back", "left", "right"], "3D direction."),
    section_box: PT("3D: [x0,y0,z0,x1,y1,z1] mm; [] turns it off."),
    ids: SA("create 3d/section: fit the box around these elements."),
    from: S("Like ids: r# handle or 'selection'."),
    start: PT("section: cut line start [x,y]."),
    end: PT("section: cut line end [x,y] (looks left of start->end)."),
    depth: N("section: view depth mm. Default 3000."),
    height: N("section: height mm above level. Default level-to-level."),
    at: PT("elevation: marker point [x,y]; duplicate: viewport center on sheet."),
    direction: E(["north", "south", "east", "west"], "elevation: looking direction."),
    region: PT("callout: [x0,y0,x1,y1] mm in the parent view."),
    area_scheme: S("area_plan: area scheme name."),
    perspective: B("3d: perspective camera. Default false (isometric)."),
    mode: E(["copy", "detailing", "dependent"], "duplicate: default copy."),
    sheet: S("duplicate: also place the copy on this sheet."),
    underlay: S("Underlay level name; 'none'."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const GFX_OPS = {
  override: ["view,ids/from/filter/category/view_filter", "color,fill,line_weight,fill_pattern,transparency,halftone"],
  reset: ["view", "ids/from/category/view_filter"],
  hide: ["view,ids/from/category", ""],
  unhide: ["view,ids/from/category", ""],
  color_by: ["view,category,param", ""],
  filter_create: ["view_filter,categories,rules", "view,visible,color,fill,transparency,halftone"],
  filter_edit: ["view_filter", "categories,rules"],
  filter_apply: ["view_filter,view/views", "visible,color,fill,line_weight,fill_pattern,transparency,halftone"],
  filter_remove: ["view_filter,view/views", ""],
};
add({
  group: "write",
  name: "view_graphics",
  title: "View graphics and filters",
  description:
    "Change how a view shows things: override color, lines, pattern, transparency, halftone for elements, categories or filters; permanent hide/unhide; view filters with rules; color by parameter; reset.",
  annotations: W,
  properties: {
    op: opParam(GFX_OPS),
    view: S("View name/id or 'active'; a template name also works."),
    views: P.views,
    ids: P.ids,
    from: P.from,
    filter: P.filter,
    category: P.category,
    view_filter: S("Revit view filter name."),
    categories: SA("filter_create/edit: categories the filter applies to."),
    rules: SA("Filter rules 'Param op value' (ANDed), op: = != > >= < <= contains startswith."),
    param: S("color_by: parameter whose values get distinct colors."),
    color: P.color,
    fill: B("Solid surface fill in color. Default true for elements, false for filters."),
    line_weight: I("1-16."),
    fill_pattern: S("Fill pattern name. Default Solid fill."),
    transparency: I("0-100."),
    halftone: B("Halftone."),
    visible: B("filter_apply: filtered elements visible. Default true."),
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
});

const SHEET_OPS = {
  create: ["number/items", "name,titleblock"],
  duplicate: ["sheet,number", "name,mode,prefix"],
  rename: ["sheet", "number,name"],
  set_titleblock: ["sheet/sheets,titleblock", ""],
  place: ["sheet,view", "at,type"],
  move_viewport: ["sheet,view,at/by", ""],
  remove_viewport: ["sheet,view", ""],
  set_viewport_type: ["sheet,view,type", ""],
  revision_create: ["description", "date,issued_by,issued_to"],
  revision_edit: ["revision", "description,date,issued_by,issued_to,issued"],
  revision_add: ["revision,sheet/sheets", ""],
  revision_remove: ["revision,sheet/sheets", ""],
};
add({
  group: "write",
  name: "edit_sheets",
  title: "Edit sheets",
  description:
    "Sheets: create (one or many), duplicate, renumber/rename, set titleblock; place, move or remove views, schedules and legends; viewport type; revisions create/edit and add/remove on sheets. Sheet mm from origin.",
  annotations: WD,
  properties: {
    op: opParam(SHEET_OPS),
    sheet: S("Sheet number, name or id."),
    sheets: SA("Several sheets (numbers/ids)."),
    number: S("Sheet number (duplicate/rename: the new number)."),
    name: P.name,
    items: OA("create many: [{number,name}]."),
    titleblock: S("Titleblock type. Default: first loaded."),
    mode: E(["empty", "with_views", "with_detailing"], "duplicate: default with_views."),
    prefix: S("duplicate: prefix for copied view names."),
    view: S("View, schedule or legend name/id (placed or to place)."),
    at: PT("Viewport center [x,y] mm on the sheet. Default: sheet center."),
    by: PT("move_viewport: shift [dx,dy] mm."),
    type: S("Viewport type name."),
    revision: S("Revision description, number or id."),
    description: S("Revision description."),
    date: S("Revision date text."),
    issued_by: S("Issued by."),
    issued_to: S("Issued to."),
    issued: B("Mark revision issued."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const ANNO_OPS = {
  tag: ["view,ids/from/filter", "type,at,leader,orientation"],
  tag_all: ["view,category", "type,untagged_only,leader,offset"],
  text: ["view,at,text", "type,width,rotation,leader"],
  dimension: ["view,refs/ids,start,end", "type,mode,overrides"],
  dimension_edit: ["ids", "type,by,overrides,refs,start,end"],
  spot: ["view,kind,host,at", "type,leader"],
  detail_line: ["view,points", "line_style,closed,through"],
  filled_region: ["view,points", "type,holes,line_style"],
  detail_item: ["view,type,at/start+end", "rotation"],
  revision_cloud: ["view,points,revision", ""],
  copy_to_views: ["view,views,ids/from", ""],
};
add({
  group: "write",
  name: "annotate",
  title: "Annotate views",
  description:
    "Annotation in a view: tag elements or tag_all by category, text notes, dimensions (refs, grids, walls), spot elevations/coordinates, detail lines, filled regions, detail items/symbols, revision clouds, copy to views.",
  annotations: W,
  properties: {
    op: opParam(ANNO_OPS),
    view: S("View name/id or 'active'."),
    views: SA("copy_to_views: target views."),
    ids: P.ids,
    from: P.from,
    filter: P.filter,
    category: P.category,
    type: S("Tag/text/dimension/region/detail type. Default: category default."),
    at: P.at,
    start: PT("Dimension line start / line-based item start."),
    end: PT("Dimension line end / line-based item end."),
    through: PT("Point on arc."),
    points: PTS("Line/loop points [[x,y],...] mm."),
    holes: { type: "array", items: { type: "array", items: { type: "array", items: { type: "number" } } }, description: "filled_region inner loops." },
    text: S("Text note content."),
    width: N("Text wrap width mm (paper)."),
    rotation: P.rotation,
    leader: B("Add a leader."),
    orientation: E(["horizontal", "vertical", "model"], "Tag orientation."),
    untagged_only: B("tag_all: skip elements already tagged. Default true."),
    offset: PT("tag_all: tag head offset [dx,dy] mm from element center."),
    refs: SA("dimension: stable reference strings from describe_elements."),
    mode: E(["centers", "faces", "exterior", "core"], "dimension with ids: which references. Default centers."),
    overrides: OA("Dimension text: [{segment,value,prefix,suffix,above,below}]."),
    by: P.by,
    kind: E(["elevation", "coordinate", "slope"], "spot kind."),
    host: S("spot: element id whose face is picked at 'at'."),
    revision: S("Revision description or number."),
    line_style: S("Line style name."),
    closed: B("Close the loop."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const SCHED_OPS = {
  create: ["category", "name,kind,fields,filters,sort,itemize,totals"],
  add_fields: ["schedule,fields", ""],
  remove_fields: ["schedule,fields", ""],
  set_field: ["schedule,field", "heading,hidden,totals"],
  order_fields: ["schedule,fields", ""],
  set_filters: ["schedule,filters", ""],
  set_sort: ["schedule,sort", "headers,totals"],
  set_options: ["schedule", "name,itemize,totals"],
  duplicate: ["schedule,name", ""],
};
add({
  group: "write",
  name: "edit_schedules",
  title: "Edit schedules",
  description:
    "Create schedules (regular, material takeoff, key) with fields, filters, sorting; add, remove, order, rename or hide fields; set filters, sort/group, itemize, totals; duplicate. Read rows with read_schedule.",
  annotations: W,
  properties: {
    op: opParam(SCHED_OPS),
    schedule: S("Schedule name or id."),
    category: S("create: category, e.g. Doors."),
    name: P.name,
    kind: E(["regular", "material_takeoff", "key"], "create: default regular."),
    fields: SA("Field names, e.g. ['Mark','Type','Width']."),
    field: S("One field name."),
    heading: S("Column heading."),
    hidden: B("Hide the column."),
    filters: SA("['Level = Level 1', 'Width > 900'] (max 8, ANDed); [] clears."),
    sort: SA("Sort/group fields in order; prefix '-' for descending."),
    headers: B("set_sort: group headers. Default false."),
    itemize: B("Itemize every instance. Default true."),
    totals: B("Grand totals (set_field: column total)."),
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
});

const FAM_OPS = {
  open: ["family", ""],
  add_param: ["name", "kind,data,group,shared,formula,value,reporting"],
  remove_param: ["name", ""],
  rename_param: ["name,new_name", ""],
  set_param: ["name", "kind,group,reporting"],
  set_formula: ["name,formula", ""],
  set_values: ["name+value/rows", "types"],
  add_type: ["name", "copy_from,values"],
  rename_type: ["name,new_name", ""],
  delete_type: ["name", ""],
  set_category: ["category", ""],
  load_into: ["", "into,overwrite"],
  save: ["", ""],
  save_as: ["path", ""],
};
add({
  group: "write",
  name: "edit_family",
  title: "Edit family parameters and types",
  description:
    "Edit a family's parameters and types: add/remove/rename params, formulas, values per type, add/rename/delete types. family= a loaded family (edited, then reloaded) or doc= an open .rfa; save/save_as.",
  annotations: WD,
  properties: {
    op: opParam(FAM_OPS),
    family: S("Loaded family: edited in the background and reloaded. open: keep it open as doc #n."),
    name: S("Parameter name (type ops: the type name)."),
    new_name: S("New name."),
    kind: E(["type", "instance"], "Default type."),
    data: S("Data type: text, length, area, volume, angle, number, integer, yesno, material, url, image, or family_type:<Category>."),
    group: S("Properties group, e.g. Dimensions, Identity Data, Constraints. Default Other."),
    shared: S("Shared parameter name or GUID (from the shared parameter file)."),
    reporting: B("Reporting parameter."),
    formula: S("Formula, e.g. 'Width / 2'; '' clears."),
    value: S("Value (lengths mm; '900' ok)."),
    values: O("add_type: {param: value}."),
    types: SA("set_values: family types to change; ['*'] = all. Required when >1 type."),
    rows: OA("set_values: [{type, values:{param: value}}]."),
    copy_from: S("add_type: copy values from this type."),
    category: S("set_category: family category."),
    reload: B("With family=: reload into the project after the edit. Default true."),
    overwrite: B("On reload: overwrite project type parameter values. Default: only if this call changed type values."),
    into: SA("load_into: open projects to load into. Default: the pinned project only; more than one asks to confirm."),
    path: S("save_as: .rfa path."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const MEP_OPS = {
  pipe: ["points,level", "type,system,size,offset,slope,fittings"],
  duct: ["points,level", "type,system,size/width+height,offset,fittings"],
  conduit: ["points,level", "type,size,offset,fittings"],
  cable_tray: ["points,level", "type,width,height,offset,fittings"],
  flex_pipe: ["points,level", "type,system,size,offset"],
  flex_duct: ["points,level", "type,system,size,offset"],
  connect: ["ids", ""],
  system_create: ["ids,kind", "name,type"],
  system_add: ["system,ids", ""],
  system_remove: ["system,ids", ""],
  circuit: ["ids", "panel"],
  insulate: ["ids/from,type,thickness", ""],
  space: ["level,at/all", "name,number"],
  zone: ["name,ids", "level"],
};
add({
  group: "write",
  name: "mep",
  title: "MEP systems",
  description:
    "MEP: pipes, ducts, conduits, cable trays and flex runs along points with automatic elbows; connect elements; create/edit piping, duct and electrical systems, circuits; insulation; spaces and zones.",
  annotations: W,
  properties: {
    op: opParam(MEP_OPS),
    points: PTS("Run path [[x,y],...] or [[x,y,z],...] (z above level)."),
    level: P.level,
    offset: N("Run centerline height above level mm. Default 2700."),
    type: S("Pipe/duct/conduit/tray/insulation type name."),
    system: S("System type (e.g. Domestic Cold Water, Supply Air) or system name."),
    size: N("Diameter mm."),
    width: N("Duct/tray width mm."),
    height: N("Duct/tray height mm."),
    slope: N("Pipes: slope in percent. Default 0."),
    fittings: B("Add elbows/tees at bends. Default true."),
    ids: P.ids,
    from: P.from,
    kind: E(["piping", "duct", "electrical"], "system_create kind."),
    name: P.name,
    number: S("Space number."),
    at: P.at,
    all: B("space: in every enclosed area of the level."),
    thickness: N("insulate: mm."),
    panel: S("circuit: panel name or id."),
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const STRUCT_OPS = {
  column: ["type,level,at/points/grids", "top,offset,top_offset,rotation,start,end"],
  beam: ["type,level,start+end/points/between", "offset"],
  brace: ["type,level,start,end", ""],
  beam_system: ["type,level,points", "spacing,direction"],
  foundation: ["kind,type", "ids/from,points,level"],
};
add({
  group: "write",
  name: "structure",
  title: "Structural elements",
  description:
    "Structure: columns at points or grid intersections, beams along lines or between columns, braces, beam systems, isolated/wall/slab foundations. type='Family: Type'; lengths mm.",
  annotations: W,
  properties: {
    op: opParam(STRUCT_OPS),
    type: P.type,
    level: S("Base or reference level."),
    top: S("column: top level. Default next level up."),
    at: P.at,
    points: PTS("Several points (columns) or a chain (beams, beam_system outline)."),
    grids: SA("column: at intersections of these grids; ['*'] = all."),
    between: SA("beam: column ids to connect in order."),
    start: PT("Beam/brace or slanted column start [x,y,z]."),
    end: PT("Beam/brace or slanted column end [x,y,z]."),
    offset: N("Offset from level mm (beam z, column base)."),
    top_offset: N("column: top offset mm."),
    rotation: P.rotation,
    spacing: N("beam_system: spacing mm."),
    direction: N("beam_system: beam direction degrees. Default 0 (x axis)."),
    kind: E(["isolated", "wall", "slab"], "foundation kind."),
    ids: SA("foundation: columns (isolated) or walls (wall)."),
    from: P.from,
    ...WRITE_TAIL,
  },
  required: ["op"],
});

const DOC_OPS = {
  open: ["path", "activate,detach,audit,worksets,local,central"],
  activate: ["doc", ""],
  save: ["", "compact"],
  save_as: ["path", "overwrite,as_central,compact"],
  close: ["doc", "save"],
  new_project: ["path", "template,overwrite,activate"],
  new_family: ["template,path", ""],
  purge: ["", "passes"],
  set_units: ["unit", "accuracy"],
  coordinates: ["base_point/survey_point/true_north", ""],
};
add({
  group: "write",
  name: "manage_document",
  title: "Manage documents",
  description:
    "Files: open (rvt/rfa; detach, audit, worksets), activate, save, save_as, close, new project from template, new family from .rft, purge unused, project units, base/survey point. Risky ones ask to confirm.",
  annotations: WD_OPEN,
  properties: {
    op: opParam(DOC_OPS),
    path: S("File path (.rvt, .rfa)."),
    template: S("Template .rte/.rft path or name. new_project default: settings template."),
    activate: B("open/new_project: show it in the UI. Default true."),
    local: S("open (central model): path of the new local copy. Default Documents\\<name>_<user>.rvt."),
    central: B("open: open the central file itself instead of a local copy (asks to confirm)."),
    detach: E(["no", "preserve", "discard"], "open: detach from central. Default no."),
    audit: B("open: audit."),
    worksets: E(["all", "none", "last", "editable"], "open: worksets to open. Default last."),
    overwrite: B("Replace an existing file."),
    as_central: B("save_as: save as central model."),
    compact: B("Compact the file."),
    save: B("close: save first. Default false (unsaved changes ask to confirm)."),
    passes: I("purge: repeat passes 1-3. Default 3."),
    unit: E(["mm", "cm", "m", "in", "ft"], "set_units: project length unit."),
    accuracy: N("set_units: rounding, e.g. 1 or 0.1."),
    base_point: PT("coordinates: project base point [x,y,z] mm."),
    survey_point: PT("coordinates: survey point [x,y,z] mm."),
    true_north: N("coordinates: true north angle degrees."),
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
});

const WS_OPS = {
  sync: ["", "comment,relinquish,compact"],
  reload_latest: ["", ""],
  relinquish: ["", ""],
  workset_create: ["name", ""],
  workset_rename: ["workset,name", ""],
  set_active: ["workset", ""],
  move_to: ["workset,ids/from/filter", ""],
  borrow: ["ids/from/filter", ""],
  enable: ["", "name"],
};
add({
  group: "write",
  name: "worksharing",
  title: "Worksharing",
  description:
    "Workshared models: sync with central (comment, relinquish), reload latest, relinquish all, create/rename worksets, set active workset, move elements to a workset, borrow elements, enable worksharing.",
  annotations: WD_OPEN,
  properties: {
    op: opParam(WS_OPS),
    comment: S("sync: comment."),
    relinquish: B("sync: relinquish everything after. Default true."),
    compact: B("sync: compact central."),
    workset: S("Workset name."),
    name: S("New workset name (enable: name for the default workset)."),
    ids: P.ids,
    from: P.from,
    filter: P.filter,
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
});

const LINK_OPS = {
  link_rvt: ["path", "position"],
  link_ifc: ["path", "position"],
  link_cad: ["path", "position,view,cad_units,this_view_only"],
  import_cad: ["path", "position,view,cad_units,this_view_only"],
  reload: ["link", ""],
  reload_from: ["link,path", ""],
  unload: ["link", ""],
  remove: ["link", ""],
  acquire_coordinates: ["link", ""],
};
add({
  group: "write",
  name: "links",
  title: "Links and imports",
  description:
    "Linked models and CAD: link RVT/IFC/DWG (origin, center or shared coordinates), import CAD, reload, reload from a new path, unload, remove, acquire coordinates. Read link contents via find_elements link=.",
  annotations: WD_OPEN,
  properties: {
    op: opParam(LINK_OPS),
    path: S("File path."),
    link: S("Link name or id."),
    position: E(["origin", "center", "shared", "base_point"], "Placement. Default origin."),
    view: S("CAD: view to place in. Default active."),
    cad_units: E(["auto", "mm", "cm", "m", "in", "ft"], "CAD import units. Default auto."),
    this_view_only: B("CAD: current view only."),
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
});

add({
  group: "write",
  name: "export",
  title: "Export files",
  description:
    "Export pdf (sheets/views, combined or not), dwg, dxf, ifc, nwc, image (hi-res), csv (schedules), fbx, gbxml to a folder (default <home>/exports/<doc>). Long exports continue as a job.",
  annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: true },
  properties: {
    format: E(["pdf", "dwg", "dxf", "ifc", "nwc", "image", "csv", "fbx", "gbxml"], "File format."),
    views: SA("Views/sheets (names, numbers, ids), or ['all_sheets'], ['set:<sheet set>']. Default active view."),
    folder: S("Output folder. Default <home>/exports/<doc>."),
    name: S("File name pattern, e.g. '{number} - {name}'."),
    combine: B("pdf: one combined file. Default false."),
    setup: S("dwg/dxf/ifc: export setup name."),
    size: I("image: long edge px. Default 3000."),
    color: E(["color", "gray", "bw"], "pdf/image: default color."),
    overwrite: B("Replace existing files. Default false."),
    doc: P.doc,
    confirm: P.confirm,
  },
  required: ["format"],
});

const MD_OPS = {
  inspect: ["sources", "project,recipe"],
  preview: ["recipe", ""],
  execute: ["confirm", ""],
  save_recipe: ["recipe", "expected_sha"],
  get_recipe: ["project", ""],
  list_recipes: ["", "limit"],
  fixture: ["template,folder,id", ""],
};
add({
  group: "write",
  name: "model_delivery",
  title: "Model delivery package",
  description:
    "Delivery packages of standalone RVTs (links, cleanup, exports, QA) from a saved recipe: inspect sources, preview (gives confirm), execute (job), save/get/list recipes, test fixture. Recipe shape: help topic=recipe.",
  annotations: WD_OPEN,
  properties: {
    op: opParam(MD_OPS),
    sources: SA("inspect: source .rvt paths."),
    project: S("Project id (recipe library key)."),
    recipe: O("Delivery recipe; shape in help {topic:'recipe'}."),
    expected_sha: S("save_recipe: only overwrite this version."),
    template: S("fixture: .rte template path."),
    folder: S("fixture: new empty folder."),
    id: S("fixture: fixture id."),
    limit: I("list_recipes: default 100."),
    confirm: P.confirm,
  },
  required: ["op"],
});

// =====================================================================================
// BATCH / CONTROL / HELP / OPT-IN
// =====================================================================================
add({
  group: "control",
  name: "change_set",
  title: "Atomic change set",
  description:
    "Run up to 100 write ops in one undo step, all or nothing; later ops use earlier results: '$0' (first id), '$0.ids', '$1.type'. ops=[{tool:'create_elements',op:'level',...}]. Not for open/save/sync/close.",
  annotations: WD,
  properties: {
    ops: {
      type: "array",
      description: "[{tool, op, ...that op's params}].",
      items: {
        type: "object",
        properties: { tool: E(WRITE_TOOLS, "Write tool."), op: { type: "string", description: "That tool's op." } },
        required: ["tool", "op"],
      },
    },
    name: S("Undo history name. Default 'change_set'."),
    doc: P.doc,
    preview: P.preview,
    confirm: P.confirm,
    units: P.units,
  },
});
add({
  group: "control",
  name: "undo",
  title: "Undo",
  description:
    "Undo your last write(s) in the target doc while they are still Revit's newest changes (steps 1-10, default 1); redo=true redoes. If the user edited since, explains how to revert instead.",
  annotations: WD,
  properties: {
    steps: I("1-10. Default 1."),
    redo: B("Redo instead."),
    mode: E(["auto", "compensate"], "auto (default): Revit undo; compensate: apply the inverse change as a new write."),
    doc: P.doc,
  },
});
add({
  group: "control",
  name: "job_status",
  title: "Job status",
  description:
    "State, progress and result of a job (j#: export, sync, open, delivery, long scan) or of a write whose outcome was unclear (w#). wait= seconds to wait, 0-45, default 20. No id: your recent jobs.",
  annotations: RO_IDEM,
  properties: {
    id: S("Job id j# or write id w#."),
    wait: I("Seconds to wait for completion, 0-45. Default 20."),
  },
});
add({
  group: "control",
  name: "cancel_job",
  title: "Cancel job",
  description:
    "Cancel a queued or running job or queued request. A running Revit step finishes or rolls back safely; partial exports and delivery staging are removed.",
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  properties: {
    id: S("Job id j# or request id w#."),
    reason: S("Why (logged)."),
  },
  required: ["id"],
});
add({
  group: "control",
  name: "help",
  title: "Help and examples",
  description:
    "Params and a working example for a tool or op: help {tool:'create_elements',op:'wall'}. topic= units, targeting, selectors, confirm, errors, recipe, run_csharp, workflow:<audit|sheets|family|rooms|visual>, error codes.",
  annotations: RO_IDEM,
  properties: {
    tool: S("Tool name."),
    op: S("Op name."),
    topic: S("Topic, or an error code."),
  },
});
add({
  group: "optin",
  name: "run_csharp",
  title: "Run C# (opt-in)",
  description:
    "Run C# on the Revit API when no tool fits. Off unless the user enabled it locally. read and dry_run (default) always roll back; dry_run reports changes and a token; commit needs that token and the user's OK.",
  annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: true },
  properties: {
    code: S("C# method body with doc, uidoc, app, args (IDictionary), log(object); return a value."),
    args: O("Values passed as args."),
    mode: E(["read", "dry_run", "commit"], "Default dry_run."),
    timeout: I("Seconds 1-45. Default 30."),
    doc: P.doc,
    confirm: P.confirm,
  },
  required: ["code"],
});

// ---------- build tools/list ----------
function buildTool(t) {
  const inputSchema = { type: "object", properties: t.properties };
  if (t.required?.length) inputSchema.required = t.required;
  return { name: t.name, title: t.title, description: t.description, inputSchema, annotations: t.annotations };
}
const DISCRIMINATOR = { list: "kind", check_model: "check", export: "format" };
const listed = tools.map(buildTool);
const defaultListed = listed.filter((t) => t.name !== "run_csharp");
const bytes = (v) => Buffer.byteLength(JSON.stringify(v), "utf8");

const perTool = listed.map((t) => ({
  name: t.name,
  total: bytes(t),
  schema: bytes(t.inputSchema),
  desc: t.description.length,
  params: Object.keys(t.inputSchema.properties).length,
  ops: (t.inputSchema.properties[DISCRIMINATOR[t.name] ?? "op"]?.enum?.length) ?? 0,
}));
const stats = {
  toolCount: defaultListed.length,
  toolCountWithOptIn: listed.length,
  toolsListBytesDefault: bytes({ tools: defaultListed }),
  toolsListBytesWithOptIn: bytes({ tools: listed }),
  maxDescriptionChars: Math.max(...listed.map((t) => t.description.length)),
  longDescriptions: listed.filter((t) => t.description.length > 220).map((t) => `${t.name}:${t.description.length}`),
  opCount: perTool.reduce((a, t) => a + t.ops, 0),
  perTool,
};
const coreNames = ["status", "set_target", "list", "find_elements", "describe_elements", "get_view", "capture", "ui", "create_elements", "place_family", "modify_elements", "set_parameters", "change_set", "undo", "job_status", "help", "read_many"];
stats.coreProfileBytes = bytes({ tools: listed.filter((t) => coreNames.includes(t.name)) });

// forbidden keyword check (Codex drops these; Claude rejects root anyOf)
const forbidden = [];
const BAD = ["const", "default", "minimum", "maximum", "pattern", "format", "$schema", "$ref", "$defs", "anyOf", "oneOf", "allOf", "minLength", "maxLength", "maxItems", "additionalProperties"];
function walkSchema(node, path) {
  if (!node || typeof node !== "object") return;
  for (const k of Object.keys(node)) if (BAD.includes(k)) forbidden.push(path + "." + k);
  if (node.properties) for (const [pk, pv] of Object.entries(node.properties)) walkSchema(pv, path + "." + pk);
  if (node.items) walkSchema(node.items, path + "[]");
}
for (const t of listed) walkSchema(t.inputSchema, t.name);
stats.forbiddenKeywords = forbidden;
function depth(node) {
  if (!node || typeof node !== "object") return 0;
  let d = 0;
  if (node.properties) for (const v of Object.values(node.properties)) d = Math.max(d, depth(v));
  if (node.items) d = Math.max(d, depth(node.items));
  return 1 + d;
}
stats.maxSchemaDepth = Math.max(...listed.map((t) => depth(t.inputSchema)));
const undescribed = [];
for (const t of listed) for (const [k, p] of Object.entries(t.inputSchema.properties)) if (!p.description) undescribed.push(t.name + "." + k);
stats.undescribedParams = undescribed;
const banned = ["ai_element_filter", "get_current_view_elements", "get_selected_elements", "get_available_family_types", "say_hello", "delete_element", "send_code_to_revit"];
stats.bannedNameHits = listed.filter((t) => banned.includes(t.name)).map((t) => t.name);
stats.maxQualifiedNameLen = Math.max(...listed.map((t) => ("mcp__revit__" + t.name).length));

writeFileSync(join(here, "SPEC-tools-list.json"), JSON.stringify({ tools: listed }));

// ---------- markdown ----------
const esc = (s) => String(s).replace(/\|/g, "\\|");
function paramType(p) {
  if (p.enum) return p.enum.length > 8 ? `enum(${p.enum.length})` : p.enum.join("|");
  if (p.type === "array") {
    const it = p.items;
    if (it?.type === "array") return it.items?.type === "array" ? "loops" : "points";
    if (it?.enum) return `[${it.enum.join("|")}]`;
    if (it?.type === "number" && p.description?.match(/^(Point|Start|End|Offset|Zoom|Intersects|Crop|3D|Rotation|Viewport|mirror|reshape|split|Move|section|elevation|callout|tag_all|move_viewport|Dimension|Line-based|coordinates|\[)/i)) return "point/box";
    return `${it?.type ?? "any"}[]`;
  }
  return p.type;
}
let md = "";
const groups = [
  ["session", "Session, targeting, health"],
  ["read", "Reads"],
  ["visual", "Visual"],
  ["ui", "UI control"],
  ["write", "Domain write tools"],
  ["control", "Batch, undo, jobs, help"],
  ["optin", "Opt-in"],
];
for (const [g, title] of groups) {
  md += `\n### ${title}\n`;
  for (const t of tools.filter((x) => x.group === g)) {
    const ann = t.annotations;
    const annTxt = ann.readOnlyHint ? "readOnly" : ann.destructiveHint ? "destructive" : "write (non-destructive)";
    const size = perTool.find((x) => x.name === t.name).total;
    md += `\n#### \`${t.name}\` (${annTxt}${ann.openWorldHint ? ", openWorld" : ""}${ann.idempotentHint ? ", idempotent" : ""}; ${size} B)\n\n`;
    md += `${t.description}\n\n`;
    md += `| param | type | notes |\n|---|---|---|\n`;
    for (const [k, p] of Object.entries(t.properties)) {
      if (k === "op" && p.enum) continue;
      const req = t.required?.includes(k) ? " **(required)**" : "";
      md += `| ${k}${req} | ${esc(paramType(p))} | ${esc(p.description ?? (p.enum ? p.enum.join(", ") : ""))} |\n`;
    }
    const op = t.properties.op;
    if (op && op.enum) {
      md += `\nOps (\`op\` required; params before \`;\` required, \`/\` = alternatives, \`+\` = together):\n\n`;
      const selNote = op.description.startsWith("sel = ") ? "`sel` = one of `ids`, `from`, `filter`.\n\n" : "";
      md += selNote + "```\n" + op.description.replace(/^(sel = ids, from or filter\. )?Params per op, optional after ';': /, "").split(" ").join("\n") + "\n```\n";
    }
  }
}
writeFileSync(join(here, "SPEC-catalog.generated.md"), md);
writeFileSync(join(here, "SPEC-stats.json"), JSON.stringify(stats, null, 1));
console.log(JSON.stringify({ ...stats, perTool: undefined }, null, 1));
console.log(perTool.map((t) => `${t.name.padEnd(18)} ${String(t.total).padStart(6)} B  params ${t.params}  ops ${t.ops}  desc ${t.desc}`).join("\n"));
