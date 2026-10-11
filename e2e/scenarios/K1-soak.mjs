// K1 soak (SPEC §13.5 profile soak, D4 §8.11; owner W3-E2E). Needs Revit with the add-in.
// A mixed loop for E2E_SOAK_MIN minutes (default 30) per Revit year on one disposable project: reads (list, find,
// describe, status), a write (wall), a transform (move), undo of that move, a capture and get_changes, with
// E2E_SOAK_IDLE_S seconds (default 20) of idle every 10 rounds. Asserts: no unexpected error, p95 latency per key stays
// under the budget (reads 3 s, writes 8 s, capture 15 s, undo 10 s), and Revit's working set grows by less than
// E2E_SOAK_MAX_GROWTH_MB (default 1500 MB) from the first to the last round.

import { spawnSync } from "node:child_process";

const createdIds = (r) => (Array.isArray(r.json?.created) ? r.json.created.map(String) : []);
const BUDGET_MS = { read: 3_000, write: 8_000, capture: 15_000, undo: 10_000 };

function workingSetMb(pid) {
  const out = spawnSync("powershell.exe", ["-NoProfile", "-Command", `(Get-Process -Id ${Number(pid)} -ErrorAction SilentlyContinue).WorkingSet64`], { encoding: "utf8", windowsHide: true, timeout: 20_000 });
  const bytes = Number((out.stdout ?? "").trim());
  return Number.isFinite(bytes) && bytes > 0 ? Math.round(bytes / 2 ** 20) : null;
}

function p95(values) {
  const s = [...values].sort((a, b) => a - b);
  return s.length ? s[Math.min(s.length - 1, Math.ceil(0.95 * s.length) - 1)] : 0;
}

export default {
  id: "K1",
  title: "Soak: mixed read/write/capture/undo loop, latency p95 and Revit memory",
  profiles: ["soak"],
  years: "each",
  async run(t) {
    const minutes = Number(process.env.E2E_SOAK_MIN ?? 30) || 30;
    const idleS = Number(process.env.E2E_SOAK_IDLE_S ?? 20) || 0;
    const maxGrowth = Number(process.env.E2E_SOAK_MAX_GROWTH_MB ?? 1500) || 1500;
    const { path } = await t.newProject("K1");
    t.setTargetPath(path);
    const levels = await t.call("list", { kind: "levels" });
    const L1 = levels.json.rows[0][levels.json.cols.indexOf("name")];
    const times = {};
    const time = async (kind, key, fn) => {
      const t0 = Date.now();
      const r = await fn();
      (times[`${kind}:${key}`] ??= []).push(Date.now() - t0);
      return r;
    };
    const memStart = workingSetMb(t.revit.pid);
    const until = Date.now() + minutes * 60_000;
    let round = 0;
    await t.step(`loop for ${minutes} min`, async () => {
      while (Date.now() < until) {
        round += 1;
        await time("read", "status", () => t.call("status", {}));
        await time("read", "list.views", () => t.call("list", { kind: "views", limit: 50 }));
        const wall = await time("write", "create_elements.wall", () => t.call("create_elements", { op: "wall", level: L1, points: [[0, round * 1000], [4000, round * 1000]], height: 3000 }));
        const id = createdIds(wall)[0];
        await time("read", "find_elements", () => t.call("find_elements", { category: ["Walls"], limit: 20 }));
        await time("read", "describe_elements", () => t.call("describe_elements", { ids: [id], include: ["geometry"] }));
        await time("write", "modify_elements.move", () => t.call("modify_elements", { op: "move", ids: [id], by: [0, 100] }));
        await time("undo", "undo", () => t.call("undo", {}));
        if (round % 3 === 0) await time("capture", "capture", () => t.call("capture", { size: "small" }));
        await time("read", "get_changes", () => t.call("get_changes", { limit: 20 }));
        if (idleS > 0 && round % 10 === 0) await new Promise((r) => setTimeout(r, idleS * 1000));
      }
    });
    const memEnd = workingSetMb(t.revit.pid);
    const report = Object.fromEntries(Object.entries(times).map(([k, v]) => [k, { n: v.length, p95: p95(v) }]));
    t.log(`${round} rounds; Revit working set ${memStart} MB → ${memEnd} MB; p95 ${JSON.stringify(report)}`);
    await t.step("p95 latency inside the budgets", () => {
      const slow = Object.entries(report).filter(([k, v]) => v.p95 > BUDGET_MS[k.split(":")[0]]);
      t.assert(slow.length === 0, `slow keys: ${slow.map(([k, v]) => `${k} p95 ${v.p95} ms`).join(", ")}`);
    });
    await t.step("Revit memory growth bounded", () => {
      if (memStart === null || memEnd === null) {
        t.log("working set not readable; memory check skipped");
        return;
      }
      t.assertLe(memEnd - memStart, maxGrowth, "working set growth MB");
    });
  },
};
