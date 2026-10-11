// S35 change_set (SPEC §6.7, §13.3; owner P-CONTROL). One TransactionGroup per set: $refs between ops, one undo step,
// all or nothing on failure, preview + confirm token, static blast rules for the aggregate, and the broker refusals
// (forward refs, bad ref syntax, lifecycle ops, write tools only).

/** How many of these ids still exist (find_elements skips gone ids with warn IDS_GONE). */
async function existing(t, ids) {
  if (!ids.length) return 0;
  const r = await t.call("find_elements", { ids, limit: 500 });
  return Number(r.json?.total ?? r.json?.rows?.length ?? 0);
}

const L = (name, elevation) => ({ tool: "create_elements", op: "level", elevation, name, plans: false });

export default {
  id: "S35",
  title: "change_set: $refs, one undo step, all or nothing, preview/confirm, refusals",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S35");
    t.setTargetPath(path);

    const applied = await t.step("level + two walls on $0 ($prev chaining) apply as one write", async () => {
      const r = await t.call("change_set", {
        name: "S35 shell",
        ops: [
          L("S35 L3", 7000),
          { tool: "create_elements", op: "wall", level: "$0", start: [0, 0], end: [12000, 0], height: 3000 },
          { tool: "create_elements", op: "wall", level: "$0", start: [12000, 0], end: [12000, 8000], height: 3000 },
        ],
      });
      t.assertEq(r.status, "ok", "change_set status");
      t.assertJson(r, (j) => Array.isArray(j.ops) && j.ops.length === 3, "three ops reported");
      t.assertJson(r, (j) => /^w\d+$/.test(j.write), "write id w#");
      t.assertJson(r, (j) => Array.isArray(j.created) && j.created.length >= 3, "level and walls created");
      t.assertMatch(r.summary, /change_set S35 shell: 3 ops/, "summary names the set");
      const ids = r.json.created;
      t.assertEq(await existing(t, ids), ids.length, "every created element exists");
      return { ids };
    });

    await t.step("one undo removes the whole set; redo brings it back", async () => {
      const u = await t.call("undo", {});
      t.assertEq(u.status, "ok", "undo status");
      t.assertMatch(u.summary, /undid .*change_set S35 shell/, "undo names the change_set");
      t.assertEq(await existing(t, applied.ids), 0, "nothing of the set is left");
      t.assertJson(u, (j) => typeof j.undo === "string" && j.undo.includes("redo"), "the hint after undo is redo");
      const re = await t.call("undo", { redo: true });
      t.assertEq(re.status, "ok", "redo status");
      t.assertEq(await existing(t, applied.ids), applied.ids.length, "redo restored the set");
    });

    await t.step("a failing op rolls back the whole set (all or nothing)", async () => {
      const r = await t.call("change_set", { ops: [L("S35 L9", 21000), { tool: "create_elements", op: "wall", level: "No Such Level", start: [0, 0], end: [1000, 0] }] }, { expectError: true });
      t.assertMatch(r.summary, /op \[1\]/, "the error names op [1]");
      // The level of op [0] must not exist: creating it again by the same name succeeds.
      const again = await t.call("change_set", { ops: [L("S35 L9", 21000)] });
      t.assertEq(again.status, "ok", "the level name is still free");
    });

    await t.step("preview changes nothing and its token applies exactly that plan", async () => {
      const p = await t.call("change_set", { preview: true, ops: [L("S35 P1", 24000)] });
      t.assertEq(p.status, "ok", "preview status");
      t.assertMatch(p.summary, /^preview/, "preview summary");
      t.assertJson(p, (j) => typeof j.confirm === "string" && j.confirm.length === 6, "preview token");
      const twice = await t.call("change_set", { preview: true, ops: [L("S35 P1", 24000)] });
      t.assertEq(twice.status, "ok", "the previewed level was not created (a second preview still works)");
      const a = await t.call("change_set", { confirm: p.json.confirm });
      t.assertEq(a.status, "ok", "confirm applies");
      t.assertJson(a, (j) => j.created.length >= 1, "the planned level was created");
      await t.call("change_set", { confirm: p.json.confirm }, { expectError: ["CONFIRM_EXPIRED"] });
    });

    await t.step("broker refusals: forward ref, bad ref, lifecycle op, read tool", async () => {
      const fwd = await t.call("change_set", { ops: [{ tool: "create_elements", op: "wall", level: "$1", start: [0, 0], end: [1000, 0] }, L("S35 X", 30000)] }, { expectError: ["INVALID_ARGS"] });
      t.assertMatch(fwd.summary, /earlier ops|refers to op \[1\]/, "forward reference explained");
      await t.call("change_set", { ops: [L("S35 Y", 31000), { tool: "create_elements", op: "wall", level: "$0.no-such", start: [0, 0], end: [1000, 0] }] }, { expectError: ["INVALID_ARGS"] });
      const life = await t.call("change_set", { ops: [{ tool: "manage_document", op: "save" }] }, { expectError: ["INVALID_ARGS"] });
      t.assertMatch(life.fix, /^manage_document \{/, "the fix is the standalone call");
      await t.call("change_set", { ops: [{ tool: "find_elements", op: "x", category: ["Walls"] }] }, { expectError: ["INVALID_ARGS"] });
    });

    await t.step("an op that always needs the user's OK makes the whole set NOT APPLIED", async () => {
      const r = await t.call("change_set", { ops: [L("S35 C", 33000), { tool: "manage_document", op: "coordinates", true_north: 0 }] });
      t.assertEq(r.status, "not_applied", "NOT APPLIED");
      t.assertJson(r, (j) => j.rule === "always" && typeof j.confirm === "string", "rule always + token");
      t.assertMatch(r.next, /^ask the user, then change_set \{"confirm":"[A-Z0-9]{6}"\}$/, "next asks the user");
    });

    await t.step("a change_set with more than 100 ops is refused before Revit", async () => {
      const ops = Array.from({ length: 101 }, (_, i) => L(`S35 M${i}`, 40000 + i * 100));
      await t.call("change_set", { ops }, { expectError: ["INVALID_ARGS"] });
    });
  },
};
