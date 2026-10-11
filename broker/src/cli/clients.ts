// `revit-mcp-cli clients install|uninstall|show` (SPEC §12.2, §12.4-§12.6): registers the installed broker with Codex
// (config.toml text-block surgery, plugin/skill, AGENTS.md snippet, optional cowork disable), Claude Code
// (claude mcp add-json -s user + skills plugin) and Claude Desktop (JSON). Every changed file is backed up first.

import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import {
  backupFile,
  claudeDesktopConfigCandidates,
  claudeJsonPath,
  claudeSettingsPath,
  codexHome as resolveCodexHome,
  flagBool,
  flagList,
  flagString,
  launchTarget,
  readJsonFile,
  readText,
  run,
  samePath,
  timestamp,
  which,
  writeTextAtomic,
  type ParsedArgs,
} from "./common.js";
import { parseToml, readPairs, readValue, removeTables, insertLines, serialize, setTableValue, tableBody, tablesUnder, tomlHeader, tomlString, type TomlDoc } from "./toml.js";

export const SERVER_NAME = "revit";
export const LEGACY_SERVER_NAMES = ["revit-mcp-next"];
export const MARKETPLACE_NAME = "revit-mcp-next";
export const PLUGIN_NAME = "revit-mcp-next";
export const PLUGIN_ID = `${PLUGIN_NAME}@${MARKETPLACE_NAME}`;
/** Tools Codex may call without asking (SPEC §12.2, C36): everything else stays on the user's approval policy. */
export const AUTO_APPROVED_TOOLS = ["status", "help", "set_target", "ui"];
export const CONFLICTING_PLUGIN = /^revit-mcp-cowork@/i;
export const STARTUP_TIMEOUT_SEC = 30;
export const TOOL_TIMEOUT_SEC = 120;
const MANAGED_COMMENT = /^\s*#\s*managed by revit-mcp-next installer\b/i;
const DISABLED_MARK = "disabled by revit-mcp-next installer";
export const AGENTS_BEGIN = "<!-- revit-mcp-next:begin (managed by the revit-mcp-next installer; edits here are replaced) -->";
export const AGENTS_END = "<!-- revit-mcp-next:end -->";

export type ClientName = "codex" | "claude" | "claude-desktop";

export interface Change {
  client: ClientName | "conflicts";
  step: string;
  /** done | skipped | would (dry run) | failed | unchanged | warn */
  result: "done" | "skipped" | "would" | "failed" | "unchanged" | "warn";
  detail: string;
  file?: string;
  backup?: string;
  command?: string;
}

export interface ClientsOptions {
  action: "install" | "uninstall" | "show";
  home: string;
  clients: ClientName[];
  dryRun: boolean;
  codexHome: string;
  agentsSnippet: boolean;
  agentsSnippetFile?: string;
  disableConflictingPlugins: boolean;
  plugins: boolean;
  pluginsDir: string;
  stamp: string;
  version: string;
  sha: string;
  claudeDesktopConfig?: string;
  codexExe?: string | null;
  claudeExe?: string | null;
  /** Node/bundle to register (defaults to the home's runtime\node.exe and broker\revit-mcp.mjs). */
  command?: string;
  args?: string[];
}

// ------------------------------------------------------------------------------------------------ Codex block

export function codexBlock(o: { home: string; command: string; args: string[]; version: string; sha: string }): string[] {
  const lines = [
    `# managed by revit-mcp-next installer ${o.version}${o.sha ? ` (${o.sha})` : ""}; rerun the installer instead of editing`,
    `[mcp_servers.${SERVER_NAME}]`,
    `command = ${tomlString(o.command)}`,
    `args = [${o.args.map(tomlString).join(", ")}]`,
    `startup_timeout_sec = ${STARTUP_TIMEOUT_SEC}`,
    `tool_timeout_sec = ${TOOL_TIMEOUT_SEC}`,
    `required = true`,
    ``,
    `[mcp_servers.${SERVER_NAME}.env]`,
    `REVIT_MCP_NEXT_HOME = ${tomlString(o.home)}`,
  ];
  for (const tool of AUTO_APPROVED_TOOLS) {
    lines.push(``, `[mcp_servers.${SERVER_NAME}.tools.${tool}]`, `approval_mode = "auto"`);
  }
  return lines;
}

const isOurServerTable = (path: string[]) => path[0] === "mcp_servers" && (path[1] === SERVER_NAME || LEGACY_SERVER_NAMES.includes(path[1] ?? ""));

/** Pure text transform of config.toml for install/uninstall. */
export function transformCodexConfig(
  text: string,
  o: { install: boolean; block?: string[]; disableConflicting?: boolean; restoreConflicting?: boolean; marketplace?: { dir: string } | null; removeMarketplace?: boolean },
): { text: string; notes: string[] } {
  const doc = parseToml(text);
  const notes: string[] = [];
  // Inline definitions ([mcp_servers] revit = {...} or dotted keys) cannot be edited safely as text.
  const inline = tablesUnder(doc, ["mcp_servers"]).filter((t) => t.path.length === 1);
  for (const t of inline) {
    if (tableBody(doc, t).some((l) => new RegExp(`^\\s*("?)(${SERVER_NAME}|revit-mcp-next)\\1\\s*[.=]`).test(l))) {
      throw new Error(`config.toml defines mcp_servers.${SERVER_NAME} inline under [mcp_servers]; move it to its own [mcp_servers.${SERVER_NAME}] table or remove it, then rerun`);
    }
  }
  const { removed, at } = removeTables(doc, (t) => isOurServerTable(t.path), MANAGED_COMMENT);
  if (removed.length) notes.push(`removed ${[...new Set(removed.map((n) => `[${n}]`))].join(", ")}`);
  if (o.install && o.block) {
    insertLines(doc, at, o.block);
    notes.push(`wrote [mcp_servers.${SERVER_NAME}] (+ env, ${AUTO_APPROVED_TOOLS.length} auto-approved tools)`);
  }
  if (o.marketplace) {
    const name = ["marketplaces", MARKETPLACE_NAME];
    const existing = doc.tables.find((t) => t.path.join(".") === name.join("."));
    if (!existing) {
      insertLines(doc, -1, [`[${tomlHeader(name)}]`, `source_type = "local"`, `source = ${tomlString(o.marketplace.dir)}`]);
      notes.push(`added [marketplaces.${MARKETPLACE_NAME}]`);
    } else {
      setTableValue(doc, existing, "source", tomlString(o.marketplace.dir));
    }
    const pluginPath = ["plugins", PLUGIN_ID];
    const plugin = doc.tables.find((t) => t.path.join(".") === pluginPath.join("."));
    if (!plugin) {
      insertLines(doc, -1, [`[${tomlHeader(pluginPath)}]`, `enabled = true`]);
      notes.push(`enabled [plugins."${PLUGIN_ID}"]`);
    } else if (readValue(tableBody(doc, plugin), "enabled") !== true) {
      setTableValue(doc, plugin, "enabled", "true");
      notes.push(`enabled [plugins."${PLUGIN_ID}"]`);
    }
  }
  if (o.removeMarketplace) {
    const r = removeTables(doc, (t) => t.path.join(".") === `marketplaces.${MARKETPLACE_NAME}` || t.path.join(".") === `plugins.${PLUGIN_ID}`, MANAGED_COMMENT);
    if (r.removed.length) notes.push(`removed ${r.removed.map((n) => `[${n}]`).join(", ")}`);
  }
  for (const table of doc.tables.filter((t) => t.path[0] === "plugins" && CONFLICTING_PLUGIN.test(t.path[1] ?? ""))) {
    const body = tableBody(doc, table);
    const enabled = readValue(body, "enabled");
    const ours = body.some((l) => l.includes(DISABLED_MARK));
    if (o.disableConflicting && enabled !== false) {
      setTableValue(doc, table, "enabled", "false", DISABLED_MARK);
      notes.push(`disabled [plugins."${table.path[1]}"] (was ${enabled === undefined ? "unset" : String(enabled)})`);
    } else if (o.restoreConflicting && ours) {
      setTableValue(doc, table, "enabled", "true");
      notes.push(`re-enabled [plugins."${table.path[1]}"] (the installer had disabled it)`);
    }
  }
  return { text: serialize(doc), notes };
}

/** Facts the doctor needs from config.toml. */
export function inspectCodexConfig(text: string): {
  server: { command?: string; args?: string[]; env: Record<string, string>; pairs: Record<string, unknown>; tools: Record<string, Record<string, unknown>> } | null;
  legacy: string[];
  otherRevitServers: string[];
  conflictingPlugins: { id: string; enabled: boolean | undefined; ours: boolean }[];
  plugin: { enabled: boolean | undefined } | null;
  marketplace: string | null;
  inlineIssue: boolean;
} {
  const doc = parseToml(text);
  const find = (name: string) => doc.tables.find((t) => t.name === name);
  const main = find(`mcp_servers.${SERVER_NAME}`);
  let server = null;
  if (main) {
    const pairs = readPairs(tableBody(doc, main));
    const envTable = find(`mcp_servers.${SERVER_NAME}.env`);
    const env = envTable ? (readPairs(tableBody(doc, envTable)) as Record<string, string>) : {};
    const tools: Record<string, Record<string, unknown>> = {};
    for (const t of tablesUnder(doc, ["mcp_servers", SERVER_NAME, "tools"])) if (t.path.length === 4) tools[t.path[3]!] = readPairs(tableBody(doc, t));
    server = { command: pairs.command as string | undefined, args: pairs.args as string[] | undefined, env, pairs, tools };
  }
  const servers = [...new Set(tablesUnder(doc, ["mcp_servers"]).filter((t) => t.path.length === 2).map((t) => t.path[1]!))];
  const disabled = (name: string) => {
    const t = find(`mcp_servers.${name}`);
    return t ? readValue(tableBody(doc, t), "enabled") === false : false;
  };
  const plugins = doc.tables.filter((t) => t.path[0] === "plugins" && t.path.length === 2);
  const pluginTable = plugins.find((t) => t.path[1] === PLUGIN_ID);
  const market = find(`marketplaces.${MARKETPLACE_NAME}`);
  return {
    server,
    legacy: servers.filter((s) => LEGACY_SERVER_NAMES.includes(s) && !disabled(s)),
    otherRevitServers: servers.filter((s) => /revit/i.test(s) && s !== SERVER_NAME && !LEGACY_SERVER_NAMES.includes(s) && !disabled(s)),
    conflictingPlugins: plugins
      .filter((t) => CONFLICTING_PLUGIN.test(t.path[1]!))
      .map((t) => ({ id: t.path[1]!, enabled: readValue(tableBody(doc, t), "enabled") as boolean | undefined, ours: tableBody(doc, t).some((l) => l.includes(DISABLED_MARK)) })),
    plugin: pluginTable ? { enabled: readValue(tableBody(doc, pluginTable), "enabled") as boolean | undefined } : null,
    marketplace: market ? ((readValue(tableBody(doc, market), "source") as string | undefined) ?? null) : null,
    inlineIssue: false,
  };
}

// ------------------------------------------------------------------------------------------------ AGENTS.md

/** Same text as installer/agents-snippet.md (docs/skill-eval.md "AGENTS snippet", < 700 chars); used when that file is missing. */
export const DEFAULT_AGENTS_SNIPPET = [
  "## Revit",
  "Use only the `revit` MCP tools for Revit and follow the revit skill. Never use screen automation, screenshots or clicks on Revit. Start with status. Do exactly what each next:/fix: line says; \"ask the user\" means stop and ask. If a result says NOT APPLIED, show the user the plan and ask; call again with confirm only when the user asked for exactly this or approves. Never repeat a write that is running or unknown; call job_status. run_csharp only when no tool fits and the user agreed; never edit ~\\.revit-mcp-next\\config\\settings.json or enable code execution yourself. If the revit tools are missing, tell the user to start a new thread and run the revit-mcp-next doctor.",
].join("\n");

export function loadAgentsSnippet(file?: string): string {
  if (file) {
    const text = readText(file);
    if (text && text.trim()) return text.trim();
  }
  return DEFAULT_AGENTS_SNIPPET;
}

export function transformAgents(text: string | null, snippet: string | null): string | null {
  const body = text ?? "";
  const eol = body.includes("\r\n") ? "\r\n" : "\n";
  const begin = body.indexOf(AGENTS_BEGIN);
  const end = body.indexOf(AGENTS_END);
  let without = body;
  if (begin >= 0 && end > begin) {
    const head = body.slice(0, begin).replace(/(\r?\n)+$/, "");
    const tail = body.slice(end + AGENTS_END.length).replace(/^(\r?\n)+/, "");
    without = head && tail ? head + eol + eol + tail : head ? head + eol : tail;
    if (!without.trim()) without = "";
  }
  if (snippet === null) return without;
  const block = [AGENTS_BEGIN, ...snippet.split(/\r?\n/), AGENTS_END].join(eol);
  const base = without.replace(/(\r?\n)+$/, "");
  return (base ? base + eol + eol : "") + block + eol;
}

// ------------------------------------------------------------------------------------------------ runner

function cmdLine(exe: string, args: string[]): string {
  return [exe.includes(" ") ? `"${exe}"` : exe, ...args.map((a) => (/[\s"{}]/.test(a) ? `'${a}'` : a))].join(" ");
}

export function runClients(o: ClientsOptions): { changes: Change[]; ok: boolean } {
  const changes: Change[] = [];
  const add = (c: Change) => changes.push(c);
  const target = launchTarget(o.home);
  const command = o.command ?? target.command;
  const args = o.args ?? target.args;
  const install = o.action === "install";

  for (const client of o.clients) {
    try {
      if (client === "codex") codexClient(o, install, command, args, add);
      else if (client === "claude") claudeClient(o, install, command, args, add);
      else if (client === "claude-desktop") claudeDesktopClient(o, install, command, args, add);
    } catch (error) {
      add({ client, step: "error", result: "failed", detail: (error as Error).message });
    }
  }
  return { changes, ok: !changes.some((c) => c.result === "failed") };
}

function codexClient(o: ClientsOptions, install: boolean, command: string, args: string[], add: (c: Change) => void): void {
  const home = o.codexHome;
  const config = join(home, "config.toml");
  const codexExe = o.codexExe === undefined ? which("codex") : o.codexExe;
  if (!existsSync(home) && !codexExe) {
    add({ client: "codex", step: "detect", result: "skipped", detail: `Codex not found (no ${home} and no codex on PATH)` });
    return;
  }
  const env = { ...process.env, CODEX_HOME: home };
  // In a dry run the installer has not copied the plugins yet: assume it will.
  const pluginReady = o.plugins && (o.dryRun || existsSync(join(o.pluginsDir, ".agents", "plugins", "marketplace.json")));
  if (o.plugins && !pluginReady && install) add({ client: "codex", step: "plugin", result: "warn", detail: `${join(o.pluginsDir, ".agents", "plugins", "marketplace.json")} missing; plugin/skill not registered` });

  if (o.dryRun) {
    const before = readText(config) ?? "";
    const { text: after, notes } = transformCodexConfig(before, {
      install,
      block: install ? codexBlock({ home: o.home, command, args, version: o.version, sha: o.sha }) : undefined,
      disableConflicting: install && o.disableConflictingPlugins,
      restoreConflicting: !install,
    });
    if (after === before) add({ client: "codex", step: "config.toml", result: "unchanged", file: config, detail: "already up to date" });
    else add({ client: "codex", step: "config.toml", result: "would", file: config, detail: notes.join("; ") });
    if (pluginReady || (!install && o.plugins)) {
      const cmds = install
        ? [`codex plugin marketplace add ${o.pluginsDir}`, `codex plugin add ${PLUGIN_ID}`]
        : [`codex plugin remove ${PLUGIN_ID}`, `codex plugin marketplace remove ${MARKETPLACE_NAME}`];
      add({ client: "codex", step: "plugin", result: "would", detail: cmds.join(" && ") });
    }
    if (o.agentsSnippet || !install) {
      add({ client: "codex", step: "AGENTS.md", result: "would", file: join(home, "AGENTS.md"), detail: install ? "insert/replace the Revit snippet between markers" : "remove the Revit snippet" });
    }
    return;
  }

  const backup = backupFile(config, o.stamp);
  if (backup) add({ client: "codex", step: "backup", result: "done", file: config, backup, detail: `backed up config.toml` });

  // Plugin/skill through the Codex CLI (keeps Codex's own plugin cache consistent); text fallback without the CLI.
  let pluginByText = false;
  if (install && pluginReady) {
    if (codexExe) {
      const current = readText(config);
      const facts = current ? inspectCodexConfig(current) : null;
      if (facts?.marketplace && !samePath(facts.marketplace, o.pluginsDir)) {
        const rm = run(codexExe, ["plugin", "marketplace", "remove", MARKETPLACE_NAME], { env });
        add({ client: "codex", step: "plugin", result: rm.ok ? "done" : "warn", command: rm.command, detail: rm.ok ? "removed the old marketplace source" : (rm.stderr || rm.error || "").trim().slice(0, 300) });
      }
      if (!facts?.marketplace || !samePath(facts.marketplace, o.pluginsDir)) {
        const mk = run(codexExe, ["plugin", "marketplace", "add", o.pluginsDir], { env });
        add({ client: "codex", step: "plugin", result: mk.ok ? "done" : "warn", command: mk.command, detail: mk.ok ? "marketplace added" : (mk.stderr || mk.error || "").trim().slice(0, 300) });
        if (!mk.ok) pluginByText = true;
      }
      // Remove first so an updated skill replaces the cached copy of the same version.
      run(codexExe, ["plugin", "remove", PLUGIN_ID], { env });
      const pa = run(codexExe, ["plugin", "add", PLUGIN_ID], { env });
      add({ client: "codex", step: "plugin", result: pa.ok ? "done" : "warn", command: pa.command, detail: pa.ok ? `installed ${PLUGIN_ID}` : (pa.stderr || pa.error || "").trim().slice(0, 300) });
      if (!pa.ok) pluginByText = true;
    } else pluginByText = true;
  } else if (!install && o.plugins && codexExe) {
    const pr = run(codexExe, ["plugin", "remove", PLUGIN_ID], { env });
    add({ client: "codex", step: "plugin", result: pr.ok ? "done" : "unchanged", command: pr.command, detail: pr.ok ? `removed ${PLUGIN_ID}` : "plugin not installed" });
    const mr = run(codexExe, ["plugin", "marketplace", "remove", MARKETPLACE_NAME], { env });
    add({ client: "codex", step: "plugin", result: mr.ok ? "done" : "unchanged", command: mr.command, detail: mr.ok ? `removed marketplace ${MARKETPLACE_NAME}` : "marketplace not configured" });
  }

  const before = readText(config) ?? "";
  const { text, notes } = transformCodexConfig(before, {
    install,
    block: install ? codexBlock({ home: o.home, command, args, version: o.version, sha: o.sha }) : undefined,
    disableConflicting: install && o.disableConflictingPlugins,
    restoreConflicting: !install,
    marketplace: pluginByText ? { dir: o.pluginsDir } : null,
    removeMarketplace: !install && o.plugins,
  });
  if (text !== before) {
    writeTextAtomic(config, text);
    add({ client: "codex", step: "config.toml", result: "done", file: config, detail: notes.join("; ") });
  } else add({ client: "codex", step: "config.toml", result: "unchanged", file: config, detail: "already up to date" });
  if (pluginByText) add({ client: "codex", step: "plugin", result: "warn", detail: `codex CLI unavailable or failed: wrote [marketplaces.${MARKETPLACE_NAME}] and [plugins."${PLUGIN_ID}"] directly; Codex installs the plugin at its next start` });

  // AGENTS.md (reported in the installer summary; -NoAgentsSnippet skips it).
  const agents = join(home, "AGENTS.md");
  if (install && o.agentsSnippet) {
    const current = readText(agents);
    const next = transformAgents(current, loadAgentsSnippet(o.agentsSnippetFile));
    if (next !== current) {
      const b = backupFile(agents, o.stamp);
      writeTextAtomic(agents, next ?? "");
      add({ client: "codex", step: "AGENTS.md", result: "done", file: agents, backup: b ?? undefined, detail: current?.includes(AGENTS_BEGIN) ? "replaced the Revit snippet" : "added the Revit snippet" });
    } else add({ client: "codex", step: "AGENTS.md", result: "unchanged", file: agents, detail: "snippet already current" });
  } else if (!install) {
    const current = readText(agents);
    if (current?.includes(AGENTS_BEGIN)) {
      const b = backupFile(agents, o.stamp);
      writeTextAtomic(agents, transformAgents(current, null) ?? "");
      add({ client: "codex", step: "AGENTS.md", result: "done", file: agents, backup: b ?? undefined, detail: "removed the Revit snippet" });
    }
  }

  // V22: let Codex itself parse the block.
  if (install && codexExe) {
    const get = run(codexExe, ["mcp", "get", SERVER_NAME, "--json"], { env, timeoutMs: 60_000 });
    if (!get.ok) add({ client: "codex", step: "verify", result: "failed", command: get.command, detail: `codex mcp get ${SERVER_NAME} failed: ${(get.stderr || get.error || "").trim().slice(0, 400)}` });
    else {
      const problems = verifyCodexGet(get.stdout, { home: o.home, command, args });
      add({ client: "codex", step: "verify", result: problems.length ? "failed" : "done", command: get.command, detail: problems.length ? problems.join("; ") : "codex mcp get revit --json matches the block" });
    }
  }
}

/** Compare `codex mcp get revit --json` with what we wrote (V22). */
export function verifyCodexGet(stdout: string, o: { home: string; command: string; args: string[] }): string[] {
  const problems: string[] = [];
  let parsed: Record<string, unknown>;
  try {
    parsed = JSON.parse(stdout.slice(stdout.indexOf("{")));
  } catch {
    return [`unparseable output: ${stdout.slice(0, 200)}`];
  }
  const transport = (parsed.transport ?? parsed) as Record<string, unknown>;
  if (!samePath(String(transport.command ?? ""), o.command)) problems.push(`command is ${String(transport.command)}`);
  const gotArgs = (transport.args as string[] | undefined) ?? [];
  if (gotArgs.length !== o.args.length || gotArgs.some((a, i) => !samePath(a, o.args[i]!))) problems.push(`args are ${JSON.stringify(gotArgs)}`);
  const env = (transport.env ?? parsed.env ?? {}) as Record<string, string>;
  if (!samePath(env.REVIT_MCP_NEXT_HOME, o.home)) problems.push(`env.REVIT_MCP_NEXT_HOME is ${env.REVIT_MCP_NEXT_HOME ?? "missing"}`);
  if (parsed.enabled === false) problems.push("enabled is false");
  const tools = (parsed.tools ?? {}) as Record<string, { approval_mode?: string }>;
  const autos = AUTO_APPROVED_TOOLS.filter((t) => tools[t]?.approval_mode !== "auto");
  if (Object.keys(tools).length > 0 && autos.length) problems.push(`approval_mode auto missing for ${autos.join(", ")}`);
  return problems;
}

function claudeClient(o: ClientsOptions, install: boolean, command: string, args: string[], add: (c: Change) => void): void {
  const claude = o.claudeExe === undefined ? which("claude") : o.claudeExe;
  const json = JSON.stringify({ type: "stdio", command, args, env: { REVIT_MCP_NEXT_HOME: o.home } });
  const pluginReady = o.plugins && (o.dryRun || existsSync(join(o.pluginsDir, ".claude-plugin", "marketplace.json")));
  if (!claude) {
    add({
      client: "claude",
      step: "detect",
      result: "skipped",
      detail: install ? `claude CLI not found; to register by hand run: claude mcp add-json ${SERVER_NAME} '${json}' -s user` : "claude CLI not found",
    });
    return;
  }
  const current = readJsonFile<{ mcpServers?: Record<string, { command?: string; args?: string[]; env?: Record<string, string> }> }>(claudeJsonPath()).value?.mcpServers?.[SERVER_NAME];
  const same = !!current && samePath(current.command, command) && (current.args ?? []).length === args.length && args.every((a, i) => samePath(current.args?.[i], a)) && samePath(current.env?.REVIT_MCP_NEXT_HOME, o.home);
  if (install && same) add({ client: "claude", step: "mcp add-json", result: "unchanged", file: claudeJsonPath(), detail: `user-scope ${SERVER_NAME} already points to this home` });
  const cmds: string[][] = install
    ? [
        ...(same ? [] : [["mcp", "remove", SERVER_NAME, "-s", "user"], ["mcp", "add-json", SERVER_NAME, json, "-s", "user"]]),
        ...(pluginReady ? [["plugin", "marketplace", "remove", MARKETPLACE_NAME], ["plugin", "marketplace", "add", o.pluginsDir], ["plugin", "install", PLUGIN_ID, "-s", "user"]] : []),
      ]
    : [["mcp", "remove", SERVER_NAME, "-s", "user"], ...(o.plugins ? [["plugin", "uninstall", PLUGIN_ID, "-s", "user"], ["plugin", "marketplace", "remove", MARKETPLACE_NAME]] : [])];
  if (o.dryRun) {
    add({ client: "claude", step: "commands", result: "would", detail: cmds.map((c) => cmdLine("claude", c)).join(" && ") });
    return;
  }
  for (const file of [claudeJsonPath(), claudeSettingsPath()]) {
    const b = backupFile(file, o.stamp);
    if (b) add({ client: "claude", step: "backup", result: "done", file, backup: b, detail: `backed up ${file}` });
  }
  for (const c of cmds) {
    const optional = (c[0] === "mcp" && c[1] === "remove" && install) || (c[0] === "plugin" && c[1] === "marketplace" && c[2] === "remove" && install);
    const r = run(claude, c, { timeoutMs: 180_000 });
    const what = c.slice(0, c[0] === "plugin" && c[1] === "marketplace" ? 3 : 2).join(" ");
    if (r.ok) add({ client: "claude", step: what, result: "done", command: cmdLine("claude", c), detail: (r.stdout || "ok").trim().split(/\r?\n/)[0]!.slice(0, 200) });
    else if (optional || !install) add({ client: "claude", step: what, result: "unchanged", command: cmdLine("claude", c), detail: "nothing to remove" });
    else add({ client: "claude", step: what, result: c[0] === "plugin" ? "warn" : "failed", command: cmdLine("claude", c), detail: (r.stderr || r.stdout || r.error || "").trim().slice(0, 400) });
  }
  if (install) {
    const facts = readJsonFile<{ mcpServers?: Record<string, { command?: string; args?: string[]; env?: Record<string, string> }> }>(claudeJsonPath());
    const entry = facts.value?.mcpServers?.[SERVER_NAME];
    const good = entry && samePath(entry.command, command) && samePath(entry.args?.[0], args[0]) && samePath(entry.env?.REVIT_MCP_NEXT_HOME, o.home);
    add({ client: "claude", step: "verify", result: good ? "done" : "failed", file: claudeJsonPath(), detail: good ? `${claudeJsonPath()} mcpServers.${SERVER_NAME} matches` : `mcpServers.${SERVER_NAME} in ${claudeJsonPath()} does not match` });
  }
}

function claudeDesktopClient(o: ClientsOptions, install: boolean, command: string, args: string[], add: (c: Change) => void): void {
  const candidates = o.claudeDesktopConfig ? [o.claudeDesktopConfig] : claudeDesktopConfigCandidates();
  const targets = candidates.filter((p) => existsSync(dirname(p)));
  if (targets.length === 0) {
    add({ client: "claude-desktop", step: "detect", result: "skipped", detail: "Claude Desktop config folder not found" });
    return;
  }
  for (const file of targets) {
    const read = readJsonFile<Record<string, unknown>>(file);
    if (read.error) {
      add({ client: "claude-desktop", step: "config", result: "failed", file, detail: `invalid JSON (${read.error}); fix the file and rerun` });
      continue;
    }
    const data = (read.value ?? {}) as { mcpServers?: Record<string, unknown> };
    const servers = { ...(data.mcpServers ?? {}) };
    const before = JSON.stringify(servers[SERVER_NAME] ?? null);
    if (install) servers[SERVER_NAME] = { command, args, env: { REVIT_MCP_NEXT_HOME: o.home } };
    else delete servers[SERVER_NAME];
    for (const legacy of LEGACY_SERVER_NAMES) delete servers[legacy];
    const changed = before !== JSON.stringify(servers[SERVER_NAME] ?? null) || LEGACY_SERVER_NAMES.some((n) => (data.mcpServers ?? {})[n] !== undefined);
    if (!changed) {
      add({ client: "claude-desktop", step: "config", result: "unchanged", file, detail: "already up to date" });
      continue;
    }
    if (o.dryRun) {
      add({ client: "claude-desktop", step: "config", result: "would", file, detail: install ? `set mcpServers.${SERVER_NAME}` : `remove mcpServers.${SERVER_NAME}` });
      continue;
    }
    const b = backupFile(file, o.stamp);
    writeTextAtomic(file, JSON.stringify({ ...data, mcpServers: servers }, null, 2) + "\n");
    add({ client: "claude-desktop", step: "config", result: "done", file, backup: b ?? undefined, detail: install ? `set mcpServers.${SERVER_NAME} (restart Claude Desktop)` : `removed mcpServers.${SERVER_NAME}` });
  }
}

// ------------------------------------------------------------------------------------------------ CLI entry

export function clientsCommand(args: ParsedArgs, home: string, build: { version: string; gitSha: string }): number {
  const action = (args._[1] ?? "show") as ClientsOptions["action"];
  if (!["install", "uninstall", "show"].includes(action)) {
    process.stderr.write("usage: revit-mcp-cli clients install|uninstall|show [--clients codex,claude,claude-desktop] [--dry-run] [--json]\n");
    return 2;
  }
  const clients = (flagList(args, "clients") ?? ["codex", "claude"]).filter((c): c is ClientName => c === "codex" || c === "claude" || c === "claude-desktop");
  const o: ClientsOptions = {
    action,
    home,
    clients,
    dryRun: flagBool(args, "dry-run") || action === "show",
    codexHome: resolveCodexHome(process.env, flagString(args, "codex-home")),
    agentsSnippet: !flagBool(args, "no-agents-snippet"),
    agentsSnippetFile: flagString(args, "agents-snippet"),
    disableConflictingPlugins: flagBool(args, "disable-conflicting-plugins"),
    plugins: !flagBool(args, "no-plugins"),
    pluginsDir: flagString(args, "plugins-dir") ?? join(home, "plugins"),
    stamp: flagString(args, "stamp") ?? timestamp(),
    version: build.version,
    sha: build.gitSha,
    claudeDesktopConfig: flagString(args, "claude-desktop-config"),
    command: flagString(args, "command"),
    args: flagString(args, "bundle") ? [flagString(args, "bundle")!] : undefined,
  };
  if (action === "show") {
    const target = launchTarget(home);
    const block = codexBlock({ home, command: o.command ?? target.command, args: o.args ?? target.args, version: o.version, sha: o.sha });
    const json = JSON.stringify({ type: "stdio", command: o.command ?? target.command, args: o.args ?? target.args, env: { REVIT_MCP_NEXT_HOME: home } });
    if (flagBool(args, "json")) process.stdout.write(JSON.stringify({ codexToml: block.join("\n"), claudeJson: JSON.parse(json) }, null, 2) + "\n");
    else process.stdout.write(`# Codex (${join(o.codexHome, "config.toml")})\n${block.join("\n")}\n\n# Claude Code\nclaude mcp add-json ${SERVER_NAME} '${json}' -s user\n`);
    return 0;
  }
  const { changes, ok } = runClients(o);
  if (flagBool(args, "json")) process.stdout.write(JSON.stringify({ ok, action, dryRun: o.dryRun, changes }, null, 2) + "\n");
  else {
    for (const c of changes) {
      process.stdout.write(`[${c.client}] ${c.step}: ${c.result} - ${c.detail}${c.backup ? ` (backup ${c.backup})` : ""}\n`);
    }
  }
  return ok ? 0 : 1;
}

export function readFileIfExists(path: string): string | null {
  try {
    return readFileSync(path, "utf8");
  } catch {
    return null;
  }
}

export type { TomlDoc };
