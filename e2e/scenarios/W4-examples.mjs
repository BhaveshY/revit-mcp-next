// W4 help examples, offline part (SPEC §4.1 examples, §13.3 W4, D4 §8.9; owner W3-E2E). No Revit.
// A weak model copies examples from help {tool, op}; every example must pass validation as it stands:
//   1. The broker's own normalize → resolveOp → validateOp accepts every example of every op with no error and no
//      PARAM_RENAMED / PARAM_IGNORED / VALUE_CLAMPED warning.
//   2. Sent through MCP with preview:true (writes) every example reaches the broker's Revit check (NO_REVIT_RUNNING),
//      or a broker-only result, but never INVALID_ARGS or UNKNOWN_OP.
// W4R sends the same examples to a real Revit with preview:true, where the add-in validates them too.

import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { byName, CATALOG, fullProperties } from "@revit-mcp-next/contracts/catalog";
import { makeCaller } from "../lib/call.mjs";
import { brokerValidation } from "../lib/hints.mjs";

/** Broker-only ops with side effects in the isolated home (fine), or that need no Revit at all. */
const SKIP_MCP = new Set(["cancel_job", "job_status"]);

export default {
  id: "W4",
  title: "Help examples (offline): every example validates in the broker and over MCP with preview",
  profiles: ["surface", "quick", "full"],
  years: "none",
  async run(t) {
    const v = await brokerValidation();
    t.assert(v, "broker/dist is built (npm run build)");
    const failures = [];
    let examples = 0;

    await t.step("normalize + validate accept every example with no warnings", () => {
      for (const tool of CATALOG) {
        for (const [op, spec] of Object.entries(tool.ops)) {
          for (const example of spec.examples) {
            examples += 1;
            const label = `${tool.name}${op ? "." + op : ""} ${JSON.stringify(example).slice(0, 140)}`;
            const norm = v.normalizeArgs(tool, structuredClone(example));
            if (norm.error) {
              failures.push(`${label}: ${norm.error.message}`);
              continue;
            }
            if (norm.warnings.length) failures.push(`${label}: normalize warns ${norm.warnings.join("; ")}`);
            const resolved = v.resolveOp(tool, norm.args);
            if (resolved.error) {
              failures.push(`${label}: ${resolved.error.code} ${resolved.error.message}`);
              continue;
            }
            // A confirm call replays the stored plan: the broker skips the required-param check for it.
            const val = v.validateOp(tool, resolved.op, norm.args, { skipRequired: "confirm" in example });
            if (val.error) failures.push(`${label}: ${val.error.message}`);
            else if (val.warnings.length) failures.push(`${label}: validate warns ${val.warnings.join("; ")}`);
          }
        }
      }
      t.assert(failures.length === 0, `${failures.length} example(s) do not validate:\n${failures.slice(0, 25).join("\n")}`);
      t.log(`${examples} examples validate`);
    });

    const home = join(t.runRoot, "w4");
    mkdirSync(join(home, "config"), { recursive: true });
    writeFileSync(join(home, "config", "settings.json"), JSON.stringify({ enableCodeExecution: true }));
    const session = await t.connect({ home });
    const call = makeCaller({ session });
    try {
      await t.step("every example over MCP (writes with preview:true) never fails validation", async () => {
        let sent = 0;
        for (const tool of CATALOG) {
          if (SKIP_MCP.has(tool.name)) continue;
          const hasPreview = "preview" in fullProperties(tool);
          for (const [op, spec] of Object.entries(tool.ops)) {
            for (const example of spec.examples) {
              const args = hasPreview && !tool.annotations.readOnlyHint && !("confirm" in example) ? { ...example, preview: true } : { ...example };
              const r = await call(tool.name, args, { allowError: true });
              sent += 1;
              if (r.status === "error" && (r.code === "INVALID_ARGS" || r.code === "UNKNOWN_OP")) failures.push(`${tool.name}${op ? "." + op : ""}: ${r.code} ${r.summary.slice(0, 200)}`);
              if (r.warnings.some((w) => /^(PARAM_IGNORED|PARAM_RENAMED|VALUE_CLAMPED)/.test(w))) failures.push(`${tool.name}${op ? "." + op : ""}: warns ${r.warnings.join("; ").slice(0, 200)}`);
            }
          }
        }
        t.assert(failures.length === 0, `${failures.length} example(s) fail over MCP:\n${failures.slice(0, 25).join("\n")}`);
        t.log(`${sent} examples sent over MCP`);
      });
    } finally {
      await session.close();
    }
    void byName;
  },
};
