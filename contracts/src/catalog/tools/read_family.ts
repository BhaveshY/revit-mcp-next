// Catalog entry for `read_family` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { meta, op, P, READ, RO_IDEM, S, SA } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ, { scope: "project_or_family", idle: false, job: "auto" });

export const tool: ToolSpec = {
  name: "read_family",
  title: "Read family",
  description: "Family parameters (name, type/instance, group, data type, formula, shared), family types with per-type values, category and nested families. family= loaded family, path= an .rfa file, or doc= an open family doc.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["full"],
  properties: {
    family: S("Loaded family name or id (read without opening the UI)."),
    path: S("Path to an .rfa file (opened hidden, read-only)."),
    types: SA("Only these family types' values. Default all (up to 30)."),
    detail: P.detail,
    doc: P.doc,
  },
  ops: {
    "": op("", "family,path,types,detail,doc", M, [{"family":"Single-Flush"}, {"path":"C:\\Families\\Door-Double.rfa"}]),
  },
};
