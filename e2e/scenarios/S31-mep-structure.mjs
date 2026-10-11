// S31 MEP and structure (SPEC §13.3, D4 S30 mep/structure part; owner P-MODEL) on a disposable project.
// mep: runs with elbows at bends or warn FITTING_FAILED (never a failed call), sloped pipe, round/rectangular ducts,
// conduit, cable tray, flex runs, connect, insulation, spaces/zones (Revit 2027: system-zones), systems and circuits
// (positive when the template has terminals/fixtures, error paths always). structure: columns at grid intersections
// (re-run skips existing ones), beams between columns, brace, beam system, isolated/wall/slab foundations.

const near = (a, b, tol = 1) => Math.abs(Number(a) - Number(b)) <= tol;

function tableOf(json) {
  if (!json || typeof json !== "object") return null;
  if (Array.isArray(json.cols) && Array.isArray(json.rows)) return json;
  for (const value of Object.values(json)) if (value && typeof value === "object" && Array.isArray(value.cols) && Array.isArray(value.rows)) return value;
  return null;
}

function rowsOf(json) {
  const table = tableOf(json);
  if (!table) return [];
  return table.rows.map((row) => Object.fromEntries(table.cols.map((c, i) => [c, row[i]])));
}

async function firstType(t, filter, pick = () => true) {
  const r = await t.call("list", { kind: "types", limit: 200, ...filter }, { allowError: true });
  if (r.status !== "ok") return null;
  const row = rowsOf(r.json).find(pick);
  return row ? { id: String(row.id), row } : null;
}

/** A bend got a fitting, or the call says why not (FITTING_FAILED) — both are acceptable outcomes (SPEC §13.3 S31). */
function assertBends(t, r, bends, what) {
  const failed = r.json.failed_fittings ?? 0;
  t.assertEq(r.json.fittings.length + failed, bends, `${what}: ${bends} bend(s) -> ${r.json.fittings.length} fitting(s) + ${failed} FITTING_FAILED`);
  if (failed > 0) t.assert(r.warnings.some((w) => w.startsWith("FITTING_FAILED")), `${what}: a missing fitting is reported as warn FITTING_FAILED`);
}

export default {
  id: "S31",
  title: "MEP and structure",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S31");
    t.setTargetPath(path);

    // ================================================================ MEP runs
    const pipes = await t.step("pipe L-run: 2 segments, elbow or FITTING_FAILED", async () => {
      const r = await t.call("mep", { op: "pipe", level: "Level 1", points: [[0, 0], [5000, 0], [5000, 3000]], size: 25, offset: 2800 });
      t.assertEq(r.json.segments.length, 2, "2 segments");
      assertBends(t, r, 1, "pipe");
      t.assert(near(r.json.length, 8000), `length 8000 (got ${r.json.length})`);
      return r.json.segments.map(String);
    });

    await t.step("sloped pipe; slope with explicit z is ignored with a warning", async () => {
      const r = await t.call("mep", { op: "pipe", level: "Level 1", points: [[0, 6000], [8000, 6000]], size: 100, offset: 500, slope: 2 });
      t.assertEq(r.json.segments.length, 1, "1 segment");
      const ignored = await t.call("mep", { op: "pipe", level: "Level 1", points: [[0, 7000, 500], [4000, 7000, 500]], slope: 1 });
      t.assert(ignored.warnings.some((w) => w.startsWith("PARAM_IGNORED")), "slope ignored with z");
      await t.expectError("mep", { op: "pipe", level: "Level 1", points: [[0, 0]] }, "INVALID_ARGS");
      await t.expectError("mep", { op: "pipe", level: "Level 1", points: [[0, 0], [1000, 0]], system: "No Such System" }, "NOT_FOUND");
    });

    await t.step("ducts: rectangular U-run and round run", async () => {
      const rect = await t.call("mep", { op: "duct", level: "Level 1", points: [[0, 10000], [6000, 10000], [6000, 14000], [0, 14000]], width: 400, height: 250 });
      t.assertEq(rect.json.segments.length, 3, "3 segments");
      assertBends(t, rect, 2, "rectangular duct");
      const round = await t.call("mep", { op: "duct", level: "Level 1", points: [[10000, 10000], [16000, 10000], [16000, 14000]], size: 250 });
      assertBends(t, round, 1, "round duct");
      await t.expectError("mep", { op: "duct", level: "Level 1", points: [[0, 0], [1000, 0]], size: 200, width: 300, height: 200 }, "INVALID_ARGS");
    });

    await t.step("conduit and cable tray with a bend", async () => {
      const conduit = await t.call("mep", { op: "conduit", level: "Level 1", points: [[0, 20000], [4000, 20000], [4000, 24000]], size: 25, offset: 3000 });
      assertBends(t, conduit, 1, "conduit");
      const tray = await t.call("mep", { op: "cable_tray", level: "Level 1", points: [[10000, 20000], [18000, 20000], [18000, 24000]], width: 300, height: 100 });
      assertBends(t, tray, 1, "cable tray");
      const straight = await t.call("mep", { op: "cable_tray", level: "Level 1", points: [[0, 26000], [4000, 26000], [8000, 26000]], fittings: false });
      t.assertEq(straight.json.segments.length, 1, "collinear points make one segment");
    });

    await t.step("flex pipe and flex duct", async () => {
      const fp = await t.call("mep", { op: "flex_pipe", level: "Level 1", points: [[0, 30000], [500, 30300], [1000, 30300]], size: 20 });
      t.assertEq(fp.json.created.length, 1, "flex pipe");
      const fd = await t.call("mep", { op: "flex_duct", level: "Level 1", points: [[3000, 30000], [3600, 30400], [4200, 30400]], size: 200 });
      t.assertEq(fd.json.created.length, 1, "flex duct");
      await t.expectError("mep", { op: "flex_duct", level: "Level 1", points: [[0, 0]] }, "INVALID_ARGS");
    });

    await t.step("connect: aligned touching pipes join; perpendicular touching pipes get an elbow", async () => {
      const a = await t.call("mep", { op: "pipe", level: "Level 1", points: [[20000, 0], [23000, 0]], size: 50, fittings: false });
      const b = await t.call("mep", { op: "pipe", level: "Level 1", points: [[23000, 0], [26000, 0]], size: 50, fittings: false });
      const joined = await t.call("mep", { op: "connect", ids: [String(a.json.segments[0]), String(b.json.segments[0])] });
      t.assertEq(joined.json.joined.length, 1, "1 pair joined");
      const c = await t.call("mep", { op: "pipe", level: "Level 1", points: [[30000, 0], [33000, 0]], size: 50, fittings: false });
      const d = await t.call("mep", { op: "pipe", level: "Level 1", points: [[33000, 0], [33000, 3000]], size: 50, fittings: false });
      const elbow = await t.call("mep", { op: "connect", ids: [String(c.json.segments[0]), String(d.json.segments[0])] }, { allowError: true });
      if (elbow.status === "ok") t.assertEq(elbow.json.joined.length, 1, "elbow pair");
      else t.assertEq(elbow.code, "REVIT_REFUSED", "a refused elbow is REVIT_REFUSED");
      await t.expectError("mep", { op: "connect", ids: [pipes[0]] }, "INVALID_ARGS");
    });

    await t.step("insulate pipes; repeating updates the thickness", async () => {
      const type = await firstType(t, { class: "PipeInsulationType" });
      if (!type) {
        t.log("no pipe insulation type in the template; error path only");
        await t.expectError("mep", { op: "insulate", ids: pipes, type: "No Such Insulation", thickness: 25 }, "NOT_FOUND");
        return;
      }
      const r = await t.call("mep", { op: "insulate", ids: pipes, type: type.id, thickness: 25 });
      t.assertEq(r.json.created.length, 2, "2 insulations");
      const again = await t.call("mep", { op: "insulate", ids: pipes, type: type.id, thickness: 40 });
      t.assertMatch(again.summary, /updated 2 existing/, "thickness updated");
    });

    // ================================================================ spaces, zones, systems, circuits
    const spaces = await t.step("spaces in an enclosed box; zone; NAME_TAKEN for a zone name", async () => {
      await t.call("create_elements", { op: "wall", level: "Level 1", top: "Level 2", points: [[40000, 0], [48000, 0], [48000, 6000], [40000, 6000]], closed: true });
      const s = await t.call("mep", { op: "space", level: "Level 1", at: [44000, 3000], name: "E2E Plant", number: "S1" });
      const row = rowsOf(s.json)[0];
      t.assertEq(row.number, "S1", "space number");
      t.assert(Number(row.area_m2) > 40, `bounded space (${row.area_m2} m2)`);
      await t.expectError("mep", { op: "space", level: "Level 1", at: [44000, 3000] }, "INVALID_ARGS");
      const all = await t.call("mep", { op: "space", level: "Level 1", all: true });
      t.assertEq(all.status, "ok", "space all");
      const z = await t.call("mep", { op: "zone", name: "E2E Zone", ids: [String(row.id)] });
      t.assert(z.json.created.length >= 1, "zone created");
      await t.expectError("mep", { op: "zone", name: "E2E Zone", ids: [String(row.id)] }, "NAME_TAKEN");
      return [String(row.id)];
    });
    void spaces;

    await t.step("systems: duct system from air terminals (when loaded); error paths always", async () => {
      await t.expectError("mep", { op: "system_create", ids: pipes, kind: "piping" }, "INVALID_ARGS");
      await t.expectError("mep", { op: "system_add", system: "No Such System", ids: pipes }, "NOT_FOUND");
      await t.expectError("mep", { op: "system_remove", system: "No Such System", ids: pipes }, "NOT_FOUND");
      const terminal = await firstType(t, { category: ["Air Terminals"] });
      if (!terminal) return t.log("no air terminal type in the template; positive system ops skipped");
      const placed = await t.call("place_family", { op: "place", type: terminal.id, points: [[42000, 2000], [46000, 2000], [44000, 4500]], level: "Level 1", offset: 2700 }, { allowError: true });
      if (placed.status !== "ok") return t.log(`air terminals could not be placed (${placed.code}); positive system ops skipped`);
      const [t1, t2, t3] = placed.json.created.map(String);
      const sys = await t.call("mep", { op: "system_create", ids: [t1, t2], kind: "duct", name: "E2E-SA" });
      t.assertEq(sys.json.system, "E2E-SA", "system name");
      const added = await t.call("mep", { op: "system_add", system: "E2E-SA", ids: [t3] });
      t.assertEq(added.status, "ok", "system_add");
      const removed = await t.call("mep", { op: "system_remove", system: "E2E-SA", ids: [t3] });
      t.assertEq(removed.status, "ok", "system_remove");
    });

    await t.step("circuit from electrical fixtures (when loaded); error path always", async () => {
      await t.expectError("mep", { op: "circuit", ids: pipes }, "INVALID_ARGS");
      const fixture = (await firstType(t, { category: ["Electrical Fixtures"] })) ?? (await firstType(t, { category: ["Lighting Fixtures"] }));
      if (!fixture) return t.log("no electrical/lighting fixture type; positive circuit skipped");
      const placed = await t.call("place_family", { op: "place", type: fixture.id, points: [[41000, 1000], [47000, 1000]], level: "Level 1", offset: 2600 }, { allowError: true });
      if (placed.status !== "ok") return t.log(`fixtures could not be placed (${placed.code}); positive circuit skipped`);
      const r = await t.call("mep", { op: "circuit", ids: placed.json.created.map(String) }, { allowError: true });
      if (r.status === "ok") t.assert(r.json.created.length >= 1, "circuit created");
      else t.log(`circuit: ${r.code} ${r.summary}`);
      const ec = await t.call("mep", { op: "system_create", ids: placed.json.created.map(String), kind: "electrical", name: "E2E circuit" }, { allowError: true });
      t.log(`system_create electrical: ${ec.status} ${ec.code ?? ""}`);
    });

    // ================================================================ structure
    const grid = await t.step("grids for the frame", async () => {
      const x = await t.call("create_elements", { op: "grid", start: [0, -50000], end: [0, -30000], name: "S1", count: 3, spacing: 6000 });
      const y = await t.call("create_elements", { op: "grid", start: [-16000, -48000], end: [4000, -48000], name: "SA", count: 2, spacing: 8000 });
      t.assertEq(x.json.created.length + y.json.created.length, 5, "5 grids");
      return [...rowsOf(x.json).map((g) => g.name), ...rowsOf(y.json).map((g) => g.name)];
    });

    const columnType = await t.step("structural column type", async () => (await firstType(t, { category: ["Structural Columns"] }))?.id ?? null);
    const framingType = await t.step("structural framing type", async () => (await firstType(t, { category: ["Structural Framing"] }))?.id ?? null);

    const columns = await t.step("columns at the intersections of the frame grids; re-run skips them", async () => {
      if (!columnType) {
        t.log("no structural column type; error path only");
        await t.expectError("structure", { op: "column", type: "No Such Column", level: "Level 1", grids: grid }, "NOT_FOUND");
        return null;
      }
      const r = await t.call("structure", { op: "column", type: columnType, level: "Level 1", grids: grid });
      t.assertEq(r.json.created.length, 6, "3 x 2 intersections");
      t.assertEq(r.json.top, "Level 2", "top defaults to the next level");
      await t.expectError("structure", { op: "column", type: columnType, level: "Level 1", grids: grid }, "INVALID_ARGS");
      const pts = await t.call("structure", { op: "column", type: columnType, level: "Level 1", points: [[30000, -40000], [36000, -40000]], rotation: 45 });
      t.assertEq(pts.json.created.length, 2, "columns at points");
      return { ids: r.json.created.map(String), handle: r.json.handle };
    });

    await t.step("beams between columns, along a chain and from start/end; brace", async () => {
      if (!framingType) {
        t.log("no structural framing type; error path only");
        await t.expectError("structure", { op: "beam", type: "No Such Beam", level: "Level 2", start: [0, 0], end: [6000, 0] }, "NOT_FOUND");
        return;
      }
      if (columns) {
        const between = await t.call("structure", { op: "beam", type: framingType, level: "Level 2", between: columns.ids.slice(0, 2) });
        t.assertEq(between.json.created.length, 1, "1 beam between 2 columns");
      }
      const chain = await t.call("structure", { op: "beam", type: framingType, level: "Level 2", points: [[0, -60000], [6000, -60000], [6000, -54000]] });
      t.assertEq(chain.json.created.length, 2, "2 beams along the chain");
      const single = await t.call("structure", { op: "beam", type: framingType, level: "Level 2", start: [0, -62000], end: [6000, -62000], offset: -300 });
      t.assertEq(single.json.created.length, 1, "1 beam");
      const brace = await t.call("structure", { op: "brace", type: framingType, level: "Level 1", start: [0, -60000, 0], end: [6000, -60000, 3500] });
      t.assertEq(brace.json.created.length, 1, "brace");
      await t.expectError("structure", { op: "brace", type: framingType, level: "Level 1", start: [0, 0, 0], end: [0, 0, 0] }, "INVALID_ARGS");
    });

    await t.step("beam system with spacing and direction", async () => {
      if (!framingType) return;
      const r = await t.call("structure", { op: "beam_system", type: framingType, level: "Level 2", points: [[20000, -60000], [26000, -60000], [26000, -52000], [20000, -52000]], spacing: 1500, direction: 90 });
      t.assert(r.json.beams >= 3, `beams every 1500 mm across 6000 mm (${r.json.beams})`);
    });

    await t.step("foundations: isolated under the columns, wall foundation, slab", async () => {
      await t.expectError("structure", { op: "foundation", kind: "isolated", type: "No Such Footing", points: [[0, 0]], level: "Level 1" }, "NOT_FOUND");
      const footing = await firstType(t, { category: ["Structural Foundations"], class: "FamilySymbol" });
      if (footing && columns) {
        const iso = await t.call("structure", { op: "foundation", kind: "isolated", type: footing.id, from: columns.handle });
        t.assertEq(iso.json.created.length, 6, "a footing under each column");
      } else t.log("no isolated footing type or no columns; isolated skipped");

      const wallType = await firstType(t, { class: "WallFoundationType" });
      if (wallType) {
        const w = await t.call("create_elements", { op: "wall", level: "Level 1", start: [0, -70000], end: [10000, -70000], height: 3000 });
        const wf = await t.call("structure", { op: "foundation", kind: "wall", type: wallType.id, ids: w.json.created.map(String) });
        t.assertEq(wf.json.created.length, 1, "wall foundation");
        await t.expectError("structure", { op: "foundation", kind: "wall", type: wallType.id, ids: w.json.created.map(String) }, "INVALID_ARGS");
      } else t.log("no wall foundation type; wall foundation skipped");

      const slabType = await firstType(t, { class: "FloorType", category: ["Structural Foundations"] });
      if (slabType) {
        const s = await t.call("structure", { op: "foundation", kind: "slab", type: slabType.id, level: "Level 1", points: [[0, -80000], [10000, -80000], [10000, -72000], [0, -72000]] });
        t.assertEq(s.json.created.length, 1, "foundation slab");
      } else t.log("no foundation slab type; slab skipped");
    });
  },
};
