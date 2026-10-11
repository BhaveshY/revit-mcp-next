// run_csharp (SPEC §11, broker part). The tool is listed only when enableCodeExecution was true at broker start; the
// add-in re-checks the setting on every call. Modes:
//   read      always rolled back; no token.
//   dry_run   rolled back; when the code changed the model the result is NOT APPLIED with a confirm token bound to
//             sha256(code), sha256(canonical args) and the doc key, stored with the code and args (the code is not
//             re-sent): next is "ask the user, then run_csharp {"mode":"commit","confirm":"<token>"}".
//   commit    only through such a token: the stored code and args run in "MCP <wtag> run_csharp" (one undo step).
// While Revit shows its code-execution consent prompt (codeExecution.consentPrompt) the call returns "running ... j#".
import { createHash } from "node:crypto";
import { canonicalJson } from "@revit-mcp-next/contracts/catalog";
import type { ToolContextImpl } from "../framework/context.js";
import type { AddinResult, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

const MAX_EXAMPLE_CODE = 300;
const sha256 = (text: string): string => createHash("sha256").update(text, "utf8").digest("hex");
const isObject = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === "object" && !Array.isArray(v);

/** The code for a fix example: the user's code when short, else a placeholder. */
function exampleCode(code: unknown): string {
  const text = typeof code === "string" ? code : "";
  return text && text.length <= MAX_EXAMPLE_CODE ? text : "<the same code>";
}

async function prepare(ctx: ToolContext): Promise<void | ToolOutcome> {
  const c = ctx as ToolContextImpl;
  const plan = c.confirmPlan;
  if (plan) {
    const planMode = String(plan.args.mode ?? "dry_run");
    if (planMode === "read") {
      c.session.confirms.discard(plan.token);
      return ctx.fail("INVALID_ARGS", `token ${plan.token} belongs to a read-only run, which changes nothing, so there is nothing to commit`, {
        param: "confirm",
        reason: "read-only token",
      });
    }
    const codeSha = isObject(plan.extra) && typeof plan.extra.codeSha === "string" ? plan.extra.codeSha : null;
    if (codeSha && codeSha !== sha256(String(plan.args.code ?? ""))) {
      return ctx.fail("CONFIRM_MISMATCH", `token ${plan.token} does not match its stored code; run the dry run again`, {
        retry: { mode: "dry_run", code: exampleCode(plan.args.code) },
      });
    }
    const doc = await ctx.resolveDoc();
    if (plan.docKey && doc?.key && plan.docKey !== doc.key) {
      return ctx.fail("CONFIRM_MISMATCH", `token ${plan.token} was made for another document than ${doc.title ?? "the target"}; run the dry run in this document first`, {
        retry: { mode: "dry_run", code: exampleCode(plan.args.code) },
      });
    }
    // A token from a dry run always means commit (also tokens issued when job_status renders a finished dry run).
    ctx.args = { ...ctx.args, mode: "commit", audit_token: plan.token };
    return;
  }
  if (ctx.args.mode === "commit") {
    return ctx.fail("INVALID_ARGS", "mode commit needs the confirm token of a dry run: run the code with mode dry_run, show the user what it changes, then commit with that token", {
      param: "confirm",
      reason: "commit without a dry-run token",
      example: { code: exampleCode(ctx.args.code), mode: "dry_run" },
    });
  }
}

async function finish(ctx: ToolContext, r: AddinResult, o: ToolOutcome): Promise<ToolOutcome> {
  const c = ctx as ToolContextImpl;
  if (o.status === "running") {
    const job = isObject(o.data) && typeof o.data.job === "string" ? o.data.job : null;
    return job ? { ...o, next: `ask the user to answer the code-execution prompt in Revit if one is showing, then job_status {"id":"${job}"}` } : o;
  }
  if (o.status !== "ok" || !r.ok) return o;
  const mode = String(ctx.args.mode ?? "dry_run");
  if (mode === "commit") return o;

  // read / dry_run: replace the generic preview token with the code-commit token (or none).
  const data: Record<string, unknown> = isObject(o.data) ? { ...o.data } : {};
  if (typeof data.confirm === "string") c.session.confirms.discard(data.confirm);
  delete data.confirm;
  delete data.expires_min;
  delete data.delete;
  const summary = r.summary || o.summary.replace(/^preview: /, "");
  if (mode === "read" || !r.needsConfirm) return { ...o, status: "ok", summary, data, next: undefined };

  const doc = c.resolvedCache;
  const args: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(ctx.args)) if (!["preview", "confirm", "audit_token"].includes(k)) args[k] = v;
  args.mode = "commit";
  const token = c.session.confirms.issue({
    tool: ctx.tool,
    key: ctx.key,
    op: null,
    args,
    docKey: doc?.key ?? null,
    instanceId: doc?.instanceId ?? null,
    stamp: r.needsConfirm.stamp ?? 0,
    deleteSet: [],
    rule: "code_commit",
    plan: r.needsConfirm.plan,
    extra: { codeSha: sha256(String(ctx.args.code ?? "")), argsSha: sha256(canonicalJson(ctx.args.args ?? {})) },
  });
  return {
    ...o,
    status: "not_applied",
    summary,
    data: { ...data, confirm: token, expires_min: 10 },
    next: `ask the user, then run_csharp {"mode":"commit","confirm":"${token}"}`,
  };
}

export const module: ToolModule = { name: "run_csharp", prepare, finish };
