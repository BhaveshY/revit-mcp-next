// Fixtures (SPEC §13.1-13.2, D4 §8.5): template discovery, run folders <home>\runs\<runId>\<year>\, disposable
// projects created through manage_document new_project, and the write guard (every write targets a doc under the
// run folder; nothing outside the e2e home is ever written).

import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { join, relative, resolve, isAbsolute } from "node:path";
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
export async function newProject(t, name) {
  const template = projectTemplate(t.year);
  if (!template) throw new AssertionFailure(`no Revit ${t.year} project template found under ProgramData\\Autodesk\\RVT ${t.year}\\Templates`);
  const path = join(t.yearDir, `${name}.rvt`);
  const r = await t.call("manage_document", { op: "new_project", path, template, activate: true }, { timeoutMs: 120_000 });
  t.docs.set(name, path);
  return { r, path, template };
}
