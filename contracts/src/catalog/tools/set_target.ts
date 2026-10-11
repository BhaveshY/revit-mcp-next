// Catalog entry for `set_target` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { BROKER, meta, op, S, W_IDEM } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(BROKER);

export const tool: ToolSpec = {
  name: "set_target",
  title: "Pin target document",
  description: "Pin the doc that every call uses this session: 'active', a # from status, a title or a path. 'follow' tracks the active tab; 'none' unpins. Family docs work too; links are read with find_elements link=.",
  group: "session",
  annotations: W_IDEM,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    doc: S("'active', 'follow', 'none', #, title or path."),
    instance: S("Revit year or process id when the same file is open twice."),
  },
  required: ["doc"],
  ops: {
    "": op("doc", "instance", M, [{"doc":"#2"}, {"doc":"active"}, {"doc":"Tower_A@2027"}, {"doc":"follow"}, {"doc":"none"}, {"doc":"Tower_A","instance":"2027"}, {"doc":"@2027"}], {
      help: "Pins the doc for this session (notice TARGET_NOW). Grammar: #n or n, 'active', 'follow', 'none', exact title (extension optional), unique title prefix, absolute path, title@2027, or @2027 (the active doc of Revit 2027). The pin follows Save As and a Revit restart (REBOUND). Calls arriving while set_target runs wait for it.",
      errors: ["DOC_NOT_OPEN", "TARGET_AMBIGUOUS", "NO_OPEN_DOCUMENT", "NO_REVIT_RUNNING"],
    }),
  },
};
