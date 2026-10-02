// Catalog entry for `links` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, meta, op, S, syn, UNITS_SYN, WD_OPEN, WRITE, WRITE_TAIL_NOUNITS } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { idle: false, inproc: false });

export const tool: ToolSpec = {
  name: "links",
  title: "Links and imports",
  description: "Linked models and CAD: link RVT/IFC/DWG (origin, center or shared coordinates), import CAD, reload, reload from a new path, unload, remove, acquire coordinates. Read link contents via find_elements link=.",
  group: "write",
  annotations: WD_OPEN,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    path: S("File path."),
    link: S("Link name or id."),
    position: syn(E(["origin","center","shared","base_point"], "Placement. Default origin."), {"autoorigin":"origin","origintoorigin":"origin","centertocenter":"center","centre":"center","sharedcoordinates":"shared","projectbasepoint":"base_point"}),
    view: S("CAD: view to place in. Default active."),
    cad_units: syn(E(["auto","mm","cm","m","in","ft"], "CAD import units. Default auto."), UNITS_SYN),
    this_view_only: B("CAD: current view only."),
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
  ops: {
    link_rvt: op("path", "position", M, [{"op":"link_rvt","path":"C:\\Projects\\Structure.rvt","position":"shared"}]),
    link_ifc: op("path", "position", M, [{"op":"link_ifc","path":"C:\\Projects\\Structure.ifc"}]),
    link_cad: op("path", "position,view,cad_units,this_view_only", M, [{"op":"link_cad","path":"C:\\Projects\\Survey.dwg","view":"Level 1","cad_units":"mm"}]),
    import_cad: op("path", "position,view,cad_units,this_view_only", M, [{"op":"import_cad","path":"C:\\Projects\\Detail.dwg","view":"Detail 1","this_view_only":true}]),
    reload: op("link", "", meta(M, { kind: "lifecycle", tx: "lifecycle", job: "auto", cs: false }), [{"op":"reload","link":"Structure.rvt"}]),
    reload_from: op("link,path", "", meta(M, { kind: "lifecycle", tx: "lifecycle", job: "auto", cs: false }), [{"op":"reload_from","link":"Structure.rvt","path":"C:\\Projects\\Structure_v2.rvt"}]),
    unload: op("link", "", meta(M, { kind: "lifecycle", tx: "lifecycle", cs: false }), [{"op":"unload","link":"Structure.rvt"}]),
    remove: op("link", "", meta(M, { blast: ["always"] }), [{"op":"remove","link":"Structure.rvt"}]),
    acquire_coordinates: op("link", "", meta(M, { blast: ["always"] }), [{"op":"acquire_coordinates","link":"Site.rvt"}]),
  },
};
