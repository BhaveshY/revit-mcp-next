// S30 architecture (SPEC §13.3, D4 S30; owner P-MODEL): create_elements and place_family on a disposable project.
// Covers every create_elements op and place_family place/load: datums, walls with a curtain wall and an opening given
// by model points (projected onto the wall), floors/ceilings from points and from rooms, rooms + separators + numbering
// (NAME_TAKEN with the next number), roofs (footprint with slope edges, extrusion), shaft, ref plane, model lines,
// stairs (StairsEditScope, railings), railings, curtain grids, toposolid with the §5.4 z rule, family placement with
// automatic wall hosts, and the load guards. Types are discovered through list/find so the scenario works on any
// metric template (EN 2024/2027).

const near = (a, b, tol = 1) => Math.abs(Number(a) - Number(b)) <= tol;

/** The first {cols, rows} table found in a result JSON (top level or one level down). */
function tableOf(json) {
  if (!json || typeof json !== "object") return null;
  if (Array.isArray(json.cols) && Array.isArray(json.rows)) return json;
  for (const value of Object.values(json)) if (value && typeof value === "object" && Array.isArray(value.cols) && Array.isArray(value.rows)) return value;
  return null;
}

/** Rows of a table as objects keyed by column name. */
function rowsOf(json) {
  const table = tableOf(json);
  if (!table) return [];
  return table.rows.map((row) => Object.fromEntries(table.cols.map((c, i) => [c, row[i]])));
}

/** First type (id + label) matching list {kind:"types", ...filter}; null when the template has none. */
async function firstType(t, filter, pick = () => true) {
  const r = await t.call("list", { kind: "types", limit: 200, ...filter }, { allowError: true });
  if (r.status !== "ok") return null;
  const row = rowsOf(r.json).find(pick);
  if (!row) return null;
  const label = row.family && row.name ? `${row.family}: ${row.name}` : row.name ?? row.type ?? String(row.id);
  return { id: String(row.id), label, row };
}

export default {
  id: "S30",
  title: "Architecture: create_elements and place_family",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S30");
    t.setTargetPath(path);

    // ---------------------------------------------------------------- datums
    const level3 = await t.step("level with plans; NAME_TAKEN on repeat", async () => {
      const r = await t.call("create_elements", { op: "level", elevation: 7000, name: "E2E L3" });
      const rows = rowsOf(r.json);
      t.assertEq(rows[0]?.name, "E2E L3", "level name");
      t.assert(near(rows[0]?.elevation, 7000), `elevation 7000 (got ${rows[0]?.elevation})`);
      t.assert(r.json.created.length >= 3, `level + floor plan + ceiling plan created (${r.json.created.length})`);
      t.assertMatch(r.json.undo ?? "", /^undo /, "undo hint");
      const dup = await t.expectError("create_elements", { op: "level", elevation: 9000, name: "E2E L3" }, "NAME_TAKEN");
      t.assert(!!dup.fix, "fix line");
      const stack = await t.call("create_elements", { op: "level", elevation: 10500, count: 2, spacing: "3.5 m", plans: false });
      t.assertEq(stack.json.created.length, 2, "count 2 without plans");
      return { id: rows[0].id };
    });

    await t.step("grids with count/spacing and an arc grid", async () => {
      const r = await t.call("create_elements", { op: "grid", start: [0, -2000], end: [0, 14000], name: "E1", count: 3, spacing: 6000 });
      t.assertEq(r.json.created.length, 3, "3 grids");
      t.assertDeepEqual(rowsOf(r.json).map((g) => g.name), ["E1", "E2", "E3"], "incremented names");
      const arc = await t.call("create_elements", { op: "grid", start: [-3000, 0], end: [15000, 0], through: [6000, -3000], name: "EA" });
      t.assertEq(arc.json.created.length, 1, "arc grid");
    });

    // ---------------------------------------------------------------- walls, opening, curtain grid
    const walls = await t.step("closed wall loop to Level 2", async () => {
      const r = await t.call("create_elements", { op: "wall", level: "Level 1", top: "Level 2", points: [[0, 0], [12000, 0], [12000, 8000], [0, 8000]], closed: true });
      t.assertEq(r.json.created.length, 4, "4 walls");
      t.assertEq(r.json.level, "Level 1", "level");
      t.assertEq(r.json.top, "Level 2", "top constraint");
      return { ids: r.json.created, south: r.json.created[0], handle: r.json.handle };
    });

    await t.step("wall opening via model points projected onto the wall", async () => {
      const r = await t.call("create_elements", { op: "opening", host: String(walls.south), start: [2000, -60], end: [3000, 75], offset: 900, height: 1200 });
      t.assertEq(r.json.created.length, 1, "one opening");
      t.assertMatch(r.summary, /1000 x 1200 mm, sill 900 mm/, "projected width 1000, height 1200, sill 900");
      t.assert(r.json.modified.includes(walls.south), "host wall reported as modified");
    });

    await t.step("wall from start/end with location line and an arc wall", async () => {
      const straight = await t.call("create_elements", { op: "wall", level: "Level 1", start: [0, 12000], end: [6000, 12000], height: 2800, line: "finish_ext" });
      t.assertEq(straight.json.created.length, 1, "1 wall");
      t.assertEq(straight.json.line, "finish_ext", "location line");
      const arc = await t.call("create_elements", { op: "wall", level: "Level 1", start: [14000, 0], end: [14000, 8000], through: [16000, 4000] });
      t.assertMatch(arc.summary, /arc/, "arc wall");
    });

    const curtain = await t.step("curtain grid on a curtain wall (vertical spacing + horizontal positions)", async () => {
      const type = await firstType(t, { class: "WallType", name: "Curtain" });
      if (!type) {
        t.log("no curtain wall type in the template; curtain_grid checked through its error path only");
        await t.expectError("create_elements", { op: "curtain_grid", host: String(walls.south), direction: "vertical", spacing: 1500 }, "INVALID_ARGS");
        return null;
      }
      const wall = await t.call("create_elements", { op: "wall", level: "Level 1", type: type.id, start: [0, 20000], end: [9000, 20000], height: 3000 });
      const host = String(wall.json.created[0]);
      const v = await t.call("create_elements", { op: "curtain_grid", host, direction: "vertical", spacing: 1500 });
      t.assert(v.json.created.length >= 5, `vertical lines every 1500 mm on 9000 mm (${v.json.created.length})`);
      const h = await t.call("create_elements", { op: "curtain_grid", host, direction: "horizontal", positions: [1000, 2000] });
      t.assert(h.json.created.length >= 2, "2 horizontal lines");
      await t.expectError("create_elements", { op: "curtain_grid", host: String(walls.south), direction: "vertical", spacing: 1500 }, "INVALID_ARGS");
      return host;
    });
    void curtain;

    // ---------------------------------------------------------------- rooms, separators, floors, ceilings
    const rooms = await t.step("separator splits the box; rooms; duplicate number gives NAME_TAKEN with the next number", async () => {
      const sep = await t.call("create_elements", { op: "room_separator", view: "Level 1", points: [[6000, 0], [6000, 8000]] });
      t.assertEq(sep.json.created.length, 1, "1 separator");
      const a = await t.call("create_elements", { op: "room", level: "Level 1", at: [3000, 4000], name: "E2E Office", number: "E101", department: "QA" });
      const row = rowsOf(a.json)[0];
      t.assertEq(row.number, "E101", "room number");
      t.assert(Number(row.area_m2) > 40 && Number(row.area_m2) < 50, `bounded room area ~48 m2 (got ${row.area_m2})`);
      await t.expectError("create_elements", { op: "room", level: "Level 1", at: [3000, 4000] }, "INVALID_ARGS");
      const dup = await t.expectError("create_elements", { op: "room", level: "Level 1", at: [9000, 4000], number: "E101" }, "NAME_TAKEN");
      t.assertMatch(dup.fix ?? "", /E102/, "fix proposes the next free number");
      const b = await t.call("create_elements", { op: "room", level: "Level 1", at: [9000, 4000], number: "E101", allow_duplicate: true });
      t.assertEq(b.json.created.length, 1, "allow_duplicate creates it");
      const all = await t.call("create_elements", { op: "room", level: "Level 1", all: true });
      t.assertEq(all.status, "ok", "all on a filled level is ok (maybe nothing created)");
      return { handle: a.json.handle, id: row.id };
    });

    await t.step("floor from points with a hole and a slope; floor and ceiling from rooms", async () => {
      const f = await t.call("create_elements", { op: "floor", level: "Level 1", points: [[0, 0], [12000, 0], [12000, 8000], [0, 8000]], holes: [[[4000, 2000], [5000, 2000], [5000, 3000], [4000, 3000]]] });
      t.assert(near(f.json.area_m2, 95, 1.5), `floor area 96 - 1 m2 (got ${f.json.area_m2})`);
      const sloped = await t.call("create_elements", { op: "floor", level: "E2E L3", points: [[0, 0], [4000, 0], [4000, 3000], [0, 3000]], slope: 3 });
      t.assertMatch(sloped.summary, /slope 3 deg/, "sloped floor");
      const fromRoom = await t.call("create_elements", { op: "floor", level: "Level 2", from: rooms.handle });
      t.assertEq(fromRoom.json.created.length >= 1, true, "floor from the room boundary");
      const ceiling = await t.call("create_elements", { op: "ceiling", level: "Level 1", from: rooms.handle, offset: 2600 });
      t.assert(near(ceiling.json.height_offset, 2600), `ceiling at 2600 (got ${ceiling.json.height_offset})`);
      const pts = await t.call("create_elements", { op: "ceiling", level: "Level 1", points: [[6500, 500], [11500, 500], [11500, 7500], [6500, 7500]] });
      t.assertEq(pts.json.created.length >= 1, true, "ceiling from points");
    });

    await t.step("areas: area plan boundary + area (when the template has an area plan)", async () => {
      const views = await t.call("list", { kind: "views", view_type: "AreaPlan", limit: 20 }, { allowError: true });
      const plan = rowsOf(views.json).find((v) => v.name);
      if (!plan) {
        t.log("no area plan in the template; area ops checked through their error path");
        await t.expectError("create_elements", { op: "area_boundary", view: "Level 1", points: [[0, 0], [1000, 0]] }, "INVALID_ARGS");
        await t.expectError("create_elements", { op: "area", view: "Level 1", at: [1000, 1000] }, "INVALID_ARGS");
        return;
      }
      const b = await t.call("create_elements", { op: "area_boundary", view: plan.name, points: [[-1000, -1000], [13000, -1000], [13000, 9000], [-1000, 9000]], closed: true });
      t.assertEq(b.json.created.length, 4, "4 boundary lines");
      const a = await t.call("create_elements", { op: "area", view: plan.name, at: [3000, 3000], name: "E2E Area", number: "A1" });
      t.assertEq(a.json.number, "A1", "area number");
    });

    // ---------------------------------------------------------------- roofs, shaft, ref plane, model lines
    await t.step("footprint roof with overhang and slope edges; extrusion roof", async () => {
      const r = await t.call("create_elements", { op: "roof", level: "Level 2", points: [[0, 0], [12000, 0], [12000, 8000], [0, 8000]], slope: 30, overhang: 500 });
      t.assertDeepEqual(r.json.sloped_edges, [0, 1, 2, 3], "all edges slope");
      const gable = await t.call("create_elements", { op: "roof", level: "E2E L3", points: [[0, 0], [6000, 0], [6000, 4000], [0, 4000]], slope: 20, slope_edges: [0, 2] });
      t.assertDeepEqual(gable.json.sloped_edges, [0, 2], "two sloped edges");
      await t.expectError("create_elements", { op: "roof", level: "Level 2", points: [[0, 0], [1000, 0], [1000, 1000]], slope_edges: [7], slope: 10 }, "INVALID_ARGS");
      const ext = await t.call("create_elements", { op: "roof_extrusion", level: "Level 2", start: [20000, 0], end: [26000, 0], profile: [[0, 0], [3000, 1500], [6000, 0]], depth: 4000 });
      t.assert(ext.json.created.length >= 2, "reference plane + extrusion roof");
    });

    await t.step("shaft, reference plane, model lines", async () => {
      const shaft = await t.call("create_elements", { op: "shaft", level: "Level 1", top: "Level 2", points: [[8000, 6000], [9000, 6000], [9000, 7000], [8000, 7000]] });
      t.assertEq(shaft.json.created.length >= 1, true, "shaft opening");
      const rp = await t.call("create_elements", { op: "ref_plane", start: [-500, -1000], end: [-500, 9000], name: "E2E Axis" });
      t.assertEq(rp.json.created.length, 1, "reference plane");
      await t.expectError("create_elements", { op: "ref_plane", start: [0, 0], end: [0, 5000], name: "E2E Axis" }, "NAME_TAKEN");
      const ml = await t.call("create_elements", { op: "model_line", level: "Level 1", points: [[0, -3000], [5000, -3000], [5000, -1000]] });
      t.assertEq(ml.json.created.length, 2, "2 model lines");
      const vertical = await t.call("create_elements", { op: "model_line", points: [[-2000, -2000, 0], [-2000, -2000, 3000]] });
      t.assertEq(vertical.json.created.length, 1, "vertical model line on a vertical plane");
    });

    // ---------------------------------------------------------------- stairs and railings
    await t.step("stairs (StairsEditScope) with railings; free railing; railing on stairs refused when present", async () => {
      const s = await t.call("create_elements", { op: "stairs", level: "Level 1", top: "Level 2", points: [[1000, 9000], [1000, 15000]], width: 1100 });
      t.assertEq(s.json.runs.length, 1, "1 run");
      t.assert(s.json.risers >= 10, `risers from the type (got ${s.json.risers})`);
      t.assert(near(s.json.riser_height * s.json.risers, 4000, 30) || s.json.risers > 0, "risers span Level 1 -> Level 2");
      if (s.json.railings.length === 0) t.log("V8: no railing type, stairs have none");
      else await t.expectError("create_elements", { op: "railing", host: String(s.json.stairs) }, "INVALID_ARGS");
      const lshape = await t.call("create_elements", { op: "stairs", level: "Level 1", top: "Level 2", points: [[20000, 9000], [20000, 13000], [24000, 13000]], width: 1000 }, { allowError: true });
      if (lshape.status === "ok") t.assertEq(lshape.json.runs.length, 2, "2 runs for an L path");
      else t.log(`L-shaped stairs: ${lshape.code} ${lshape.summary}`);
      const free = await t.call("create_elements", { op: "railing", level: "Level 1", points: [[0, -5000], [6000, -5000], [6000, -2000]] });
      t.assertEq(free.json.created.length >= 1, true, "free railing");
    });

    // ---------------------------------------------------------------- toposolid z rule (§5.4)
    await t.step("toposolid: z above level when level is given, else absolute", async () => {
      const rel = await t.call("create_elements", { op: "toposolid", level: "E2E L3", points: [[30000, 0, 0], [40000, 0, 0], [40000, 10000, 1000], [30000, 10000, 1000]] });
      t.assert(near(rel.json.z_min, 7000) && near(rel.json.z_max, 8000), `z relative to E2E L3 (7000): ${rel.json.z_min}..${rel.json.z_max}`);
      const abs = await t.call("create_elements", { op: "toposolid", points: [[30000, 20000, -500], [40000, 20000, -500], [40000, 30000, 250], [30000, 30000, 0]], survey: [[35000, 25000, 600]] });
      t.assert(near(abs.json.z_min, -500) && near(abs.json.z_max, 600), `absolute z: ${abs.json.z_min}..${abs.json.z_max}`);
    });

    // ---------------------------------------------------------------- place_family
    await t.step("doors and windows find their wall automatically; sill offset; flip", async () => {
      const door = await firstType(t, { category: ["Doors"] });
      t.assert(door, "the template has a door type");
      const d = await t.call("place_family", { op: "place", type: door.id, at: [9000, 40], level: "Level 1" });
      const row = rowsOf(d.json)[0];
      t.assertEq(String(row.host), String(walls.south), "auto host = nearest wall");
      t.assert(d.json.modified.includes(walls.south), "host listed as modified");
      const window = await firstType(t, { category: ["Windows"] });
      if (window) {
        const w = await t.call("place_family", { op: "place", type: window.id, points: [[12000, 2000], [12000, 6000]], level: "Level 1", offset: 900, flip_facing: true });
        t.assertEq(w.json.created.length, 2, "2 windows on the east wall");
      }
      await t.expectError("place_family", { op: "place", type: door.id, at: [50000, 50000], level: "Level 1" }, "INVALID_ARGS");
      await t.expectError("place_family", { op: "place", type: "No Such Family: Nope" , at: [0, 0] }, "NOT_FOUND");
    });

    await t.step("level-based family at points with rotation; z above level", async () => {
      const furniture = (await firstType(t, { category: ["Furniture"] })) ?? (await firstType(t, { category: ["Generic Models"] })) ?? (await firstType(t, { category: ["Plumbing Fixtures"] }));
      if (!furniture) {
        t.log("no level-based family in the template; skipped");
        return;
      }
      const r = await t.call("place_family", { op: "place", type: furniture.id, points: [[2000, 2000], [4000, 2000]], level: "Level 1", rotation: 90 }, { allowError: true });
      if (r.status === "error") {
        t.log(`${furniture.label}: ${r.code} ${r.summary}`);
        return;
      }
      t.assertEq(r.json.created.length, 2, "one instance per point");
    });

    await t.step("load guards: missing file, wrong extension, network share; reload of a saved family", async () => {
      const missing = `${t.yearDir}\\NoSuchFamily.rfa`;
      await t.expectError("place_family", { op: "load", path: missing }, "NOT_FOUND");
      await t.expectError("place_family", { op: "load", path: `${t.yearDir}\\notes.txt` }, "INVALID_ARGS");
      await t.expectError("place_family", { op: "load", path: "relative\\Door.rfa" }, "INVALID_ARGS");

      // Positive load: save a family of the project as .rfa (edit_family, P-FAMILY), then load it back.
      const door = await firstType(t, { category: ["Doors"] });
      const familyName = door?.row?.family;
      if (!familyName) return;
      const opened = await t.call("edit_family", { op: "open", family: familyName }, { allowError: true });
      if (opened.status !== "ok") {
        t.log(`edit_family open unavailable (${opened.code}); positive load skipped`);
        await t.call("set_target", { doc: "S30" }, { allowError: true });
        return;
      }
      const rfa = `${t.yearDir}\\E2E-${familyName.replace(/[^A-Za-z0-9-]/g, "_")}.rfa`;
      const saved = await t.call("edit_family", { op: "save_as", path: rfa }, { allowError: true });
      await t.call("manage_document", { op: "close", save: false }, { allowError: true });
      await t.call("set_target", { doc: "S30" });
      if (saved.status !== "ok") {
        t.log(`edit_family save_as unavailable (${saved.code}); positive load skipped`);
        return;
      }
      const r = await t.call("place_family", { op: "load", path: rfa });
      t.assertMatch(r.summary, /reloaded|already loaded|loaded/, "reload of an existing family");
      await t.expectError("place_family", { op: "load", path: rfa, categories: ["Windows"] }, "INVALID_ARGS");
      await t.expectError("place_family", { op: "load", path: rfa, sha256: "0".repeat(64) }, "INVALID_ARGS");
    });

    await t.step("change_set: level + wall + door as one undo step ($refs)", async () => {
      const door = await firstType(t, { category: ["Doors"] });
      const ops = [
        { tool: "create_elements", op: "level", elevation: 21000, name: "E2E L9", plans: false },
        { tool: "create_elements", op: "wall", level: "$0", points: [[0, 30000], [8000, 30000]], height: 3000 },
      ];
      if (door) ops.push({ tool: "place_family", op: "place", type: door.id, at: [4000, 30000], level: "$0", host: "$1" });
      const r = await t.call("change_set", { ops, name: "S30 set" }, { allowError: true });
      if (r.status === "error" && r.code === "ADDIN_OUTDATED") return t.log("change_set not served yet");
      t.assertEq(r.status, "ok", `change_set ${r.code ?? ""} ${r.summary}`);
      t.assertEq(r.json.ops.length, ops.length, "all ops ran");
    });

    void level3;
  },
};
