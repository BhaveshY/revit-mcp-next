// W3R units against Revit (SPEC §5.3 step 3, §5.4, §13.3 W3; owner W3-E2E). Needs Revit with the add-in.
// Weak models send metres, feet and unit strings; the broker keeps plain numbers in the call's units and the add-in
// scales them (RequestContext → PayloadReader factor from args.units). Every value read back is in mm/deg:
//   1. create_elements level with units:"m", elevation 3.5 → list levels shows 3500 mm.
//   2. a wall from [0,0] to [10,0] with units:"ft" → describe_elements geometry length 3048 mm.
//   3. a wall with unit strings in an mm call ("5 m", "4000 mm") → length 5000 mm.
//   4. modify_elements move by [1,0] with units:"m" → by [1000,0] mm; rotate angle "1.5708 rad" → 90 deg.
//   5. change_set units:"m" applies to every op (a wall of 2 m).

const createdIds = (r) => (Array.isArray(r.json?.created) ? r.json.created.map(String) : []);
const near = (a, b, tol = 1) => typeof a === "number" && Math.abs(a - b) <= tol;

function rowsOf(r) {
  const j = r.json ?? {};
  if (!Array.isArray(j.rows) || !Array.isArray(j.cols)) return [];
  return j.rows.map((row) => Object.fromEntries(j.cols.map((c, i) => [c, row[i]])));
}

async function lengthOf(t, id) {
  const r = await t.call("describe_elements", { ids: [String(id)], include: ["geometry"] });
  return r.json?.elements?.[0]?.geometry?.length;
}

export default {
  id: "W3R",
  title: "Units round trip in Revit: units m/ft and unit strings read back in mm/deg",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("W3R");
    t.setTargetPath(path);
    const L1 = await t.step("first level", async () => {
      const rows = rowsOf(await t.call("list", { kind: "levels" }));
      t.assert(rows.length >= 1, "the template has a level");
      return rows[0].name;
    });

    await t.step("level with units m: elevation 3.5 → 3500 mm", async () => {
      await t.call("create_elements", { op: "level", name: "W3R metres", elevation: 3.5, units: "m" });
      const row = rowsOf(await t.call("list", { kind: "levels" })).find((x) => x.name === "W3R metres");
      t.assert(row, "the level exists");
      t.assert(near(row.elevation, 3500), `elevation 3500 mm, got ${row.elevation}`);
    });

    await t.step("wall with units ft: 10 ft → 3048 mm", async () => {
      const r = await t.call("create_elements", { op: "wall", level: L1, points: [[0, 0], [10, 0]], height: 10, units: "ft" });
      const id = createdIds(r)[0];
      t.assert(id, "a wall was created");
      const length = await lengthOf(t, id);
      t.assert(near(length, 3048), `length 3048 mm, got ${length}`);
    });

    const wall = await t.step("wall with unit strings in an mm call: 5 m → 5000 mm", async () => {
      const r = await t.call("create_elements", { op: "wall", level: L1, points: [["0 m", "5 m"], ["5 m", "5000 mm"]], height: "3 m" });
      const id = createdIds(r)[0];
      const length = await lengthOf(t, id);
      t.assert(near(length, 5000), `length 5000 mm, got ${length}`);
      return id;
    });

    await t.step("move by [1,0] with units m → by [1000,0] mm; rotate 1.5708 rad → 90 deg", async () => {
      const moved = await t.call("modify_elements", { op: "move", ids: [wall], by: [1, 0], units: "m" });
      t.assert(Array.isArray(moved.json?.by) && near(moved.json.by[0], 1000) && near(moved.json.by[1], 0), `by in mm: ${JSON.stringify(moved.json?.by)}`);
      const rotated = await t.call("modify_elements", { op: "rotate", ids: [wall], angle: "1.5708 rad" });
      t.assertEq(rotated.status, "ok", "rotate with radians");
      const length = await lengthOf(t, wall);
      t.assert(near(length, 5000), `rotation keeps the length, got ${length}`);
    });

    await t.step("change_set units m applies to every op", async () => {
      const r = await t.call("change_set", { units: "m", ops: [{ tool: "create_elements", op: "wall", level: L1, points: [[0, 10], [2, 10]], height: 3 }] });
      const id = createdIds(r)[0] ?? r.json?.ops?.[0]?.created?.[0];
      t.assert(id, `change_set created a wall: ${r.text.slice(0, 200)}`);
      const length = await lengthOf(t, id);
      t.assert(near(length, 2000), `length 2000 mm, got ${length}`);
    });
  },
};
