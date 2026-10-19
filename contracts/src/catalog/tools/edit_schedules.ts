// Catalog entry for `edit_schedules` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, meta, op, P, S, SA, syn, W, WRITE, WRITE_TAIL_NOUNITS } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE);

export const tool: ToolSpec = {
  name: "edit_schedules",
  title: "Edit schedules",
  description: "Create schedules (regular, material takeoff, key) with fields, filters, sorting; add, remove, order, rename or hide fields; set filters, sort/group, itemize, totals; duplicate. Read rows with read_schedule.",
  group: "write",
  annotations: W,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    schedule: S("Schedule name or id."),
    category: S("create: category, e.g. Doors."),
    name: P.name,
    kind: syn(E(["regular","material_takeoff","key"], "create: default regular."), {"schedule":"regular","quantities":"regular","takeoff":"material_takeoff","keyschedule":"key"}),
    fields: SA("Field names, e.g. ['Mark','Type','Width']."),
    field: S("One field name."),
    heading: S("Column heading."),
    hidden: B("Hide the column."),
    filters: SA("['Level = Level 1', 'Width > 900'] (max 8, ANDed); [] clears."),
    sort: SA("Sort/group fields in order; prefix '-' for descending."),
    headers: B("set_sort: group headers. Default false."),
    itemize: B("Itemize every instance. Default true."),
    totals: B("Grand totals (set_field: column total)."),
    ...WRITE_TAIL_NOUNITS,
  },
  required: ["op"],
  ops: {
    create: op("category", "name,kind,fields,filters,sort,itemize,totals", M, [{"op":"create","category":"Doors","name":"Door Schedule","fields":["Mark","Type","Width","Height","Level"],"sort":["Level","Mark"]}, {"op":"create","category":"Walls","kind":"material_takeoff","name":"Wall Materials","fields":["Material: Name","Material: Area"]}], { help: "Fields resolve by name, heading or English label (read_schedule {category, available:true} lists them). Without fields a regular schedule gets Family and Type + Count. category 'Multi-Category' makes a multi-category schedule.", api: "ViewSchedule.CreateSchedule / CreateMaterialTakeoff / CreateKeySchedule", errors: ["NAME_TAKEN", "NOT_FOUND", "INVALID_ARGS"] }),
    add_fields: op("schedule,fields", "", M, [{"op":"add_fields","schedule":"Door Schedule","fields":["Fire Rating"]}], { help: "Appends fields; fields already in the schedule are skipped with a warning.", errors: ["NOT_FOUND"] }),
    remove_fields: op("schedule,fields", "", M, [{"op":"remove_fields","schedule":"Door Schedule","fields":["Fire Rating"]}], { help: "Removes fields; filters and sorting that use them are removed too (warned).", errors: ["NOT_FOUND"] }),
    set_field: op("schedule,field", "heading,hidden,totals", M, [{"op":"set_field","schedule":"Door Schedule","field":"Mark","heading":"Door No."}, {"op":"set_field","schedule":"Wall Schedule","field":"Area","totals":true}], { help: "Column heading, hidden, and totals (column total; numeric fields only).", errors: ["NOT_FOUND", "INVALID_ARGS"] }),
    order_fields: op("schedule,fields", "", M, [{"op":"order_fields","schedule":"Door Schedule","fields":["Level","Mark","Type","Width","Height"]}], { help: "The named fields come first in this order; the others follow in their current order." }),
    set_filters: op("schedule,filters", "", M, [{"op":"set_filters","schedule":"Door Schedule","filters":["Level = Level 1"]}, {"op":"set_filters","schedule":"Door Schedule","filters":["Width >= 900","Mark begins with D"]}], { help: "Replaces all filters (ANDed, max 8; [] clears). Ops: = != > >= < <= contains, not contains, begins with, ends with, has value, has no value. Lengths in mm; Level and other element fields by name. Fields not in the schedule are added hidden.", errors: ["INVALID_ARGS", "NOT_FOUND"], emptyOk: "filters" }),
    set_sort: op("schedule,sort", "headers,totals", M, [{"op":"set_sort","schedule":"Door Schedule","sort":["Level","-Mark"],"headers":true}], { help: "Replaces the sort/group fields in order ('-' = descending; [] clears). headers adds group headers; totals adds group footers and grand totals.", errors: ["NOT_FOUND", "INVALID_ARGS"], emptyOk: "sort" }),
    set_options: op("schedule", "name,itemize,totals", M, [{"op":"set_options","schedule":"Door Schedule","itemize":true,"totals":true}, {"op":"set_options","schedule":"Door Schedule","name":"Doors - All"}], { help: "Rename (NAME_TAKEN when used), itemize every instance, grand totals.", errors: ["NAME_TAKEN"] }),
    duplicate: op("schedule,name", "", M, [{"op":"duplicate","schedule":"Door Schedule","name":"Door Schedule - Level 2"}], { help: "Copies the schedule definition under a new name.", errors: ["NAME_TAKEN", "NOT_FOUND"] }),
  },
};
