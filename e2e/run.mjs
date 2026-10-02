#!/usr/bin/env node
// e2e runner (SPEC §13, D4 §8). Real Revit only; no unit tests, no doubles.
//
//   node e2e/run.mjs --profile surface                      S00 only (no Revit; hosted CI)
//   node e2e/run.mjs --years 2024,2027 --launch --only S00,S01
//
// Flags:
//   --home <dir>        e2e home (default %USERPROFILE%\.revit-mcp-next-e2e); Revit is launched with REVIT_MCP_NEXT_HOME=<dir>
//   --years 2024,2027   Revit years for Revit scenarios (default: 2024 if installed, else 2027)
//   --launch            dev-install the build into the home and launch Revit (WMI, outside any app job) when not running
//   --keep-revit        leave harness-launched Revit running (warm) at the end
//   --restart           kill the harness-launched Revit of this home first (after an add-in rebuild)
//   --build             npm run build first, and scripts/build-addin.ps1 per year with --launch
//   --only S00,S01      run these scenario ids
//   --profile surface|lane|quick|full   select scenarios by profile (default: surface, or lane with --only)
//   --broker <path>     broker entry (default broker/dist/src/index.js)
//   --json <path>       also write the summary JSON there
//   --verbose           print every call

import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { userProfileDir } from "@revit-mcp-next/contracts/home";
import * as A from "./lib/assert.mjs";
import { Coverage, makeCaller } from "./lib/call.mjs";
import { makeWriteGuard, newProject, newRunId, projectTemplate, runDir, runRoot } from "./lib/fixtures.mjs";
import { connectBroker, DEFAULT_BROKER, REPO } from "./lib/mcp.mjs";
import { buildAddin, devInstall, findRevitExe, installedYears, killOwned, launchRevit, LicensingBlocked, ownedRevit, registrationFor, waitReady } from "./lib/revit.mjs";
import { acquireSlot, bindSlot, findSlot, releaseSlot } from "./lib/slots.mjs";

function parseArgs(argv) {
  const flags = { only: null, years: null, profile: null, home: null, launch: false, keepRevit: false, restart: false, build: false, broker: null, json: null, verbose: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case "--only": flags.only = next().split(",").map((s) => s.trim().toUpperCase()).filter(Boolean); break;
      case "--years": flags.years = next().split(",").map((s) => Number(s.trim())).filter(Boolean); break;
      case "--profile": flags.profile = next(); break;
      case "--home": flags.home = next(); break;
      case "--launch": flags.launch = true; break;
      case "--keep-revit": flags.keepRevit = true; break;
      case "--restart": flags.restart = true; break;
      case "--build": flags.build = true; break;
      case "--broker": flags.broker = next(); break;
      case "--json": flags.json = next(); break;
      case "--verbose": flags.verbose = true; break;
      case "--help": case "-h": flags.help = true; break;
      default: throw new Error(`unknown flag ${a} (see the header of e2e/run.mjs)`);
    }
  }
  return flags;
}

function gitSha() {
  const out = spawnSync("git", ["rev-parse", "--short", "HEAD"], { cwd: REPO, encoding: "utf8", windowsHide: true });
  const dirty = spawnSync("git", ["status", "--porcelain"], { cwd: REPO, encoding: "utf8", windowsHide: true });
  return { sha: (out.stdout ?? "").trim(), dirty: (dirty.stdout ?? "").trim().length > 0 };
}

async function loadScenarios() {
  const dir = join(REPO, "e2e", "scenarios");
  const files = readdirSync(dir).filter((f) => /^[A-Z]\d+[-_].*\.mjs$/.test(f)).sort();
  const out = [];
  for (const file of files) {
    const mod = await import(pathToFileURL(join(dir, file)).href);
    const s = mod.default;
    if (!s || !s.id || typeof s.run !== "function") throw new Error(`${file}: default export must be {id, title, profiles, years, run(t)}`);
    out.push({ ...s, file });
  }
  return out;
}

const log = (line) => process.stdout.write(`${line}\n`);

async function main() {
  const flags = parseArgs(process.argv.slice(2));
  if (flags.help) {
    log("see the header of e2e/run.mjs and e2e/README.md");
    return 0;
  }
  const home = flags.home ?? join(userProfileDir(), ".revit-mcp-next-e2e");
  const profile = flags.profile ?? (flags.only ? "lane" : "surface");
  const broker = flags.broker ?? DEFAULT_BROKER;
  const runId = newRunId();
  const git = gitSha();
  const summary = { runId, profile, home, broker, startedAt: new Date().toISOString(), git, scenarios: [], failures: [], skipped: [], revit: [], coverage: [] };
  const coverage = new Coverage();

  if (flags.build) {
    log("building (npm run build)...");
    const b = spawnSync(process.platform === "win32" ? "npm.cmd" : "npm", ["run", "build"], { cwd: REPO, stdio: "inherit", shell: process.platform === "win32" });
    if (b.status !== 0) throw new Error("npm run build failed");
  }
  if (!existsSync(broker)) throw new Error(`broker not built: ${broker} (run npm run build)`);

  const all = await loadScenarios();
  let selected = flags.only ? all.filter((s) => flags.only.includes(s.id.toUpperCase())) : all.filter((s) => (s.profiles ?? []).includes(profile));
  if (flags.only) for (const id of flags.only) if (!all.some((s) => s.id.toUpperCase() === id)) summary.skipped.push({ id, reason: "no such scenario file" });
  if (selected.length === 0) throw new Error(`no scenarios selected (profile ${profile}${flags.only ? `, only ${flags.only.join(",")}` : ""})`);

  const needsRevit = selected.some((s) => s.years !== "none");
  const years = flags.years ?? (installedYears().length ? [installedYears()[0]] : [2024]);
  const revits = new Map(); // year -> {pid, launched, slot}
  const blocked = new Map(); // year -> reason

  if (needsRevit && flags.launch) {
    for (const year of years) if (!findRevitExe(year)) blocked.set(year, `Revit ${year} is not installed`);
    const buildable = years.filter((y) => !blocked.has(y));
    if (flags.build) for (const year of buildable) {
      log(`building the add-in for Revit ${year}...`);
      buildAddin({ repo: REPO, year });
    }
    if (buildable.length) {
      log(`dev-install into ${home} for ${buildable.join(",")}...`);
      try {
        devInstall({ repo: REPO, home, years: buildable });
      } catch (error) {
        for (const y of buildable) blocked.set(y, `dev-install failed: ${error.message}`);
      }
    }
  }

  const ensureRevit = async (year) => {
    if (revits.has(year)) return revits.get(year);
    if (blocked.has(year)) return null;
    let owned = ownedRevit(home, year);
    if (owned && flags.restart) {
      log(`restarting Revit ${year} (pid ${owned.pid})`);
      killOwned(owned.pid);
      owned = null;
    }
    if (owned && registrationFor(home, year, owned.pid)) {
      const entry = { pid: owned.pid, launched: false, slot: findSlot(home, year) };
      revits.set(year, entry);
      return entry;
    }
    if (!flags.launch) {
      blocked.set(year, `no harness Revit ${year} registered in ${home}; pass --launch`);
      return null;
    }
    const slot = acquireSlot(home, year);
    log(`launching Revit ${year} with REVIT_MCP_NEXT_HOME=${home} ...`);
    const launched = await launchRevit({ year, home });
    bindSlot(slot, launched.pid);
    const session = await connectBroker({ home, broker });
    try {
      const call = makeCaller({ session });
      const ready = await waitReady({ year, pid: launched.pid, home, startedAt: launched.startedAt, call, log: (m) => flags.verbose && log(m) });
      log(`Revit ${year} pid ${launched.pid} ready in ${Math.round(ready.readyMs / 1000)} s`);
      summary.revit.push({ year, pid: launched.pid, startupMs: ready.readyMs, launchedBy: "e2e" });
    } catch (error) {
      try {
        killOwned(launched.pid);
      } catch {
        // already gone
      }
      releaseSlot(slot);
      blocked.set(year, error instanceof LicensingBlocked ? `blocked: licensing (${error.message})` : error.message);
      return null;
    } finally {
      await session.close();
    }
    const entry = { pid: launched.pid, launched: true, slot };
    revits.set(year, entry);
    return entry;
  };

  for (const scenario of selected) {
    const runYears = scenario.years === "none" ? [null] : scenario.years === "one" ? [years[0]] : years;
    for (const year of runYears) {
      const label = `${scenario.id}${year ? ` (Revit ${year})` : ""}`;
      const started = Date.now();
      const record = { id: scenario.id, title: scenario.title, year, status: "pass", durationMs: 0, assertions: [], calls: [] };
      let session = null;
      try {
        let revit = null;
        if (year) {
          revit = await ensureRevit(year);
          if (!revit) {
            record.status = blocked.get(year)?.startsWith("blocked") ? "blocked" : "skipped";
            record.reason = blocked.get(year);
            summary.skipped.push({ id: scenario.id, year, reason: record.reason });
            log(`${record.status.toUpperCase()} ${label}: ${record.reason}`);
            summary.scenarios.push(record);
            continue;
          }
        }
        const root = runRoot(home, runId);
        const yearDir = year ? runDir(home, runId, year) : root;
        const t = {
          id: scenario.id,
          year,
          home,
          repo: REPO,
          broker,
          runId,
          runRoot: root,
          yearDir,
          revit,
          coverage,
          docs: new Map(),
          log: (m) => log(`  ${scenario.id}: ${m}`),
          ...A,
          projectTemplate: () => projectTemplate(year),
          async connect(o = {}) {
            return connectBroker({ home, broker, ...o });
          },
          async step(name, fn) {
            const t0 = Date.now();
            try {
              const value = await fn();
              record.assertions.push({ id: name, ok: true, ms: Date.now() - t0 });
              if (flags.verbose) log(`  ok   ${name}`);
              return value;
            } catch (error) {
              record.assertions.push({ id: name, ok: false, ms: Date.now() - t0, error: error.message, details: error.details });
              throw error;
            }
          },
        };
        if (year) {
          session = await connectBroker({ home, broker });
          t.session = session;
          let targetPath = null;
          t.setTargetPath = (p) => (targetPath = p);
          t.call = makeCaller({
            session,
            coverage,
            year,
            record: (c) => {
              record.calls.push(c);
              if (flags.verbose) log(`    ${c.tool}${c.op ? "." + c.op : ""} ${c.status}${c.code ? " " + c.code : ""} ${c.ms} ms ${c.bytes} B`);
            },
            writeGuard: makeWriteGuard(root, () => targetPath),
          });
          t.expectError = t.call.expectError;
          t.newProject = (name) => newProject(t, name);
        }
        await scenario.run(t);
        record.durationMs = Date.now() - started;
        log(`PASS ${label} (${Math.round(record.durationMs / 100) / 10} s, ${record.assertions.length} checks)`);
      } catch (error) {
        record.status = "fail";
        record.durationMs = Date.now() - started;
        record.error = error.message;
        record.details = error.details;
        summary.failures.push({ id: scenario.id, year, error: error.message, details: error.details });
        log(`FAIL ${label}: ${error.message}`);
        if (error.details) log(`     ${JSON.stringify(error.details).slice(0, 800)}`);
        if (flags.verbose && error.stack) log(error.stack);
      } finally {
        if (session) await session.close();
      }
      summary.scenarios.push(record);
    }
  }

  // Close what we launched (unless kept warm).
  for (const [year, r] of revits) {
    if (r.launched && !flags.keepRevit) {
      try {
        killOwned(r.pid);
        if (r.slot) releaseSlot(r.slot);
        log(`closed Revit ${year} (pid ${r.pid})`);
      } catch (error) {
        log(`could not close Revit ${year}: ${error.message}`);
      }
    }
  }

  summary.coverage = coverage.toJSON();
  summary.durationMs = Date.now() - Date.parse(summary.startedAt);
  const outDir = join(REPO, "artifacts", "e2e", runId);
  mkdirSync(outDir, { recursive: true });
  writeFileSync(join(outDir, "summary.json"), JSON.stringify(summary, null, 2));
  writeFileSync(join(REPO, "artifacts", "e2e", "latest.json"), JSON.stringify(summary, null, 2));
  if (flags.json) writeFileSync(flags.json, JSON.stringify(summary, null, 2));
  const passed = summary.scenarios.filter((s) => s.status === "pass").length;
  log(`\n${passed} passed, ${summary.failures.length} failed, ${summary.skipped.length} skipped/blocked; summary ${join(outDir, "summary.json")}`);
  return summary.failures.length > 0 ? 1 : 0;
}

main().then(
  (code) => {
    process.exitCode = code;
  },
  (error) => {
    process.stderr.write(`e2e: ${error.message}\n`);
    process.exitCode = 2;
  }
);
