#!/usr/bin/env node
// Broker bootstrap (D2 §14.1 order): console → stderr, crash guards, home, settings, logger, registry, in-flight
// recovery (async), serveStdio(factory, {legacy:"serve", onerror}), idempotent shutdown within 2 s of stdin EOF.
// Startup touches nothing but the home: initialize must stay fast. Frozen in wave 2.

import { redirectConsoleToStderr, installCrashGuards } from "./runtime/crashGuards.js";

redirectConsoleToStderr();

import { serveStdio } from "@modelcontextprotocol/server/stdio";
import { isTerminalLedgerState, type LedgerEntry } from "@revit-mcp-next/contracts/protocol";
import { ToolRegistry } from "./framework/registry.js";
import type { BrokerServices } from "./framework/types.js";
import { ChannelPool } from "./instances/InstanceChannel.js";
import { InstanceRegistry } from "./instances/InstanceRegistry.js";
import { ResultCache } from "./jobs/ResultCache.js";
import { AuthStore } from "./runtime/auth.js";
import { readBuildInfo, resolveBrokerHome } from "./runtime/home.js";
import { BrokerLog } from "./runtime/log.js";
import { SettingsStore } from "./runtime/settings.js";
import { createServerFactory } from "./server.js";
import { InflightJournal } from "./state/InflightJournal.js";
import { TargetResolver } from "./targeting/TargetResolver.js";
import { ALL_TOOL_MODULES } from "./tools/index.js";

let log: BrokerLog | null = null;
let shuttingDown: Promise<void> | null = null;
let stdioHandle: { close(): Promise<void> } | null = null;
let services: BrokerServices | null = null;

function shutdown(code = 0): Promise<void> {
  if (shuttingDown) return shuttingDown;
  shuttingDown = (async () => {
    const force = setTimeout(() => process.exit(code), 2_000);
    force.unref();
    try {
      services?.channels.retireAll();
      log?.info("shutdown", { code });
      await Promise.race([
        Promise.allSettled([stdioHandle?.close(), log?.close()]),
        new Promise((resolve) => setTimeout(resolve, 1_500).unref()),
      ]);
    } catch {
      // shutting down anyway
    }
    process.exit(code);
  })();
  return shuttingDown;
}

installCrashGuards({ log: () => log, exit: (code) => void shutdown(code) });

const homeInfo = resolveBrokerHome();
const settings = new SettingsStore(homeInfo.home);
const startSettings = settings.atStart.settings;
log = new BrokerLog(homeInfo.home, startSettings.log);
const build = readBuildInfo();
const auth = new AuthStore(homeInfo.home);
const registry = new InstanceRegistry(homeInfo.home, log);
const channels = new ChannelPool({ home: homeInfo.home, auth, settings, log, registry });
const resolver = new TargetResolver(registry, channels, log);
const results = new ResultCache();
const journal = new InflightJournal(homeInfo.home, log);
const profile = (process.env.REVIT_MCP_NEXT_PROFILE ?? "").trim().toLowerCase() === "core" ? "core" : "full";
const structured = (process.env.REVIT_MCP_NEXT_STRUCTURED ?? "").trim() === "1";
const codeExecution = startSettings.enableCodeExecution === true;
const tools = new ToolRegistry(ALL_TOOL_MODULES, { profile, codeExecution });

services = {
  home: homeInfo.home,
  homeSource: homeInfo.source,
  homeWarning: homeInfo.warning,
  homeInstalled: homeInfo.installed,
  build,
  startedAt: Date.now(),
  profile,
  structured,
  codeExecutionListed: codeExecution,
  settings,
  auth,
  log,
  registry,
  channels,
  resolver,
  results,
  journal,
  tools,
};

log.info("start", { version: build.version, gitSha: build.gitSha, node: process.version, home: homeInfo.home, homeSource: homeInfo.source, profile, structured, codeExecution, problems: tools.problems });
for (const problem of tools.problems) process.stderr.write(`revit-mcp-next: ${problem}\n`);

stdioHandle = serveStdio(createServerFactory(services), {
  legacy: "serve",
  onerror: (error) => log?.warn("stdio", { error }),
});

process.stdin.once("end", () => void shutdown(0));
process.stdin.once("close", () => void shutdown(0));
process.once("SIGINT", () => void shutdown(0));
process.once("SIGTERM", () => void shutdown(0));

// In-flight write recovery of dead brokers (async, never blocks initialize).
setTimeout(() => {
  void journal
    .recover(async (instanceId, requestId) => {
      const info = await registry.find(instanceId);
      if (!info) return null;
      const r = await channels.get(info).control("get_request_result", { requestId }, 800);
      const entry = r.ok ? (r.data as LedgerEntry | null) : null;
      return entry && isTerminalLedgerState(entry.state) ? entry : null;
    })
    .catch((error) => log?.warn("recover", { error }));
}, 50).unref();
