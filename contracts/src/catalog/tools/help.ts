// Catalog entry for `help` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { BROKER, meta, op, RO_IDEM, S } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(BROKER);

export const tool: ToolSpec = {
  name: "help",
  title: "Help and examples",
  description: "Params and a working example for a tool or op: help {tool:'create_elements',op:'wall'}. topic= units, targeting, selectors, confirm, errors, recipe, run_csharp, workflow:<audit|sheets|family|rooms|visual>, error codes.",
  group: "control",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    tool: S("Tool name."),
    op: S("Op name."),
    topic: S("Topic, or an error code."),
  },
  ops: {
    "": op("", "tool,op,topic", M, [{"tool":"create_elements","op":"wall"}, {"topic":"units"}, {}], { help: "help {} lists tools and topics; help {tool} lists ops; help {tool,op} gives params and a working example; help {topic:'<CODE>'} explains an error code." }),
  },
};
