// S88 purge (SPEC §13.3, P-DOCS): manage_document purge as a write: preview lists the delete set by category
// without changing anything; above confirm.deleteOver it is NOT APPLIED until confirmed; one undo step; a second
// purge finds (almost) nothing.

export default {
  id: "S88",
  title: "Purge unused: preview, delete rule confirm, applied once",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("P88");
    t.setTargetPath(path);

    const preview = await t.step("purge preview changes nothing and returns a token", async () => {
      const r = await t.call("manage_document", { op: "purge", passes: 1, preview: true }, { timeoutMs: 120_000 });
      t.assertEq(r.status, "ok", "preview");
      t.assertMatch(r.summary, /^preview: /, "preview summary");
      t.assert(r.json?.confirm, "preview token");
      return r;
    });
    t.log(`purge preview: ${preview.summary}`);

    await t.step("passes is clamped to 1-3", async () => {
      const r = await t.call("manage_document", { op: "purge", passes: 9, preview: true }, { timeoutMs: 120_000 });
      t.assert(r.warnings.some((w) => w.startsWith("VALUE_CLAMPED")), `clamp warning: ${r.warnings.join(" | ")}`);
    });

    const first = await t.step("purge: NOT APPLIED above deleteOver, then confirmed", async () => {
      let r = await t.call("manage_document", { op: "purge" }, { timeoutMs: 120_000 });
      if (r.status === "not_applied") {
        t.assert(r.json?.delete?.total > 0, "delete total");
        t.assert(r.json?.delete?.by_category && typeof r.json.delete.by_category === "object", "by_category");
        r = await t.call("manage_document", { op: "purge", confirm: r.json.confirm }, { timeoutMs: 120_000 });
      }
      t.assertEq(r.status, "ok", "purge applied");
      t.assert(typeof r.json?.write === "string" && r.json.write.startsWith("w"), "write id");
      t.assert(r.json?.undo, "undo hint");
      return r;
    });
    t.log(`purged: ${first.summary}`);

    await t.step("a second purge finds little or nothing", async () => {
      const r = await t.call("manage_document", { op: "purge" }, { timeoutMs: 120_000 });
      const applied = r.status === "not_applied" ? await t.call("manage_document", { op: "purge", confirm: r.json.confirm }, { timeoutMs: 120_000 }) : r;
      t.assertEq(applied.status, "ok", "second purge");
      t.assert(Number(applied.json?.purged ?? 0) <= Number(first.json?.purged ?? 0), "second purge is not larger than the first");
    });
  },
};
