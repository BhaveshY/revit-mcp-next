// Catalog entry for `structure` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { deg, E, meta, mm, N, op, P, PT, PTS, S, SA, syn, W, WRITE, WRITE_TAIL } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { blast: ["create"] });

export const tool: ToolSpec = {
  name: "structure",
  title: "Structural elements",
  description: "Structure: columns at points or grid intersections, beams along lines or between columns, braces, beam systems, isolated/wall/slab foundations. type='Family: Type'; lengths mm.",
  group: "write",
  annotations: W,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    type: P.type,
    level: S("Base or reference level."),
    top: S("column: top level. Default next level up."),
    at: P.at,
    points: mm(PTS("Several points (columns) or a chain (beams, beam_system outline).")),
    grids: SA("column: at intersections of these grids; ['*'] = all."),
    between: SA("beam: column ids to connect in order."),
    start: mm(PT("Beam/brace or slanted column start [x,y,z].")),
    end: mm(PT("Beam/brace or slanted column end [x,y,z].")),
    offset: mm(N("Offset from level mm (beam z, column base).")),
    top_offset: mm(N("column: top offset mm.")),
    rotation: P.rotation,
    spacing: mm(N("beam_system: spacing mm.")),
    direction: deg(N("beam_system: beam direction degrees. Default 0 (x axis).")),
    kind: syn(E(["isolated","wall","slab"], "foundation kind."), {"footing":"isolated","isolatedfooting":"isolated","pad":"isolated","strip":"wall","wallfoundation":"wall","foundationslab":"slab","mat":"slab"}),
    ids: SA("foundation: columns (isolated) or walls (wall)."),
    from: P.from,
    ...WRITE_TAIL,
  },
  required: ["op"],
  ops: {
    column: op("type,level,at/points/grids", "top,offset,top_offset,rotation,start,end", M, [{"op":"column","type":"Concrete-Rectangular-Column: 300 x 450mm","level":"Level 1","grids":["*"]}]),
    beam: op("type,level,start+end/points/between", "offset", M, [{"op":"beam","type":"W-Wide Flange: W310X38.7","level":"Level 2","start":[0,0],"end":[6000,0]}]),
    brace: op("type,level,start,end", "", M, [{"op":"brace","type":"HSS-Hollow Structural Section: HSS152X152X9.5","level":"Level 1","start":[0,0,0],"end":[6000,0,3500]}]),
    beam_system: op("type,level,points", "spacing,direction", M, [{"op":"beam_system","type":"W-Wide Flange: W310X38.7","level":"Level 2","points":[[0,0],[6000,0],[6000,8000],[0,8000]],"spacing":1500}]),
    foundation: op("kind,type", "ids/from,points,level", M, [{"op":"foundation","kind":"isolated","type":"Footing-Rectangular: 1800 x 1200 x 450mm","from":"r14"}]),
  },
};
