// Catalog entry for `get_quantities` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, meta, op, P, READ, RO_IDEM, S, syn } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ, { scope: "project" });

export const tool: ToolSpec = {
  name: "get_quantities",
  title: "Get quantities",
  description: "Quantity takeoff: count, length (m), area (m2), volume (m3) summed by material, type, level, category or family, for all or filtered elements. Default by=material.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["full"],
  properties: {
    by: syn(E(["material","type","level","category","family"], "Group rows by. Default material."), {"materials":"material","types":"type","levels":"level","categories":"category","families":"family"}),
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
  ops: {
    "": op("", "by,category,level,view,from,material,paint,limit,page,doc", M, [{"by":"type","category":["Walls","Floors"]}, {"by":"material","category":["Walls"],"level":"Level 1"}]),
  },
};
