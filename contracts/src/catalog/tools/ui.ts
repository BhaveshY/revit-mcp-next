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
    select: op("ids/from/filter", "mode,zoom", M, [{"op":"select","from":"r3","zoom":true}, {"op":"select","filter":{"category":"Doors","level":"Level 1"}}, {"op":"select","ids":["304512"],"mode":"add"}], { help: "Needs the target doc to be UI-active (NEEDS_ACTIVE_DOC otherwise); it never switches documents implicitly.", errors: ["NEEDS_ACTIVE_DOC", "NOT_FOUND"] }),
    zoom_to: op("ids/from/region/fit", "view", M, [{"op":"zoom_to","ids":["304512"]}, {"op":"zoom_to","view":"Level 1","region":[0,0,12000,8000]}, {"op":"zoom_to","fit":true}], { help: "region is model x,y mm on plans (along-view/elevation mm on sections). view= first opens that view of the active doc.", errors: ["NEEDS_ACTIVE_DOC", "NOT_FOUND"] }),
    isolate: op("ids/from/category", "view", meta(M, { ui: false, tx: "in" }), [{"op":"isolate","category":["Walls"]}, {"op":"isolate","from":"r3","view":"Level 1"}], { help: "Temporary hide/isolate of the view (default: the active view); ui reset ends it. Undo is not needed." }),
    hide: op("ids/from/category", "view", meta(M, { ui: false, tx: "in" }), [{"op":"hide","ids":["304512"]}, {"op":"hide","category":["Furniture"]}], { help: "Temporary hide; permanent hiding is view_graphics hide." }),
    reset: op("", "view", meta(M, { ui: false, tx: "in" }), [{"op":"reset"}, {"op":"reset","view":"Level 1"}]),
    activate_view: op("view", "", meta(M, { ui: false, idle: false }), [{"op":"activate_view","view":"Level 2"}, {"op":"activate_view","view":"A101"}], { help: "Opens the view in Revit; when its doc is in the background the doc comes to the front first and is re-pinned.", errors: ["NOT_FOUND", "REVIT_REFUSED"] }),
    activate_doc: op("doc", "", meta(M, { scope: "none", ui: false, idle: false }), [{"op":"activate_doc","doc":"#2"}], { help: "Brings the doc to the front and re-pins it for this session (notice TARGET_NOW).", errors: ["DOC_NOT_OPEN", "REVIT_REFUSED"] }),
    open_views: op("", "", M, [{"op":"open_views"}], { errors: ["NEEDS_ACTIVE_DOC"] }),
    close_views: op("views", "", M, [{"op":"close_views","views":["all_but_active"]}, {"op":"close_views","views":["Level 2","{3D}"]}], { help: "Never closes the last open view of a doc (LAST_VIEW): Revit would close the document.", errors: ["NEEDS_ACTIVE_DOC", "LAST_VIEW"] }),
    dialogs: op("", "instance", meta(M, { kind: "control", impl: "control", scope: "none", ui: false }), [{"op":"dialogs"}]),
    press: op("button", "dialog,instance,confirm", meta(M, { kind: "control", impl: "control", scope: "none", ui: false, blast: ["button"] }), [{"op":"press","button":"Cancel"}], { help: "Cancel and Close apply at once; any other button returns NOT APPLIED and needs confirm. Buttons that could discard work are never pressed." }),
  },
};
