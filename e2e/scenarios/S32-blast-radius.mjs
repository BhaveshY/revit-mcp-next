// S32 blast radius (SPEC §6.1-§6.4, §13.3; D4 S32; owner P-MODIFY). Needs Revit with the add-in loaded from the e2e home.
// delete of 25 elements → NOT APPLIED (rule delete) with an exact plan and a confirm token, nothing deleted until the
// confirm call; expect_count / expect_ids abort with EXPECT_MISMATCH (nothing deleted) or pass when exact; preview is a
// dry run; pinned targets give ELEMENTS_PINNED unless allow_pinned; set_parameters on 250 elements → NOT APPLIED
// (rule bulk) → confirm applies all 250.

/** Ids of a write result (the list is capped at 50; the handle r# carries all of them). */
const createdIds = (r) => (Array.isArray(r.json?.created) ? r.json.created.map(String) : []);
const createdTotal = (r) => r.json?.created_total ?? (r.json?.created ?? []).length;

/** {cols, rows} table → array of objects. */
function rowsOf(r) {
  const j = r.json ?? {};
  const table = Array.isArray(j.rows) && Array.isArray(j.cols) ? j : j.table ?? j;
  if (!Array.isArray(table.rows) || !Array.isArray(table.cols)) return [];
  return table.rows.map((row) => Object.fromEntries(table.cols.map((c, i) => [c, row[i]])));
}

const totalOf = (r) => r.json?.total ?? r.json?.count ?? Number(/(\d[\d,]*)/.exec(r.summary ?? "")?.[1]?.replace(/,/g, "") ?? NaN);

/** A level name of the target doc (lowest by elevation when the table has it). */
async function baseLevel(t) {
  const r = await t.call("find_elements", { category: ["Levels"], fields: ["name", "elevation"], limit: 50 });
  const rows = rowsOf(r).filter((x) => x.name);
  t.assert(rows.length > 0, "the template has at least one level");
  rows.sort((a, b) => (Number(a.elevation) || 0) - (Number(b.elevation) || 0));
  return rows[0].name;
}

/** n wall segments along a zigzag starting at y0 (n+1 points, 1 m apart). */
const zigzag = (n, y0) => Array.from({ length: n + 1 }, (_, i) => [i * 1000, y0 + (i % 2) * 400]);

export default {
  id: "S32",
  title: "Blast radius: delete 25 → confirm, expect_count/expect_ids, preview, pinned, set_parameters 250 → confirm",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S32");
    t.setTargetPath(path);
    const level = await baseLevel(t);

    const walls = await t.step("create 25 + 2 walls for the delete checks", async () => {
      const big = await t.call("create_elements", { op: "wall", level, points: zigzag(25, 0), height: 3000 });
      t.assertEq(createdTotal(big), 25, "25 walls created");
      const small = await t.call("create_elements", { op: "wall", level, points: [[0, 20000], [3000, 20000], [6000, 20000]], height: 3000 });
      t.assertEq(createdTotal(small), 2, "2 walls created");
      return { big: createdIds(big), bigHandle: big.json.handle, small: createdIds(small) };
    });

    await t.step("expect_count / expect_ids mismatch → EXPECT_MISMATCH, nothing deleted", async () => {
      const wrongIds = await t.expectError("modify_elements", { op: "delete", ids: walls.small, expect_ids: [walls.small[0]] }, "EXPECT_MISMATCH");
      t.assertMatch(wrongIds.summary, /nothing was deleted/, "summary says nothing was deleted");
      t.assert(!!wrongIds.fix, "EXPECT_MISMATCH has a fix line");
      await t.expectError("modify_elements", { op: "delete", ids: walls.small, expect_count: 3 }, "EXPECT_MISMATCH");
      const still = await t.call("find_elements", { ids: walls.small, count_only: true });
      t.assertEq(totalOf(still), 2, "both walls still exist");
    });

    await t.step("expect_ids exact → deleted", async () => {
      const r = await t.call("modify_elements", { op: "delete", ids: walls.small, expect_ids: walls.small });
      t.assertEq(r.status, "ok", "delete applied");
      t.assertEq((r.json.deleted ?? []).length, 2, "2 deleted");
      t.assertJson(r, (j) => typeof j.write === "string" && j.write.startsWith("w"), "write id");
    });

    await t.step("preview delete 25 is a dry run with the plan", async () => {
      const r = await t.call("modify_elements", { op: "delete", from: walls.bigHandle, preview: true });
      t.assert(r.status === "ok" || r.status === "not_applied", `preview status ${r.status}`);
      t.assertMatch(r.summary, /preview|would/i, "preview summary");
      const still = await t.call("find_elements", { from: walls.bigHandle, count_only: true });
      t.assertEq(totalOf(still), 25, "nothing deleted by preview");
    });

    const plan = await t.step("delete 25 → NOT APPLIED (rule delete) with exact counts", async () => {
      const r = await t.call("modify_elements", { op: "delete", from: walls.bigHandle });
      t.assertEq(r.status, "not_applied", "needs the user's OK");
      t.assertEq(r.json.rule, "delete", "rule delete");
      t.assertEq(r.json.delete?.total, 25, "delete total 25");
      t.assertEq(r.json.delete?.by_category?.Walls, 25, "25 walls by category");
      t.assertMatch(r.json.confirm, /^[A-Z0-9]{6}$/i, "confirm token");
      t.assertMatch(r.next ?? "", /^ask the user, then modify_elements \{"op":"delete","confirm":"/, "next asks the user first");
      const still = await t.call("find_elements", { from: walls.bigHandle, count_only: true });
      t.assertEq(totalOf(still), 25, "nothing deleted before confirm");
      return { token: r.json.confirm };
    });

    await t.step("pinned target → ELEMENTS_PINNED; unpin restores", async () => {
      const one = [walls.big[0]];
      await t.call("modify_elements", { op: "pin", ids: one });
      const r = await t.expectError("modify_elements", { op: "move", ids: one, by: [0, 100] }, "ELEMENTS_PINNED");
      t.assertMatch(r.fix ?? "", /allow_pinned/, "fix mentions allow_pinned");
      await t.call("modify_elements", { op: "pin", ids: one, pinned: false });
    });

    await t.step("confirm applies exactly the plan", async () => {
      const r = await t.call("modify_elements", { op: "delete", confirm: plan.token });
      t.assertEq(r.status, "ok", "confirmed delete applied");
      t.assertEq(r.json.deleted_total ?? (r.json.deleted ?? []).length, 25, "25 deleted");
      const reuse = await t.call("modify_elements", { op: "delete", confirm: plan.token }, { allowError: true });
      t.assertEq(reuse.status, "error", "a confirm token is single use");
    });

    const many = await t.step("create 250 walls", async () => {
      const r = await t.call("create_elements", { op: "wall", level, points: zigzag(250, 40000), height: 2500 }, { timeoutMs: 120_000 });
      t.assertEq(createdTotal(r), 250, "250 walls created");
      return { handle: r.json.handle, ids: createdIds(r) };
    });

    await t.step("set_parameters on 250 → NOT APPLIED (rule bulk) → confirm", async () => {
      const r = await t.call("set_parameters", { from: many.handle, values: { Comments: "S32 {n}" } });
      t.assertEq(r.status, "not_applied", "bulk needs the user's OK");
      t.assertEq(r.json.rule, "bulk", "rule bulk");
      t.assertEq(r.json.modify, 250, "modify 250");
      const ok = await t.call("set_parameters", { confirm: r.json.confirm }, { timeoutMs: 120_000 });
      t.assertEq(ok.status, "ok", "confirmed set applied");
      t.assertEq(ok.json.set, 250, "250 elements set");
      t.assertEq(ok.json.modified_total ?? (ok.json.modified ?? []).length, 250, "250 modified");
      const check = await t.call("find_elements", { category: ["Walls"], where: ["Comments = S32 250"], count_only: true });
      t.assertEq(totalOf(check), 1, "{n} numbered the last wall 250");
    });

    await t.step("set_parameters below the threshold applies at once; unknown names fail with nothing written", async () => {
      const few = many.ids.slice(0, 3);
      const r = await t.call("set_parameters", { ids: few, values: { Mark: "M{n}" } });
      t.assertEq(r.status, "ok", "3 elements apply without confirm");
      t.assertEq(r.json.set, 3, "3 set");
      t.assertDeepEqual(r.json.values.map((row) => row[1].Mark), ["M1", "M2", "M3"], "{n} numbering in target order");
      const unknown = await t.expectError("set_parameters", { from: many.handle, values: { "No Such Param S32": 1 } }, "NOT_FOUND");
      t.assertMatch(unknown.summary, /nothing was written/, "nothing written");
      const info = await t.call("set_parameters", { ids: ["project_info"], values: { "Project Number": "S32-001" } });
      t.assertEq(info.status, "ok", "project info set");
    });
  },
};
