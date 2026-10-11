#!/usr/bin/env node
// e2e runner (SPEC §13, D4 §8). Real Revit only; no unit tests, no doubles.
//
//   node e2e/run.mjs --profile surface                                  no Revit (CI): S00 + offline W1-W4/T1
//   node e2e/run.mjs --years 2024,2027 --launch --profile full          the test-PC run (see e2e/TEST-PC.md)
//   node e2e/run.mjs --years 2024 --launch --only S20,S40               one lane's scenarios
//
// Flags:
//   --home <dir>        e2e home (default %USERPROFILE%\.revit-mcp-next-e2e); Revit is launched with REVIT_MCP_NEXT_HOME=<dir>.
//                       Non-default homes get config\e2e-test-ops.enable (test ops, SPEC §13.4).
//   --years 2024,2027   Revit years for Revit scenarios (default: the first installed of 2024, 2027)
//   --launch            dev-install the add-in into the home and launch Revit (WMI, outside any app job) when not running
//   --artifacts <dir>   install the add-in from this folder (an extracted release package or another artifacts\ tree)
//                       instead of the repo's artifacts\ (no .NET SDK needed on the test PC)
//   --keep-revit        leave harness-launched Revit running (warm) at the end
//   --restart           kill the harness-launched Revit of this home first (after an add-in rebuild or a --language change)
//   --language <code>   launch Revit with /language <code> (e.g. DEU for the S21 German-UI pass); use with --restart
//   --build             npm run build first, and scripts/build-addin.ps1 per year with --launch
//   --only S00,S01      run these scenario ids (profile "lane"; gates hints and t1 only)
//   --profile <name>    surface | quick | full | soak | codex | claude (e2e/profiles.json; default surface, or lane with --only)
//   --broker <path>     broker entry (default broker/dist/src/index.js)
//   --json <path>       also write the summary JSON there
//   --update-t1-baseline  write e2e/t1-baseline.json from this run's per-key p95 output sizes
//   --no-gates          report the gates but do not fail the run on them
//   --list              print the profiles and scenarios and exit
//   --verbose           print every call
//
// Output: artifacts/e2e/<runId>/summary.json (all years), summary-<year>.json per Revit year, and artifacts/e2e/latest.json.
// Exit code: 0 all passed and every gate held; 1 a scenario or gate failed; 2 the harness itself failed.

import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { arch, cpus, release, totalmem } from "node:os";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { isDefaultHome, userProfileDir } from "@revit-mcp-next/contracts/home";
import { REGISTRY } from "@revit-mcp-next/contracts/catalog";
import * as A from "./lib/assert.mjs";
import { Coverage, makeCaller, setRunHooks } from "./lib/call.mjs";
import { makeWriteGuard, newProject, newRunId, projectTemplate, runDir, runRoot } from "./lib/fixtures.mjs";
import { budgetProblems, coverageGate, loadBudgets, t1Gate, writeT1Baseline } from "./lib/gates.mjs";
import { hintProblems } from "./lib/hints.mjs";
import { connectBroker, DEFAULT_BROKER, REPO } from "./lib/mcp.mjs";
import {
  buildAddin,
  devInstall,
  findArtifactsRoot,
  findRevitExe,
  installedYears,
  killOwned,
  launchRevit,
  LicensingBlocked,
  ownedRevit,
  registrationFor,
  revitVersion,
  setDefaultLaunchArgs,
  waitReady,
} from "./lib/revit.mjs";
import { acquireSlot, bindSlot, findSlot, releaseSlot } from "./lib/slots.mjs";

function parseArgs(argv) {
  const flags = { only: null, years: null, profile: null, home: null, launch: false, keepRevit: false, restart: false, build: false, broker: null, json: null, verbose: false, language: null, artifacts: null, updateT1: false, noGates: false, list: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      const v = argv[++i];
      if (v === undefined) throw new Error(`${a} needs a value`);
      return v;
    };
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
      case "--language": flags.language = next().trim().toUpperCase(); break;
      case "--artifacts": flags.artifacts = resolve(next()); break;
      case "--update-t1-baseline": flags.updateT1 = true; break;
      case "--no-gates": flags.noGates = true; break;
      case "--list": flags.list = true; break;
      case "--verbose": flags.verbose = true; break;
      case "--help": case "-h": flags.help = true; break;
      default: throw new Error(`unknown flag ${a} (see the header of e2e/run.mjs)`);
    }
  }
  for (const y of flags.years ?? []) if (y !== 2024 && y !== 2027) throw new Error(`--years: ${y} is not supported (2024, 2027)`);
  return flags;
}

function gitSha() {
  const out = spawnSync("git", ["rev-parse", "--short", "HEAD"], { cwd: REPO, encoding: "utf8", windowsHide: true });
  const dirty = spawnSync("git", ["status", "--porcelain"], { cwd: REPO, encoding: "utf8", windowsHide: true });
  return { sha: (out.stdout ?? "").trim() || null, dirty: (dirty.stdout ?? "").trim().length > 0 };
}

async function loadScenarios() {
  const dir = join(REPO, "e2e", "scenarios");
  const files = readdirSync(dir).filter((f) => /^[A-Z]\d+[A-Z]?[-_].*\.mjs$/.test(f)).sort();
  const out = [];
  for (const file of files) {
    const mod = await import(pathToFileURL(join(dir, file)).href);
    const s = mod.default;
    if (!s || !s.id || typeof s.run !== "function") throw new Error(`${file}: default export must be {id, title, profiles, years, run(t)}`);
    out.push({ ...s, file });
  }
  return out;
}

function loadProfiles() {
  const path = join(REPO, "e2e", "profiles.json");
  return existsSync(path) ? JSON.parse(readFileSync(path, "utf8")).profiles ?? {} : {};
}

/** Scenarios of a profile in profiles.json order (unknown ids are reported). Profiles not in the file use scenario.profiles. */
function selectScenarios(all, profiles, profile, only) {
  if (only) return { selected: all.filter((s) => only.includes(s.id.toUpperCase())), missing: only.filter((id) => !all.some((s) => s.id.toUpperCase() === id)) };
  const def = profiles[profile];
  if (!def) return { selected: all.filter((s) => (s.profiles ?? []).includes(profile)), missing: [] };
  const byId = new Map(all.map((s) => [s.id.toUpperCase(), s]));
  const ids = def.scenarios ?? [];
  return { selected: ids.map((id) => byId.get(id.toUpperCase())).filter(Boolean), missing: ids.filter((id) => !byId.has(id.toUpperCase())) };
}

function payloadOf(home, year) {
  try {
    return JSON.parse(readFileSync(join(home, "addin", String(year), "current.json"), "utf8"));
  } catch {
    return null;
  }
}

const log = (line) => process.stdout.write(`${line}\n`);

async function main() {
  const flags = parseArgs(process.argv.slice(2));
  if (flags.help) {
    log("see the header of e2e/run.mjs, e2e/README.md and e2e/TEST-PC.md");
    return 0;
  }
  const profiles = loadProfiles();
  const all = await loadScenarios();
  if (flags.list) {
    for (const [name, p] of Object.entries(profiles)) log(`${name.padEnd(8)} ${p.scenarios.join(" ")}\n         ${p.description} gates: ${(p.gates ?? []).join(", ") || "none"}`);
    for (const s of all) log(`${s.id.padEnd(5)} ${String(s.years).padEnd(5)} ${s.title}`);
    return 0;
  }

  const home = resolve(flags.home ?? join(userProfileDir(), ".revit-mcp-next-e2e"));
  const profile = flags.only ? "lane" : flags.profile ?? "surface";
  const profileDef = profiles[profile] ?? null;
  if (!flags.only && !profileDef && !all.some((s) => (s.profiles ?? []).includes(profile))) throw new Error(`unknown profile ${profile} (${Object.keys(profiles).join(", ")})`);
  const gatesOn = new Set(flags.only ? ["hints", "t1"] : profileDef?.gates ?? ["hints", "t1"]);
  const broker = flags.broker ?? DEFAULT_BROKER;
  const runId = newRunId();
  const git = gitSha();
  const startedAt = new Date().toISOString();
  const summary = {
    runId,
    profile,
    home,
    broker,
    startedAt,
    git,
    language: flags.language,
    environment: { node: process.version, os: `${process.platform} ${release()} ${arch()}`, cpus: cpus().length, memoryGb: Math.round(totalmem() / 2 ** 30), revit: {}, addin: {} },
    years: [],
    scenarios: [],
    failures: [],
    skipped: [],
    revit: [],
    coverage: [],
    gates: {},
  };
  const coverage = new Coverage();
  const allCalls = [];
  const hintViolations = [];
  const budgetViolations = [];
  const budgets = loadBudgets();
  let current = { id: null, year: null, record: null };

  setRunHooks({
    record: (c) => {
      allCalls.push({ ...c, scenario: current.id });
      current.record?.calls.push(c);
    },
    observe: async (r, year) => {
      for (const v of await hintProblems(r)) hintViolations.push({ scenario: current.id, year, tool: r.tool, op: r.op, rule: v.rule, problem: v.problem });
      for (const problem of budgetProblems(r, budgets)) budgetViolations.push({ scenario: current.id, year, tool: r.tool, op: r.op, problem });
    },
  });

  if (flags.build) {
    log("building (npm run build)...");
    const b = spawnSync(process.platform === "win32" ? "npm.cmd" : "npm", ["run", "build"], { cwd: REPO, stdio: "inherit", shell: process.platform === "win32" });
    if (b.status !== 0) throw new Error("npm run build failed");
  }
  if (!existsSync(broker)) throw new Error(`broker not built: ${broker} (run npm ci && npm run build)`);

  const { selected, missing } = selectScenarios(all, profiles, profile, flags.only);
  for (const id of missing) summary.skipped.push({ id, reason: "no such scenario file" });
  if (selected.length === 0) throw new Error(`no scenarios selected (profile ${profile}${flags.only ? `, only ${flags.only.join(",")}` : ""}); see --list`);

  const needsRevit = selected.some((s) => s.years !== "none");
  const years = flags.years ?? (installedYears().length ? [installedYears()[0]] : [2024]);
  summary.years = needsRevit ? years : [];
  const revits = new Map(); // year -> {pid, launched, slot}
  const blocked = new Map(); // year -> reason

  // Isolated home: test ops need <home>\config\e2e-test-ops.enable before Revit starts (dev-install also writes it).
  if (needsRevit && !isDefaultHome(home)) {
    mkdirSync(join(home, "config"), { recursive: true });
    const enable = join(home, "config", "e2e-test-ops.enable");
    if (!existsSync(enable)) writeFileSync(enable, "enabled by e2e/run.mjs\r\n");
  }
  if (flags.language) setDefaultLaunchArgs(["/language", flags.language]);

  if (needsRevit && flags.launch) {
    for (const year of years) if (!findRevitExe(year)) blocked.set(year, `Revit ${year} is not installed`);
    const buildable = years.filter((y) => !blocked.has(y));
    if (flags.build && !flags.artifacts) for (const year of buildable) {
      log(`building the add-in for Revit ${year}...`);
      buildAddin({ repo: REPO, year });
    }
    if (buildable.length) {
      let artifactsRoot = null;
      if (flags.artifacts) {
        artifactsRoot = findArtifactsRoot(flags.artifacts, buildable);
        if (!artifactsRoot) for (const y of buildable) blocked.set(y, `--artifacts ${flags.artifacts}: no addin\\${y}\\RevitMcpNext.Addin.dll found (up to 3 levels deep)`);
      } else if (!findArtifactsRoot(join(REPO, "artifacts"), buildable)) {
        for (const y of buildable) blocked.set(y, `the add-in for Revit ${y} is not built: pass --build (needs the .NET 10 SDK) or --artifacts <extracted release package>`);
      }
      const ready = buildable.filter((y) => !blocked.has(y));
      if (ready.length) {
        log(`dev-install into ${home} for ${ready.join(",")}${artifactsRoot ? ` from ${artifactsRoot}` : ""}...`);
        try {
          devInstall({ repo: REPO, home, years: ready, artifactsRoot });
        } catch (error) {
          for (const y of ready) blocked.set(y, `dev-install failed: ${error.message}`);
        }
      }
    }
  }
  for (const year of summary.years) {
    summary.environment.revit[year] = revitVersion(year);
    summary.environment.addin[year] = payloadOf(home, year);
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
    let slot;
    try {
      slot = acquireSlot(home, year);
    } catch (error) {
      blocked.set(year, error.message);
      return null;
    }
    log(`launching Revit ${year}${flags.language ? ` (/language ${flags.language})` : ""} with REVIT_MCP_NEXT_HOME=${home} ...`);
    let launched;
    try {
      launched = await launchRevit({ year, home });
    } catch (error) {
      releaseSlot(slot);
      blocked.set(year, error.message);
      return null;
    }
    bindSlot(slot, launched.pid);
    const session = await connectBroker({ home, broker });
    try {
      const call = makeCaller({ session, observe: async () => {}, record: () => {} });
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
      const record = { id: scenario.id, title: scenario.title, year, status: "pass", durationMs: 0, assertions: [], calls: [], logs: [] };
      current = { id: scenario.id, year, record };
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
          years,
          home,
          repo: REPO,
          broker,
          runId,
          runRoot: root,
          yearDir,
          revit,
          coverage,
          profile,
          language: flags.language,
          docs: new Map(),
          log: (m) => {
            record.logs.push(String(m).slice(0, 2_000));
            log(`  ${scenario.id}: ${m}`);
          },
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
              allCalls.push({ ...c, scenario: scenario.id });
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
  current = { id: null, year: null, record: null };

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

  // ------------------------------------------------------------------------------------------------ gates
  const yearsRun = summary.years.filter((y) => summary.scenarios.some((s) => s.year === y && (s.status === "pass" || s.status === "fail")));
  const yearsBlocked = summary.years.filter((y) => !yearsRun.includes(y)).map((y) => ({ year: y, reason: blocked.get(y) ?? "no scenario ran" }));
  summary.coverage = coverage.toJSON();
  summary.modes = coverage.modesJSON();
  const gateResults = {};
  if (gatesOn.has("coverage")) gateResults.coverage = coverageGate({ marks: summary.coverage, modes: summary.modes, yearsRun, yearsBlocked });
  if (gatesOn.has("hints")) {
    const failures = hintViolations.map((v) => `${v.rule} ${v.scenario}${v.year ? ` (${v.year})` : ""} ${v.tool}${v.op ? "." + v.op : ""}: ${v.problem}`);
    gateResults.hints = { ok: failures.length === 0, failures: [...new Set(failures)], checkedCalls: allCalls.length };
  }
  if (gatesOn.has("t1")) {
    const t1 = t1Gate({ calls: allCalls, violations: budgetViolations, budgets });
    gateResults.t1 = { ok: t1.ok, failures: t1.failures, baseline: t1.baseline, stats: t1.stats };
    if (flags.updateT1) log(`wrote ${writeT1Baseline(t1.stats, runId)}`);
  }
  summary.gates = gateResults;
  const gateFailures = Object.entries(gateResults).filter(([, g]) => !g.ok);

  summary.finishedAt = new Date().toISOString();
  summary.durationMs = Date.now() - Date.parse(startedAt);
  summary.totals = {
    scenarios: summary.scenarios.length,
    passed: summary.scenarios.filter((s) => s.status === "pass").length,
    failed: summary.failures.length,
    skipped: summary.scenarios.filter((s) => s.status === "skipped" || s.status === "blocked").length,
    calls: allCalls.length,
    errors: allCalls.filter((c) => c.isError).length,
    registryKeys: REGISTRY.length,
  };
  summary.ok = summary.failures.length === 0 && (flags.noGates || gateFailures.length === 0);

  const outDir = join(REPO, "artifacts", "e2e", runId);
  mkdirSync(outDir, { recursive: true });
  const write = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2));
  write(join(outDir, "summary.json"), summary);
  for (const year of summary.years) {
    const mine = (text) => text.includes(`(${year})`) || text.includes(` ${year}`);
    const perYear = {
      ...summary,
      years: [year],
      scenarios: summary.scenarios.filter((s) => s.year === year || s.year === null),
      failures: summary.failures.filter((f) => f.year === year || f.year === null || f.year === undefined),
      skipped: summary.skipped.filter((s) => s.year === year || s.year === undefined),
      coverage: summary.coverage.filter((c) => c.years.includes(year)),
      gates: Object.fromEntries(
        Object.entries(gateResults).map(([k, g]) => {
          if (k === "coverage") return [k, { ok: g.ok, perYear: g.perYear[year] ?? null, failures: g.failures.filter(mine), table: g.table.map((r) => ({ key: r.key, status: r.years[year] ?? "not run" })), modes: g.modes.map((m) => ({ mode: m.mode, ...(m.years[year] ?? { status: "not run" }) })) }];
          if (k === "t1") return [k, { ok: g.ok, failures: g.failures.filter(mine), stats: g.stats[year] ?? null }];
          return [k, { ok: g.ok, failures: g.failures.filter(mine) }];
        })
      ),
    };
    write(join(outDir, `summary-${year}.json`), perYear);
  }
  mkdirSync(join(REPO, "artifacts", "e2e"), { recursive: true });
  write(join(REPO, "artifacts", "e2e", "latest.json"), summary);
  if (flags.json) write(flags.json, summary);

  // ------------------------------------------------------------------------------------------------ report
  for (const [name, g] of Object.entries(gateResults)) {
    if (name === "coverage") {
      const per = Object.entries(g.perYear).map(([y, c]) => `${y}: ${c.covered} covered, ${c.exempt} exempt, ${c.na} n/a, ${c.missing} missing of ${c.total}`).join("; ");
      log(`gate coverage: ${g.ok ? "ok" : "FAIL"} (${per || "no Revit year ran"})`);
    } else log(`gate ${name}: ${g.ok ? "ok" : "FAIL"}${name === "hints" ? ` (${g.checkedCalls} calls checked)` : ""}`);
    for (const f of g.failures.slice(0, 25)) log(`  - ${f}`);
    if (g.failures.length > 25) log(`  - +${g.failures.length - 25} more in summary.json`);
  }
  log(`\n${summary.totals.passed} passed, ${summary.failures.length} failed, ${summary.totals.skipped} skipped/blocked; gates ${gateFailures.length ? `FAILED: ${gateFailures.map(([n]) => n).join(", ")}` : "ok"}; summary ${join(outDir, "summary.json")}`);
  return summary.ok ? 0 : 1;
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
