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
    pipe: op("points,level", "type,system,size,offset,slope,fittings", M, [{"op":"pipe","level":"Level 1","points":[[0,0],[5000,0],[5000,3000]],"system":"Domestic Cold Water","size":25,"offset":2800}, {"op":"pipe","level":"Level 1","points":[[0,0],[8000,0]],"system":"Sanitary","size":100,"offset":-300,"slope":2}], { help: "One segment per pair of points, elbows at bends (fittings, default true); a refused fitting gives warn FITTING_FAILED and the segments stay. Points without z sit at level + offset (default 2700); slope (percent) falls along the run.", errors: ["NOT_FOUND", "INVALID_ARGS"] }),
    duct: op("points,level", "type,system,size/width+height,offset,fittings", M, [{"op":"duct","level":"Level 1","points":[[0,1000],[10000,1000]],"system":"Supply Air","width":400,"height":250}, {"op":"duct","level":"Level 1","points":[[0,2000],[6000,2000],[6000,6000]],"system":"Return Air","size":250}], { help: "size makes a round duct, width + height a rectangular one; without type the first duct type of that shape is used. Elbows at bends as for pipes.", errors: ["NOT_FOUND", "INVALID_ARGS"] }),
    conduit: op("points,level", "type,size,offset,fittings", M, [{"op":"conduit","level":"Level 1","points":[[0,2000],[8000,2000]],"size":25}, {"op":"conduit","level":"Level 1","points":[[0,0],[4000,0],[4000,4000]],"size":20,"offset":3000}], { help: "One conduit per segment with elbows at bends; size is the diameter." }),
    cable_tray: op("points,level", "type,width,height,offset,fittings", M, [{"op":"cable_tray","level":"Level 1","points":[[0,3000],[8000,3000]],"width":300,"height":100}], { help: "One tray per segment with elbows at bends." }),
    flex_pipe: op("points,level", "type,system,size,offset", M, [{"op":"flex_pipe","level":"Level 1","points":[[0,0],[500,300],[1000,300]],"size":20}], { help: "One flexible pipe through all points (at least 2)." }),
    flex_duct: op("points,level", "type,system,size,offset", M, [{"op":"flex_duct","level":"Level 1","points":[[0,1000],[600,1400],[1200,1400]],"size":200}], { help: "One flexible round duct through all points (at least 2)." }),
    connect: op("ids", "", M, [{"op":"connect","ids":["309001","309002"]}, {"op":"connect","ids":["309010","309011","309012"]}], { help: "Connects consecutive ids at their closest free connectors: touching connectors are joined, two runs apart get an elbow/transition; equipment must touch the run end.", errors: ["INVALID_ARGS", "REVIT_REFUSED"] }),
    system_create: op("ids,kind", "name,type", M, [{"op":"system_create","ids":["309100","309101"],"kind":"duct","name":"SA-01"}, {"op":"system_create","ids":["309150","309151"],"kind":"piping","type":"Domestic Hot Water"}], { help: "ids are equipment, fixtures or terminals; their free connectors of the kind join the new system (runs join through their connections). type is the system type (default supply air / domestic cold water). kind electrical makes a circuit.", errors: ["INVALID_ARGS", "NOT_FOUND"] }),
    system_add: op("system,ids", "", M, [{"op":"system_add","system":"SA-01","ids":["309102"]}], { help: "system is a system name or id; for circuits the elements are added to the circuit." }),
    system_remove: op("system,ids", "", M, [{"op":"system_remove","system":"SA-01","ids":["309102"]}], { help: "Removes the elements' connectors from the system (or the elements from the circuit)." }),
    circuit: op("ids", "panel", M, [{"op":"circuit","ids":["309200","309201"],"panel":"LP-1"}], { help: "A power circuit of elements with electrical connectors; panel (name or id) is selected when given.", errors: ["INVALID_ARGS", "NOT_FOUND"] }),
    insulate: op("ids/from,type,thickness", "", M, [{"op":"insulate","from":"r12","type":"Fibreglass","thickness":25}], { help: "Pipes, ducts and their fittings/accessories; existing insulation gets the new thickness. type is a pipe or duct insulation type name." }),
    space: op("level,at/all", "name,number", M, [{"op":"space","level":"Level 1","all":true}, {"op":"space","level":"Level 1","at":[3000,2000],"name":"Office","number":"S101"}], { help: "at must lie in an enclosed area without a space; all fills every enclosed area on the level (needs a floor plan of the level)." }),
    zone: op("name,ids", "level", M, [{"op":"zone","name":"Zone 1","ids":["309300","309301"]}], { help: "ids are spaces. On Revit 2027 the zone is a space-based system-zone and all spaces must be on one level.", errors: ["NAME_TAKEN", "INVALID_ARGS"] }),
  },
};
