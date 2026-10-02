// Catalog entry for `describe_elements` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { E, EA, meta, op, P, READ, RO_IDEM, S, syn } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ);

export const tool: ToolSpec = {
  name: "describe_elements",
  title: "Describe elements",
  description: "Details for up to 50 elements: parameters with values, units and writability, type parameters, geometry, host/hosted, room, joins, group, MEP connectors; dimensions show witness refs. Use before set_parameters.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    ids: P.ids,
    from: P.from,
    params: syn(E(["writable","all","names","none"], "Instance parameters: writable (default) values, all values, names only, none."), {"writeable":"writable","editable":"writable","values":"all","every":"all"}),
    include: syn(EA(["type","geometry","relations","connectors"], "Extra blocks. type = type parameters."), {"types":"type","typeparams":"type","type_params":"type","geom":"geometry","relation":"relations","connector":"connectors"}),
    match: S("Only parameters whose name contains this."),
    link: S("Elements are inside this linked model."),
    page: P.page,
    doc: P.doc,
  },
  ops: {
    "": op("ids/from", "params,include,match,link,page,doc", M, [{"ids":["304512"],"include":["type","relations"]}, {"from":"r3","params":"all","match":"Fire"}], { help: "At most 50 elements per page. Dimension elements carry stable refs for annotate op=dimension refs=." }),
  },
};
