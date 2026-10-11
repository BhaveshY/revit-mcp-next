// Catalog entry for `view_graphics` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, I, meta, op, P, pct, S, SA, W, WRITE, WRITE_TAIL_NOUNITS } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { blast: ["bulk"] });

export const tool: ToolSpec = {
  name: "view_graphics",
  title: "View graphics and filters",
  description: "Change how a view shows things: override color, lines, pattern, transparency, halftone for elements, categories or filters; permanent hide/unhide; view filters with rules; color by parameter; reset.",
  group: "write",
  annotations: W,
  discriminator: "op",
  profiles: ["full"],
  properties: {
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
    transparency: pct(I("0-100.")),
    halftone: B("Halftone."),
    visible: B("filter_apply: filtered elements visible. Default true."),
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
  ops: {
    override: op("view,ids/from/filter/category/view_filter", "color,fill,line_weight,fill_pattern,transparency,halftone", M, [{"op":"override","view":"Level 1","from":"r3","color":"#FF0000","fill":true}, {"op":"override","view":"Level 1","category":["Walls"],"line_weight":6,"color":"0,0,160"}, {"op":"override","view":"Level 1","filter":{"category":"Furniture"},"halftone":true,"transparency":50}], {
      help: "Only the given attributes change; others keep their current override. color sets lines (and the surface fill when fill is true, the default for elements and categories).",
      errors: ["TEMPLATE_CONTROLLED", "UNSUPPORTED_OP", "NOT_FOUND", "INVALID_ARGS"],
    }),
    reset: op("view", "ids/from/category/view_filter", M, [{"op":"reset","view":"Level 1","from":"r3"}, {"op":"reset","view":"Level 1"}], { help: "Without a target it clears every element, category and filter override of the view (template-controlled ones are kept with a warning)." }),
    hide: op("view,ids/from/category", "", M, [{"op":"hide","view":"Level 1","category":["Furniture"]}, {"op":"hide","view":"Level 1","ids":["304512"]}], { help: "Permanent hide in the view; ui hide is the temporary one.", errors: ["TEMPLATE_CONTROLLED"] }),
    unhide: op("view,ids/from/category", "", M, [{"op":"unhide","view":"Level 1","category":["Furniture"]}, {"op":"unhide","view":"Level 1","ids":["304512"]}]),
    color_by: op("view,category,param", "", M, [{"op":"color_by","view":"Level 1","category":["Rooms"],"param":"Department"}, {"op":"color_by","view":"Level 1","category":["Walls"],"param":"Type"}], { help: "Element overrides per distinct value (Tableau-10 palette, grey for empty); the legend comes back in data.legend [[value, color, count]]. Rooms/areas/spaces show their Interior Fill.", errors: ["NOT_FOUND"] }),
    filter_create: op("view_filter,categories,rules", "view,visible,color,fill,transparency,halftone", M, [{"op":"filter_create","view_filter":"Fire rated doors","categories":["Doors"],"rules":["Fire Rating notempty"],"view":"Level 1","color":"red"}, {"op":"filter_create","view_filter":"Thick walls","categories":["Walls"],"rules":["Width >= 300"]}], {
      help: "rules are ANDed: 'Param op value' with op = != > >= < <= contains startswith empty notempty; lengths in mm. The parameter must be filterable for every category.",
      errors: ["NAME_TAKEN", "NOT_FOUND", "INVALID_ARGS"],
    }),
    filter_edit: op("view_filter", "categories,rules", M, [{"op":"filter_edit","view_filter":"Fire rated doors","rules":["Fire Rating = EI30"]}, {"op":"filter_edit","view_filter":"Fire rated doors","categories":["Doors","Windows"]}]),
    filter_apply: op("view_filter,view/views", "visible,color,fill,line_weight,fill_pattern,transparency,halftone", M, [{"op":"filter_apply","view_filter":"Fire rated doors","views":["Level 1","Level 2"],"color":"#FF8800"}, {"op":"filter_apply","view_filter":"Fire rated doors","view":"Level 1","visible":false}], { errors: ["TEMPLATE_CONTROLLED", "NOT_FOUND"] }),
    filter_remove: op("view_filter,view/views", "", M, [{"op":"filter_remove","view_filter":"Fire rated doors","view":"Level 2"}], { errors: ["TEMPLATE_CONTROLLED", "NOT_FOUND"] }),
  },
};
