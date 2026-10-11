#!/usr/bin/env node
// revit-mcp-cli (installed as <home>\broker\revit-mcp-cli.mjs; run with <home>\runtime\node.exe):
//   doctor   [--quick] [--json] [--codex-home <dir>] [--clients codex,claude,claude-desktop]
//   clients  install|uninstall|show [--clients ...] [--dry-run] [--no-agents-snippet] [--disable-conflicting-plugins] [--json]
//   settings merge|show [--enable-code-execution|--disable-code-execution] [--dry-run] [--json]
//   raw      <op> [--instance <id>|--year <y>] [--args <json>] [--doc <json>] [--kind <k>] [--timeout <ms>]
//   support  [--out <dir>] [--full]
//   version
// Every command takes --home <dir> (default: env REVIT_MCP_NEXT_HOME, else the home this file is installed in, else
// %USERPROFILE%\.revit-mcp-next).

import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { readBuildInfo } from "../runtime/home.js";
import { clientsCommand } from "./clients.js";
import { cliHome, codexHome, flagBool, flagList, flagString, parseArgs, printChecks } from "./common.js";
import { runDoctor, summarize } from "./doctor.js";
import { rawCommand } from "./raw.js";
import { settingsCommand } from "./settings.js";
import { supportCommand } from "./support.js";

const BOOLEAN_FLAGS = [
  "json", "quick", "full", "dry-run", "no-agents-snippet", "disable-conflicting-plugins", "no-plugins", "enable-code-execution",
  "disable-code-execution", "control", "compact", "no-spawn", "help",
];

async function main(): Promise<number> {
  const args = parseArgs(process.argv.slice(2), BOOLEAN_FLAGS);
  const command = args._[0] ?? "help";
  const selfDir = dirname(fileURLToPath(import.meta.url));
  const { home, source } = cliHome(args, selfDir);
  const build = readBuildInfo();

  switch (command) {
    case "doctor": {
      const quick = flagBool(args, "quick");
      const { checks, spawn } = await runDoctor({
        home,
        homeSource: source,
        quick,
        codexHome: codexHome(process.env, flagString(args, "codex-home")),
        noSpawn: flagBool(args, "no-spawn"),
        clients: flagList(args, "clients"),
        addinsRoot: flagString(args, "addins-root"),
      });
      const sum = summarize(checks);
      if (flagBool(args, "json")) process.stdout.write(JSON.stringify({ home, version: build.version, gitSha: build.gitSha, quick, ...sum, checks, spawn }, null, 2) + "\n");
      else {
        process.stdout.write(`revit-mcp-next doctor ${build.version} (${build.gitSha || "dev"}) home ${home}${quick ? " [quick]" : ""}\n`);
        printChecks(checks);
        process.stdout.write(`${sum.ok ? "PASS" : "FAIL"}: ${sum.fails} failure(s), ${sum.warns} warning(s)\n`);
      }
      return sum.ok ? 0 : 1;
    }
    case "clients":
      return clientsCommand(args, home, build);
    case "settings":
      return settingsCommand(args, home);
    case "raw":
      return rawCommand(args, home);
    case "support":
      return supportCommand(args, home, source);
    case "version":
      process.stdout.write(JSON.stringify({ version: build.version, gitSha: build.gitSha, sdk: build.sdk, node: process.version, home, homeSource: source }) + "\n");
      return 0;
    default:
      process.stdout.write(
        [
          "revit-mcp-cli <command> [--home <dir>]",
          "  doctor [--quick] [--json]                 check the install, clients and a Codex-exact broker start",
          "  clients install|uninstall|show            register the broker with Codex / Claude Code / Claude Desktop",
          "  settings merge|show                       merge config\\settings.json with the schema",
          "  raw <op> [--year <y>] [--args <json>]     send one v3 frame to the add-in (diagnostics)",
          "  support [--out <dir>]                     zip logs and reports for a bug report",
          "  version",
          "",
        ].join("\n"),
      );
      return command === "help" || flagBool(args, "help") ? 0 : 2;
  }
}

main().then(
  (code) => {
    process.exitCode = code;
  },
  (error) => {
    process.stderr.write(`revit-mcp-cli: ${(error as Error).stack ?? String(error)}\n`);
    process.exitCode = 1;
  },
);
