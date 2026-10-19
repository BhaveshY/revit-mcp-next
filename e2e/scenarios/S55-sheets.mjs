// S55 sheets (SPEC §13.3, owner P-SHEETS-ANNO): every edit_sheets op on a disposable project.
//   create (one, many, NAME_TAKEN with the next free number for number= and for items=), rename, set_titleblock,
//   place (view, schedule; a view on a second sheet is UNSUPPORTED_OP with an edit_views fix), move_viewport (at, by),
//   set_viewport_type, remove_viewport, duplicate (with_views + prefix, empty), revisions create/edit (issued lock)/
//   add/remove, preview changes nothing, NOT_FOUND fixes. Names come from the template through list, so the
//   scenario also runs on non-English templates.

const tableOf = (j) => (j && Array.isArray(j.rows) && Array.isArray(j.cols) ? j : null);
const col = (table, name) => (table ? table.cols.indexOf(name) : -1);
const near = (a, b, tol = 0.6) => Math.abs(Number(a) - Number(b)) <= tol;

export default {
  id: "S55",
  title: "Sheets: create/duplicate/rename, titleblocks, viewports, schedules on sheets, revisions",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    await t.step("disposable project S55", async () => {
      const { r, path } = await t.newProject("S55");
      t.assertEq(r.status, "ok", "new_project");
      t.setTargetPath(path);
    });

    // ---------------------------------------------------------------- names from the template
    const names = await t.step("plan view and titleblock from the template (list)", async () => {
      const levels = tableOf((await t.call("list", { kind: "levels" })).json);
      t.assert(levels && levels.rows.length > 0, "the template has levels");
      const views = tableOf((await t.call("list", { kind: "views", view_type: "FloorPlan" })).json);
      t.assert(views && views.rows.length >= 2, "the template has at least two floor plans");
      // Templates place some plans on their own sheets already (2027 multi-discipline: A101, E101...): use free ones.
      const sheetCol = col(views, "sheet");
      const free = views.rows.filter((row) => sheetCol < 0 || !row[sheetCol]);
      t.assert(free.length >= 2, `the template has two floor plans on no sheet (${free.length})`);
      const plan = free[0][col(views, "name")];
      const plan2 = free[1][col(views, "name")];
      const blocks = tableOf((await t.call("list", { kind: "title_blocks" })).json);
      const titleblock = blocks && blocks.rows.length > 0 ? `${blocks.rows[0][col(blocks, "family")]}: ${blocks.rows[0][col(blocks, "name")]}` : null;
      return { plan, plan2, titleblock };
    });
    t.log(`plan '${names.plan}', second plan '${names.plan2}', titleblock ${names.titleblock ?? "(none loaded)"}`);

    // ---------------------------------------------------------------- create
    await t.step("create two sheets with items", async () => {
      const args = { op: "create", items: [{ number: "Q901", name: "Ground floor" }, { number: "Q902", name: "First floor" }] };
      if (names.titleblock) args.titleblock = names.titleblock;
      const r = await t.call("edit_sheets", args);
      t.assertEq(r.json.created.length, 2, "two sheets created");
      const sheets = tableOf(r.json.sheets);
      t.assertEq(sheets.rows.map((row) => row[col(sheets, "number")]).join(","), "Q901,Q902", "sheet numbers");
      if (names.titleblock) t.assert(sheets.rows.every((row) => row[col(sheets, "titleblock")]), "titleblock set");
      t.assertMatch(r.json.write ?? "", /^w\d+$/, "write id");
    });

    await t.step("NAME_TAKEN offers the next free number (number= and items=)", async () => {
      const single = await t.expectError("edit_sheets", { op: "create", number: "Q901", name: "Again" }, "NAME_TAKEN");
      t.assertMatch(single.fix, /"number":"Q903"/, "fix uses the next free number Q903");
      const many = await t.expectError("edit_sheets", { op: "create", items: [{ number: "Q905" }, { number: "Q902" }] }, "NAME_TAKEN");
      t.assertMatch(many.fix, /"items":\[/, "fix repeats the call with corrected items");
      t.assertMatch(many.fix, /"number":"Q905"/, "free numbers are kept");
      t.assert(!/"number":"Q902"/.test(many.fix), "the used number is replaced");
    });

    await t.step("preview changes nothing", async () => {
      const r = await t.call("edit_sheets", { op: "create", number: "Q990", name: "Preview only", preview: true });
      t.assertMatch(r.summary, /preview/, "preview summary");
      await t.expectError("edit_sheets", { op: "rename", sheet: "Q990", name: "x" }, "NOT_FOUND");
    });

    await t.step("rename number and name", async () => {
      const r = await t.call("edit_sheets", { op: "rename", sheet: "Q902", number: "Q910", name: "First floor plan" });
      t.assertEq(r.json.sheet.number, "Q910", "new number");
      t.assertEq(r.json.sheet.name, "First floor plan", "new name");
      t.assertEq(r.json.modified.length, 1, "one sheet modified");
      await t.expectError("edit_sheets", { op: "rename", sheet: "Q910", number: "Q901" }, "NAME_TAKEN");
    });

    if (names.titleblock) {
      await t.step("set_titleblock on several sheets", async () => {
        const r = await t.call("edit_sheets", { op: "set_titleblock", sheets: ["Q901", "Q910"], titleblock: names.titleblock });
        const rows = tableOf(r.json.sheets).rows;
        t.assertEq(rows.length, 2, "two sheets reported");
      });
    }
    await t.step("NOT_FOUND for an unknown titleblock names candidates", async () => {
      const r = await t.expectError("edit_sheets", { op: "set_titleblock", sheet: "Q901", titleblock: "No Such Titleblock" }, "NOT_FOUND");
      t.assert(!!r.fix, "fix present");
    });

    // ---------------------------------------------------------------- viewports
    const placed = await t.step("place a plan view on Q901 at the titleblock centre", async () => {
      const r = await t.call("edit_sheets", { op: "place", sheet: "Q901", view: names.plan });
      t.assertEq(r.json.placed.kind, "view", "placed a view");
      t.assertEq(r.json.created.length, 1, "one viewport");
      return { id: r.json.placed.id, type: r.json.placed.type, at: r.json.placed.at };
    });

    await t.step("a view already on a sheet cannot be placed again (fix: edit_views duplicate)", async () => {
      const r = await t.expectError("edit_sheets", { op: "place", sheet: "Q910", view: names.plan }, "UNSUPPORTED_OP");
      t.assertMatch(r.fix, /^edit_views \{.*"op":"duplicate"/, "fix duplicates the view onto the sheet");
    });

    await t.step("move_viewport by and at", async () => {
      const by = await t.call("edit_sheets", { op: "move_viewport", sheet: "Q901", view: names.plan, by: [50, 0] });
      t.assert(near(by.json.to[0] - by.json.from[0], 50) && near(by.json.to[1], by.json.from[1]), `moved by 50 mm in x: ${JSON.stringify(by.json)}`);
      const at = await t.call("edit_sheets", { op: "move_viewport", sheet: "Q901", view: names.plan, at: [400, 300] });
      t.assert(near(at.json.to[0], 400) && near(at.json.to[1], 300), `centre at [400,300]: ${JSON.stringify(at.json.to)}`);
      t.assertEq(at.json.modified[0], placed.id, "the viewport is the modified element");
    });

    await t.step("set_viewport_type to another viewport type", async () => {
      const types = tableOf((await t.call("list", { kind: "viewport_types" })).json);
      t.assert(types && types.rows.length > 0, "viewport types listed");
      const other = types.rows.map((row) => row[col(types, "name")]).find((n) => n !== placed.type) ?? placed.type;
      const r = await t.call("edit_sheets", { op: "set_viewport_type", sheet: "Q901", view: names.plan, type: other });
      t.assertEq(r.json.type, other, "type set");
      await t.expectError("edit_sheets", { op: "set_viewport_type", sheet: "Q901", view: names.plan, type: "No Such Viewport Type" }, "NOT_FOUND");
    });

    await t.step("place a schedule on a sheet and remove it again", async () => {
      const created = await t.call("edit_schedules", { op: "create", category: "Doors", name: "S55 Doors", fields: ["Mark"] });
      t.assertEq(created.json.created.length, 1, "schedule created");
      const p = await t.call("edit_sheets", { op: "place", sheet: "Q910", view: "S55 Doors", at: [30, 500] });
      t.assertEq(p.json.placed.kind, "schedule", "placed as a schedule instance");
      const instance = p.json.placed.id;
      const again = await t.call("edit_sheets", { op: "place", sheet: "Q901", view: "S55 Doors" });
      t.assertEq(again.json.placed.kind, "schedule", "a schedule can be on several sheets");
      const removed = await t.call("edit_sheets", { op: "remove_viewport", sheet: "Q910", view: "S55 Doors" });
      t.assert(removed.json.deleted.includes(instance), "the schedule instance is deleted");
      await t.expectError("edit_sheets", { op: "remove_viewport", sheet: "Q910", view: "S55 Doors" }, "NOT_FOUND");
      const list = tableOf((await t.call("list", { kind: "schedules", name: "S55 Doors" })).json);
      t.assertEq(list.rows.length, 1, "the schedule itself still exists");
    });

    // ---------------------------------------------------------------- duplicate
    await t.step("duplicate with views and a prefix, and as an empty sheet", async () => {
      const r = await t.call("edit_sheets", { op: "duplicate", sheet: "Q901", number: "Q920", mode: "with_views", prefix: "E2E " });
      t.assertEq(r.json.sheet.number, "Q920", "new sheet number");
      const views = tableOf(r.json.views);
      t.assert(views && views.rows.length >= 1, "a view was duplicated");
      t.assert(views.rows.every((row) => String(row[col(views, "view")]).startsWith("E2E ")), "copied views carry the prefix");
      t.assert(r.json.created.length >= 2, "sheet and view copies are created");
      const empty = await t.call("edit_sheets", { op: "duplicate", sheet: "Q901", number: "Q921", mode: "empty", name: "Blank copy" });
      t.assertEq(empty.json.sheet.name, "Blank copy", "name of the empty copy");
      t.assert(!empty.json.views, "no views copied for mode empty");
      await t.expectError("edit_sheets", { op: "duplicate", sheet: "Q901", number: "Q920" }, "NAME_TAKEN");
    });

    // ---------------------------------------------------------------- revisions
    await t.step("revision create/edit (issued lock)/add/remove", async () => {
      const c = await t.call("edit_sheets", { op: "revision_create", description: "S55 Issue A", date: "2026-10-11", issued_by: "E2E" });
      t.assertEq(c.json.revision.description, "S55 Issue A", "description");
      const add = await t.call("edit_sheets", { op: "revision_add", revision: "S55 Issue A", sheets: ["Q901", "Q910"] });
      t.assertEq(add.json.modified.length, 2, "two sheets got the revision");
      const again = await t.call("edit_sheets", { op: "revision_add", revision: "S55 Issue A", sheet: "Q901" });
      t.assertEq(again.json.modified.length, 0, "adding twice changes nothing");

      const issue = await t.call("edit_sheets", { op: "revision_edit", revision: "S55 Issue A", issued: true });
      t.assertEq(issue.json.revision.issued, true, "issued");
      const locked = await t.expectError("edit_sheets", { op: "revision_edit", revision: "S55 Issue A", date: "2026-10-12" }, "INVALID_ARGS");
      t.assertMatch(locked.fix, /"issued":false/, "fix unlocks the revision");
      const edit = await t.call("edit_sheets", { op: "revision_edit", revision: "S55 Issue A", issued: false, date: "2026-10-12" });
      t.assertEq(edit.json.revision.date, "2026-10-12", "date edited after unlocking");

      const remove = await t.call("edit_sheets", { op: "revision_remove", revision: "S55 Issue A", sheet: "Q910" });
      const rows = tableOf(remove.json.sheets).rows;
      t.assertEq(rows[0][1], "removed", "removed from Q910");
      const twice = await t.call("edit_sheets", { op: "revision_remove", revision: "S55 Issue A", sheet: "Q910" });
      t.assertEq(tableOf(twice.json.sheets).rows[0][1], "was not on sheet", "second remove is a no-op");
      await t.expectError("edit_sheets", { op: "revision_add", revision: "No Such Revision", sheet: "Q901" }, "NOT_FOUND");
    });

    await t.step("NOT_FOUND on one of several sheets offers the corrected list", async () => {
      const r = await t.expectError("edit_sheets", { op: "revision_add", revision: "S55 Issue A", sheets: ["Q901", "Q9100"] }, "NOT_FOUND");
      t.assertMatch(r.fix, /"sheets":\["Q901",/, "fix repeats the call with the sheets list");
    });

    await t.step("change_set: create a sheet and place a view as one undo step ($0)", async () => {
      const r = await t.call("change_set", {
        ops: [
          { tool: "edit_sheets", op: "create", number: "Q930", name: "Change set" },
          { tool: "edit_sheets", op: "place", sheet: "$0", view: names.plan2 },
        ],
      });
      t.assertEq(r.status, "ok", "change_set applied");
      t.assert(r.json.created.length >= 2, "sheet and viewport created");
    });
  },
};
