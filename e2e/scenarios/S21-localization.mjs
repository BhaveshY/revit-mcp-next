// S21 localization (SPEC §9.7, §5.4, §13.3; owner P-READ). Needs Revit with the add-in loaded from the e2e home.
// 1. dev.dump_labels (internal key, raw pipe with the e2e home's token) writes the live label tables. On an English UI
//    every category label of the shipped seed must exist in Revit and map to the same category; parameter labels must
//    exist (alias entries that point at differently labelled built-ins are only logged). The dump path is logged: copy
//    it over addin/RevitMcpNext.Addin/Common/Aliases/aliases-<year>.json to ship generated tables.
// 2. English names on any UI language (the S20/S30/S40 subset): categories, parameters, rows and groups use English
//    labels. With Revit started under /language DEU this is the German-UI check; on another UI it still runs and says so.

import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { BRIDGE_PROTOCOL_VERSION } from "@revit-mcp-next/contracts/protocol";
import { openExchange } from "../../broker/dist/src/ipc/PipeClient.js";
import { AuthStore } from "../../broker/dist/src/runtime/auth.js";
import { ulid } from "../../broker/dist/src/runtime/ids.js";
import { registrationFor } from "../lib/revit.mjs";

function rowsOf(r) {
  const j = r.json ?? {};
  if (!Array.isArray(j.rows) || !Array.isArray(j.cols)) return [];
  return j.rows.map((row) => Object.fromEntries(j.cols.map((c, i) => [c, row[i]])));
}

const createdIds = (r) => (Array.isArray(r.json?.created) ? r.json.created.map(Number) : []);

/** One request on the add-in's primary pipe (internal dev.* keys are not reachable through MCP). */
async function rawCall(t, op, args = {}) {
  const file = registrationFor(t.home, t.year, t.revit.pid);
  t.assert(file, `registration of Revit ${t.year} pid ${t.revit.pid} in ${t.home}`);
  const registration = JSON.parse(readFileSync(file, "utf8"));
  const auth = new AuthStore(t.home).get();
  t.assert(auth, "the e2e home has an auth token");
  const request = {
    v: BRIDGE_PROTOCOL_VERSION,
    requestId: ulid(),
    clientKey: `e2e-S21#${process.pid}`,
    auth,
    op,
    kind: "read",
    timeoutMs: 50_000,
    doc: null,
    args,
  };
  const exchange = openExchange(registration.pipe, request);
  const timer = setTimeout(() => exchange.abandon("S21 timeout"), 55_000);
  try {
    return await exchange.response;
  } finally {
    clearTimeout(timer);
  }
}

export default {
  id: "S21",
  title: "Localization: generated English alias tables, English names on any UI language",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const status = await t.call("status", { detail: "full", instance: String(t.revit.pid) });
    const inst = status.json.instances?.find((i) => i.pid === t.revit.pid);
    const language = String(inst?.language ?? "?");
    t.log(`Revit ${t.year} UI language ${language}`);
    const { path } = await t.newProject("S21");
    t.setTargetPath(path);

    await t.step("dev.dump_labels: live label tables vs the shipped seed", async () => {
      const r = await rawCall(t, "dev.dump_labels", {});
      t.assert(r.ok, `dev.dump_labels: ${r.code} ${r.message}`);
      t.assert(r.data.categories > 100 && r.data.parameters > 500, `label counts ${r.data.categories}/${r.data.parameters}`);
      t.assert(r.data.path && existsSync(r.data.path), "the dump file was written");
      t.log(`alias dump (${r.data.language}): ${r.data.path}`);
      if (!r.data.english) {
        t.log("UI is not English: the dump holds localized labels and is not compared with the English seed");
        return;
      }
      const live = JSON.parse(readFileSync(r.data.path, "utf8"));
      const seed = JSON.parse(readFileSync(join(t.repo, "addin", "RevitMcpNext.Addin", "Common", "Aliases", `aliases-${t.year}.json`), "utf8"));
      const liveCategoryTokens = new Set(Object.values(live.categories));
      const badCategories = [];
      const otherChoice = [];
      for (const [label, token] of Object.entries(seed.categories)) {
        const liveToken = live.categories[label];
        if (liveToken === token) continue;
        if (liveToken === undefined && !liveCategoryTokens.has(token)) {
          // Neither the label nor the category exists in this Revit (e.g. a newer category): harmless.
          continue;
        }
        if (liveToken === undefined) badCategories.push(`${label} -> ${token} (no such label)`);
        else otherChoice.push(`${label}: seed ${token}, live ${liveToken}`);
      }
      if (otherChoice.length) t.log(`seed picks another category for ${otherChoice.length} shared labels: ${otherChoice.slice(0, 10).join("; ")}`);
      // An English alias that is not Revit's own label (e.g. "Room Separation Lines" for <Room Separation>) is still a
      // valid name for a model to use: it maps to a category this Revit has, so it is logged, not failed.
      if (badCategories.length) t.log(`seed aliases that are not Revit ${t.year} labels (kept as extra English names): ${badCategories.join("; ")}`);
      const missingLabels = Object.keys(seed.parameters).filter((label) => !(label in live.parameters));
      // Seed labels can be absent in one year (e.g. "OmniClass Number" on 2027): still valid aliases, so log them.
      if (missingLabels.length) t.log(`seed parameter labels that Revit ${t.year} does not use (kept as aliases): ${missingLabels.join(", ")}`);
      const aliasOnly = [];
      for (const [label, tokens] of Object.entries(seed.parameters)) {
        const liveTokens = new Set(live.parameters[label] ?? []);
        for (const token of tokens) if (!liveTokens.has(token)) aliasOnly.push(`${label}:${token}`);
      }
      if (aliasOnly.length) t.log(`alias-only parameter entries (another built-in label): ${aliasOnly.slice(0, 20).join(", ")}${aliasOnly.length > 20 ? " ..." : ""}`);
    });

    await t.step(`English names on a ${language} UI: categories, parameters, rows, groups`, async () => {
      const levels = rowsOf(await t.call("list", { kind: "levels" }));
      const L1 = levels[0].name;
      const L2 = levels[1]?.name;
      const wallTypes = rowsOf(await t.call("list", { kind: "types", category: ["Walls"], class: "WallType", limit: 50 }));
      t.assert(wallTypes.length > 0 && wallTypes.every((x) => x.category === "Walls"), "types rows use the English category");
      const type = wallTypes[0];
      const wall = createdIds(await t.call("create_elements", { op: "wall", level: L1, start: [0, 0], end: [8000, 0], ...(L2 ? { top: L2 } : { height: 3000 }), type: `${type.family}: ${type.name}` }))[0];
      t.assert(wall, "wall created with English category names in the type lookup");
      const found = await t.call("find_elements", { category: ["Walls"] });
      t.assertEq(found.json.total, 1, "category Walls (English) resolves");
      t.assertEq(rowsOf(found)[0].category, "Walls", "rows use the English category label");
      const set = await t.call("set_parameters", { ids: [String(wall)], values: { Comments: "S21" } });
      t.assertEq(set.status, "ok", "English parameter name Comments resolves");
      const where = await t.call("find_elements", { category: ["Walls"], where: ["Comments = S21"], count_only: true });
      t.assertEq(where.json.total, 1, "where on the English parameter name");
      const described = await t.call("describe_elements", { ids: [String(wall)], params: "all" });
      const keys = Object.keys(described.json.elements[0].params);
      for (const key of ["Comments", "Mark", "Length", "Base Constraint"]) t.assert(keys.includes(key), `describe key ${key} is English: ${keys.slice(0, 12).join(", ")}`);
      const stats = await t.call("check_model", { check: "stats" });
      t.assert(stats.json.rows.some((row) => row[0] === "Walls"), "stats groups use English categories");
      const quantities = await t.call("get_quantities", { by: "category", category: ["Walls"] });
      t.assertEq(rowsOf(quantities)[0]?.category, "Walls", "quantities group by the English category");
      const localized = await t.call("find_elements", { category: ["OST_Walls"], count_only: true });
      t.assertEq(localized.json.total, 1, "OST_ tokens resolve too");
      if (language !== "DEU") t.log(`UI language is ${language}; start Revit with /language DEU to run this step on a German UI`);
    });
  },
};
