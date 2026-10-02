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
    override: op("view,ids/from/filter/category/view_filter", "color,fill,line_weight,fill_pattern,transparency,halftone", M, [{"op":"override","view":"Level 1","from":"r3","color":"#FF0000","fill":true}]),
    reset: op("view", "ids/from/category/view_filter", M, [{"op":"reset","view":"Level 1","from":"r3"}]),
    hide: op("view,ids/from/category", "", M, [{"op":"hide","view":"Level 1","category":["Furniture"]}]),
    unhide: op("view,ids/from/category", "", M, [{"op":"unhide","view":"Level 1","category":["Furniture"]}]),
    color_by: op("view,category,param", "", M, [{"op":"color_by","view":"Level 1","category":["Rooms"],"param":"Department"}]),
    filter_create: op("view_filter,categories,rules", "view,visible,color,fill,transparency,halftone", M, [{"op":"filter_create","view_filter":"Fire rated doors","categories":["Doors"],"rules":["Fire Rating notempty"],"view":"Level 1","color":"red"}]),
    filter_edit: op("view_filter", "categories,rules", M, [{"op":"filter_edit","view_filter":"Fire rated doors","rules":["Fire Rating = EI30"]}]),
    filter_apply: op("view_filter,view/views", "visible,color,fill,line_weight,fill_pattern,transparency,halftone", M, [{"op":"filter_apply","view_filter":"Fire rated doors","views":["Level 1","Level 2"],"color":"#FF8800"}]),
    filter_remove: op("view_filter,view/views", "", M, [{"op":"filter_remove","view_filter":"Fire rated doors","view":"Level 2"}]),
  },
};
