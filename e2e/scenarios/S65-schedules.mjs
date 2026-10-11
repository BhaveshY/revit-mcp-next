// S65 schedules (SPEC §13.3, owner P-SHEETS-ANNO): every edit_schedules op and read_schedule on a door schedule of a
// disposable project with three doors.
//   read_schedule category= (available fields via the temp probe), create (fields, sort; NAME_TAKEN), add/remove/
//   order fields, set_field (heading, hidden, totals), set_filters (typed lengths, clear), set_sort, set_options,
//   duplicate. The id column (C7): with the unique Mark key every row maps to its door, and after re-sorting the
//   schedule the id-to-row mapping is still correct; with a non-unique key, a hidden key or a non-itemized schedule
//   there is no id column, data.elementIds lists the doors and a "rows have no ids" notice says why. Paging follows
//   the more: token.

import { parseCallLine } from "../lib/call.mjs";

const tableOf = (j) => (j && Array.isArray(j.rows) && Array.isArray(j.cols) ? j : null);
const col = (table, name) => (table ? table.cols.indexOf(name) : -1);

/** Row id → Mark cell of a read_schedule result with ids. */
function idToMark(r, heading) {
  const rows = tableOf(r.json.rows);
  const idCol = col(rows, "id");
  const markCol = col(rows, heading);
  const map = new Map();
  for (const row of rows.rows) if (row[idCol] !== null && row[idCol] !== undefined) map.set(String(row[idCol]), String(row[markCol]));
  return map;
}

export default {
  id: "S65",
  title: "Schedules: create/edit fields, filters, sorting, options; read_schedule rows, ids and paging",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    await t.step("disposable project S65", async () => {
      const { r, path } = await t.newProject("S65");
      t.assertEq(r.status, "ok", "new_project");
      t.setTargetPath(path);
    });

    const m = await t.step("model: 4 walls and 3 doors with marks D1-D3", async () => {
      const levels = tableOf((await t.call("list", { kind: "levels" })).json);
      const sorted = [...levels.rows].sort((a, b) => Number(a[col(levels, "elevation")]) - Number(b[col(levels, "elevation")]));
      const level = sorted[0][col(levels, "name")];
      const walls = await t.call("create_elements", { op: "wall", level, points: [[0, 0], [12000, 0], [12000, 8000], [0, 8000]], closed: true, height: 3000 });
      const types = tableOf((await t.call("list", { kind: "types", category: "Doors" })).json);
      t.assert(types && types.rows.length > 0, "the template has a door type");
      const type = `${types.rows[0][col(types, "family")]}: ${types.rows[0][col(types, "name")]}`;
      const doors = [];
      for (const [i, x] of [2000, 6000, 10000].entries()) {
        const placed = await t.call("place_family", { op: "place", type, at: [x, 0], level, host: String(walls.json.created[0]) });
        doors.push(String(placed.json.created[0]));
        // Marks in reverse order of placement so sorting by Mark differs from element order.
        await t.call("set_parameters", { ids: [doors[i]], values: { Mark: `D${3 - i}` } });
      }
      return { level, doors };
    });

    await t.step("read_schedule category= lists available fields (temp probe)", async () => {
      const r = await t.call("read_schedule", { category: "Doors", available: true, rows: false });
      const available = tableOf(r.json.available);
      t.assert(available && available.rows.some((row) => row[0] === "Mark" || /mark/i.test(row[0])), "Mark is schedulable");
      const filtered = await t.call("read_schedule", { category: "Doors", name: "Wid" });
      t.assert(tableOf(filtered.json.available).rows.every((row) => /wid/i.test(row[0])), "name filters the fields");
      await t.expectError("read_schedule", { category: "No Such Category" }, "NOT_FOUND");
    });

    await t.step("create a door schedule with fields and sorting; NAME_TAKEN", async () => {
      const r = await t.call("edit_schedules", { op: "create", category: "Doors", name: "S65 Doors", fields: ["Mark", "Type", "Width", "Level"], sort: ["Mark"] });
      t.assertEq(r.json.created.length, 1, "schedule created");
      t.assertEq(r.json.schedule.fields.length, 4, "four fields");
      const taken = await t.expectError("edit_schedules", { op: "create", category: "Doors", name: "S65 Doors" }, "NAME_TAKEN");
      t.assertMatch(taken.fix, /S65 Doors 2/, "fix suggests the next free name");
      const bad = await t.expectError("edit_schedules", { op: "create", category: "Doors", name: "S65 Bad", fields: ["Mark", "No Such Field"] }, "NOT_FOUND");
      t.assertMatch(bad.fix, /"fields":\["Mark",/, "fix repeats the call with the corrected field list");
    });

    await t.step("add_fields, set_field heading/hidden/totals, order_fields", async () => {
      const add = await t.call("edit_schedules", { op: "add_fields", schedule: "S65 Doors", fields: ["Height", "Mark"] });
      t.assert(add.json.schedule.fields.includes("Height"), "Height added");
      t.assert(add.warnings.some((w) => /Mark/.test(w)), "Mark already present is warned");
      const heading = await t.call("edit_schedules", { op: "set_field", schedule: "S65 Doors", field: "Mark", heading: "Door No." });
      t.assertEq(heading.json.field.heading, "Door No.", "heading set");
      const hidden = await t.call("edit_schedules", { op: "set_field", schedule: "S65 Doors", field: "Height", hidden: true });
      t.assertEq(hidden.json.field.hidden, true, "Height hidden");
      const total = await t.call("edit_schedules", { op: "set_field", schedule: "S65 Doors", field: "Mark", totals: true }, { allowError: true });
      t.assert(total.status === "error" ? total.code === "INVALID_ARGS" : true, "a text field cannot be totalled");
      const order = await t.call("edit_schedules", { op: "order_fields", schedule: "S65 Doors", fields: ["Level", "Mark"] });
      t.assertEq(order.json.schedule.fields.slice(0, 2).join(","), "Level,Mark", "Level and Mark first");
    });

    const first = await t.step("read rows: id column through the unique Mark key", async () => {
      const r = await t.call("read_schedule", { schedule: "S65 Doors" });
      const rows = tableOf(r.json.rows);
      t.assertEq(rows.cols[0], "id", "id column present");
      t.assertEq(rows.total, 3, "three door rows");
      t.assert(!rows.cols.includes("Height"), "hidden fields have no column");
      const map = idToMark(r, "Door No.");
      t.assertEq(map.size, 3, "every row has an id");
      for (const id of m.doors) t.assert(map.has(id), `door ${id} is a row`);
      t.assertEq(r.json.sort.join(","), "Mark", "sorted by Mark");
      return map;
    });

    await t.step("re-sorted schedule keeps the id-to-row mapping", async () => {
      await t.call("edit_schedules", { op: "set_sort", schedule: "S65 Doors", sort: ["-Mark"] });
      const r = await t.call("read_schedule", { schedule: "S65 Doors" });
      const map = idToMark(r, "Door No.");
      t.assertEq(map.size, 3, "every row has an id");
      for (const [id, mark] of first) t.assertEq(map.get(id), mark, `door ${id} keeps mark ${mark}`);
      const rows = tableOf(r.json.rows);
      const marks = rows.rows.map((row) => row[col(rows, "Door No.")]);
      t.assertEq(marks.join(","), "D3,D2,D1", "descending order");
      // Cross-check one id against the element itself.
      const [someId, someMark] = [...map.entries()][0];
      const d = await t.call("describe_elements", { ids: [someId], params: "all", match: "Mark" });
      t.assert(d.text.includes(someMark), `describe_elements of ${someId} shows mark ${someMark}`);
    });

    await t.step("no id column: non-unique key, hidden key, not itemized", async () => {
      const level = await t.call("read_schedule", { schedule: "S65 Doors", key: "Level" });
      t.assert(!tableOf(level.json.rows).cols.includes("id"), "no id column for a non-unique key");
      t.assert(level.notices.some((n) => /rows have no ids/.test(n)), `notice: ${level.notices.join(" | ")}`);
      t.assertEq(level.json.elementIds.length, 3, "elementIds lists the doors");
      const hidden = await t.call("read_schedule", { schedule: "S65 Doors", key: "Height" });
      t.assert(hidden.notices.some((n) => /hidden/.test(n)), "hidden key named in the notice");
      await t.call("edit_schedules", { op: "set_options", schedule: "S65 Doors", itemize: false });
      const grouped = await t.call("read_schedule", { schedule: "S65 Doors" });
      t.assert(grouped.notices.some((n) => /not itemized/.test(n)), "not itemized named in the notice");
      await t.call("edit_schedules", { op: "set_options", schedule: "S65 Doors", itemize: true });
    });

    await t.step("paging with limit 1 follows the more: token", async () => {
      const p1 = await t.call("read_schedule", { schedule: "S65 Doors", limit: 1, available: true });
      t.assertEq(tableOf(p1.json.rows).rows.length, 1, "one row");
      t.assert(tableOf(p1.json.available).rows.length > 0, "available fields listed with a schedule");
      t.assert(p1.more, "more: line present");
      const next = parseCallLine(p1.more);
      const p2 = await t.call(next.tool, next.args);
      const rows = tableOf(p2.json.rows);
      t.assertEq(rows.rows.length, 1, "second page has one row");
      t.assert(String(rows.rows[0][0]) !== String(tableOf(p1.json.rows).rows[0][0]), "second page is a different row");
    });

    await t.step("set_filters with a typed length, then clear", async () => {
      const f = await t.call("edit_schedules", { op: "set_filters", schedule: "S65 Doors", filters: ["Width >= 10", "Door No. has value"] });
      t.assertEq(f.json.filters.length, 2, "two filters");
      const r = await t.call("read_schedule", { schedule: "S65 Doors" });
      t.assertEq(tableOf(r.json.rows).total, 3, "all doors are wider than 10 mm");
      const none = await t.call("edit_schedules", { op: "set_filters", schedule: "S65 Doors", filters: ["Width > 100000"] });
      t.assertEq(none.json.filters.length, 1, "one filter");
      const empty = await t.call("read_schedule", { schedule: "S65 Doors" });
      t.assertEq(tableOf(empty.json.rows).total, 0, "no door is wider than 100 m");
      const cleared = await t.call("edit_schedules", { op: "set_filters", schedule: "S65 Doors", filters: [] });
      t.assertEq(cleared.json.filters.length, 0, "filters cleared");
      await t.expectError("edit_schedules", { op: "set_filters", schedule: "S65 Doors", filters: ["Width >> 3"] }, "INVALID_ARGS");
      await t.expectError("edit_schedules", { op: "set_filters", schedule: "S65 Doors", filters: ["Width > wide"] }, "INVALID_ARGS");
    });

    await t.step("remove_fields drops a sorted field and its sorting", async () => {
      await t.call("edit_schedules", { op: "set_sort", schedule: "S65 Doors", sort: ["Level", "Mark"], headers: true });
      const r = await t.call("edit_schedules", { op: "remove_fields", schedule: "S65 Doors", fields: ["Level"] });
      t.assert(!r.json.schedule.fields.includes("Level"), "Level removed");
      t.assertEq(r.json.sort.length, 1, "the Level sort went with it");
      t.assert(r.warnings.some((w) => /PARTIAL_RESULT/.test(w)), "removed rules are warned");
    });

    await t.step("set_options rename; duplicate", async () => {
      const ren = await t.call("edit_schedules", { op: "set_options", schedule: "S65 Doors", name: "S65 Doors All", totals: true });
      t.assertEq(ren.json.schedule.name, "S65 Doors All", "renamed");
      t.assertEq(ren.json.schedule.totals, true, "grand totals on");
      const read = await t.call("read_schedule", { schedule: "S65 Doors All" });
      t.assert(tableOf(read.json.rows).cols[0] === "id", "the grand total row does not break the id column");
      const dup = await t.call("edit_schedules", { op: "duplicate", schedule: "S65 Doors All", name: "S65 Doors Copy" });
      t.assertEq(dup.json.created.length, 1, "duplicate created");
      t.assertEq(dup.json.schedule.fields.join(","), ren.json.schedule.fields.join(","), "same fields");
      await t.expectError("edit_schedules", { op: "duplicate", schedule: "S65 Doors All", name: "S65 Doors Copy" }, "NAME_TAKEN");
    });

    await t.step("material takeoff and key schedule", async () => {
      const mt = await t.call("edit_schedules", { op: "create", category: "Walls", kind: "material_takeoff", name: "S65 Wall Materials" }, { allowError: true });
      t.assert(mt.status === "ok", `material takeoff: ${mt.code ?? "ok"} ${mt.summary}`);
      const key = await t.call("edit_schedules", { op: "create", category: "Rooms", kind: "key", name: "S65 Room Keys" });
      t.assertEq(key.json.schedule.kind, "key", "key schedule");
    });
  },
};
