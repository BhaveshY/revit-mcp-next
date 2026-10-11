// C1 Codex CLI integration (SPEC §12.2-§12.3, §17 V22, D4 §8.13; owner W3-E2E; profile codex). No model quota
// unless E2E_CODEX_EXEC=1.
//   1. codex --version (Codex CLI 0.153+ on PATH, or E2E_CODEX).
//   2. Isolated CODEX_HOME (<run>\codex-home): the SPEC §12.2 [mcp_servers.revit] block for this checkout and the e2e
//      home; `codex mcp get revit --json` shows command, args, env, startup 30 s and tool 120 s.
//   3. V22 TOML syntax: Codex parses [mcp_servers.revit.tools.<tool>] approval_mode (a bogus value is rejected naming
//      the allowed values) and output_token_limit (a string is rejected; a number is accepted).
//   4. Installed config (read only, the user's CODEX_HOME): [mcp_servers.revit] exists with node + revit-mcp.mjs,
//      REVIT_MCP_NEXT_HOME, startup_timeout_sec 30, tool_timeout_sec 120, required = true; the legacy
//      revit-mcp-next block is gone.
//   5. E2E_CODEX_EXEC=1 (uses the user's model quota; approved for gpt-6-sol, reasoning medium): one `codex exec --json`
//      run against this checkout's broker and the e2e home (-c overrides, no config file is changed). The model calls
//      help and status; every revit tool result in the event stream is compared with the same call made directly, and
//      any shortening is reported as the measured tool output limit (V22). With a harness Revit registered in the e2e
//      home it also asks for a ~40 KB detail:"full" list. The raw events are written next to summary.json.

import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { makeCaller } from "../lib/call.mjs";

const tomlString = (s) => `'${String(s).replace(/'/g, "''")}'`;

function codex(args, env = {}, timeout = 60_000) {
  const exe = process.env.E2E_CODEX ?? "codex";
  const out = spawnSync(exe, args, { encoding: "utf8", windowsHide: true, timeout, env: { ...process.env, ...env } });
  return { status: out.status, stdout: out.stdout ?? "", stderr: out.stderr ?? "", error: out.error };
}

/** The JSON document in codex output (Codex prints WARNING lines before it). */
function jsonOf(text) {
  const start = text.search(/^[{[]/m);
  if (start < 0) return null;
  try {
    return JSON.parse(text.slice(start));
  } catch {
    return null;
  }
}

function block({ node, broker, home, extra = "" }) {
  return [
    "# written by e2e/scenarios/C1-codex.mjs (isolated CODEX_HOME)",
    "[mcp_servers.revit]",
    `command = ${tomlString(node)}`,
    `args = [${tomlString(broker)}]`,
    "startup_timeout_sec = 30",
    "tool_timeout_sec = 120",
    "",
    "[mcp_servers.revit.env]",
    `REVIT_MCP_NEXT_HOME = ${tomlString(home)}`,
    "",
    ...["status", "help", "set_target", "ui"].flatMap((tool) => [`[mcp_servers.revit.tools.${tool}]`, 'approval_mode = "auto"']),
    extra,
    "",
  ].join("\n");
}

/** Every revit tool result in a codex exec --json event stream: [{tool, text}]. */
function toolResults(lines) {
  const out = [];
  const visit = (node) => {
    if (!node || typeof node !== "object") return;
    const tool = node.tool ?? node.tool_name ?? node.name;
    const server = node.server ?? node.server_name;
    const result = node.result ?? node.output ?? node.content;
    if (typeof tool === "string" && (server === undefined || /revit/i.test(String(server))) && result) {
      const content = Array.isArray(result) ? result : Array.isArray(result?.content) ? result.content : null;
      const text = content ? content.filter((c) => c?.type === "text").map((c) => c.text).join("\n") : typeof result === "string" ? result : null;
      if (text) out.push({ tool, text });
    }
    for (const v of Object.values(node)) if (v && typeof v === "object") visit(v);
  };
  for (const line of lines) {
    try {
      visit(JSON.parse(line));
    } catch {
      // not JSON
    }
  }
  return out;
}

export default {
  id: "C1",
  title: "Codex CLI: isolated and installed revit block, V22 TOML syntax, optional codex exec output-limit run",
  profiles: ["codex"],
  years: "none",
  async run(t) {
    const version = await t.step("codex --version", () => {
      const v = codex(["--version"]);
      t.assert(v.status === 0, `codex is not runnable (${v.error?.message ?? v.stderr.trim().slice(0, 200)}); install the Codex CLI or set E2E_CODEX`);
      const m = /(\d+\.\d+\.\d+)/.exec(v.stdout);
      t.log(`codex ${m?.[1] ?? v.stdout.trim()}`);
      return m?.[1] ?? null;
    });

    const codexHome = join(t.runRoot, "codex-home");
    mkdirSync(codexHome, { recursive: true });
    const config = join(codexHome, "config.toml");
    const env = { CODEX_HOME: codexHome };
    const base = { node: process.execPath, broker: t.broker, home: t.home };

    await t.step("isolated CODEX_HOME: codex mcp get revit --json shows the §12.2 block", () => {
      writeFileSync(config, block(base));
      const r = codex(["mcp", "get", "revit", "--json"], env);
      t.assert(r.status === 0, `codex mcp get failed: ${(r.stderr || r.stdout).trim().slice(0, 400)}`);
      const j = jsonOf(r.stdout);
      t.assert(j && j.name === "revit", "server revit listed");
      t.assertEq(j.transport?.command, process.execPath, "command");
      t.assertEq(j.transport?.args?.[0], t.broker, "args");
      t.assertEq(j.transport?.env?.REVIT_MCP_NEXT_HOME, t.home, "env REVIT_MCP_NEXT_HOME");
      t.assertEq(j.startup_timeout_sec, 30, "startup_timeout_sec");
      t.assertEq(j.tool_timeout_sec, 120, "tool_timeout_sec");
    });

    await t.step("V22: per-tool approval_mode and output_token_limit are parsed (bad values rejected)", () => {
      writeFileSync(config, block(base).replace('[mcp_servers.revit.tools.status]\napproval_mode = "auto"', '[mcp_servers.revit.tools.status]\napproval_mode = "bogus"'));
      const bad = codex(["mcp", "get", "revit", "--json"], env);
      t.assert(bad.status !== 0 && /approval_mode/.test(bad.stderr + bad.stdout), `a bogus approval_mode must be rejected: ${(bad.stderr + bad.stdout).trim().slice(0, 300)}`);
      const allowed = /expected one of ([^\n]+)/.exec(bad.stderr + bad.stdout)?.[1] ?? "?";
      writeFileSync(config, block({ ...base, extra: '[mcp_servers.revit.tools.list]\noutput_token_limit = "abc"' }));
      const badLimit = codex(["mcp", "get", "revit", "--json"], env);
      t.assert(badLimit.status !== 0 && /output_token_limit/.test(badLimit.stderr + badLimit.stdout), "a string output_token_limit must be rejected (the key exists)");
      writeFileSync(config, block({ ...base, extra: "[mcp_servers.revit.tools.list]\noutput_token_limit = 12000" }));
      const good = codex(["mcp", "get", "revit", "--json"], env);
      t.assert(good.status === 0, `a numeric output_token_limit must be accepted: ${(good.stderr || good.stdout).trim().slice(0, 300)}`);
      writeFileSync(config, block(base));
      t.log(`V22 codex ${version}: per-tool approval_mode values ${allowed}; per-tool output_token_limit accepted`);
    });

    await t.step("installed config (read only): [mcp_servers.revit] per SPEC §12.2", () => {
      const r = codex(["mcp", "get", "revit", "--json"]);
      t.assert(r.status === 0, `no revit server in your Codex config (run the revit-mcp-next installer): ${(r.stderr || r.stdout).trim().slice(0, 300)}`);
      const j = jsonOf(r.stdout);
      t.assert(/node(\.exe)?$/i.test(j?.transport?.command ?? ""), `command is node: ${j?.transport?.command}`);
      t.assert(/revit-mcp\.mjs$/i.test(j?.transport?.args?.[0] ?? ""), `args[0] is the broker bundle revit-mcp.mjs: ${j?.transport?.args?.[0]}`);
      t.assert(typeof j?.transport?.env?.REVIT_MCP_NEXT_HOME === "string", "env REVIT_MCP_NEXT_HOME");
      t.assertEq(j?.startup_timeout_sec, 30, "startup_timeout_sec");
      t.assertEq(j?.tool_timeout_sec, 120, "tool_timeout_sec");
      const legacy = codex(["mcp", "get", "revit-mcp-next", "--json"]);
      t.assert(legacy.status !== 0, "the legacy revit-mcp-next server block is removed by the installer");
      t.log(`installed: ${j.transport.command} ${j.transport.args.join(" ")} (home ${j.transport.env.REVIT_MCP_NEXT_HOME})`);
    });

    if (process.env.E2E_CODEX_EXEC !== "1") {
      t.log("codex exec real-model run skipped (set E2E_CODEX_EXEC=1; uses your model quota)");
      return;
    }

    await t.step("codex exec: tool results reach the model unshortened (V22 output limit)", async () => {
      const session = await t.connect({ home: t.home });
      const direct = makeCaller({ session });
      let big = null;
      try {
        const status = await direct("status", {});
        const revitUp = Array.isArray(status.json?.revit) && status.json.revit.length > 0;
        if (revitUp) big = { tool: "list", args: { kind: "types", detail: "full", limit: 500 } };
        const calls = [{ tool: "help", args: { tool: "create_elements" } }, { tool: "status", args: {} }, ...(big ? [big] : [])];
        const expected = [];
        for (const c of calls) expected.push({ ...c, text: (await direct(c.tool, c.args, { allowError: true })).text });
        const prompt =
          "Use only the revit MCP server. Make exactly these tool calls in this order, with exactly these arguments, and nothing else: " +
          calls.map((c) => `${c.tool} ${JSON.stringify(c.args)}`).join("; ") +
          ". Then reply with the number of characters of each tool result's text.";
        const model = process.env.E2E_CODEX_MODEL ?? "gpt-6-sol";
        const workdir = join(t.runRoot, "codex-exec");
        mkdirSync(workdir, { recursive: true });
        const r = codex(
          [
            "exec", "--json", "--skip-git-repo-check", "-C", workdir,
            "-m", model, "-c", 'model_reasoning_effort="medium"',
            "-c", `mcp_servers.revit.command=${JSON.stringify(process.execPath)}`,
            "-c", `mcp_servers.revit.args=[${JSON.stringify(t.broker)}]`,
            "-c", `mcp_servers.revit.env.REVIT_MCP_NEXT_HOME=${JSON.stringify(t.home)}`,
            // exec has no one to answer an approval prompt: the read-only list call of this run is auto-approved.
            "-c", 'mcp_servers.revit.tools.list.approval_mode="auto"',
            prompt,
          ],
          {},
          10 * 60_000
        );
        const lines = r.stdout.split(/\r?\n/).filter(Boolean);
        const eventsFile = join(t.repo, "artifacts", "e2e", t.runId, "codex-exec-events.jsonl");
        mkdirSync(join(t.repo, "artifacts", "e2e", t.runId), { recursive: true });
        writeFileSync(eventsFile, r.stdout);
        t.assert(r.status === 0, `codex exec failed (exit ${r.status}): ${(r.stderr || "").trim().slice(-400)}`);
        const seen = toolResults(lines);
        t.assert(seen.length > 0, `no revit tool result found in the codex exec events (${eventsFile})`);
        const report = expected.map((e) => {
          const got = seen.find((s) => s.tool.endsWith(e.tool));
          return { tool: e.tool, sentChars: e.text.length, seenChars: got?.text.length ?? null, shortened: got ? got.text.length < e.text.length : null };
        });
        t.log(`codex exec ${version} (${model}): ${JSON.stringify(report)}; events ${eventsFile}`);
        const shortened = report.filter((x) => x.shortened);
        t.assert(shortened.length === 0, `Codex shortened tool output: ${JSON.stringify(shortened)} (lower the detail:"full" cap in broker/src/framework/render.ts or set output_token_limit per tool, SPEC §12.3)`);
      } finally {
        await session.close();
      }
    });
    void existsSync;
  },
};
