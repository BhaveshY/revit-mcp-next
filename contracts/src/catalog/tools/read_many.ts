// Catalog entry for `read_many` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { BROKER, meta, op, RO_IDEM } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(BROKER, { kind: "read" });

export const tool: ToolSpec = {
  name: "read_many",
  title: "Run several reads",
  description: "Run up to 8 read calls in one round trip, sharing one time budget (max 2 images): calls=[{tool:'list',args:{kind:'levels'}},{tool:'capture',args:{}}]. One failure does not stop the rest.",
  group: "read",
  annotations: RO_IDEM,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    calls: {"type":"array","description":"Up to 8 {tool, args}; args as for that tool.","items":{"type":"object","properties":{"tool":{"type":"string","enum":["status","list","find_elements","describe_elements","get_view","read_schedule","read_family","check_model","get_quantities","get_changes","capture","help"],"description":"A read tool."},"args":{"type":"object","description":"That tool's args."}},"required":["tool"]}},
  },
  required: ["calls"],
  ops: {
    "": op("calls", "", M, [{"calls":[{"tool":"list","args":{"kind":"levels"}},{"tool":"capture","args":{"size":"small"}}]}], { help: "Runs up to 8 reads with one shared time budget; sub-call i gets remaining/(n-i). At most 2 images (2 MB)." }),
  },
};
