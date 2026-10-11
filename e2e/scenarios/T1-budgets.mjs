// T1 output budgets, offline part (SPEC §15, §13.3 T1, D4 §8.9; owner W3-E2E). No Revit.
//   1. e2e/budgets.json is generated from contracts/src/catalog/checks.ts (outputs and t1 equal GATES).
//   2. Every help answer (index, 38 tools, 243 tool+op) and every INVALID_ARGS answer stays inside the result budget;
//      errors stay under 2 KB (a weak model reads the whole error).
// The run-level t1 gate checks every call of every scenario against the absolute budgets (status compact 2 KB, pages
// 8 KB, results 12 KB / 40 KB with detail:"full", image base64 1.5 MB, read_many ≤ 2 images / 2 MB) and the per-key p95
// growth over e2e/t1-baseline.json (> 20 % fails).

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { byName, REGISTRY } from "@revit-mcp-next/contracts/catalog";
import { deepEqual, GATES } from "@revit-mcp-next/contracts/catalog/checks";
import { makeCaller } from "../lib/call.mjs";

const ERROR_MAX_BYTES = 2_048;

export default {
  id: "T1",
  title: "Output budgets (offline): budgets.json generated from GATES; help and error sizes",
  profiles: ["surface", "quick", "full"],
  years: "none",
  async run(t) {
    await t.step("e2e/budgets.json outputs and t1 equal GATES", () => {
      const budgets = JSON.parse(readFileSync(join(t.repo, "e2e", "budgets.json"), "utf8"));
      t.assert(deepEqual(budgets.outputs, GATES.outputs), "budgets.json outputs = GATES.outputs (npm run gen:catalog -- --update-baseline)");
      t.assert(deepEqual(budgets.t1, GATES.t1), "budgets.json t1 = GATES.t1");
      t.assertEq(budgets.gates?.perToolMaxBytes, GATES.perToolMaxBytes, "budgets.json gates");
    });

    const home = join(t.runRoot, "t1");
    mkdirSync(join(home, "config"), { recursive: true });
    writeFileSync(join(home, "config", "settings.json"), JSON.stringify({ enableCodeExecution: true }));
    const session = await t.connect({ home });
    const call = makeCaller({ session });
    const failures = [];
    const sizes = { help: 0, error: 0 };
    try {
      await t.step("help answers stay inside the result budget", async () => {
        const index = await call("help", {});
        if (index.bytes > GATES.outputs.resultMaxBytes) failures.push(`help {} is ${index.bytes} B`);
        for (const name of byName.keys()) {
          const r = await call("help", { tool: name });
          sizes.help = Math.max(sizes.help, r.bytes);
          if (r.bytes > GATES.outputs.resultMaxBytes) failures.push(`help ${name} is ${r.bytes} B > ${GATES.outputs.resultMaxBytes}`);
        }
        for (const r of REGISTRY) {
          const res = await call("help", r.op ? { tool: r.tool, op: r.op } : { tool: r.tool });
          sizes.help = Math.max(sizes.help, res.bytes);
          if (res.bytes > GATES.outputs.resultMaxBytes) failures.push(`help ${r.key} is ${res.bytes} B`);
        }
        t.assert(failures.length === 0, failures.join("\n"));
      });

      await t.step("INVALID_ARGS answers stay under 2 KB", async () => {
        for (const r of REGISTRY) {
          const spec = byName.get(r.tool);
          if (!spec.ops[r.op ?? ""]?.req.trim()) continue;
          const res = await call(r.tool, spec.discriminator && r.op ? { [spec.discriminator]: r.op } : {}, { allowError: true });
          sizes.error = Math.max(sizes.error, res.bytes);
          if (res.bytes > ERROR_MAX_BYTES) failures.push(`${r.key} INVALID_ARGS is ${res.bytes} B > ${ERROR_MAX_BYTES}`);
        }
        t.assert(failures.length === 0, failures.join("\n"));
      });
    } finally {
      await session.close();
    }
    t.log(`largest help ${sizes.help} B, largest INVALID_ARGS ${sizes.error} B`);
  },
};
