// Catalog entry for `worksharing` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, LIFE, meta, op, P, S, WD_OPEN, WRITE_TAIL_NOUNITS } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(LIFE);

export const tool: ToolSpec = {
  name: "worksharing",
  title: "Worksharing",
  description: "Workshared models: sync with central (comment, relinquish), reload latest, relinquish all, create/rename worksets, set active workset, move elements to a workset, borrow elements, enable worksharing.",
  group: "write",
  annotations: WD_OPEN,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    comment: S("sync: comment."),
    relinquish: B("sync: relinquish everything after. Default true."),
    compact: B("sync: compact central."),
    workset: S("Workset name."),
    name: S("New workset name (enable: name for the default workset)."),
    ids: P.ids,
    from: P.from,
    filter: P.filter,
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
  ops: {
    sync: op("", "comment,relinquish,compact", meta(M, { blast: ["always"], job: "always" }), [{"op":"sync","comment":"Doors renumbered"}], { help: "Always asks to confirm. Runs as a job; relinquish defaults to true." }),
    reload_latest: op("", "", meta(M, { job: "always" }), [{"op":"reload_latest"}]),
    relinquish: op("", "", M, [{"op":"relinquish"}]),
    workset_create: op("name", "", meta(M, { kind: "write", idle: true, tx: "in", job: "never", cs: true }), [{"op":"workset_create","name":"Facade"}]),
    workset_rename: op("workset,name", "", meta(M, { kind: "write", idle: true, tx: "in", job: "never", cs: true }), [{"op":"workset_rename","workset":"Facade","name":"Facade North"}]),
    set_active: op("workset", "", meta(M, { kind: "write", idle: true, tx: "in", job: "never", cs: true }), [{"op":"set_active","workset":"Facade North"}]),
    move_to: op("workset,ids/from/filter", "", meta(M, { kind: "write", idle: true, tx: "in", blast: ["bulk"], job: "never", cs: true }), [{"op":"move_to","workset":"Facade North","from":"r3"}]),
    borrow: op("ids/from/filter", "", meta(M, { tx: "none" }), [{"op":"borrow","ids":["304601"]}]),
    enable: op("", "name", meta(M, { blast: ["always"] }), [{"op":"enable","name":"Shared Levels and Grids"}]),
  },
};
