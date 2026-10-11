// S50 views (SPEC §13.3; owner P-VIEWS-VISUAL). get_view, edit_views (create of every common kind, duplicate, rename,
// set incl. view range and crop, create_template, TEMPLATE_CONTROLLED, delete, preview) and view_graphics (category and
// element overrides, permanent hide/unhide, color_by legend, view filters create/edit/apply/remove, reset).
// Runs in a disposable project created from the metric template.

const near = (a, b, tol = 1) => typeof a === "number" && Math.abs(a - b) <= tol;

export async function levelsOf(t) {
  const r = await t.call("status", {});
  const rows = (r.json?.levels?.rows ?? []).slice().sort((a, b) => a[1] - b[1]);
  t.assert(rows.length >= 1, `status lists the levels of the target doc: ${r.jsonText?.slice(0, 200)}`);
  return rows.map((row) => row[0]);
}

export default {
  id: "S50",
  title: "Views: get_view, edit_views create/duplicate/rename/set/template/delete, view_graphics overrides and filters",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S50");
    t.setTargetPath(path);
    const levels = await levelsOf(t);
    const L1 = levels[0];
    const L2 = levels[1] ?? levels[0];

    await t.step("get_view {} describes the active view", async () => {
      const r = await t.call("get_view", {});
      t.assertEq(r.status, "ok", "get_view status");
      t.assert(typeof r.json?.id === "number" && typeof r.json?.name === "string", `id and name: ${r.jsonText}`);
      t.assert(r.json.active === true, "the active view is flagged active");
    });

    const plan = await t.step("create floor_plan on the first level with a name", async () => {
      const r = await t.call("edit_views", { op: "create", kind: "floor_plan", level: L1, name: "E2E Plan", template: "none" });
      t.assertEq(r.status, "ok", "create status");
      t.assertEq(r.json.name, "E2E Plan", "name");
      t.assertEq(r.json.type, "FloorPlan", "type");
      t.assert(Array.isArray(r.json.created) && r.json.created.includes(r.json.id), "created lists the view");
      t.assert(typeof r.json.write === "string" && r.json.write.startsWith("w"), "write id");
      const g = await t.call("get_view", { view: "E2E Plan" });
      t.assertEq(g.json.level, L1, "plan level");
      return r.json;
    });

    await t.step("set scale, detail level and view range; get_view reads them back", async () => {
      await t.call("edit_views", { op: "set", view: "E2E Plan", scale: 50, detail_level: "fine", view_range: { cut: 1100 } });
      const g = await t.call("get_view", { view: plan.id });
      t.assertEq(g.json.scale, 50, "scale");
      t.assertEq(g.json.detail_level, "fine", "detail level");
      t.assert(near(g.json.view_range?.cut, 1100), `cut plane 1100 mm: ${JSON.stringify(g.json.view_range)}`);
    });

    await t.step("crop_box turns the crop on with the given model rectangle", async () => {
      await t.call("edit_views", { op: "set", view: "E2E Plan", crop_box: [0, 0, 24000, 12000], crop_visible: true });
      const g = await t.call("get_view", { view: "E2E Plan" });
      t.assertEq(g.json.crop?.active, true, "crop active");
      t.assertEq(g.json.crop?.visible, true, "crop visible");
      const box = g.json.crop?.box ?? [];
      t.assert(near(box[0], 0, 2) && near(box[1], 0, 2) && near(box[2], 24000, 2) && near(box[3], 12000, 2), `crop box ${JSON.stringify(box)}`);
    });

    await t.step("rename; a duplicate name gives NAME_TAKEN with a free suggestion", async () => {
      const r = await t.call("edit_views", { op: "rename", view: "E2E Plan", name: "E2E Plan R" });
      t.assertMatch(r.summary, /E2E Plan R/, "rename summary");
      await t.call("edit_views", { op: "create", kind: "floor_plan", level: L2, name: "E2E Plan 2", template: "none" });
      const taken = await t.expectError("edit_views", { op: "rename", view: "E2E Plan 2", name: "E2E Plan R" }, "NAME_TAKEN");
      t.assertMatch(taken.fix ?? "", /E2E Plan R \(2\)/, "fix suggests a free name");
    });

    await t.step("preview rename changes nothing and returns a confirm token", async () => {
      const r = await t.call("edit_views", { op: "rename", view: "E2E Plan 2", name: "E2E Preview", preview: true });
      t.assertMatch(r.summary, /^preview/, "preview summary");
      t.assert(typeof r.json?.confirm === "string", "confirm token");
      await t.expectError("get_view", { view: "E2E Preview" }, "NOT_FOUND");
    });

    await t.step("section looks left of start->end with the far clip at depth (V5)", async () => {
      const r = await t.call("edit_views", { op: "create", kind: "section", start: [0, 4000], end: [12000, 4000], depth: 5000, name: "E2E Section" });
      t.assertEq(r.json.type, "Section", "section type");
      const g = await t.call("get_view", { view: "E2E Section" });
      t.assert(near(g.json.far_clip, 5000, 2), `far clip 5000: ${g.json.far_clip}`);
      const [x, y] = g.json.looking ?? [];
      t.assert(near(x, 0, 0.01) && near(y, 1, 0.01), `looks north (+Y): ${JSON.stringify(g.json.looking)}`);
    });

    await t.step("elevation marker index maps to the requested direction (V5)", async () => {
      for (const [direction, expected] of [["north", [0, 1]], ["east", [1, 0]]]) {
        const r = await t.call("edit_views", { op: "create", kind: "elevation", at: [6000, -3000], direction, level: L1, name: `E2E Elev ${direction}` });
        const g = await t.call("get_view", { view: r.json.id });
        const [x, y] = g.json.looking ?? [];
        t.assert(near(x, expected[0], 0.01) && near(y, expected[1], 0.01), `${direction}: looking ${JSON.stringify(g.json.looking)}`);
      }
    });

    await t.step("3D view with orient and section box", async () => {
      await t.call("edit_views", { op: "create", kind: "3d", name: "E2E 3D", orient: "iso_ne", section_box: [0, 0, 0, 12000, 8000, 7000] });
      const g = await t.call("get_view", { view: "E2E 3D" });
      t.assertEq(g.json.perspective, false, "isometric");
      t.assertEq(g.json.orient, "iso_ne", "orientation preset");
      const box = g.json.section_box ?? [];
      t.assert(near(box[3], 12000, 2) && near(box[5], 7000, 2), `section box ${JSON.stringify(box)}`);
      await t.call("edit_views", { op: "set", view: "E2E 3D", section_box: [] });
      const off = await t.call("get_view", { view: "E2E 3D" });
      t.assert(off.json.section_box === undefined, "section box off");
    });

    await t.step("drafting, ceiling plan, callout and legend (or LEGEND_NEEDS_SEED)", async () => {
      const d = await t.call("edit_views", { op: "create", kind: "drafting", name: "E2E Drafting", scale: 10 });
      t.assertEq(d.json.scale, 10, "drafting scale");
      await t.call("edit_views", { op: "create", kind: "ceiling_plan", level: L1, name: "E2E RCP" });
      const c = await t.call("edit_views", { op: "create", kind: "callout", view: "E2E Plan R", region: [0, 0, 4000, 3000], name: "E2E Callout" });
      t.assertEq(c.status, "ok", "callout");
      const legend = await t.call("edit_views", { op: "create", kind: "legend", name: "E2E Legend" }, { allowError: true });
      t.assert(legend.status === "ok" || legend.code === "LEGEND_NEEDS_SEED", `legend: ${legend.status} ${legend.code ?? ""}`);
    });

    await t.step("duplicate as dependent and with detailing", async () => {
      const r = await t.call("edit_views", { op: "duplicate", view: "E2E Plan R", mode: "dependent", name: "E2E Plan R - A" });
      const g = await t.call("get_view", { view: r.json.id });
      t.assertEq(g.json.dependent_of, "E2E Plan R", "dependent of the parent");
      const copy = await t.call("edit_views", { op: "duplicate", view: "E2E Plan R", mode: "detailing" });
      t.assert(copy.json.name && copy.json.name !== "E2E Plan R", `Revit names the copy: ${copy.json.name}`);
    });

    await t.step("create_template; a template-controlled setting gives TEMPLATE_CONTROLLED", async () => {
      await t.call("edit_views", { op: "create_template", view: "E2E Plan R", name: "E2E Template" });
      await t.call("edit_views", { op: "set", view: "E2E Plan 2", template: "E2E Template" });
      const g = await t.call("get_view", { view: "E2E Plan 2" });
      t.assertEq(g.json.template, "E2E Template", "template applied");
      t.assert(Array.isArray(g.json.template_controls) && g.json.template_controls.includes("scale"), `template controls scale: ${JSON.stringify(g.json.template_controls)}`);
      const e = await t.expectError("edit_views", { op: "set", view: "E2E Plan 2", scale: 200 }, "TEMPLATE_CONTROLLED");
      t.assertMatch(e.fix ?? "", /E2E Template/, "fix names the template");
      await t.call("edit_views", { op: "set", view: "E2E Plan 2", template: "none" });
    });

    const walls = await t.step("model content for graphics (two walls)", async () => {
      const r = await t.call("create_elements", { op: "wall", level: L1, start: [0, 2000], end: [10000, 2000], height: 3000 });
      const r2 = await t.call("create_elements", { op: "wall", level: L1, start: [0, 6000], end: [10000, 6000], height: 3000 });
      await t.call("set_parameters", { ids: [String(r.json.created[0])], values: { Comments: "E2E-A" } });
      return [r.json.created[0], r2.json.created[0]];
    });

    await t.step("view_graphics category override and element override read back by get_view", async () => {
      await t.call("view_graphics", { op: "override", view: "E2E Plan R", category: ["Walls"], line_weight: 6, color: "0,0,160" });
      await t.call("view_graphics", { op: "override", view: "E2E Plan R", ids: [String(walls[0])], color: "#FF0000", fill: true });
      const g = await t.call("get_view", { view: "E2E Plan R", include: ["overrides"] });
      const walls_ = (g.json.overrides?.categories ?? []).find((c) => c.category === "Walls");
      t.assert(walls_ && walls_.line_weight === 6 && walls_.color === "#0000A0", `Walls category override: ${JSON.stringify(walls_)}`);
      const el = (g.json.overrides?.elements ?? []).find((e) => e.id === walls[0]);
      t.assert(el && el.color === "#FF0000" && el.fill === "#FF0000", `element override: ${JSON.stringify(el)}`);
    });

    await t.step("permanent hide/unhide of a category", async () => {
      await t.call("view_graphics", { op: "hide", view: "E2E Plan R", category: ["Furniture"] });
      let g = await t.call("get_view", { view: "E2E Plan R", include: ["categories"] });
      t.assert((g.json.hidden_categories?.hidden ?? []).includes("Furniture"), "Furniture hidden");
      await t.call("view_graphics", { op: "unhide", view: "E2E Plan R", category: ["Furniture"] });
      g = await t.call("get_view", { view: "E2E Plan R", include: ["categories"] });
      t.assert(!(g.json.hidden_categories?.hidden ?? []).includes("Furniture"), "Furniture shown again");
      const h = await t.call("view_graphics", { op: "hide", view: "E2E Plan R", ids: [String(walls[1])] });
      t.assertEq(h.json.changed, 1, "one element hidden");
      await t.call("view_graphics", { op: "unhide", view: "E2E Plan R", ids: [String(walls[1])] });
    });

    await t.step("color_by returns a legend with one color per value", async () => {
      const r = await t.call("view_graphics", { op: "color_by", view: "E2E Plan R", category: ["Walls"], param: "Comments" });
      const legend = r.json.legend ?? [];
      t.assert(legend.length >= 1, `legend: ${JSON.stringify(legend)}`);
      t.assertEq(legend.reduce((n, row) => n + row[2], 0), r.json.elements, "legend counts add up");
      t.assert(legend.every((row) => /^#[0-9A-F]{6}$/.test(row[1])), "hex colors");
    });

    await t.step("view filter create with rules, apply, edit, remove", async () => {
      const c = await t.call("view_graphics", { op: "filter_create", view_filter: "E2E Walls", categories: ["Walls"], rules: ["Comments contains E2E"], view: "E2E Plan R", color: "red" });
      t.assertEq(c.status, "ok", "filter_create");
      const dup = await t.expectError("view_graphics", { op: "filter_create", view_filter: "E2E Walls", categories: ["Walls"], rules: ["Comments notempty"] }, "NAME_TAKEN");
      t.assertMatch(dup.fix ?? "", /E2E Walls \(2\)/, "fix suggests a free filter name");
      await t.expectError("view_graphics", { op: "filter_create", view_filter: "E2E Bad", categories: ["Walls"], rules: ["NoSuchParam = 1"] }, "NOT_FOUND");
      const g = await t.call("get_view", { view: "E2E Plan R", include: ["filters"] });
      const f = (g.json.filters ?? []).find((x) => x.name === "E2E Walls");
      t.assert(f && f.visible === true && f.overrides?.color === "#FF0000", `filter applied with red: ${JSON.stringify(f)}`);
      await t.call("view_graphics", { op: "filter_apply", view_filter: "E2E Walls", views: ["E2E Section", "E2E Plan 2"], visible: false });
      await t.call("view_graphics", { op: "filter_edit", view_filter: "E2E Walls", rules: ["Comments startswith E2E", "Comments != X"] });
      const removed = await t.call("view_graphics", { op: "filter_remove", view_filter: "E2E Walls", view: "E2E Section" });
      t.assertEq(removed.json.removed_from, 1, "removed from one view");
    });

    await t.step("reset without a target clears the view's overrides", async () => {
      await t.call("view_graphics", { op: "reset", view: "E2E Plan R" });
      const g = await t.call("get_view", { view: "E2E Plan R", include: ["overrides"] });
      t.assertEq((g.json.overrides?.categories ?? []).length, 0, "no category overrides");
      t.assertEq(g.json.overrides?.elements_total ?? 0, 0, "no element overrides");
    });

    await t.step("delete views; the deleted view is gone", async () => {
      const r = await t.call("edit_views", { op: "delete", views: ["E2E 3D", "E2E Drafting"] });
      t.assert(r.json.deleted.length >= 2, `deleted ${JSON.stringify(r.json.deleted)}`);
      await t.expectError("get_view", { view: "E2E 3D" }, "NOT_FOUND");
    });
  },
};
