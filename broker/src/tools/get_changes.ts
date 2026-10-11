// get_changes (SPEC Appendix A, §6.6): the broker owns the `since` grammar and the m# marks.
//   since "session" (default) → session_gen: the doc generation when this session first read the doc (noted by every
//                                 P-READ tool through noteDocSeen); the add-in also reaches back to the session's first
//                                 own transaction in its journal.
//   since "last"               → since_write: the w# of this session's last write on the doc (selector `last`).
//   since "m12"                → since_gen: the generation stored with mark m12 (HANDLE_EXPIRED when the doc was
//                                 reopened, INVALID_ARGS when the mark belongs to another doc).
// Every successful result issues a fresh mark for "now" (data.mark) and a next: line to poll from it.
import type { Session } from "../framework/session.js";
import type { AddinResult, ResolvedDoc, ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

/** Session → docKey → generation when the session first read that doc. */
const firstSeen = new WeakMap<Session, Map<string, { rid: number; generation: number }>>();

/** Remember the first generation this session saw for a doc (called from the prepare hook of every read tool). */
export function noteDocSeen(session: Session, doc: ResolvedDoc | null): void {
  if (!doc?.key || doc.rid === null || typeof doc.generation !== "number") return;
  let docs = firstSeen.get(session);
  if (!docs) {
    docs = new Map();
    firstSeen.set(session, docs);
  }
  const known = docs.get(doc.key);
  // A reopened doc (new rid) starts over: generations are per open document.
  if (!known || known.rid !== doc.rid) docs.set(doc.key, { rid: doc.rid, generation: doc.generation });
}

/** A prepare hook that only records the doc (list, find_elements, describe_elements, check_model, get_quantities). */
export async function noteTarget(ctx: ToolContext): Promise<void> {
  try {
    noteDocSeen(ctx.session, await ctx.resolveDoc());
  } catch {
    // Resolution errors surface from the real call.
  }
}

async function prepare(ctx: ToolContext): Promise<void | ToolOutcome> {
  const doc = await ctx.resolveDoc();
  if (!doc?.key || doc.rid === null) return;
  const seen = firstSeen.get(ctx.session)?.get(doc.key);
  noteDocSeen(ctx.session, doc);
  const raw = String(ctx.args.since ?? "session").trim();
  const since = raw.toLowerCase();
  delete ctx.args.since_gen;
  delete ctx.args.since_write;
  delete ctx.args.session_gen;
  if (since === "" || since === "session") {
    ctx.args.since = "session";
    if (seen && seen.rid === doc.rid) ctx.args.session_gen = seen.generation;
    return;
  }
  if (since === "last") {
    const last = ctx.session.lastWrites.get(doc.key);
    if (!last) return ctx.fail("INVALID_ARGS", `no write of this session in ${doc.title ?? "the target doc"} yet, so 'last' is empty; use since "session"`, { param: "since", reason: "no last write", example: { since: "session" } });
    ctx.args.since = "last";
    ctx.args.since_write = last.write;
    return;
  }
  if (/^m\d+$/.test(since)) {
    const mark = ctx.session.marks.get(since);
    if (!mark) return ctx.fail("HANDLE_EXPIRED", `mark ${since} is unknown in this session`, { param: "since" });
    if (mark.docKey !== doc.key) return ctx.fail("INVALID_ARGS", `mark ${since} belongs to another document; pass doc or use a mark from ${doc.title ?? "the target"}`, { param: "since", reason: "mark of another doc", example: { since: "session" } });
    if (mark.rid !== doc.rid) return ctx.fail("HANDLE_EXPIRED", `mark ${since} is from before ${doc.title ?? "the doc"} was reopened`, { param: "since" });
    ctx.args.since = since;
    ctx.args.since_gen = mark.generation;
    return;
  }
  if (/^\d+$/.test(since)) {
    ctx.args.since_gen = Number(since);
    return;
  }
  return ctx.fail("INVALID_ARGS", `since must be "session", "last" or a mark like m12 (got "${raw}")`, { param: "since", reason: "unknown since", example: { since: "last" } });
}

async function finish(ctx: ToolContext, r: AddinResult, o: ToolOutcome): Promise<ToolOutcome> {
  if (!r.ok || o.status !== "ok") return o;
  const doc = await ctx.resolveDoc();
  const data = o.data && typeof o.data === "object" && !Array.isArray(o.data) ? (o.data as Record<string, unknown>) : null;
  const now = data && typeof data.now === "number" ? data.now : r.doc?.generation;
  if (!doc?.key || doc.rid === null || typeof now !== "number") return o;
  const mark = ctx.session.mark(doc.key, doc.rid, now);
  return {
    ...o,
    data: { mark, ...(data ?? {}) },
    next: o.next ?? `get_changes {"since":"${mark}"} later shows what changed after this point`,
  };
}

export const module: ToolModule = { name: "get_changes", prepare, finish };
