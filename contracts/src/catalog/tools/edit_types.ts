// Catalog entry for `edit_types` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { I, meta, OA, op, P, pct, S, WD, WRITE, WRITE_TAIL } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE);

export const tool: ToolSpec = {
  name: "edit_types",
  title: "Edit types and materials",
  description: "Types and materials: duplicate, rename or delete a type; set wall/floor/roof/ceiling layers; default type per category; create or edit materials (color, transparency, patterns); rename a family.",
  group: "write",
  annotations: WD,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    type: P.type,
    name: S("New name."),
    category: S("set_default: category."),
    family: S("Family name."),
    material: S("Material name (material_create: copy from this one)."),
    layers: OA("[{function:structure|substrate|thermal|finish1|finish2|membrane, material, thickness}] exterior first."),
    color: P.color,
    transparency: pct(I("0-100.")),
    surface_pattern: S("Fill pattern name for surfaces."),
    cut_pattern: S("Fill pattern name for cut."),
    ...WRITE_TAIL,
  },
  required: ["op"],
  ops: {
    duplicate_type: op("type,name", "", M, [{"op":"duplicate_type","type":"Generic - 200mm","name":"EBA Wall 240"}]),
    rename_type: op("type,name", "", M, [{"op":"rename_type","type":"EBA Wall 240","name":"EBA Wall 250"}]),
    delete_type: op("type", "", meta(M, { blast: ["delete"], inproc: false }), [{"op":"delete_type","type":"EBA Wall 250"}]),
    set_layers: op("type,layers", "", M, [{"op":"set_layers","type":"EBA Wall 240","layers":[{"function":"finish1","material":"Plaster","thickness":15},{"function":"structure","material":"Concrete","thickness":210},{"function":"finish2","material":"Plaster","thickness":15}]}]),
    set_default: op("category,type", "", M, [{"op":"set_default","category":"Walls","type":"Generic - 200mm"}]),
    material_create: op("name", "color,transparency,surface_pattern,cut_pattern,material", M, [{"op":"material_create","name":"EBA Brick","color":"#B5523B"}]),
    material_edit: op("material", "name,color,transparency,surface_pattern,cut_pattern", M, [{"op":"material_edit","material":"EBA Brick","transparency":20}]),
    rename_family: op("family,name", "", M, [{"op":"rename_family","family":"Single-Flush","name":"EBA Door Single"}]),
  },
};
