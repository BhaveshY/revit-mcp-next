// S01 bootstrap (SPEC §13.3; joint wave-1 gate with W1-ADDIN). Needs Revit with the add-in loaded from the e2e home.
// launch (runner) → hello/status via the control snapshot → manage_document new_project A (activate true) re-pins with
// notice TARGET_NOW → set_target grammar (#n, exact title, 'active') → help {tool, op} JSON line.

export default {
  id: "S01",
  title: "Bootstrap: status via snapshot, new_project re-pin, set_target grammar, help",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const pid = t.revit.pid;

    const full = await t.step("status detail:full answers from the control snapshot", async () => {
      const r = await t.call("status", { detail: "full", instance: String(pid) });
      t.assertEq(r.status, "ok", "status");
      const row = r.json.revit.find((x) => x[1] === pid);
      t.assert(row, `instance pid ${pid} is listed`);
      t.assertEq(row[0], t.year, "instance year");
      const inst = r.json.instances?.find((i) => i.pid === pid);
      t.assert(inst, "detail:full lists the instance");
      t.assertEq(String(inst.home).toLowerCase(), t.home.toLowerCase(), "the add-in uses the e2e home");
      t.assertEq(inst.compat, "ok", "protocol compatible");
      return r;
    });
    t.log(`Revit ${t.year} build ${full.json.instances.find((i) => i.pid === pid)?.build ?? "?"}`);

    const compact = await t.step("compact status stays small and names no paths", async () => {
      const r = await t.call("status", {});
      t.assertLe(r.bytes, 2_048, "compact status bytes");
      t.assert(!/[A-Za-z]:\\\\/.test(r.jsonText ?? ""), "no paths in compact status");
      return r;
    });
    void compact;

    const project = await t.step("new_project A (activate true) re-pins with TARGET_NOW", async () => {
      const { r, path } = await t.newProject("A");
      t.assertEq(r.status, "ok", "new_project status");
      t.assert(r.notices.some((n) => /^TARGET_NOW #\d+ A \(Revit \d{4}\)/.test(n)), `TARGET_NOW notice: ${r.notices.join(" | ")}`);
      t.assertEq(r.doc?.title.replace(/\.rvt$/i, ""), "A", "result names doc A");
      t.setTargetPath(path);
      return { path };
    });

    const listing = await t.step("status shows A as the pinned target", async () => {
      const r = await t.call("status", {});
      const rows = r.json.docs.rows;
      const a = rows.find((row) => row[1] === "A" && row[3] === t.year);
      t.assert(a, "doc A is listed");
      t.assertMatch(a[4], /target/, "A carries the target flag");
      t.assertEq(r.json.target?.doc, `#${a[0]}`, "target is A");
      return { n: a[0] };
    });

    await t.step("set_target grammar: #n, exact title, 'active'", async () => {
      const byNumber = await t.call("set_target", { doc: `#${listing.n}` });
      t.assert(byNumber.notices.some((n) => n.startsWith("TARGET_NOW")), "#n gives TARGET_NOW");
      const byTitle = await t.call("set_target", { doc: "A" });
      t.assertEq(byTitle.doc?.title.replace(/\.rvt$/i, ""), "A", "exact title");
      const active = await t.call("set_target", { doc: "active" });
      t.assertEq(active.status, "ok", "active");
      const bad = await t.call("set_target", { doc: "#999" }, { expectError: ["DOC_NOT_OPEN", "TARGET_AMBIGUOUS"] });
      t.assert(!!bad.fix, "unknown doc gives a fix");
    });

    await t.step("help {tool, op} JSON line", async () => {
      const r = await t.call("help", { tool: "create_elements", op: "wall" });
      t.assert(Array.isArray(r.json?.required) && r.json.required.includes("level"), "required params");
      t.assertMatch(r.next, /^create_elements \{/, "working example");
    });

    t.log(`doc A at ${project.path}`);
  },
};
