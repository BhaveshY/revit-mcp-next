// Fixtures (SPEC §13.1-13.2, D4 §8.5): template discovery, run folders <home>\runs\<runId>\<year>\, disposable
// projects created through manage_document new_project, and the write guard (every write targets a doc under the
// run folder; nothing outside the e2e home is ever written).

import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { basename, join, relative, resolve, isAbsolute } from "node:path";
import { defaultProjectTemplateCandidates } from "@revit-mcp-next/contracts/home";
import { AssertionFailure } from "./assert.mjs";

/** The project template for a year: settings default, the English metric template, else any .rte found. */
export function projectTemplate(year) {
  for (const candidate of defaultProjectTemplateCandidates(year)) if (existsSync(candidate)) return candidate;
  const root = join(process.env.ProgramData ?? process.env.PROGRAMDATA ?? "C:\\ProgramData", "Autodesk", `RVT ${year}`, "Templates");
  const found = [];
  const walk = (dir, depth) => {
    if (depth > 2 || !existsSync(dir)) return;
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const path = join(dir, entry.name);
      if (entry.isDirectory()) walk(path, depth + 1);
      else if (/\.rte$/i.test(entry.name)) found.push(path);
    }
  };
  walk(root, 0);
  found.sort((a, b) => Number(/metric/i.test(b)) - Number(/metric/i.test(a)));
  return found[0] ?? null;
}

export function newRunId(year) {
  const d = new Date();
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}${year ? `-${year}` : ""}`;
}

export function runRoot(home, runId) {
  const dir = join(home, "runs", runId);
  mkdirSync(dir, { recursive: true });
  return dir;
}

export function runDir(home, runId, year) {
  const dir = join(runRoot(home, runId), String(year));
  mkdirSync(dir, { recursive: true });
  return dir;
}

export function isUnder(path, root) {
  const rel = relative(resolve(root).toLowerCase(), resolve(path).toLowerCase());
  return rel === "" || (!rel.startsWith("..") && !isAbsolute(rel));
}

/**
 * The write guard: file params of writes must be under the run folder, and (when known) the target doc too.
 * @param {string} root run root
 * @param {() => string|null} targetPath current target doc path, if known
 */
export function makeWriteGuard(root, targetPath) {
  const FILE_PARAMS = ["path", "folder", "local"];
  return (tool, args) => {
    for (const p of FILE_PARAMS) {
      const v = args?.[p];
      if (typeof v === "string" && v.trim() && (/^[a-z]:[\\/]/i.test(v) || v.startsWith("\\\\")) && !isUnder(v, root))
        throw new AssertionFailure(`write guard: ${tool}.${p} ${v} is outside the run folder ${root}`);
    }
    const current = targetPath?.();
    if (current && !isUnder(current, root) && !["set_target", "ui"].includes(tool) && !(tool === "manage_document" && ["new_project", "new_family", "open"].includes(args?.op)))
      throw new AssertionFailure(`write guard: the target doc ${current} is outside the run folder ${root}`);
  };
}

/** Create a disposable project through the product (manage_document new_project, activate true). */
/** options.levels:false keeps the template's level names (no write of ours in the new doc, e.g. S36 undo checks). */
export async function newProject(t, name, options = {}) {
  const template = projectTemplate(t.year);
  if (!template) throw new AssertionFailure(`no Revit ${t.year} project template found under ProgramData\\Autodesk\\RVT ${t.year}\\Templates`);
  const path = join(t.yearDir, `${name}.rvt`);
  // doc "@<year>" pins the call to this run's Revit year: with several Revit sessions open and no document yet, the
  // broker would otherwise pick the most recently used Revit (possibly another year that cannot read this template).
  const r = await t.call("manage_document", { op: "new_project", path, template, activate: true, doc: `@${t.year}` }, { timeoutMs: 120_000 });
  t.docs.set(name, path);
  if (r.status === "ok" && options.levels !== false) await normalizeLevels(t);
  return { r, path, template };
}

/**
 * Scenarios address the two lowest levels as "Level 1" and "Level 2"; templates name them differently per language and
 * year (L1/L2, Ebene 0/1, Level 1/2). Rename them through the product (set_parameters on the level's Name) so every
 * scenario sees the same names; this also exercises set_parameters on datums.
 */
export async function normalizeLevels(t) {
  const levels = await t.call("list", { kind: "levels" });
  const cols = levels.json?.cols ?? [];
  const rows = (levels.json?.rows ?? []).map((row) => Object.fromEntries(cols.map((c, i) => [c, row[i]])));
  rows.sort((a, b) => Number(a.elevation) - Number(b.elevation));
  const wanted = ["Level 1", "Level 2"];
  const updates = rows.slice(0, 2).map((row, i) => ({ row, name: wanted[i] })).filter((u) => u.row.name !== u.name);
  if (!updates.length) return;
  // Free the names first if another level already uses them (rename in two passes through temporary names).
  const r = await t.call("set_parameters", { rows: updates.map((u) => ({ id: String(u.row.id), values: { Name: u.name } })) });
  if (r.status !== "ok") t.log(`could not rename levels to Level 1/Level 2: ${r.text?.split("\n")[0]}`);
}

/**
 * After a scenario: close every document of this Revit year that lives in the scenario's folder (t.yearDir), without
 * saving, so one Revit session does not pile up dozens of open documents over a full run and, with --years 2024,2027,
 * no title stays open in the other Revit (same titles would be TARGET_AMBIGUOUS). Docs are addressed by the session's
 * #n. Best effort and outside coverage/hints: errors are ignored, the only open doc stays.
 */
export async function closeScenarioDocs(t, makeCaller) {
  if (!t.session || !t.year) return;
  const call = makeCaller({ session: t.session, year: t.year });
  let docs = [];
  try {
    const status = await call("status", { detail: "full" }, { allowError: true, timeoutMs: 30_000 });
    docs = (status.json?.docsFull ?? []).filter((d) => d.path && isUnder(d.path, t.yearDir) && (status.json?.docs?.rows ?? []).some((row) => row[0] === d.n && row[3] === t.year));
  } catch {
    return;
  }
  if (docs.length === 0) return;
  // Revit keeps at least one document open: when every open doc of this year would close, open a parking project
  // first (outside the scenario folders, unique per year), so no scenario title stays open next to the other year's.
  try {
    const status = await call("status", {}, { allowError: true, timeoutMs: 30_000 });
    const open = (status.json?.docs?.rows ?? []).filter((row) => row[3] === t.year).length;
    if (open <= docs.length) {
      const template = projectTemplate(t.year);
      const park = join(t.runRoot, "park", String(t.year), `E2E-Park-${t.year}.rvt`);
      mkdirSync(join(t.runRoot, "park", String(t.year)), { recursive: true });
      if (template) await call("manage_document", existsSync(park) ? { op: "open", path: park, activate: true, doc: `@${t.year}` } : { op: "new_project", path: park, template, activate: true, doc: `@${t.year}` }, { allowError: true, timeoutMs: 120_000 });
    }
  } catch {
    // best effort
  }
  for (const d of docs) {
    try {
      let r = await call("manage_document", { op: "close", doc: `#${d.n}`, save: false }, { allowError: true, timeoutMs: 60_000 });
      if (r.status === "not_applied" && r.json?.confirm) r = await call("manage_document", { op: "close", confirm: r.json.confirm }, { allowError: true, timeoutMs: 60_000 });
      for (let i = 0; i < 4 && r.status === "running"; i++) {
        const ref = r.json?.job ?? r.json?.ref;
        if (!ref) break;
        r = await call("job_status", { id: ref, wait: 30 }, { allowError: true, timeoutMs: 60_000 });
      }
    } catch {
      // Revit gone or the doc already closed: nothing to clean up.
    }
  }
}
