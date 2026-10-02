// Catalog entry for `undo` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, I, meta, op, P, syn, WD, WRITE } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { scope: "project_or_family", ui: true, idle: false, tx: "none", inproc: false, cs: false });

export const tool: ToolSpec = {
  name: "undo",
  title: "Undo",
  description: "Undo your last write(s) in the target doc while they are still Revit's newest changes (steps 1-10, default 1); redo=true redoes. If the user edited since, explains how to revert instead.",
  group: "control",
  annotations: WD,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    steps: I("1-10. Default 1."),
    redo: B("Redo instead."),
    mode: syn(E(["auto","compensate"], "auto (default): Revit undo; compensate: apply the inverse change as a new write."), {"revit":"auto","inverse":"compensate"}),
    doc: P.doc,
  },
  ops: {
    "": op("", "steps,redo,mode,doc", M, [{}, {"steps":2}, {"redo":true}], { help: "Only your own newest writes; the doc must be UI-active. A newer change by someone else gives UNDO_BLOCKED." }),
  },
};
