// Catalog entry for `edit_family` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, meta, O, OA, op, S, SA, syn, WD, WRITE, WRITE_TAIL } from "../params.js";
import type { ErrorSpec } from "../../errors.js";
import type { ToolSpec } from "../types.js";

/** Family edit problems (SPEC §4.3, D3 §3.7), merged into the ErrorCatalog. */
const FAMILY_ERRORS: Record<string, ErrorSpec> = {
  NEEDS_TYPES: {
    code: "NEEDS_TYPES",
    meaning: "The family has several types and the call did not say which.",
    nothingChanged: true,
    details: "options[] (type names)",
    fix: { kind: "same_call_with", set: { types: ["*"] }, note: "changes every type; or list the types to change" },
  },
  NOT_EDITABLE_FAMILY: {
    code: "NOT_EDITABLE_FAMILY",
    meaning: "System, in-place and protected families cannot be edited.",
    nothingChanged: true,
    details: "family, reason",
    fix: { kind: "call", tool: "list", args: { kind: "families" }, note: "pick a loadable family" },
  },
  FORMULA_INVALID: {
    code: "FORMULA_INVALID",
    meaning: "Revit rejected the formula.",
    nothingChanged: true,
    details: "apiMessage",
    fix: { kind: "same_call_with", set: { formula: "<corrected formula>" } },
  },
  PARAM_IN_USE: {
    code: "PARAM_IN_USE",
    meaning: "The parameter is used by a formula, label or another family element.",
    nothingChanged: true,
    details: "usedBy[]",
    fix: [
      { kind: "ask", when: { path: "details.usedBy" }, text: "remove its uses first (${details.usedBy}), or keep the parameter" },
      { kind: "ask", text: "remove its uses first, or keep the parameter" },
    ],
  },
  PARAM_HAS_FORMULA: {
    code: "PARAM_HAS_FORMULA",
    meaning: "The parameter's value comes from a formula.",
    nothingChanged: true,
    fix: {
      kind: "call",
      tool: "edit_family",
      args: { op: "set_formula", family: "${args.family}", doc: "${args.doc}", name: "${args.name}", formula: "" },
      note: "clears the formula first",
    },
  },
  SHARED_PARAM_FILE_MISSING: {
    code: "SHARED_PARAM_FILE_MISSING",
    meaning: "No shared parameter file is set or it cannot be read.",
    nothingChanged: true,
    fix: { kind: "ask", text: "set a shared parameter file in Revit (Manage > Shared Parameters), then retry" },
  },
  SHARED_PARAM_RENAME: {
    code: "SHARED_PARAM_RENAME",
    meaning: "Shared parameters cannot be renamed inside a family.",
    nothingChanged: true,
    fix: { kind: "ask", text: "shared parameters cannot be renamed; remove it and add the other shared parameter (remove_param, then add_param with shared=)" },
  },
};

const M = meta(WRITE, { scope: "project_or_family", idle: false, tx: "none", inproc: false });

export const tool: ToolSpec = {
  name: "edit_family",
  title: "Edit family parameters and types",
  description: "Edit a family's parameters and types: add/remove/rename params, formulas, values per type, add/rename/delete types. family= a loaded family (edited, then reloaded) or doc= an open .rfa; save/save_as.",
  group: "write",
  annotations: WD,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    family: S("Loaded family: edited in the background and reloaded. open: keep it open as doc #n."),
    name: S("Parameter name (type ops: the type name)."),
    new_name: S("New name."),
    kind: syn(E(["type","instance"], "Default type."), {"typeparameter":"type","instanceparameter":"instance"}),
    data: S("Data type: text, length, area, volume, angle, number, integer, yesno, material, url, image, or family_type:<Category>."),
    group: S("Properties group, e.g. Dimensions, Identity Data, Constraints. Default Other."),
    shared: S("Shared parameter name or GUID (from the shared parameter file)."),
    reporting: B("Reporting parameter."),
    formula: S("Formula, e.g. 'Width / 2'; '' clears."),
    value: S("Value (lengths mm; '900' ok)."),
    values: O("add_type: {param: value}."),
    types: SA("set_values: family types to change; ['*'] = all. Required when >1 type."),
    rows: OA("set_values: [{type, values:{param: value}}]."),
    copy_from: S("add_type: copy values from this type."),
    category: S("set_category: family category."),
    reload: B("With family=: reload into the project after the edit. Default true."),
    overwrite: B("On reload: overwrite project type parameter values. Default: only if this call changed type values."),
    into: SA("load_into: open projects to load into. Default: the pinned project only; more than one asks to confirm."),
    path: S("save_as: .rfa path."),
    ...WRITE_TAIL,
  },
  required: ["op"],
  ops: {
    open: op("family", "", meta(M, { kind: "lifecycle", tx: "lifecycle", job: "auto", cs: false }), [{"op":"open","family":"Single-Flush"}], { help: "Keeps the family doc open as #n and re-pins to it (notice TARGET_NOW)." }),
    add_param: op("name", "kind,data,group,shared,formula,value,reporting", M, [{"op":"add_param","family":"Single-Flush","name":"Kick Plate","data":"yesno","kind":"instance","group":"Construction"}], { help: "With family= the family is edited in the background and reloaded; overwrite of project type values defaults to false." }),
    remove_param: op("name", "", M, [{"op":"remove_param","family":"Single-Flush","name":"Kick Plate"}]),
    rename_param: op("name,new_name", "", M, [{"op":"rename_param","family":"Single-Flush","name":"Kick Plate","new_name":"Kick Plate Fitted"}]),
    set_param: op("name", "kind,group,reporting", M, [{"op":"set_param","family":"Single-Flush","name":"Kick Plate","kind":"type"}]),
    set_formula: op("name,formula", "", M, [{"op":"set_formula","family":"Single-Flush","name":"Leaf Width","formula":"Width - 2 * Frame Width"}]),
    set_values: op("name+value/rows", "types", M, [{"op":"set_values","family":"Single-Flush","name":"Width","value":"1000","types":["0915 x 2134mm"]}]),
    add_type: op("name", "copy_from,values", M, [{"op":"add_type","family":"Single-Flush","name":"1010 x 2250mm","values":{"Width":1010,"Height":2250}}]),
    rename_type: op("name,new_name", "", M, [{"op":"rename_type","family":"Single-Flush","name":"1010 x 2250mm","new_name":"1010 x 2300mm"}]),
    delete_type: op("name", "", M, [{"op":"delete_type","family":"Single-Flush","name":"1010 x 2300mm"}]),
    set_category: op("category", "", M, [{"op":"set_category","doc":"#3","category":"Furniture"}]),
    load_into: op("", "into,overwrite", meta(M, { scope: "family", blast: ["multi_doc"], cs: false }), [{"op":"load_into","doc":"#3"}], { help: "Defaults to the session's project pin only; other projects must be named in into. More than one doc returns NOT APPLIED." }),
    save: op("", "", meta(M, { kind: "lifecycle", scope: "family", tx: "lifecycle", cs: false }), [{"op":"save","doc":"#3"}]),
    save_as: op("path", "", meta(M, { kind: "lifecycle", scope: "family", tx: "lifecycle", blast: ["file_overwrite"], cs: false }), [{"op":"save_as","doc":"#3","path":"C:\\Families\\EBA Desk.rfa"}]),
  },
  errors: FAMILY_ERRORS,
};
