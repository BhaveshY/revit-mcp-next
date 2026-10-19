// S70 family (SPEC §13.3, owner P-FAMILY): read_family and every edit_family op against a loaded family of a
// disposable project, plus the SPEC additions:
//   - a project-side type value of another parameter survives an add_param reload (overwrite false);
//   - load_into two docs returns NOT APPLIED (rule multi_doc) and applies after confirm.
// Also: NEEDS_TYPES, NAME_TAKEN, PARAM_IN_USE, FAMILY_DOC_REQUIRED, NOT_FOUND, preview, change_set family segment,
// open (re-pin with TARGET_NOW), save_as/save into the run folder, read_family path= of the saved file.

import { existsSync } from "node:fs";
import { join } from "node:path";

/** Families of the default metric/imperial templates that are editable and have types (first found wins). */
const PREFERRED = ["M_Single-Flush", "Single-Flush", "M_Door-Single-Panel", "Door-Single-Panel", "M_Fixed", "Fixed", "M_Door Tag", "Door Tag"];

const rowsOf = (table) => (table && Array.isArray(table.rows) ? table.rows : []);
const colIndex = (table, name) => (table && Array.isArray(table.cols) ? table.cols.indexOf(name) : -1);

export default {
  id: "S70",
  title: "Family: read_family, edit_family ops, reload semantics, load_into confirm",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const a = await t.step("disposable project A", async () => {
      const { r, path } = await t.newProject("S70A");
      t.assertEq(r.status, "ok", "new_project A");
      t.setTargetPath(path);
      return { path };
    });

    // ---------------------------------------------------------------- pick a loaded, editable family with types
    const fam = await t.step("find an editable loaded family (read_family)", async () => {
      const tried = [];
      const candidates = [...PREFERRED];
      const probe = await t.call("read_family", { family: "Door" }, { allowError: true });
      if (probe.status === "error" && probe.code === "NOT_FOUND") {
        const found = probe.json?.details?.candidates ?? probe.json?.candidates ?? [];
        for (const name of found) if (!candidates.includes(name)) candidates.push(name);
      } else if (probe.status === "ok") {
        candidates.unshift("Door");
      }
      for (const name of candidates) {
        const r = await t.call("read_family", { family: name }, { allowError: true });
        tried.push(`${name}:${r.code ?? r.status}`);
        if (r.status !== "ok" || r.json?.editable === false) continue;
        const types = rowsOf(r.json?.project_types);
        if (types.length === 0) continue;
        if (!(r.json.params?.total > 0)) continue; // e.g. a tag family without own parameters
        t.assertEq(r.json.source, "loaded", "source");
        return { name: r.json.family, typeId: types[0][0], typeName: types[0][1], typeCount: r.json.types?.total ?? types.length };
      }
      t.assert(false, `no editable loaded family with types found (${tried.join(", ")})`);
    });
    t.log(`family ${fam.name}, type ${fam.typeName} (${fam.typeId}), ${fam.typeCount} types`);
    const F = fam.name;

    // ---------------------------------------------------------------- overwrite false keeps project-side values
    await t.step("project-side type value survives an add_param reload (overwrite false)", async () => {
      const set = await t.call("set_parameters", { ids: [String(fam.typeId)], values: { "Type Comments": "E2E keep" } });
      t.assertEq(set.status, "ok", "set Type Comments on the project type");
      const r = await t.call("edit_family", { op: "add_param", family: F, name: "E2E_Note", data: "text", kind: "type", group: "Identity Data" });
      t.assertEq(r.status, "ok", "add_param");
      t.assertEq(r.json.reload?.overwrite, false, "overwrite defaults to false without type value changes");
      t.assert(Array.isArray(r.json.types_changed) && r.json.types_changed.length === 0, "types_changed is empty");
      t.assertMatch(r.json.write ?? "", /^w\d+$/, "write id");
      const d = await t.call("describe_elements", { ids: [String(fam.typeId)], params: "all", match: "Type Comments" });
      t.assertMatch(d.text, /E2E keep/, "the project-side Type Comments value survived the reload");
      const read = await t.call("read_family", { family: F });
      t.assert(rowsOf(read.json.params).some((row) => row[0] === "E2E_Note"), "E2E_Note is in the family");
    });

    await t.step("NAME_TAKEN for a duplicate parameter", async () => {
      const r = await t.expectError("edit_family", { op: "add_param", family: F, name: "E2E_Note", data: "text" }, "NAME_TAKEN");
      t.assertMatch(r.fix, /E2E_Note 2/, "fix suggests the next free name");
    });

    // ---------------------------------------------------------------- values per type and the overwrite default
    await t.step("set_values on all types overwrites project values and lists types_changed", async () => {
      if (fam.typeCount > 1) {
        const needs = await t.expectError("edit_family", { op: "set_values", family: F, name: "E2E_Note", value: "x" }, "NEEDS_TYPES");
        t.assertMatch(needs.fix, /types/, "NEEDS_TYPES fix names types");
      }
      const r = await t.call("edit_family", { op: "set_values", family: F, name: "E2E_Note", value: "hello", types: ["*"] });
      t.assertEq(r.json.reload?.overwrite, true, "overwrite true when type values changed");
      t.assertEq(r.json.types_changed.length, fam.typeCount, "every type changed");
      const read = await t.call("read_family", { family: F });
      t.assertEq(read.json.types?.same?.E2E_Note, "hello", "equal values are reported under same");
    });

    // ---------------------------------------------------------------- types: add, rename (project follows), delete
    await t.step("add_type / rename_type / delete_type reach the project", async () => {
      const added = await t.call("edit_family", { op: "add_type", family: F, name: "E2E Type", copy_from: fam.typeName, values: { E2E_Note: "t2" } });
      const created = added.json.reload?.types_created ?? [];
      t.assert(created.some((row) => row[1] === "E2E Type"), "the new type exists in the project");
      const renamed = await t.call("edit_family", { op: "rename_type", family: F, name: "E2E Type", new_name: "E2E Type R" });
      const projectTypes = renamed.json.project_types ?? [];
      t.assert(projectTypes.some((row) => row[1] === "E2E Type R"), "the project type was renamed");
      t.assert(!projectTypes.some((row) => row[1] === "E2E Type"), "no stale copy under the old name");
      const deleted = await t.call("edit_family", { op: "delete_type", family: F, name: "E2E Type R" });
      t.assert((deleted.json.reload?.types_removed ?? []).some((row) => row[1] === "E2E Type R"), "the unused project type was removed");
    });

    // ---------------------------------------------------------------- formulas and parameter changes
    await t.step("formula, PARAM_IN_USE, clear, rename, set_param, remove", async () => {
      await t.call("edit_family", { op: "add_param", family: F, name: "E2E_Len", data: "length", value: "1200" });
      await t.call("edit_family", { op: "add_param", family: F, name: "E2E_Half", data: "length", formula: "E2E_Len / 2" });
      const inUse = await t.expectError("edit_family", { op: "remove_param", family: F, name: "E2E_Len" }, "PARAM_IN_USE");
      t.assertMatch(inUse.summary, /E2E_Half/, "PARAM_IN_USE names the formula");
      const bad = await t.expectError("edit_family", { op: "set_formula", family: F, name: "E2E_Half", formula: "E2E_Len / / 2" }, "FORMULA_INVALID");
      t.assert(!!bad.fix, "FORMULA_INVALID has a fix");
      const read = await t.call("read_family", { family: F, detail: "full" });
      const fi = colIndex(read.json.params, "formula");
      const half = rowsOf(read.json.params).find((row) => row[0] === "E2E_Half");
      t.assert(half && /E2E_Len/.test(String(half[fi])), "the formula is listed");
      await t.call("edit_family", { op: "set_formula", family: F, name: "E2E_Half", formula: "" });
      await t.call("edit_family", { op: "remove_param", family: F, name: "E2E_Len" });
      await t.call("edit_family", { op: "rename_param", family: F, name: "E2E_Half", new_name: "E2E_Half2" });
      const kind = await t.call("edit_family", { op: "set_param", family: F, name: "E2E_Half2", kind: "instance", group: "Dimensions" });
      t.assertEq(kind.json.param?.kind, "instance", "now an instance parameter");
      t.assertEq(kind.json.param?.group, "Dimensions", "group Dimensions");
      await t.call("edit_family", { op: "remove_param", family: F, name: "E2E_Half2" });
    });

    // ---------------------------------------------------------------- preview and change_set (one family session)
    await t.step("preview changes nothing; change_set edits the family once", async () => {
      const preview = await t.call("edit_family", { op: "add_param", family: F, name: "E2E_Preview", data: "text", preview: true });
      t.assertMatch(preview.summary, /^preview/, "preview summary");
      const cs = await t.call("change_set", {
        name: "e2e family",
        ops: [
          { tool: "edit_family", op: "add_param", family: F, name: "E2E_CS", data: "integer" },
          { tool: "edit_family", op: "set_values", family: F, name: "E2E_CS", value: "7", types: ["*"] },
        ],
      });
      t.assertEq(cs.status, "ok", "change_set");
      const read = await t.call("read_family", { family: F });
      const names = rowsOf(read.json.params).map((row) => row[0]);
      t.assert(!names.includes("E2E_Preview"), "the preview added nothing");
      t.assert(names.includes("E2E_CS"), "the change_set added E2E_CS");
      t.assertEq(read.json.types?.same?.E2E_CS, 7, "and set its value on every type");
    });

    await t.step("argument errors: FAMILY_DOC_REQUIRED, NOT_FOUND", async () => {
      await t.expectError("edit_family", { op: "add_param", name: "E2E_X", data: "text" }, "FAMILY_DOC_REQUIRED");
      const nf = await t.expectError("edit_family", { op: "add_param", family: "E2E no such family", name: "E2E_X" }, "NOT_FOUND");
      t.assert(!!nf.fix, "NOT_FOUND has a fix");
    });

    // ---------------------------------------------------------------- open, direct edits, save_as, save, load_into
    const opened = await t.step("open keeps the family as doc #n and re-pins", async () => {
      const r = await t.call("edit_family", { op: "open", family: F });
      const notice = r.notices.find((n) => n.startsWith("TARGET_NOW"));
      t.assert(notice, `TARGET_NOW notice: ${r.notices.join(" | ")}`);
      const n = /#(\d+)/.exec(notice)?.[1];
      t.assert(n, "doc number in TARGET_NOW");
      const category = r.json?.category;
      t.assert(typeof category === "string" && category.length > 0, "open reports the family category");
      const same = await t.call("edit_family", { op: "set_category", category });
      t.assertEq(same.json?.unchanged, true, "set_category to its own category changes nothing");
      const edit = await t.call("edit_family", { op: "add_param", name: "E2E_Open", data: "yesno", kind: "instance" });
      t.assertEq(edit.status, "ok", "direct add_param on the open family doc");
      const read = await t.call("read_family", {});
      t.assertEq(read.json.source, "doc", "reads the pinned family doc");
      t.assert(rowsOf(read.json.params).some((row) => row[0] === "E2E_Open"), "E2E_Open in the open doc");
      return { n: `#${n}` };
    });

    const saved = await t.step("save_as into the run folder, then save", async () => {
      const path = join(t.yearDir, `${F.replace(/[^A-Za-z0-9_-]+/g, "_")}_e2e.rfa`);
      const r = await t.call("edit_family", { op: "save_as", path });
      t.assertEq(r.status, "ok", "save_as");
      t.assert(existsSync(path), `the file exists: ${path}`);
      const again = await t.call("edit_family", { op: "save" });
      t.assertEq(again.status, "ok", "save");
      const file = await t.call("read_family", { path });
      t.assertEq(file.json.source, "file", "read_family path=");
      t.assert(rowsOf(file.json.params).some((row) => row[0] === "E2E_Open"), "the saved file has E2E_Open");
      return { path };
    });
    t.log(`saved ${saved.path}`);

    await t.step("load_into the project pin, then two docs need the user's OK", async () => {
      const one = await t.call("edit_family", { op: "load_into" });
      t.assertEq(one.status, "ok", "load_into (project pin)");
      t.assertEq(one.json.loaded?.length, 1, "one target");
      t.assertMatch(String(one.json.loaded[0].doc), /^S70A(\.rvt)?$/i, "loaded into S70A");

      const b = await t.newProject("S70B");
      t.assertEq(b.r.status, "ok", "new_project B");
      t.setTargetPath(b.path);
      await t.call("set_target", { doc: opened.n });
      const two = await t.call("edit_family", { op: "load_into", into: ["S70A", "S70B"] });
      t.assertEq(two.status, "not_applied", "two docs need confirmation");
      t.assertEq(two.json.rule, "multi_doc", "rule multi_doc");
      t.assertMatch(two.next ?? "", /"confirm":"[A-Z0-9]{6}"/, "next carries the confirm token");
      const confirmed = await t.call("edit_family", { op: "load_into", confirm: two.json.confirm });
      t.assertEq(confirmed.status, "ok", "confirmed load_into");
      t.assertEq(confirmed.json.loaded?.length, 2, "loaded into both");
    });

    t.log(`project A at ${a.path}`);
  },
};
