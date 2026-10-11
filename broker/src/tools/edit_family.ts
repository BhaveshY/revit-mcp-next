// Tool module for `edit_family` (P-FAMILY). Every op works through the default handlers (framework/defaults.ts);
// load_into adds a prepare hook (SPEC §5.9, §6.3 multi_doc, §7.4 projectPin):
//   - `into` items use the doc grammar (#n, exact title, path, title@year) and must be project docs of the same Revit
//     as the family doc; without `into` the session's project pin is the target.
//   - The resolved docs are sent as canonical `rid:<rid>|<docKey>` tokens, so the add-in loads into exactly the docs
//     the user saw (titles can repeat across folders).
//   - More than one target doc returns NOT APPLIED (rule multi_doc) naming the docs; the confirm call replays the
//     canonical targets.
import { formatCall } from "../framework/errors.js";
import type { ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";
import { docLabel, stripExt } from "../targeting/docParam.js";

const CANONICAL = /^rid:(\d+)\|(.+)$/;

interface Target {
  rid: number;
  key: string;
  label: string;
}

export const module: ToolModule = { name: "edit_family", prepare };

async function prepare(ctx: ToolContext): Promise<void | ToolOutcome> {
  if (ctx.op !== "load_into") return;
  const family = await ctx.resolveDoc();
  if (!family || family.rid === null || !family.key) return;

  const raw = ctx.args.into;
  const items = (Array.isArray(raw) ? raw : raw === undefined || raw === null || raw === "" ? [] : [raw]).map((v) => String(v).trim()).filter((v) => v.length > 0);
  const targets: Target[] = [];

  if (items.length === 0) {
    const pin = ctx.session.projectPin;
    // Without a project pin the add-in falls back to the project the family came from, or the only open project.
    if (!pin || pin.instanceId !== family.instanceId) return;
    targets.push({ rid: pin.rid, key: pin.key, label: docLabel({ n: ctx.session.docNumber(pin.key), title: pin.title, year: pin.year }) });
  } else {
    for (const item of items) {
      const canonical = CANONICAL.exec(item);
      if (canonical) {
        const key = canonical[2]!;
        targets.push({ rid: Number(canonical[1]), key, label: labelOfKey(ctx, key, family.year) });
        continue;
      }
      const outcome = await ctx.services.resolver.resolve({
        session: ctx.session,
        doc: item,
        scope: "project",
        kind: "write",
        key: ctx.key,
        tool: ctx.tool,
        deadline: ctx.deadline,
      });
      if (!outcome.ok) {
        const failed = ctx.failFrom(outcome.error);
        return { ...failed, summary: `into "${item}": ${failed.summary}` };
      }
      const doc = outcome.doc;
      if (!doc || doc.rid === null || !doc.key) return ctx.fail("DOC_NOT_OPEN", `into "${item}" is not an open project`, { param: "into", title: item });
      if (doc.instanceId !== family.instanceId) {
        return ctx.fail(
          "INVALID_ARGS",
          `into "${item}" is open in Revit ${doc.year}, but the family doc ${stripExt(family.title ?? "")} is in Revit ${family.year}; a family loads only into projects of the same Revit`,
          { param: "into", reason: "other Revit instance" }
        );
      }
      targets.push({ rid: doc.rid, key: doc.key, label: docLabel({ n: doc.n ?? ctx.session.docNumber(doc.key), title: doc.title ?? item, year: doc.year }) });
    }
  }

  const unique: Target[] = [];
  for (const t of targets) if (!unique.some((u) => u.key === t.key)) unique.push(t);
  ctx.args.into = unique.map((t) => `rid:${t.rid}|${t.key}`);

  const confirming = (ctx as ToolContext & { confirmPlan?: unknown }).confirmPlan;
  if (unique.length > 1 && !confirming && ctx.args.preview !== true) {
    const plan = `load ${stripExt(family.title ?? "the family")} into ${unique.length} documents (${unique.map((t) => t.label).join(", ")})`;
    const args = { ...ctx.args };
    delete args.confirm;
    delete args.preview;
    const token = ctx.session.confirms.issue({
      tool: ctx.tool,
      key: ctx.key,
      op: ctx.op,
      args,
      docKey: family.key,
      instanceId: family.instanceId,
      stamp: 0,
      deleteSet: [],
      rule: "multi_doc",
      plan,
    });
    return {
      status: "not_applied",
      summary: plan,
      data: { rule: "multi_doc", into: unique.map((t) => t.label), overwrite: ctx.args.overwrite === true, confirm: token, expires_min: 10 },
      next: `ask the user, then ${formatCall(ctx.tool, { op: ctx.op, confirm: token })}`,
    };
  }
}

/** "#3 Tower_A (Revit 2024)" from a doc key (the title is the file name of file/central keys). */
function labelOfKey(ctx: ToolContext, key: string, year: number): string {
  const parts = key.split("|");
  const tail = parts[parts.length - 1] ?? key;
  const title = tail.split(/[\\/]/).pop() ?? tail;
  return docLabel({ n: ctx.session.docNumber(key), title, year });
}
