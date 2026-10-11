// Catalog entry for `model_delivery` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { I, LIFE, meta, O, op, P, S, SA, WD_OPEN } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(LIFE, { scope: "none", job: "always" });

export const tool: ToolSpec = {
  name: "model_delivery",
  title: "Model delivery package",
  description: "Delivery packages of standalone RVTs (links, cleanup, exports, QA) from a saved recipe: inspect sources, preview (gives confirm), execute (job), save/get/list recipes, test fixture. Recipe shape: help topic=recipe.",
  group: "write",
  annotations: WD_OPEN,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    sources: SA("inspect: source .rvt paths."),
    project: S("Project id (recipe library key)."),
    recipe: O("Delivery recipe; shape in help {topic:'recipe'}."),
    expected_sha: S("save_recipe: only overwrite this version."),
    template: S("fixture: .rte template path."),
    folder: S("fixture: new empty folder."),
    id: S("fixture: fixture id."),
    limit: I("list_recipes: default 100."),
    confirm: P.confirm,
  },
  required: ["op"],
  ops: {
    inspect: op("sources", "project,recipe", meta(M, { kind: "read" }), [{"op":"inspect","sources":["\\\\srv\\proj\\A.rvt","\\\\srv\\proj\\S.rvt"],"project":"P2026-017"}], { help: "Opens isolated copies of the sources and reports links, views and the decisions still needed; project compares with the saved recipe of that project.", errors: ["NOT_FOUND", "INVALID_ARGS", "REVIT_REFUSED"] }),
    preview: op("recipe", "", meta(M, { kind: "read" }), [{"op":"preview","recipe":{"projectId":"P2026-017","recipeVersion":"1","deliveryId":"d-2026-10-02","packageName":"P2026-017_Delivery","destinationRoot":"\\\\srv\\deliveries","sourceModels":[{"id":"arch","sourcePath":"\\\\srv\\proj\\A.rvt","targetFileName":"P2026-017_A.rvt"},{"id":"struct","sourcePath":"\\\\srv\\proj\\S.rvt","targetFileName":"P2026-017_S.rvt"}],"linkRules":[{"sourceModelId":"arch","matchFileName":"S.rvt","action":"repath","targetModelId":"struct"}],"coordinates":{"preserveLinkTransforms":true,"packagedLinkPathType":"relative"},"cleanup":{"deleteSheets":true,"deleteViews":true,"deleteSchedules":true,"deleteLegends":true,"deleteDraftingViews":true,"deleteViewTemplates":false,"deleteUnusedFilters":true,"removeUnmappedLinks":true,"purgeUnusedPasses":3,"protectedViewNames":["{3D}"]},"exports":[{"format":"ifc","required":true}],"qa":{"requireStandalone":true,"requireNoCentralPath":true,"requireSourceHashUnchanged":true,"requireCleanupMatchesPreview":true,"requireAllRequiredExports":true}}}], { help: "Checks the recipe against the sources (closed in Revit) and plans every model; ready plans give a confirm token for execute, blockers say what to fix.", errors: ["INVALID_ARGS", "NOT_FOUND", "REVIT_BUSY"] }),
    execute: op("confirm", "", meta(M, { blast: ["always"] }), [{"op":"execute","confirm":"K7QM2X"}], { help: "Takes only the confirm token from preview and starts job j#: copies, links, cleanup, exports and QA in staging, then publishes the package atomically. Sources never change.", errors: ["CONFIRM_EXPIRED", "CONFIRM_STALE", "REVIT_BUSY", "REVIT_REFUSED"] }),
    save_recipe: op("recipe", "expected_sha", meta(M, { kind: "control", impl: "broker", job: "never" }), [{"op":"save_recipe","recipe":{"projectId":"P2026-017","recipeVersion":"1","deliveryId":"d-2026-10-02","packageName":"P2026-017_Delivery","destinationRoot":"\\\\srv\\deliveries","sourceModels":[{"id":"arch","sourcePath":"\\\\srv\\proj\\A.rvt","targetFileName":"P2026-017_A.rvt"},{"id":"struct","sourcePath":"\\\\srv\\proj\\S.rvt","targetFileName":"P2026-017_S.rvt"}],"linkRules":[{"sourceModelId":"arch","matchFileName":"S.rvt","action":"repath","targetModelId":"struct"}],"coordinates":{"preserveLinkTransforms":true,"packagedLinkPathType":"relative"},"cleanup":{"deleteSheets":true,"deleteViews":true,"deleteSchedules":true,"deleteLegends":true,"deleteDraftingViews":true,"deleteViewTemplates":false,"deleteUnusedFilters":true,"removeUnmappedLinks":true,"purgeUnusedPasses":3,"protectedViewNames":["{3D}"]},"exports":[{"format":"ifc","required":true}],"qa":{"requireStandalone":true,"requireNoCentralPath":true,"requireSourceHashUnchanged":true,"requireCleanupMatchesPreview":true,"requireAllRequiredExports":true}}}]),
    get_recipe: op("project", "", meta(M, { kind: "read", impl: "broker", job: "never" }), [{"op":"get_recipe","project":"P2026-017"}], { errors: ["NOT_FOUND"] }),
    list_recipes: op("", "limit", meta(M, { kind: "read", impl: "broker", job: "never" }), [{"op":"list_recipes"}]),
    fixture: op("template,folder,id", "", meta(M, { blast: ["always"] }), [{"op":"fixture","template":"C:\\ProgramData\\Autodesk\\RVT 2024\\Templates\\English\\Default-Multi-Discipline_Metric.rte","folder":"C:\\Temp\\delivery-fixture","id":"fx1"}], { help: "Test only: creates two linked central models and a matching recipe in a new folder.", errors: ["NAME_TAKEN", "NOT_FOUND"] }),
  },
};
