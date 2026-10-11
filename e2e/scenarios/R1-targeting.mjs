// R1 targeting (SPEC §7, §13.3; D4 §8.8 R1 updated per SPEC §7.4; D2 §10). One Revit, disposable docs only:
// projects Tower and Site plus a family R1Fam, all created through the product under the run folder.
//   1. A fresh session with no pin and Site UI-active picks Site (a project, never the family), pins it and names it.
//   2. ui activate_doc Tower by this session re-pins with TARGET_NOW (SPEC §7.4 item 1).
//   3. Another session switching the active doc gives ACTIVE_DIFFERS once (explicit pin); an auto-pinned session's next
//      write returns TARGET_CHANGED {pinned, active} with nothing written, and the identical repeat succeeds.
//   4. set_target grammar: active, #n, exact title, path, title@year, @year, follow (tracks tab switches; a write after a
//      switch is gated once), none.
//   5. A per-call doc never moves the pin; a read accepts a unique prefix ("Tow"), a write with doc "Tow" is
//      TARGET_AMBIGUOUS with numbered options.
//   6. A family-only op on a project gives FAMILY_DOC_REQUIRED with a fix; read_family on R1Fam works.
//   7. save_as Tower → Tower2: the next call names Tower2 with no set_target (SAVED_AS).
//   8. Closing the pinned Tower2: the next write is TARGET_CHANGED (never a silent retarget); open Tower2 re-pins.
// Every result is checked for the doc suffix; errors must carry fix:.

import { existsSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { makeCaller } from "../lib/call.mjs";
import { makeWriteGuard } from "../lib/fixtures.mjs";

const title = (r) => (r.doc?.title ?? "").replace(/\.(rvt|rfa|rte)$/i, "");
const notice = (r, code) => r.notices.find((n) => n.startsWith(code));
let levelSeq = 0;

/** A second MCP session (another "Codex thread") with the same coverage and write guard. */
async function openSession(t, o = {}) {
  const session = await t.connect(o);
  const call = makeCaller({ session, coverage: t.coverage, year: t.year, writeGuard: makeWriteGuard(t.runRoot, () => null) });
  call.expectError = (tool, args, codes, options = {}) => call(tool, args, { ...options, expectError: codes });
  return { session, call };
}

/** The metric generic-model family template of a year (English first, else any Generic Model .rft). */
export function familyTemplate(year) {
  const root = join(process.env.ProgramData ?? process.env.PROGRAMDATA ?? "C:\\ProgramData", "Autodesk", `RVT ${year}`, "Family Templates");
  const preferred = [join(root, "English", "Metric Generic Model.rft"), join(root, "English_I", "Generic Model.rft"), join(root, "German", "M_Allgemeines Modell.rft")];
  for (const p of preferred) if (existsSync(p)) return p;
  if (!existsSync(root)) return null;
  for (const dir of readdirSync(root, { withFileTypes: true })) {
    if (!dir.isDirectory()) continue;
    const hit = readdirSync(join(root, dir.name)).find((f) => /generic model\.rft$/i.test(f) || /allgemeines modell\.rft$/i.test(f));
    if (hit) return join(root, dir.name, hit);
  }
  return null;
}

/** A small, harmless write: one level at a unique elevation. */
function levelArgs(extra = {}) {
  levelSeq += 1;
  return { op: "level", elevation: 20_000 + levelSeq * 500 + Math.floor(Math.random() * 400), name: `E2E_R1_${Date.now().toString(36)}_${levelSeq}`, plans: false, ...extra };
}

export default {
  id: "R1",
  title: "Targeting: auto pin, re-pin, gates, grammar, prefix/exact rules, Save As and close",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const at = `@${t.year}`;
    const towerPath = join(t.yearDir, "Tower.rvt");
    const sitePath = join(t.yearDir, "Site.rvt");
    const tower2Path = join(t.yearDir, "Tower2.rvt");

    // ------------------------------------------------------------------ fixtures (session 1 = t.call)
    await t.step("create Tower, Site and R1Fam in this Revit", async () => {
      for (const [name, path] of [["Tower", towerPath], ["Site", sitePath]]) {
        const r = await t.call("manage_document", { op: "new_project", path, template: t.projectTemplate(), activate: true, doc: at }, { timeoutMs: 120_000 });
        t.assert(notice(r, "TARGET_NOW"), `new_project ${name} re-pins with TARGET_NOW: ${r.notices.join(" | ")}`);
        t.assertEq(title(r), name, `new_project names ${name}`);
        t.assertEq(r.doc?.year, t.year, "doc year");
      }
      const rft = familyTemplate(t.year);
      if (rft) {
        const f = await t.call("manage_document", { op: "new_family", template: rft, path: join(t.yearDir, "R1Fam.rfa"), doc: at }, { timeoutMs: 120_000 });
        t.assertEq(f.status, "ok", "new_family");
      } else t.log("SKIP family steps: no Generic Model family template found");
      // Make Site the UI-active doc for the auto-pin check.
      const a = await t.call("ui", { op: "activate_doc", doc: "Site" });
      t.assertEq(title(a), "Site", "activate_doc Site");
      t.setTargetPath(sitePath);
    });

    // ------------------------------------------------------------------ 1. auto pin in a fresh session
    const s2 = await openSession(t);
    try {
      await t.step("fresh session auto-pins the UI-active project and names it", async () => {
        const r = await s2.call("list", { kind: "levels" });
        t.assertEq(title(r), "Site", "auto-resolved doc");
        t.assertEq(r.doc?.year, t.year, "auto-resolved year");
        t.assert(notice(r, "AUTO_PINNED"), `AUTO_PINNED notice (several docs open): ${r.notices.join(" | ")}`);
        const again = await s2.call("list", { kind: "levels" });
        t.assert(!notice(again, "AUTO_PINNED"), "AUTO_PINNED only once");
      });

      // ------------------------------------------------------------------ 2. activate_doc re-pins
      await t.step("ui activate_doc by the session re-pins with TARGET_NOW", async () => {
        const r = await s2.call("ui", { op: "activate_doc", doc: "Tower" });
        t.assert(notice(r, "TARGET_NOW"), `TARGET_NOW after activate_doc: ${r.notices.join(" | ")}`);
        const next = await s2.call("list", { kind: "levels" });
        t.assertEq(title(next), "Tower", "the pin moved to Tower");
      });

      // ------------------------------------------------------------------ 3. ACTIVE_DIFFERS and the auto-pin switch gate
      await t.step("another session's tab switch: ACTIVE_DIFFERS once for an explicit pin", async () => {
        await s2.call("set_target", { doc: "Tower" });
        await t.call("ui", { op: "activate_doc", doc: "Site" });
        const r = await s2.call("list", { kind: "levels" });
        t.assertEq(title(r), "Tower", "explicit pin unchanged");
        t.assert(notice(r, "ACTIVE_DIFFERS"), `ACTIVE_DIFFERS notice: ${r.notices.join(" | ")}`);
        const again = await s2.call("list", { kind: "levels" });
        t.assert(!notice(again, "ACTIVE_DIFFERS"), "ACTIVE_DIFFERS once per change");
      });

      await t.step("auto pin + user switch: first write TARGET_CHANGED, identical repeat writes into the pinned doc", async () => {
        const s3 = await openSession(t);
        try {
          // Site is active now: s3 auto-pins Site and writes once.
          const first = await s3.call("create_elements", levelArgs());
          t.assertEq(title(first), "Site", "s3 auto-pinned Site");
          // The "user" (session 1) switches the active project.
          await t.call("ui", { op: "activate_doc", doc: "Tower" });
          const args = levelArgs();
          const gated = await s3.call.expectError("create_elements", args, "TARGET_CHANGED");
          t.assertMatch(gated.summary, /nothing was written/i, "TARGET_CHANGED says nothing was written");
          t.assertMatch(gated.fix ?? "", /create_elements|set_target/, "fix repeats the call or re-targets");
          const repeat = await s3.call("create_elements", args);
          t.assertEq(title(repeat), "Site", "the repeat writes into the pinned doc");
        } finally {
          await s3.session.close();
        }
      });

      // ------------------------------------------------------------------ 4. grammar
      await t.step("set_target grammar: active, #n, title, path, title@year, @year, none", async () => {
        const active = await s2.call("set_target", { doc: "active" });
        t.assertEq(title(active), "Tower", "active = the UI-active doc");
        const status = await s2.call("status", {});
        const site = status.json.docs.rows.find((row) => row[1] === "Site" && row[3] === t.year);
        t.assert(site, "Site listed in status");
        const byNumber = await s2.call("set_target", { doc: `#${site[0]}` });
        t.assertEq(title(byNumber), "Site", "#n");
        t.assertEq(title(await s2.call("set_target", { doc: "tower" })), "Tower", "exact title, case-insensitive");
        t.assertEq(title(await s2.call("set_target", { doc: sitePath })), "Site", "absolute path");
        t.assertEq(title(await s2.call("set_target", { doc: `Tower@${t.year}` })), "Tower", "title@year");
        t.assertEq((await s2.call("set_target", { doc: at })).doc?.year, t.year, "@year");
        const none = await s2.call("set_target", { doc: "none" });
        t.assertEq(none.status, "ok", "none unpins");
        await s2.call.expectError("set_target", { doc: "NoSuchDocE2E" }, ["DOC_NOT_OPEN"]);
      });

      await t.step("follow mode tracks tab switches; a write after a switch is gated once", async () => {
        await t.call("ui", { op: "activate_doc", doc: "Tower" });
        const follow = await s2.call("set_target", { doc: "follow" });
        t.assertEq(title(follow), "Tower", "follow starts on the active doc");
        const w1 = await s2.call("create_elements", levelArgs());
        t.assertEq(title(w1), "Tower", "follow write goes to Tower");
        await t.call("ui", { op: "activate_doc", doc: "Site" });
        const read = await s2.call("list", { kind: "levels" });
        t.assertEq(title(read), "Site", "follow read follows the switch");
        const args = levelArgs();
        await s2.call.expectError("create_elements", args, "TARGET_CHANGED");
        const repeat = await s2.call("create_elements", args);
        t.assertEq(title(repeat), "Site", "repeat writes into the new active doc");
        await s2.call("set_target", { doc: "Tower" });
      });

      // ------------------------------------------------------------------ 5. per-call doc, prefix vs exact
      await t.step("per-call doc never moves the pin; read prefix ok; write prefix TARGET_AMBIGUOUS", async () => {
        const other = await s2.call("list", { kind: "levels", doc: "Site" });
        t.assertEq(title(other), "Site", "per-call doc");
        const pinned = await s2.call("list", { kind: "levels" });
        t.assertEq(title(pinned), "Tower", "pin unchanged after a per-call doc");
        const prefix = await s2.call("list", { kind: "levels", doc: "Tow" });
        t.assertEq(title(prefix), "Tower", "read accepts a unique title prefix");
        const amb = await s2.call.expectError("create_elements", levelArgs({ doc: "Tow" }), "TARGET_AMBIGUOUS");
        t.assert(amb.options || /#\d/.test(amb.fix ?? ""), `numbered options for the ambiguous write: ${amb.text.slice(0, 300)}`);
        t.assertMatch(amb.summary, /nothing ran/i, "nothing ran");
      });

      // ------------------------------------------------------------------ 6. doc kinds
      await t.step("family-only op on a project: FAMILY_DOC_REQUIRED; family reads work on the family doc", async () => {
        const r = await s2.call.expectError("edit_family", { op: "save", doc: "Tower" }, ["FAMILY_DOC_REQUIRED"]);
        t.assert(!!r.fix, "fix present");
        if (familyTemplate(t.year)) {
          const fam = await s2.call("read_family", { doc: "R1Fam" });
          t.assertEq(title(fam), "R1Fam", "read_family on the family doc");
          // Family docs are never auto-picked while a project is open (fresh session).
          const s4 = await openSession(t);
          try {
            await t.call("ui", { op: "activate_doc", doc: "R1Fam" });
            const auto = await s4.call("list", { kind: "levels" });
            t.assert(title(auto) !== "R1Fam", `a fresh session never auto-picks the family doc (got ${title(auto)})`);
          } finally {
            await s4.session.close();
          }
        }
      });

      // ------------------------------------------------------------------ 7. Save As
      await t.step("save_as Tower -> Tower2: the next call names Tower2 without set_target", async () => {
        await s2.call("set_target", { doc: "Tower" });
        const r = await s2.call("manage_document", { op: "save_as", path: tower2Path, overwrite: true }, { timeoutMs: 120_000 });
        t.assertEq(r.status, "ok", "save_as");
        const next = await s2.call("list", { kind: "levels" });
        t.assertEq(title(next), "Tower2", "pin followed Save As");
        t.setTargetPath(tower2Path);
      });

      // ------------------------------------------------------------------ 8. close the pinned doc
      await t.step("close the pinned Tower2: the next write is gated, never a silent retarget; open re-pins", async () => {
        const closed = await s2.call("manage_document", { op: "close", doc: "Tower2", save: false }, { timeoutMs: 120_000, allowError: true });
        if (closed.status === "not_applied") {
          // Unsaved changes ask to confirm (SPEC §6.3): confirm it, the doc is disposable.
          const token = closed.json?.confirm;
          t.assert(token, "close confirm token");
          await s2.call("manage_document", { op: "close", confirm: token }, { timeoutMs: 120_000 });
        } else t.assertEq(closed.status, "ok", `close: ${closed.code ?? ""} ${closed.summary}`);
        const args = levelArgs();
        const gated = await s2.call("create_elements", args, { allowError: true });
        t.assertEq(gated.status, "error", "the first write after closing the pinned doc does not write");
        t.assert(["TARGET_CHANGED", "TARGET_CLOSED", "DOC_NOT_OPEN", "TARGET_AMBIGUOUS"].includes(gated.code), `actionable targeting error, got ${gated.code}`);
        t.assert(!!gated.fix, "fix present");
        const reopened = await s2.call("manage_document", { op: "open", path: tower2Path, activate: true }, { timeoutMs: 120_000 });
        t.assert(notice(reopened, "TARGET_NOW"), `open re-pins: ${reopened.notices.join(" | ")}`);
        const read = await s2.call("list", { kind: "levels" });
        t.assertEq(title(read), "Tower2", "pinned to the reopened doc");
      });
    } finally {
      await s2.session.close();
    }
  },
};
