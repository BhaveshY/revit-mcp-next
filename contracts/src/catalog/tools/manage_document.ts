// Catalog entry for `manage_document` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, deg, E, I, LIFE, meta, mm, N, op, PT, S, syn, UNITS_SYN, WD_OPEN, WRITE_TAIL_NOUNITS } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(LIFE);

export const tool: ToolSpec = {
  name: "manage_document",
  title: "Manage documents",
  description: "Files: open (rvt/rfa; detach, audit, worksets), activate, save, save_as, close, new project from template, new family from .rft, purge unused, project units, base/survey point. Risky ones ask to confirm.",
  group: "write",
  annotations: WD_OPEN,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    path: S("File path (.rvt, .rfa)."),
    template: S("Template .rte/.rft path or name. new_project default: settings template."),
    activate: B("open/new_project: show it in the UI. Default true."),
    local: S("open (central model): path of the new local copy. Default Documents\\<name>_<user>.rvt."),
    central: B("open: open the central file itself instead of a local copy (asks to confirm)."),
    detach: syn(E(["no","preserve","discard"], "open: detach from central. Default no."), {"false":"no","none":"no","true":"preserve","yes":"preserve","keep":"preserve"}),
    audit: B("open: audit."),
    worksets: syn(E(["all","none","last","editable"], "open: worksets to open. Default last."), {"lastviewed":"last","*":"all","everything":"all"}),
    overwrite: B("Replace an existing file."),
    as_central: B("save_as: save as central model."),
    compact: B("Compact the file."),
    save: B("close: save first. Default false (unsaved changes ask to confirm)."),
    passes: I("purge: repeat passes 1-3. Default 3."),
    unit: syn(E(["mm","cm","m","in","ft"], "set_units: project length unit."), UNITS_SYN),
    accuracy: N("set_units: rounding, e.g. 1 or 0.1."),
    base_point: mm(PT("coordinates: project base point [x,y,z] mm.")),
    survey_point: mm(PT("coordinates: survey point [x,y,z] mm.")),
    true_north: deg(N("coordinates: true north angle degrees.")),
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
  ops: {
    open: op("path", "activate,detach,audit,worksets,local,central", meta(M, { scope: "none", blast: ["central_open"] }), [{"op":"open","path":"C:\\Projects\\Site.rvt","activate":false},{"op":"open","path":"\\\\srv\\proj\\Tower.rvt","worksets":"editable"},{"op":"open","path":"C:\\Projects\\Tower.rvt","detach":"preserve","audit":true}], { help: "Opening a central creates a new local copy (Documents\\<name>_<user>.rvt; an existing local of the same central is reused) unless central:true, which needs confirm. An already open file is shown instead of opened twice. Re-pins (TARGET_NOW).", errors: ["NOT_FOUND","UNSUPPORTED_VERSION","INVALID_ARGS","REVIT_REFUSED"] }),
    activate: op("doc", "", meta(M, { kind: "ui", scope: "none", tx: "none", job: "never" }), [{"op":"activate","doc":"#2"}], { help: "Shows the open doc in Revit and re-pins (TARGET_NOW). A never-saved doc cannot be switched to through the API.", errors: ["UNSUPPORTED_OP","TARGET_AMBIGUOUS"] }),
    save: op("", "compact", meta(M, { scope: "project_or_family" }), [{"op":"save"},{"op":"save","compact":true}], { help: "Saves the doc to its file. Workshared: saves the local file only; worksharing sync sends the changes to central.", errors: ["SAVE_AS_REQUIRED","DOC_READ_ONLY","REVIT_REFUSED"] }),
    save_as: op("path", "overwrite,as_central,compact", meta(M, { scope: "project_or_family", blast: ["file_overwrite"] }), [{"op":"save_as","path":"C:\\Temp\\Tower_A_copy.rvt"},{"op":"save_as","path":"\\\\srv\\proj\\Tower_central.rvt","as_central":true}], { help: "Families save as .rfa, projects as .rvt/.rte. A workshared doc saves under a new name only as a new central (as_central:true). Replacing an existing file needs confirm.", errors: ["INVALID_ARGS","REVIT_REFUSED"] }),
    close: op("doc", "save", meta(M, { scope: "project_or_family", blast: ["unsaved_close"] }), [{"op":"close","doc":"#2","save":true},{"op":"close","doc":"#3"}], { help: "Never closes the UI-active doc directly: another open saved doc is activated first and the close runs in the next step (job). The only open doc gives CANNOT_CLOSE_ACTIVE. Unsaved changes with save:false need confirm. Clears the pin when it was the target.", errors: ["CANNOT_CLOSE_ACTIVE","SAVE_AS_REQUIRED","UNSUPPORTED_OP"] }),
    new_project: op("path", "template,overwrite,activate", meta(M, { scope: "none", blast: ["file_overwrite"] }), [{"op":"new_project","path":"C:\\Temp\\e2e.rvt","template":"C:\\ProgramData\\Autodesk\\RVT 2024\\Templates\\English\\Default-Multi-Discipline_Metric.rte"},{"op":"new_project","path":"C:\\Projects\\Study.rvt","template":"Default-Multi-Discipline_Metric"}], { help: "Creates and saves a project from the template (path or name; default from settings or the English metric template); activate defaults to true and re-pins. An existing file needs confirm.", errors: ["NOT_FOUND","INVALID_ARGS","REVIT_REFUSED"] }),
    new_family: op("template,path", "", meta(M, { scope: "none", blast: ["file_overwrite"] }), [{"op":"new_family","template":"Metric Generic Model.rft","path":"C:\\Temp\\EBA Generic.rfa"},{"op":"new_family","template":"Metric Furniture","path":"C:\\Families\\EBA Desk.rfa"}], { help: "template: .rft path or a name under the family template folder. The family is saved to path, shown in Revit and pinned; edit it with edit_family.", errors: ["NOT_FOUND","AMBIGUOUS_NAME","INVALID_ARGS"] }),
    purge: op("", "passes", meta(M, { kind: "write", idle: true, tx: "in", blast: ["delete"], cs: true }), [{"op":"purge"},{"op":"purge","passes":1,"preview":true}], { help: "Deletes unused families, types, materials and patterns in up to 3 passes (one undo step). Above settings confirm.deleteOver it asks to confirm with counts by category; preview:true shows them first.", errors: ["REVIT_REFUSED"] }),
    set_units: op("unit", "accuracy", meta(M, { kind: "write", idle: true, tx: "in", job: "never", cs: true }), [{"op":"set_units","unit":"mm","accuracy":1},{"op":"set_units","unit":"m","accuracy":0.01}], { help: "Project length display unit and rounding. Tool inputs stay in mm regardless.", errors: ["INVALID_ARGS"] }),
    coordinates: op("base_point/survey_point/true_north", "", meta(M, { kind: "write", idle: true, tx: "in", blast: ["always"], job: "never", cs: true }), [{"op":"coordinates","true_north":12.5},{"op":"coordinates","base_point":[1200,-800,34500]}], { help: "base_point and survey_point are the shared values [E/W, N/S, Elev] in mm shown in Revit's properties; true_north is the angle to true north in degrees. Always asks to confirm.", errors: ["UNSUPPORTED_OP","NOT_EDITABLE"] }),
  },
};
