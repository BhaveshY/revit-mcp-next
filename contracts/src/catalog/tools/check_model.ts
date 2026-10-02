// Catalog entry for `check_model` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { E, meta, mm, N, op, P, READ, RO_IDEM, S, SA, syn } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ, { scope: "project" });

export const tool: ToolSpec = {
  name: "check_model",
  title: "Check model",
  description: "Model health: stats (counts by category/level), warnings (grouped, element ids), readiness for common tasks, purgeable (unused items), clashes (set a vs b, links ok). Default check=stats.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: "check",
  defaultOp: "stats",
  profiles: ["full"],
  properties: {
    check: syn(E(["stats","warnings","readiness","purgeable","clashes"], "What to check. Default stats."), {"stat":"stats","statistics":"stats","summary":"stats","warning":"warnings","errors":"warnings","ready":"readiness","purge":"purgeable","unused":"purgeable","clash":"clashes","collisions":"clashes"}),
    a: SA("Clashes: categories or an r# handle."),
    b: SA("Clashes: categories or an r# handle. Default = a."),
    link: S("Clashes: set b is inside this linked model."),
    tolerance: mm(N("Clashes: ignore overlaps smaller than this, mm. Default 0.")),
    match: S("Warnings: text contains."),
    severity: syn(E(["warning","error"], "Warnings: only this severity."), {"warnings":"warning","errors":"error"}),
    ids: SA("Warnings involving these elements."),
    group_by: S("Stats: category, level, type, workset."),
    scenarios: SA("Readiness: walls, floors, rooms, families, sheets, tags... Default common set."),
    detail: P.detail,
    limit: P.limit,
    page: P.page,
    doc: P.doc,
  },
  ops: {
    stats: op("", "group_by,detail,limit,page,doc", M, [{"check":"stats","group_by":"level"}]),
    warnings: op("", "match,severity,ids,detail,limit,page,doc", M, [{"check":"warnings"}, {"check":"warnings","severity":"error","match":"overlap"}]),
    readiness: op("", "scenarios,detail,doc", M, [{"check":"readiness","scenarios":["walls","rooms","sheets"]}]),
    purgeable: op("", "detail,limit,page,doc", meta(M, { job: "auto" }), [{"check":"purgeable"}]),
    clashes: op("a", "b,link,tolerance,detail,limit,page,doc", meta(M, { job: "auto" }), [{"check":"clashes","a":["Ducts"],"b":["Structural Framing"],"tolerance":10}]),
  },
};
