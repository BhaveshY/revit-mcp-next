// Catalog entry for `ui` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, meta, mm, op, P, PT, S, SA, syn, UI, W_IDEM } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(UI);

export const tool: ToolSpec = {
  name: "ui",
  title: "Control Revit UI",
  description: "Drive the Revit window without changing the model: select, zoom_to, temporary isolate/hide, reset, activate_view, activate_doc, list/close open views, list dialogs and press a dialog button.",
  group: "ui",
  annotations: W_IDEM,
  discriminator: "op",
  profiles: ["core", "full"],
  properties: {
    ids: P.ids,
    from: P.from,
    filter: P.filter,
    category: P.category,
    view: P.view,
    views: SA("close_views: names/ids, or ['all_but_active']."),
    region: mm(PT("[x0,y0,x1,y1] mm.")),
    fit: B("zoom_to: zoom to fit the whole view."),
    mode: syn(E(["replace","add","remove"], "select: default replace."), {"set":"replace","append":"add","plus":"add","subtract":"remove","minus":"remove"}),
    zoom: B("select: also zoom to the selection."),
    dialog: S("Dialog id from op=dialogs. Default: the open one."),
    button: S("Button text or id. Cancel/Close apply at once; other buttons need the user's OK (confirm)."),
    instance: S("dialogs/press: Revit year or process id. Default: every Revit."),
    confirm: P.confirm,
    doc: P.doc,
  },
  required: ["op"],
  ops: {
    select: op("ids/from/filter", "mode,zoom", M, [{"op":"select","from":"r3","zoom":true}], { help: "Needs the target doc to be UI-active (NEEDS_ACTIVE_DOC otherwise); it never switches documents implicitly." }),
    zoom_to: op("ids/from/region/fit", "view", M, [{"op":"zoom_to","ids":["304512"]}]),
    isolate: op("ids/from/category", "view", meta(M, { ui: false, tx: "in" }), [{"op":"isolate","category":["Walls"]}]),
    hide: op("ids/from/category", "view", meta(M, { ui: false, tx: "in" }), [{"op":"hide","ids":["304512"]}]),
    reset: op("", "view", meta(M, { ui: false, tx: "in" }), [{"op":"reset"}]),
    activate_view: op("view", "", meta(M, { ui: false, idle: false }), [{"op":"activate_view","view":"Level 2"}]),
    activate_doc: op("doc", "", meta(M, { scope: "none", ui: false, idle: false }), [{"op":"activate_doc","doc":"#2"}], { help: "Brings the doc to the front and re-pins it for this session (notice TARGET_NOW)." }),
    open_views: op("", "", M, [{"op":"open_views"}]),
    close_views: op("views", "", M, [{"op":"close_views","views":["all_but_active"]}]),
    dialogs: op("", "instance", meta(M, { kind: "control", impl: "control", scope: "none", ui: false }), [{"op":"dialogs"}]),
    press: op("button", "dialog,instance,confirm", meta(M, { kind: "control", impl: "control", scope: "none", ui: false, blast: ["button"] }), [{"op":"press","button":"Cancel"}], { help: "Cancel and Close apply at once; any other button returns NOT APPLIED and needs confirm. Buttons that could discard work are never pressed." }),
  },
};
