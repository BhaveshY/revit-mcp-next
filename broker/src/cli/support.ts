// `revit-mcp-cli support [--out <dir>]`: a support bundle for bug reports. Collects the doctor report, receipts,
// settings, registrations, manifests, the Codex/Claude entries for `revit` and the newest logs into
// <home>\support\support-<ts>.zip. Never includes auth.env, tokens or whole client config files.

import { copyFileSync, existsSync, mkdirSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { basename, join } from "node:path";
import { addinCurrentFile, brokerBuildInfo, installReceiptFile, instancesDir, logsDir, manifestFile, settingsFile } from "@revit-mcp-next/contracts/home";
import { inspectCodexConfig, SERVER_NAME } from "./clients.js";
import { claudeJsonPath, codexHome, flagBool, flagString, readJsonFile, readText, run, timestamp, type ParsedArgs } from "./common.js";
import { runDoctor, summarize, YEARS } from "./doctor.js";

const MAX_LOGS = 8;
const MAX_LOG_BYTES = 8 * 1024 * 1024;

export async function supportCommand(args: ParsedArgs, home: string, homeSource: string): Promise<number> {
  const stamp = timestamp();
  const root = flagString(args, "out") ?? join(home, "support");
  const dir = join(root, `support-${stamp}`);
  mkdirSync(dir, { recursive: true });
  const copy = (src: string, name = basename(src)) => {
    try {
      if (existsSync(src) && statSync(src).isFile() && statSync(src).size <= MAX_LOG_BYTES) copyFileSync(src, join(dir, name));
    } catch {
      // unreadable: skip
    }
  };

  const ch = codexHome(process.env, flagString(args, "codex-home"));
  const doctor = await runDoctor({ home, homeSource, quick: !flagBool(args, "full"), codexHome: ch });
  writeFileSync(join(dir, "doctor.json"), JSON.stringify({ home, ...summarize(doctor.checks), ...doctor }, null, 2));
  copy(installReceiptFile(home));
  copy(brokerBuildInfo(home));
  copy(settingsFile(home));
  for (const year of YEARS) {
    copy(addinCurrentFile(home, year), `current-${year}.json`);
    try {
      copy(manifestFile(year), `RevitMcpNext-${year}.addin`);
    } catch {
      // no APPDATA
    }
  }
  try {
    for (const name of readdirSync(instancesDir(home)).filter((n) => n.endsWith(".json"))) copy(join(instancesDir(home), name), `instance-${name}`);
  } catch {
    // no instances
  }
  try {
    const logs = readdirSync(logsDir(home))
      .filter((n) => /\.(jsonl|log)$/i.test(n) && !/^code-exec/i.test(n))
      .map((n) => ({ n, t: statSync(join(logsDir(home), n)).mtimeMs }))
      .sort((a, b) => b.t - a.t)
      .slice(0, MAX_LOGS);
    mkdirSync(join(dir, "logs"), { recursive: true });
    for (const { n } of logs) copy(join(logsDir(home), n), join("logs", n));
  } catch {
    // no logs
  }
  const codexText = readText(join(ch, "config.toml"));
  const claude = readJsonFile<{ mcpServers?: Record<string, unknown> }>(claudeJsonPath());
  writeFileSync(
    join(dir, "clients.json"),
    JSON.stringify(
      {
        codex: codexText ? inspectCodexConfig(codexText) : null,
        claude: claude.value?.mcpServers?.[SERVER_NAME] ?? null,
        node: process.version,
        os: `${process.platform} ${process.arch}`,
      },
      null,
      2,
    ),
  );

  const zip = `${dir}.zip`;
  const ps = run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", `Compress-Archive -LiteralPath '${dir.replace(/'/g, "''")}' -DestinationPath '${zip.replace(/'/g, "''")}' -Force`], { timeoutMs: 120_000 });
  if (ps.ok && existsSync(zip)) {
    rmSync(dir, { recursive: true, force: true });
    process.stdout.write(`support bundle: ${zip}\n`);
  } else process.stdout.write(`support bundle folder: ${dir} (zip failed: ${(ps.stderr || ps.error || "").trim().slice(0, 200)})\n`);
  return 0;
}
