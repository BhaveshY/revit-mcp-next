// C2 Claude Code integration (SPEC §12.4, D4 §8.13; owner W3-E2E; profile claude). No model quota by default.
//   1. claude --version (Claude Code on PATH, or E2E_CLAUDE).
//   2. `claude mcp get revit` (the user-scope registration from the installer): stdio, node + revit-mcp.mjs, and
//      Claude Code's own health check connects (the broker starts under Claude's environment).
//   3. The large-capture check (a ~1.5 MB base64 capture reaches Claude Code untruncated, MAX_MCP_OUTPUT_TOKENS) needs
//      `claude -p`, which is NOT approved; it runs only with E2E_CLAUDE_P=1 and a harness Revit in the e2e home.

import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

function claude(args, timeout = 90_000) {
  const exe = process.env.E2E_CLAUDE ?? "claude";
  let out = spawnSync(exe, args, { encoding: "utf8", windowsHide: true, timeout });
  // An npm-installed Claude Code is claude.cmd, which needs cmd.exe: quote every argument for it.
  if (out.error?.code === "ENOENT" && process.platform === "win32") {
    out = spawnSync(`${exe}.cmd`, args.map((a) => `"${String(a).replace(/"/g, '\\"')}"`), { encoding: "utf8", windowsHide: true, timeout, shell: true });
  }
  return { status: out.status, stdout: out.stdout ?? "", stderr: out.stderr ?? "", error: out.error };
}

const field = (text, name) => new RegExp(`^\\s*${name}:\\s*(.+)$`, "mi").exec(text)?.[1]?.trim() ?? null;

export default {
  id: "C2",
  title: "Claude Code: user-scope revit registration and health check; optional claude -p large capture",
  profiles: ["claude"],
  years: "none",
  async run(t) {
    await t.step("claude --version", () => {
      const v = claude(["--version"]);
      t.assert(v.status === 0, `claude is not runnable (${v.error?.message ?? v.stderr.trim().slice(0, 200)}); install Claude Code or set E2E_CLAUDE`);
      t.log(`claude ${v.stdout.trim()}`);
    });

    await t.step("claude mcp get revit: stdio node + revit-mcp.mjs, connected", () => {
      const r = claude(["mcp", "get", "revit"]);
      const text = `${r.stdout}\n${r.stderr}`;
      t.assert(r.status === 0 && /revit/i.test(text), `revit is not registered in Claude Code (run the revit-mcp-next installer): ${text.trim().slice(0, 300)}`);
      const command = field(text, "Command") ?? "";
      const args = field(text, "Args") ?? "";
      const status = field(text, "Status") ?? "";
      t.assert(/node(\.exe)?"?$/i.test(command) || /node(\.exe)?/i.test(command), `command is node: ${command}`);
      t.assert(/revit-mcp\.mjs/i.test(args), `args name the broker bundle revit-mcp.mjs: ${args}`);
      t.assert(/connected/i.test(status), `Claude Code's health check connects: ${status}`);
      t.log(`claude: ${command} ${args} (${status})`);
    });

    if (process.env.E2E_CLAUDE_P !== "1") {
      t.log("claude -p large-capture check skipped (not approved; set E2E_CLAUDE_P=1 once it is)");
      return;
    }

    await t.step("claude -p: a large capture reaches Claude Code untruncated", () => {
      const dir = join(t.runRoot, "claude-p");
      mkdirSync(dir, { recursive: true });
      const config = join(dir, "mcp.json");
      writeFileSync(config, JSON.stringify({ mcpServers: { revit: { type: "stdio", command: process.execPath, args: [t.broker], env: { REVIT_MCP_NEXT_HOME: t.home } } } }));
      const r = claude(
        ["-p", "--output-format", "json", "--mcp-config", config, "--strict-mcp-config", "--allowedTools", "mcp__revit__capture,mcp__revit__status", 'Call the revit capture tool with {"size":"large"} once. Reply only with OK if you received an image, or with the exact error text otherwise.'],
        10 * 60_000
      );
      writeFileSync(join(dir, "result.json"), r.stdout);
      t.assert(r.status === 0, `claude -p failed: ${(r.stderr || r.stdout).trim().slice(-400)}`);
      t.assert(!/exceeds maximum allowed tokens|truncated/i.test(r.stdout), `Claude Code truncated the capture (lower the large preset for clientInfo claude-code, SPEC §12.4): ${r.stdout.slice(0, 400)}`);
      t.assertMatch(r.stdout, /\bOK\b/, "the model received the image");
    });
  },
};
