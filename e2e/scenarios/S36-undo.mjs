// S36 undo (SPEC §6.9, §13.3; owner P-CONTROL). PostCommand undo/redo of this session's writes confirmed by
// DocumentChanged; "MCP ui" steps on top (ui isolate) are undone transparently; another session's newer write blocks
// (UNDO_BLOCKED, nothing undone) and compensate deletes what our write created as a new, itself undoable write;
// writes that changed existing elements are IRREVERSIBLE by compensation; the doc must be UI-active.
import { makeCaller } from "../lib/call.mjs";

async function existing(t, ids) {
  if (!ids.length) return 0;
  const r = await t.call("find_elements", { ids, limit: 500 });
  return Number(r.json?.total ?? r.json?.rows?.length ?? 0);
}

export default {
  id: "S36",
  title: "undo: own writes only, ui steps transparent, blocked by others, compensate, active doc",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S36");
    t.setTargetPath(path);

    const a = await t.step("create level S36 A (one write)", async () => {
      const r = await t.call("create_elements", { op: "level", elevation: 4000, name: "S36 A", plans: false });
      t.assertJson(r, (j) => Array.isArray(j.created) && j.created.length >= 1, "level created");
      return { ids: r.json.created, write: r.json.write };
    });

    await t.step("ui isolate on top, then undo undoes the level transparently", async () => {
      const iso = await t.call("ui", { op: "isolate", category: ["Walls"] });
      t.assertEq(iso.status, "ok", "isolate");
      const u = await t.call("undo", {});
      t.assertEq(u.status, "ok", "undo status");
      t.assertMatch(u.summary, new RegExp(`undid ${a.write} create_elements\\.level`), "undo names the write");
      t.log(`ui steps undone along: ${u.json?.ui_steps ?? 0}`);
      t.assertEq(await existing(t, a.ids), 0, "the level is gone");
    });

    await t.step("redo restores it; undo after redo works again; redo again", async () => {
      const re = await t.call("undo", { redo: true });
      t.assertEq(re.status, "ok", "redo");
      t.assertEq(await existing(t, a.ids), a.ids.length, "level back");
      const u = await t.call("undo", { steps: 1 });
      t.assertEq(u.status, "ok", "second undo");
      const re2 = await t.call("undo", { redo: true });
      t.assertEq(re2.status, "ok", "second redo");
      t.assertEq(await existing(t, a.ids), a.ids.length, "level back again");
    });

    const other = await t.connect();
    try {
      const call2 = makeCaller({ session: other });
      const b = await t.step("another session writes on top", async () => {
        const r = await call2("create_elements", { op: "level", elevation: 8000, name: "S36 B", plans: false });
        t.assertEq(r.status, "ok", "session 2 write");
        return { ids: r.json.created };
      });

      await t.step("undo is blocked by the other session's newer write (nothing undone)", async () => {
        const r = await t.call("undo", {}, { expectError: ["UNDO_BLOCKED"] });
        t.assertMatch(r.summary, /another AI session/, "names the other session");
        t.assertMatch(r.fix ?? "", /undo \{.*"mode":"compensate"/, "fix offers compensate (the level is compensable)");
        t.assertEq(await existing(t, a.ids), a.ids.length, "our level is untouched");
        t.assertEq(await existing(t, b.ids), b.ids.length, "their level is untouched");
      });

      await t.step("compensate deletes what our newest write created, as a new write", async () => {
        const r = await t.call("undo", { mode: "compensate" });
        t.assertEq(r.status, "ok", "compensate status");
        t.assertJson(r, (j) => /^w\d+$/.test(j.write) && j.deleted.length >= 1, "a write that deleted the level");
        t.assertMatch(r.summary, /compensated/, "summary");
        t.assertEq(await existing(t, a.ids), 0, "our level is gone");
        t.assertEq(await existing(t, b.ids), b.ids.length, "their level stays");
      });

      await t.step("the compensation is our newest write, so undo {} reverts it", async () => {
        const u = await t.call("undo", {});
        t.assertEq(u.status, "ok", "undo of the compensation");
        t.assertMatch(u.summary, /undo\.compensate/, "it undid the compensation");
        t.assertEq(await existing(t, a.ids), a.ids.length, "our level is back");
      });

      await t.step("session 2 undoes its own write", async () => {
        const u = await call2("undo", {});
        t.assertEq(u.status, "ok", "session 2 undo");
        t.assertEq(await existing(t, b.ids), 0, "their level is gone");
      });
    } finally {
      await other.close();
    }

    await t.step("compensation of a write that changed existing elements is IRREVERSIBLE", async () => {
      const s = await t.call("set_parameters", { ids: a.ids.slice(0, 1), values: { Elevation: 4500 } });
      t.assertEq(s.status, "ok", "change the level elevation");
      await t.call("undo", { mode: "compensate" }, { expectError: ["IRREVERSIBLE"] });
      const u = await t.call("undo", {});
      t.assertEq(u.status, "ok", "but Revit undo of it works");
    });

    await t.step("undo needs the target doc UI-active (never switches it)", async () => {
      const second = await t.newProject("S36B", { levels: false });
      t.setTargetPath(second.path);
      const nothing = await t.call("undo", {}, { expectError: ["UNDO_BLOCKED"] });
      t.assertMatch(nothing.summary, /no change of yours/, "nothing of ours in the new doc");
      const pin = await t.call("set_target", { doc: "S36" });
      t.assertEq(pin.status, "ok", "pin the first doc (not active)");
      t.setTargetPath(path);
      await t.call("undo", {}, { expectError: ["NEEDS_ACTIVE_DOC"] });
    });
  },
};
