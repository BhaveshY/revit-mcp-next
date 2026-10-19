// S80 links (SPEC §13.3, P-DOCS): link_rvt (origin/shared/base_point), unload/reload/reload_from, remove and
// acquire_coordinates behind the always-confirm, link_cad/import_cad of a DWG exported from the host.
import { existsSync, readdirSync } from "node:fs";
import { join } from "node:path";

/** A plan view reference: the name when unique, else the id of the floor plan among same-named views. */
async function viewRef(t, name, folder) {
  const r = await t.call("export", { format: "csv", views: [name], folder }, { allowError: true });
  if (r.code === "AMBIGUOUS_NAME") {
    const candidates = r.json?.details?.candidates ?? [];
    const pick = candidates.find((c) => /FloorPlan/.test(c)) ?? candidates[0];
    const m = /id (\d+)/.exec(pick ?? "");
    if (m) return m[1];
  }
  return name;
}

async function settle(t, r, label) {
  let current = r;
  for (let i = 0; i < 12 && current.status === "running"; i++) {
    const ref = current.json?.job ?? current.json?.ref;
    t.assert(ref, `${label}: running result names its job`);
    current = await t.call("job_status", { id: ref, wait: 30 }, { timeoutMs: 60_000 });
  }
  return current;
}

async function confirm(t, tool, r, op) {
  t.assertEq(r.status, "not_applied", `${tool}.${op} asks to confirm`);
  t.assert(r.json?.confirm, `${tool}.${op} carries a confirm token`);
  return settle(t, await t.call(tool, { op, confirm: r.json.confirm }, { timeoutMs: 120_000 }), `${tool}.${op}`);
}

export default {
  id: "S80",
  title: "Links: link/unload/reload/reload_from/remove RVT, acquire coordinates, link and import CAD",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const linked = await t.step("create the model to link and close it", async () => {
      const { path } = await t.newProject("Linked80");
      const { path: second } = await t.newProject("Linked80b");
      const { path: host } = await t.newProject("Host80");
      t.setTargetPath(host);
      for (const title of ["Linked80", "Linked80b"]) {
        // The level normalization modified the new doc: save it so close needs no unsaved_close confirm.
        const saved = await t.call("manage_document", { op: "save", doc: title });
        t.assertEq(saved.status, "ok", `save ${title}`);
        const closed = await settle(t, await t.call("manage_document", { op: "close", doc: title }), `close ${title}`);
        t.assertEq(closed.status, "ok", `close ${title}`);
      }
      return { path, second, host };
    });

    const linkId = await t.step("link_rvt origin to origin; the link is a write with an undo hint", async () => {
      const r = await t.call("links", { op: "link_rvt", path: linked.path }, { timeoutMs: 120_000 });
      t.assertEq(r.status, "ok", "link_rvt");
      t.assert(Array.isArray(r.json?.created) && r.json.created.length >= 1, "created ids");
      t.assert(r.json?.undo, "undo hint");
      t.assert(r.json?.id, "instance id");
      await t.expectError("links", { op: "link_rvt", path: join(t.yearDir, "missing.rvt") }, "NOT_FOUND");
      return String(r.json.id);
    });

    await t.step("unload, reload, reload_from another file", async () => {
      const unloaded = await t.call("links", { op: "unload", link: "Linked80" });
      t.assertEq(unloaded.status, "ok", "unload");
      t.assertEq(unloaded.json?.loaded, false, "unloaded");
      const reloaded = await settle(t, await t.call("links", { op: "reload", link: "Linked80" }, { timeoutMs: 120_000 }), "reload");
      t.assertEq(reloaded.status, "ok", "reload");
      t.assertEq(reloaded.json?.loaded, true, "loaded again");
      const from = await settle(t, await t.call("links", { op: "reload_from", link: linkId, path: linked.second }, { timeoutMs: 120_000 }), "reload_from");
      t.assertEq(from.status, "ok", "reload_from");
    });

    await t.step("acquire_coordinates and remove always confirm", async () => {
      const acquired = await confirm(t, "links", await t.call("links", { op: "acquire_coordinates", link: linkId }), "acquire_coordinates");
      t.assertEq(acquired.status, "ok", "acquire_coordinates");
      const removed = await confirm(t, "links", await t.call("links", { op: "remove", link: linkId }), "remove");
      t.assertEq(removed.status, "ok", "remove");
      t.assert((removed.json?.deleted ?? []).length >= 1, "deleted ids");
      await t.expectError("links", { op: "reload", link: linkId }, "NOT_FOUND");
    });

    await t.step("link_rvt with base_point placement", async () => {
      const shared = await t.call("links", { op: "link_rvt", path: linked.second, position: "base_point" }, { timeoutMs: 120_000 });
      t.assertEq(shared.status, "ok", "link_rvt base_point");
    });

    await t.step("link_ifc of an IFC exported from the host (Revit converts it to <file>.ifc.RVT)", async () => {
      const folder = join(t.yearDir, "ifc80");
      const exported = await settle(t, await t.call("export", { format: "ifc", folder }, { timeoutMs: 180_000, allowError: true }), "export ifc");
      if (exported.status === "error") {
        t.log(`IFC export unavailable (${exported.code}); link_ifc is checked through its NOT_FOUND path only`);
        await t.expectError("links", { op: "link_ifc", path: join(folder, "missing.ifc") }, "NOT_FOUND");
        return;
      }
      const ifc = readdirSync(folder).filter((f) => f.toLowerCase().endsWith(".ifc")).map((f) => join(folder, f))[0];
      t.assert(ifc && existsSync(ifc), "an IFC was exported");
      const r = await settle(t, await t.call("links", { op: "link_ifc", path: ifc }, { timeoutMs: 300_000, allowError: true }), "link_ifc");
      if (r.status === "error") {
        t.assertEq(r.code, "REVIT_REFUSED", `link_ifc: ${r.code} ${r.summary.slice(0, 200)}`);
        t.log(`link_ifc refused by Revit: ${r.summary.slice(0, 200)}`);
        await t.expectError("links", { op: "link_ifc", path: ifc }, "REVIT_REFUSED", { timeoutMs: 300_000 });
        return;
      }
      t.assert(Array.isArray(r.json?.created) && r.json.created.length >= 1, "the IFC link instance was created");
      t.assert(existsSync(`${ifc}.RVT`) || existsSync(`${ifc}.rvt`), "Revit wrote the .ifc.RVT cache next to the IFC");
    });

    await t.step("link_cad and import_cad of a DWG exported from the host", async () => {
      const folder = join(t.yearDir, "dwg80");
      const level1 = await viewRef(t, "Level 1", folder);
      const exported = await settle(t, await t.call("export", { format: "dwg", views: [level1], folder }, { timeoutMs: 120_000, allowError: true }), "export dwg");
      if (exported.status === "error") {
        t.log(`DWG export unavailable (${exported.code}); CAD link part skipped`);
        return;
      }
      const dwg = readdirSync(folder).filter((f) => f.toLowerCase().endsWith(".dwg")).map((f) => join(folder, f))[0];
      t.assert(dwg && existsSync(dwg), "a DWG was exported");
      const link = await t.call("links", { op: "link_cad", path: dwg, view: level1, cad_units: "mm" }, { timeoutMs: 120_000 });
      t.assertEq(link.status, "ok", "link_cad");
      const imported = await t.call("links", { op: "import_cad", path: dwg, view: level1, this_view_only: true }, { timeoutMs: 120_000 });
      t.assertEq(imported.status, "ok", "import_cad");
      const unload = await t.expectError("links", { op: "unload", link: String(link.json?.id ?? "") }, ["UNSUPPORTED_OP", "NOT_FOUND", "AMBIGUOUS_NAME"]);
      t.assert(!!unload.fix, "CAD unload explains");
    });
  },
};
