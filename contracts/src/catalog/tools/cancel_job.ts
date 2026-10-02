// Catalog entry for `cancel_job` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { BROKER, meta, op, S, W_IDEM } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(BROKER);

export const tool: ToolSpec = {
  name: "cancel_job",
  title: "Cancel job",
  description: "Cancel a queued or running job or queued request. A running Revit step finishes or rolls back safely; partial exports and delivery staging are removed.",
  group: "control",
  annotations: W_IDEM,
  discriminator: null,
  profiles: ["full"],
  properties: {
    id: S("Job id j# or request id w#."),
    reason: S("Why (logged)."),
  },
  required: ["id"],
  ops: {
    "": op("id", "reason", M, [{"id":"j2"}]),
  },
};
