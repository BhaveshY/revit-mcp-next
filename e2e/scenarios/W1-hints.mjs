// W1 hint following, offline part (SPEC §13.3 W1, D4 §8.9; owner W3-E2E). No Revit.
// A weak model copies fix:/next: lines verbatim, so every call those lines name must exist and must validate:
//   1. help {tool, op} for all 243 keys: the next: line is a call to an existing tool/op that passes broker validation.
//   2. Every op with required params, called with only its discriminator: ERROR INVALID_ARGS whose fix: is a complete
//      call; following that fix verbatim never gives INVALID_ARGS/UNKNOWN_OP again (no Revit: NO_REVIT_RUNNING is fine).
//   3. A misspelt op on every tool with ops: UNKNOWN_OP whose fix: names the closest real op; following it validates.
//   4. Every ErrorCatalog template rendered by the broker's renderer with synthetic details: every call it renders
//      names an existing tool, op and params.
// The run-level hints gate repeats the W1/W2 line checks on every call of every scenario (lib/hints.mjs).

import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { byName, ERRORS, fullProperties, REGISTRY } from "@revit-mcp-next/contracts/catalog";
import { templatesOf } from "@revit-mcp-next/contracts/errors";
import { makeCaller } from "../lib/call.mjs";
import { callProblems, extractCalls } from "../lib/hints.mjs";

const VALIDATION_CODES = new Set(["INVALID_ARGS", "UNKNOWN_OP"]);

async function lineProblems(line) {
  const problems = [];
  for (const call of extractCalls(line)) problems.push(...(await callProblems(call)));
  return problems;
}

/** Synthetic details for a template: every ${details.x.y} path gets a plausible value for the tool. */
function syntheticDetails(templates, spec) {
  const details = {};
  const firstParam = Object.keys(fullProperties(spec)).find((k) => k !== spec.discriminator && k !== "doc") ?? "name";
  const set = (path, value) => {
    const parts = path.split(".");
    let node = details;
    for (let i = 0; i < parts.length - 1; i++) {
      const key = parts[i];
      const nextIsIndex = /^\d+$/.test(parts[i + 1]);
      node[key] ??= nextIsIndex ? [] : {};
      node = node[key];
    }
    node[parts[parts.length - 1]] = value;
  };
  const text = JSON.stringify(templates);
  for (const m of text.matchAll(/\$\{details\.([A-Za-z0-9_.]+)\}/g)) {
    const path = m[1];
    const last = path.split(".").pop();
    if (last === "param") set(path, firstParam);
    else if (last === "closest") set(path, spec.discriminator ? Object.keys(spec.ops)[0] : "x");
    else if (/^\d+$/.test(last)) set(path, "E2E value");
    else if (/year/i.test(last)) set(path, 2024);
    else if (/example|args/i.test(last)) set(path, spec.ops[Object.keys(spec.ops)[0]]?.examples?.[0] ?? {});
    else set(path, "E2E value");
  }
  return details;
}

export default {
  id: "W1",
  title: "Hint following (offline): help next lines, INVALID_ARGS and UNKNOWN_OP fixes, rendered error templates",
  profiles: ["surface", "quick", "full"],
  years: "none",
  async run(t) {
    // A home with code execution enabled lists run_csharp too (all 38 tools); no Revit is involved.
    const home = join(t.runRoot, "w1");
    mkdirSync(join(home, "config"), { recursive: true });
    writeFileSync(join(home, "config", "settings.json"), JSON.stringify({ enableCodeExecution: true }));
    const session = await t.connect({ home });
    const call = makeCaller({ session });
    const failures = [];
    try {
      await t.step("help {tool, op}: next: is a valid call for every key", async () => {
        for (const r of REGISTRY) {
          const args = r.op ? { tool: r.tool, op: r.op } : { tool: r.tool };
          const res = await call("help", args);
          if (!res.next) {
            failures.push(`help ${r.key}: no next: line`);
            continue;
          }
          for (const p of await lineProblems(res.next)) failures.push(`help ${r.key} next: ${p}`);
        }
        t.assert(failures.length === 0, `help next lines:\n${failures.slice(0, 20).join("\n")}`);
      });

      let followed = 0;
      await t.step("INVALID_ARGS fixes are complete calls, and following them validates", async () => {
        for (const r of REGISTRY) {
          const spec = byName.get(r.tool);
          const op = spec.ops[r.op ?? ""];
          if (!op || !op.req.trim()) continue;
          const args = spec.discriminator && r.op ? { [spec.discriminator]: r.op } : {};
          const res = await call(r.tool, args, { allowError: true });
          if (res.status !== "error" || res.code !== "INVALID_ARGS") {
            failures.push(`${r.key} with no params: expected INVALID_ARGS, got ${res.status} ${res.code ?? ""}`);
            continue;
          }
          for (const p of await lineProblems(res.fix)) failures.push(`${r.key} fix: ${p}`);
          const fix = extractCalls(res.fix).find((c) => c.atStart && byName.has(c.tool));
          if (!fix?.args) {
            if (!/^(ask the user|retry in)/.test(res.fix)) failures.push(`${r.key}: fix is not a call: ${res.fix.slice(0, 120)}`);
            continue;
          }
          const again = await call(fix.tool, fix.args, { allowError: true });
          followed += 1;
          if (again.status === "error" && VALIDATION_CODES.has(again.code)) failures.push(`${r.key}: following the fix ${res.fix.slice(0, 160)} gives ${again.code}: ${again.summary.slice(0, 160)}`);
        }
        t.assert(failures.length === 0, `INVALID_ARGS fixes:\n${failures.slice(0, 20).join("\n")}`);
        t.log(`followed ${followed} INVALID_ARGS fixes verbatim`);
      });

      await t.step("UNKNOWN_OP fixes name the closest op, and following them validates", async () => {
        for (const spec of byName.values()) {
          if (!spec.discriminator) continue;
          const ops = Object.keys(spec.ops);
          const target = ops[ops.length - 1];
          const typo = target.length > 3 ? target.slice(0, -1) + (target.endsWith("x") ? "y" : "x") : `${target}xx`;
          if (ops.includes(typo)) continue;
          // A realistic slip: the op's own example with the op name misspelt (the other args are right).
          const example = spec.ops[target].examples[0] ?? {};
          const res = await call(spec.name, { ...example, [spec.discriminator]: typo }, { allowError: true });
          if (res.status !== "error" || !VALIDATION_CODES.has(res.code)) {
            failures.push(`${spec.name} ${spec.discriminator}=${typo}: expected UNKNOWN_OP/INVALID_ARGS, got ${res.status} ${res.code ?? ""}`);
            continue;
          }
          for (const p of await lineProblems(res.fix)) failures.push(`${spec.name} ${typo} fix: ${p}`);
          const fix = extractCalls(res.fix).find((c) => c.atStart && byName.has(c.tool));
          if (fix?.args && fix.tool === spec.name && fix.args[spec.discriminator] !== undefined && !ops.includes(fix.args[spec.discriminator])) failures.push(`${spec.name} ${typo}: fix names ${fix.args[spec.discriminator]}, not a real op`);
        }
        t.assert(failures.length === 0, `UNKNOWN_OP fixes:\n${failures.slice(0, 20).join("\n")}`);
      });

      await t.step("every ErrorCatalog template renders calls to existing tools, ops and params", async () => {
        const { renderFix } = await import(pathToFileURL(join(t.repo, "broker", "dist", "src", "framework", "errors.js")).href);
        let rendered = 0;
        for (const [code, specErr] of Object.entries(ERRORS)) {
          const templates = [...templatesOf(specErr), ...(specErr.or ? (Array.isArray(specErr.or) ? specErr.or : [specErr.or]) : [])];
          const users = REGISTRY.filter((r) => (byName.get(r.tool).ops[r.op ?? ""]?.errors ?? []).includes(code)).slice(0, 3);
          const contexts = users.length ? users : [{ tool: "find_elements", op: null, key: "find_elements" }];
          for (const u of contexts) {
            const spec = byName.get(u.tool);
            const opSpec = spec.ops[u.op ?? ""];
            const args = { ...(opSpec?.examples?.[0] ?? {}) };
            const details = syntheticDetails(templates, spec);
            const out = renderFix({ code, tool: u.tool, op: u.op, key: u.key, args, details, doc: { n: 1, title: "E2E", year: 2024 } });
            rendered += 1;
            for (const line of [out.fix]) {
              for (const c of extractCalls(line)) {
                if (!byName.has(c.tool)) continue;
                const s = byName.get(c.tool);
                if (c.parseError) failures.push(`${code} (${u.key}): unparsable call ${c.raw.slice(0, 120)}`);
                else if (c.args && s.discriminator && c.args[s.discriminator] !== undefined && !(c.args[s.discriminator] in s.ops)) failures.push(`${code} (${u.key}): unknown op ${c.args[s.discriminator]} in ${line.slice(0, 160)}`);
                else if (c.args) for (const k of Object.keys(c.args)) if (!(k in fullProperties(s))) failures.push(`${code} (${u.key}): unknown param ${c.tool}.${k} in ${line.slice(0, 160)}`);
              }
            }
          }
        }
        t.assert(failures.length === 0, `rendered templates:\n${failures.slice(0, 20).join("\n")}`);
        t.log(`rendered ${rendered} error fixes over ${Object.keys(ERRORS).length} codes`);
      });
    } finally {
      await session.close();
    }
  },
};
