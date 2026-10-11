// Catalog entry for `get_view` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { EA, meta, op, P, READ, RO_IDEM, S, syn } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ);

export const tool: ToolSpec = {
  name: "get_view",
  title: "Get view details",
  description: "One view or sheet: type, scale, detail level, template, crop, view range, phase, discipline; include= overrides, filters, hidden categories, viewports, revisions. Default: the active view.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    view: S("View name, id, sheet number, or 'active' (default)."),
    include: syn(EA(["overrides","filters","categories","viewports","revisions"], "Extra blocks."), {"override":"overrides","filter":"filters","category":"categories","hidden":"categories","viewport":"viewports","revision":"revisions"}),
    doc: P.doc,
  },
  ops: {
    "": op("", "view,include,doc", M, [{}, {"view":"A101","include":["viewports"]}, {"view":"Level 1","include":["overrides","filters","categories"]}, {"view":"A101","include":["revisions"]}], {
      help: "template_controls lists the settings the view template locks (edit them on the template). view_range is in mm above the view level. include=overrides scans up to 5,000 elements of the view.",
      errors: ["NOT_FOUND", "AMBIGUOUS_NAME"],
    }),
  },
};
