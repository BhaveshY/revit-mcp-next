// Catalog entry for `read_schedule` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, I, meta, op, P, READ, RO_IDEM, S } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ, { scope: "project", tx: "temp" });

export const tool: ToolSpec = {
  name: "read_schedule",
  title: "Read schedule",
  description: "Read a schedule as a table (paged) with its fields, filters and sorting. category= without schedule= lists the fields available for a new schedule of that category.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["full"],
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
  ops: {
    "": op("schedule/category", "rows,available,name,key,limit,page,doc", M, [{"schedule":"Door Schedule","limit":200}, {"category":"Doors","available":true,"rows":false}], { help: "Rows come from the schedule cells. An id column appears only when the key field (default Mark) maps rows 1:1 onto elements." }),
  },
};
