// `revit-mcp-cli settings merge` (SPEC §3.1, §12.1): merges <home>\config\settings.json against the single schema.
// Known keys keep the user's (validated, clamped) values, unknown or invalid keys are dropped and reported, and
// enableCodeExecution changes only with --enable-code-execution / --disable-code-execution (never by an agent).

import { existsSync } from "node:fs";
import { defaultSettings, settingsFile, validateSettings, type Settings } from "@revit-mcp-next/contracts/home";
import { backupFile, flagBool, flagString, readText, timestamp, writeTextAtomic, type ParsedArgs } from "./common.js";

type Json = Record<string, unknown>;

const isObject = (v: unknown): v is Json => v !== null && typeof v === "object" && !Array.isArray(v);
/** Map-valued settings: copied whole (after validation) instead of key by key. */
const MAP_KEYS = new Set(["dialogPolicy.extra", "dialogPolicy.deny", "defaultTemplate"]);

/** Keep exactly the paths present in `raw` that the schema knows, with the validated values. */
function project(raw: Json, validated: Json, defaults: Json, prefix = ""): { out: Json; dropped: string[] } {
  const out: Json = {};
  const dropped: string[] = [];
  for (const [key, value] of Object.entries(raw)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (!prefix && (key.startsWith("$") || key.startsWith("_"))) {
      out[key] = value;
      continue;
    }
    if (!(key in defaults)) {
      dropped.push(path);
      continue;
    }
    if (MAP_KEYS.has(path) || !isObject(defaults[key])) {
      out[key] = validated[key];
      continue;
    }
    if (!isObject(value)) {
      dropped.push(path);
      continue;
    }
    const inner = project(value, validated[key] as Json, defaults[key] as Json, path);
    out[key] = inner.out;
    dropped.push(...inner.dropped);
  }
  return { out, dropped };
}

export interface SettingsMergeResult {
  file: string;
  existed: boolean;
  changed: boolean;
  backup: string | null;
  issues: string[];
  dropped: string[];
  enableCodeExecution: boolean;
  invalidJson: boolean;
  text: string;
}

export function mergeSettings(home: string, o: { enable?: boolean; disable?: boolean; dryRun?: boolean; stamp?: string } = {}): SettingsMergeResult {
  const file = settingsFile(home);
  const before = readText(file);
  const existed = before !== null && existsSync(file);
  let raw: Json = {};
  let invalidJson = false;
  if (before && before.trim()) {
    try {
      const parsed = JSON.parse(before);
      if (isObject(parsed)) raw = parsed;
      else invalidJson = true;
    } catch {
      invalidJson = true;
    }
  }
  const { settings, issues } = validateSettings(raw);
  const { out, dropped } = project(raw, settings as unknown as Json, defaultSettings() as unknown as Json);
  const current = typeof raw.enableCodeExecution === "boolean" ? raw.enableCodeExecution : false;
  const enable = o.enable ? true : o.disable ? false : current;
  const merged: Json = { schemaVersion: (settings as Settings).schemaVersion, ...out, enableCodeExecution: enable };
  merged.schemaVersion = 1;
  // Lead override: run_csharp is off by default and the per-session consent prompt defaults to false.
  if (!isObject(merged.codeExecution)) merged.codeExecution = {};
  if ((merged.codeExecution as Json).consentPrompt === undefined) (merged.codeExecution as Json).consentPrompt = false;
  const text = JSON.stringify(merged, null, 2) + "\n";
  const changed = text !== before;
  let backup: string | null = null;
  if (changed && !o.dryRun) {
    if (existed) backup = backupFile(file, o.stamp ?? timestamp());
    writeTextAtomic(file, text);
  }
  if (invalidJson) issues.unshift("settings.json was not valid JSON; rewritten from defaults (the old file is in the backup)");
  return { file, existed, changed, backup, issues, dropped, enableCodeExecution: enable, invalidJson, text };
}

export function settingsCommand(args: ParsedArgs, home: string): number {
  const sub = args._[1] ?? "merge";
  if (sub !== "merge" && sub !== "show") {
    process.stderr.write("usage: revit-mcp-cli settings merge|show [--home <dir>] [--enable-code-execution|--disable-code-execution] [--dry-run] [--json]\n");
    return 2;
  }
  const result = mergeSettings(home, {
    enable: flagBool(args, "enable-code-execution"),
    disable: flagBool(args, "disable-code-execution"),
    dryRun: sub === "show" || flagBool(args, "dry-run"),
    stamp: flagString(args, "stamp"),
  });
  if (flagBool(args, "json")) process.stdout.write(JSON.stringify({ ...result, text: undefined }, null, 2) + "\n");
  else {
    process.stdout.write(`${result.file}: ${result.changed ? (sub === "show" || flagBool(args, "dry-run") ? "would change" : "written") : "unchanged"}; enableCodeExecution=${result.enableCodeExecution}\n`);
    for (const issue of result.issues) process.stdout.write(`  issue: ${issue}\n`);
    for (const d of result.dropped) process.stdout.write(`  dropped unknown key: ${d}\n`);
  }
  return 0;
}
