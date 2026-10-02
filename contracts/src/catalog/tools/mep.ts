// Catalog entry for `mep` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, meta, mm, N, op, P, pct, PTS, S, syn, W, WRITE, WRITE_TAIL } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { blast: ["create"] });

export const tool: ToolSpec = {
  name: "mep",
  title: "MEP systems",
  description: "MEP: pipes, ducts, conduits, cable trays and flex runs along points with automatic elbows; connect elements; create/edit piping, duct and electrical systems, circuits; insulation; spaces and zones.",
  group: "write",
  annotations: W,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    points: mm(PTS("Run path [[x,y],...] or [[x,y,z],...] (z above level).")),
    level: P.level,
    offset: mm(N("Run centerline height above level mm. Default 2700.")),
    type: S("Pipe/duct/conduit/tray/insulation type name."),
    system: S("System type (e.g. Domestic Cold Water, Supply Air) or system name."),
    size: mm(N("Diameter mm.")),
    width: mm(N("Duct/tray width mm.")),
    height: mm(N("Duct/tray height mm.")),
    slope: pct(N("Pipes: slope in percent. Default 0.")),
    fittings: B("Add elbows/tees at bends. Default true."),
    ids: P.ids,
    from: P.from,
    kind: syn(E(["piping","duct","electrical"], "system_create kind."), {"pipe":"piping","pipes":"piping","plumbing":"piping","hvac":"duct","mechanical":"duct","air":"duct","power":"electrical"}),
    name: P.name,
    number: S("Space number."),
    at: P.at,
    all: B("space: in every enclosed area of the level."),
    thickness: mm(N("insulate: mm.")),
    panel: S("circuit: panel name or id."),
    ...WRITE_TAIL,
  },
  required: ["op"],
  ops: {
    pipe: op("points,level", "type,system,size,offset,slope,fittings", M, [{"op":"pipe","level":"Level 1","points":[[0,0],[5000,0],[5000,3000]],"system":"Domestic Cold Water","size":25,"offset":2800}]),
    duct: op("points,level", "type,system,size/width+height,offset,fittings", M, [{"op":"duct","level":"Level 1","points":[[0,1000],[10000,1000]],"system":"Supply Air","width":400,"height":250}]),
    conduit: op("points,level", "type,size,offset,fittings", M, [{"op":"conduit","level":"Level 1","points":[[0,2000],[8000,2000]],"size":25}]),
    cable_tray: op("points,level", "type,width,height,offset,fittings", M, [{"op":"cable_tray","level":"Level 1","points":[[0,3000],[8000,3000]],"width":300,"height":100}]),
    flex_pipe: op("points,level", "type,system,size,offset", M, [{"op":"flex_pipe","level":"Level 1","points":[[0,0],[500,300],[1000,300]],"size":20}]),
    flex_duct: op("points,level", "type,system,size,offset", M, [{"op":"flex_duct","level":"Level 1","points":[[0,1000],[600,1400],[1200,1400]],"size":200}]),
    connect: op("ids", "", M, [{"op":"connect","ids":["309001","309002"]}]),
    system_create: op("ids,kind", "name,type", M, [{"op":"system_create","ids":["309100","309101"],"kind":"duct","name":"SA-01"}]),
    system_add: op("system,ids", "", M, [{"op":"system_add","system":"SA-01","ids":["309102"]}]),
    system_remove: op("system,ids", "", M, [{"op":"system_remove","system":"SA-01","ids":["309102"]}]),
    circuit: op("ids", "panel", M, [{"op":"circuit","ids":["309200","309201"],"panel":"LP-1"}]),
    insulate: op("ids/from,type,thickness", "", M, [{"op":"insulate","from":"r12","type":"Fibreglass","thickness":25}]),
    space: op("level,at/all", "name,number", M, [{"op":"space","level":"Level 1","all":true}]),
    zone: op("name,ids", "level", M, [{"op":"zone","name":"Zone 1","ids":["309300","309301"]}]),
  },
};
