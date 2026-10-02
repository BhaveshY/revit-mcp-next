// Catalog entry for `find_elements` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, meta, mm, op, P, PT, READ, RO_IDEM, S, SA } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ);

export const tool: ToolSpec = {
  name: "find_elements",
  title: "Find elements",
  description: "Find elements by category, level, type, view, selection, parameter values, box or link. Returns the exact total, compact rows and a handle r# for from= in later calls. count_only/group_by for fast counts.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["core", "full"],
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
    box: mm(PT("Intersects box [x0,y0,x1,y1] (plan) or [x0,y0,z0,x1,y1,z1] mm.")),
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
  ops: {
    "": op("", "category,class,level,type,family,view,from,ids,where,box,link,workset,phase,design_option,hidden,types,fields,group_by,count_only,detail,limit,page,doc", M, [{"category":["Doors"],"level":"Level 1","where":["Width >= 900"]}, {"from":"selection","fields":["Mark","location"]}, {"category":["Walls"],"group_by":"type"}, {"category":["Furniture"],"link":"Interior.rvt","box":[0,0,20000,15000]}], { help: "Exact totals; the handle r# keeps the full id list (up to 100,000) for from= in later calls. Pages re-query rows for id slices, so paging is stable." }),
  },
};
