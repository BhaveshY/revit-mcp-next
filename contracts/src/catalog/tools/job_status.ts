// Catalog entry for `job_status` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { BROKER, I, meta, op, RO_IDEM, S, sec } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(BROKER);

export const tool: ToolSpec = {
  name: "job_status",
  title: "Job status",
  description: "State, progress and result of a job (j#: export, sync, open, delivery, long scan) or of a write whose outcome was unclear (w#). wait= seconds to wait, 0-45, default 20. No id: your recent jobs.",
  group: "control",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    id: S("Job id j# or write id w#."),
    wait: sec(I("Seconds to wait for completion, 0-45. Default 20.")),
  },
  ops: {
    "": op("", "id,wait", M, [{"id":"j2","wait":30}, {}], { help: "Waits up to wait seconds (default 20). w# ids report write outcomes from the add-in ledger." }),
  },
};
