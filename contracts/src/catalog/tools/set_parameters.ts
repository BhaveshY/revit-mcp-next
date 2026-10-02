// Catalog entry for `set_parameters` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { E, meta, O, OA, op, P, SA, syn, W, WRITE, WRITE_TAIL } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { scope: "project_or_family", blast: ["bulk"] });

export const tool: ToolSpec = {
  name: "set_parameters",
  title: "Set parameters",
  description: "Set parameter values on elements, their types, views, sheets or project info: values={Mark:'D1',Width:900} for every target or rows=[{id,values}] each. Lengths mm, areas m2, angles deg; '{Param}' copies values.",
  group: "write",
  annotations: W,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    ids: SA("Element, type, view or sheet ids; ['project_info'] for project info."),
    from: P.from,
    filter: P.filter,
    values: O("{param name: value}. Text may use '{Other Param}' and '{n}' (1,2,3...)."),
    rows: OA("Per element: [{id, values:{...}}]."),
    on: syn(E(["instance","type"], "type = set on the targets' types. Default instance."), {"types":"type","instances":"instance","element":"instance"}),
    ...WRITE_TAIL,
  },
  ops: {
    "": op("ids+values/from+values/filter+values/rows", "on,doc,preview,confirm,units", M, [{"from":"r3","values":{"Fire Rating":"EI30"}}, {"filter":{"category":["Doors"],"level":"Level 1"},"values":{"Mark":"{Level}-D{n}"}}, {"ids":["project_info"],"values":{"Project Number":"2026-017"}}, {"ids":["304512"],"on":"type","values":{"Width":1000}}, {"rows":[{"id":"304512","values":{"Mark":"D1"}},{"id":"304513","values":{"Mark":"D2"}}]}], { help: "Bare numbers on lengths are mm (never feet). English and localized parameter names both resolve. '{Param}' copies values; '{n}' counts 1,2,3." }),
  },
};
