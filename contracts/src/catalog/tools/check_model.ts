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
    stats: op("", "group_by,detail,limit,page,doc", M, [{"check":"stats","group_by":"level"}, {"check":"stats"}], { help: "Model element counts per category (or level, type, family, workset) plus totals: types, families, in-place families, views, sheets, rooms, groups, links, imports, warnings, file size.", api: "FilteredElementCollector", errors: ["PROJECT_DOC_REQUIRED"] }),
    warnings: op("", "match,severity,ids,detail,limit,page,doc", M, [{"check":"warnings"}, {"check":"warnings","severity":"error","match":"overlap"}, {"check":"warnings","ids":["304512"]}], { help: "Revit warnings grouped by message (errors first) with up to 20 element ids each; detail:full lists every warning. ids= keeps warnings involving those elements.", api: "Document.GetWarnings", errors: ["INVALID_ARGS", "PROJECT_DOC_REQUIRED"] }),
    readiness: op("", "scenarios,detail,doc", M, [{"check":"readiness","scenarios":["walls","rooms","sheets"]}, {"check":"readiness","scenarios":["all"]}], { help: "Per scenario: ready, missing prerequisites, counts and the next call. Scenarios: levels, grids, walls, floors, roofs, ceilings, rooms, families, doors, windows, views, sheets, tags, dimensions, text, schedules, mep, structure, worksharing, links, or all. Default walls, floors, rooms, families, sheets, tags.", errors: ["INVALID_ARGS", "PROJECT_DOC_REQUIRED"] }),
    purgeable: op("", "detail,limit,page,doc", meta(M, { job: "auto" }), [{"check":"purgeable"}, {"check":"purgeable","limit":200}], { help: "One purge pass (unused families, types, materials, patterns, styles) that Revit allows to delete; purging can free further nested items. Nothing is deleted: manage_document op=purge does that.", api: "Document.GetUnusedElements, DocumentValidation.CanDeleteElement", errors: ["REVIT_REFUSED", "READ_STILL_RUNNING", "PROJECT_DOC_REQUIRED"] }),
    clashes: op("a", "b,link,tolerance,detail,limit,page,doc", meta(M, { job: "auto" }), [{"check":"clashes","a":["Ducts"],"b":["Structural Framing"],"tolerance":10}, {"check":"clashes","a":["Pipes"],"b":["Walls"],"link":"Architecture.rvt"}], { help: "Exact element pairs whose solids intersect: depth = penetration (mm), volume m3, point = centre of the overlap. a/b take categories or an r# handle; b defaults to a (each pair once). link= puts set b inside a linked model (its ids are link ids). tolerance drops overlaps shallower than it. Long checks return partial with a more: page that continues.", api: "ElementIntersectsSolidFilter, BooleanOperationsUtils", errors: ["NOT_FOUND", "INVALID_ARGS", "HANDLE_EXPIRED", "READ_STILL_RUNNING", "PROJECT_DOC_REQUIRED"] }),
  },
};
