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
    create: op("number/items", "name,titleblock", meta(M, { blast: ["create"] }), [{"op":"create","items":[{"number":"A101","name":"Ground floor"},{"number":"A102","name":"First floor"}]}, {"op":"create","number":"A201","name":"Sections","titleblock":"A1 metric"}], { help: "One sheet (number) or several (items [{number,name}]). titleblock defaults to the document default or the first loaded one; with none loaded the sheet has no titleblock. A used number gives NAME_TAKEN with the next free number.", api: "ViewSheet.Create", errors: ["NAME_TAKEN", "NOT_FOUND", "AMBIGUOUS_NAME"] }),
    duplicate: op("sheet,number", "name,mode,prefix", meta(M, { strict: true }), [{"op":"duplicate","sheet":"A101","number":"A103","mode":"with_views"}, {"op":"duplicate","sheet":"A101","number":"A104","mode":"empty","name":"Ground floor (blank)"}], { help: "mode empty copies the sheet and titleblock; with_views duplicates the placed views too (prefix renames the copies); with_detailing also copies their detailing. Viewports keep position and type; sheet parameters are copied.", api: "ViewSheet.Duplicate(SheetDuplicateOption)", errors: ["NAME_TAKEN", "UNSUPPORTED_OP", "NOT_FOUND"] }),
    rename: op("sheet", "number,name", M, [{"op":"rename","sheet":"A101","name":"Ground floor plan"}, {"op":"rename","sheet":"A102","number":"A110"}], { help: "Changes the number and/or name; a used number gives NAME_TAKEN with the next free number.", errors: ["NAME_TAKEN", "NOT_FOUND"] }),
    set_titleblock: op("sheet/sheets,titleblock", "", M, [{"op":"set_titleblock","sheets":["A101","A102"],"titleblock":"A1 metric"}], { help: "Changes the titleblock type on each sheet, or places it at the sheet origin when the sheet has none.", errors: ["NOT_FOUND", "AMBIGUOUS_NAME"] }),
    place: op("sheet,view", "at,type", M, [{"op":"place","sheet":"A101","view":"Level 1"}, {"op":"place","sheet":"A101","view":"Door Schedule","at":[20,580]}], { help: "Views and legends become viewports centred at 'at' (default: titleblock centre); schedules are placed with their top-left at 'at'. A view already on another sheet gives UNSUPPORTED_OP (duplicate it with edit_views).", api: "Viewport.Create / ScheduleSheetInstance.Create", errors: ["UNSUPPORTED_OP", "NOT_FOUND", "INVALID_ARGS"] }),
    move_viewport: op("sheet,view,at/by", "", M, [{"op":"move_viewport","sheet":"A101","view":"Level 1","by":[50,0]}, {"op":"move_viewport","sheet":"A101","view":"Level 1","at":[420,297]}], { help: "at = new centre [x,y] mm on the sheet, by = shift [dx,dy] mm. view is the placed view, schedule or legend (or the viewport id).", errors: ["NOT_FOUND"] }),
    remove_viewport: op("sheet,view", "", meta(M, { inproc: false }), [{"op":"remove_viewport","sheet":"A101","view":"Level 1"}], { help: "Removes the viewport or schedule instance from the sheet; the view itself is kept.", errors: ["NOT_FOUND"] }),
    set_viewport_type: op("sheet,view,type", "", M, [{"op":"set_viewport_type","sheet":"A101","view":"Level 1","type":"Title w Line"}], { help: "type is a viewport type name valid for this viewport (list kind viewport_types). Schedules have no viewport type.", errors: ["NOT_FOUND", "UNSUPPORTED_OP"] }),
    revision_create: op("description", "date,issued_by,issued_to", M, [{"op":"revision_create","description":"Issue 2","date":"2026-10-02","issued_by":"EBA"}], { help: "Creates the next revision in sequence; refer to it later by description, number, 'Seq. n' or id.", api: "Revision.Create" }),
    revision_edit: op("revision", "description,date,issued_by,issued_to,issued", M, [{"op":"revision_edit","revision":"Issue 2","issued":true}, {"op":"revision_edit","revision":"Issue 2","issued":false,"date":"2026-10-09"}], { help: "An issued revision is locked: pass issued:false to edit it (issued:true locks it again after the edits).", errors: ["INVALID_ARGS", "NOT_FOUND", "AMBIGUOUS_NAME"] }),
    revision_add: op("revision,sheet/sheets", "", M, [{"op":"revision_add","revision":"Issue 2","sheets":["A101","A102"]}], { help: "Adds the revision to the sheets' revision lists (additional revisions).", errors: ["NOT_FOUND"] }),
    revision_remove: op("revision,sheet/sheets", "", M, [{"op":"revision_remove","revision":"Issue 2","sheet":"A102"}], { help: "Removes the revision from the sheets' additional revisions; it stays shown (warn PARTIAL_RESULT) where revision clouds on the sheet or its views use it.", errors: ["NOT_FOUND"] }),
  },
};
