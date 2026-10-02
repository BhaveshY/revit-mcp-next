#!/usr/bin/env node
// `npm run gen:catalog` writes artifacts/catalog/{catalog.json, tools-list.json, tools-list.default.json, tools-list.core.json}.
// `npm run gen:catalog -- --check` also runs the §15 gates and exits 1 on any failure.
// Flags: --check, --spec (spec parity with docs/design is fatal), --update-baseline (rewrite e2e/budgets.json), --quiet, --out <dir>.
//
// catalog.json = { version, hash, tools: ToolSpec[] (full), registry, errors, naming, protocol } (SPEC §4.1). The add-in
// embeds it as resource RevitMcpNext.catalog.json (scripts/build-addin.ps1).

import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { CODE_SYNONYMS, NOTICE_CODES, WARNING_CODES } from "../errors.js";
import { TEMP_PURPOSES, TX_NAME_PATTERN } from "../naming.js";
import {
  BRIDGE_PROTOCOL_VERSION,
  BROKER_PROTOCOL_RANGE,
  CONTROL_OPS,
  CONTROL_PIPE_INSTANCES,
  HANDSHAKE_TIMEOUT_MS,
  MAX_FRAME_BYTES,
  PRIMARY_PIPE_INSTANCES,
} from "../protocol.js";
import { type Budgets, deepEqual, firstDifference, runChecks, surfaceStats } from "./checks.js";
import { CATALOG, catalogHash, ERRORS, REGISTRY, toolsList } from "./index.js";
import { INSTRUCTIONS } from "./instructions.js";

export const CATALOG_FORMAT_VERSION = 1;

export interface CatalogJson {
  version: number;
  hash: string;
  tools: unknown[];
  registry: unknown[];
  errors: unknown[];
  naming: Record<string, unknown>;
  protocol: Record<string, unknown>;
  warnings: Record<string, string>;
  notices: Record<string, string>;
  codeSynonyms: Record<string, string>;
  instructions: string;
}

export function buildCatalogJson(): CatalogJson {
  return {
    version: CATALOG_FORMAT_VERSION,
    hash: catalogHash(),
    tools: [...CATALOG],
    registry: [...REGISTRY],
    errors: Object.values(ERRORS),
    naming: { pattern: TX_NAME_PATTERN.source, prefix: "MCP ", tempPurposes: [...TEMP_PURPOSES] },
    protocol: {
      version: BRIDGE_PROTOCOL_VERSION,
      min: BROKER_PROTOCOL_RANGE.min,
      max: BROKER_PROTOCOL_RANGE.max,
      maxFrameBytes: MAX_FRAME_BYTES,
      primaryInstances: PRIMARY_PIPE_INSTANCES,
      controlInstances: CONTROL_PIPE_INSTANCES,
      handshakeTimeoutMs: HANDSHAKE_TIMEOUT_MS,
      controlOps: [...CONTROL_OPS],
    },
    warnings: { ...WARNING_CODES },
    notices: { ...NOTICE_CODES },
    codeSynonyms: { ...CODE_SYNONYMS },
    instructions: INSTRUCTIONS,
  };
}

/** Repo root: contracts/dist/catalog/emit.js → ../../.. */
export function repoRoot(): string {
  return resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "..");
}

function writeAtomic(path: string, text: string): void {
  mkdirSync(dirname(path), { recursive: true });
  const tmp = `${path}.${process.pid}.tmp`;
  writeFileSync(tmp, text, "utf8");
  renameSync(tmp, path);
}

function readJson(path: string): unknown {
  return JSON.parse(readFileSync(path, "utf8").replace(/^﻿/, ""));
}

export function budgetsPath(root = repoRoot()): string {
  return join(root, "e2e", "budgets.json");
}

export function loadBudgets(root = repoRoot()): Budgets | null {
  const path = budgetsPath(root);
  if (!existsSync(path)) return null;
  return readJson(path) as Budgets;
}

function currentBudgets(): Budgets {
  const s = surfaceStats();
  return {
    toolsListBytes: { default: s.toolsListBytesDefault, withOptIn: s.toolsListBytesWithOptIn, core: s.toolsListBytesCore },
    perTool: s.perTool,
    updatedAt: new Date().toISOString().slice(0, 10),
    note: "Baseline for the §15 growth gate. Growth > 5 % needs an update of this file in the same commit (npm run gen:catalog -- --update-baseline).",
  };
}

function main(argv: string[]): number {
  const flags = new Set(argv.filter((a) => a.startsWith("--")));
  const outIndex = argv.indexOf("--out");
  const root = repoRoot();
  const outDir = outIndex >= 0 && argv[outIndex + 1] ? resolve(argv[outIndex + 1]!) : join(root, "artifacts", "catalog");
  const quiet = flags.has("--quiet");
  const log = (line: string) => {
    if (!quiet) process.stdout.write(line + "\n");
  };

  const catalog = buildCatalogJson();
  writeAtomic(join(outDir, "catalog.json"), JSON.stringify(catalog));
  writeAtomic(join(outDir, "tools-list.json"), JSON.stringify(toolsList({ includeOptIn: true })));
  writeAtomic(join(outDir, "tools-list.default.json"), JSON.stringify(toolsList({})));
  writeAtomic(join(outDir, "tools-list.core.json"), JSON.stringify(toolsList({ profile: "core" })));

  if (flags.has("--update-baseline") || !existsSync(budgetsPath(root))) {
    writeAtomic(budgetsPath(root), JSON.stringify(currentBudgets(), null, 2) + "\n");
    log(`wrote ${budgetsPath(root)}`);
  }

  const stats = surfaceStats();
  log(
    `catalog: ${CATALOG.length} tools (${stats.toolCount} default, ${stats.coreToolCount} core), ${REGISTRY.length} keys (${stats.addinBoundKeys} add-in bound); ` +
      `tools/list ${stats.toolsListBytesDefault} B default, ${stats.toolsListBytesWithOptIn} B with run_csharp, ${stats.toolsListBytesCore} B core; ` +
      `largest ${stats.largestTool[0]} ${stats.largestTool[1]} B; instructions head ${stats.instructionsHeadChars}, total ${stats.instructionsTotalChars}; hash ${catalog.hash.slice(0, 12)}`
  );
  log(`wrote ${outDir}`);

  if (!flags.has("--check") && !flags.has("--spec")) return 0;

  const result = runChecks(loadBudgets(root));
  for (const w of result.warnings) log(`warn: ${w}`);

  // Spec parity (W1 acceptance): advertised tools/list and registry equal docs/design.
  const parity: string[] = [];
  const specList = join(root, "docs", "design", "SPEC-tools-list.json");
  const specRegistry = join(root, "docs", "design", "SPEC-registry.json");
  if (existsSync(specList)) {
    const served = toolsList({ includeOptIn: true });
    const spec = readJson(specList);
    if (!deepEqual(served, spec)) parity.push(`tools-list.json differs from docs/design/SPEC-tools-list.json: ${firstDifference(served, spec)}`);
    else if (JSON.stringify(served) !== JSON.stringify(spec)) parity.push("tools-list.json deep-equals the spec but key order differs");
  }
  if (existsSync(specRegistry)) {
    const spec = readJson(specRegistry);
    if (!deepEqual(REGISTRY, spec)) parity.push(`registry differs from docs/design/SPEC-registry.json: ${firstDifference(REGISTRY, spec)}`);
  }
  if (parity.length === 0) log("spec parity: tools-list.json and the registry equal docs/design (byte-identical tools/list)");
  for (const p of parity) log(`${flags.has("--spec") ? "FAIL" : "warn"}: spec parity: ${p}`);

  for (const f of result.failures) process.stderr.write(`FAIL: ${f}\n`);
  const failed = result.failures.length > 0 || (flags.has("--spec") && parity.length > 0);
  log(failed ? `gen:catalog --check: ${result.failures.length} failure(s)` : "gen:catalog --check: all gates pass");
  return failed ? 1 : 0;
}

const invokedDirectly = process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (invokedDirectly) process.exitCode = main(process.argv.slice(2));
