// S40 modify (SPEC §5.9, §13.3; owner P-MODIFY). Needs Revit with the add-in loaded from the e2e home.
// Every modify_elements op on a disposable project, the split rules (a door spanning the split point is refused; after a
// split the door count is unchanged and each half keeps its door), attach on 2027 / UNSUPPORTED_VERSION on 2024,
// set_parameters (rows, {n}, {Param}, on:type, project_info) and every edit_types op incl. NAME_TAKEN.

const createdIds = (r) => (Array.isArray(r.json?.created) ? r.json.created.map(String) : []);
const firstCreated = (r) => createdIds(r)[0];
const totalOf = (r) => r.json?.total ?? r.json?.count ?? Number(/(\d[\d,]*)/.exec(r.summary ?? "")?.[1]?.replace(/,/g, "") ?? NaN);

function rowsOf(r) {
  const j = r.json ?? {};
  const table = Array.isArray(j.rows) && Array.isArray(j.cols) ? j : j.table ?? j;
  if (!Array.isArray(table.rows) || !Array.isArray(table.cols)) return [];
  return table.rows.map((row) => Object.fromEntries(table.cols.map((c, i) => [c, row[i]])));
}

/** "Family: Type" names of the element types of a category. */
async function typeNames(t, category) {
  const r = await t.call("find_elements", { category: [category], types: true, fields: ["family", "name"], limit: 100 });
  return rowsOf(r)
    .map((x) => (x.family && (x.name || x.type) ? `${x.family}: ${x.name ?? x.type}` : x.name ?? x.type))
    .filter((name) => typeof name === "string" && name.length > 0);
}

async function levels(t) {
  const r = await t.call("find_elements", { category: ["Levels"], fields: ["name", "elevation"], limit: 50 });
  const rows = rowsOf(r).filter((x) => x.name);
  rows.sort((a, b) => (Number(a.elevation) || 0) - (Number(b.elevation) || 0));
  return rows;
}

async function doorCount(t) {
  return totalOf(await t.call("find_elements", { category: ["Doors"], count_only: true }));
}

export default {
  id: "S40",
  title: "Modify: transforms, split keeps inserts, joins/attach, groups, set_parameters, edit_types",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S40");
    t.setTargetPath(path);
    let lv = await levels(t);
    if (lv.length < 2) {
      await t.call("create_elements", { op: "level", elevation: 4000, name: "S40 Level 2" });
      lv = await levels(t);
    }
    const L1 = lv[0].name;
    const L2 = lv[1].name;
    const allWallTypes = await typeNames(t, "Walls");
    const basic = allWallTypes.filter((n) => /^Basic Wall:/i.test(n));
    const wallTypes = basic.length >= 2 ? basic : allWallTypes;
    const doorTypes = await typeNames(t, "Doors");
    t.assert(wallTypes.length >= 2 && doorTypes.length >= 1, `template has wall types (${wallTypes.length}) and door types (${doorTypes.length})`);
    const doorType = doorTypes[0];

    const base = await t.step("wall 10 m with two doors at 2.5 m and 7.5 m", async () => {
      const wall = await t.call("create_elements", { op: "wall", level: L1, start: [0, 0], end: [10000, 0], top: L2, type: wallTypes[0] });
      const id = firstCreated(wall);
      t.assert(id, "wall created");
      const doors = await t.call("place_family", { op: "place", type: doorType, points: [[2500, 0], [7500, 0]], level: L1, host: id });
      t.assertEq(createdIds(doors).length >= 2, true, "two doors placed");
      return { wall: id, doors: createdIds(doors).slice(0, 2) };
    });
    const doorsBefore = await doorCount(t);

    await t.step("split through a door → UNSUPPORTED_OP, nothing changed", async () => {
      const r = await t.expectError("modify_elements", { op: "split", ids: [base.wall], at: [2500, 0] }, "UNSUPPORTED_OP");
      t.assertMatch(r.summary, /spans the split point/, "names the spanning insert");
      t.assertEq(await doorCount(t), doorsBefore, "door count unchanged");
    });

    const halves = await t.step("split between the doors keeps every door on its half", async () => {
      const r = await t.call("modify_elements", { op: "split", ids: [base.wall], at: [5000, 0] });
      t.assertEq(r.status, "ok", "split ok");
      const pair = r.json.split?.[0];
      t.assert(Array.isArray(pair) && String(pair[0]) === String(base.wall), "first half is the original wall");
      t.assertEq(await doorCount(t), doorsBefore, "insert count unchanged after the split");
      const second = String(pair[1]);
      // The door on the new half is the copy's duplicate: split reports [old, new] so callers keep working ids.
      const replaced = new Map((r.json.replaced ?? []).map(([o, n]) => [String(o), String(n)]));
      t.assert(replaced.has(String(base.doors[1])), `split reports the replaced door id (${JSON.stringify(r.json.replaced ?? null)})`);
      base.doors = base.doors.map((d) => replaced.get(String(d)) ?? d);
      const hosts = await t.call("find_elements", { category: ["Doors"], fields: ["host"], limit: 50 });
      const byHost = rowsOf(hosts).reduce((m, x) => ((m[String(x.host)] = (m[String(x.host)] ?? 0) + 1), m), {});
      t.assert((byHost[String(base.wall)] ?? 0) >= 1 && (byHost[second] ?? 0) >= 1, `each half hosts a door: ${JSON.stringify(byHost)}`);
      return { first: String(base.wall), second };
    });

    await t.step("move by / to, rotate, mirror (copy and in place)", async () => {
      const moved = await t.call("modify_elements", { op: "move", ids: [halves.second], by: [0, 1000] });
      t.assertEq(moved.json.moved, 1, "moved 1");
      const back = await t.call("modify_elements", { op: "move", ids: [halves.second], to: [5000, 0] });
      t.assertDeepEqual(back.json.by.slice(0, 2), [0, -1000], "to moves the location start back");
      const solo = firstCreated(await t.call("create_elements", { op: "wall", level: L1, start: [0, -10000], end: [4000, -10000], height: 3000 }));
      const copy = await t.call("modify_elements", { op: "copy", ids: [solo], by: [0, 6000], count: 2 });
      t.assert(createdIds(copy).length >= 2, "two copies");
      const rotated = await t.call("modify_elements", { op: "rotate", ids: [firstCreated(copy)], angle: 90, center: [0, 6000] });
      t.assertEq(rotated.json.rotated, 1, "rotated 1");
      const mirrored = await t.call("modify_elements", { op: "mirror", ids: [firstCreated(copy)], start: [-5000, 0], end: [-5000, 10000] });
      t.assert(createdIds(mirrored).length >= 1, "mirror kept the original and created a copy");
      const inPlace = await t.call("modify_elements", { op: "mirror", ids: [createdIds(copy)[1]], start: [0, 9000], end: [10000, 9000], keep: false });
      t.assertEq(inPlace.json.kept_original, false, "mirrored in place");
    });

    await t.step("array linear and radial", async () => {
      const wall = await t.call("create_elements", { op: "wall", level: L1, start: [20000, 0], end: [23000, 0], height: 3000 });
      const linear = await t.call("modify_elements", { op: "array", ids: [firstCreated(wall)], count: 4, by: [0, 2000] });
      t.assertEq(createdIds(linear).length, 3, "count includes the original: 3 new");
      const radial = await t.call("modify_elements", { op: "array", ids: [firstCreated(wall)], count: 4, angle: 90, center: [20000, 0], kind: "radial" });
      t.assertEq(createdIds(radial).length, 3, "radial: 3 new");
    });

    await t.step("copy to a level re-associates the base level", async () => {
      const solo = firstCreated(await t.call("create_elements", { op: "wall", level: L1, start: [0, -20000], end: [4000, -20000], height: 3000 }));
      const r = await t.call("modify_elements", { op: "copy", ids: [solo], levels: [L2] });
      t.assert(createdIds(r).length >= 1, "copied to the level");
      const desc = await t.call("find_elements", { ids: [firstCreated(r)], fields: ["level"] });
      t.assertEq(rowsOf(desc)[0]?.level, L2, "copy sits on the target level");
    });

    await t.step("align to a grid (parallel move) and to nothing parallel → skipped", async () => {
      await t.call("create_elements", { op: "grid", start: [30000, -2000], end: [30000, 12000], name: "S40A" });
      const wall = await t.call("create_elements", { op: "wall", level: L1, start: [29500, 0], end: [29500, 5000], height: 3000 });
      const r = await t.call("modify_elements", { op: "align", ids: [firstCreated(wall)], target: "S40A" });
      t.assertEq(r.json.aligned, 1, "aligned 1");
      t.assertDeepEqual(r.json.modified.map(String), [firstCreated(wall)], "the wall moved");
    });

    await t.step("pin, ELEMENTS_PINNED, allow_pinned, unpin", async () => {
      await t.call("modify_elements", { op: "pin", ids: [halves.first] });
      await t.expectError("modify_elements", { op: "rotate", ids: [halves.first], angle: 5 }, "ELEMENTS_PINNED");
      const allowed = await t.call("modify_elements", { op: "move", ids: [halves.first], by: [0, 10], allow_pinned: true });
      t.assertEq(allowed.json.moved, 1, "allow_pinned moves");
      const unpin = await t.call("modify_elements", { op: "pin", ids: [halves.first], pinned: false });
      t.assertEq(unpin.json.unpinned, 1, "unpinned");
      await t.call("modify_elements", { op: "move", ids: [halves.first], by: [0, -10] });
    });

    await t.step("change_type, flip door, reshape", async () => {
      const r = await t.call("modify_elements", { op: "change_type", ids: [halves.second], type: wallTypes[1] });
      t.assertEq(r.json.changed, 1, "type changed");
      const flipped = await t.call("modify_elements", { op: "flip", ids: [base.doors[0]], side: "hand" });
      t.assertEq(flipped.json.flipped, 1, "door flipped");
      const wall = await t.call("create_elements", { op: "wall", level: L1, start: [0, 30000], end: [4000, 30000], height: 3000 });
      const reshaped = await t.call("modify_elements", { op: "reshape", ids: [firstCreated(wall)], start: [0, 30000], end: [8000, 30000] });
      t.assertEq(reshaped.json.length, 8000, "new length 8000 mm");
      const arc = await t.call("modify_elements", { op: "reshape", ids: [firstCreated(wall)], start: [0, 30000], end: [8000, 30000], through: [4000, 32000] });
      t.assertEq(arc.json.arc, true, "arc");
    });

    await t.step("join / switch_join / unjoin two crossing walls", async () => {
      const a = await t.call("create_elements", { op: "wall", level: L1, start: [40000, 0], end: [44000, 0], height: 3000 });
      const b = await t.call("create_elements", { op: "wall", level: L1, start: [42000, -2000], end: [42000, 2000], height: 3000 });
      const joined = await t.call("modify_elements", { op: "join", ids: [firstCreated(a)], with: firstCreated(b) }, { allowError: true });
      t.assert(joined.status === "ok" || joined.code === "UNSUPPORTED_OP", `join ${joined.status} ${joined.code ?? ""}`);
      if (joined.status === "ok") {
        await t.call("modify_elements", { op: "switch_join", ids: [firstCreated(a)], with: firstCreated(b) });
        const un = await t.call("modify_elements", { op: "unjoin", ids: [firstCreated(a)], with: firstCreated(b) });
        t.assertEq(un.status, "ok", "unjoined");
      }
    });

    await t.step("cut / uncut a floor with an overlapping wall (Revit may refuse solid cuts between host elements)", async () => {
      const floor = firstCreated(await t.call("create_elements", { op: "floor", level: L1, points: [[60000, -2000], [64000, -2000], [64000, 2000], [60000, 2000]] }));
      const wall = firstCreated(await t.call("create_elements", { op: "wall", level: L1, start: [59000, 0], end: [65000, 0], height: 3000 }));
      const cut = await t.call("modify_elements", { op: "cut", ids: [floor], with: wall }, { allowError: true });
      t.assert(cut.status === "ok" || cut.code === "UNSUPPORTED_OP", `cut: ${cut.status} ${cut.code ?? ""} ${cut.summary.slice(0, 160)}`);
      if (cut.status !== "ok") {
        t.log(`Revit refused the solid cut: ${cut.summary.slice(0, 160)}`);
        await t.expectError("modify_elements", { op: "cut", ids: [floor], with: wall }, "UNSUPPORTED_OP");
      }
      const uncut = await t.call("modify_elements", { op: "uncut", ids: [floor], with: wall });
      t.assertEq(uncut.status, "ok", "uncut (or nothing to uncut)");
    });

    await t.step(t.year >= 2027 ? "attach wall top to a floor (2027)" : "attach → UNSUPPORTED_VERSION on 2024", async () => {
      const wall = await t.call("create_elements", { op: "wall", level: L1, start: [50000, 0], end: [54000, 0], height: 2000 });
      if (t.year < 2027) {
        const r = await t.expectError("modify_elements", { op: "attach", ids: [firstCreated(wall)], with: firstCreated(wall), side: "top" }, "UNSUPPORTED_VERSION");
        t.assert(!!r.fix, "UNSUPPORTED_VERSION has a fix");
        return;
      }
      const floor = await t.call("create_elements", { op: "floor", level: L2, points: [[49000, -2000], [55000, -2000], [55000, 2000], [49000, 2000]] });
      const r = await t.call("modify_elements", { op: "attach", ids: [firstCreated(wall)], with: firstCreated(floor), side: "top" });
      t.assertEq(r.json.attached, 1, "attached");
      const d = await t.call("modify_elements", { op: "detach", ids: [firstCreated(wall)], side: "top" });
      t.assertEq(d.json.detached, 1, "detached");
    });

    await t.step("group, place_group, ungroup", async () => {
      const a = await t.call("create_elements", { op: "wall", level: L1, start: [60000, 0], end: [63000, 0], height: 3000 });
      const b = await t.call("create_elements", { op: "wall", level: L1, start: [63000, 0], end: [63000, 3000], height: 3000 });
      const g = await t.call("modify_elements", { op: "group", ids: [firstCreated(a), firstCreated(b)], name: "S40 Group" });
      t.assertEq(g.json.name, "S40 Group", "group named");
      await t.expectError("modify_elements", { op: "group", ids: [firstCreated(a)], name: "S40 Group" }, ["INVALID_ARGS", "NAME_TAKEN"]);
      const placed = await t.call("modify_elements", { op: "place_group", name: "S40 Group", at: [60000, 10000], level: L1 });
      t.assert(placed.json.group, "group placed");
      const un = await t.call("modify_elements", { op: "ungroup", ids: [String(placed.json.group)] });
      t.assertEq(un.json.ungrouped, 1, "ungrouped");
    });

    await t.step("set_parameters: rows, {n}, {Param}, on:type, project_info, unknown name", async () => {
      const rows = await t.call("set_parameters", { rows: [{ id: base.doors[0], values: { Mark: "S40-D1" } }, { id: base.doors[1], values: { Mark: "S40-D2" } }] });
      t.assertEq(rows.json.set, 2, "rows set");
      const copied = await t.call("set_parameters", { ids: base.doors, values: { Comments: "{Mark} #{n}" } });
      t.assertDeepEqual(copied.json.values.map((x) => x[1].Comments), ["S40-D1 #1", "S40-D2 #2"], "{Param} and {n}");
      const typed = await t.call("set_parameters", { ids: [base.doors[0]], on: "type", values: { "Type Comments": "S40 type" } });
      t.assertEq(typed.json.on, "type", "on type");
      const again = await t.call("set_parameters", { ids: [base.doors[0]], on: "type", values: { "Type Comments": "S40 type" } });
      t.assertEq(again.json.set, 0, "same value again is unchanged");
      const length = await t.call("set_parameters", { ids: [halves.second], values: { "Base Offset": 100 } });
      t.assertEq(length.json.values[0][1]["Base Offset"], 100, "bare numbers are mm");
      const info = await t.call("set_parameters", { ids: ["project_info"], values: { "Project Name": "S40" } });
      t.assertEq(info.status, "ok", "project info");
      await t.expectError("set_parameters", { ids: base.doors, values: { "S40 No Such Param": "x" } }, "NOT_FOUND");
    });

    await t.step("edit_types: duplicate, NAME_TAKEN, rename, set_layers, set_default, delete", async () => {
      const dup = await t.call("edit_types", { op: "duplicate_type", type: wallTypes[0], name: "S40 Wall" });
      t.assertMatch(dup.json.name, /S40 Wall$/, "duplicated");
      const taken = await t.expectError("edit_types", { op: "duplicate_type", type: wallTypes[0], name: "S40 Wall" }, "NAME_TAKEN");
      t.assertMatch(taken.fix ?? "", /S40 Wall 2/, "fix suggests the next free name");
      const renamed = await t.call("edit_types", { op: "rename_type", type: "S40 Wall", name: "S40 Wall 250" });
      t.assertMatch(renamed.json.name, /S40 Wall 250$/, "renamed");
      const mat = await t.call("edit_types", { op: "material_create", name: "S40 Brick", color: "#B5523B", transparency: 10 });
      t.assertEq(mat.json.color, "#B5523B", "material color");
      const edited = await t.call("edit_types", { op: "material_edit", material: "S40 Brick", transparency: 25, surface_pattern: "solid" });
      t.assertEq(edited.json.transparency, 25, "transparency edited");
      const layers = await t.call("edit_types", { op: "set_layers", type: "S40 Wall 250", layers: [
        { function: "finish1", material: "S40 Brick", thickness: 20 },
        { function: "structure", thickness: 210 },
        { function: "finish2", thickness: 20 },
      ] });
      t.assertEq(layers.json.thickness, 250, "total 250 mm");
      const def = await t.call("edit_types", { op: "set_default", category: "Walls", type: "S40 Wall 250" });
      t.assertEq(def.status, "ok", "default set");
      await t.call("edit_types", { op: "set_default", category: "Walls", type: wallTypes[0] });
      const del = await t.call("edit_types", { op: "delete_type", type: "S40 Wall 250" });
      t.assertEq(del.status, "ok", "type deleted");
      const fam = doorType.split(":")[0].trim();
      const rf = await t.call("edit_types", { op: "rename_family", family: fam, name: `${fam} S40` });
      t.assertEq(rf.json.name, `${fam} S40`, "family renamed");
    });

    await t.step("delete with a dependent: a wall and its door", async () => {
      const r = await t.call("modify_elements", { op: "delete", ids: [halves.second] });
      t.assertEq(r.status, "ok", "deleted");
      t.assert((r.json.deleted ?? []).length >= 2, "the hosted door is deleted with the wall");
      t.assert(r.json.dependents >= 1, "dependents counted");
    });
  },
};
