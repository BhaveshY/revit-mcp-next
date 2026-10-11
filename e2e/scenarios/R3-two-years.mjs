// R3 two Revit years at once (D4 §8.8 R3, D2 §10.5 "2024 and 2027 side by side", SPEC §7.3 title@year).
// Needs a harness Revit 2024 and 2027 in the same e2e home: run with --years 2024,2027. The scenario runs once, in the
// iteration of the later year (by then both are up); the earlier iteration only logs that it waits for the other year.
//   1. "R3Same" is created in both years from the same template ("@<year>" picks the Revit); status lists both with the
//      year, and detail:full shows two different document keys (the major year is part of the key).
//   2. A fresh session: set_target "R3Same" is TARGET_AMBIGUOUS with numbered options naming both years; following
//      the options (R3Same@2027, or doc + instance) resolves it; a write with doc "R3Same" is TARGET_AMBIGUOUS.
//   3. Two sessions (two "Codex threads") pinned to different years run 40 interleaved parallel calls, captures
//      included: every result names the right doc and year, nothing errors.
//   4. Registration stays healthy: both instances stay listed, no registration file is served from a fallback, and
//      the add-in logs never show "Unable to remove the file to be replaced".
//   5. Closing the 2027 doc leaves the 2024 session unaffected.

import { existsSync, mkdirSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { makeCaller } from "../lib/call.mjs";
import { makeWriteGuard } from "../lib/fixtures.mjs";
import { defaultProjectTemplateCandidates } from "@revit-mcp-next/contracts/home";

const title = (r) => (r.doc?.title ?? "").replace(/\.(rvt|rfa|rte)$/i, "");

async function openSession(t, o = {}) {
  const session = await t.connect(o);
  const call = makeCaller({ session, coverage: t.coverage, year: t.year, writeGuard: makeWriteGuard(t.runRoot, () => null) });
  call.expectError = (tool, args, codes, options = {}) => call(tool, args, { ...options, expectError: codes });
  return { session, call };
}

function templateOf(year) {
  for (const candidate of defaultProjectTemplateCandidates(year)) if (existsSync(candidate)) return candidate;
  return null;
}

export default {
  id: "R3",
  title: "Two Revit years side by side: title@year, parallel sessions, registration health",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const status = await t.call("status", { detail: "full" });
    const years = [...new Set((status.json?.revit ?? []).map((row) => row[0]))].sort();
    if (!(years.includes(2024) && years.includes(2027))) {
      t.log(`waiting for both years (running: ${years.join(", ") || "none"}); run with --years 2024,2027 to exercise R3`);
      return;
    }
    if (t.year !== Math.max(...years)) {
      t.log(`R3 runs in the Revit ${Math.max(...years)} iteration`);
      return;
    }

    const paths = {};
    await t.step("create R3Same in 2024 and 2027 from the same template", async () => {
      for (const year of [2024, 2027]) {
        const dir = join(t.runRoot, String(year));
        mkdirSync(dir, { recursive: true });
        const template = templateOf(year);
        t.assert(template, `a Revit ${year} project template exists`);
        paths[year] = join(dir, "R3Same.rvt");
        const r = await t.call("manage_document", { op: "new_project", path: paths[year], template, activate: true, doc: `@${year}` }, { timeoutMs: 120_000 });
        t.assertEq(title(r), "R3Same", `new_project in ${year}`);
        t.assertEq(r.doc?.year, year, `"@${year}" picked Revit ${year}`);
      }
      const full = await t.call("status", { detail: "full" });
      const same = (full.json.docsFull ?? []).filter((d) => /^R3Same(\.rvt)?$/i.test(d.title));
      t.assertEq(same.length, 2, "R3Same listed once per year");
      t.assert(same[0].key !== same[1].key, `distinct document keys per year (${same.map((d) => d.key).join(" vs ")})`);
      t.setTargetPath(paths[2027]);
    });

    const sA = await openSession(t);
    const sB = await openSession(t);
    try {
      await t.step("same title in two years: TARGET_AMBIGUOUS with numbered options; title@year resolves", async () => {
        const amb = await sA.call.expectError("set_target", { doc: "R3Same" }, "TARGET_AMBIGUOUS");
        const text = `${amb.options ?? ""} ${amb.fix ?? ""}`;
        t.assertMatch(text, /2024/, "options name Revit 2024");
        t.assertMatch(text, /2027/, "options name Revit 2027");
        const a = await sA.call("set_target", { doc: "R3Same", instance: "2024" });
        t.assertEq(a.doc?.year, 2024, "doc + instance resolves");
        const b = await sB.call("set_target", { doc: "R3Same@2027" });
        t.assertEq(b.doc?.year, 2027, "title@2027 resolves");
        await sA.call.expectError("create_elements", { op: "level", elevation: 31_000, name: "E2E_R3_amb", plans: false, doc: "R3Same" }, "TARGET_AMBIGUOUS");
      });

      await t.step("two sessions pinned to different years: 40 interleaved parallel calls name the right year", async () => {
        const reads = (i) => [
          ["list", { kind: "levels" }],
          ["find_elements", { category: ["Walls"] }],
          ["status", {}],
          ["get_view", {}],
          ["list", { kind: "views" }],
        ][i % 5];
        const jobs = [];
        for (let i = 0; i < 20; i++) {
          for (const [s, year] of [[sA, 2024], [sB, 2027]]) {
            const [tool, args] = i === 7 || i === 15 ? ["capture", {}] : reads(i);
            jobs.push(
              s.call(tool, args).then((r) => {
                if (tool !== "status") t.assertEq(r.doc?.year, year, `${tool} in the ${year} session names Revit ${year}`);
                else t.assert(r.json?.target?.doc, "status shows the session's target");
                if (tool !== "status") t.assertEq(title(r), "R3Same", `${tool} names R3Same`);
                return r.ms;
              })
            );
          }
        }
        const ms = await Promise.all(jobs);
        t.log(`40 parallel calls: max ${Math.max(...ms)} ms, median ${ms.sort((x, y) => x - y)[20]} ms`);
      });

      await t.step("registration stays healthy for both years", async () => {
        const full = await t.call("status", { detail: "full" });
        const listed = new Set((full.json.revit ?? []).map((row) => row[0]));
        t.assert(listed.has(2024) && listed.has(2027), "both instances still listed");
        for (const inst of full.json.instances ?? []) t.assert(inst.snapshot?.source === "file" || inst.snapshot?.source === "control", `Revit ${inst.year} registration served from ${inst.snapshot?.source} (${inst.snapshot?.problem ?? ""})`);
        const logs = join(t.home, "logs");
        if (existsSync(logs)) {
          for (const name of readdirSync(logs).filter((n) => /^addin-\d{4}-.*\.jsonl$/i.test(n))) {
            const text = readFileSync(join(logs, name), "utf8");
            t.assert(!/Unable to remove the file to be replaced/i.test(text), `${name} has no "Unable to remove the file to be replaced"`);
          }
        }
      });

      await t.step("closing the 2027 doc leaves the 2024 session unaffected", async () => {
        const closed = await sB.call("manage_document", { op: "close", doc: "R3Same@2027", save: false }, { timeoutMs: 120_000, allowError: true });
        if (closed.status === "not_applied" && closed.json?.confirm) await sB.call("manage_document", { op: "close", confirm: closed.json.confirm }, { timeoutMs: 120_000 });
        else t.assertEq(closed.status, "ok", `close: ${closed.code ?? ""} ${closed.summary}`);
        const a = await sA.call("list", { kind: "levels" });
        t.assertEq(a.doc?.year, 2024, "the 2024 session still works");
        t.assertEq(title(a), "R3Same", "and still names R3Same");
        const b = await sB.call("list", { kind: "levels" }, { allowError: true });
        t.assert(b.status === "error" ? !!b.fix : b.notices.some((n) => n.startsWith("PIN_MOVED")), `the 2027 session is told its doc closed: ${b.text.slice(0, 200)}`);
      });
    } finally {
      await sA.session.close();
      await sB.session.close();
    }
  },
};
