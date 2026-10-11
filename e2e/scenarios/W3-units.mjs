// W3 units, offline part (SPEC §5.3 step 3, §5.4, §13.3 W3, D4 §8.9; owner W3-E2E). No Revit.
//   1. Every numeric parameter of the catalog carries a unit tag (mm, deg, pct, m2, m3, s, px) or is a known unitless
//      count/index; help {tool, op} reports the unit, so a weak model never has to guess feet vs millimetres.
//   2. The broker's lenient unit parsing turns what weak models send into the call's units: "3.5 m" → 3500,
//      "12'6\"" → 3810, "45°" → 45, "0.5 rad" → 28.65 deg, "2%" → 2; with units:"m", "350 cm" → 3.5 and plain numbers stay.
//   3. Through MCP: string units in a write never fail validation (no Revit: NO_REVIT_RUNNING), and a nonsense unit is
//      a precise INVALID_ARGS naming the field.
// W3R repeats the round trip against Revit (units:"m"/"ft" writes read back in mm).

import { join } from "node:path";
import { CATALOG, fullProperties } from "@revit-mcp-next/contracts/catalog";
import { makeCaller } from "../lib/call.mjs";
import { brokerValidation } from "../lib/hints.mjs";

/** Numeric params that are counts, indexes, factors or directions, not measurements. */
const UNITLESS = new Set(["limit", "count", "risers", "scale", "accuracy", "passes", "axis", "expect_count", "steps", "line_weight"]);

function numericLeaves(tool) {
  const out = [];
  const walk = (path, p, unit) => {
    if (!p || typeof p !== "object") return;
    const u = p.unit ?? unit;
    const numeric = p.type === "number" || p.type === "integer" || (p.type === "array" && (p.items?.type === "number" || p.items?.type === "array"));
    if (numeric) out.push({ path, unit: u, name: path.split(/[.[\]]+/).filter(Boolean).pop() });
    if (p.properties) for (const [k, v] of Object.entries(p.properties)) walk(`${path}.${k}`, v, u);
    if (p.items?.properties) for (const [k, v] of Object.entries(p.items.properties)) walk(`${path}[].${k}`, v, u);
  };
  for (const [k, p] of Object.entries(fullProperties(tool))) walk(`${tool.name}.${k}`, p, undefined);
  return out;
}

const near = (a, b) => typeof a === "number" && Math.abs(a - b) < 1e-6 * Math.max(1, Math.abs(b));

export default {
  id: "W3",
  title: "Units (offline): unit tags on numeric params, lenient unit parsing, INVALID_ARGS on nonsense units",
  profiles: ["surface", "quick", "full"],
  years: "none",
  async run(t) {
    await t.step("every numeric param has a unit tag or is a known count", () => {
      const missing = [];
      let tagged = 0;
      for (const tool of CATALOG) {
        for (const leaf of numericLeaves(tool)) {
          if (leaf.unit) tagged += 1;
          else if (!UNITLESS.has(leaf.name)) missing.push(leaf.path);
        }
      }
      t.assert(missing.length === 0, `numeric params without a unit: ${missing.join(", ")}`);
      t.log(`${tagged} numeric params carry a unit tag`);
    });

    await t.step("the broker parses weak-model units into the call's units", async () => {
      const v = await brokerValidation();
      t.assert(v, "broker/dist is built (npm run build)");
      const cases = [
        ["3.5 m", "mm", "mm", 3500],
        ["12'6\"", "mm", "mm", 3810],
        ["900mm", "mm", "mm", 900],
        ["2,5 m", "mm", "mm", 2500],
        ["350 cm", "mm", "m", 3.5],
        ["10 ft", "mm", "ft", 10],
        ["45°", "deg", "mm", 45],
        ["0.5 rad", "deg", "mm", 28.647890],
        ["2%", "pct", "mm", 2],
        ["12 m²", "m2", "mm", 12],
        ["30 s", "s", "mm", 30],
      ];
      for (const [text, unit, callUnits, expected] of cases) {
        const got = v.parseWithUnit(text, unit, callUnits);
        t.assert(typeof got === "number" && Math.abs(got - expected) < 1e-3, `${JSON.stringify(text)} (${unit}, units ${callUnits}) → ${got}, expected ${expected}`);
      }
      const wall = CATALOG.find((x) => x.name === "create_elements");
      const norm = v.normalizeArgs(wall, { op: "wall", level: "Level 1", points: [["0", "0"], ["5 m", "0"], ["5 m", "4000 mm"]], height: "3 m", units: "mm" });
      t.assert(!norm.error, `normalize: ${norm.error?.message}`);
      t.assert(near(norm.args.points[1][0], 5000) && near(norm.args.points[2][1], 4000) && near(norm.args.height, 3000), `points/height in mm: ${JSON.stringify(norm.args)}`);
      const metres = v.normalizeArgs(wall, { op: "wall", level: "Level 1", points: [[0, 0], [5, 0]], height: "300 cm", units: "m" });
      t.assert(!metres.error && near(metres.args.points[1][0], 5) && near(metres.args.height, 3) && metres.args.units === "m", `units m keeps plain numbers and converts strings into m: ${JSON.stringify(metres.args)}`);
      const rotate = v.normalizeArgs(CATALOG.find((x) => x.name === "modify_elements"), { op: "rotate", ids: [1], angle: "1.5708 rad" });
      t.assert(!rotate.error && Math.abs(rotate.args.angle - 90) < 0.01, `angle in degrees: ${rotate.args.angle}`);
    });

    const session = await t.connect({ home: join(t.runRoot, "w3") });
    const call = makeCaller({ session });
    try {
      await t.step("through MCP: string units never fail validation; a nonsense unit is a precise INVALID_ARGS", async () => {
        const ok = await call("create_elements", { op: "wall", level: "Level 1", points: [["0 m", "0 m"], ["5 m", "0 m"]], height: "3 m" }, { allowError: true });
        t.assertEq(ok.code, "NO_REVIT_RUNNING", "unit strings reach the broker's Revit check");
        const feet = await call("modify_elements", { op: "move", ids: [1], by: ["10'", "0"], units: "ft" }, { allowError: true });
        t.assertEq(feet.code, "NO_REVIT_RUNNING", "feet strings with units ft validate");
        const bad = await call("create_elements", { op: "wall", level: "Level 1", points: [["0", "0"], ["5 parsecs", "0"]] }, { expectError: "INVALID_ARGS" });
        t.assertMatch(bad.summary, /points/, "names the field");
        const badUnits = await call("create_elements", { op: "wall", level: "Level 1", points: [[0, 0], [5, 0]], units: "furlong" }, { expectError: "INVALID_ARGS" });
        t.assertMatch(badUnits.summary, /units/, "names units");
        const help = await call("help", { tool: "modify_elements", op: "rotate" });
        t.assertEq(help.json?.params?.angle?.unit, "deg", "help gives the angle unit");
      });
    } finally {
      await session.close();
    }
  },
};
