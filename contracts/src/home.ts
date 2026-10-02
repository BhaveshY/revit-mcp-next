// Runtime home layout (SPEC §3, §4.5) and the single settings schema (SPEC §3.1 + lead override 5).
// Mirrors addin/RevitMcpNext.Addin/Runtime/McpHome.cs and Runtime/Settings.cs. The installer, doctor and e2e use this module.

import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { dirname, isAbsolute, join, resolve, sep } from "node:path";

// ---------------------------------------------------------------------------------------------
// Resolution: env REVIT_MCP_NEXT_HOME ?? <self-located home> ?? %USERPROFILE%\.revit-mcp-next
// ---------------------------------------------------------------------------------------------

export const HOME_ENV = "REVIT_MCP_NEXT_HOME";
export const HOME_MARKER = ".revit-mcp-next-home";
export const DEFAULT_HOME_DIRNAME = ".revit-mcp-next";
export const ADDIN_CLIENT_ID = "6F78E70D-BE13-4E0B-9B11-9E28F876AF71";
export const MANIFEST_FILE_NAME = "RevitMcpNext.addin";

export type HomeSource = "env" | "self" | "default";

export interface HomeMarker {
  schema: 1;
  createdAtUtc: string;
  createdBy: "installer" | "dev-install" | "addin" | "broker";
}

export interface ResolvedHome {
  home: string;
  source: HomeSource;
  /** True when the marker file exists in the resolved home. */
  installed: boolean;
}

export interface ResolveHomeOptions {
  env?: Record<string, string | undefined>;
  /** Directory to start the upward marker search from (the broker passes its own file's directory). */
  selfDir?: string;
  /** Override for tests of other machines; defaults to fs.existsSync. */
  exists?: (path: string) => boolean;
  /** Max directories to walk up while self-locating. */
  maxDepth?: number;
}

export function userProfileDir(env: Record<string, string | undefined> = process.env): string {
  const profile = env.USERPROFILE ?? env.HOME ?? (env.HOMEDRIVE && env.HOMEPATH ? `${env.HOMEDRIVE}${env.HOMEPATH}` : undefined);
  if (!profile) throw new Error("Cannot resolve the user profile directory (USERPROFILE is not set).");
  return profile;
}

export function defaultHome(env: Record<string, string | undefined> = process.env): string {
  return join(userProfileDir(env), DEFAULT_HOME_DIRNAME);
}

/** Walk up from `startDir` looking for the home marker. Returns the directory holding it. */
export function selfLocateHome(startDir: string, exists: (path: string) => boolean = existsSync, maxDepth = 8): string | null {
  let dir = resolve(startDir);
  for (let i = 0; i <= maxDepth; i++) {
    if (exists(join(dir, HOME_MARKER))) return dir;
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

export function resolveHome(options: ResolveHomeOptions = {}): ResolvedHome {
  const env = options.env ?? process.env;
  const exists = options.exists ?? existsSync;
  const fromEnv = env[HOME_ENV]?.trim();
  if (fromEnv) {
    const home = resolve(fromEnv);
    return { home, source: "env", installed: exists(join(home, HOME_MARKER)) };
  }
  if (options.selfDir) {
    const found = selfLocateHome(options.selfDir, exists, options.maxDepth ?? 8);
    if (found) return { home: found, source: "self", installed: true };
  }
  const home = defaultHome(env);
  return { home, source: "default", installed: exists(join(home, HOME_MARKER)) };
}

/** Returns why a home location is refused (installer, dev-install, e2e), or null when it is acceptable. */
export function forbiddenHomeReason(home: string, env: Record<string, string | undefined> = process.env): string | null {
  if (!isAbsolute(home)) return "the home must be an absolute path";
  const norm = (p: string) => resolve(p).replace(/[\\/]+$/, "").toLowerCase();
  const h = norm(home);
  const under = (root: string | undefined) => {
    if (!root) return false;
    const r = norm(root);
    return h === r || h.startsWith(r + sep) || h.startsWith(r + "\\") || h.startsWith(r + "/");
  };
  if (/[\\/]packages[\\/]/i.test(home)) return "a home inside ...\\Packages\\ is virtualized for packaged apps";
  if (/windowsapps/i.test(home)) return "WindowsApps is not writable";
  if (under(env.LOCALAPPDATA)) return "%LOCALAPPDATA% may be virtualized for packaged apps";
  if (under(env.APPDATA)) return "%APPDATA% may be virtualized for packaged apps";
  if (under(env.TEMP) || under(env.TMP)) return "%TEMP% is not durable and may be virtualized";
  if (env.USERPROFILE && h === norm(env.USERPROFILE)) return "the home must be a folder inside the user profile, not the profile itself";
  if (/^[a-z]:$/i.test(h) || /^[a-z]:\\?$/i.test(h)) return "the home must not be a drive root";
  return null;
}

/** e2e and lanes use %USERPROFILE%\.revit-mcp-next-dev-<package-id> (SPEC §13.2). */
export function devHome(packageId: string, env: Record<string, string | undefined> = process.env): string {
  return join(userProfileDir(env), `${DEFAULT_HOME_DIRNAME}-dev-${packageId}`);
}

/** True when `home` is the user's default (production) home. Test ops and e2e pre-approval are never honoured there (§13.4). */
export function isDefaultHome(home: string, env: Record<string, string | undefined> = process.env): boolean {
  return resolve(home).toLowerCase() === resolve(defaultHome(env)).toLowerCase();
}

// ---------------------------------------------------------------------------------------------
// Layout (§3). Every function takes the home as its first argument.
// ---------------------------------------------------------------------------------------------

export const markerFile = (home: string) => join(home, HOME_MARKER);
export const installReceiptFile = (home: string) => join(home, "install.json");
export const runtimeNodeExe = (home: string) => join(home, "runtime", "node.exe");
export const brokerDir = (home: string) => join(home, "broker");
export const brokerBundle = (home: string) => join(home, "broker", "revit-mcp.mjs");
export const brokerCliBundle = (home: string) => join(home, "broker", "revit-mcp-cli.mjs");
export const brokerBuildInfo = (home: string) => join(home, "broker", "build-info.json");
export const addinYearDir = (home: string, year: number | string) => join(home, "addin", String(year));
export const loaderDir = (home: string, year: number | string, loaderVersion: string) => join(home, "addin", String(year), "loader", loaderVersion);
export const loaderDll = (home: string, year: number | string, loaderVersion: string) => join(loaderDir(home, year, loaderVersion), "RevitMcpNext.Loader.dll");
export const payloadDir = (home: string, year: number | string, payloadId: string) => join(home, "addin", String(year), payloadId);
export const payloadAddinDll = (home: string, year: number | string, payloadId: string) => join(payloadDir(home, year, payloadId), "RevitMcpNext.Addin.dll");
export const payloadScriptingDir = (home: string, year: number | string, payloadId: string) => join(payloadDir(home, year, payloadId), "scripting");
export const addinCurrentFile = (home: string, year: number | string) => join(home, "addin", String(year), "current.json");
export const configDir = (home: string) => join(home, "config");
export const authFile = (home: string) => join(home, "config", "auth.env");
export const settingsFile = (home: string) => join(home, "config", "settings.json");
export const e2eTestOpsEnableFile = (home: string) => join(home, "config", "e2e-test-ops.enable");
export const instancesDir = (home: string) => join(home, "instances");
export const instanceFile = (home: string, instanceId: string) => join(home, "instances", `${instanceId}.json`);
export const lockFile = (home: string, instanceId: string) => join(home, "instances", `${instanceId}.lock`);
export const ledgerDir = (home: string) => join(home, "ledger");
export const ledgerFile = (home: string, instanceId: string) => join(home, "ledger", `${instanceId}.jsonl`);
export const jobsDir = (home: string, instanceId: string) => join(home, "jobs", instanceId);
export const jobFile = (home: string, instanceId: string, jobId: string) => join(home, "jobs", instanceId, `${jobId}.json`);
export const stateDir = (home: string) => join(home, "state");
export const inflightDir = (home: string) => join(home, "state", "inflight");
export const inflightFile = (home: string, brokerPid: number, startMs: number) => join(home, "state", "inflight", `${brokerPid}-${startMs}.json`);
export const logsDir = (home: string) => join(home, "logs");
export const brokerLogFile = (home: string, yyyymmdd: string, n: number) => join(home, "logs", `broker-${yyyymmdd}.${n}.jsonl`);
export const addinLogFile = (home: string, year: number | string, yyyymmdd: string, n: number) => join(home, "logs", `addin-${year}-${yyyymmdd}.${n}.jsonl`);
export const loaderLogFile = (home: string, year: number | string) => join(home, "logs", `loader-${year}.log`);
export const codeAuditFile = (home: string) => join(home, "logs", "code-exec-audit.jsonl");
export const codeSourceFile = (home: string, sha256: string) => join(home, "logs", "code", `${sha256}.cs`);
export const installLogFile = (home: string, stamp: string) => join(home, "logs", `install-${stamp}.log`);
export const capturesDir = (home: string) => join(home, "captures");
export const captureDir = (home: string, yyyyMmDd: string, stamp: string) => join(home, "captures", yyyyMmDd, stamp);
export const exportsDir = (home: string, docTitle?: string) => (docTitle ? join(home, "exports", sanitizeFileName(docTitle)) : join(home, "exports"));
export const recipesDir = (home: string) => join(home, "recipes");
export const pluginsDir = (home: string) => join(home, "plugins");
export const runsDir = (home: string) => join(home, "runs");

/** Manifest location for a Revit year: %APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpNext.addin. */
export function manifestFile(year: number | string, env: Record<string, string | undefined> = process.env): string {
  if (!env.APPDATA) throw new Error("APPDATA is not set");
  return join(env.APPDATA, "Autodesk", "Revit", "Addins", String(year), MANIFEST_FILE_NAME);
}

export function sanitizeFileName(name: string): string {
  const cleaned = name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").trim();
  return cleaned.length > 0 ? cleaned.slice(0, 120) : "_";
}

/** yyyyMMdd in local time (log file names). */
export function logDateStamp(date = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}`;
}

export interface AddinCurrent {
  payloadId: string;
  gitSha: string;
  builtAtUtc: string;
  installedAtUtc: string;
}

// ---------------------------------------------------------------------------------------------
// Auth token file (D4 §4.7)
// ---------------------------------------------------------------------------------------------

export const AUTH_TOKEN_ENV = "REVIT_MCP_NEXT_AUTH_TOKEN";
export const AUTH_TOKEN_PATTERN = /^[A-Za-z0-9_-]{43,}$/;

export interface ParsedAuthEnv {
  token: string | null;
  version: number | null;
}

/** Parses `# comment`, `AUTH_CONFIG_VERSION=1`, `REVIT_MCP_NEXT_AUTH_TOKEN=<43+ base64url chars>`. */
export function parseAuthEnv(text: string): ParsedAuthEnv {
  let token: string | null = null;
  let version: number | null = null;
  for (const raw of text.replace(/^\uFEFF/, "").split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const eq = line.indexOf("=");
    if (eq < 0) continue;
    const key = line.slice(0, eq).trim();
    const value = line.slice(eq + 1).trim().replace(/^"(.*)"$/, "$1");
    if (key === AUTH_TOKEN_ENV && AUTH_TOKEN_PATTERN.test(value)) token = value;
    else if (key === "AUTH_CONFIG_VERSION" && /^\d+$/.test(value)) version = Number(value);
  }
  return { token, version };
}

export function formatAuthEnv(token: string): string {
  return `# revit-mcp-next local pipe token. Local only; never share or paste.\r\nAUTH_CONFIG_VERSION=1\r\n${AUTH_TOKEN_ENV}=${token}\r\n`;
}

/** fp = first 8 hex chars of sha256(token). The token itself never appears in logs or results. */
export function authFingerprint(token: string): string {
  return createHash("sha256").update(token, "utf8").digest("hex").slice(0, 8);
}

// ---------------------------------------------------------------------------------------------
// Settings (§3.1 + lead override 5: codeExecution.consentPrompt, default false)
// ---------------------------------------------------------------------------------------------

export type CaptureSize = "small" | "medium" | "large";
export type CaptureFormat = "auto" | "png" | "jpg";
export type LogLevel = "debug" | "info" | "warn" | "error";

export interface Settings {
  schemaVersion: number;
  /** Changed only by the installer switches or the user editing the file; never by a tool argument. */
  enableCodeExecution: boolean;
  codeExecution: {
    timeoutSec: number;
    maxOutputKB: number;
    allowUnsafeApis: boolean;
    experimentalOutOfProcess: boolean;
    /** Lead override 5: when false the per-Revit-session consent TaskDialog is skipped (default false). */
    consentPrompt: boolean;
    /** e2e only; honoured only under the §13.4 conditions (isolated home + config\e2e-test-ops.enable). */
    e2ePreapproved: boolean;
  };
  /** Clamped to 10000-55000. */
  callBudgetMs: number;
  perInstancePrimarySlots: number;
  wake: { watchdogMs: number; wmNull: boolean; idlingFallback: boolean; idlingFallbackAfterMs: number };
  stall: { dialogMs: number; hungMs: number; editModeMs: number; busyFailFastMs: number };
  dialogPolicy: { autoRespond: boolean; extra: Record<string, string>; deny: string[] };
  confirm: { deleteOver: number; bulkOver: number; createOver: number };
  capture: { size: CaptureSize; format: CaptureFormat; retainHours: number; maxMB: number; maxFolders: number };
  log: { level: LogLevel; maxFileMb: number; retainDays: number };
  useElicitation: boolean;
  experimentalUndoStack: boolean;
  /** Per Revit year: default template for manage_document new_project ("" = the English metric template). */
  defaultTemplate: Record<string, string>;
}

export const SETTINGS_SCHEMA_VERSION = 1;
export const CALL_BUDGET_MIN_MS = 10_000;
export const CALL_BUDGET_MAX_MS = 55_000;

export function defaultSettings(): Settings {
  return {
    schemaVersion: SETTINGS_SCHEMA_VERSION,
    enableCodeExecution: false,
    codeExecution: { timeoutSec: 30, maxOutputKB: 64, allowUnsafeApis: false, experimentalOutOfProcess: false, consentPrompt: false, e2ePreapproved: false },
    callBudgetMs: 50_000,
    perInstancePrimarySlots: 4,
    wake: { watchdogMs: 200, wmNull: true, idlingFallback: true, idlingFallbackAfterMs: 1000 },
    stall: { dialogMs: 3000, hungMs: 5000, editModeMs: 6000, busyFailFastMs: 8000 },
    dialogPolicy: { autoRespond: true, extra: {}, deny: [] },
    confirm: { deleteOver: 20, bulkOver: 200, createOver: 500 },
    capture: { size: "medium", format: "auto", retainHours: 72, maxMB: 1024, maxFolders: 500 },
    log: { level: "info", maxFileMb: 10, retainDays: 14 },
    useElicitation: false,
    experimentalUndoStack: false,
    defaultTemplate: { "2024": "", "2027": "" },
  };
}

/** Dialog ids that dialogPolicy.extra can never add (our own prompts). */
export const PROTECTED_DIALOG_PREFIX = "RevitMcpNext_";

export interface SettingsValidation {
  settings: Settings;
  /** Human-readable problems (wrong types, clamped values, refused entries). Unknown keys are reported too. */
  issues: string[];
}

export interface ValidateSettingsOptions {
  /** Isolated e2e homes may raise codeExecution.timeoutSec for R5 (§13.4). */
  isolatedHome?: boolean;
}

/** Merge raw settings over the defaults with type checks and clamps. Never throws. */
export function validateSettings(raw: unknown, options: ValidateSettingsOptions = {}): SettingsValidation {
  const d = defaultSettings();
  const issues: string[] = [];
  if (raw === undefined || raw === null) return { settings: d, issues };
  if (typeof raw !== "object" || Array.isArray(raw)) return { settings: d, issues: ["settings.json must hold a JSON object"] };
  const r = raw as Record<string, unknown>;
  const known = new Set(Object.keys(d));
  for (const key of Object.keys(r)) if (!known.has(key) && !key.startsWith("$") && !key.startsWith("_")) issues.push(`unknown key ${key} ignored`);

  const bool = (path: string, value: unknown, fallback: boolean): boolean => {
    if (value === undefined) return fallback;
    if (typeof value === "boolean") return value;
    issues.push(`${path} must be true or false; using ${fallback}`);
    return fallback;
  };
  const num = (path: string, value: unknown, fallback: number, min: number, max: number, integer = true): number => {
    if (value === undefined) return fallback;
    if (typeof value !== "number" || !Number.isFinite(value)) {
      issues.push(`${path} must be a number; using ${fallback}`);
      return fallback;
    }
    let v = integer ? Math.round(value) : value;
    if (v < min || v > max) {
      const clamped = Math.min(max, Math.max(min, v));
      issues.push(`${path}=${value} clamped to ${clamped} (${min}-${max})`);
      v = clamped;
    }
    return v;
  };
  const oneOf = <T extends string>(path: string, value: unknown, fallback: T, allowed: readonly T[]): T => {
    if (value === undefined) return fallback;
    if (typeof value === "string" && (allowed as readonly string[]).includes(value)) return value as T;
    issues.push(`${path} must be one of ${allowed.join("|")}; using ${fallback}`);
    return fallback;
  };
  const obj = (path: string, value: unknown): Record<string, unknown> => {
    if (value === undefined) return {};
    if (value !== null && typeof value === "object" && !Array.isArray(value)) return value as Record<string, unknown>;
    issues.push(`${path} must be an object; using defaults`);
    return {};
  };

  const s = d;
  s.schemaVersion = num("schemaVersion", r.schemaVersion, d.schemaVersion, 1, 1000);
  s.enableCodeExecution = bool("enableCodeExecution", r.enableCodeExecution, d.enableCodeExecution);

  const ce = obj("codeExecution", r.codeExecution);
  s.codeExecution = {
    timeoutSec: num("codeExecution.timeoutSec", ce.timeoutSec, 30, 1, options.isolatedHome ? 600 : 45),
    maxOutputKB: num("codeExecution.maxOutputKB", ce.maxOutputKB, 64, 1, 1024),
    allowUnsafeApis: bool("codeExecution.allowUnsafeApis", ce.allowUnsafeApis, false),
    experimentalOutOfProcess: bool("codeExecution.experimentalOutOfProcess", ce.experimentalOutOfProcess, false),
    consentPrompt: bool("codeExecution.consentPrompt", ce.consentPrompt, false),
    e2ePreapproved: bool("codeExecution.e2ePreapproved", ce.e2ePreapproved, false),
  };

  s.callBudgetMs = num("callBudgetMs", r.callBudgetMs, d.callBudgetMs, CALL_BUDGET_MIN_MS, CALL_BUDGET_MAX_MS);
  s.perInstancePrimarySlots = num("perInstancePrimarySlots", r.perInstancePrimarySlots, d.perInstancePrimarySlots, 1, 16);

  const wake = obj("wake", r.wake);
  s.wake = {
    watchdogMs: num("wake.watchdogMs", wake.watchdogMs, d.wake.watchdogMs, 50, 5000),
    wmNull: bool("wake.wmNull", wake.wmNull, d.wake.wmNull),
    idlingFallback: bool("wake.idlingFallback", wake.idlingFallback, d.wake.idlingFallback),
    idlingFallbackAfterMs: num("wake.idlingFallbackAfterMs", wake.idlingFallbackAfterMs, d.wake.idlingFallbackAfterMs, 100, 60_000),
  };

  const stall = obj("stall", r.stall);
  s.stall = {
    dialogMs: num("stall.dialogMs", stall.dialogMs, d.stall.dialogMs, 500, 60_000),
    hungMs: num("stall.hungMs", stall.hungMs, d.stall.hungMs, 1000, 60_000),
    editModeMs: num("stall.editModeMs", stall.editModeMs, d.stall.editModeMs, 1000, 60_000),
    busyFailFastMs: num("stall.busyFailFastMs", stall.busyFailFastMs, d.stall.busyFailFastMs, 1000, 50_000),
  };

  const dp = obj("dialogPolicy", r.dialogPolicy);
  const deny: string[] = [];
  if (dp.deny !== undefined) {
    if (Array.isArray(dp.deny)) {
      for (const id of dp.deny) if (typeof id === "string" && id.trim()) deny.push(id.trim());
    } else {
      issues.push("dialogPolicy.deny must be an array of dialog ids");
    }
  }
  const extra: Record<string, string> = {};
  if (dp.extra !== undefined) {
    if (dp.extra !== null && typeof dp.extra === "object" && !Array.isArray(dp.extra)) {
      for (const [id, button] of Object.entries(dp.extra as Record<string, unknown>)) {
        if (id.startsWith(PROTECTED_DIALOG_PREFIX)) issues.push(`dialogPolicy.extra cannot answer our own dialog ${id}; refused`);
        else if (deny.includes(id)) issues.push(`dialogPolicy.extra entry ${id} is on the deny list; refused`);
        else if (typeof button !== "string" || !button.trim()) issues.push(`dialogPolicy.extra.${id} must name a button`);
        else extra[id] = button.trim();
      }
    } else issues.push("dialogPolicy.extra must be an object {dialogId: button}");
  }
  s.dialogPolicy = { autoRespond: bool("dialogPolicy.autoRespond", dp.autoRespond, true), extra, deny };

  const confirm = obj("confirm", r.confirm);
  s.confirm = {
    deleteOver: num("confirm.deleteOver", confirm.deleteOver, d.confirm.deleteOver, 0, 1_000_000),
    bulkOver: num("confirm.bulkOver", confirm.bulkOver, d.confirm.bulkOver, 0, 1_000_000),
    createOver: num("confirm.createOver", confirm.createOver, d.confirm.createOver, 0, 1_000_000),
  };

  const capture = obj("capture", r.capture);
  s.capture = {
    size: oneOf("capture.size", capture.size, d.capture.size, ["small", "medium", "large"] as const),
    format: oneOf("capture.format", capture.format, d.capture.format, ["auto", "png", "jpg"] as const),
    retainHours: num("capture.retainHours", capture.retainHours, d.capture.retainHours, 1, 24 * 365),
    maxMB: num("capture.maxMB", capture.maxMB, d.capture.maxMB, 16, 1_048_576),
    maxFolders: num("capture.maxFolders", capture.maxFolders, d.capture.maxFolders, 10, 1_000_000),
  };

  const log = obj("log", r.log);
  s.log = {
    level: oneOf("log.level", log.level, d.log.level, ["debug", "info", "warn", "error"] as const),
    maxFileMb: num("log.maxFileMb", log.maxFileMb, d.log.maxFileMb, 1, 1024),
    retainDays: num("log.retainDays", log.retainDays, d.log.retainDays, 1, 3650),
  };

  s.useElicitation = bool("useElicitation", r.useElicitation, d.useElicitation);
  s.experimentalUndoStack = bool("experimentalUndoStack", r.experimentalUndoStack, d.experimentalUndoStack);

  const tpl = obj("defaultTemplate", r.defaultTemplate);
  const defaultTemplate: Record<string, string> = { ...d.defaultTemplate };
  for (const [year, path] of Object.entries(tpl)) {
    if (!/^\d{4}$/.test(year)) issues.push(`defaultTemplate key ${year} must be a Revit year`);
    else if (typeof path !== "string") issues.push(`defaultTemplate.${year} must be a path string`);
    else defaultTemplate[year] = path;
  }
  s.defaultTemplate = defaultTemplate;

  return { settings: s, issues };
}

/** Parse settings.json text. Invalid JSON throws a SyntaxError so callers can keep the last good values (SETTINGS_INVALID). */
export function parseSettingsText(text: string, options: ValidateSettingsOptions = {}): SettingsValidation {
  const trimmed = text.replace(/^\uFEFF/, "").trim();
  if (!trimmed) return { settings: defaultSettings(), issues: [] };
  return validateSettings(JSON.parse(trimmed), options);
}

/** English metric template for a year, used when settings.defaultTemplate has none (§13.2, D4 §8.5). */
export function defaultProjectTemplateCandidates(year: number | string, env: Record<string, string | undefined> = process.env): string[] {
  const programData = env.ProgramData ?? env.PROGRAMDATA ?? "C:\\ProgramData";
  const root = join(programData, "Autodesk", `RVT ${year}`, "Templates");
  return [
    join(root, "English", "Default-Multi-Discipline_Metric.rte"),
    join(root, "English", "DefaultMetric.rte"),
    join(root, "English-Imperial", "Default-Multi-Discipline.rte"),
    join(root, "German", "BIM_Architektur_und_Ingenieurbau.rte"),
    join(root, "German", "DACH-Vorlage.rte"),
  ];
}

/** Revit.exe candidates for a year (portable: honours E2E_REVIT_<year>_EXE and ProgramW6432/ProgramFiles). */
export function revitExeCandidates(year: number | string, env: Record<string, string | undefined> = process.env): string[] {
  const out: string[] = [];
  const override = env[`E2E_REVIT_${year}_EXE`];
  if (override) out.push(override);
  for (const root of [env.ProgramW6432, env.ProgramFiles, env.PROGRAMFILES, "C:\\Program Files"]) {
    if (root) out.push(join(root, "Autodesk", `Revit ${year}`, "Revit.exe"));
  }
  return [...new Set(out)];
}

/** Revit journal folder for a year: %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <year>\Journals. */
export function revitJournalDir(year: number | string, env: Record<string, string | undefined> = process.env): string | null {
  if (!env.LOCALAPPDATA) return null;
  return join(env.LOCALAPPDATA, "Autodesk", "Revit", `Autodesk Revit ${year}`, "Journals");
}
