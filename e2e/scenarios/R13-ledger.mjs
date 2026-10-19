// R13 write ledger and budgets (SPEC §8.10, §13.3; D2 §13, E7, E11, E15).
//   E11  A broker killed while its write is in flight: a new broker reconciles the dead broker's in-flight journal
//        through the add-in ledger and shows RECOVERED_WRITE once (committed / ended / no recorded outcome).
//   E15  read_many with slow sub-calls (the UI thread blocked by the test op test.sleep_ui) answers within 55 s; every
//        sub-call is REVIT_BUSY or marked "unavailable: BUDGET", nothing hangs. Needs P-REL-ADDIN's test ops
//        (<home>\config\e2e-test-ops.enable, dev-install -EnableTestOps); skipped with a note otherwise.
//   E7   Revit killed during a long write: the call ends with WRITE_OUTCOME_UNKNOWN / REVIT_EXITED (or a recovered
//        result), never a silent retry, and job_status on its w# gives guidance. Revit is relaunched afterwards so the
//        run can continue (only a Revit this harness started is killed).

import { existsSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { BRIDGE_PROTOCOL_VERSION } from "@revit-mcp-next/contracts/protocol";
import { makeCaller } from "../lib/call.mjs";
import { makeWriteGuard } from "../lib/fixtures.mjs";
import { killOwned, launchRevit, processAlive, waitReady } from "../lib/revit.mjs";
import { bindSlot } from "../lib/slots.mjs";

const title = (r) => (r.doc?.title ?? "").replace(/\.(rvt|rfa|rte)$/i, "");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let elevationSeq = 0;

async function openSession(t, o = {}) {
  const session = await t.connect(o);
  const call = makeCaller({ session, coverage: t.coverage, year: t.year, writeGuard: makeWriteGuard(t.runRoot, () => null) });
  call.expectError = (tool, args, codes, options = {}) => call(tool, args, { ...options, expectError: codes });
  return { session, call };
}

/** n level ops for one change_set (slow enough to be in flight for a moment). */
function levelOps(n, tag) {
  const ops = [];
  for (let i = 0; i < n; i++) {
    elevationSeq += 1;
    ops.push({ tool: "create_elements", op: "level", elevation: 60_000 + elevationSeq * 300, name: `E2E_${tag}_${Date.now().toString(36)}_${elevationSeq}`, plans: true });
  }
  return ops;
}

/**
 * A raw primary-pipe request with the broker's own transport (test ops are not MCP tools; SPEC §13.4).
 * Resolves with the add-in response, or rejects with the transport error.
 */
async function rawRequest(t, pipe, op, args, timeoutMs) {
  const dist = (p) => import(pathToFileURL(join(t.repo, "broker", "dist", "src", p)).href);
  const [{ openExchange }, { AuthStore }, { ulid }] = await Promise.all([dist("ipc/PipeClient.js"), dist("runtime/auth.js"), dist("runtime/ids.js")]);
  const auth = new AuthStore(t.home).get();
  const request = { v: BRIDGE_PROTOCOL_VERSION, requestId: ulid(), clientKey: `e2e-R13#${process.pid}`, ...(auth ? { auth } : {}), op, kind: "read", timeoutMs, doc: null, args };
  return openExchange(pipe, request).response;
}

async function instanceFacts(t) {
  const full = await t.call("status", { detail: "full", instance: String(t.revit.pid) });
  return (full.json?.instances ?? []).find((i) => i.pid === t.revit.pid) ?? null;
}

export default {
  id: "R13",
  title: "Write ledger: broker killed mid-write, read_many budget under a blocked UI thread, Revit killed mid-write",
  profiles: ["full"],
  years: "one",
  async run(t) {
    const path = join(t.yearDir, "R13A.rvt");
    await t.call("manage_document", { op: "new_project", path, template: t.projectTemplate(), activate: true, doc: `@${t.year}` }, { timeoutMs: 120_000 });
    t.setTargetPath(path);
    if (!existsSync(path)) await t.call("manage_document", { op: "save" }, { timeoutMs: 120_000 });
    t.assert(existsSync(path), "R13A.rvt is on disk (E7 relaunches Revit with it)");
    await t.call("set_target", { doc: "R13A" });

    // ------------------------------------------------------------------ E11
    await t.step("E11: broker killed mid-write -> the next broker shows RECOVERED_WRITE once", async () => {
      const s1 = await openSession(t);
      await s1.call("set_target", { doc: "R13A" });
      const pending = s1.call("change_set", { ops: levelOps(60, "E11"), name: "E2E R13 E11" }, { allowError: true }).catch((error) => ({ lost: error.message }));
      await sleep(600);
      const pid = s1.session.pid;
      process.kill(pid);
      await pending;
      await s1.session.close();
      const end = Date.now() + 5_000;
      while (processAlive(pid) && Date.now() < end) await sleep(50);
      t.assert(!processAlive(pid), "the killed broker is gone");

      const s2 = await openSession(t);
      try {
        let seen = null;
        for (let i = 0; i < 6 && !seen; i++) {
          const r = await s2.call("status", {});
          seen = r.notices.find((n) => n.startsWith("RECOVERED_WRITE")) ?? null;
          if (!seen) await sleep(1_000);
        }
        t.assert(seen, "a RECOVERED_WRITE notice appears on one of the first results of the new broker");
        t.log(seen);
        t.assertMatch(seen, /w\d+ \(change_set/, "the notice names the write and its tool");
        const later = await s2.call("status", {});
        t.assert(!later.notices.some((n) => n.startsWith("RECOVERED_WRITE")), "shown once");
        const recent = await s2.call("job_status", {});
        t.assert(recent.json?.previousSession?.rows?.length >= 1, "job_status {} lists the previous session's write");
      } finally {
        await s2.session.close();
      }
    });

    // ------------------------------------------------------------------ E15
    await t.step("E15: read_many with slow sub-calls stays within 55 s and marks what could not run", async () => {
      const inst = await instanceFacts(t);
      const testOps = inst?.testOps;
      if (!testOps || (Array.isArray(testOps) && !testOps.some((op) => /sleep_ui/.test(String(op))))) {
        t.log("SKIP E15: test ops are not enabled in this home (dev-install -EnableTestOps / config\\e2e-test-ops.enable)");
        return;
      }
      const pipe = inst.pipes?.primary;
      t.assert(pipe, "primary pipe name from status detail:full");
      const blocked = rawRequest(t, pipe, "test.sleep_ui", { ms: 45_000 }, 60_000).catch((error) => ({ ok: false, code: "TRANSPORT", message: error.message }));
      // Wait until the block is executing.
      const end = Date.now() + 10_000;
      for (;;) {
        const facts = await instanceFacts(t);
        if (/test\.sleep_ui/.test(facts?.queue?.executing?.op ?? "")) break;
        if (Date.now() > end) {
          t.log("test.sleep_ui never showed as executing; E15 checks the budget only");
          break;
        }
        await sleep(300);
      }
      const calls = [
        { tool: "list", args: { kind: "levels" } },
        { tool: "list", args: { kind: "views" } },
        { tool: "find_elements", args: { category: ["Walls"] } },
        { tool: "get_view", args: {} },
        { tool: "list", args: { kind: "sheets" } },
        { tool: "check_model", args: { check: "warnings" } },
      ];
      const started = Date.now();
      const r = await t.call("read_many", { calls }, { timeoutMs: 58_000 });
      const ms = Date.now() - started;
      t.assertLe(ms, 55_000, "read_many total ms");
      const blocks = r.blocks.join("\n");
      for (let i = 0; i < calls.length; i++) {
        const block = r.blocks.find((b) => b.startsWith(`[${i}]`)) ?? "";
        t.assert(/REVIT_BUSY|unavailable: BUDGET|BRIDGE_BUSY|REVIT_NOT_RESPONDING|^\[\d+\] \w+: ok/m.test(block), `sub-call ${i} is busy, BUDGET or done: ${block.slice(0, 160)}`);
      }
      t.log(`read_many under a blocked UI thread: ${ms} ms; ${(blocks.match(/unavailable: BUDGET/g) ?? []).length} BUDGET, ${(blocks.match(/REVIT_BUSY/g) ?? []).length} REVIT_BUSY`);
      const done = await blocked;
      t.log(`test.sleep_ui ended: ${done.ok === false ? `${done.code} ${done.message ?? ""}` : "ok"}`);
      const after = await t.call("list", { kind: "levels" });
      t.assertEq(after.status, "ok", "calls work after the block");
    });

    // ------------------------------------------------------------------ E7
    await t.step("E7: Revit killed during a long write -> outcome unknown or recovered, never retried", async () => {
      const pid = t.revit?.pid;
      const pending = t.call("change_set", { ops: levelOps(100, "E7"), name: "E2E R13 E7" }, { allowError: true, timeoutMs: 58_000 });
      // Kill as soon as the write executes (or after 1.5 s).
      const watcher = await openSession(t);
      try {
        const end = Date.now() + 1_500;
        while (Date.now() < end) {
          const full = await watcher.call("status", { detail: "full", instance: String(pid) });
          const inst = (full.json?.instances ?? []).find((i) => i.pid === pid);
          if (inst?.queue?.executing?.op === "change_set") break;
          await sleep(100);
        }
      } finally {
        await watcher.session.close();
      }
      try {
        killOwned(pid);
      } catch (error) {
        t.log(`SKIP E7: ${error.message}`);
        await pending;
        return;
      }
      const r = await pending;
      t.log(`E7 result: ${r.status}${r.code ? ` ${r.code}` : ""}: ${r.summary.slice(0, 200)}`);
      if (r.status === "ok") {
        t.assert(r.warnings.some((w) => w.startsWith("BRIDGE_RESPONSE_RECOVERED")) || r.json?.write, "an ok result is the committed (possibly recovered) write");
      } else {
        t.assert(["WRITE_OUTCOME_UNKNOWN", "REVIT_EXITED", "WRITE_STILL_RUNNING"].includes(r.code), `outcome-unknown class code, got ${r.code}`);
        t.assertMatch(r.fix ?? "", /never repeat|do not repeat|job_status|check|restart/i, "the fix never says to repeat blindly");
        const ref = r.json?.details?.ref ?? r.json?.ref ?? (/\bw\d+\b/.exec(`${r.summary} ${r.fix}`) ?? [])[0];
        if (ref) {
          const js = await t.call("job_status", { id: ref, wait: 0 }, { allowError: true });
          t.assert(js.status !== "error" || ["WRITE_OUTCOME_UNKNOWN", "REVIT_EXITED"].includes(js.code), `job_status ${ref}: ${js.code ?? js.status}`);
          t.assert(js.status !== "error" || !!js.fix, "job_status error has a fix");
        }
      }
      // Bring Revit back with the saved R13A so the run can go on.
      const launched = await launchRevit({ year: t.year, home: t.home, args: [path] });
      t.revit.pid = launched.pid;
      t.revit.launched = true;
      if (t.revit.slot) bindSlot(t.revit.slot, launched.pid);
      await waitReady({ year: t.year, pid: launched.pid, home: t.home, startedAt: launched.startedAt, call: t.call, doc: "R13A" });
      const back = await t.call("list", { kind: "levels" });
      t.assertEq(title(back), "R13A", "the session rebinds to R13A after the relaunch");
    });
  },
};
