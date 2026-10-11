// R4 kill and relaunch (D4 §8.8 R4, D2 §10.5 "Revit crash" / "Revit restarted", E6). The same MCP session (same broker
// pid) is pinned to R4A while the harness kills its own Revit (taskkill /PID <pid> /F, never by name) and relaunches it.
//   1. The next call fails in <= 3 s with a REVIT_EXITED-class code that names R4A and carries a fix:.
//   2. Relaunch "Revit.exe R4A.rvt": the first call after registration succeeds in <= 2 s, names R4A and carries the
//      REBOUND notice; no set_target was needed (documentKey rebind).
//   3. Relaunch without the file: the error guides to manage_document open with the path; after the open, R4A is the
//      target again.
// The Revit relaunched last stays running and replaces the harness entry, so the runner closes it at the end.
// The "kill during a long write" variant is D2 E7 in R13.

import { existsSync } from "node:fs";
import { join } from "node:path";
import { killOwned, launchRevit, processAlive, waitReady } from "../lib/revit.mjs";
import { bindSlot } from "../lib/slots.mjs";

const title = (r) => (r.doc?.title ?? "").replace(/\.(rvt|rfa|rte)$/i, "");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitGone(pid, ms = 15_000) {
  const end = Date.now() + ms;
  while (processAlive(pid)) {
    if (Date.now() > end) return false;
    await sleep(100);
  }
  return true;
}

/** Kill the harness Revit of this year; false (with a log line) when this harness may not kill it. */
async function killHarnessRevit(t) {
  const pid = t.revit?.pid;
  try {
    killOwned(pid);
  } catch (error) {
    t.log(`SKIP: ${error.message}`);
    return false;
  }
  t.assert(await waitGone(pid), `Revit pid ${pid} exited after taskkill`);
  return true;
}

/** Launch Revit (optionally with a file) for this home and make it the harness entry for this year. */
async function relaunch(t, args) {
  const launched = await launchRevit({ year: t.year, home: t.home, args });
  t.revit.pid = launched.pid;
  t.revit.launched = true;
  if (t.revit.slot) bindSlot(t.revit.slot, launched.pid);
  const ready = await waitReady({ year: t.year, pid: launched.pid, home: t.home, startedAt: launched.startedAt, call: t.call });
  t.log(`Revit ${t.year} relaunched as pid ${launched.pid} in ${Math.round(ready.readyMs / 1000)} s`);
  return launched;
}

export default {
  id: "R4",
  title: "Kill and relaunch Revit: REVIT_EXITED-class error, automatic rebind, open after a bare relaunch",
  profiles: ["full"],
  years: "one",
  async run(t) {
    const path = join(t.yearDir, "R4A.rvt");
    const brokerPid = t.session.pid;

    await t.step("R4A exists on disk and is the explicit target", async () => {
      await t.call("manage_document", { op: "new_project", path, template: t.projectTemplate(), activate: true, doc: `@${t.year}` }, { timeoutMs: 120_000 });
      t.setTargetPath(path);
      if (!existsSync(path)) await t.call("manage_document", { op: "save" }, { timeoutMs: 120_000 });
      t.assert(existsSync(path), "R4A.rvt was written");
      const pin = await t.call("set_target", { doc: "R4A" });
      t.assertEq(title(pin), "R4A", "pinned R4A");
      await t.call("list", { kind: "levels" });
    });

    if (!(await killHarnessRevit(t))) return;

    await t.step("the next call fails fast with a REVIT_EXITED-class error naming R4A", async () => {
      const r = await t.call("list", { kind: "levels" }, { allowError: true });
      t.assertEq(r.status, "error", "the call fails while Revit is gone");
      t.assert(["NO_REVIT_RUNNING", "REVIT_EXITED", "DOC_NOT_OPEN", "TARGET_CLOSED"].includes(r.code), `REVIT_EXITED-class code, got ${r.code}`);
      t.assertLe(r.ms, 3_000, "error latency ms");
      t.assertMatch(`${r.summary} ${r.fix}`, /R4A/, "the error names R4A");
      t.assert(!!r.fix, "fix present");
      t.assertEq(t.session.pid, brokerPid, "same broker process (same MCP session)");
    });

    await t.step("relaunch with R4A: the first call rebinds automatically (REBOUND) within 2 s", async () => {
      await relaunch(t, [path]);
      const r = await t.call("list", { kind: "levels" });
      t.assertEq(title(r), "R4A", "the call targets R4A again");
      t.assert(r.notices.some((n) => n.startsWith("REBOUND")), `REBOUND notice: ${r.notices.join(" | ")}`);
      t.assertLe(r.ms, 2_000, "first call after the relaunch ms");
    });

    if (!(await killHarnessRevit(t))) return;

    await t.step("relaunch without the file: the error guides to manage_document open; the open re-targets R4A", async () => {
      await relaunch(t, []);
      const r = await t.call("list", { kind: "levels" }, { allowError: true });
      t.assertEq(r.status, "error", "no silent retarget while R4A is not open");
      t.assert(["DOC_NOT_OPEN", "NO_OPEN_DOCUMENT", "TARGET_CLOSED"].includes(r.code), `DOC_NOT_OPEN-class code, got ${r.code}`);
      t.assertMatch(r.fix ?? "", /manage_document/, "fix opens the document");
      const open = await t.call("manage_document", { op: "open", path, activate: true }, { timeoutMs: 120_000 });
      t.assertEq(title(open), "R4A", "open names R4A");
      const again = await t.call("list", { kind: "levels" });
      t.assertEq(title(again), "R4A", "R4A is the target again");
    });
  },
};
