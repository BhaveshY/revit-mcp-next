// R10 registry junk (SPEC §8.7, D2 §2.4/E12; D4 R10). Owner P-REL-ADDIN.
// Junk in <home>\instances (a stale registration of a dead pid without a lock, an orphan lock, corrupt JSON, temp files,
// foreign names) never confuses discovery: status lists only the live Revit and calls keep working. When the live
// registration file is deleted, the add-in rewrites it within its 30 s heartbeat while the broker keeps serving.

import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { instancesDir } from "@revit-mcp-next/contracts/home";
import { health, rawAddin, registration, sleep } from "./R5-busy.mjs";

/** A pid that is certainly not running (Windows pids are multiples of 4 below 2^32). */
const DEAD_PID = 4_294_967_291;

export default {
  id: "R10",
  title: "Registry junk: stale, corrupt and orphan instance files never break discovery; deleted registration heals",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const addin = rawAddin(t);
    const dir = instancesDir(t.home);
    mkdirSync(dir, { recursive: true });
    const live = registration(t.home, t.year, t.revit.pid);
    const junk = [];
    const put = (name, body) => {
      const file = join(dir, name);
      writeFileSync(file, body);
      junk.push(file);
    };

    try {
      await t.step("write junk into the instances folder", async () => {
        const stale = { ...(live.snapshot ?? {}), instanceId: `r${t.year}-${DEAD_PID}-dead01`, pid: DEAD_PID, state: "ready", pipe: `revit-mcp-next-r${t.year}-${DEAD_PID}-dead01`, controlPipe: `revit-mcp-next-r${t.year}-${DEAD_PID}-dead01-control`, writtenAtUtc: new Date().toISOString() };
        put(`r${t.year}-${DEAD_PID}-dead01.json`, JSON.stringify(stale));
        put(`r${t.year}-${DEAD_PID - 4}-dead02.lock`, String(DEAD_PID - 4));
        put(`r${t.year}-${DEAD_PID - 8}-dead03.json`, "{ this is not json");
        put(`r${t.year}-${DEAD_PID - 12}-dead04.json.tmp`, "{}");
        put(`r${t.year}-${DEAD_PID - 16}-dead05.json`, "");
        put("notes.txt", "not an instance");
        put("r2099-1-zzzzzz.json", JSON.stringify({ schemaVersion: 99 }));
      });

      await t.step("status lists the live Revit only, without errors", async () => {
        const r = await t.call("status", { detail: "full" });
        t.assertEq(r.status, "ok", "status");
        const rows = Array.isArray(r.json?.revit) ? r.json.revit : [];
        t.assert(rows.some((x) => x[1] === t.revit.pid), "the live Revit is listed");
        t.assert(!rows.some((x) => x[1] >= DEAD_PID - 16), `no junk instance listed: ${JSON.stringify(rows)}`);
      });

      await t.step("calls still reach the live Revit", async () => {
        const r = await t.call("status", { instance: String(t.revit.pid) });
        t.assertEq(r.status, "ok", "status for the live pid");
        const ping = await addin.call("dev.ping", {}, { timeoutMs: 10_000 });
        t.assert(ping.ok, `dev.ping: ${ping.code ?? ""}`);
      });

      await t.step("a deleted live registration is rewritten within 35 s; status keeps working meanwhile", async () => {
        rmSync(live.file, { force: true });
        const r = await t.call("status", { instance: String(t.revit.pid) }, { allowError: true });
        t.assert(r.status === "ok" || r.code === "REVIT_STARTING", `status right after the delete: ${r.status} ${r.code ?? ""}`);
        const deadline = Date.now() + 35_000;
        while (!existsSync(live.file) && Date.now() < deadline) await sleep(500);
        t.assert(existsSync(live.file), `the add-in rewrote ${live.file}`);
        const again = await t.call("status", { instance: String(t.revit.pid) });
        t.assertEq(again.status, "ok", "status after the rewrite");
      });

      await t.step("the add-in still holds its liveness lock and serves health", async () => {
        t.assert(existsSync(join(dir, `${live.instanceId}.lock`)), "the .lock file exists (held by Revit)");
        const h = await health(addin);
        t.assert(h && typeof h.listeners?.primaryWaiting === "number", "health.listeners");
        t.assert(h.listeners.primaryWaiting + h.listeners.primaryActive >= 1, "primary pipe instances are listening");
      });
    } finally {
      for (const file of junk) rmSync(file, { force: true });
    }
  },
};
