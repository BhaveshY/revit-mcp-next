// set_target (SPEC §5.9, §7.3): pins the doc for this session — 'active', 'follow', 'none', #n, title, path,
// title@2027. Not read-only, and a per-session barrier: calls arriving while it runs wait for it.
import type { ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";
import { docLabel } from "../targeting/docParam.js";

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  return ctx.session.withBarrier(async () => {
    const r = await ctx.services.resolver.setTarget(ctx.session, ctx.args.doc, ctx.args.instance, ctx.deadline);
    if (!r.ok) return ctx.fail(r.error.code ?? "INTERNAL_ERROR", r.error.message ?? "set_target failed", (r.error.details ?? undefined) as Record<string, unknown> | undefined);
    for (const line of r.notices) {
      const space = line.indexOf(" ");
      ctx.notice(space > 0 ? line.slice(0, space) : line, space > 0 ? line.slice(space + 1) : "");
    }
    const pin = r.pin;
    if (!pin) return { status: "ok", summary: r.text, data: { target: null, mode: "auto" }, doc: null, next: "status {}" };
    const n = ctx.session.peekDocNumber(pin.key) ?? null;
    return {
      status: "ok",
      summary: r.text,
      data: { target: n ? `#${n}` : null, title: pin.title, year: pin.year, kind: pin.kind, mode: pin.mode },
      doc: { title: pin.title, year: pin.year, n },
      next: pin.mode === "follow" ? undefined : `every call now uses ${docLabel({ n: n ?? 0, title: pin.title, year: pin.year })}; set_target {"doc":"none"} unpins`,
    };
  });
}

export const module: ToolModule = { name: "set_target", handle };
