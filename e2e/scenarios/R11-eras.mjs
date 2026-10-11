// R11 eras and clients side by side (D4 §8.8 R11, D2 E14, SPEC §8.13): one legacy 2025-06-18 session (Codex), one
// 2025-11-25 session (Claude) and one modern session pinned to 2026-07-28 against the same Revit.
//   1. Each session keeps its own pin (set_target in one never moves another's).
//   2. The same read gives identical results in every era (same JSON line, same doc suffix, no structuredContent).
//   3. Each broker exits within 2 s after its client closes stdin.

import { join } from "node:path";
import { makeCaller } from "../lib/call.mjs";
import { makeWriteGuard } from "../lib/fixtures.mjs";
import { processAlive } from "../lib/revit.mjs";

const title = (r) => (r.doc?.title ?? "").replace(/\.(rvt|rfa|rte)$/i, "");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function openSession(t, era) {
  const session = await t.connect({ era });
  const call = makeCaller({ session, coverage: t.coverage, year: t.year, writeGuard: makeWriteGuard(t.runRoot, () => null) });
  return { era, session, call };
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
  id: "R11",
  title: "Eras side by side: legacy, 2025-11-25 and modern sessions keep their own pins and get identical results",
  profiles: ["quick", "full"],
  years: "one",
  async run(t) {
    for (const name of ["R11A", "R11B"]) {
      await t.call("manage_document", { op: "new_project", path: join(t.yearDir, `${name}.rvt`), template: t.projectTemplate(), activate: true, doc: `@${t.year}` }, { timeoutMs: 120_000 });
    }
    t.setTargetPath(join(t.yearDir, "R11B.rvt"));

    const sessions = [];
    try {
      for (const era of ["2025-06-18", "2025-11-25", "modern"]) sessions.push(await openSession(t, era));
      const [legacy, claude, modern] = sessions;

      await t.step("each era negotiated its protocol", async () => {
        t.assertEq(legacy.session.protocol(), "2025-06-18", "legacy era");
        t.assertEq(claude.session.protocol(), "2025-11-25", "2025-11-25 era");
        t.assertEq(modern.session.protocol(), "2026-07-28", "modern era");
      });

      await t.step("each session keeps its own pin", async () => {
        t.assertEq(title(await legacy.call("set_target", { doc: "R11A" })), "R11A", "legacy pins R11A");
        t.assertEq(title(await claude.call("set_target", { doc: "R11B" })), "R11B", "claude pins R11B");
        t.assertEq(title(await modern.call("set_target", { doc: "R11A" })), "R11A", "modern pins R11A");
        const [a, b, c] = await Promise.all([legacy, claude, modern].map((s) => s.call("list", { kind: "levels" })));
        t.assertEq(title(a), "R11A", "legacy still on R11A");
        t.assertEq(title(b), "R11B", "claude still on R11B");
        t.assertEq(title(c), "R11A", "modern still on R11A");
      });

      await t.step("the same read is identical in every era", async () => {
        const results = await Promise.all(sessions.map((s) => s.call("list", { kind: "levels", doc: "R11A" })));
        const [first, ...rest] = results;
        for (const [i, r] of rest.entries()) {
          t.assertDeepEqual(r.json, first.json, `${sessions[i + 1].era} JSON line equals the legacy one`);
          t.assertEq(r.summary, first.summary, `${sessions[i + 1].era} summary equals the legacy one`);
          t.assertEq(title(r), "R11A", `${sessions[i + 1].era} names R11A`);
        }
        const help = await Promise.all(sessions.map((s) => s.call("help", { tool: "set_target" })));
        for (const h of help.slice(1)) t.assertEq(h.text, help[0].text, "help text identical across eras");
      });

      await t.step("every broker exits within 2 s after its client closes", async () => {
        for (const s of sessions) {
          const pid = s.session.pid;
          await s.session.close();
          t.assert(await waitExit(pid, 2_000), `${s.era} broker pid ${pid} exited within 2 s`);
        }
      });
    } finally {
      for (const s of sessions) await s.session.close();
    }
  },
};
