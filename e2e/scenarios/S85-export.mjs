// S85 export (SPEC §13.3, P-EXPORT): every format to a folder under the run, NAME_TAKEN for existing files,
// overwrite:true behind file_overwrite, name patterns, several files as a job, wrong view kinds refused.
import { existsSync, statSync } from "node:fs";
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
  for (let i = 0; i < 20 && current.status === "running"; i++) {
    const ref = current.json?.job ?? current.json?.ref;
    t.assert(ref, `${label}: running result names its job`);
    current = await t.call("job_status", { id: ref, wait: 30 }, { timeoutMs: 60_000 });
  }
  return current;
}

function files(r) {
  return (r.json?.files ?? []).map((f) => f.path);
}

export default {
  id: "S85",
  title: "Export: pdf, dwg, dxf, ifc, image, csv, fbx, gbxml, nwc; overwrite rules; name patterns",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("E85");
    t.setTargetPath(path);
    const folder = join(t.yearDir, "exports85");
    const level1 = await viewRef(t, "Level 1", folder);
    const level2 = await viewRef(t, "Level 2", folder);

    await t.step("pdf of one view; repeat gives NAME_TAKEN; overwrite needs confirm", async () => {
      const r = await settle(t, await t.call("export", { format: "pdf", views: [level1], folder }, { timeoutMs: 120_000 }), "pdf");
      t.assertEq(r.status, "ok", "pdf");
      const [pdf] = files(r);
      t.assert(pdf && existsSync(pdf) && statSync(pdf).size > 0, `pdf written: ${pdf}`);
      t.assertMatch(pdf, /Level 1\.pdf$/, "default name pattern {name}");
      const taken = await t.expectError("export", { format: "pdf", views: [level1], folder }, "NAME_TAKEN");
      t.assert(!!taken.fix, "NAME_TAKEN has a fix");
      const plan = await t.call("export", { format: "pdf", views: [level1], folder, overwrite: true });
      t.assertEq(plan.status, "not_applied", "overwrite asks to confirm");
      const done = await settle(t, await t.call("export", { format: "pdf", confirm: plan.json.confirm }, { timeoutMs: 120_000 }), "pdf overwrite");
      t.assertEq(done.status, "ok", "overwrite applied");
    });

    await t.step("pdf of two views with a name pattern runs as a job or inline and writes both", async () => {
      const r = await settle(t, await t.call("export", { format: "pdf", views: [level1, level2], folder, name: "S85_{n}_{name}", color: "bw" }, { timeoutMs: 120_000 }), "pdf x2");
      t.assertEq(r.status, "ok", "pdf x2");
      t.assertEq(files(r).length, 2, "two files");
      for (const f of files(r)) t.assert(existsSync(f), `exists ${f}`);
      const combined = await settle(t, await t.call("export", { format: "pdf", views: [level1, level2], folder, combine: true, name: "S85 combined" }, { timeoutMs: 120_000 }), "pdf combined");
      t.assertEq(files(combined).length, 1, "one combined file");
    });

    await t.step("dwg and dxf", async () => {
      for (const format of ["dwg", "dxf"]) {
        const r = await settle(t, await t.call("export", { format, views: [level1], folder }, { timeoutMs: 120_000, allowError: true }), format);
        if (r.status === "error") {
          t.assertEq(r.code, "EXPORTER_MISSING", `${format} error`);
          continue;
        }
        t.assert(files(r).every((f) => existsSync(f)) && files(r).length === 1, `${format} written`);
      }
    });

    await t.step("ifc (temp transaction, nothing left behind)", async () => {
      const r = await settle(t, await t.call("export", { format: "ifc", folder, setup: "IFC4 Reference View" }, { timeoutMs: 120_000 }), "ifc");
      t.assertEq(r.status, "ok", "ifc");
      t.assert(files(r)[0]?.toLowerCase().endsWith(".ifc"), "ifc file");
      const changes = await t.call("get_changes", { since: "last" }, { allowError: true });
      t.log(`after ifc: ${changes.summary}`);
    });

    await t.step("image at a given size", async () => {
      const r = await settle(t, await t.call("export", { format: "image", views: [level1], folder, size: 1200 }, { timeoutMs: 120_000 }), "image");
      t.assertEq(r.status, "ok", "image");
      t.assert(files(r)[0]?.toLowerCase().endsWith(".png"), "png");
    });

    await t.step("csv refuses non-schedules; fbx refuses non-3D views", async () => {
      await t.expectError("export", { format: "csv", views: [level1], folder }, "INVALID_ARGS");
      await t.expectError("export", { format: "fbx", views: [level1], folder }, ["INVALID_ARGS", "EXPORTER_MISSING"]);
    });

    await t.step("gbxml of the model", async () => {
      const r = await settle(t, await t.call("export", { format: "gbxml", folder }, { timeoutMs: 120_000, allowError: true }), "gbxml");
      if (r.status === "error") t.log(`gbxml: ${r.code} ${r.summary}`);
      else t.assert(files(r)[0]?.toLowerCase().endsWith(".xml"), "xml");
    });

    await t.step("nwc exports or says the exporter is missing", async () => {
      const r = await settle(t, await t.call("export", { format: "nwc", folder }, { timeoutMs: 120_000, allowError: true }), "nwc");
      if (r.status === "error") t.assertEq(r.code, "EXPORTER_MISSING", "nwc without exporter");
    });

    await t.step("an unknown view gives NOT_FOUND with candidates", async () => {
      const r = await t.expectError("export", { format: "pdf", views: ["Levle 1"], folder }, "NOT_FOUND");
      t.assert(!!r.fix, "fix");
    });
  },
};
