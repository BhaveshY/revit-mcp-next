// Catalog entry for `status` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { BROKER, DETAIL_SYN, E, EA, meta, op, P, RO_IDEM, S, syn } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(BROKER);

export const tool: ToolSpec = {
  name: "status",
  title: "Revit status",
  description: "Start here. Lists running Revit versions and open docs (#1, #2...), the pinned target doc, active view, selection count, levels and busy/dialog state. include= adds sections; detail=full adds health.",
  group: "session",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    include: syn(EA(["views","selection","view_elements","readiness","context","warnings","writes"], "Extra sections for the target doc (writes = recent writes by any session)."), {"view":"views","elements":"view_elements","warning":"warnings","write":"writes","recentwrites":"writes","selected":"selection"}),
    detail: syn(E(["compact","full"], "full adds versions, paths, queue and pipe health."), DETAIL_SYN),
    instance: S("Only this Revit: year (2024) or process id."),
    doc: P.doc,
  },
  ops: {
    "": op("", "include,detail,instance,doc", M, [{}, {"include":["selection","warnings"]}, {"detail":"full"}], { help: "Served from control snapshots and health only; it never waits for Revit's UI thread. include= adds sections from other reads (shared 10 s budget)." }),
  },
};
