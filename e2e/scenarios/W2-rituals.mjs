// W2 no copying rituals, offline part (SPEC §5.5, §6.4, §6.6, §13.3 W2, D4 §8.9; owner W3-E2E). No Revit.
// A weak model must never have to copy add-in internals or re-send a whole call to continue:
//   1. No ErrorCatalog template renders an add-in internal id (request/job/instance ids, UniqueIds, hashes) into a
//      fix: line; add-in ids stay internal (SPEC §6.6).
//   2. No catalog example carries a GUID, ULID or long hex token (examples are copied verbatim).
//   3. help next: lines and the INVALID_ARGS fix: lines of all 243 keys stay short and carry no opaque tokens.
//   4. The server instructions tell the model that NOT APPLIED needs the user's OK and that the confirm call is only
//      op + confirm (C6), and the confirm param is advertised on every op that has a blast rule.
// The run-level hints gate checks the live shapes on every call (NOT APPLIED next: = "ask the user, then <tool>
// {op, confirm}" with a 6-char token; more: = <tool> {"page":"p#"} repeated in the summary; running → job_status j#).

import { join } from "node:path";
import { byName, CATALOG, ERRORS, fullProperties, REGISTRY } from "@revit-mcp-next/contracts/catalog";
import { templatesOf } from "@revit-mcp-next/contracts/errors";
import { INSTRUCTIONS } from "@revit-mcp-next/contracts/catalog/instructions";
import { makeCaller } from "../lib/call.mjs";
import { ritualProblems } from "../lib/hints.mjs";

const INTERNAL = /request_?id|requestid|job_?id|jobid|instance_?id|instanceid|unique_?id|uniqueid|ulid|guid|sha256|fingerprint|addin_?id/i;
const OPAQUE = [/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i, /\b[0-9A-HJKMNP-TV-Z]{26}\b/, /\b[0-9a-f]{24,}\b/i];

export default {
  id: "W2",
  title: "No copying rituals (offline): templates, examples, hint lines, NOT APPLIED instructions",
  profiles: ["surface", "quick", "full"],
  years: "none",
  async run(t) {
    const failures = [];

    await t.step("error templates never render add-in internal ids", () => {
      for (const [code, spec] of Object.entries(ERRORS)) {
        const templates = [...templatesOf(spec), ...(spec.or ? (Array.isArray(spec.or) ? spec.or : [spec.or]) : [])];
        // ask templates may name ids for the user to report (INTERNAL_ERROR requestId); calls must never carry them.
        for (const m of JSON.stringify(templates.filter((x) => x.kind !== "ask")).matchAll(/\$\{([A-Za-z0-9_.]+)\}/g)) if (INTERNAL.test(m[1])) failures.push(`${code}: template uses \${${m[1]}}`);
      }
      t.assert(failures.length === 0, failures.join("\n"));
    });

    await t.step("catalog examples carry no GUIDs, ULIDs or long hex tokens", () => {
      for (const tool of CATALOG) {
        for (const [op, spec] of Object.entries(tool.ops)) {
          const text = JSON.stringify(spec.examples);
          for (const re of OPAQUE) if (re.test(text)) failures.push(`${tool.name}${op ? "." + op : ""}: example carries ${re.exec(text)[0]}`);
        }
      }
      t.assert(failures.length === 0, failures.join("\n"));
    });

    const session = await t.connect({ home: join(t.runRoot, "w2") });
    const call = makeCaller({ session });
    try {
      await t.step("help next: and INVALID_ARGS fix: lines are short and token-free for every key", async () => {
        let longest = 0;
        for (const r of REGISTRY) {
          if (byName.get(r.tool)?.optIn) continue;
          const help = await call("help", r.op ? { tool: r.tool, op: r.op } : { tool: r.tool });
          for (const p of ritualProblems(help)) failures.push(`help ${r.key}: ${p}`);
          const spec = byName.get(r.tool);
          if (!spec.ops[r.op ?? ""]?.req.trim()) continue;
          const res = await call(r.tool, spec.discriminator && r.op ? { [spec.discriminator]: r.op } : {}, { allowError: true });
          longest = Math.max(longest, (res.fix ?? "").length);
          for (const p of ritualProblems(res)) failures.push(`${r.key} INVALID_ARGS: ${p}`);
        }
        t.assert(failures.length === 0, failures.slice(0, 20).join("\n"));
        t.log(`longest INVALID_ARGS fix: ${longest} chars`);
      });
    } finally {
      await session.close();
    }

    await t.step("instructions: NOT APPLIED needs the user's OK; confirm is advertised wherever a blast rule applies", () => {
      t.assertMatch(INSTRUCTIONS, /NOT APPLIED/, "instructions mention NOT APPLIED");
      t.assertMatch(INSTRUCTIONS, /ask|user/i, "instructions say to ask the user");
      for (const r of REGISTRY) {
        if (!r.blast.length || r.blast.every((b) => b === "button")) continue;
        if (!("confirm" in fullProperties(byName.get(r.tool)))) failures.push(`${r.key} has blast rules ${r.blast.join(",")} but no confirm param`);
      }
      t.assert(failures.length === 0, failures.join("\n"));
    });
  },
};
