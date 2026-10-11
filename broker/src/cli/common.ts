// Shared helpers of the revit-mcp-cli commands (doctor, clients, raw, support, settings).
// No dependency on the running broker; everything works from the home and the client config files.

import { spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, renameSync, statSync, writeFileSync } from "node:fs";
import { delimiter, dirname, extname, isAbsolute, join, resolve } from "node:path";
import { brokerBundle, brokerCliBundle, resolveHome, runtimeNodeExe } from "@revit-mcp-next/contracts/home";

// ------------------------------------------------------------------------------------------------ args

export interface ParsedArgs {
  _: string[];
  flags: Record<string, string | boolean>;
}

/** --key value | --key=value | --switch; repeated keys keep the last value. */
export function parseArgs(argv: string[], booleans: string[] = []): ParsedArgs {
  const out: ParsedArgs = { _: [], flags: {} };
  const bool = new Set(booleans);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]!;
    if (!a.startsWith("--")) {
      out._.push(a);
      continue;
    }
    const eq = a.indexOf("=");
    if (eq > 0) {
      out.flags[a.slice(2, eq)] = a.slice(eq + 1);
      continue;
    }
    const key = a.slice(2);
    const next = argv[i + 1];
    if (bool.has(key) || next === undefined || next.startsWith("--")) out.flags[key] = true;
    else {
      out.flags[key] = next;
      i++;
    }
  }
  return out;
}

export function flagString(args: ParsedArgs, key: string): string | undefined {
  const v = args.flags[key];
  return typeof v === "string" && v.trim() ? v.trim() : undefined;
}

export function flagBool(args: ParsedArgs, key: string): boolean {
  const v = args.flags[key];
  return v === true || v === "true" || v === "1";
}

export function flagList(args: ParsedArgs, key: string): string[] | undefined {
  const v = flagString(args, key);
  return v ? v.split(/[,; ]+/).map((s) => s.trim().toLowerCase()).filter(Boolean) : undefined;
}

// ------------------------------------------------------------------------------------------------ home

export interface CliHome {
  home: string;
  source: "flag" | "env" | "self" | "default";
}

/** --home, else env REVIT_MCP_NEXT_HOME, else the home this CLI is installed in, else %USERPROFILE%\.revit-mcp-next. */
export function cliHome(args: ParsedArgs, selfDir: string): CliHome {
  const flag = flagString(args, "home");
  if (flag) return { home: resolve(flag), source: "flag" };
  const r = resolveHome({ selfDir });
  return { home: r.home, source: r.source };
}

/** node.exe and bundle the clients should launch: the home's copies (installed layout). */
export function launchTarget(home: string): { command: string; args: string[]; cli: string } {
  return { command: runtimeNodeExe(home), args: [brokerBundle(home)], cli: brokerCliBundle(home) };
}

export function codexHome(env: NodeJS.ProcessEnv = process.env, override?: string): string {
  if (override) return resolve(override);
  const fromEnv = env.CODEX_HOME?.trim();
  if (fromEnv) return resolve(fromEnv);
  return join(env.USERPROFILE ?? env.HOME ?? ".", ".codex");
}

/** Claude Code keeps user-scope MCP servers in <CLAUDE_CONFIG_DIR>/.claude.json or ~/.claude.json. */
export function claudeJsonPath(env: NodeJS.ProcessEnv = process.env): string {
  const dir = env.CLAUDE_CONFIG_DIR?.trim();
  if (dir) return join(resolve(dir), ".claude.json");
  return join(env.USERPROFILE ?? env.HOME ?? ".", ".claude.json");
}

export function claudeSettingsPath(env: NodeJS.ProcessEnv = process.env): string {
  const dir = env.CLAUDE_CONFIG_DIR?.trim();
  return join(dir ? resolve(dir) : join(env.USERPROFILE ?? env.HOME ?? ".", ".claude"), "settings.json");
}

/** Claude Desktop config candidates (classic install, then the MSIX package's redirected roaming folder). */
export function claudeDesktopConfigCandidates(env: NodeJS.ProcessEnv = process.env): string[] {
  const out: string[] = [];
  if (env.APPDATA) out.push(join(env.APPDATA, "Claude", "claude_desktop_config.json"));
  if (env.LOCALAPPDATA) {
    const packages = join(env.LOCALAPPDATA, "Packages");
    try {
      for (const name of readdirSync(packages)) {
        if (/^(AnthropicPBC\.)?Claude_/i.test(name)) out.push(join(packages, name, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
      }
    } catch {
      // no Packages folder
    }
  }
  return out;
}

// ------------------------------------------------------------------------------------------------ files

export function readText(path: string): string | null {
  try {
    return readFileSync(path, "utf8").replace(/^﻿/, "");
  } catch {
    return null;
  }
}

export function readJsonFile<T = unknown>(path: string): { value: T | null; error: string | null; exists: boolean } {
  const text = readText(path);
  if (text === null) return { value: null, error: null, exists: existsSync(path) };
  try {
    return { value: (text.trim() ? JSON.parse(text) : {}) as T, error: null, exists: true };
  } catch (error) {
    return { value: null, error: (error as Error).message, exists: true };
  }
}

export function timestamp(date = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}-${p(date.getHours())}${p(date.getMinutes())}${p(date.getSeconds())}`;
}

/** Copy `path` to `<path>.revit-mcp-next-<ts>.bak` (once per run per file). Returns the backup path or null when there was nothing to back up. */
const backedUp = new Map<string, string>();
export function backupFile(path: string, stamp: string): string | null {
  const key = resolve(path).toLowerCase();
  if (backedUp.has(key)) return backedUp.get(key)!;
  if (!existsSync(path)) return null;
  let target = `${path}.revit-mcp-next-${stamp}.bak`;
  for (let i = 2; existsSync(target); i++) target = `${path}.revit-mcp-next-${stamp}-${i}.bak`;
  copyFileSync(path, target);
  backedUp.set(key, target);
  return target;
}

/** Atomic text write (temp + rename); creates the folder. */
export function writeTextAtomic(path: string, text: string): void {
  mkdirSync(dirname(path), { recursive: true });
  const temp = `${path}.${process.pid}.${Date.now()}.tmp`;
  writeFileSync(temp, text, "utf8");
  renameSync(temp, path);
}

export function fileSize(path: string): number | null {
  try {
    return statSync(path).size;
  } catch {
    return null;
  }
}

// ------------------------------------------------------------------------------------------------ processes

/** Find an executable on PATH (honours PATHEXT). Absolute paths are returned when they exist. */
export function which(name: string, env: NodeJS.ProcessEnv = process.env): string | null {
  if (isAbsolute(name)) return existsSync(name) ? name : null;
  const exts = extname(name) ? [""] : (env.PATHEXT ?? ".EXE;.CMD;.BAT;.COM").split(";").filter(Boolean).map((e) => e.toLowerCase());
  const pathKey = Object.keys(env).find((k) => k.toUpperCase() === "PATH") ?? "PATH";
  for (const dir of (env[pathKey] ?? "").split(delimiter)) {
    if (!dir) continue;
    for (const ext of exts) {
      const candidate = join(dir.replace(/^"|"$/g, ""), name + ext);
      if (existsSync(candidate)) return candidate;
    }
  }
  return null;
}

export interface RunResult {
  ok: boolean;
  code: number | null;
  stdout: string;
  stderr: string;
  error?: string;
  command: string;
}

const quoteCmd = (s: string) => (/^[A-Za-z0-9_\-.:\\/=@]+$/.test(s) ? s : `"${s.replace(/"/g, '""')}"`);

/** Run a tool synchronously; .cmd/.bat shims go through cmd.exe with quoted arguments. */
export function run(exe: string, args: string[], options: { env?: NodeJS.ProcessEnv; timeoutMs?: number; cwd?: string; input?: string } = {}): RunResult {
  const shim = /\.(cmd|bat)$/i.test(exe);
  const display = [exe, ...args].map(quoteCmd).join(" ");
  const result = shim
    ? spawnSync(process.env.ComSpec ?? "cmd.exe", ["/d", "/s", "/c", `"${[exe, ...args].map(quoteCmd).join(" ")}"`], {
        env: options.env ?? process.env,
        encoding: "utf8",
        timeout: options.timeoutMs ?? 120_000,
        windowsHide: true,
        windowsVerbatimArguments: true,
        cwd: options.cwd,
        input: options.input,
      })
    : spawnSync(exe, args, { env: options.env ?? process.env, encoding: "utf8", timeout: options.timeoutMs ?? 120_000, windowsHide: true, cwd: options.cwd, input: options.input });
  return {
    ok: !result.error && result.status === 0,
    code: result.status,
    stdout: result.stdout ?? "",
    stderr: result.stderr ?? "",
    error: result.error ? result.error.message : undefined,
    command: display,
  };
}

// ------------------------------------------------------------------------------------------------ Codex env

/**
 * The environment Codex forwards to stdio MCP servers on Windows (codex rmcp-client default forwarding), compared
 * case-insensitively. Same list as e2e/lib/codex-env.mjs (W3-E2E owns that file; keep them equal).
 */
export const CODEX_ENV_WHITELIST = [
  "PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "USERNAME", "USERDOMAIN", "USERPROFILE", "HOMEDRIVE",
  "HOMEPATH", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432", "PROGRAMDATA", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP",
  "TMPDIR", "POWERSHELL", "PWSH", "SHELL",
];

export function codexEnv(base: NodeJS.ProcessEnv, extra: Record<string, string> = {}): Record<string, string> {
  const allowed = new Set(CODEX_ENV_WHITELIST);
  const env: Record<string, string> = {};
  for (const [key, value] of Object.entries(base)) if (value !== undefined && allowed.has(key.toUpperCase())) env[key] = value;
  for (const [key, value] of Object.entries(extra)) {
    for (const existing of Object.keys(env)) if (existing.toUpperCase() === key.toUpperCase()) delete env[existing];
    env[key] = value;
  }
  return env;
}

// ------------------------------------------------------------------------------------------------ output

export type Level = "ok" | "info" | "warn" | "fail";

export interface Check {
  id: string;
  level: Level;
  message: string;
  fix?: string;
  data?: unknown;
}

export function printChecks(checks: Check[], write: (line: string) => void = (l) => process.stdout.write(l + "\n")): void {
  const tag: Record<Level, string> = { ok: "[ OK ]", info: "[INFO]", warn: "[WARN]", fail: "[FAIL]" };
  for (const c of checks) {
    write(`${tag[c.level]} ${c.id}: ${c.message}`);
    if (c.fix) write(`       fix: ${c.fix}`);
  }
}

export function samePath(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return false;
  const n = (p: string) => resolve(p.replace(/^\\\\\?\\/, "")).replace(/[\\/]+$/, "").toLowerCase();
  return n(a) === n(b);
}

export function isUnder(path: string, root: string): boolean {
  const n = (p: string) => resolve(p).replace(/[\\/]+$/, "").toLowerCase();
  const p = n(path);
  const r = n(root);
  return p === r || p.startsWith(r + "\\") || p.startsWith(r + "/");
}

export { brokerBundle, brokerCliBundle, runtimeNodeExe };
