// Run-level gates (SPEC §13.5, §15, D4 §8.10): the coverage gate over every Appendix B key plus the call modes, the
// weak-model hint gate (W1/W2 over every call of the run) and T1 output budgets (absolute caps from e2e/budgets.json and
// per-key p95 growth against e2e/t1-baseline.json). Gates are evaluated by run.mjs after all scenarios ran; which gates
// apply comes from the profile (e2e/profiles.json).

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { REGISTRY } from "@revit-mcp-next/contracts/catalog";
import { REPO } from "./mcp.mjs";

export const MODES = ["preview", "confirm", "detail:full", "page", "from:r#"];

const readJson = (path) => JSON.parse(readFileSync(path, "utf8").replace(/^﻿/, ""));

/** The call modes an args object exercises. */
export function modesOf(args) {
  const out = [];
  if (!args || typeof args !== "object") return out;
  if (args.preview === true) out.push("preview");
  if (typeof args.confirm === "string" && args.confirm) out.push("confirm");
  if (args.detail === "full") out.push("detail:full");
  if (typeof args.page === "string" && /^p\d+$/i.test(args.page)) out.push("page");
  if (typeof args.from === "string" && /^r\d+$/i.test(args.from.trim())) out.push("from:r#");
  return out;
}

// ------------------------------------------------------------------------------------------------ exemptions

/**
 * e2e/coverage-exemptions.json: {"exemptions":[{"key":"links.link_ifc"|"mode:page", "years":[2024]?, "reason", "expires":"YYYY-MM-DD"}]}.
 * An entry without years applies to every year. Expired entries no longer exempt anything and fail the gate.
 */
export function loadExemptions(path = join(REPO, "e2e", "coverage-exemptions.json")) {
  if (!existsSync(path)) return { entries: [], problems: [] };
  const raw = readJson(path);
  const entries = Array.isArray(raw.exemptions) ? raw.exemptions : [];
  const problems = [];
  const known = new Set([...REGISTRY.map((r) => r.key), ...MODES.map((m) => `mode:${m}`)]);
  for (const e of entries) {
    if (!e.key || !known.has(e.key)) problems.push(`exemption for unknown key ${JSON.stringify(e.key)}`);
    if (!e.reason || String(e.reason).trim().length < 10) problems.push(`exemption ${e.key}: a reason is required`);
    if (!/^\d{4}-\d{2}-\d{2}$/.test(String(e.expires ?? ""))) problems.push(`exemption ${e.key}: expires must be YYYY-MM-DD`);
  }
  return { entries, problems };
}

function exemptionFor(entries, key, year, today) {
  const e = entries.find((x) => x.key === key && (!Array.isArray(x.years) || x.years.length === 0 || x.years.includes(year)));
  if (!e) return null;
  return { ...e, expired: String(e.expires) < today };
}

// ------------------------------------------------------------------------------------------------ coverage gate

/**
 * @param {{ marks: Array<{tool:string, op:string|null, years:number[]}>, modes: Record<string, Record<number, string[]>>,
 *           yearsRun: number[], yearsBlocked: Array<{year:number, reason:string}>, exemptionsPath?: string, today?: string }} o
 */
export function coverageGate(o) {
  const today = o.today ?? new Date().toISOString().slice(0, 10);
  const { entries, problems } = loadExemptions(o.exemptionsPath);
  const covered = new Map();
  for (const m of o.marks) covered.set(m.op ? `${m.tool}.${m.op}` : m.tool, new Set(m.years));
  const table = [];
  const missing = [];
  const exempted = [];
  const expired = [];
  for (const r of REGISTRY) {
    const row = { key: r.key, min: r.min, years: {} };
    for (const year of o.yearsRun) {
      if (r.min > year) {
        row.years[year] = covered.get(r.key)?.has(year) ? "covered" : "n/a";
        continue;
      }
      if (covered.get(r.key)?.has(year)) {
        row.years[year] = "covered";
        continue;
      }
      const ex = exemptionFor(entries, r.key, year, today);
      if (ex && !ex.expired) {
        row.years[year] = "exempt";
        exempted.push({ key: r.key, year, reason: ex.reason, expires: ex.expires });
      } else {
        row.years[year] = ex ? "expired-exemption" : "missing";
        (ex ? expired : missing).push({ key: r.key, year, ...(ex ? { reason: ex.reason, expires: ex.expires } : {}) });
      }
    }
    table.push(row);
  }
  const modeRows = [];
  for (const mode of MODES) {
    const row = { mode, years: {} };
    for (const year of o.yearsRun) {
      const keys = o.modes?.[mode]?.[year] ?? [];
      if (keys.length > 0) row.years[year] = { status: "covered", keys: keys.slice(0, 12) };
      else {
        const ex = exemptionFor(entries, `mode:${mode}`, year, today);
        if (ex && !ex.expired) {
          row.years[year] = { status: "exempt" };
          exempted.push({ key: `mode:${mode}`, year, reason: ex.reason, expires: ex.expires });
        } else {
          row.years[year] = { status: ex ? "expired-exemption" : "missing" };
          (ex ? expired : missing).push({ key: `mode:${mode}`, year });
        }
      }
    }
    modeRows.push(row);
  }
  const notRun = (o.yearsBlocked ?? []).map((b) => `Revit ${b.year} did not run (${b.reason})`);
  const failures = [
    ...problems,
    ...notRun,
    ...(o.yearsRun.length === 0 ? ["no Revit year ran, so nothing is covered"] : []),
    ...missing.map((m) => `${m.key} not covered on ${m.year}`),
    ...expired.map((m) => `${m.key} on ${m.year}: exemption expired ${m.expires}`),
  ];
  const keysTotal = REGISTRY.length;
  const summaryPerYear = Object.fromEntries(
    o.yearsRun.map((y) => [y, { covered: table.filter((r) => r.years[y] === "covered").length, exempt: table.filter((r) => r.years[y] === "exempt").length, na: table.filter((r) => r.years[y] === "n/a").length, missing: table.filter((r) => r.years[y] === "missing" || r.years[y] === "expired-exemption").length, total: keysTotal }])
  );
  return { ok: failures.length === 0, failures, perYear: summaryPerYear, table, modes: modeRows, exempted, missing, expired };
}

// ------------------------------------------------------------------------------------------------ T1 output budgets

export function loadBudgets() {
  const path = join(REPO, "e2e", "budgets.json");
  const b = existsSync(path) ? readJson(path) : {};
  return {
    outputs: b.outputs ?? { statusCompactMaxBytes: 2048, pageMaxBytes: 8192, resultMaxBytes: 12000, resultFullMaxBytes: 40000, imageBase64MaxBytes: 1500000, readManyMaxImages: 2, readManyMaxImageBytes: 2097152 },
    t1: b.t1 ?? { p95GrowthMax: 0.2, pageSlackBytes: 600 },
  };
}

/** Absolute output-budget problems of one call result (lib/call.mjs shape). */
export function budgetProblems(r, budgets = loadBudgets()) {
  const out = [];
  const o = budgets.outputs;
  const full = r.args?.detail === "full";
  const cap = full ? o.resultFullMaxBytes : o.resultMaxBytes;
  if (r.bytes > cap) out.push(`${r.tool}${r.op ? "." + r.op : ""} text is ${r.bytes} B > ${cap} B${full ? " (detail:full)" : ""}`);
  if (r.tool === "status" && !full && r.bytes > o.statusCompactMaxBytes) out.push(`status (compact) is ${r.bytes} B > ${o.statusCompactMaxBytes} B`);
  if ((r.tool === "list" || r.tool === "find_elements") && !full && r.bytes > o.pageMaxBytes + budgets.t1.pageSlackBytes) out.push(`${r.tool}${r.op ? "." + r.op : ""} page is ${r.bytes} B > ${o.pageMaxBytes} B (+${budgets.t1.pageSlackBytes} B header slack)`);
  for (const image of r.images ?? []) if (image.base64Length > o.imageBase64MaxBytes) out.push(`${r.tool} image base64 is ${image.base64Length} B > ${o.imageBase64MaxBytes} B`);
  if (r.tool === "read_many") {
    if ((r.images ?? []).length > o.readManyMaxImages) out.push(`read_many returned ${r.images.length} images > ${o.readManyMaxImages}`);
    const total = (r.images ?? []).reduce((s, i) => s + i.base64Length, 0);
    if (total > o.readManyMaxImageBytes) out.push(`read_many images total ${total} B > ${o.readManyMaxImageBytes} B`);
  }
  return out;
}

function p(values, q) {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil(q * sorted.length) - 1)];
}

/** Per-key output statistics of a run: {key: {n, p50, p95, max, p95ms}} per year. */
export function outputStats(calls) {
  const byYear = {};
  for (const c of calls) {
    const year = c.year ?? "none";
    const key = c.op ? `${c.tool}.${c.op}` : c.tool;
    byYear[year] ??= {};
    (byYear[year][key] ??= { bytes: [], ms: [] }).bytes.push(c.bytes);
    byYear[year][key].ms.push(c.ms);
  }
  const out = {};
  for (const [year, keys] of Object.entries(byYear)) {
    out[year] = {};
    for (const [key, v] of Object.entries(keys)) out[year][key] = { n: v.bytes.length, p50: p(v.bytes, 0.5), p95: p(v.bytes, 0.95), max: Math.max(...v.bytes), p95ms: p(v.ms, 0.95) };
  }
  return out;
}

export const T1_BASELINE = join(REPO, "e2e", "t1-baseline.json");

/** T1 gate: absolute budget violations plus p95 growth over e2e/t1-baseline.json (when it exists for that year). */
export function t1Gate({ calls, violations, budgets = loadBudgets() }) {
  const stats = outputStats(calls);
  const growth = [];
  const baseline = existsSync(T1_BASELINE) ? readJson(T1_BASELINE) : null;
  if (baseline) {
    for (const [year, keys] of Object.entries(stats)) {
      const base = baseline.years?.[year];
      if (!base) continue;
      for (const [key, s] of Object.entries(keys)) {
        const b = base[key];
        if (!b || b.p95 <= 0 || s.n < 3) continue;
        if (s.p95 > b.p95 * (1 + budgets.t1.p95GrowthMax)) growth.push(`${key} on ${year}: p95 ${s.p95} B > baseline ${b.p95} B + ${Math.round(budgets.t1.p95GrowthMax * 100)} %`);
      }
    }
  }
  const failures = [...violations.map((v) => `${v.scenario}${v.year ? ` (${v.year})` : ""}: ${v.problem}`), ...growth];
  return { ok: failures.length === 0, failures, baseline: baseline ? T1_BASELINE : null, stats };
}

/** Write e2e/t1-baseline.json from a run's stats (--update-t1-baseline; commit it with the change that moved the numbers). */
export function writeT1Baseline(stats, runId) {
  const previous = existsSync(T1_BASELINE) ? readJson(T1_BASELINE) : { years: {} };
  const years = { ...(previous.years ?? {}) };
  for (const [year, keys] of Object.entries(stats)) {
    if (year === "none") continue;
    years[year] = Object.fromEntries(Object.entries(keys).map(([k, s]) => [k, { p95: s.p95, n: s.n }]));
  }
  writeFileSync(T1_BASELINE, JSON.stringify({ note: "T1 baseline: per-key p95 output bytes per Revit year (node e2e/run.mjs ... --update-t1-baseline).", runId, updatedAt: new Date().toISOString().slice(0, 10), years }, null, 2) + "\n");
  return T1_BASELINE;
}
