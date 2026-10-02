// Machine-wide Revit slots (SPEC §13.2, lead override 6): at most RMN_E2E_SLOTS (default 3) harness-launched Revit
// processes. A slot is %USERPROFILE%\.revit-mcp-next-slots\slot-<n>.lock holding {revitPid, harnessPid, home, year};
// it is free when the file is missing or neither pid is alive. Warm instances (--keep-revit) keep their slot.

import { existsSync, mkdirSync, openSync, closeSync, readFileSync, renameSync, writeFileSync, writeSync } from "node:fs";
import { join } from "node:path";
import { userProfileDir } from "@revit-mcp-next/contracts/home";
import { processAlive } from "./revit.mjs";

export const SLOT_DIR = () => join(userProfileDir(), ".revit-mcp-next-slots");
export const slotCount = () => Math.max(1, Number(process.env.RMN_E2E_SLOTS ?? 3) || 3);

function read(path) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch {
    return null;
  }
}

function stale(record) {
  if (!record) return true;
  const revitAlive = record.revitPid ? processAlive(record.revitPid) : false;
  const harnessAlive = record.harnessPid ? processAlive(record.harnessPid) : false;
  return !revitAlive && !harnessAlive;
}

/** The slot already held for this home/year (warm instance), if any. */
export function findSlot(home, year) {
  const dir = SLOT_DIR();
  for (let n = 1; n <= slotCount(); n++) {
    const path = join(dir, `slot-${n}.lock`);
    const record = existsSync(path) ? read(path) : null;
    if (record && !stale(record) && record.home === home && record.year === year) return { n, path, record };
  }
  return null;
}

/** Take a free slot for (home, year). Throws when all slots are in use. */
export function acquireSlot(home, year) {
  const existing = findSlot(home, year);
  if (existing) return existing;
  const dir = SLOT_DIR();
  mkdirSync(dir, { recursive: true });
  for (let n = 1; n <= slotCount(); n++) {
    const path = join(dir, `slot-${n}.lock`);
    const record = { harnessPid: process.pid, revitPid: null, home, year, at: new Date().toISOString() };
    try {
      const fd = openSync(path, "wx");
      writeSync(fd, JSON.stringify(record));
      closeSync(fd);
      return { n, path, record };
    } catch (error) {
      if (error.code !== "EEXIST") throw error;
      if (stale(read(path))) {
        const tmp = `${path}.${process.pid}.tmp`;
        writeFileSync(tmp, JSON.stringify(record));
        renameSync(tmp, path);
        return { n, path, record };
      }
    }
  }
  throw new Error(`all ${slotCount()} e2e Revit slots are in use (see ${dir}); stop a harness Revit or raise RMN_E2E_SLOTS`);
}

/** Record the Revit pid in the slot (the slot then lives as long as that Revit). */
export function bindSlot(slot, revitPid) {
  slot.record = { ...slot.record, revitPid };
  writeFileSync(slot.path, JSON.stringify(slot.record));
}

/** Release a slot (its Revit was closed). */
export function releaseSlot(slot) {
  writeFileSync(slot.path, JSON.stringify({ released: new Date().toISOString() }));
}
