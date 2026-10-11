// `revit-mcp-cli doctor [--quick] [--json]`: checks one home end to end the way the clients see it.
//   home       marker, receipt, runtime node, broker bundle, settings, auth
//   add-in     per Revit year: payload + current.json, loader, manifest -> loader of THIS home (no split install), trust,
//              live registrations vs running Revit.exe processes
//   Codex      [mcp_servers.revit] block, legacy entries, conflicting plugins, plugin/skill, AGENTS snippet;
//              full: `codex mcp get revit --json` (V22)
//   spawn      starts the configured command exactly like Codex (env whitelist + config env), initialize, tools/list,
//              status; full: both protocol eras (2025-06-18 legacy and 2026-07-28 modern)
//   Claude     user-scope mcpServers.revit, conflicting plugins (revit-mcp-cowork auto-allow hook), Claude Desktop
//   legacy     old roots and manifests

import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { Client } from "@modelcontextprotocol/client";
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio";
import {
  ADDIN_CLIENT_ID,
  addinCurrentFile,
  authFile,
  authFingerprint,
  brokerBuildInfo,
  forbiddenHomeReason,
  installReceiptFile,
  loaderLogFile,
  MANIFEST_FILE_NAME,
  manifestFile,
  markerFile,
  parseAuthEnv,
  parseSettingsText,
  payloadAddinDll,
  revitExeCandidates,
  settingsFile,
} from "@revit-mcp-next/contracts/home";
import { findLegacyRoots } from "../runtime/home.js";
import { AGENTS_BEGIN, AUTO_APPROVED_TOOLS, inspectCodexConfig, PLUGIN_ID, SERVER_NAME, STARTUP_TIMEOUT_SEC, TOOL_TIMEOUT_SEC, verifyCodexGet } from "./clients.js";
import {
  claudeDesktopConfigCandidates,
  claudeJsonPath,
  claudeSettingsPath,
  codexEnv,
  isUnder,
  launchTarget,
  readJsonFile,
  readText,
  run,
  samePath,
  which,
  type Check,
} from "./common.js";
import { listInstances } from "./raw.js";

export const YEARS = [2024, 2027];

export interface DoctorOptions {
  home: string;
  homeSource: string;
  quick: boolean;
  codexHome: string;
  /** Skip the broker spawn (installer dry runs). */
  noSpawn?: boolean;
  clients?: string[];
  /** Manifest root instead of %APPDATA%\Autodesk\Revit\Addins (scratch installs). */
  addinsRoot?: string;
}

export interface SpawnReport {
  era: string;
  ok: boolean;
  initMs?: number;
  protocol?: string;
  tools?: number;
  status?: string;
  error?: string;
  stderr?: string;
}

const MODERN = "2026-07-28";

function regQuery(key: string, value: string): string | null {
  const r = run("reg.exe", ["query", key, "/v", value], { timeoutMs: 10_000 });
  if (!r.ok) return null;
  const line = r.stdout.split(/\r?\n/).find((l) => l.trim().toLowerCase().startsWith(value.toLowerCase()));
  if (!line) return null;
  const parts = line.trim().split(/\s{2,}|\t/);
  return parts[parts.length - 1] ?? null;
}

function revitProcesses(): { pid: number; year?: number }[] {
  const r = run("tasklist.exe", ["/FI", "IMAGENAME eq Revit.exe", "/FO", "CSV", "/NH"], { timeoutMs: 15_000 });
  if (!r.ok) return [];
  const out: { pid: number }[] = [];
  for (const line of r.stdout.split(/\r?\n/)) {
    const m = /^"Revit\.exe","(\d+)"/i.exec(line.trim());
    if (m) out.push({ pid: Number(m[1]) });
  }
  return out;
}

/** <home>\addin\<year>\loader\<id>\RevitMcpNext.Loader.dll -> <home>. */
function homeOfLoader(dll: string): string {
  return dirname(dirname(dirname(dirname(dirname(dll)))));
}

export async function spawnCheck(o: { command: string; args: string[]; env: Record<string, string>; cwd: string; era: "2025-06-18" | "modern"; timeoutMs?: number }): Promise<SpawnReport> {
  const transport = new StdioClientTransport({ command: o.command, args: o.args, env: o.env, cwd: o.cwd, stderr: "pipe" });
  let stderr = "";
  transport.stderr?.on("data", (chunk: Buffer) => {
    stderr = (stderr + chunk.toString()).slice(-4000);
  });
  const options: Record<string, unknown> = { capabilities: {} };
  if (o.era === "modern") options.versionNegotiation = { mode: { pin: MODERN } };
  else options.supportedProtocolVersions = [o.era];
  const client = new Client({ name: o.era === "modern" ? "revit-mcp-doctor" : "codex-mcp-client", version: "doctor" }, options as never);
  const t0 = Date.now();
  try {
    await client.connect(transport, { timeout: o.timeoutMs ?? STARTUP_TIMEOUT_SEC * 1000 });
    const initMs = Date.now() - t0;
    const protocol = (client as unknown as { getNegotiatedProtocolVersion?: () => string }).getNegotiatedProtocolVersion?.();
    const list = await client.listTools(undefined, { timeout: 30_000 });
    const status = await client.callTool({ name: "status", arguments: {} }, { timeout: 60_000 });
    const content = (status.content as { type: string; text?: string }[] | undefined) ?? [];
    const text = content.filter((c) => c.type === "text").map((c) => c.text ?? "").join("\n");
    return { era: o.era, ok: true, initMs, protocol, tools: list.tools.length, status: text.split(/\r?\n/).slice(0, 4).join(" | ").slice(0, 400) };
  } catch (error) {
    return { era: o.era, ok: false, error: (error as Error).message, stderr: stderr.trim().slice(-800) };
  } finally {
    try {
      await client.close();
    } catch {
      // closed
    }
  }
}

export async function runDoctor(o: DoctorOptions): Promise<{ checks: Check[]; spawn: SpawnReport[] }> {
  const checks: Check[] = [];
  const c = (id: string, level: Check["level"], message: string, fix?: string, data?: unknown) => checks.push({ id, level, message, fix, data });
  const home = o.home;
  const reinstall = "run the installer again (installer\\install.ps1)";

  // ---------------------------------------------------------------- node + home
  const major = Number(process.versions.node.split(".")[0]);
  c("node", major >= 24 ? "ok" : "fail", `doctor runs on node ${process.version}`, major >= 24 ? undefined : "use the installed runtime\\node.exe (node 24+)");
  const forbidden = forbiddenHomeReason(home);
  c("home", forbidden ? "fail" : existsSync(markerFile(home)) ? "ok" : "fail", `${home} (from ${o.homeSource})${forbidden ? `: ${forbidden}` : existsSync(markerFile(home)) ? "" : ": no .revit-mcp-next-home marker"}`, forbidden || !existsSync(markerFile(home)) ? reinstall : undefined);
  const receipt = readJsonFile<{ version?: string; gitSha?: string; installedAtUtc?: string; years?: unknown }>(installReceiptFile(home));
  if (receipt.value) c("receipt", "ok", `install.json ${receipt.value.version ?? "?"} (${receipt.value.gitSha ?? "?"}) installed ${receipt.value.installedAtUtc ?? "?"}`);
  else c("receipt", "warn", receipt.error ? `install.json is invalid: ${receipt.error}` : "no install.json (dev install or not installed by the installer)", reinstall);

  const target = launchTarget(home);
  if (existsSync(target.command)) {
    const v = run(target.command, ["--version"], { timeoutMs: 15_000 });
    const nodeMajor = Number(/^v(\d+)/.exec(v.stdout.trim())?.[1] ?? 0);
    c("runtime", v.ok && nodeMajor >= 24 ? "ok" : "fail", `${target.command} ${v.stdout.trim() || v.error || "did not run"}`, v.ok && nodeMajor >= 24 ? undefined : reinstall);
  } else c("runtime", "fail", `${target.command} is missing`, reinstall);
  const info = readJsonFile<{ version?: string; gitSha?: string; builtAt?: string; sdk?: string }>(brokerBuildInfo(home));
  if (existsSync(target.args[0]!)) c("broker", "ok", `${target.args[0]} ${info.value?.version ?? "?"} (${info.value?.gitSha ?? "?"}, SDK ${info.value?.sdk ?? "?"})`);
  else c("broker", "fail", `${target.args[0]} is missing`, reinstall);

  const settingsText = readText(settingsFile(home));
  if (settingsText === null) c("settings", "info", "no config\\settings.json (all defaults; run_csharp off)");
  else {
    try {
      const { settings, issues } = parseSettingsText(settingsText);
      c("settings", issues.length ? "warn" : "ok", `settings.json valid; enableCodeExecution=${settings.enableCodeExecution}${issues.length ? `; ${issues.join("; ")}` : ""}`, issues.length ? "fix the listed keys or rerun the installer (it merges the file)" : undefined);
    } catch (error) {
      c("settings", "fail", `settings.json is not valid JSON (${(error as Error).message}); the add-in keeps its last good values`, "fix the JSON or rerun the installer");
    }
  }
  const authText = readText(authFile(home));
  const token = authText ? parseAuthEnv(authText).token : null;
  c("auth", token ? "ok" : "fail", token ? `auth.env token fp ${authFingerprint(token)}` : `${authFile(home)} is missing or has no token`, token ? undefined : reinstall);

  // ---------------------------------------------------------------- add-in per year
  const procs = revitProcesses();
  const live = await listInstances(home);
  const userEnvHome = regQuery("HKCU\\Environment", "REVIT_MCP_NEXT_HOME");
  if (userEnvHome && !samePath(userEnvHome, home)) {
    c("addin.env", "warn", `the user environment sets REVIT_MCP_NEXT_HOME=${userEnvHome}; Revit started from Explorer loads that home, not ${home}`, "remove the user variable or install into that home");
  }
  for (const year of YEARS) {
    const revitExe = revitExeCandidates(year).find((p) => existsSync(p));
    let manifestPath: string | null = null;
    try {
      manifestPath = o.addinsRoot ? join(o.addinsRoot, String(year), MANIFEST_FILE_NAME) : manifestFile(year);
    } catch {
      manifestPath = null;
    }
    const manifest = manifestPath ? readText(manifestPath) : null;
    const current = readJsonFile<{ payloadId?: string; gitSha?: string; installedAtUtc?: string }>(addinCurrentFile(home, year));
    if (!revitExe && !manifest && !current.value) continue;
    const prefix = `addin.${year}`;
    if (!revitExe) c(prefix, "info", `Revit ${year} is not installed on this PC`);
    if (current.value?.payloadId) {
      const dll = payloadAddinDll(home, year, current.value.payloadId);
      c(`${prefix}.payload`, existsSync(dll) ? "ok" : "fail", `payload ${current.value.payloadId} (${current.value.gitSha || "?"})${existsSync(dll) ? "" : `: ${dll} missing`}`, existsSync(dll) ? undefined : reinstall);
    } else if (revitExe) c(`${prefix}.payload`, "fail", `no ${addinCurrentFile(home, year)}`, reinstall);

    if (!manifest) {
      if (revitExe) c(`${prefix}.manifest`, "fail", `${manifestPath ?? "manifest"} is missing: Revit ${year} does not load the add-in`, reinstall);
    } else {
      const assembly = /<Assembly>([^<]+)<\/Assembly>/i.exec(manifest)?.[1]?.trim() ?? "";
      const cls = /<FullClassName>([^<]+)<\/FullClassName>/i.exec(manifest)?.[1]?.trim() ?? "";
      const clientId = /<ClientId>([^<]+)<\/ClientId>/i.exec(manifest)?.[1]?.trim() ?? "";
      const absolute = /^[a-z]:\\/i.test(assembly) ? assembly : join(dirname(manifestPath!), assembly);
      if (cls !== "RevitMcpNext.Loader.LoaderApplication") {
        c(`${prefix}.manifest`, "fail", `${manifestPath} still points to ${cls || "?"} at ${assembly} (pre-loader install); Revit loads an old add-in that cannot talk to this broker`, reinstall);
      } else if (!existsSync(absolute)) {
        c(`${prefix}.manifest`, "fail", `${manifestPath} points to ${absolute}, which is missing`, reinstall);
      } else {
        const loaderHome = homeOfLoader(absolute);
        const split = !samePath(loaderHome, home) && !userEnvHome;
        c(
          `${prefix}.manifest`,
          split ? "fail" : "ok",
          split ? `split install: the manifest loader belongs to ${loaderHome}, so Revit ${year} uses that home and this broker (${home}) never sees it` : `loader ${absolute}`,
          split ? `install into ${loaderHome}, or rerun the installer for ${home}` : undefined,
        );
      }
      if (clientId.toUpperCase() !== ADDIN_CLIENT_ID) c(`${prefix}.manifest`, "warn", `ClientId ${clientId} differs from ${ADDIN_CLIENT_ID}`);
      const trust = regQuery(`HKCU\\Software\\Autodesk\\Revit\\Autodesk Revit ${year}\\CodeSigning`, ADDIN_CLIENT_ID.toLowerCase());
      c(`${prefix}.trust`, trust && /0x0*1$/i.test(trust) ? "ok" : "info", trust ? `add-in trust value ${trust}` : "no trust entry: Revit asks once whether to load the unsigned add-in (choose Always Load)", trust ? undefined : "or rerun the installer with -TrustRevitAlwaysLoad");
    }
    const log = loaderLogFile(home, year);
    if (existsSync(log)) {
      const age = Date.now() - statSync(log).mtimeMs;
      const last = (readText(log) ?? "").trim().split(/\r?\n/).pop() ?? "";
      if (age < 7 * 24 * 3600_000) c(`${prefix}.loader-log`, "warn", `loader log (${Math.round(age / 60000)} min ago): ${last.slice(0, 300)}`, "the loader failed to load the payload; rerun the installer");
    }
  }
  for (const inst of live) {
    const docs = ((inst.snapshot?.docs as { title?: string }[] | undefined) ?? []).map((d) => d.title).filter(Boolean);
    c(`revit.${inst.instanceId}`, "ok", `Revit ${inst.year} pid ${inst.pid} registered in this home (${docs.length} doc(s)${docs.length ? `: ${docs.slice(0, 5).join(", ")}` : ""})`);
  }
  for (const p of procs) {
    if (!live.some((i) => i.pid === p.pid)) {
      c(`revit.pid${p.pid}`, "warn", `Revit.exe pid ${p.pid} is running but has no registration in ${home}: still starting, the add-in failed to load, or it uses another home`, "wait for Revit to finish starting; otherwise check the loader log and the manifest lines above");
    }
  }
  if (procs.length === 0) c("revit", "info", "no Revit.exe is running (status reports NO_REVIT_RUNNING until one starts)");

  // ---------------------------------------------------------------- Codex
  const clients = o.clients ?? ["codex", "claude", "claude-desktop"];
  let spawnCommand = target.command;
  let spawnArgs = target.args;
  let spawnEnv: Record<string, string> = { REVIT_MCP_NEXT_HOME: home };
  const codexConfig = join(o.codexHome, "config.toml");
  const codexText = clients.includes("codex") ? readText(codexConfig) : null;
  if (clients.includes("codex") && codexText === null) c("codex", "info", `${codexConfig} not found (Codex not installed or never started)`);
  if (codexText !== null) {
    const facts = inspectCodexConfig(codexText);
    const s = facts.server;
    if (!s) c("codex.server", "fail", `[mcp_servers.${SERVER_NAME}] missing in ${codexConfig}`, reinstall);
    else {
      const problems: string[] = [];
      if (!s.command || !existsSync(s.command)) problems.push(`command ${s.command ?? "unset"} does not exist`);
      if (!s.args?.[0] || !existsSync(s.args[0])) problems.push(`args[0] ${s.args?.[0] ?? "unset"} does not exist`);
      if (!samePath(s.env.REVIT_MCP_NEXT_HOME, home)) problems.push(`env.REVIT_MCP_NEXT_HOME is ${s.env.REVIT_MCP_NEXT_HOME ?? "unset"}, not ${home}`);
      if (s.args?.[0] && !isUnder(s.args[0], home)) problems.push(`the bundle ${s.args[0]} is outside ${home}`);
      const tts = Number(s.pairs.tool_timeout_sec ?? 60);
      if (tts < TOOL_TIMEOUT_SEC) problems.push(`tool_timeout_sec ${tts} < ${TOOL_TIMEOUT_SEC}`);
      if (s.pairs.enabled === false) problems.push("enabled = false");
      const notAuto = AUTO_APPROVED_TOOLS.filter((t) => s.tools[t]?.approval_mode !== "auto");
      c("codex.server", problems.length ? "fail" : "ok", problems.length ? problems.join("; ") : `[mcp_servers.${SERVER_NAME}] -> ${s.command} ${s.args?.join(" ")} (required=${s.pairs.required === true}, startup ${s.pairs.startup_timeout_sec ?? "default"} s, tools ${tts} s)`, problems.length ? reinstall : undefined);
      if (notAuto.length) c("codex.approval", "warn", `approval_mode auto missing for ${notAuto.join(", ")}`, reinstall);
      if (s.command) spawnCommand = s.command;
      if (s.args?.length) spawnArgs = s.args;
      spawnEnv = Object.fromEntries(Object.entries(s.env).map(([k, v]) => [k, String(v)]));
    }
    for (const legacy of facts.legacy) c("codex.legacy", "fail", `[mcp_servers.${legacy}] is still enabled: a second (old) broker drives Revit`, reinstall);
    for (const other of facts.otherRevitServers) c("codex.conflict", "warn", `[mcp_servers.${other}] also looks like a Revit server; two servers confuse the model`, `disable it: set enabled = false under [mcp_servers.${other}]`);
    for (const p of facts.conflictingPlugins) {
      if (p.enabled !== false) c("codex.conflict", "warn", `plugin ${p.id} is enabled in Codex; its hooks and skills act on every revit tool call`, "rerun the installer with -DisableConflictingPlugins");
      else c("codex.conflict", "ok", `plugin ${p.id} is disabled${p.ours ? " (by the installer)" : ""}`);
    }
    c("codex.plugin", facts.plugin?.enabled === true ? "ok" : "warn", facts.plugin ? `plugin ${PLUGIN_ID} enabled=${String(facts.plugin.enabled)}${facts.marketplace ? ` from ${facts.marketplace}` : ""}` : `plugin ${PLUGIN_ID} (revit skills) is not installed`, facts.plugin?.enabled === true ? undefined : reinstall);
    const agents = readText(join(o.codexHome, "AGENTS.md"));
    c("codex.agents", agents?.includes(AGENTS_BEGIN) ? "ok" : "info", agents?.includes(AGENTS_BEGIN) ? "AGENTS.md has the Revit snippet" : "AGENTS.md has no Revit snippet (installed with -NoAgentsSnippet?)");
    if (!o.quick) {
      const codexExe = which("codex");
      if (!codexExe) c("codex.cli", "info", "codex CLI not on PATH; skipped `codex mcp get`");
      else {
        const get = run(codexExe, ["mcp", "get", SERVER_NAME, "--json"], { env: { ...process.env, CODEX_HOME: o.codexHome }, timeoutMs: 60_000 });
        if (!get.ok) c("codex.cli", "fail", `codex mcp get ${SERVER_NAME} --json failed: ${(get.stderr || get.error || "").trim().slice(0, 300)}`, reinstall);
        else {
          const problems = verifyCodexGet(get.stdout, { home, command: spawnCommand, args: spawnArgs });
          c("codex.cli", problems.length ? "fail" : "ok", problems.length ? problems.join("; ") : "codex mcp get revit --json parses the block (per-tool approval syntax accepted)", problems.length ? reinstall : undefined);
        }
      }
    }
  }

  // ---------------------------------------------------------------- Codex-exact spawn
  const spawn: SpawnReport[] = [];
  if (!o.noSpawn) {
    const env = codexEnv(process.env, spawnEnv);
    const eras: ("2025-06-18" | "modern")[] = o.quick ? ["2025-06-18"] : ["2025-06-18", "modern"];
    for (const era of eras) {
      if (!existsSync(spawnCommand)) break;
      const r = await spawnCheck({ command: spawnCommand, args: spawnArgs, env, cwd: existsSync(home) ? home : process.cwd(), era });
      spawn.push(r);
      const label = era === "modern" ? "modern 2026-07-28" : "Codex 2025-06-18";
      if (r.ok) {
        const slow = (r.initMs ?? 0) > 5_000;
        c(`spawn.${era === "modern" ? "modern" : "legacy"}`, slow ? "warn" : "ok", `${label}: initialize ${r.initMs} ms (protocol ${r.protocol ?? "?"}), ${r.tools} tools; status: ${r.status}`, slow ? "the broker starts slowly; check antivirus exclusions for the home" : undefined);
      } else c(`spawn.${era === "modern" ? "modern" : "legacy"}`, "fail", `${label}: ${r.error}${r.stderr ? ` | stderr: ${r.stderr}` : ""}`, reinstall);
    }
  }

  // ---------------------------------------------------------------- Claude Code + Desktop
  if (clients.includes("claude")) {
    const cj = readJsonFile<{ mcpServers?: Record<string, { command?: string; args?: string[]; env?: Record<string, string>; disabled?: boolean }> }>(claudeJsonPath());
    const servers = cj.value?.mcpServers ?? {};
    const entry = servers[SERVER_NAME];
    if (!cj.exists) c("claude", "info", `${claudeJsonPath()} not found (Claude Code not installed or never started)`);
    else if (!entry) c("claude.server", "warn", `no user-scope mcpServers.${SERVER_NAME} in ${claudeJsonPath()}`, reinstall);
    else {
      const problems: string[] = [];
      if (!entry.command || !existsSync(entry.command)) problems.push(`command ${entry.command ?? "unset"} does not exist`);
      if (!entry.args?.[0] || !existsSync(entry.args[0])) problems.push(`args[0] ${entry.args?.[0] ?? "unset"} does not exist`);
      if (!samePath(entry.env?.REVIT_MCP_NEXT_HOME, home)) problems.push(`env.REVIT_MCP_NEXT_HOME is ${entry.env?.REVIT_MCP_NEXT_HOME ?? "unset"}`);
      c("claude.server", problems.length ? "fail" : "ok", problems.length ? problems.join("; ") : `user-scope ${SERVER_NAME} -> ${entry.command} ${entry.args?.join(" ")}`, problems.length ? reinstall : undefined);
    }
    for (const name of Object.keys(servers)) {
      if (name !== SERVER_NAME && /revit/i.test(name)) c("claude.conflict", "warn", `user-scope MCP server ${name} also looks like a Revit server`, `claude mcp remove ${name} -s user`);
    }
    const cs = readJsonFile<{ enabledPlugins?: Record<string, boolean> }>(claudeSettingsPath());
    for (const [id, enabled] of Object.entries(cs.value?.enabledPlugins ?? {})) {
      if (/^revit-mcp-cowork@/i.test(id) && enabled) c("claude.conflict", "warn", `plugin ${id} is enabled in Claude Code: its hook auto-approves every revit tool call, including destructive ones`, `claude plugin disable ${id}`);
      if (id === PLUGIN_ID) c("claude.plugin", enabled ? "ok" : "warn", `plugin ${PLUGIN_ID} enabled=${enabled}`);
    }
    if (!o.quick) {
      const claudeExe = which("claude");
      if (claudeExe) {
        const get = run(claudeExe, ["mcp", "get", SERVER_NAME], { timeoutMs: 90_000 });
        const status = /Status:\s*(.+)/i.exec(get.stdout)?.[1]?.trim();
        c("claude.cli", get.ok && /connected/i.test(status ?? "") ? "ok" : "warn", `claude mcp get ${SERVER_NAME}: ${status ?? (get.stderr || get.error || "no status").trim().slice(0, 200)}`);
      }
    }
  }
  if (clients.includes("claude-desktop")) {
    for (const file of claudeDesktopConfigCandidates().filter((p) => existsSync(p))) {
      const cfg = readJsonFile<{ mcpServers?: Record<string, { command?: string; env?: Record<string, string> }> }>(file);
      const entry = cfg.value?.mcpServers?.[SERVER_NAME];
      if (cfg.error) c("claude-desktop", "warn", `${file} is invalid JSON: ${cfg.error}`);
      else if (entry) c("claude-desktop", entry.command && existsSync(entry.command) && samePath(entry.env?.REVIT_MCP_NEXT_HOME, home) ? "ok" : "warn", `${file}: mcpServers.${SERVER_NAME} -> ${entry.command}`);
      else c("claude-desktop", "info", `${file} has no mcpServers.${SERVER_NAME} (install with -Clients codex,claude,claude-desktop to add it)`);
    }
  }

  // ---------------------------------------------------------------- legacy
  const legacy = findLegacyRoots();
  const appdata = process.env.APPDATA;
  for (const year of YEARS) {
    if (!appdata) break;
    const old = join(appdata, "Autodesk", "Revit", "Addins", String(year), "RevitMcpNext");
    if (existsSync(old)) legacy.push(old);
  }
  if (legacy.length) c("legacy", "info", `old install folders (unused): ${legacy.join("; ")}`, "rerun the installer with -RemoveLegacy to delete them");
  return { checks, spawn };
}

export function summarize(checks: Check[]): { ok: boolean; fails: number; warns: number } {
  const fails = checks.filter((c) => c.level === "fail").length;
  return { ok: fails === 0, fails, warns: checks.filter((c) => c.level === "warn").length };
}

export function readJsonSafe(path: string): unknown {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch {
    return null;
  }
}

export function listDir(path: string): string[] {
  try {
    return readdirSync(path);
  } catch {
    return [];
  }
}
