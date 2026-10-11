// R6 idle (SPEC §8.2, §17 V18; D4 R6, D2 E1). Owner P-REL-ADDIN.
// The wake pump (WM_NULL after Raise + 200 ms watchdog + Idling fallback) must service the queue promptly while Revit is
// in the background, minimized, or idle for a long time. dev.ping (raw pipe) measures round trip and raiseToExecMs.
// V18 A/B: the same pings with wake.wmNull off are recorded (not asserted) and the setting is restored.
// Env: E2E_R6_IDLE_S (default 60) seconds of idling before the "long idle" probe.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { settingsFile } from "@revit-mcp-next/contracts/home";
import { health, rawAddin, requireTestOps, sleep } from "./R5-busy.mjs";

function percentile(values, p) {
  const sorted = [...values].sort((a, b) => a - b);
  if (sorted.length === 0) return 0;
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(p * sorted.length) - 1))];
}

async function pings(addin, n, gapMs = 150) {
  const rt = [];
  const r2e = [];
  for (let i = 0; i < n; i++) {
    const t0 = Date.now();
    const r = await addin.call("dev.ping", {}, { timeoutMs: 10_000 });
    if (!r.ok) throw new Error(`dev.ping failed: ${r.code} ${r.message}`);
    rt.push(Date.now() - t0);
    r2e.push(r.data?.raiseToExecMs ?? r.metrics?.raiseToExecMs ?? 0);
    await sleep(gapMs);
  }
  return { n, p50: percentile(rt, 0.5), p95: percentile(rt, 0.95), max: Math.max(...rt), raiseP95: percentile(r2e, 0.95) };
}

/** Merges a patch into <home>\config\settings.json; returns a restore function (the add-in hot-reloads in ~500 ms). */
function patchSettings(home, patch) {
  const file = settingsFile(home);
  const before = existsSync(file) ? readFileSync(file, "utf8") : null;
  let current = {};
  try {
    current = before ? JSON.parse(before) : {};
  } catch {
    current = {};
  }
  const merged = { schemaVersion: 1, ...current };
  for (const [k, v] of Object.entries(patch)) merged[k] = typeof v === "object" && v && !Array.isArray(v) ? { ...(merged[k] ?? {}), ...v } : v;
  writeFileSync(file, JSON.stringify(merged, null, 2));
  return () => {
    if (before === null) writeFileSync(file, JSON.stringify({ schemaVersion: 1 }, null, 2));
    else writeFileSync(file, before);
  };
}

export default {
  id: "R6",
  title: "Idle: the pump services calls promptly in the background, minimized and after a long idle",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const addin = rawAddin(t);
    await requireTestOps(t, addin);
    const idleS = Number(process.env.E2E_R6_IDLE_S ?? 60);
    const before = await health(addin);

    const normal = await t.step("dev.ping as Revit runs (p95 ≤ 1 s)", async () => {
      const s = await pings(addin, 20);
      t.assertLe(s.p95, 1_000, "round trip p95 ms");
      return s;
    });
    t.log(`normal: ${JSON.stringify(normal)}`);

    let minimized;
    try {
      minimized = await t.step("minimized Revit: dev.ping p95 ≤ 1.5 s and WM_NULL posts grow", async () => {
        const w = await addin.call("test.window", { state: "minimize" });
        t.assert(w.ok, `test.window minimize: ${w.code ?? ""}`);
        await sleep(2_000);
        const s = await pings(addin, 30);
        t.assertLe(s.p95, 1_500, "minimized round trip p95 ms");
        const h = await health(addin);
        t.assert((h?.pump?.wmNullPosts ?? 0) > (before?.pump?.wmNullPosts ?? 0), "health.pump.wmNullPosts increased");
        t.assert((h?.pump?.n ?? 0) > (before?.pump?.n ?? 0), "health.pump.n counts raise-to-exec samples");
        return s;
      });
      t.log(`minimized (wmNull on): ${JSON.stringify(minimized)}`);

      await t.step("V18 A/B: wake.wmNull off (recorded, not asserted)", async () => {
        const restore = patchSettings(t.home, { wake: { wmNull: false } });
        try {
          await sleep(1_500);
          const off = await pings(addin, 30);
          t.log(`V18 minimized wmNull off: ${JSON.stringify(off)} vs on: ${JSON.stringify(minimized)}`);
        } finally {
          restore();
          await sleep(1_500);
        }
      });
    } finally {
      await addin.call("test.window", { state: "restore" }).catch(() => undefined);
      await sleep(1_000);
    }

    await t.step(`after ${idleS} s idle, MCP calls answer within 3 s`, async () => {
      await sleep(idleS * 1_000);
      for (const [tool, args] of [["status", {}], ["find_elements", { category: ["Walls"], count_only: true }]]) {
        const t0 = Date.now();
        const r = await t.call(tool, args, { allowError: true });
        const ms = Date.now() - t0;
        t.assert(r.status !== "error" || r.code === "NO_OPEN_DOCUMENT", `${tool} after idle: ${r.status} ${r.code ?? ""}`);
        t.assertLe(ms, 3_000, `${tool} after idle ms`);
      }
      const s = await pings(addin, 5);
      t.assertLe(s.p95, 1_000, "dev.ping after idle p95 ms");
    });

    await t.step("pump health is reported (p50/p90/p99, raiseResults, execViaIdling, recreates)", async () => {
      const h = await health(addin);
      for (const k of ["p50", "p90", "p99", "max", "n", "over1s", "wmNullPosts", "execViaIdling", "recreates"]) t.assert(typeof h?.pump?.[k] === "number", `pump.${k}`);
      t.assert(typeof h.pump.raiseResults?.accepted === "number", "pump.raiseResults.accepted");
      t.assert(typeof h.lastIdlingAtUtc === "string", "lastIdlingAtUtc");
      t.log(`pump: ${JSON.stringify(h.pump)}`);
    });
  },
};
