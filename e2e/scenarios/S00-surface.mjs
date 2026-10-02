// S00 surface (SPEC §13.3, §15; D4 §8.6). No Revit needed (~15 s; hosted CI).
// Eras 2025-06-18 (Codex), 2025-11-25 (Claude), 2026-07-28 (modern, server/discover). tools/list equals
// artifacts/catalog/tools-list*.json byte for byte and passes the §15 gates. Instructions head ≤ 512. Lenient input never
// fails SDK validation. status without Revit is "ok: no Revit with the add-in is running" plus a next line. Profiles,
// structured flag, prompts/resources. Stdin EOF exits ≤ 2 s and stdout carries only JSON-RPC.

import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { REGISTRY } from "@revit-mcp-next/contracts/catalog";
import { checkAdvertised, GATES, runChecks } from "@revit-mcp-next/contracts/catalog/checks";
import { INSTRUCTIONS, INSTRUCTIONS_HEAD } from "@revit-mcp-next/contracts/catalog/instructions";
import { makeCaller } from "../lib/call.mjs";
import { spawnRawBroker } from "../lib/mcp.mjs";

const readJson = (path) => JSON.parse(readFileSync(path, "utf8"));

export default {
  id: "S00",
  title: "Surface: eras, tools/list, gates, lenient input, no-Revit status, EOF",
  profiles: ["surface", "quick", "full"],
  years: "none",
  async run(t) {
    const art = join(t.repo, "artifacts", "catalog");
    const files = {
      all: join(art, "tools-list.json"),
      def: join(art, "tools-list.default.json"),
      core: join(art, "tools-list.core.json"),
      catalog: join(art, "catalog.json"),
    };
    const homes = join(t.runRoot, "s00");
    const home = (name, settings) => {
      const dir = join(homes, name);
      mkdirSync(join(dir, "config"), { recursive: true });
      if (settings) writeFileSync(join(dir, "config", "settings.json"), JSON.stringify(settings, null, 2));
      return dir;
    };
    const surface = {};

    await t.step("artifacts exist (npm run build ran gen:catalog)", () => {
      for (const [k, p] of Object.entries(files)) t.assert(existsSync(p), `missing ${k}: ${p}; run npm run build`);
    });
    const expectedDefault = readFileSync(files.def, "utf8");
    const expectedAll = readFileSync(files.all, "utf8");
    const expectedCore = readFileSync(files.core, "utf8");

    await t.step("catalog gates (§15) and spec parity", () => {
      const budgetsPath = join(t.repo, "e2e", "budgets.json");
      const result = runChecks(existsSync(budgetsPath) ? readJson(budgetsPath) : null);
      t.assert(result.ok, `surface gates failed:\n${result.failures.join("\n")}`);
      t.assertEq(result.stats.toolCount, GATES.defaultToolCount, "default tool count");
      t.assertLe(result.stats.toolsListBytesWithOptIn, GATES.totalMaxBytes, "tools/list bytes");
      const spec = join(t.repo, "docs", "design", "SPEC-tools-list.json");
      t.assertEq(expectedAll, readFileSync(spec, "utf8").trim(), "tools-list.json equals docs/design/SPEC-tools-list.json byte for byte");
      t.assertDeepEqual(REGISTRY, readJson(join(t.repo, "docs", "design", "SPEC-registry.json")), "registry equals docs/design/SPEC-registry.json");
      surface.stats = result.stats;
    });

    await t.step("instructions equal SPEC-instructions.mjs; head ≤ 512 and self-contained", async () => {
      const spec = await import(pathToFileURL(join(t.repo, "docs", "design", "SPEC-instructions.mjs")).href);
      t.assertEq(INSTRUCTIONS, spec.HEAD + spec.TAIL, "instructions text");
      t.assertEq(INSTRUCTIONS_HEAD.length, 504, "head length");
      t.assertLe(INSTRUCTIONS.length, 1500, "total length");
      for (const must of ["status", "fix:", "capture", "screen automation"]) t.assert(INSTRUCTIONS.slice(0, 512).includes(must), `head mentions ${must}`);
    });

    // ---------------------------------------------------------------- Codex era (2025-06-18)
    const codexHome = home("codex");
    const codex = await t.connect({ home: codexHome, era: "2025-06-18", clientName: "codex-mcp-client", elicitation: true });
    const call = makeCaller({ session: codex });
    try {
      await t.step("2025-06-18 initialize (Codex)", () => {
        t.assertEq(codex.protocol(), "2025-06-18", "negotiated protocol");
        t.assertLe(codex.initMs, 10_000, "spawn to initialized ms");
        if (codex.initMs > 1_500) t.log(`warn: initialize took ${codex.initMs} ms (> 1.5 s warm budget)`);
        t.assertEq(codex.instructions(), INSTRUCTIONS, "instructions served by initialize");
        surface.initMs = codex.initMs;
      });

      await t.step("tools/list is the catalog verbatim (77,661 B default), deterministic, gate-clean", async () => {
        const first = await codex.client.listTools(undefined, { cacheMode: "bypass" });
        const second = await codex.client.listTools(undefined, { cacheMode: "bypass" });
        const a = JSON.stringify({ tools: first.tools });
        const b = JSON.stringify({ tools: second.tools });
        t.assertEq(a, expectedDefault, "served tools/list equals artifacts/catalog/tools-list.default.json");
        t.assertEq(a, b, "two tools/list calls are byte-identical");
        t.assertEq(Buffer.byteLength(a), surface.stats.toolsListBytesDefault, "tools/list bytes");
        t.assert(!a.includes("$schema") && !a.includes("outputSchema"), "no $schema and no outputSchema");
        const failures = [];
        checkAdvertised(first.tools, failures, "served");
        t.assert(failures.length === 0, `served tools/list violates the gates:\n${failures.join("\n")}`);
        surface.toolsListBytes = Buffer.byteLength(a);
        surface.tools = first.tools.length;
        surface.names = first.tools.map((x) => x.name);
      });

      await t.step("status without Revit: ok + next line, < 3 s, no phantom instance", async () => {
        const r = await call("status", {});
        t.assertEq(r.status, "ok", "status line");
        t.assertMatch(r.summary, /^no Revit with the add-in is running$/, "summary");
        t.assert(!!r.next, "has a next: line");
        t.assert(Array.isArray(r.json?.revit) && r.json.revit.length === 0, "no instance listed");
        t.assertLe(r.ms, 3_000, "status ms");
        t.assertLe(r.bytes, GATES.outputs.statusCompactMaxBytes, "compact status bytes");
      });

      await t.step("lenient input: numeric ids and a category string never fail SDK validation", async () => {
        for (const args of [{ ids: [304512] }, { category: "Walls" }, { ids: "304512,304513", level: ["Level 1"], limit: "20" }, { Category: ["Walls"], countOnly: "yes" }]) {
          const r = await call("find_elements", args, { allowError: true });
          t.assert(!/Input validation|Invalid arguments/i.test(r.text), `SDK validation leaked for ${JSON.stringify(args)}: ${r.text.slice(0, 200)}`);
          t.assertEq(r.code, "NO_REVIT_RUNNING", `find_elements ${JSON.stringify(args)} reaches the broker`);
          t.assert(r.fix?.startsWith("ask the user:"), "fix asks the user to start Revit");
          t.assertLe(r.ms, 3_000, "no-Revit answer ms");
        }
        const renamed = await call("find_elements", { Category: "Walls", countOnly: true }, { allowError: true });
        t.assert(renamed.warnings.some((w) => w.startsWith("PARAM_RENAMED")), "camelCase keys are renamed with a warning");
      });

      await t.step("doc-scoped read without Revit: ERROR NO_REVIT_RUNNING + fix", async () => {
        const r = await call("list", { kind: "levels" }, { expectError: "NO_REVIT_RUNNING" });
        t.assert(r.fix && r.fix.length > 0, "fix line");
      });

      await t.step("bad input is a precise INVALID_ARGS with a working example", async () => {
        const r = await call("create_elements", { op: "wall" }, { expectError: "INVALID_ARGS" });
        t.assertMatch(r.summary, /op=wall.*missing/, "names the op and the missing field");
        t.assertMatch(r.fix, /^create_elements \{"op":"wall","level":/, "fix is a complete create_elements call");
        t.assertLe(r.bytes, 1_024, "error size");
        const unknown = await call("modify_elements", { op: "delte", ids: [1] }, { expectError: "UNKNOWN_OP" });
        t.assertMatch(unknown.fix, /^modify_elements \{"op":"delete"/, "UNKNOWN_OP fix uses the closest op");
        const typo = await call("find_elements", { categry: "Walls" }, { expectError: "INVALID_ARGS" });
        t.assertMatch(typo.fix, /^find_elements \{"category":"Walls"\}/, "did-you-mean fix is the corrected call");
        const missing = await call("set_target", {}, { expectError: "INVALID_ARGS" });
        t.assert(!/Input validation/i.test(missing.text), "missing required param is a broker INVALID_ARGS");
      });

      await t.step("help: index, every tool, tool+op JSON line (D1 §4B)", async () => {
        const index = await call("help", {});
        t.assertEq(index.json?.instructions, INSTRUCTIONS, "help {} starts with the instructions");
        let ops = 0;
        for (const name of surface.names) {
          const r = await call("help", { tool: name });
          t.assertLe(r.bytes, 6_144 * 2, `help ${name} size`);
          t.assertEq(r.json?.tool, name, `help ${name} JSON line`);
          if (r.json?.ops) {
            for (const [op, o] of Object.entries(r.json.ops)) {
              t.assert(o.example && typeof o.example === "object", `help ${name} op ${op} has an example`);
              ops += 1;
            }
          } else ops += 1;
        }
        const wall = await call("help", { tool: "create_elements", op: "wall" });
        t.assert(wall.json?.required?.includes("level"), "help {tool,op} lists required params");
        t.assert(wall.json?.params?.points?.unit === "mm", "help {tool,op} gives units");
        t.assertMatch(wall.next, /^create_elements \{"op":"wall"/, "help {tool,op} next is a working call");
        const topic = await call("help", { topic: "TARGET_CHANGED" });
        t.assertEq(topic.json?.code, "TARGET_CHANGED", "error codes are help topics");
        surface.helpOps = ops;
      });

      await t.step("prompts and resources mirror help", async () => {
        const prompts = await codex.client.listPrompts();
        const names = prompts.prompts.map((p) => p.name);
        t.assert(names.includes("start_workflow") && names.includes("workflow"), `prompts: ${names.join(",")}`);
        const wf = await codex.client.getPrompt({ name: "workflow", arguments: { name: "audit" } });
        t.assertMatch(wf.messages[0]?.content?.text ?? "", /status/, "workflow prompt text");
        const overview = await codex.client.readResource({ uri: "revit://help/overview" });
        t.assertMatch(overview.contents[0]?.text ?? "", /^ok: help for 37 Revit tools/, "revit://help/overview equals help {}");
        const tool = await codex.client.readResource({ uri: "revit://help/create_elements.wall" });
        t.assertMatch(tool.contents[0]?.text ?? "", /create_elements op=wall/, "revit://help/{tool.op}");
        const templates = await codex.client.listResourceTemplates();
        t.assert(templates.resourceTemplates.some((x) => x.uriTemplate === "revit://help/{name}"), "help resource template");
        const completion = await codex.client.complete({ ref: { type: "ref/resource", uri: "revit://help/{name}" }, argument: { name: "name", value: "create_" } });
        t.assert(completion.completion.values.includes("create_elements"), "completion over tool names");
      });
    } finally {
      await codex.close();
    }

    // ---------------------------------------------------------------- Claude era (2025-11-25)
    await t.step("2025-11-25 initialize (Claude): same tools and instructions", async () => {
      const s = await t.connect({ home: home("claude"), era: "2025-11-25", clientName: "claude-code", clientVersion: "2.1.0" });
      try {
        t.assertEq(s.protocol(), "2025-11-25", "negotiated protocol");
        t.assertEq(s.instructions(), INSTRUCTIONS, "instructions");
        const list = await s.client.listTools(undefined, { cacheMode: "bypass" });
        t.assertEq(JSON.stringify({ tools: list.tools }), expectedDefault, "tools/list");
        const r = await makeCaller({ session: s })("status", {});
        t.assertEq(r.status, "ok", "status works");
      } finally {
        await s.close();
      }
    });

    // ---------------------------------------------------------------- modern era (2026-07-28)
    await t.step("2026-07-28 server/discover (modern): same tools, instructions and cache hints", async () => {
      const s = await t.connect({ home: home("modern"), era: "modern" });
      try {
        t.assertEq(s.protocol(), "2026-07-28", "negotiated protocol");
        const discover = s.discover();
        t.assert(discover, "server/discover result");
        t.assertEq(s.instructions(), INSTRUCTIONS, "instructions from server/discover");
        const list = await s.client.listTools(undefined, { cacheMode: "bypass" });
        t.assertEq(JSON.stringify({ tools: list.tools }), expectedDefault, "tools/list");
        t.assert(typeof list.ttlMs === "number" ? list.ttlMs === 300_000 : true, "tools/list ttlMs 300 s when exposed");
        const r = await makeCaller({ session: s })("find_elements", { ids: [304512] }, { expectError: "NO_REVIT_RUNNING" });
        t.assert(!!r.fix, "modern era tool call");
      } finally {
        await s.close();
      }
      surface.eras = { "2025-06-18": true, "2025-11-25": true, "2026-07-28": true };
    });

    // ---------------------------------------------------------------- configuration variants
    await t.step("REVIT_MCP_NEXT_STRUCTURED=1 adds structuredContent next to the text", async () => {
      const s = await t.connect({ home: home("structured"), env: { REVIT_MCP_NEXT_STRUCTURED: "1" } });
      try {
        const r = await makeCaller({ session: s, structured: true })("status", {});
        t.assert(r.raw.structuredContent && r.raw.structuredContent.status === "ok", "structuredContent present");
        t.assert(r.text.startsWith("ok: "), "text block still present");
      } finally {
        await s.close();
      }
    });

    await t.step("profile core lists the 17 core tools", async () => {
      const s = await t.connect({ home: home("core"), env: { REVIT_MCP_NEXT_PROFILE: "core" } });
      try {
        const list = await s.client.listTools(undefined, { cacheMode: "bypass" });
        t.assertEq(list.tools.length, GATES.coreToolCount, "core tool count");
        t.assertEq(JSON.stringify({ tools: list.tools }), expectedCore, "core tools/list");
      } finally {
        await s.close();
      }
    });

    await t.step("enableCodeExecution at start lists run_csharp (78,649 B) and status says so", async () => {
      const s = await t.connect({ home: home("code", { enableCodeExecution: true }) });
      try {
        const list = await s.client.listTools(undefined, { cacheMode: "bypass" });
        t.assertEq(JSON.stringify({ tools: list.tools }), expectedAll, "tools/list with run_csharp equals tools-list.json");
        const r = await makeCaller({ session: s })("status", {});
        t.assert(r.notices.some((n) => n.startsWith("CODE_EXECUTION_ENABLED")), "CODE_EXECUTION_ENABLED notice");
      } finally {
        await s.close();
      }
    });

    // ---------------------------------------------------------------- stdout hygiene and EOF
    await t.step("stdout carries only JSON-RPC; stdin EOF exits within 2 s; broker log written", async () => {
      const rawHome = home("raw");
      const b = spawnRawBroker({ home: rawHome, broker: t.broker });
      try {
        b.send({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "codex-mcp-client", version: "0.153.4" } } });
        const init = await b.response(1);
        t.assertEq(init.result?.protocolVersion, "2025-06-18", "raw initialize");
        b.send({ jsonrpc: "2.0", method: "notifications/initialized" });
        b.send({ jsonrpc: "2.0", id: 2, method: "tools/list" });
        const list = await b.response(2);
        t.assertEq(JSON.stringify({ tools: list.result.tools }), expectedDefault, "raw tools/list");
        b.send({ jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "find_elements", arguments: { ids: [304512], category: "Walls" } } });
        const callResult = await b.response(3);
        t.assert(callResult.result && !callResult.error, "lenient call returns a tool result, not a JSON-RPC error");
        const endedAt = b.endStdin();
        const exit = await Promise.race([b.exited, new Promise((r) => setTimeout(() => r(null), 5_000))]);
        t.assert(exit !== null, "broker exited after stdin EOF");
        const exitMs = exit.at - endedAt;
        t.assertLe(exitMs, 2_000, "exit ms after stdin EOF");
        surface.eofExitMs = exitMs;
        surface.exitCode = exit.code;
        for (const line of b.stdoutLines) {
          let parsed;
          try {
            parsed = JSON.parse(line);
          } catch {
            parsed = null;
          }
          t.assert(parsed && parsed.jsonrpc === "2.0", `non-JSON-RPC stdout line: ${line.slice(0, 120)}`);
        }
        const logs = join(rawHome, "logs");
        t.assert(existsSync(logs) && readdirSync(logs).some((n) => /^broker-\d{8}\.\d+\.jsonl$/.test(n)), "broker log file in <home>\\logs");
      } finally {
        b.kill();
      }
    });

    t.log(`tools/list ${surface.toolsListBytes} B (${surface.tools} tools); init ${surface.initMs} ms; eras ${Object.keys(surface.eras ?? {}).join(", ")}; EOF exit ${surface.eofExitMs} ms (code ${surface.exitCode}); help ops ${surface.helpOps}`);
  },
};
