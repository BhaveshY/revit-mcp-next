#!/usr/bin/env node
// Generates the tool documentation from the emitted catalog (artifacts/catalog/catalog.json, written by `npm run build`).
// It never spawns the broker. Outputs (SPEC §14):
//   docs/tools.md                                              full reference (every tool, op, param and example)
//   README.md                       <!-- tool-table:begin/end -->   the tool overview table
//   docs/troubleshooting.md         <!-- error-codes:begin/end -->  every ErrorCatalog code with its fix
//   plugins/{codex,claude}/revit-mcp-next/skills/revit/SKILL.md   <!-- skill-tool-map:begin/end -->
//   plugins/{codex,claude}/revit-mcp-next/skills/revit/references/tools.md   the skill's tool reference
// Usage: node scripts/gen-tool-docs.mjs [--check] [--catalog <path>]
//   --check  writes nothing; exits 1 and names every file that is out of date.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const argv = process.argv.slice(2);
const check = argv.includes("--check");
const catalogArg = argv.indexOf("--catalog");
const catalogPath = catalogArg >= 0 ? resolve(argv[catalogArg + 1] ?? "") : join(repo, "artifacts", "catalog", "catalog.json");
if (!existsSync(catalogPath)) {
  console.error(`gen-tool-docs: ${catalogPath} is missing; run npm run build first`);
  process.exit(2);
}
const catalog = JSON.parse(readFileSync(catalogPath, "utf8"));
const tools = catalog.tools;
const byName = new Map(tools.map((t) => [t.name, t]));
const problems = [];

// ------------------------------------------------------------------------------------------------ shared helpers

const GROUPS = {
  session: "Session and target",
  read: "Reads",
  visual: "Visual",
  ui: "Revit window (no model change)",
  write: "Writes",
  control: "Batch, undo, jobs, help",
  optin: "Opt-in (listed only when the user enabled it)",
};
const TAIL = ["doc", "preview", "confirm", "units"];
const BLAST = {
  delete: "over 20 deleted",
  bulk: "over 200 modified",
  create: "over 500 created",
  always: "always",
  file_overwrite: "if a file exists",
  unsaved_close: "if unsaved",
  multi_doc: "if several docs",
  central_open: "central:true",
  code_commit: "commit",
  button: "buttons other than Cancel/Close",
};
const req = (r) => (!r.trim() ? "" : r.split(",").map((g) => g.split("/").join("|")).join(", "));
const splitList = (s) => s.split(",").map((x) => x.trim()).filter(Boolean);
const shortHelp = (s) => {
  if (!s) return "";
  const parts = s.match(/[^.!?]+[.!?]+(\s|$)/g) || [s];
  let o = "";
  for (const p of parts) {
    if ((o + p).length > 260 && o) break;
    o += p;
  }
  return o.trim();
};
const firstSentence = (s) => (s.match(/^[^.!?]+[.!?]/)?.[0] ?? s).trim();
const fmtCall = (tool, disc, ex) => {
  const o = disc && disc in ex ? { [disc]: ex[disc], ...Object.fromEntries(Object.entries(ex).filter(([k]) => k !== disc)) } : ex;
  return `${tool} ${JSON.stringify(o)}`;
};
const typeStr = (p) => {
  let t = p.type === "array" ? `${(p.items && p.items.type) || "any"}[]` : p.type;
  if (p.unit) t += ` ${p.unit}`;
  return t;
};
const annotation = (t) => (t.annotations.readOnlyHint ? "read-only" : t.annotations.destructiveHint ? "writes, can delete" : "writes");
const cell = (s) => String(s).replace(/\|/g, "\\|");

// ------------------------------------------------------------------------------------------------ tool reference body

/** Per-tool sections, shared by docs/tools.md and the skill's references/tools.md. */
function toolSections(headingLevel) {
  const h1 = "#".repeat(headingLevel);
  const h2 = "#".repeat(headingLevel + 1);
  const out = [];
  let lastGroup = null;
  for (const t of tools) {
    if (t.group !== lastGroup) {
      out.push("");
      out.push(`${h1} ${GROUPS[t.group] || t.group}`);
      lastGroup = t.group;
    }
    out.push("");
    out.push(`${h2} ${t.name}`);
    out.push("");
    out.push(`${t.description} (${annotation(t)}${t.profiles.includes("core") ? "; core profile" : ""})`);
    out.push("");
    const props = t.properties;
    const disc = t.discriminator;
    const ops = Object.entries(t.ops);
    const reqParams = new Set(ops.flatMap(([, o]) => o.req.split(/[,/+]/).map((x) => x.trim()).filter(Boolean)));
    const tail = TAIL.filter((k) => k in props && !reqParams.has(k));
    const optSets = ops.map(([, o]) => o.opt);
    const commonOpt = ops.length > 2 && optSets.every((x) => x === optSets[0]) ? optSets[0] : null;
    out.push("Params:");
    for (const [k, p] of Object.entries(props)) {
      if (TAIL.includes(k) && !reqParams.has(k)) continue;
      const values = p.enum || (p.items && p.items.enum);
      out.push(`- \`${k}\` (${typeStr(p)}${values ? `: ${values.join("|")}` : ""}): ${p.description}`);
      if (p.items && p.items.properties) {
        for (const [k2, p2] of Object.entries(p.items.properties)) {
          const v2 = p2.enum;
          out.push(`  - \`${k}[].${k2}\` (${p2.type}${v2 ? `: ${v2.join("|")}` : ""}): ${p2.description}`);
        }
      }
    }
    if (tail.length) out.push(`- every op also takes: ${tail.map((k) => `\`${k}\``).join(", ")}`);
    out.push("");
    if (commonOpt) {
      out.push(`Optional for every ${disc}: ${splitList(commonOpt).filter((x) => !TAIL.includes(x)).join(", ")}`);
      out.push("");
    }
    out.push(disc ? `Ops (\`${disc}\`):` : "Call:");
    for (const [op, o] of ops) {
      const key = op ? `${t.name}.${op}` : t.name;
      const r = req(o.req);
      const opt = commonOpt ? "" : splitList(o.opt).filter((s) => !TAIL.includes(s)).join(", ");
      const flags = [];
      if (o.meta.min !== 2024) flags.push(`Revit ${o.meta.min}+`);
      if (o.meta.blast.length) flags.push(`NOT APPLIED ${o.meta.blast.map((b) => BLAST[b] || b).join(", ")}`);
      if (o.meta.job === "always") flags.push("runs as job j#");
      else if (o.meta.job === "auto") flags.push("may become job j#");
      if (o.meta.ui) flags.push("doc must be active in Revit");
      if (t.name !== "change_set" && (o.meta.kind === "write" || o.meta.kind === "lifecycle") && !o.meta.cs) flags.push("not in change_set");
      if (o.meta.scope === "family") flags.push("family doc");
      out.push(`- \`${key}\`: ${r || "(none)"}${opt ? `; ${opt}` : ""}${flags.length ? ` [${flags.join("; ")}]` : ""}`);
      const h = shortHelp(o.help);
      if (h) out.push(`  ${h}`);
      if (o.examples[0]) {
        const call = fmtCall(t.name, disc, o.examples[0]);
        out.push(call.length > 400 ? `  e.g. see \`help {"tool":"${t.name}","op":"${op}"}\` (long example)` : `  e.g. \`${call}\``);
      }
    }
  }
  return out;
}

const READING_GUIDE = [
  "How to read an op line: `tool.op: required; optional`.",
  "- In the required part `,` separates groups that are all needed, `|` means exactly one of, `+` means these go together. `ids|from|filter` = choose the elements one way.",
  "- Example: `create_elements.wall: level, points|start+end; type, height` = level is required, plus either points or start and end; type and height are optional.",
  "- `doc` (target doc), `preview` (dry run), `confirm` (token from NOT APPLIED) and `units` (default mm) are listed once per tool under \"every op also takes\".",
  "- Lengths mm, angles degrees, areas m2, volumes m3, pipe slope percent. Points `[x,y]` or `[x,y,z]` in mm.",
];

function skillReference() {
  const out = [
    "# Revit tool reference",
    "",
    "Generated from the revit-mcp-next catalog (`contracts/src/catalog`). The server is the source of truth: `help {\"tool\":\"<tool>\",\"op\":\"<op>\"}` returns the same params plus working examples, live.",
    "",
    ...READING_GUIDE,
    "- Search this file for `## <tool>` or `<tool>.<op>`.",
    ...toolSections(1),
    "",
  ];
  return out.join("\n");
}

// ------------------------------------------------------------------------------------------------ docs/tools.md

function docsTools() {
  const keys = catalog.registry.length;
  const bound = catalog.registry.filter((r) => r.impl === "addin").length;
  const defaults = tools.filter((t) => t.group !== "optin");
  const core = tools.filter((t) => t.profiles.includes("core"));
  const out = [
    "# Tool reference",
    "",
    "<!-- Generated by scripts/gen-tool-docs.mjs from artifacts/catalog/catalog.json. Do not edit; run `npm run build` then `node scripts/gen-tool-docs.mjs`. -->",
    "",
    `The \`revit\` MCP server lists ${defaults.length} tools by default (${core.length} with \`REVIT_MCP_NEXT_PROFILE=core\`); \`run_csharp\` is added only when the user enabled code execution. The tools expose ${keys} op keys (\`<tool>.<op>\`), ${bound} of them served by the Revit add-in and the rest by the broker. Catalog hash \`${catalog.hash.slice(0, 12)}\`.`,
    "",
    "The live server is the source of truth: `help {\"tool\":\"<tool>\",\"op\":\"<op>\"}` returns the same params plus working examples, and `help {\"topic\":\"<CODE>\"}` explains an error code ([troubleshooting.md](troubleshooting.md) lists them all).",
    "",
    "## Overview",
    "",
    "| Tool | Kind | Ops | Core | What it does |",
    "|---|---|---|---|---|",
  ];
  for (const t of tools) {
    const ops = Object.keys(t.ops).filter(Boolean);
    out.push(`| [\`${t.name}\`](#${t.name}) | ${annotation(t)} | ${ops.length || "-"} | ${t.profiles.includes("core") ? "yes" : ""} | ${cell(firstSentence(t.description))} |`);
  }
  out.push(
    "",
    "Flags in the op lists: `NOT APPLIED ...` = when the call needs the user's OK (confirm token); `runs as job j#` / `may become job j#` = long work continues as a job (`job_status`); `doc must be active in Revit` = the target doc must be the active window (NEEDS_ACTIVE_DOC otherwise); `not in change_set` = call it alone; `Revit 2027+` = not on 2024 (UNSUPPORTED_VERSION).",
    "",
    ...READING_GUIDE,
    ...toolSections(2),
    "",
  );
  return out.join("\n");
}

// ------------------------------------------------------------------------------------------------ README tool table

function readmeToolTable() {
  const rows = ["| Group | Tools |", "|---|---|"];
  const groups = [];
  for (const t of tools) {
    let g = groups.find((x) => x.group === t.group);
    if (!g) groups.push((g = { group: t.group, names: [] }));
    g.names.push(`\`${t.name}\``);
  }
  for (const g of groups) rows.push(`| ${GROUPS[g.group] || g.group} | ${g.names.join(", ")} |`);
  return rows.join("\n");
}

// ------------------------------------------------------------------------------------------------ SKILL tool map

const listKinds = Object.keys(byName.get("list")?.ops ?? {}).filter(Boolean).length;
/** Intent and example call per tool, in catalog order. The call's tool and op are checked against the catalog. */
const SKILL_MAP = {
  status: ["What is open, target, levels, busy/dialog", 'status {}'],
  set_target: ["Choose the document", 'set_target {"doc":"#2"}'],
  list: ["List levels, views, sheets, types, families, rooms, materials, links...", `list {"kind":"levels"}`, `(${listKinds} kinds)`],
  find_elements: ["Find elements, count, group", 'find_elements {"category":["Doors"],"level":"Level 1"}'],
  describe_elements: ["Parameters, geometry, relations of elements", 'describe_elements {"from":"r3"}'],
  get_view: ["View settings (scale, template, crop, range)", 'get_view {"view":"Level 1"}'],
  read_schedule: ["Schedule rows / fields for a new schedule", 'read_schedule {"schedule":"Door Schedule"}'],
  read_family: ["Family params, formulas, per-type values", 'read_family {"family":"Single-Flush"}'],
  check_model: ["Model health: stats, warnings, readiness, purgeable, clashes", 'check_model {"check":"warnings"}'],
  get_quantities: ["Quantity takeoff", 'get_quantities {"by":"type","category":["Walls"]}'],
  get_changes: ["What changed", 'get_changes {"since":"last"}'],
  read_many: ["Up to 8 reads in one call", 'read_many {"calls":[...]}'],
  capture: ["See a view, sheet or elements", 'capture {"view":"Level 1"}'],
  ui: ["Select, zoom, isolate, open/close views, dialogs", 'ui {"op":"select","from":"r3"}'],
  create_elements: ["Levels, grids, walls, floors, roofs, ceilings, rooms, openings, stairs, railings, toposolid", 'create_elements {"op":"wall",...}'],
  place_family: ["Place or load families (doors, windows, furniture...)", 'place_family {"op":"place",...}'],
  modify_elements: ["Move, copy, rotate, mirror, array, align, delete, change type, join, split, group", 'modify_elements {"op":"move",...}'],
  set_parameters: ["Set parameter values (instances, types, views, project info)", 'set_parameters {"from":"r3","values":{...}}'],
  edit_types: ["Duplicate/rename types, wall layers, materials", 'edit_types {"op":"duplicate_type",...}'],
  edit_views: ["Create and set up views (plans, sections, 3D, callouts)", 'edit_views {"op":"create","kind":"section",...}'],
  view_graphics: ["Overrides, hide/unhide, view filters, color by parameter", 'view_graphics {"op":"color_by",...}'],
  edit_sheets: ["Sheets, place views, titleblocks, revisions", 'edit_sheets {"op":"create",...}'],
  annotate: ["Tags, text, dimensions, detail lines, regions", 'annotate {"op":"tag_all",...}'],
  edit_schedules: ["Create and change schedules", 'edit_schedules {"op":"create",...}'],
  edit_family: ["Family parameters, formulas, types; load into project", 'edit_family {"op":"add_param",...}'],
  mep: ["Pipes, ducts, conduits, trays, systems, spaces", 'mep {"op":"pipe",...}'],
  structure: ["Columns, beams, braces, beam systems, foundations", 'structure {"op":"column",...}'],
  manage_document: ["Open, save, save as, close, new project/family, purge", 'manage_document {"op":"open",...}'],
  worksharing: ["Sync, reload latest, worksets, borrow", 'worksharing {"op":"sync",...}'],
  links: ["Link or import RVT/IFC/CAD, reload, unload", 'links {"op":"link_rvt",...}'],
  export: ["PDF, DWG, DXF, IFC, NWC, image, CSV, FBX, gbXML", 'export {"format":"pdf",...}'],
  model_delivery: ["Packages of standalone RVTs from a recipe", 'model_delivery {"op":"inspect",...}'],
  change_set: ["Many writes as one undo step", 'change_set {"ops":[...]}'],
  undo: ["Undo or redo your own writes", 'undo {}'],
  job_status: ["Result of a job or unclear write", 'job_status {"id":"j3"}'],
  cancel_job: ["Stop a job", 'cancel_job {"id":"j3"}'],
  help: ["Params and examples of any op; topics; error codes", 'help {"tool":"annotate","op":"tag_all"}'],
  run_csharp: ["C# on the Revit API (opt-in, last resort)", 'run_csharp {"code":"...","mode":"read"}'],
};

function skillToolMap() {
  const rows = ["| Intent | Call |", "|---|---|"];
  for (const t of tools) {
    const entry = SKILL_MAP[t.name];
    if (!entry) {
      problems.push(`SKILL tool map: catalog tool ${t.name} has no row in SKILL_MAP (scripts/gen-tool-docs.mjs)`);
      continue;
    }
    const [intent, call, suffix] = entry;
    const m = /^(\w+) (\{.*\})$/.exec(call);
    if (!m || m[1] !== t.name) problems.push(`SKILL tool map: the call for ${t.name} does not start with that tool: ${call}`);
    const disc = t.discriminator;
    const opMatch = disc ? new RegExp(`"${disc}":"([^"]+)"`).exec(call) : null;
    if (opMatch && !(opMatch[1] in t.ops)) problems.push(`SKILL tool map: ${t.name} has no ${disc} "${opMatch[1]}"`);
    rows.push(`| ${intent} | \`${call}\`${suffix ? ` ${suffix}` : ""} |`);
  }
  for (const name of Object.keys(SKILL_MAP)) if (!byName.has(name)) problems.push(`SKILL tool map: ${name} is not in the catalog`);
  return rows.join("\n");
}

// ------------------------------------------------------------------------------------------------ error codes

const placeholder = (s) => String(s).replace(/\$\{(?:details|args)\.([\w.]+)\}/g, "<$1>").replace(/\$\{([\w.]+)\}/g, "<$1>");
/** A template's args: an object (rendered as JSON) or one placeholder for a whole args object. */
const argsText = (args) => (typeof args === "string" ? placeholder(args).replace(/^<(\w+)>$/, "<$1 args>") : placeholder(JSON.stringify(args ?? {})).replace(/"(<[\w.]+>)"/g, "$1"));
function fixText(f) {
  if (!f) return "";
  switch (f.kind) {
    case "ask":
      return `ask the user: ${placeholder(f.text)}`;
    case "retry_in":
      return `retry in ${f.seconds} s`;
    case "call":
      return `\`${placeholder(f.tool)} ${argsText(f.args)}\`${f.note ? ` (${placeholder(f.note)})` : ""}`;
    case "same_call_with":
      return `${Object.keys(f.set ?? {}).length ? `the same call with \`${argsText(f.set)}\`` : "repeat the identical call"}${f.note ? ` (${placeholder(f.note)})` : ""}`;
    case "same_call_without":
      return `the same call without ${(f.drop ?? []).map((d) => `\`${d}\``).join(", ")}${f.note ? ` (${placeholder(f.note)})` : ""}`;
    case "choose":
      return `\`${placeholder(f.tool)} ${placeholder(JSON.stringify({ ...(f.base ?? {}), [f.arg]: "<one of the options>" })).replace(/"(<[\w. ]+>)"/g, "$1")}\``;
    default:
      return placeholder(JSON.stringify(f));
  }
}
function fixCell(e) {
  const list = Array.isArray(e.fix) ? e.fix : [e.fix];
  const parts = list.filter(Boolean).map((f, i) => (f.when && list.length > 1 ? `when ${f.when.path.replace(/^details\./, "")}${f.when.equals !== undefined ? `=${f.when.equals}` : ""}: ` : i > 0 ? "otherwise: " : "") + fixText(f));
  let text = parts.join("; ");
  if (e.or?.length) text += `; or ${e.or.map((f) => (f.when ? `(when ${f.when.path.replace(/^details\./, "")}) ` : "") + fixText(f)).join(", or ")}`;
  return cell(text);
}
function errorTable() {
  const rows = ["| Code | Meaning | Model changed? | fix: line |", "|---|---|---|---|"];
  for (const e of catalog.errors) {
    rows.push(`| \`${e.code}\` | ${cell(e.meaning)} | ${e.nothingChanged ? "no" : "maybe"} | ${fixCell(e)} |`);
  }
  rows.push("", "Warnings (`warn:` lines; the call still worked):", "", "| Code | Meaning |", "|---|---|");
  for (const [code, meaning] of Object.entries(catalog.warnings)) rows.push(`| \`${code}\` | ${cell(meaning)} |`);
  rows.push("", "Notices (`notice:` lines; information only):", "", "| Code | Meaning |", "|---|---|");
  for (const [code, meaning] of Object.entries(catalog.notices)) rows.push(`| \`${code}\` | ${cell(meaning)} |`);
  return rows.join("\n");
}

// ------------------------------------------------------------------------------------------------ write / check

const results = [];
const norm = (s) => s.replace(/\r\n/g, "\n");
function emitFile(rel, content) {
  const path = join(repo, rel);
  const before = existsSync(path) ? norm(readFileSync(path, "utf8")) : null;
  results.push({ rel, changed: before !== content });
  if (!check && before !== content) writeFileSync(path, content, "utf8");
}
function emitRegion(rel, name, content) {
  const path = join(repo, rel);
  if (!existsSync(path)) {
    problems.push(`${rel} is missing`);
    return;
  }
  const before = norm(readFileSync(path, "utf8"));
  const begin = `<!-- ${name}:begin -->`;
  const end = `<!-- ${name}:end -->`;
  const i = before.indexOf(begin);
  const j = before.indexOf(end);
  if (i < 0 || j < i) {
    problems.push(`${rel}: markers ${begin} / ${end} not found`);
    return;
  }
  const after = `${before.slice(0, i + begin.length)}\n${content}\n${before.slice(j)}`;
  emitFile(rel, after);
}

emitFile("docs/tools.md", docsTools());
emitRegion("README.md", "tool-table", readmeToolTable());
emitRegion("docs/troubleshooting.md", "error-codes", errorTable());
const map = skillToolMap();
const reference = skillReference();
for (const kind of ["codex", "claude"]) {
  const skill = `plugins/${kind}/revit-mcp-next/skills/revit`;
  emitRegion(`${skill}/SKILL.md`, "skill-tool-map", map);
  emitFile(`${skill}/references/tools.md`, reference);
}

if (problems.length) {
  for (const p of problems) console.error(`gen-tool-docs: ${p}`);
  process.exit(1);
}
const stale = results.filter((r) => r.changed).map((r) => r.rel);
const source = relative(repo, catalogPath).replace(/\\/g, "/");
if (check) {
  if (stale.length) {
    console.error(`gen-tool-docs: out of date (run node scripts/gen-tool-docs.mjs): ${stale.join(", ")}`);
    process.exit(1);
  }
  console.log(`gen-tool-docs: ok (${results.length} outputs match ${source}, catalog ${catalog.hash.slice(0, 12)})`);
} else {
  console.log(`gen-tool-docs: ${stale.length ? `wrote ${stale.join(", ")}` : "everything up to date"} (from ${source}, catalog ${catalog.hash.slice(0, 12)})`);
}
