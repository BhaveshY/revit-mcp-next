// Tool module for `model_delivery` (SPEC Appendix A/B, §6.3 rule always, §6.4 confirm tokens).
//   save_recipe / get_recipe / list_recipes  broker only: the recipe library in <home>/recipes (sha-versioned files).
//   inspect   add-in job; `project` loads the saved recipe as the previous recipe (repeat deliveries).
//   preview   add-in job; a ready plan gets a confirm token for op execute that carries previewId + planHash + recipe.
//             A preview still running at the call budget issues the token when it finishes (notice DELIVERY_READY).
//   execute   only with that token: sends the stored recipe/previewId/planHash with confirmed{} as a job (j#).
//   fixture   add-in job behind the default static confirm (rule always).
import { join } from "node:path";
import type { ModelDeliveryRecipe } from "@revit-mcp-next/contracts";
import { defaultHandler } from "../framework/defaults.js";
import { formatCall } from "../framework/errors.js";
import type { ToolContextImpl } from "../framework/context.js";
import { Deadline } from "../runtime/deadline.js";
import { pollJob } from "../jobs/JobRunner.js";
import { ModelDeliveryRecipeStore } from "../recipes/ModelDeliveryRecipeStore.js";
import type { AddinResult, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

/** How long a preview that outlived the call budget is followed in the background. */
const BACKGROUND_PREVIEW_MS = 30 * 60_000;

const isObject = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === "object" && !Array.isArray(v);

function store(ctx: ToolContext): ModelDeliveryRecipeStore {
  return new ModelDeliveryRecipeStore(join(ctx.services.home, "recipes"));
}

/** Shape checks before anything is stored or sent (the add-in validates paths and references in depth). */
function recipeProblem(recipe: unknown): string | null {
  if (!isObject(recipe)) return 'recipe must be an object (shape: help {"topic":"recipe"})';
  for (const field of ["projectId", "recipeVersion", "deliveryId", "packageName", "destinationRoot"]) {
    const value = recipe[field];
    if (typeof value !== "string" || !value.trim()) return `recipe.${field} is required (a non-empty string)`;
  }
  if (String(recipe.projectId) !== String(recipe.projectId).trim()) return "recipe.projectId must not start or end with spaces";
  if (!Array.isArray(recipe.sourceModels) || recipe.sourceModels.length === 0) return "recipe.sourceModels needs at least one {id, sourcePath, targetFileName}";
  for (const [i, s] of (recipe.sourceModels as unknown[]).entries()) {
    if (!isObject(s) || typeof s.id !== "string" || typeof s.sourcePath !== "string" || typeof s.targetFileName !== "string")
      return `recipe.sourceModels[${i}] needs id, sourcePath and targetFileName`;
  }
  for (const field of ["linkRules", "exports"]) if (recipe[field] !== undefined && !Array.isArray(recipe[field])) return `recipe.${field} must be a list`;
  for (const field of ["coordinates", "cleanup", "qa"]) if (!isObject(recipe[field])) return `recipe.${field} is required (an object)`;
  return null;
}

function invalidRecipe(ctx: ToolContext, problem: string): ToolOutcome {
  return ctx.fail("INVALID_ARGS", problem, { param: "recipe", reason: problem, example: { op: ctx.op, recipe: { projectId: "P2026-017", "...": 'help {"topic":"recipe"}' } } });
}

// ------------------------------------------------------------------------------------------------ recipe library

/** A complete inspect call for a recipe's source models (a next: line must be a call a model can send verbatim). */
function inspectCall(recipe: Record<string, unknown>, projectId: string): string {
  const models = Array.isArray(recipe.sourceModels) ? (recipe.sourceModels as Array<Record<string, unknown>>) : [];
  const sources = models.map((s) => s.sourcePath).filter((p): p is string => typeof p === "string" && p.length > 0);
  return formatCall("model_delivery", { op: "inspect", sources, project: projectId }, "op");
}

async function saveRecipe(ctx: ToolContext): Promise<ToolOutcome> {
  const recipe = ctx.args.recipe;
  const problem = recipeProblem(recipe);
  if (problem) return invalidRecipe(ctx, problem);
  const expected = typeof ctx.args.expected_sha === "string" && ctx.args.expected_sha.trim() ? ctx.args.expected_sha.trim() : undefined;
  try {
    const saved = await store(ctx).save(recipe as unknown as ModelDeliveryRecipe, expected);
    return {
      status: "ok",
      summary: saved.changed
        ? `saved recipe ${saved.projectId} version ${saved.recipeVersion} (sha ${saved.recipeSha256.slice(0, 12)})`
        : `recipe ${saved.projectId} is unchanged (sha ${saved.recipeSha256.slice(0, 12)})`,
      data: { project: saved.projectId, version: saved.recipeVersion, sha: saved.recipeSha256, changed: saved.changed, saved_at: saved.savedAtUtc },
      next: `${inspectCall(recipe as Record<string, unknown>, saved.projectId)} compares the sources with this recipe; then preview with the recipe`,
      doc: null,
    };
  } catch (error) {
    const message = (error as Error)?.message ?? String(error);
    if (/conflict/i.test(message)) {
      const current = await store(ctx).get(String((recipe as Record<string, unknown>).projectId)).catch(() => undefined);
      return ctx.fail("INVALID_ARGS", message, { param: "expected_sha", reason: "the saved recipe changed", current: current?.recipeSha256 });
    }
    return ctx.fail("INTERNAL_ERROR", `the recipe could not be saved: ${message}`, {});
  }
}

async function getRecipe(ctx: ToolContext): Promise<ToolOutcome> {
  const project = String(ctx.args.project ?? "").trim();
  if (!project) return ctx.fail("INVALID_ARGS", "project is required (the recipe library key)", { param: "project", reason: "required", example: { op: "get_recipe", project: "P2026-017" } });
  const lib = store(ctx);
  const stored = await lib.get(project).catch((error: Error) => error);
  if (stored instanceof Error) return ctx.fail("INTERNAL_ERROR", `the recipe library could not be read: ${stored.message}`, {});
  if (!stored) {
    const all = await lib.list().catch(() => []);
    return ctx.fail("NOT_FOUND", `no saved recipe for project ${project}`, { param: "project", kind: "recipe", value: project, candidates: all.map((r) => r.projectId).slice(0, 8) });
  }
  return {
    status: "ok",
    summary: `recipe ${stored.projectId} version ${stored.recipeVersion}, ${stored.recipe.sourceModels.length} model(s), saved ${stored.savedAtUtc}`,
    data: { project: stored.projectId, version: stored.recipeVersion, sha: stored.recipeSha256, saved_at: stored.savedAtUtc, recipe: stored.recipe },
    next: `${inspectCall(stored.recipe as unknown as Record<string, unknown>, stored.projectId)} detects changes; then preview with the recipe`,
    doc: null,
  };
}

async function listRecipes(ctx: ToolContext): Promise<ToolOutcome> {
  const limit = typeof ctx.args.limit === "number" && ctx.args.limit > 0 ? Math.min(1000, Math.floor(ctx.args.limit)) : 100;
  const all = await store(ctx).list().catch((error: Error) => error);
  if (all instanceof Error) return ctx.fail("INTERNAL_ERROR", `the recipe library could not be read: ${all.message}`, {});
  const rows = all.slice(0, limit).map((r) => [r.projectId, r.recipeVersion, r.sourceModelCount, r.packageName, r.savedAtUtc, r.recipeSha256.slice(0, 12)]);
  return {
    status: "ok",
    summary: all.length ? `${all.length} saved recipe${all.length === 1 ? "" : "s"}${all.length > rows.length ? `, showing ${rows.length}` : ""}` : "no saved recipes",
    data: { total: all.length, cols: ["project", "version", "models", "package", "saved_at", "sha"], rows },
    next: all.length ? `model_delivery {"op":"get_recipe","project":"${all[0]!.projectId}"}` : `help {"topic":"recipe"} shows the recipe for model_delivery save_recipe`,
    doc: null,
  };
}

// ------------------------------------------------------------------------------------------------ add-in ops

async function inspect(ctx: ToolContext): Promise<ToolOutcome> {
  let recipe = isObject(ctx.args.recipe) ? ctx.args.recipe : undefined;
  const project = typeof ctx.args.project === "string" ? ctx.args.project.trim() : "";
  let loaded: string | undefined;
  if (!recipe && project) {
    const stored = await store(ctx).get(project).catch(() => undefined);
    if (stored) {
      recipe = stored.recipe as unknown as Record<string, unknown>;
      loaded = `${stored.projectId} v${stored.recipeVersion}`;
    }
  }
  const args: Record<string, unknown> = { op: "inspect", sources: ctx.args.sources };
  if (recipe) args.recipe = recipe;
  const r = await ctx.call("model_delivery.inspect", args, { asJob: true });
  const o = ctx.outcomeFrom(r);
  if (o.status === "ok") {
    if (loaded) o.notices = [...(o.notices ?? []), `RECIPE_LOADED compared with the saved recipe ${loaded}`];
    else if (project) o.notices = [...(o.notices ?? []), `RECIPE_NONE no saved recipe for ${project}; this is a first delivery`];
  }
  return o;
}

function readyPlan(r: AddinResult): { previewId: string; planHash: string } | null {
  if (!r.ok || !isObject(r.data)) return null;
  const d = r.data;
  if (d.ready !== true || typeof d.previewId !== "string" || typeof d.planHash !== "string") return null;
  return { previewId: d.previewId, planHash: d.planHash };
}

function issueExecuteToken(ctx: ToolContext, r: AddinResult, recipe: Record<string, unknown>, plan: { previewId: string; planHash: string }, summary: string): string {
  return ctx.session.confirms.issue({
    tool: "model_delivery",
    key: "model_delivery.execute",
    op: "execute",
    args: { op: "execute" },
    docKey: null,
    instanceId: r.instanceId ?? null,
    stamp: 0,
    deleteSet: [],
    rule: "always",
    plan: summary,
    extra: { recipe, previewId: plan.previewId, planHash: plan.planHash, instanceId: r.instanceId ?? null },
  });
}

async function preview(ctx: ToolContext): Promise<ToolOutcome> {
  const recipe = ctx.args.recipe;
  const problem = recipeProblem(recipe);
  if (problem) return invalidRecipe(ctx, problem);
  const r = await ctx.call("model_delivery.preview", { op: "preview", recipe }, { asJob: true });
  const o = ctx.outcomeFrom(r);

  // Still running at the budget: follow the job and issue the token when it is ready.
  if (o.status === "running" && r.job?.jobId && r.instanceId) {
    followPreview(ctx, r, recipe as Record<string, unknown>);
    o.notices = [...(o.notices ?? []), `DELIVERY_PENDING the confirm token for execute arrives as notice DELIVERY_READY when ${r.jobRef ?? "the preview"} finishes`];
    return o;
  }
  if (o.status !== "ok") return o;
  const plan = readyPlan(r);
  if (!plan) {
    o.next = `fix the blockers listed above, then model_delivery {"op":"preview","recipe":...} again`;
    return o;
  }
  const token = issueExecuteToken(ctx, r, recipe as Record<string, unknown>, plan, `run delivery ${String((recipe as Record<string, unknown>).deliveryId)}: ${o.summary}`);
  o.data = { ...(isObject(o.data) ? o.data : {}), confirm: token, expires_min: 10 };
  o.next = `ask the user, then model_delivery {"op":"execute","confirm":"${token}"}`;
  return o;
}

/** Background follow-up of a preview job that outlived the call budget. */
function followPreview(ctx: ToolContext, r: AddinResult, recipe: Record<string, unknown>): void {
  const services = ctx.services;
  const session = ctx.session;
  const jobId = r.job!.jobId;
  const ref = r.jobRef ?? jobId;
  void (async () => {
    try {
      const live = await services.registry.list();
      const inst = live.find((i) => i.instanceId === r.instanceId);
      if (!inst) return;
      const polled = await pollJob({ channel: services.channels.get(inst), jobId, deadline: Deadline.start(BACKGROUND_PREVIEW_MS), clientKey: session.clientKey, reserveMs: 0 });
      const result = polled.result;
      if (!polled.terminal || !result) return;
      const done: AddinResult = { ...result, key: "model_delivery.preview", instanceId: inst.instanceId, year: inst.year, pid: inst.pid };
      const plan = readyPlan(done);
      if (!plan) {
        session.pendingNotices.push(`DELIVERY_BLOCKED preview ${ref} finished with blockers; job_status {"id":"${ref}"} lists them`);
        return;
      }
      const token = issueExecuteToken(ctx, done, recipe, plan, `run delivery ${String(recipe.deliveryId)}: ${done.summary ?? "approved preview"}`);
      session.pendingNotices.push(`DELIVERY_READY preview ${ref} is ready; ask the user, then model_delivery {"op":"execute","confirm":"${token}"}`);
    } catch (error) {
      services.log.warn("delivery_preview_follow", { error: (error as Error)?.message ?? String(error) });
    }
  })();
}

async function execute(ctx: ToolContext): Promise<ToolOutcome> {
  const plan = (ctx as ToolContextImpl).confirmPlan;
  const extra = plan?.extra;
  if (!plan || plan.key !== "model_delivery.execute" || !extra || !isObject(extra.recipe) || typeof extra.previewId !== "string" || typeof extra.planHash !== "string") {
    return ctx.fail("CONFIRM_EXPIRED", "execute needs the confirm token of a ready model_delivery preview", { reason: "no delivery preview token" });
  }
  ctx.session.confirms.consume(plan.token);
  const args = { op: "execute", recipe: extra.recipe, preview_id: extra.previewId, plan_hash: extra.planHash };
  const r = await ctx.call("model_delivery.execute", args, { asJob: true, confirmed: { stamp: 0 } });
  return ctx.outcomeFrom(r);
}

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  switch (ctx.op) {
    case "save_recipe":
      return saveRecipe(ctx);
    case "get_recipe":
      return getRecipe(ctx);
    case "list_recipes":
      return listRecipes(ctx);
    case "inspect":
      return inspect(ctx);
    case "preview":
      return preview(ctx);
    case "execute":
      return execute(ctx);
    default:
      // fixture: static confirm (rule always), then the add-in job.
      return defaultHandler(ctx);
  }
}

export const module: ToolModule = { name: "model_delivery", handle };
