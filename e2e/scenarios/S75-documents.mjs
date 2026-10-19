// S75 documents (SPEC §13.3, P-DOCS): manage_document new_project/save/save_as/open/activate/close (job steps,
// never the active doc directly), file_overwrite and unsaved_close confirms, new_family, set_units, coordinates.
import { existsSync } from "node:fs";
import { join } from "node:path";

/** Waits for a running job (j#) through job_status; returns the final result. */
async function settle(t, r, label) {
  let current = r;
  for (let i = 0; i < 12 && current.status === "running"; i++) {
    const ref = current.json?.job ?? current.json?.ref;
    t.assert(ref, `${label}: running result names its job`);
    current = await t.call("job_status", { id: ref, wait: 30 }, { timeoutMs: 60_000 });
  }
  return current;
}

/** Applies a NOT APPLIED plan with its confirm token. */
async function confirm(t, tool, r, op) {
  t.assertEq(r.status, "not_applied", `${tool}.${op} asks to confirm`);
  t.assert(r.json?.confirm, `${tool}.${op} carries a confirm token`);
  return t.call(tool, { op, confirm: r.json.confirm }, { timeoutMs: 120_000 });
}

export default {
  id: "S75",
  title: "Documents: new/save/save_as/open/activate/close, overwrite and unsaved confirms, new_family, units, coordinates",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    // Per-year name: with --years 2024,2027 a copy left open in the other Revit would make the title ambiguous.
    const BC = `S75Bcopy${t.year}`;
    const a = await t.step("new_project A re-pins", async () => {
      const { r, path } = await t.newProject("S75A");
      t.assertEq(r.status, "ok", "new_project A");
      t.setTargetPath(path);
      return path;
    });
    const b = await t.step("new_project B (activate true) moves the pin to B", async () => {
      const { r, path } = await t.newProject("S75B");
      t.assert(r.notices.some((n) => n.startsWith("TARGET_NOW")), "TARGET_NOW for B");
      t.setTargetPath(path);
      return path;
    });

    await t.step("save B and save_as a copy", async () => {
      const saved = await t.call("manage_document", { op: "save" });
      t.assertEq(saved.status, "ok", "save");
      const copy = join(t.yearDir, `${BC}.rvt`);
      const preview = await t.call("manage_document", { op: "save_as", path: copy, preview: true });
      t.assertMatch(preview.summary, /^preview: /, "save_as preview");
      t.assert(!existsSync(copy), "preview wrote nothing");
      const r = await t.call("manage_document", { op: "save_as", path: copy }, { timeoutMs: 120_000 });
      t.assertEq(r.status, "ok", "save_as");
      t.assert(existsSync(copy), "copy exists");
      t.assertEq(r.doc?.title.replace(/\.rvt$/i, ""), BC, "the doc is now the copy");
      t.docs.set(BC, copy);
      t.setTargetPath(copy);
      await t.expectError("manage_document", { op: "save_as", path: a }, "INVALID_ARGS");
    });

    await t.step("activate A re-pins with TARGET_NOW", async () => {
      const r = await t.call("manage_document", { op: "activate", doc: "S75A" });
      t.assertEq(r.status, "ok", "activate");
      t.assert(r.notices.some((n) => /^TARGET_NOW #\d+ S75A /.test(n)), `TARGET_NOW A: ${r.notices.join(" | ")}`);
      t.setTargetPath(a);
    });

    await t.step("close a background doc directly; opening it again; ALREADY_OPEN", async () => {
      const closed = await t.call("manage_document", { op: "close", doc: BC });
      const done = await settle(t, closed, "close B-copy");
      t.assertEq(done.status, "ok", "close B-copy");
      const opened = await t.call("manage_document", { op: "open", path: join(t.yearDir, `${BC}.rvt`), activate: false }, { timeoutMs: 120_000 });
      t.assertEq(opened.status, "ok", "open in the background");
      const again = await t.call("manage_document", { op: "open", path: join(t.yearDir, `${BC}.rvt`), activate: false });
      t.assert(again.notices.some((n) => n.startsWith("ALREADY_OPEN")), "already open notice");
      await t.call("set_target", { doc: "S75A" });
      t.setTargetPath(a);
    });

    await t.step("file_overwrite: new_project over a closed file needs confirm", async () => {
      const { r, path } = await t.newProject("S75Z", { levels: false }); // unmodified, so close needs no confirm
      t.assertEq(r.status, "ok", "new_project Z");
      t.setTargetPath(path);
      const closed = await settle(t, await t.call("manage_document", { op: "close", doc: "S75Z" }), "close Z");
      t.assertEq(closed.status, "ok", "close Z (active: via job steps)");
      const again = await t.call("manage_document", { op: "new_project", path, template: t.projectTemplate(), activate: true }, { timeoutMs: 120_000 });
      const applied = await confirm(t, "manage_document", again, "new_project");
      t.assertEq(applied.status, "ok", "confirmed overwrite");
      t.setTargetPath(path);
    });

    await t.step("unsaved_close: closing a modified doc without save needs confirm", async () => {
      const units = await t.call("manage_document", { op: "set_units", unit: "m", accuracy: 0.01 });
      t.assertEq(units.status, "ok", "set_units m");
      t.assertMatch(units.summary, /m/i, "units summary");
      const r = await t.call("manage_document", { op: "close", doc: "S75Z" });
      const applied = await settle(t, await confirm(t, "manage_document", r, "close"), "close Z unsaved");
      t.assertEq(applied.status, "ok", "closed after confirm");
      await t.call("set_target", { doc: "S75A" });
      t.setTargetPath(a);
    });

    await t.step("set_units back to mm; coordinates always confirm", async () => {
      const units = await t.call("manage_document", { op: "set_units", unit: "mm", accuracy: 1 });
      t.assertEq(units.status, "ok", "set_units mm");
      const r = await t.call("manage_document", { op: "coordinates", true_north: 10 });
      const applied = await confirm(t, "manage_document", r, "coordinates");
      t.assertEq(applied.status, "ok", "coordinates");
      t.assert(Math.abs(Number(applied.json?.after?.true_north) - 10) < 0.01, `true north 10: ${applied.jsonText}`);
    });

    await t.step("new_family from a template name, then close it through job steps", async () => {
      const path = join(t.yearDir, "S75 Generic.rfa");
      const r = await t.call("manage_document", { op: "new_family", template: "Metric Generic Model", path }, { timeoutMs: 120_000, allowError: true });
      if (r.status === "error" && r.code === "NOT_FOUND") {
        t.log(`no 'Metric Generic Model' template on this PC: ${r.summary}`);
        return;
      }
      t.assertEq(r.status, "ok", "new_family");
      t.assert(existsSync(path), "family saved");
      t.assert(r.notices.some((n) => n.startsWith("TARGET_NOW")), "family pinned");
      const closed = await settle(t, await t.call("manage_document", { op: "close", doc: "S75 Generic" }), "close family");
      t.assertEq(closed.status, "ok", "family closed");
      await t.call("set_target", { doc: "S75A" });
      t.setTargetPath(a);
    });

    await t.step("save_as onto an existing closed file needs confirm (not applied here)", async () => {
      const r = await t.call("manage_document", { op: "save_as", path: b });
      t.assertEq(r.status, "not_applied", "file_overwrite");
      t.assertEq(r.json?.rule, "file_overwrite", "rule");
    });
  },
};
