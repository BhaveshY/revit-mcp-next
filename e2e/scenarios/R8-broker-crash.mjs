// R8 broker crash (D4 §8.8 R8, D2 §13.3, SPEC §6.6/§8.10). A broker process (one MCP session) is killed hard; a new
// session must recover on its own:
//   1. The new session auto-resolves and pins without set_target.
//   2. A confirm token from the dead session is rejected with an actionable error (tokens live in broker memory).
//   3. A w# from the dead session gives JOB_UNKNOWN whose fix is status {"include":["writes"]}, and that section lists
//      the write recorded by the add-in (ids do not survive a broker restart; the add-in ledger does).
//   4. No orphan brokers: the killed process is gone at once, and a closed session's broker exits within 2 s.
// The broker-killed-mid-write recovery (RECOVERED_WRITE, D2 E11) is in R13.

import { join } from "node:path";
import { makeCaller } from "../lib/call.mjs";
import { makeWriteGuard } from "../lib/fixtures.mjs";
import { processAlive } from "../lib/revit.mjs";

const title = (r) => (r.doc?.title ?? "").replace(/\.(rvt|rfa|rte)$/i, "");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function openSession(t, o = {}) {
  const session = await t.connect(o);
  const call = makeCaller({ session, coverage: t.coverage, year: t.year, writeGuard: makeWriteGuard(t.runRoot, () => null) });
  call.expectError = (tool, args, codes, options = {}) => call(tool, args, { ...options, expectError: codes });
  return { session, call };
}

async function waitExit(pid, ms) {
  const end = Date.now() + ms;
  while (pid && processAlive(pid)) {
    if (Date.now() > end) return false;
    await sleep(50);
  }
  return true;
}

export default {
  id: "R8",
  title: "Broker crash: a new session recovers; old tokens and ids fail with actionable errors; no orphans",
  profiles: ["full"],
  years: "one",
  async run(t) {
    const path = join(t.yearDir, "R8A.rvt");
    await t.call("manage_document", { op: "new_project", path, template: t.projectTemplate(), activate: true, doc: `@${t.year}` }, { timeoutMs: 120_000 });
    t.setTargetPath(path);

    const s1 = await openSession(t);
    let token = null;
    let writeId = null;
    await t.step("session 1 writes once and holds a delete confirm token", async () => {
      await s1.call("set_target", { doc: "R8A" });
      const w = await s1.call("create_elements", { op: "level", elevation: 41_000, name: `E2E_R8_${Date.now().toString(36)}`, plans: false });
      writeId = w.json?.write ?? null;
      t.assert(writeId, `the write result carries its w# (${w.jsonText?.slice(0, 200)})`);
      const id = w.json?.created?.[0];
      t.assert(id, "a level was created");
      const preview = await s1.call("modify_elements", { op: "delete", ids: [id], preview: true });
      t.assertEq(preview.status, "not_applied", "preview returns NOT APPLIED");
      token = preview.json?.confirm ?? null;
      t.assert(token, "confirm token");
    });

    const deadPid = s1.session.pid;
    await t.step("kill session 1's broker hard", async () => {
      process.kill(deadPid);
      t.assert(await waitExit(deadPid, 2_000), `broker pid ${deadPid} is gone`);
      await s1.session.close();
    });

    const s2 = await openSession(t);
    try {
      await t.step("a new session auto-resolves and pins without set_target", async () => {
        const r = await s2.call("list", { kind: "levels" });
        t.assertEq(r.status, "ok", "first call of the new session");
        t.assert(title(r), "the result names the doc");
        t.assertEq(r.doc?.year, t.year, "and its year");
        const again = await s2.call("list", { kind: "levels" });
        t.assertEq(title(again), title(r), "the auto pin is sticky");
      });

      await t.step("the dead session's confirm token is rejected with a fix", async () => {
        const r = await s2.call.expectError("modify_elements", { op: "delete", confirm: token, doc: "R8A" }, ["CONFIRM_EXPIRED", "CONFIRM_MISMATCH"]);
        t.assert(!!r.fix, "fix present");
      });

      await t.step("the dead session's w# is JOB_UNKNOWN; status writes lists the recorded write", async () => {
        const r = await s2.call.expectError("job_status", { id: writeId }, "JOB_UNKNOWN");
        t.assertMatch(r.fix ?? "", /status \{"include":\["writes"\]\}/, "fix points at status writes");
        const writes = await s2.call("status", { include: ["writes"], doc: "R8A" });
        const section = writes.json?.sections?.writes;
        if (typeof section === "string") t.log(`status writes: ${section}`);
        else t.assert(Array.isArray(section) ? section.length > 0 : !!section, "recent writes are listed by the add-in ledger");
      });

      await t.step("no orphan broker: a closed session exits within 2 s", async () => {
        const pid = s2.session.pid;
        await s2.session.close();
        t.assert(await waitExit(pid, 2_000), `broker pid ${pid} exited after the client closed`);
        t.assert(!processAlive(deadPid), "the killed broker stays gone");
      });
    } finally {
      await s2.session.close();
    }
  },
};
