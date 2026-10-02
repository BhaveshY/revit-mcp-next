// Catalog entry for `edit_sheets` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, meta, mm, OA, op, P, PT, S, SA, syn, WD, WRITE, WRITE_TAIL } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE);

export const tool: ToolSpec = {
  name: "edit_sheets",
  title: "Edit sheets",
  description: "Sheets: create (one or many), duplicate, renumber/rename, set titleblock; place, move or remove views, schedules and legends; viewport type; revisions create/edit and add/remove on sheets. Sheet mm from origin.",
  group: "write",
  annotations: WD,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    sheet: S("Sheet number, name or id."),
    sheets: SA("Several sheets (numbers/ids)."),
    number: S("Sheet number (duplicate/rename: the new number)."),
    name: P.name,
    items: OA("create many: [{number,name}]."),
    titleblock: S("Titleblock type. Default: first loaded."),
    mode: syn(E(["empty","with_views","with_detailing"], "duplicate: default with_views."), {"empty_sheet":"empty","views":"with_views","detailing":"with_detailing"}),
    prefix: S("duplicate: prefix for copied view names."),
    view: S("View, schedule or legend name/id (placed or to place)."),
    at: mm(PT("Viewport center [x,y] mm on the sheet. Default: sheet center.")),
    by: mm(PT("move_viewport: shift [dx,dy] mm.")),
    type: S("Viewport type name."),
    revision: S("Revision description, number or id."),
    description: S("Revision description."),
    date: S("Revision date text."),
    issued_by: S("Issued by."),
    issued_to: S("Issued to."),
    issued: B("Mark revision issued."),
    ...WRITE_TAIL,
  },
  required: ["op"],
  ops: {
    create: op("number/items", "name,titleblock", meta(M, { blast: ["create"] }), [{"op":"create","items":[{"number":"A101","name":"Ground floor"},{"number":"A102","name":"First floor"}]}, {"op":"create","number":"A201","name":"Sections"}]),
    duplicate: op("sheet,number", "name,mode,prefix", meta(M, { strict: true }), [{"op":"duplicate","sheet":"A101","number":"A103","mode":"with_views"}]),
    rename: op("sheet", "number,name", M, [{"op":"rename","sheet":"A101","name":"Ground floor plan"}]),
    set_titleblock: op("sheet/sheets,titleblock", "", M, [{"op":"set_titleblock","sheets":["A101","A102"],"titleblock":"A1 metric"}]),
    place: op("sheet,view", "at,type", M, [{"op":"place","sheet":"A101","view":"Level 1"}]),
    move_viewport: op("sheet,view,at/by", "", M, [{"op":"move_viewport","sheet":"A101","view":"Level 1","by":[50,0]}]),
    remove_viewport: op("sheet,view", "", meta(M, { inproc: false }), [{"op":"remove_viewport","sheet":"A101","view":"Level 1"}]),
    set_viewport_type: op("sheet,view,type", "", M, [{"op":"set_viewport_type","sheet":"A101","view":"Level 1","type":"Title w Line"}]),
    revision_create: op("description", "date,issued_by,issued_to", M, [{"op":"revision_create","description":"Issue 2","date":"2026-10-02","issued_by":"EBA"}]),
    revision_edit: op("revision", "description,date,issued_by,issued_to,issued", M, [{"op":"revision_edit","revision":"Issue 2","issued":true}]),
    revision_add: op("revision,sheet/sheets", "", M, [{"op":"revision_add","revision":"Issue 2","sheets":["A101","A102"]}]),
    revision_remove: op("revision,sheet/sheets", "", M, [{"op":"revision_remove","revision":"Issue 2","sheet":"A102"}]),
  },
};
