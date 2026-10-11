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
    duplicate_type: op("type,name", "", M, [{"op":"duplicate_type","type":"Generic - 200mm","name":"EBA Wall 240"}, {"op":"duplicate_type","type":"Single-Flush: 0915 x 2134mm","name":"1010 x 2135mm"}], {
      help: "The copy stays in the same family; it is output as type for $refs in change_set. NAME_TAKEN suggests the next free name.",
      api: "ElementType.Duplicate",
      errors: ["NAME_TAKEN", "NOT_FOUND", "AMBIGUOUS_NAME"],
    }),
    rename_type: op("type,name", "", M, [{"op":"rename_type","type":"EBA Wall 240","name":"EBA Wall 250"}], { api: "Element.Name", errors: ["NAME_TAKEN"] }),
    delete_type: op("type", "", meta(M, { blast: ["delete"], inproc: false }), [{"op":"delete_type","type":"EBA Wall 250"}], {
      help: "Deletes the type and every instance of it; more than 20 deleted elements returns NOT APPLIED with a confirm token.",
      api: "Document.Delete",
    }),
    set_layers: op("type,layers", "", M, [{"op":"set_layers","type":"EBA Wall 240","layers":[{"function":"finish1","material":"Plaster","thickness":15},{"function":"structure","material":"Concrete","thickness":210},{"function":"finish2","material":"Plaster","thickness":15}]}, {"op":"set_layers","type":"Generic 150mm","layers":[{"function":"finish1","material":"Concrete, Cast-in-Place gray","thickness":50},{"function":"membrane","thickness":0},{"function":"structure","material":"Concrete","thickness":200}]}], {
      help: "Wall, floor, roof and ceiling types; layers exterior (top) first, thickness mm, membranes 0. The core is the span of structure layers. Every instance of the type follows.",
      api: "CompoundStructure.CreateSimpleCompoundStructure + HostObjAttributes.SetCompoundStructure",
      errors: ["UNSUPPORTED_OP", "INVALID_ARGS"],
    }),
    set_default: op("category,type", "", M, [{"op":"set_default","category":"Walls","type":"Generic - 200mm"}, {"op":"set_default","category":"Doors","type":"Single-Flush: 0915 x 2134mm"}], {
      help: "System categories (walls, floors, roofs, ceilings, levels, grids, text, dimensions, pipes, ducts ...) set the element type default; loadable categories set the family type default.",
      api: "Document.SetDefaultElementTypeId / SetDefaultFamilyTypeId",
    }),
    material_create: op("name", "color,transparency,surface_pattern,cut_pattern,material", M, [{"op":"material_create","name":"EBA Brick","color":"#B5523B"}, {"op":"material_create","name":"EBA Glass","material":"Glass","transparency":70}], {
      help: "material copies an existing material (appearance included) first. color is the shading color; patterns are fill pattern names or none.",
      api: "Material.Create / Material.Duplicate",
      errors: ["NAME_TAKEN"],
    }),
    material_edit: op("material", "name,color,transparency,surface_pattern,cut_pattern", M, [{"op":"material_edit","material":"EBA Brick","transparency":20}, {"op":"material_edit","material":"EBA Brick","surface_pattern":"Solid fill","cut_pattern":"none"}], {
      api: "Material.Color / Transparency / SurfaceForegroundPatternId / CutForegroundPatternId",
    }),
    rename_family: op("family,name", "", M, [{"op":"rename_family","family":"Single-Flush","name":"EBA Door Single"}], { help: "Loadable families only (system families have no family element).", api: "Family.Name", errors: ["NAME_TAKEN"] }),
  },
};
