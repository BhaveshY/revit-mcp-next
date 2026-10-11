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
    sync: op("", "comment,relinquish,compact", meta(M, { blast: ["always"], job: "always" }), [{"op":"sync","comment":"Doors renumbered"},{"op":"sync","comment":"WIP","relinquish":false}], { help: "Always asks to confirm. Runs as a job; relinquish defaults to true. Revit clears its undo history on sync.", errors: ["UNSUPPORTED_OP","REVIT_REFUSED"] }),
    reload_latest: op("", "", meta(M, { job: "always" }), [{"op":"reload_latest"}], { help: "Loads other users' synchronized changes into the local file (job). Revit clears its undo history.", errors: ["UNSUPPORTED_OP","REVIT_REFUSED"] }),
    relinquish: op("", "", M, [{"op":"relinquish"}], { help: "Gives back every borrowed element and workset; elements changed since the last sync stay borrowed until sync.", errors: ["UNSUPPORTED_OP","REVIT_REFUSED"] }),
    workset_create: op("name", "", meta(M, { kind: "write", idle: true, tx: "in", job: "never", cs: true }), [{"op":"workset_create","name":"Facade"}], { help: "Creates a user workset; output workset = its id.", errors: ["NAME_TAKEN","UNSUPPORTED_OP"] }),
    workset_rename: op("workset,name", "", meta(M, { kind: "write", idle: true, tx: "in", job: "never", cs: true }), [{"op":"workset_rename","workset":"Facade","name":"Facade North"}], { errors: ["NAME_TAKEN","NOT_FOUND"] }),
    set_active: op("workset", "", meta(M, { kind: "write", idle: true, tx: "in", job: "never", cs: true }), [{"op":"set_active","workset":"Facade North"}], { help: "New elements go to the active workset. The workset must be open.", errors: ["NOT_FOUND","UNSUPPORTED_OP"] }),
    move_to: op("workset,ids/from/filter", "", meta(M, { kind: "write", idle: true, tx: "in", blast: ["bulk"], job: "never", cs: true }), [{"op":"move_to","workset":"Facade North","from":"r3"},{"op":"move_to","workset":"Facade North","filter":{"category":"Curtain Panels","level":"Level 2"}}], { help: "Hosted elements follow their host; types, views and system elements keep their workset (reported as refused). Elements borrowed by others give NOT_EDITABLE.", errors: ["NOT_EDITABLE","NOT_FOUND","UNSUPPORTED_OP"] }),
    borrow: op("ids/from/filter", "", meta(M, { tx: "none" }), [{"op":"borrow","ids":["304601"]},{"op":"borrow","from":"selection"}], { help: "Borrows elements now so later edits cannot be blocked; elements owned by others are listed as refused.", errors: ["UNSUPPORTED_OP","REVIT_REFUSED"] }),
    enable: op("", "name", meta(M, { blast: ["always"] }), [{"op":"enable","name":"Workset1"}], { help: "Creates the worksets 'Shared Levels and Grids' and name (default Workset1) and saves the file as the central model. Always asks to confirm.", errors: ["UNSUPPORTED_OP","INVALID_ARGS"] }),
  },
};
