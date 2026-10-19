// Revit lifecycle for the e2e harness (SPEC §13.2, D4 §8.4, lead overrides 1-3):
// - launch: Revit.exe is started OUTSIDE the caller's process tree/job through WMI Win32_Process.Create, with the full
//   environment (minus CLAUDE*/ANTHROPIC*/NODE_USE_SYSTEM_CA/NoDefaultCurrentDirectoryInExePath) plus
//   REVIT_MCP_NEXT_HOME. Revit started inside an app's job object fails Autodesk licensing.
// - waitReady: the registration file r<year>-<pid>-*.json in the e2e home, then status reports the instance idle.
//   The newest journal is tailed for TaskDialog ids and licensing failures (Adlsdk Error / stuck at "manage licensing").
// - kill: only pids this harness started (taskkill /PID <pid> /F). Never by name.
// Portable: Revit and journals are found through ProgramFiles/ProgramW6432/LOCALAPPDATA (E2E_REVIT_<year>_EXE overrides).

import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { instancesDir, isDefaultHome, revitExeCandidates, revitJournalDir } from "@revit-mcp-next/contracts/home";

export class LicensingBlocked extends Error {
  constructor(message) {
    super(message);
    this.name = "LicensingBlocked";
  }
}

export class StartupDialog extends Error {
  constructor(message, dialogId) {
    super(message);
    this.name = "StartupDialog";
    this.dialogId = dialogId;
  }
}

export function findRevitExe(year) {
  for (const candidate of revitExeCandidates(year)) if (existsSync(candidate)) return candidate;
  return null;
}

export function installedYears() {
  return [2024, 2027].filter((y) => findRevitExe(y));
}

const ENV_EXCLUDE = /^CLAUDE|^ANTHROPIC|^NODE_USE_SYSTEM_CA$|^NoDefaultCurrentDirectoryInExePath$/i;

function psQuote(text) {
  return `'${String(text).replace(/'/g, "''")}'`;
}

/** Arguments every harness launch adds first (run.mjs --language DEU sets ["/language", "DEU"]). */
let defaultLaunchArgs = [];
export function setDefaultLaunchArgs(args) {
  defaultLaunchArgs = Array.isArray(args) ? [...args] : [];
}

/**
 * Launch Revit through WMI so it runs outside this process tree and job (lead override 1).
 * @returns {Promise<{pid:number, exe:string, startedAt:number}>}
 */
export async function launchRevit({ year, home, args = [], minimized = false }) {
  const exe = findRevitExe(year);
  if (!exe) throw new Error(`Revit ${year} is not installed (looked for ${revitExeCandidates(year).join(", ")}); set E2E_REVIT_${year}_EXE`);
  args = [...defaultLaunchArgs, ...args];
  const commandLine = [`"${exe}"`, ...args.map((a) => (/\s/.test(a) ? `"${a}"` : a))].join(" ");
  const script = [
    "$ErrorActionPreference = 'Stop'",
    `$envs = @(Get-ChildItem env: | Where-Object { $_.Name -notmatch '^CLAUDE|^ANTHROPIC|^NODE_USE_SYSTEM_CA$|^NoDefaultCurrentDirectoryInExePath$' -and $_.Name -ne 'REVIT_MCP_NEXT_HOME' } | ForEach-Object { "$($_.Name)=$($_.Value)" })`,
    `$envs += 'REVIT_MCP_NEXT_HOME=' + ${psQuote(home)}`,
    `$si = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ EnvironmentVariables = [string[]]$envs; ShowWindow = [uint16]${minimized ? 7 : 1} }`,
    `$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = ${psQuote(commandLine)}; CurrentDirectory = ${psQuote(dirname(exe))}; ProcessStartupInformation = $si }`,
    "@{ ReturnValue = [int]$r.ReturnValue; ProcessId = [int]$r.ProcessId } | ConvertTo-Json -Compress",
  ].join("\n");
  const env = {};
  for (const [k, v] of Object.entries(process.env)) if (!ENV_EXCLUDE.test(k)) env[k] = v;
  const startedAt = Date.now();
  const out = spawnSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script], { env, encoding: "utf8", windowsHide: true, timeout: 60_000 });
  if (out.status !== 0) throw new Error(`WMI launch of Revit ${year} failed: ${(out.stderr || out.stdout || "").trim().slice(0, 500)}`);
  const line = out.stdout.trim().split(/\r?\n/).filter(Boolean).pop() ?? "";
  let parsed;
  try {
    parsed = JSON.parse(line);
  } catch {
    throw new Error(`WMI launch of Revit ${year} gave unexpected output: ${line.slice(0, 300)}`);
  }
  if (parsed.ReturnValue !== 0 || !parsed.ProcessId) throw new Error(`Win32_Process.Create returned ${parsed.ReturnValue} for Revit ${year}`);
  const record = { pid: parsed.ProcessId, exe, year, home, startedAt };
  ownedPids.add(record.pid);
  writeOwned(home, year, record);
  return record;
}

/** Pids this harness (or an earlier run with --keep-revit) started, per home and year. */
const ownedPids = new Set();

function ownedFile(home, year) {
  return join(home, "runs", `e2e-revit-${year}.json`);
}

function writeOwned(home, year, record) {
  const file = ownedFile(home, year);
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(record, null, 2));
}

/** A Revit this harness launched earlier for this home (still alive), or null. */
export function ownedRevit(home, year) {
  const file = ownedFile(home, year);
  if (!existsSync(file)) return null;
  try {
    const record = JSON.parse(readFileSync(file, "utf8"));
    if (record && processAlive(record.pid) && isRevitPid(record.pid)) {
      ownedPids.add(record.pid);
      return record;
    }
  } catch {
    // ignore
  }
  return null;
}

export function processAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
}

function isRevitPid(pid) {
  const out = spawnSync("tasklist.exe", ["/FI", `PID eq ${pid}`, "/FO", "CSV", "/NH"], { encoding: "utf8", windowsHide: true });
  return /revit\.exe/i.test(out.stdout ?? "");
}

/** Kill a Revit this harness started. Refuses any other pid (lead override 3). */
export function killOwned(pid) {
  if (!ownedPids.has(pid)) throw new Error(`refusing to kill pid ${pid}: not started by this harness`);
  spawnSync("taskkill.exe", ["/PID", String(pid), "/F"], { windowsHide: true });
  ownedPids.delete(pid);
}

// ------------------------------------------------------------------------------------------------ journal

/** Newest journal written after `sinceMs`. */
export function newestJournal(year, sinceMs) {
  const dir = revitJournalDir(year);
  if (!dir || !existsSync(dir)) return null;
  let best = null;
  for (const name of readdirSync(dir)) {
    if (!/^journal\.\d+\.txt$/i.test(name)) continue;
    const path = join(dir, name);
    try {
      const info = statSync(path);
      if (info.mtimeMs >= sinceMs - 5_000 && (!best || info.mtimeMs > best.mtimeMs)) best = { path, mtimeMs: info.mtimeMs };
    } catch {
      // ignore
    }
  }
  return best?.path ?? null;
}

const KNOWN_DIALOG_HINTS = {
  TaskDialog_Security_Unsigned_File_Loading: "Revit is waiting on the unsigned add-in prompt: run scripts/ensure-revit-addin-trust.ps1 or choose 'Always Load' once",
};

/** Inspect a journal for licensing failures and startup dialogs. */
export function inspectJournal(path) {
  let text = "";
  try {
    text = readFileSync(path, "latin1");
  } catch {
    return { licensingError: null, manageLicensing: false, dialogs: [] };
  }
  const licensing = /LicenseUpd\(\d+\)\s*Adlsdk Error[^\r\n]*|Adlsdk Error[^\r\n]*/i.exec(text);
  const manage = /manage licensing/i.test(text);
  const pastLicensing = /(Revit started|Jrn\.Command\b|InitializeApplication|ApplicationInitialized)/i.test(text.split(/manage licensing/i).pop() ?? "");
  const dialogs = [...text.matchAll(/'Id\s*:\s*(TaskDialog_[A-Za-z0-9_]+)/g)].map((m) => m[1]);
  return { licensingError: licensing ? licensing[0].trim() : null, manageLicensing: manage && !pastLicensing, dialogs: [...new Set(dialogs)] };
}

// ------------------------------------------------------------------------------------------------ readiness

/** Registration files for this year/pid in the home. */
export function registrationFor(home, year, pid) {
  const dir = instancesDir(home);
  if (!existsSync(dir)) return null;
  const name = readdirSync(dir).find((n) => new RegExp(`^r${year}-${pid}-[0-9a-f]{6}\\.json$`).test(n));
  return name ? join(dir, name) : null;
}

/**
 * Wait until Revit registered in the home and the broker reports it idle.
 * Fails fast on licensing failures (lead override 2: never loops) and on known startup dialogs.
 * @param {{ year:number, pid:number, home:string, startedAt:number, call: Function, timeoutMs?: number, log?: (m:string)=>void }} o
 */
export async function waitReady(o) {
  const timeoutMs = o.timeoutMs ?? 300_000;
  const deadline = Date.now() + timeoutMs;
  let manageSince = null;
  let lastJournalCheck = 0;
  for (;;) {
    if (!processAlive(o.pid)) throw new Error(`Revit ${o.year} (pid ${o.pid}) exited during startup`);
    if (Date.now() - lastJournalCheck > 5_000) {
      lastJournalCheck = Date.now();
      const journal = newestJournal(o.year, o.startedAt);
      if (journal) {
        const j = inspectJournal(journal);
        if (j.licensingError) throw new LicensingBlocked(`Revit ${o.year} licensing failed: ${j.licensingError} (${journal})`);
        if (j.manageLicensing) {
          manageSince ??= Date.now();
          if (Date.now() - manageSince > 120_000) throw new LicensingBlocked(`Revit ${o.year} has been at "manage licensing" for more than 120 s (${journal}); sign in to Autodesk once by hand`);
        } else manageSince = null;
        for (const id of j.dialogs) if (KNOWN_DIALOG_HINTS[id]) throw new StartupDialog(KNOWN_DIALOG_HINTS[id], id);
      }
    }
    if (registrationFor(o.home, o.year, o.pid)) {
      const r = await o.call("status", { instance: String(o.pid), detail: "full" }, { allowError: true });
      const row = Array.isArray(r.json?.revit) ? r.json.revit.find((x) => Array.isArray(x) && x[1] === o.pid) : null;
      // o.doc: a file passed on the command line opens after the add-in registers; wait until the doc is listed too.
      const docRows = Array.isArray(r.json?.docs?.rows) ? r.json.docs.rows : [];
      const docOpen = !o.doc || docRows.some((d) => String(d[1] ?? "").replace(/[.](rvt|rfa)$/i, "").toLowerCase() === String(o.doc).toLowerCase());
      if (row && docOpen && (row[2] === "idle" || String(row[2]).startsWith("busy"))) return { readyMs: Date.now() - o.startedAt, state: row[2] };
      o.log?.(`Revit ${o.year} pid ${o.pid}: ${row ? row[2] : "registered, waiting for the add-in"}`);
    }
    if (Date.now() > deadline) throw new Error(`Revit ${o.year} (pid ${o.pid}) did not become ready within ${Math.round(timeoutMs / 1000)} s (no registration in ${instancesDir(o.home)})`);
    await new Promise((r) => setTimeout(r, 2_000));
  }
}

/**
 * The folder holding addin\<year>\RevitMcpNext.Addin.dll and loader\<year>\RevitMcpNext.Loader.dll: the repo's
 * artifacts\ (built by scripts/build-addin.ps1), or an extracted release package (searched up to 3 levels deep).
 */
export function findArtifactsRoot(start, years) {
  const has = (dir) => years.every((y) => existsSync(join(dir, "addin", String(y), "RevitMcpNext.Addin.dll")));
  const queue = [{ dir: start, depth: 0 }];
  while (queue.length) {
    const { dir, depth } = queue.shift();
    if (!existsSync(dir)) continue;
    if (has(dir)) return dir;
    if (depth >= 3) continue;
    let entries = [];
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const e of entries) if (e.isDirectory() && !/^(node_modules|\.git)$/i.test(e.name)) queue.push({ dir: join(dir, e.name), depth: depth + 1 });
  }
  return null;
}

/**
 * Run scripts/dev-install.ps1 for the years (W1-ADDIN's script). Test ops are enabled in every non-default home
 * (SPEC §13.4), so R5/R6/R7/R12/S99 work on the first launch. `artifactsRoot` installs an extracted release package
 * instead of the repo build.
 */
export function devInstall({ repo, home, years, artifactsRoot = null }) {
  const script = join(repo, "scripts", "dev-install.ps1");
  if (!existsSync(script)) throw new Error(`scripts/dev-install.ps1 is missing (the add-in lane has not landed it yet)`);
  const args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Home", home, "-RevitYears", years.join(",")];
  if (!isDefaultHome(home)) args.push("-EnableTestOps");
  if (artifactsRoot) args.push("-ArtifactsRoot", artifactsRoot);
  const out = spawnSync("powershell.exe", args, { encoding: "utf8", windowsHide: true, timeout: 10 * 60_000 });
  if (out.status !== 0) throw new Error(`dev-install failed (exit ${out.status}): ${(out.stderr || out.stdout || "").trim().slice(-1500)}`);
  return out.stdout;
}

/** File and product version of a Revit.exe (for summary.json), or null. */
export function revitVersion(year) {
  const exe = findRevitExe(year);
  if (!exe) return null;
  const script = `$v = (Get-Item -LiteralPath ${psQuote(exe)}).VersionInfo; @{ file = $v.FileVersion; product = $v.ProductVersion; name = $v.ProductName } | ConvertTo-Json -Compress`;
  const out = spawnSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script], { encoding: "utf8", windowsHide: true, timeout: 30_000 });
  try {
    return { exe, ...JSON.parse((out.stdout ?? "").trim().split(/\r?\n/).pop()) };
  } catch {
    return { exe };
  }
}

/** Build the add-in for a year (scripts/build-addin.ps1); E2E_DOTNET or REVIT_MCP_NEXT_DOTNET selects the SDK. */
export function buildAddin({ repo, year }) {
  const script = join(repo, "scripts", "build-addin.ps1");
  const args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-RevitYear", String(year)];
  const dotnet = process.env.E2E_DOTNET ?? process.env.REVIT_MCP_NEXT_DOTNET;
  if (dotnet) args.push("-DotnetPath", dotnet);
  const out = spawnSync("powershell.exe", args, { encoding: "utf8", windowsHide: true, timeout: 20 * 60_000, stdio: ["ignore", "pipe", "pipe"] });
  if (out.status !== 0) throw new Error(`build-addin ${year} failed (exit ${out.status}): ${(out.stderr || out.stdout || "").trim().slice(-1500)}`);
}

