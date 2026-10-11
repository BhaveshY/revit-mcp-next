// S99 in-process bridge (SPEC §9.3, §13.4, §16, C37). Owner P-REL-ADDIN.
// test.inproc_call drives RevitMcpInProcessBridge on the UI thread like pyRevit/Dynamo would: reads with inproc:true
// work; destructive, lifecycle and code keys are refused with UNSUPPORTED_OP (reason inproc) before anything resolves;
// control ops answer; press is refused (it needs the user's OK through the broker).

import { boundKeys, rawAddin, requireTestOps } from "./R5-busy.mjs";

async function inproc(t, addin, key, args = {}) {
  const r = await addin.call("test.inproc_call", { key, args }, { timeoutMs: 20_000 });
  t.assert(r.ok, `test.inproc_call ${key}: ${r.code ?? ""} ${r.message ?? ""}`);
  return r.data.response;
}

export default {
  id: "S99",
  title: "In-process bridge: reads work, destructive/lifecycle/code keys are refused",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const addin = rawAddin(t);
    await requireTestOps(t, addin);
    const { path } = await t.newProject("S99");
    t.setTargetPath(path);
    const keys = await boundKeys(addin);

    await t.step("dev.ping through the bridge runs in process", async () => {
      const r = await inproc(t, addin, "dev.ping");
      t.assert(r.ok, `dev.ping: ${r.code ?? ""}`);
      t.assertEq(r.data?.via, "inProcess", "via");
      t.assertEq(r.metrics?.via, "inProcess", "metrics.via");
    });

    await t.step("a catalog read (find_elements count) through the bridge", async () => {
      const r = await inproc(t, addin, "find_elements", { category: ["Walls"], count_only: true });
      if (keys.has("find_elements")) {
        t.assert(r.ok, `find_elements in process: ${r.code ?? ""} ${r.message ?? ""}`);
      } else {
        t.assertEq(r.code, "ADDIN_OUTDATED", "find_elements is not bound in this build (P-READ)");
        t.log("find_elements is not bound yet; the bridge reported ADDIN_OUTDATED");
      }
    });

    for (const key of ["modify_elements.delete", "manage_document.save", "run_csharp", "export.pdf"]) {
      await t.step(`${key} is refused with UNSUPPORTED_OP (reason inproc)`, async () => {
        const r = await inproc(t, addin, key, { ids: [1] });
        t.assert(!r.ok, `${key} must not run in process`);
        t.assertEq(r.code, "UNSUPPORTED_OP", "code");
        t.assertEq(r.details?.reason, "inproc", "details.reason");
      });
    }

    await t.step("control ops answer through the bridge; press is refused", async () => {
      const h = await inproc(t, addin, "health");
      t.assert(h.ok && h.data?.queue, "health in process");
      const p = await inproc(t, addin, "press", { button: "Cancel" });
      t.assertEq(p.code, "UNSUPPORTED_OP", "press is refused in process");
    });

    await t.step("an unknown key gives UNKNOWN_OP with suggestions", async () => {
      const r = await inproc(t, addin, "find_elementz");
      t.assertEq(r.code, "UNKNOWN_OP", "code");
    });
  },
};
