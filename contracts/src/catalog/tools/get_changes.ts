// Catalog entry for `get_changes` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { meta, op, P, READ, RO_IDEM, S } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ);

export const tool: ToolSpec = {
  name: "get_changes",
  title: "Get changes",
  description: "What changed since a point: added, modified and deleted elements (id, category) and transaction names, by you or the user. since='session' (default), 'last' (your last write) or a mark m#.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["full"],
  properties: {
    since: S("'session', 'last', or a mark like m12 from an earlier result."),
    limit: P.limit,
    doc: P.doc,
  },
  ops: {
    "": op("", "since,limit,doc", M, [{"since":"last"}, {"since":"session"}]),
  },
};
