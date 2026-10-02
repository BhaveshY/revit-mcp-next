// Catalog entry for `place_family` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, meta, mm, op, P, PTS, S, SA, W, WRITE, WRITE_TAIL } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { blast: ["create"] });

export const tool: ToolSpec = {
  name: "place_family",
  title: "Place family instances",
  description: "Place instances of any loadable family (doors, windows, furniture, fixtures, equipment, generic, detail-free) at points, on hosts or along lines; load .rfa families. Hosts are found automatically when omitted.",
  group: "write",
  annotations: W,
  discriminator: "op",
  profiles: ["core", "full"],
  properties: {
    type: S("Family type: 'Family: Type', type name, or id."),
    at: P.at,
    points: mm(PTS("Several insertion points [[x,y(,z)],...]: one instance each.")),
    start: P.start,
    end: P.end,
    level: S("Level for z; z of points is height above it."),
    host: S("Host id (wall, floor, ceiling, roof, face). 'auto' (default) finds the nearest valid host."),
    offset: P.offset,
    rotation: P.rotation,
    flip_hand: B("Flip hand."),
    flip_facing: B("Flip facing."),
    allow_pinned: B("Allow a pinned host."),
    path: S("load: .rfa path visible to Revit."),
    symbols: SA("load: only these types. Default all."),
    overwrite: B("load: overwrite existing parameter values. Default false."),
    categories: SA("load: refuse unless the family is one of these categories."),
    sha256: S("load: refuse unless the file hash matches."),
    allow_network: B("load: allow UNC/network paths."),
    ...WRITE_TAIL,
  },
  required: ["op"],
  ops: {
    place: op("type,at/points/start+end", "level,host,offset,rotation,flip_hand,flip_facing,allow_pinned", M, [{"op":"place","type":"Single-Flush: 0915 x 2134mm","at":[6000,0],"level":"Level 1"}, {"op":"place","type":"Desk: 1525 x 762mm","points":[[2000,2000],[4000,2000],[6000,2000]],"level":"Level 1","rotation":90}], { help: "Hosts are found automatically when host is omitted. points places one instance per point; z of a point is height above level." }),
    load: op("path", "symbols,overwrite,categories,sha256,allow_network", meta(M, { blast: [] }), [{"op":"load","path":"C:\\Families\\Door-Double.rfa"}]),
  },
};
