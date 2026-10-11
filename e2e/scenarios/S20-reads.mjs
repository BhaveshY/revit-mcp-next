// S20 reads (SPEC §13.3, D4 S20; owner P-READ). Needs Revit with the add-in loaded from the e2e home.
// A disposable project with a 12 x 8 m room enclosed by four walls, a floor and a door, then every read key:
// all 34 list kinds (page budget, stable order), find_elements (exact totals, native where rules, handles r#, stable
// handle pages, group_by/count_only, from=handle), describe_elements (writable/all/names, type params, geometry,
// relations), check_model stats/warnings/readiness/purgeable, get_quantities (by type and material), get_changes
// (session start, last write, marks m#), read_many (one budget, blocks, lenient items) and the error contract.

const LIST_KINDS = [
  "levels", "grids", "views", "sheets", "schedules", "rooms", "areas", "spaces", "types", "families", "materials", "worksets",
  "phases", "phase_filters", "design_options", "links", "revisions", "view_templates", "view_filters", "view_types", "viewport_types",
  "title_blocks", "text_types", "dimension_types", "tag_types", "line_styles", "line_patterns", "fill_patterns", "scope_boxes",
  "groups", "systems", "system_types", "project_info", "units",
];

function rowsOf(r) {
  const j = r.json ?? {};
  if (!Array.isArray(j.rows) || !Array.isArray(j.cols)) return [];
  return j.rows.map((row) => Object.fromEntries(j.cols.map((c, i) => [c, row[i]])));
}

const createdIds = (r) => (Array.isArray(r.json?.created) ? r.json.created.map(Number) : []);
const near = (actual, expected, tolerance) => typeof actual === "number" && Math.abs(actual - expected) <= tolerance;

export default {
  id: "S20",
  title: "Reads: list kinds, find/describe, check_model, quantities, changes, read_many",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S20");
    t.setTargetPath(path);

    // The first read of this session marks the session start for get_changes {since:"session"}.
    const levels = await t.step("list levels: sorted by elevation, mm", async () => {
      const r = await t.call("list", { kind: "levels" });
      const rows = rowsOf(r);
      t.assert(rows.length >= 1, "the template has levels");
      t.assertDeepEqual(r.json.cols.slice(0, 3), ["id", "name", "elevation"], "level columns");
      for (let i = 1; i < rows.length; i++) t.assert(rows[i].elevation >= rows[i - 1].elevation, "levels sorted by elevation");
      return rows;
    });
    const L1 = levels[0].name;
    let L2 = levels[1]?.name;
    if (!L2) {
      await t.call("create_elements", { op: "level", elevation: 4000, name: "S20 Level 2" });
      L2 = "S20 Level 2";
    }

    const model = await t.step("model: four walls (12 x 8 m), a floor, a room and a door", async () => {
      const types = rowsOf(await t.call("list", { kind: "types", category: ["Walls"], class: "WallType", limit: 100 }));
      const basic = types.find((x) => /200/.test(String(x.name)) && /basic/i.test(String(x.family))) ?? types.find((x) => /basic/i.test(String(x.family))) ?? types[0];
      t.assert(basic, "a wall type exists");
      const walls = await t.call("create_elements", { op: "wall", level: L1, points: [[0, 0], [12000, 0], [12000, 8000], [0, 8000]], closed: true, top: L2, type: `${basic.family}: ${basic.name}` });
      const wallIds = createdIds(walls);
      t.assertEq(wallIds.length, 4, "four walls");
      const floor = await t.call("create_elements", { op: "floor", level: L1, points: [[0, 0], [12000, 0], [12000, 8000], [0, 8000]] });
      const room = await t.call("create_elements", { op: "room", level: L1, at: [6000, 4000], name: "S20 Office", number: "101" });
      const doorTypes = rowsOf(await t.call("list", { kind: "types", category: ["Doors"], limit: 20 }));
      let door = null;
      if (doorTypes.length > 0) {
        const placed = await t.call("place_family", { op: "place", type: `${doorTypes[0].family}: ${doorTypes[0].name}`, at: [6000, 0], level: L1, host: String(wallIds[0]) });
        door = createdIds(placed)[0] ?? null;
      } else {
        t.log("the template has no door types; door assertions are skipped");
      }
      return { walls: wallIds, floor: createdIds(floor)[0], room: createdIds(room)[0], door, wallType: basic };
    });

    await t.step("every list kind answers within the 8 KB page budget", async () => {
      for (const kind of LIST_KINDS) {
        const r = await t.call("list", { kind, limit: 50 });
        t.assertEq(r.status, "ok", `list ${kind}`);
        t.assert(typeof r.json?.total === "number" && Array.isArray(r.json?.cols), `list ${kind} is a table`);
        t.assertLe(r.bytes, 8_192 + 600, `list ${kind} bytes`);
      }
    });

    await t.step("list filters: views graphical/printable/view_type, rooms, types for_element, PARAM_IGNORED", async () => {
      const graphical = rowsOf(await t.call("list", { kind: "views", graphical: true, limit: 200 }));
      t.assert(graphical.every((v) => v.view_type !== "Schedule"), "graphical drops schedules");
      const plans = rowsOf(await t.call("list", { kind: "views", view_type: "FloorPlan", limit: 200 }));
      t.assert(plans.length >= 1 && plans.every((v) => v.view_type === "FloorPlan"), "view_type FloorPlan");
      const printable = await t.call("list", { kind: "views", printable: true, limit: 5 });
      t.assertEq(printable.status, "ok", "printable");
      const rooms = rowsOf(await t.call("list", { kind: "rooms", level: L1 }));
      const office = rooms.find((x) => x.number === "101");
      t.assert(office, "room 101 listed");
      t.assertEq(office.status, "placed", "room is placed and enclosed");
      t.assert(near(office.area, 92, 6), `room area about 92 m2, got ${office.area}`);
      const valid = await t.call("list", { kind: "types", for_element: String(model.walls[0]) });
      const current = rowsOf(valid).find((x) => /current/.test(String(x.class)));
      t.assert(current && current.name === model.wallType.name, "for_element marks the current type");
      const ignored = await t.call("list", { kind: "levels", category: ["Walls"] });
      t.assert(ignored.warnings.some((w) => w.startsWith("PARAM_IGNORED")), "category does not apply to levels");
      const named = rowsOf(await t.call("list", { kind: "views", name: "Level*", limit: 100 }));
      t.assert(named.every((v) => /^level/i.test(v.name)), "name wildcard");
    });

    const handle = await t.step("find_elements: exact total, rows, handle r#, native where (mm)", async () => {
      const r = await t.call("find_elements", { category: ["Walls"] });
      t.assertEq(r.json.total, 4, "four walls");
      t.assertEq(rowsOf(r).length, 4, "four rows");
      t.assert(r.json.cols.includes("length") && r.json.cols.includes("height"), "wall columns");
      t.assertMatch(r.json.handle ?? "", /^r\d+$/, "handle");
      const lengths = rowsOf(r).map((x) => x.length).sort((a, b) => a - b);
      t.assert(near(lengths[0], 8000, 1) && near(lengths[3], 12000, 1), `wall lengths in mm: ${lengths}`);
      const long = await t.call("find_elements", { category: ["Walls"], where: ["Length >= 10000"] });
      t.assertEq(long.json.total, 2, "where Length >= 10000 (mm)");
      const marked = await t.call("find_elements", { category: "Walls", where: "Comments empty", count_only: true });
      t.assertEq(marked.json.total, 4, "empty comments");
      return r.json.handle;
    });

    await t.step("find_elements paging over a handle is stable", async () => {
      const first = await t.call("find_elements", { category: ["Walls", "Floors", "Rooms", "Doors"], limit: 2 });
      t.assert(first.more && /"page":"p\d+"/.test(first.more), `more line: ${first.more}`);
      t.assertMatch(first.summary, /showing 1-2 of \d+, more p\d+/, "page token repeated in the summary");
      const page = JSON.parse(first.more.slice(first.more.indexOf("{")));
      const second = await t.call("find_elements", page);
      const a = rowsOf(first).map((x) => x.id);
      const b = rowsOf(second).map((x) => x.id);
      t.assert(b.length >= 1 && b.every((id) => !a.includes(id)), "the second page has other ids");
      const total = first.json.total;
      t.assert(total >= 6, "walls + floor + room (+ door)");
    });

    await t.step("find_elements group_by, count_only, from handle, types", async () => {
      const grouped = await t.call("find_elements", { category: ["Walls", "Floors"], group_by: "category", count_only: true });
      const groups = Object.fromEntries(grouped.json.groups.rows);
      t.assertEq(groups.Walls, 4, "4 walls by category (English label)");
      t.assertEq(groups.Floors, 1, "1 floor");
      const from = await t.call("find_elements", { from: handle, where: ["Length < 10000"] });
      t.assertEq(from.json.total, 2, "from handle narrows to the handle's walls");
      const types = await t.call("find_elements", { category: ["Walls"], types: true, limit: 5 });
      t.assert(types.json.total >= 1 && types.json.cols.includes("name"), "types:true lists wall types");
    });

    await t.step("describe_elements: writable params, all + read_only, names, type params, geometry, relations", async () => {
      const writable = await t.call("describe_elements", { from: handle });
      t.assertEq(writable.json.total, 4, "four elements");
      const wall = writable.json.elements[0];
      t.assertEq(wall.category, "Walls", "English category");
      t.assert("Comments" in wall.params, "Comments is writable (English key)");
      t.assert(!("Area" in wall.params), "read-only Area is not in writable");
      const all = await t.call("describe_elements", { ids: [String(model.walls[0])], params: "all", include: ["type", "geometry", "relations"] });
      const one = all.json.elements[0];
      t.assert("Area" in one.params && one.read_only.includes("Area"), "all includes read-only Area and lists it");
      t.assert(one.type_params && Object.keys(one.type_params).length > 0, "type parameters");
      t.assert(near(one.geometry.length, 12000, 1), `geometry length 12000 mm, got ${one.geometry.length}`);
      if (model.door) t.assert((one.relations.hosted ?? []).map(Number).includes(model.door), "relations: the wall hosts the door");
      const names = await t.call("describe_elements", { ids: [String(model.walls[0])], params: "names", match: "Comment" });
      t.assertMatch(String(names.json.elements[0].params.Comments), /^text rw$/, "names mode: kind and writability");
      if (model.door) {
        const door = await t.call("describe_elements", { ids: [String(model.door)], include: ["relations"] });
        t.assertEq(Number(door.json.elements[0].relations.host), model.walls[0], "door host");
        t.assert(Array.isArray(door.json.elements[0].relations.to_room ?? door.json.elements[0].relations.from_room ?? []), "door rooms");
      }
    });

    await t.step("check_model stats, warnings, readiness, purgeable", async () => {
      const stats = await t.call("check_model", { check: "stats" });
      const byCategory = Object.fromEntries(stats.json.rows);
      t.assertEq(byCategory.Walls, 4, "stats count walls");
      t.assert(stats.json.totals.model_elements >= 6, "model elements total");
      const byLevel = await t.call("check_model", { check: "stats", group_by: "level" });
      t.assert(byLevel.json.rows.some((row) => row[0] === L1), "stats by level names Level 1");
      const warnings = await t.call("check_model", { check: "warnings" });
      t.assert(typeof warnings.json.total === "number", "warnings table");
      const errors = await t.call("check_model", { check: "warnings", severity: "error" });
      t.assert(rowsOf(errors).every((x) => x.severity === "error"), "severity filter");
      const ready = await t.call("check_model", { check: "readiness" });
      const walls = rowsOf(ready).find((x) => x.scenario === "walls");
      t.assertEq(walls?.ready, true, "walls are ready");
      t.assertEq(rowsOf(ready).length, 6, "default scenario set");
      const purge = await t.call("check_model", { check: "purgeable", limit: 20 });
      t.assert(purge.json.total >= 0 && typeof purge.json.by_kind === "object", "purgeable lists candidates by kind");
      const allScenarios = await t.call("check_model", { check: "readiness", scenarios: ["all"] });
      t.assert(rowsOf(allScenarios).length >= 18, "all scenarios");
    });

    await t.step("get_quantities by type and by material", async () => {
      const byType = await t.call("get_quantities", { by: "type", category: ["Walls"] });
      const row = rowsOf(byType)[0];
      t.assertEq(row.count, 4, "four walls in one type");
      t.assert(near(row.length_m, 40, 0.5), `wall length 40 m, got ${row.length_m}`);
      t.assert(row.area_m2 > 0 && row.volume_m3 > 0, "area and volume");
      const byMaterial = await t.call("get_quantities", { by: "material", category: ["Walls", "Floors"] });
      const materials = rowsOf(byMaterial);
      t.assert(materials.length >= 1 || byMaterial.json.totals.without_material > 0, "material rows (or elements without a material)");
      t.assert(materials.every((x) => x.volume_m3 >= 0 && x.area_m2 >= 0), "material quantities are non-negative");
      const byLevel = await t.call("get_quantities", { by: "level", from: handle });
      t.assertEq(rowsOf(byLevel)[0]?.count, 4, "from= handle");
    });

    await t.step("get_changes: session start, marks, last write", async () => {
      const session = await t.call("get_changes", { limit: 500 });
      const added = rowsOf(session).filter((x) => x.change === "added").map((x) => Number(x.id));
      for (const id of model.walls) t.assert(added.includes(id), `wall ${id} was added this session`);
      t.assertMatch(session.json.mark ?? "", /^m\d+$/, "a mark for now");
      t.assertMatch(session.next ?? "", /get_changes \{"since":"m\d+"\}/, "next polls from the mark");
      const quiet = await t.call("get_changes", { since: session.json.mark });
      t.assertEq(quiet.json.added + quiet.json.modified + quiet.json.deleted, 0, "nothing changed since the mark");
      const write = await t.call("set_parameters", { ids: [String(model.walls[1])], values: { Comments: "S20" } });
      t.assertEq(write.status, "ok", "set Comments");
      const since = await t.call("get_changes", { since: session.json.mark, limit: 500 });
      t.assert(rowsOf(since).some((x) => Number(x.id) === model.walls[1] && x.change === "modified"), "the wall is modified since the mark");
      t.assert(since.json.transactions.rows.some((x) => x[2] === "you"), "the transaction is ours");
      const last = await t.call("get_changes", { since: "last" });
      t.assert(rowsOf(last).some((x) => Number(x.id) === model.walls[1]), "since last write");
      await t.expectError("get_changes", { since: "m9999" }, "HANDLE_EXPIRED");
    });

    await t.step("read_many: one budget, one block per call, lenient items", async () => {
      const r = await t.call("read_many", {
        calls: [
          { tool: "list", args: { kind: "levels" } },
          { tool: "find_elements", args: { category: ["Walls"], count_only: true } },
          { tool: "check_model.warnings" },
          { tool: "describe_element", args: { ids: [String(model.walls[0])] } },
        ],
      });
      t.assertEq(r.status, "ok", "read_many ok");
      t.assertEq(r.blocks.length, 4, "four blocks");
      t.assertMatch(r.blocks[0], /^\[0\] list: ok:/, "block 0");
      t.assertMatch(r.blocks[1], /^\[1\] find_elements: ok: 4 elements/, "block 1 counts walls");
      t.assertMatch(r.blocks[2], /^\[2\] check_model: ok:/, "tool.op item");
      t.assertMatch(r.blocks[3], /^\[3\] describe_element: ERROR INVALID_ARGS[\s\S]*describe_elements/, "closest read tool suggested");
      t.assertLe(r.bytes, 14_000, "read_many stays near the result budget");
    });

    await t.step("errors carry fixes: unknown category, bad where, missing ids, unknown kind", async () => {
      const cat = await t.expectError("find_elements", { category: ["Wallz"] }, "NOT_FOUND");
      t.assertMatch(cat.fix ?? "", /Walls/, "fix offers Walls");
      await t.expectError("find_elements", { category: ["Walls"], where: ["Length is long"] }, "INVALID_ARGS");
      await t.expectError("describe_elements", { ids: ["999999999"] }, "NOT_FOUND");
      await t.expectError("list", { kind: "levelz" }, ["INVALID_ARGS", "UNKNOWN_OP"]);
      await t.expectError("check_model", { check: "readiness", scenarios: ["flying"] }, "INVALID_ARGS");
    });
  },
};
