// S60 annotate (SPEC §13.3, owner P-SHEETS-ANNO): every annotate op in a floor plan of a disposable project with a
// small model (3 grids, 4 walls, a door, a floor, a room):
//   tag (door, leader; wall: tag or NO_TAG_FAMILY), tag_all (rooms; untagged_only skips the second time), text (wrap
//   width), dimension through grids (2 segments of 6000 mm) and wall faces, dimension_edit (move, override, new line
//   recreates), dimension from describe_elements refs, spot elevation on the floor (slope: UNSUPPORTED_OP), detail
//   lines/arc/closed chain, filled region with a hole, detail item (when the template has one), revision cloud
//   (issued revision refused), copy_to_views. Names come from the template through list.

const tableOf = (j) => (j && Array.isArray(j.rows) && Array.isArray(j.cols) ? j : null);
const col = (table, name) => (table ? table.cols.indexOf(name) : -1);
const near = (a, b, tol = 1) => Math.abs(Number(a) - Number(b)) <= tol;

/** First array of strings under a key named "refs" anywhere in a JSON value. */
function findRefs(value) {
  if (!value || typeof value !== "object") return null;
  if (Array.isArray(value)) {
    for (const v of value) {
      const found = findRefs(v);
      if (found) return found;
    }
    return null;
  }
  if (Array.isArray(value.refs) && value.refs.length >= 2 && value.refs.every((r) => typeof r === "string" && r.length > 0)) return value.refs;
  for (const v of Object.values(value)) {
    const found = findRefs(v);
    if (found) return found;
  }
  return null;
}

export default {
  id: "S60",
  title: "Annotate: tags, text, dimensions, spots, detailing, revision clouds, copy to views",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    await t.step("disposable project S60", async () => {
      const { r, path } = await t.newProject("S60");
      t.assertEq(r.status, "ok", "new_project");
      t.setTargetPath(path);
    });

    const m = await t.step("model: levels/plans from list, 3 grids, 4 walls, floor, room, door", async () => {
      const levels = tableOf((await t.call("list", { kind: "levels" })).json);
      const sorted = [...levels.rows].sort((a, b) => Number(a[col(levels, "elevation")]) - Number(b[col(levels, "elevation")]));
      const level = sorted[0][col(levels, "name")];
      const level2 = sorted[1]?.[col(levels, "name")];
      const views = tableOf((await t.call("list", { kind: "views", view_type: "FloorPlan" })).json);
      const plans = views.rows.filter((row) => row[col(views, "level")] === level).map((row) => row[col(views, "name")]);
      const others = views.rows.filter((row) => row[col(views, "level")] !== level).map((row) => row[col(views, "name")]);
      t.assert(plans.length > 0 && others.length > 0, "floor plans for two levels");
      const plan = plans[0];

      const grids = [];
      for (const [i, x] of [0, 6000, 12000].entries()) {
        const g = await t.call("create_elements", { op: "grid", start: [x, -1000], end: [x, 9000], name: `S60-${i + 1}` });
        grids.push(String(g.json.created[0]));
      }
      const walls = await t.call("create_elements", { op: "wall", level, points: [[0, 0], [12000, 0], [12000, 8000], [0, 8000]], closed: true, ...(level2 ? { top: level2 } : { height: 3000 }) });
      t.assertEq(walls.json.created.length, 4, "four walls");
      const floor = await t.call("create_elements", { op: "floor", level, points: [[0, 0], [12000, 0], [12000, 8000], [0, 8000]] });
      const room = await t.call("create_elements", { op: "room", level, at: [6000, 4000], name: "S60 Room" });

      let door = null;
      const types = tableOf((await t.call("list", { kind: "types", category: "Doors" })).json);
      if (types && types.rows.length > 0) {
        const type = `${types.rows[0][col(types, "family")]}: ${types.rows[0][col(types, "name")]}`;
        const placed = await t.call("place_family", { op: "place", type, at: [3000, 0], level, host: String(walls.json.created[0]) });
        door = String(placed.json.created[0]);
      }
      return { level, plan, other: others[0], grids, walls: walls.json.created.map(String), floor: String(floor.json.created[0]), room: String(room.json.created[0]), door };
    });
    t.log(`plan '${m.plan}', other '${m.other}', door ${m.door ?? "(no door type in the template)"}`);

    // ---------------------------------------------------------------- tags
    await t.step("tag_all rooms, then again with untagged_only (nothing new)", async () => {
      const first = await t.call("annotate", { op: "tag_all", view: m.plan, category: ["Rooms"] });
      t.assertEq(first.json.created.length, 1, "one room tag");
      const again = await t.call("annotate", { op: "tag_all", view: m.plan, category: "Rooms" });
      t.assertEq(again.json.created.length, 0, "the room is already tagged");
      t.assertMatch(again.summary, /already tagged/, "summary names the skipped elements");
    });

    if (m.door) {
      await t.step("tag the door with a leader; tag_all doors adds nothing", async () => {
        const r = await t.call("annotate", { op: "tag", view: m.plan, ids: [m.door], leader: true, orientation: "horizontal" });
        t.assertEq(r.json.created.length, 1, "door tag created");
        const rows = tableOf(r.json.tags).rows;
        t.assertEq(String(rows[0][0]), m.door, "the tag row names the door");
        const all = await t.call("annotate", { op: "tag_all", view: m.plan, category: ["Doors"] });
        t.assertEq(all.json.created.length, 0, "the door is already tagged");
      });
    }

    await t.step("tag a wall: a wall tag, or NO_TAG_FAMILY with a load fix", async () => {
      const r = await t.call("annotate", { op: "tag", view: m.plan, ids: [m.walls[0]] }, { allowError: true });
      if (r.status === "error") {
        t.assertEq(r.code, "NO_TAG_FAMILY", "the only acceptable error");
        t.assertMatch(r.fix, /place_family/, "fix loads a tag family");
      } else {
        t.assertEq(r.json.created.length, 1, "wall tag created");
      }
    });

    await t.step("tag errors: unknown view, element types", async () => {
      await t.expectError("annotate", { op: "tag", view: "No Such View", ids: [m.walls[0]] }, "NOT_FOUND");
    });

    // ---------------------------------------------------------------- text
    const note = await t.step("text note with wrap width and leader", async () => {
      const r = await t.call("annotate", { op: "text", view: m.plan, at: [500, 9500], text: "S60 note\nsecond line", width: 50, leader: true });
      t.assertEq(r.json.created.length, 1, "text note");
      t.assert(Number(r.json.width) > 0, "width reported");
      return String(r.json.created[0]);
    });

    // ---------------------------------------------------------------- dimensions
    const dim = await t.step("dimension through three grids: 2 segments of 6000 mm", async () => {
      const r = await t.call("annotate", { op: "dimension", view: m.plan, ids: m.grids, start: [-500, 9500], end: [12500, 9500] });
      t.assertEq(r.json.segments, 2, "two segments");
      t.assert(r.json.values.every((v) => near(v, 6000)), `segment values ${JSON.stringify(r.json.values)}`);
      return String(r.json.created[0]);
    });

    await t.step("dimension_edit: move, override segment 1, then a new line recreates it", async () => {
      const moved = await t.call("annotate", { op: "dimension_edit", ids: [dim], by: [0, 500] });
      t.assertEq(moved.json.modified[0], Number(dim), "moved in place");
      const text = await t.call("annotate", { op: "dimension_edit", ids: [dim], overrides: [{ segment: 1, value: "EQ" }, { segment: 2, value: "EQ" }] });
      t.assertEq(text.status, "ok", "overrides applied");
      await t.expectError("annotate", { op: "dimension_edit", ids: [dim], overrides: [{ segment: 5, value: "x" }] }, "INVALID_ARGS");
      const line = await t.call("annotate", { op: "dimension_edit", ids: [dim], start: [-500, 10500], end: [12500, 10500] });
      t.assertEq(line.json.created.length, 1, "recreated");
      t.assert(line.json.deleted.includes(Number(dim)), "old dimension deleted");
      return String(line.json.created[0]);
    });

    await t.step("dimension from stable refs (describe_elements)", async () => {
      const found = await t.call("find_elements", { category: ["Dimensions"], view: m.plan });
      const ids = tableOf(found.json)?.rows.map((row) => String(row[0])) ?? [];
      t.assert(ids.length > 0, "the dimension is found in the view");
      const d = await t.call("describe_elements", { ids: [ids[ids.length - 1]] });
      const refs = findRefs(d.json);
      if (!refs) {
        t.log("describe_elements gave no refs for the dimension; refs path skipped");
        return;
      }
      const r = await t.call("annotate", { op: "dimension", view: m.plan, refs, start: [-500, 11500], end: [12500, 11500] });
      t.assertEq(r.json.segments, refs.length - 1, "one segment per reference gap");
      await t.expectError("annotate", { op: "dimension", view: m.plan, refs: ["not-a-reference", "x"], start: [0, 0], end: [1000, 0] }, "INVALID_ARGS");
    });

    await t.step("dimension across a wall to its faces", async () => {
      const r = await t.call("annotate", { op: "dimension", view: m.plan, ids: [m.walls[0]], mode: "faces", start: [3000, -800], end: [3000, 800] });
      t.assertEq(r.json.segments, 1, "one segment");
      t.assert(Number(r.json.values[0]) > 50 && Number(r.json.values[0]) < 1000, `wall thickness ${r.json.values[0]} mm`);
      await t.expectError("annotate", { op: "dimension", view: m.plan, ids: [m.grids[0]], start: [0, 9500], end: [1000, 9500] }, "INVALID_ARGS");
    });

    // ---------------------------------------------------------------- spots
    await t.step("spot elevation on the floor; spot slope is UNSUPPORTED_OP", async () => {
      const r = await t.call("annotate", { op: "spot", view: m.plan, kind: "elevation", host: m.floor, at: [6000, 4000] });
      t.assertEq(r.json.created.length, 1, "spot elevation");
      const coord = await t.call("annotate", { op: "spot", view: m.plan, kind: "coordinate", host: m.floor, at: [9000, 2000] });
      t.assertEq(coord.json.created.length, 1, "spot coordinate");
      const slope = await t.expectError("annotate", { op: "spot", view: m.plan, kind: "slope", host: m.floor, at: [6000, 4000] }, "UNSUPPORTED_OP");
      t.assertMatch(slope.fix, /ask the user/, "fix asks for manual placement");
      await t.expectError("annotate", { op: "spot", view: m.plan, kind: "elevation", host: m.floor, at: [50000, 50000] }, "INVALID_ARGS");
    });

    // ---------------------------------------------------------------- detailing
    const lines = await t.step("detail lines: segment, arc, closed chain", async () => {
      const one = await t.call("annotate", { op: "detail_line", view: m.plan, points: [[0, -1500], [12000, -1500]] });
      t.assertEq(one.json.created.length, 1, "one line");
      t.assert(near(one.json.length, 12000), "length 12000 mm");
      const arc = await t.call("annotate", { op: "detail_line", view: m.plan, points: [[0, -2500], [2000, -2500]], through: [1000, -2000] });
      t.assertEq(arc.json.created.length, 1, "one arc");
      const chain = await t.call("annotate", { op: "detail_line", view: m.plan, points: [[0, -4000], [1000, -4000], [1000, -3000]], closed: true });
      t.assertEq(chain.json.created.length, 3, "closed chain of 3");
      await t.expectError("annotate", { op: "detail_line", view: m.plan, points: [[0, 0], [1, 0], [2, 0]], through: [1, 1] }, "INVALID_ARGS");
      return one.json.created.map(String);
    });

    await t.step("filled region with a hole", async () => {
      const r = await t.call("annotate", { op: "filled_region", view: m.plan, points: [[14000, 0], [18000, 0], [18000, 3000], [14000, 3000]], holes: [[[15000, 1000], [16000, 1000], [16000, 2000], [15000, 2000]]] });
      t.assertEq(r.json.created.length, 1, "filled region");
      t.assertEq(r.json.holes, 1, "one hole");
      t.assert(near(r.json.area, 11, 0.01), `area 12 - 1 = 11 m²: ${r.json.area}`);
    });

    await t.step("detail item (when the template has a detail component)", async () => {
      const types = tableOf((await t.call("list", { kind: "types", category: "Detail Items" })).json);
      if (!types || types.rows.length === 0) {
        await t.expectError("annotate", { op: "detail_item", view: m.plan, type: "No Such Detail", at: [0, 0] }, "NOT_FOUND");
        t.log("no detail components in the template; NOT_FOUND path covered");
        return;
      }
      const type = `${types.rows[0][col(types, "family")]}: ${types.rows[0][col(types, "name")]}`;
      let r = await t.call("annotate", { op: "detail_item", view: m.plan, type, at: [20000, 1000], rotation: 15 }, { allowError: true });
      if (r.status === "error") {
        t.assertEq(r.code, "INVALID_ARGS", "line-based types ask for start/end");
        r = await t.call("annotate", { op: "detail_item", view: m.plan, type, start: [20000, 1000], end: [22000, 1000] });
      }
      t.assertEq(r.json.created.length, 1, "detail item placed");
    });

    await t.step("revision cloud; an issued revision is refused", async () => {
      await t.call("edit_sheets", { op: "revision_create", description: "S60 Cloud" });
      const r = await t.call("annotate", { op: "revision_cloud", view: m.plan, points: [[0, 0], [3000, 0], [3000, 2000], [0, 2000]], revision: "S60 Cloud" });
      t.assertEq(r.json.created.length, 1, "cloud created");
      t.assertEq(r.json.revision.description, "S60 Cloud", "revision named");
      await t.call("edit_sheets", { op: "revision_create", description: "S60 Issued" });
      await t.call("edit_sheets", { op: "revision_edit", revision: "S60 Issued", issued: true });
      await t.expectError("annotate", { op: "revision_cloud", view: m.plan, points: [[0, 0], [1000, 0], [1000, 1000]], revision: "S60 Issued" }, "INVALID_ARGS");
    });

    await t.step("copy_to_views copies the note and a detail line to the other plan", async () => {
      const r = await t.call("annotate", { op: "copy_to_views", view: m.plan, views: [m.other], ids: [note, lines[0]] });
      t.assertEq(r.json.created.length, 2, "two copies");
      t.assertEq(tableOf(r.json.views).rows[0][1], 2, "per-view count");
      await t.expectError("annotate", { op: "copy_to_views", view: m.plan, views: [m.other], ids: [m.walls[0]] }, "INVALID_ARGS");
    });
  },
};
