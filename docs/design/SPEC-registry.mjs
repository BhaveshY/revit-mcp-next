// SPEC op registry (normative). Emits SPEC-registry.json and SPEC-registry.generated.md.
// Every catalog op gets one entry keyed "<tool>.<op>" ("<tool>" for tools without a discriminator).
// W1-BROKER ports these values into the per-tool catalog files (contracts/src/catalog/tools/<tool>.ts, field `ops`).
import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { tools } from "./SPEC-catalog.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const DISCRIMINATOR = { list: "kind", check_model: "check", export: "format" };

// kind:   read | write | ui | lifecycle | control | code
// impl:   addin (add-in handler bound by [Op(key)]) | broker (broker only) | control (add-in control pipe, no UI thread) | both
// scope:  none | any | project | family | project_or_family
// tx:     none | in | own | temp | group | lifecycle
// job:    never | auto (may become j# at the budget) | always (always started as a job)
// blast:  delete | bulk | create | always | file_overwrite | unsaved_close | multi_doc | central_open | code_commit | button
// cs:     allowed inside change_set
const READ = { kind: "read", impl: "addin", scope: "any", ui: false, min: 2024, idle: true, tx: "none", blast: [], strict: false, job: "never", inproc: true, cs: false };
const WRITE = { kind: "write", impl: "addin", scope: "project", ui: false, min: 2024, idle: true, tx: "in", blast: [], strict: false, job: "never", inproc: true, cs: true };
const LIFE = { kind: "lifecycle", impl: "addin", scope: "project", ui: false, min: 2024, idle: false, tx: "lifecycle", blast: [], strict: false, job: "auto", inproc: false, cs: false };
const BROKER = { kind: "control", impl: "broker", scope: "none", ui: false, min: 2024, idle: true, tx: "none", blast: [], strict: false, job: "never", inproc: false, cs: false };

const TOOL_DEFAULTS = {
  status: BROKER, set_target: BROKER, help: BROKER, job_status: BROKER, cancel_job: BROKER, read_many: { ...BROKER, kind: "read" },
  list: READ, find_elements: READ, describe_elements: READ, get_view: READ,
  read_schedule: { ...READ, scope: "project", tx: "temp" },
  read_family: { ...READ, scope: "project_or_family", idle: false, job: "auto" },
  check_model: { ...READ, scope: "project" },
  get_quantities: { ...READ, scope: "project" },
  get_changes: READ,
  capture: { ...READ, tx: "temp", idle: false, job: "auto" },
  ui: { kind: "ui", impl: "addin", scope: "project_or_family", ui: true, min: 2024, idle: true, tx: "none", blast: [], strict: false, job: "never", inproc: true, cs: false },
  create_elements: { ...WRITE, blast: ["create"] },
  place_family: { ...WRITE, blast: ["create"] },
  modify_elements: { ...WRITE, blast: ["bulk"] },
  set_parameters: { ...WRITE, scope: "project_or_family", blast: ["bulk"] },
  edit_types: WRITE,
  edit_views: WRITE,
  view_graphics: { ...WRITE, blast: ["bulk"] },
  edit_sheets: WRITE,
  annotate: WRITE,
  edit_schedules: WRITE,
  edit_family: { ...WRITE, scope: "project_or_family", tx: "none", idle: false, inproc: false },
  mep: { ...WRITE, blast: ["create"] },
  structure: { ...WRITE, blast: ["create"] },
  manage_document: LIFE,
  worksharing: LIFE,
  links: { ...WRITE, idle: false, inproc: false },
  export: { ...LIFE, tx: "none", blast: ["file_overwrite"] },
  model_delivery: { ...LIFE, scope: "none", job: "always" },
  change_set: { ...WRITE, impl: "both", tx: "group", scope: "project_or_family", cs: false, blast: ["delete", "bulk", "create"] },
  undo: { ...WRITE, tx: "none", ui: true, idle: false, inproc: false, cs: false, scope: "project_or_family" },
  run_csharp: { kind: "code", impl: "addin", scope: "project_or_family", ui: false, min: 2024, idle: false, tx: "temp", blast: ["code_commit"], strict: false, job: "auto", inproc: false, cs: false },
};

const OVERRIDES = {
  "list.project_info": { scope: "project" }, "list.units": { scope: "project" },
  "check_model.clashes": { job: "auto" }, "check_model.purgeable": { job: "auto" },
  "ui.isolate": { tx: "in", ui: false }, "ui.hide": { tx: "in", ui: false }, "ui.reset": { tx: "in", ui: false },
  "ui.activate_view": { idle: false, ui: false }, "ui.activate_doc": { idle: false, ui: false, scope: "none" },
  "ui.dialogs": { kind: "control", impl: "control", scope: "none", ui: false },
  "ui.press": { kind: "control", impl: "control", scope: "none", ui: false, blast: ["button"] },
  "create_elements.stairs": { tx: "own", idle: false },
  "create_elements.level": { blast: ["create"] },
  "place_family.load": { blast: [] },
  "modify_elements.delete": { blast: ["delete"], inproc: false },
  "modify_elements.align": { blast: [] }, "modify_elements.join": { blast: [] }, "modify_elements.unjoin": { blast: [] },
  "modify_elements.switch_join": { blast: [] }, "modify_elements.cut": { blast: [] }, "modify_elements.uncut": { blast: [] },
  "modify_elements.attach": { blast: [], min: 2027 }, "modify_elements.detach": { blast: [], min: 2027 },
  "modify_elements.split": { blast: [] }, "modify_elements.reshape": { blast: [] },
  "modify_elements.group": { blast: [] }, "modify_elements.ungroup": { blast: [] }, "modify_elements.place_group": { blast: ["create"] },
  "edit_types.delete_type": { blast: ["delete"], inproc: false },
  "edit_views.create": { strict: true }, "edit_views.duplicate": { strict: true },
  "edit_views.delete": { blast: ["delete"], inproc: false },
  "view_graphics.reset": { blast: ["bulk"] },
  "edit_sheets.create": { blast: ["create"] }, "edit_sheets.duplicate": { strict: true },
  "edit_sheets.remove_viewport": { inproc: false },
  "annotate.tag_all": { blast: ["create"] }, "annotate.copy_to_views": { strict: true },
  "annotate.dimension": { strict: true }, "annotate.dimension_edit": { strict: true },
  "edit_family.open": { kind: "lifecycle", tx: "lifecycle", cs: false, job: "auto" },
  "edit_family.save": { kind: "lifecycle", tx: "lifecycle", cs: false, scope: "family" },
  "edit_family.save_as": { kind: "lifecycle", tx: "lifecycle", cs: false, scope: "family", blast: ["file_overwrite"] },
  "edit_family.load_into": { cs: false, blast: ["multi_doc"], scope: "family" },
  "manage_document.activate": { kind: "ui", tx: "none", job: "never", scope: "none" },
  "manage_document.open": { scope: "none", blast: ["central_open"] },
  "manage_document.new_project": { scope: "none", blast: ["file_overwrite"] },
  "manage_document.new_family": { scope: "none", blast: ["file_overwrite"] },
  "manage_document.save": { scope: "project_or_family" },
  "manage_document.save_as": { scope: "project_or_family", blast: ["file_overwrite"] },
  "manage_document.close": { scope: "project_or_family", blast: ["unsaved_close"] },
  "manage_document.purge": { kind: "write", tx: "in", idle: true, cs: true, job: "auto", blast: ["delete"] },
  "manage_document.set_units": { kind: "write", tx: "in", idle: true, cs: true, job: "never" },
  "manage_document.coordinates": { kind: "write", tx: "in", idle: true, cs: true, job: "never", blast: ["always"] },
  "worksharing.sync": { blast: ["always"], job: "always" },
  "worksharing.reload_latest": { job: "always" },
  "worksharing.relinquish": { job: "auto" },
  "worksharing.enable": { blast: ["always"] },
  "worksharing.borrow": { tx: "none" },
  "worksharing.workset_create": { kind: "write", tx: "in", idle: true, cs: true, job: "never" },
  "worksharing.workset_rename": { kind: "write", tx: "in", idle: true, cs: true, job: "never" },
  "worksharing.set_active": { kind: "write", tx: "in", idle: true, cs: true, job: "never" },
  "worksharing.move_to": { kind: "write", tx: "in", idle: true, cs: true, job: "never", blast: ["bulk"] },
  "links.reload": { kind: "lifecycle", tx: "lifecycle", cs: false, job: "auto" },
  "links.reload_from": { kind: "lifecycle", tx: "lifecycle", cs: false, job: "auto" },
  "links.unload": { kind: "lifecycle", tx: "lifecycle", cs: false },
  "links.remove": { blast: ["always"] },
  "links.acquire_coordinates": { blast: ["always"] },
  "export.ifc": { tx: "temp" },
  "export.csv": { job: "auto" },
  "model_delivery.save_recipe": { kind: "control", impl: "broker", job: "never" },
  "model_delivery.get_recipe": { kind: "read", impl: "broker", job: "never" },
  "model_delivery.list_recipes": { kind: "read", impl: "broker", job: "never" },
  "model_delivery.inspect": { kind: "read", job: "always" },
  "model_delivery.preview": { kind: "read", job: "always" },
  "model_delivery.execute": { blast: ["always"] },
  "model_delivery.fixture": { blast: ["always"] },
};

const registry = [];
for (const t of tools) {
  const disc = DISCRIMINATOR[t.name] ?? (t.properties.op && t.properties.op.enum ? "op" : null);
  const values = disc ? t.properties[disc].enum : [null];
  for (const v of values) {
    const key = v ? `${t.name}.${v}` : t.name;
    const base = TOOL_DEFAULTS[t.name];
    if (!base) throw new Error("no defaults for " + t.name);
    registry.push({ key, tool: t.name, op: v, ...base, ...(OVERRIDES[key] ?? {}) });
  }
}
for (const k of Object.keys(OVERRIDES)) if (!registry.find((r) => r.key === k)) throw new Error("override for unknown key " + k);

writeFileSync(join(here, "SPEC-registry.json"), JSON.stringify(registry));
const yn = (b) => (b ? "y" : "-");
let md = "| key | kind | impl | scope | ui | min | idle | tx | blast | strict | job | inproc | cs |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|\n";
for (const r of registry) md += `| ${r.key} | ${r.kind} | ${r.impl} | ${r.scope} | ${yn(r.ui)} | ${r.min} | ${yn(r.idle)} | ${r.tx} | ${r.blast.join(",") || "-"} | ${yn(r.strict)} | ${r.job} | ${yn(r.inproc)} | ${yn(r.cs)} |\n`;
writeFileSync(join(here, "SPEC-registry.generated.md"), md);
const byKind = {};
for (const r of registry) byKind[r.kind] = (byKind[r.kind] ?? 0) + 1;
console.log("registry entries", registry.length, JSON.stringify(byKind), "addin-bound", registry.filter((r) => r.impl === "addin" || r.impl === "both").length);
