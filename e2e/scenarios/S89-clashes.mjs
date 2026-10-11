// S89 clashes (SPEC §13.3; owner P-READ). Needs Revit with the add-in loaded from the e2e home.
// Floors are used because they never auto-join each other (crossing walls may clean up their joins). Exact pair: two
// overlapping floors clash once (each unordered pair once), a distant floor does not. Tolerance: the overlap is as deep
// as the floor is thick; tolerance thickness+50 drops it, thickness-50 keeps it. Handles: a = r# of one floor. Link: a
// second disposable project with a floor over the host's third floor is linked; set b inside the link reports link ids
// and the overlap centre comes back in host coordinates.

function rowsOf(r) {
  const j = r.json ?? {};
  if (!Array.isArray(j.rows) || !Array.isArray(j.cols)) return [];
  return j.rows.map((row) => Object.fromEntries(j.cols.map((c, i) => [c, row[i]])));
}

const createdIds = (r) => (Array.isArray(r.json?.created) ? r.json.created.map(Number) : []);
const pairKey = (a, b) => [Number(a), Number(b)].sort((x, y) => x - y).join(":");
const rect = (x0, y0, x1, y1) => [[x0, y0], [x1, y0], [x1, y1], [x0, y1]];
/** Codes that mean "this build does not serve that op yet" (another lane's op); the dependent step is skipped. */
const NOT_SERVED = new Set(["ADDIN_OUTDATED", "UNSUPPORTED_OP"]);

async function firstLevel(t) {
  const rows = rowsOf(await t.call("list", { kind: "levels" }));
  t.assert(rows.length >= 1, "the template has a level");
  return rows[0].name;
}

async function floor(t, level, points) {
  const id = createdIds(await t.call("create_elements", { op: "floor", level, points }))[0];
  t.assert(id, "floor created");
  return id;
}

export default {
  id: "S89",
  title: "Clashes: exact pairs, tolerance, handles, linked model",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const host = await t.newProject("S89");
    t.setTargetPath(host.path);
    const L1 = await firstLevel(t);

    const floors = await t.step("model: floor A, floor B overlapping it (3 x 2 m), floor C far away", async () => {
      const a = await floor(t, L1, rect(0, 0, 6000, 4000));
      const b = await floor(t, L1, rect(3000, 2000, 9000, 6000));
      const c = await floor(t, L1, rect(30000, 0, 36000, 4000));
      return { a, b, c };
    });

    const depth = await t.step("a = b = Floors: exactly the overlapping pair, once", async () => {
      const r = await t.call("check_model", { check: "clashes", a: ["Floors"] });
      const rows = rowsOf(r);
      const pairs = rows.map((x) => pairKey(x.a, x.b));
      t.assertEq(rows.length, 1, `one clash, got ${JSON.stringify(pairs)}`);
      t.assertEq(pairs[0], pairKey(floors.a, floors.b), "the pair is A/B");
      t.assert(!pairs.some((p) => p.split(":").includes(String(floors.c))), "C clashes with nothing");
      const row = rows[0];
      t.assert(row.depth >= 50 && row.depth <= 1000, `penetration = floor thickness, got ${row.depth} mm`);
      t.assert(Math.abs(row.volume - 3 * 2 * (row.depth / 1000)) < 0.05, `overlap volume 3 x 2 m x thickness, got ${row.volume} m3`);
      t.assert(Array.isArray(row.point) && Math.abs(row.point[0] - 4500) < 100 && Math.abs(row.point[1] - 3000) < 100, `overlap centre near (4500, 3000): ${row.point}`);
      t.assertEq(row.a_category, "Floors", "English category");
      return row.depth;
    });

    await t.step("tolerance: thickness+50 drops the overlap, thickness-50 keeps it", async () => {
      const dropped = await t.call("check_model", { check: "clashes", a: ["Floors"], tolerance: depth + 50 });
      t.assertEq(rowsOf(dropped).length, 0, `tolerance ${depth + 50}`);
      const kept = await t.call("check_model", { check: "clashes", a: ["Floors"], tolerance: `${depth - 50}mm` });
      t.assertEq(rowsOf(kept).length, 1, `tolerance ${depth - 50}mm`);
      t.assertEq(kept.json.tolerance_mm, depth - 50, "tolerance echoed in mm");
    });

    await t.step("a = handle of floor A, b = Floors; no partners gives no rows", async () => {
      const found = await t.call("find_elements", { ids: [String(floors.a)] });
      const handle = found.json.handle;
      t.assertMatch(handle ?? "", /^r\d+$/, "handle");
      const r = await t.call("check_model", { check: "clashes", a: [handle], b: ["Floors"] });
      const rows = rowsOf(r);
      t.assertEq(rows.length, 1, "one clash for floor A");
      t.assertEq(Number(rows[0].a), floors.a, "a is floor A");
      t.assertEq(Number(rows[0].b), floors.b, "b is floor B");
      const none = await t.call("check_model", { check: "clashes", a: ["Floors"], b: ["Doors"] });
      t.assertEq(rowsOf(none).length, 0, "no doors, no clashes");
    });

    await t.step("link: set b inside a linked model (link ids, host coordinates)", async () => {
      // A second project with one floor over floor C of the host, saved and closed, then linked into the host.
      const linked = await t.newProject("S89L");
      t.setTargetPath(linked.path);
      const LL = await firstLevel(t);
      const linkFloor = await floor(t, LL, rect(33000, 2000, 39000, 6000));
      const saved = await t.call("manage_document", { op: "save" }, { allowError: true });
      if (saved.status === "error" && NOT_SERVED.has(saved.code)) return t.log(`link part skipped: manage_document save is ${saved.code} in this build`);
      t.assertEq(saved.status, "ok", "link model saved");
      await t.call("set_target", { doc: "S89" });
      t.setTargetPath(host.path);
      const closed = await t.call("manage_document", { op: "close", doc: "S89L", save: true }, { allowError: true, timeoutMs: 120_000 });
      if (closed.status === "error" && NOT_SERVED.has(closed.code)) return t.log(`link part skipped: manage_document close is ${closed.code} in this build`);
      t.assert(closed.status === "ok" || closed.status === "running", `link model closed: ${closed.summary}`);
      if (closed.status === "running") {
        const job = /j\d+/.exec(closed.next ?? closed.summary ?? "")?.[0];
        if (job) await t.call("job_status", { id: job }, { timeoutMs: 120_000 });
      }
      await t.call("set_target", { doc: "S89" });
      const link = await t.call("links", { op: "link_rvt", path: linked.path }, { allowError: true, timeoutMs: 120_000 });
      if (link.status === "error" && NOT_SERVED.has(link.code)) return t.log(`link part skipped: links link_rvt is ${link.code} in this build`);
      t.assertEq(link.status, "ok", `linked: ${link.summary}`);
      const links = rowsOf(await t.call("list", { kind: "links" }));
      t.assertEq(links.length, 1, "one link listed");
      t.assertEq(links[0].loaded, true, "link loaded");

      const r = await t.call("check_model", { check: "clashes", a: ["Floors"], b: ["Floors"], link: "S89L" });
      const rows = rowsOf(r);
      t.assertEq(rows.length, 1, `one host/link clash, got ${rows.length}`);
      t.assertEq(Number(rows[0].a), floors.c, "host floor C");
      t.assertEq(Number(rows[0].b), linkFloor, "b is the link floor id (link ids)");
      t.assert(r.json.b_ids_are_in_link === true, "result says b ids are link ids");
      t.assert(Math.abs(rows[0].point[0] - 34500) < 100 && Math.abs(rows[0].point[1] - 3000) < 100, `overlap centre in host coordinates: ${rows[0].point}`);
      const inLink = await t.call("find_elements", { category: ["Floors"], link: "S89L" });
      t.assertEq(inLink.json.total, 1, "find_elements link= sees the link floor");
      t.assertEq(Number(rowsOf(inLink)[0].id), linkFloor, "link id");
    });
  },
};
