// W4R help examples against Revit (SPEC §4.1, §6.2, §13.3 W4; owner W3-E2E). Needs Revit with the add-in.
// Every help example of a read or write op, sent as it stands to a disposable project (writes with preview:true, so
// nothing is changed), must pass the add-in's validation too: INVALID_ARGS or UNKNOWN_OP fail the step. Errors about
// the model's content (NOT_FOUND for names the template lacks, HANDLE_EXPIRED for placeholder handles, NEEDS_ACTIVE_DOC,
// UNSUPPORTED_VERSION below the op's minimum year, ...) are expected and only counted.
// Skipped: lifecycle/ui/control/code ops (they open, save, export, press or run code), examples with file paths, and
// examples that carry a confirm token.

import { byName, CATALOG, fullProperties } from "@revit-mcp-next/contracts/catalog";

const VALIDATION = new Set(["INVALID_ARGS", "UNKNOWN_OP"]);
const FILE_PARAMS = ["path", "folder", "local", "template", "file"];

export default {
  id: "W4R",
  title: "Help examples in Revit: every read/write example passes the add-in's validation (writes as previews)",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("W4R");
    t.setTargetPath(path);
    const counts = { sent: 0, ok: 0, expectedErrors: {}, skipped: 0 };
    const failures = [];

    await t.step("every read/write example validates in the add-in", async () => {
      for (const tool of CATALOG) {
        const hasPreview = "preview" in fullProperties(tool);
        for (const [op, spec] of Object.entries(tool.ops)) {
          const kind = spec.meta.kind;
          if (!["read", "write"].includes(kind) || spec.meta.impl === "broker" || tool.optIn) {
            counts.skipped += spec.examples.length;
            continue;
          }
          for (const example of spec.examples) {
            if ("confirm" in example || FILE_PARAMS.some((p) => typeof example[p] === "string") || ("doc" in example && example.doc !== undefined)) {
              counts.skipped += 1;
              continue;
            }
            if (kind === "write" && !hasPreview) {
              counts.skipped += 1;
              continue;
            }
            const args = kind === "write" ? { ...example, preview: true } : { ...example };
            const r = await t.call(tool.name, args, { allowError: true, timeoutMs: 60_000 });
            counts.sent += 1;
            if (r.status === "error") {
              if (VALIDATION.has(r.code)) failures.push(`${tool.name}${op ? "." + op : ""} ${JSON.stringify(example).slice(0, 160)}: ${r.code} ${r.summary.slice(0, 200)}`);
              else counts.expectedErrors[r.code] = (counts.expectedErrors[r.code] ?? 0) + 1;
            } else counts.ok += 1;
          }
        }
      }
      t.log(`sent ${counts.sent} examples: ${counts.ok} ok, errors ${JSON.stringify(counts.expectedErrors)}, skipped ${counts.skipped}`);
      t.assert(failures.length === 0, `${failures.length} example(s) fail the add-in's validation:\n${failures.slice(0, 30).join("\n")}`);
    });

    await t.step("the previews changed nothing", async () => {
      const changes = await t.call("get_changes", { limit: 50 });
      const rows = Array.isArray(changes.json?.rows) ? changes.json.rows : [];
      t.assertEq(rows.length, 0, "no element changed since the session started");
    });
    void byName;
  },
};
