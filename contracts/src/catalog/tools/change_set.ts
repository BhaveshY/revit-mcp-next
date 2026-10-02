// Catalog entry for `change_set` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { meta, op, S, WD, WRITE, WRITE_TAIL } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { impl: "both", scope: "project_or_family", tx: "group", blast: ["delete","bulk","create"], cs: false });

export const tool: ToolSpec = {
  name: "change_set",
  title: "Atomic change set",
  description: "Run up to 100 write ops in one undo step, all or nothing; later ops use earlier results: '$0' (first id), '$0.ids', '$1.type'. ops=[{tool:'create_elements',op:'level',...}]. Not for open/save/sync/close.",
  group: "control",
  annotations: WD,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    ops: {"type":"array","description":"[{tool, op, ...that op's params}].","items":{"type":"object","properties":{"tool":{"type":"string","enum":["create_elements","place_family","modify_elements","set_parameters","edit_types","edit_views","view_graphics","edit_sheets","annotate","edit_schedules","edit_family","mep","structure","manage_document","worksharing","links"],"description":"Write tool."},"op":{"type":"string","description":"That tool's op."}},"required":["tool","op"]}},
    name: S("Undo history name. Default 'change_set'."),
    ...WRITE_TAIL,
  },
  ops: {
    "": op("ops", "name,doc,preview,confirm,units", M, [{"ops":[{"tool":"edit_sheets","op":"create","number":"A201","name":"Sections"},{"tool":"edit_views","op":"create","kind":"section","start":[0,4000],"end":[12000,4000]},{"tool":"edit_sheets","op":"place","sheet":"$0.sheet","view":"$1.view"}]}, {"ops":[{"tool":"create_elements","op":"level","elevation":7000,"name":"Level 3"},{"tool":"create_elements","op":"wall","level":"$0","points":[[0,0],[12000,0]],"type":"Generic - 200mm"},{"tool":"place_family","op":"place","type":"Single-Flush: 0915 x 2134mm","at":[6000,0],"level":"$0","host":"$1"}]}], { help: "One undo step, all or nothing. '$0' = first id of op 0, '$0.ids' all ids, '$1.type' a named output, '$prev' the previous op. Lifecycle ops (open/save/sync/close/export) are refused." }),
  },
};
