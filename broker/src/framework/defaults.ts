// Default handlers (SPEC §9B) and the standard AddinResult → ToolOutcome mapping:
//   defaultRead       resolve → call → handles (r#), pages (p#), READ_STILL_RUNNING cache, images (c#)
//   defaultWrite      static blast rules (always, central_open, multi_doc) → call (mode from preview) →
//                     needsConfirm → token + NOT APPLIED; success → last, r#, w#, undo hint, TARGET gates bookkeeping
//   defaultLifecycle  as write; job:always uses asJob + JobRunner; re-pin after open/activate/new_*; close clears the pin
//   defaultControl    control-pipe ops (ui.dialogs, ui.press) incl. the press confirm token
// Every wave-1 tool module without a handler works through these, so an add-in handler landing in wave 2 works end to end.
// Frozen in wave 2.

import { byKey, type Blast } from "@revit-mcp-next/contracts/catalog";
import type { BridgeChanges, BridgeResponse, ControlOp, ResponseDoc } from "@revit-mcp-next/contracts/protocol";
import { failure } from "../ipc/errors.js";
import { readCaptureImage } from "./images.js";
import { expiresMinutes, type ConfirmPlan } from "./confirm.js";
import { describeKey, readCacheKey, type ToolContextImpl } from "./context.js";
import { formatCall, renderFix } from "./errors.js";
import { fmtCount } from "./render.js";
import type { AddinResult, ResolvedDoc, ToolContext, ToolOutcome } from "./types.js";
import { stripExt } from "../targeting/docParam.js";

/** Successful calls of these keys move the pin explicitly (§7.4 item 1). */
export const REPIN_KEYS = new Set([
  "ui.activate_doc",
  "ui.activate_view",
  "manage_document.activate",
  "manage_document.open",
  "manage_document.new_project",
  "manage_document.new_family",
  "edit_family.open",
]);
const CLOSE_KEYS = new Set(["manage_document.close"]);
const WRITE_KINDS = new Set(["write", "lifecycle", "code"]);
const MAX_LIST_IDS = 50;

export async function defaultHandler(ctx: ToolContext): Promise<ToolOutcome> {
  const c = ctx as ToolContextImpl;
  const entry = byKey.get(ctx.key)!.entry;
  if (entry.impl === "broker") return ctx.fail("UNSUPPORTED_OP", `${ctx.key} is not implemented in this broker yet`, { hint: `call help {"tool":"${ctx.tool}"}` });
  if (entry.impl === "control") return defaultControl(c);
  switch (ctx.meta.kind) {
    case "read":
      return defaultRead(c);
    case "write":
    case "code":
      return defaultWrite(c);
    case "lifecycle":
      return defaultLifecycle(c);
    case "ui":
      return defaultUi(c);
    case "control":
      return defaultRead(c);
  }
}

// ------------------------------------------------------------------------------------------------ reads

export async function defaultRead(ctx: ToolContextImpl): Promise<ToolOutcome> {
  const doc = await ctx.resolveDoc();
  // A repeat of a call that returned READ_STILL_RUNNING returns the cached result (§6.8).
  const cached = doc ? await cachedRead(ctx, doc) : null;
  if (cached) return cached;
  const { args, wantIds } = pagedArgs(ctx);
  const r = await ctx.call(undefined, args, { wantIds });
  return finishWith(ctx, r, outcomeFromResult(ctx, r));
}

async function cachedRead(ctx: ToolContextImpl, doc: ResolvedDoc): Promise<ToolOutcome | null> {
  const expanded = await ctx.expandArgs(ctx.args, doc);
  const key = readCacheKey(ctx.key, expanded, doc.key);
  const entry = ctx.services.results.get(key);
  if (!entry) return null;
  const wait = Math.max(0, ctx.deadline.remaining() - 2_000);
  const response = entry.response ?? (await ctx.services.results.wait(key, wait));
  const ref = ctx.session.jobs.values().find(([, j]) => j.cacheKey === key)?.[0];
  if (!response) {
    return {
      status: "running",
      summary: `${describeKey(ctx.key)} is still running in Revit${ref ? ` as job ${ref}` : ""}`,
      data: { code: "READ_STILL_RUNNING", ref },
      next: ref ? `job_status {"id":"${ref}"} - or repeat this call: it returns the cached result` : undefined,
    };
  }
  ctx.services.results.delete(key);
  const r: AddinResult = { ...response, key: ctx.key, instanceId: doc.instanceId, year: doc.year, pid: doc.pid };
  const o = outcomeFromResult(ctx, r);
  if (ref) o.notices = [...(o.notices ?? []), `RECOVERED_WRITE result of ${ref} (finished in the background)`.replace("RECOVERED_WRITE", "LATE_RESULT")];
  return o;
}

/** Page calls: offset or id slices of the stored handle; first find_elements calls ask for every id (wantIds). */
function pagedArgs(ctx: ToolContextImpl): { args: Record<string, unknown>; wantIds: boolean } {
  const args = { ...ctx.args };
  const page = ctx.pageEntry;
  const wantsHandle = ctx.tool === "find_elements" && args.count_only !== true;
  if (!page) return { args, wantIds: wantsHandle };
  if (page.handle) {
    const h = ctx.session.handles.get(page.handle);
    if (h) {
      const limit = typeof args.limit === "number" ? args.limit : 50;
      args.ids = h.ids.slice(page.offset, page.offset + limit);
      args.offset = 0;
      args.page_offset = page.offset;
      args.page_total = h.total;
      return { args, wantIds: false };
    }
  }
  if (page.resume) args.resume = page.resume;
  else args.offset = page.offset;
  return { args, wantIds: false };
}

// ------------------------------------------------------------------------------------------------ writes

const STATIC_RULES: Blast[] = ["always", "central_open", "multi_doc"];

/** Broker-evaluated blast rules (§6.3): answered with a static plan, no add-in call. */
function staticBlast(ctx: ToolContextImpl): { rule: Blast; plan: string } | null {
  if (ctx.confirmPlan || ctx.args.preview === true) return null;
  const blast = ctx.meta.blast;
  if (blast.includes("always")) return { rule: "always", plan: staticPlanText(ctx) };
  if (blast.includes("central_open") && ctx.args.central === true) return { rule: "central_open", plan: `open the central model itself (${String(ctx.args.path ?? "")}) instead of a local copy` };
  if (blast.includes("multi_doc") && Array.isArray(ctx.args.into) && ctx.args.into.length > 1) return { rule: "multi_doc", plan: `load the family into ${ctx.args.into.length} documents (${(ctx.args.into as unknown[]).join(", ")})` };
  return null;
}

function staticPlanText(ctx: ToolContextImpl): string {
  const shown = Object.entries(ctx.args).filter(([k]) => !["doc", "preview", "confirm", "units", "op"].includes(k));
  const what = describeKey(ctx.key);
  return shown.length ? `${what} with ${shown.map(([k, v]) => `${k}=${JSON.stringify(v)}`).join(", ")}` : what;
}

export async function defaultWrite(ctx: ToolContextImpl): Promise<ToolOutcome> {
  const doc = await ctx.resolveDoc();
  const blast = staticBlast(ctx);
  if (blast) return notApplied(ctx, { rule: blast.rule, plan: blast.plan, stamp: 0, deleteSet: [] }, doc, null);
  const plan = ctx.confirmPlan;
  if (plan) ctx.session.confirms.consume(plan.token);
  const asJob = ctx.meta.job === "always";
  const r = await ctx.call(undefined, undefined, {
    asJob,
    confirmed: plan ? { stamp: plan.stamp, ...(plan.deleteSet.length ? { deleteSet: plan.deleteSet } : {}) } : undefined,
  });
  return finishWith(ctx, r, outcomeFromResult(ctx, r));
}

export const defaultLifecycle = defaultWrite;

export async function defaultUi(ctx: ToolContextImpl): Promise<ToolOutcome> {
  await ctx.resolveDoc();
  const r = await ctx.call();
  return finishWith(ctx, r, outcomeFromResult(ctx, r));
}

async function finishWith(ctx: ToolContextImpl, r: AddinResult, o: ToolOutcome): Promise<ToolOutcome> {
  const module = ctx.services.tools.module(ctx.tool);
  return module?.finish ? module.finish(ctx, r, o) : o;
}

// ------------------------------------------------------------------------------------------------ control-pipe ops

export async function defaultControl(ctx: ToolContextImpl): Promise<ToolOutcome> {
  const op = (ctx.op ?? ctx.key) as ControlOp;
  const live = await ctx.instances();
  if (live.length === 0) return ctx.fail("NO_REVIT_RUNNING", "no Revit with the add-in is running", lastExitedDetails(ctx));
  const filter = ctx.args.instance !== undefined ? String(ctx.args.instance).trim() : null;
  const targets = filter ? live.filter((i) => String(i.year) === filter || String(i.pid) === filter || i.instanceId === filter) : live;
  if (targets.length === 0) return ctx.fail("INVALID_ARGS", `no running Revit matches instance ${filter}`, { param: "instance", reason: "no match", options: live.map((i) => ({ value: String(i.year), label: `Revit ${i.year} (pid ${i.pid})` })) });
  const payload: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(ctx.args)) if (!["instance", "doc", "confirm", "preview", "op"].includes(k)) payload[k] = v;
  const plan = ctx.confirmPlan;
  if (plan) {
    ctx.session.confirms.consume(plan.token);
    Object.assign(payload, plan.extra?.payload ?? {}, { confirmed: true });
  }

  if (op === "dialogs") {
    const results = await Promise.all(targets.map((i) => ctx.control(i, "dialogs", payload, 3_000)));
    const rows: unknown[] = [];
    const errors: string[] = [];
    results.forEach((r, idx) => {
      const inst = targets[idx]!;
      if (r.ok && Array.isArray(r.data)) for (const d of r.data as Array<Record<string, unknown>>) rows.push({ instance: String(inst.year), ...d });
      else if (!r.ok) errors.push(`Revit ${inst.year}: ${r.code} ${r.message ?? ""}`.trim());
    });
    if (rows.length === 0 && errors.length === results.length) {
      const r = results[0]!;
      return ctx.fail(r.code ?? "INTERNAL_ERROR", r.message ?? "dialogs failed", (r.details ?? undefined) as Record<string, unknown> | undefined);
    }
    const o: ToolOutcome = {
      status: "ok",
      summary: rows.length ? `${rows.length} open dialog${rows.length === 1 ? "" : "s"}` : "no open dialogs",
      data: { total: rows.length, dialogs: rows },
      next: rows.length ? `ui {"op":"press","button":"Cancel"} closes it; any other button needs the user's OK` : undefined,
      doc: null,
    };
    for (const e of errors) ctx.warn("PARTIAL_RESULT", e);
    return o;
  }

  // press (and any other control op): one instance.
  let target = targets.length === 1 ? targets[0]! : null;
  if (!target) {
    const probes = await Promise.all(targets.map((i) => ctx.control(i, "dialogs", {}, 2_500)));
    const withDialogs = targets.filter((_, i) => probes[i]!.ok && Array.isArray(probes[i]!.data) && (probes[i]!.data as unknown[]).length > 0);
    if (withDialogs.length === 1) target = withDialogs[0]!;
    else
      return ctx.fail("INVALID_ARGS", `${withDialogs.length || targets.length} Revit instances are running; say which with instance`, {
        param: "instance",
        reason: "several instances",
        example: { op: ctx.op, button: ctx.args.button, instance: String((withDialogs[0] ?? targets[0])!.year) },
      });
  }
  const r = await ctx.control(target, op, payload, 5_000);
  if (!r.ok) return ctx.fail(r.code ?? "INTERNAL_ERROR", r.message ?? `${op} failed`, (r.details ?? undefined) as Record<string, unknown> | undefined);
  if (r.needsConfirm) {
    const token = ctx.session.confirms.issue({
      tool: ctx.tool,
      key: ctx.key,
      op: ctx.op,
      args: { ...ctx.args, instance: String(target.year) },
      docKey: null,
      instanceId: target.instanceId,
      stamp: r.needsConfirm.stamp ?? 0,
      deleteSet: [],
      rule: r.needsConfirm.rule ?? "button",
      plan: r.needsConfirm.plan,
      extra: { payload },
    });
    return {
      status: "not_applied",
      summary: r.needsConfirm.plan || `press ${String(ctx.args.button)} on the Revit ${target.year} dialog`,
      data: { ...(isObject(r.data) ? r.data : {}), confirm: token, expires_min: 10 },
      next: `ask the user, then ui {"op":"${ctx.op}","confirm":"${token}"}`,
      doc: null,
    };
  }
  return { status: "ok", summary: r.summary ?? `${op} done`, data: r.data, doc: null };
}

// ------------------------------------------------------------------------------------------------ NOT APPLIED / preview

function notApplied(ctx: ToolContextImpl, nc: { rule: Blast | null; plan: string; stamp: number; deleteSet: number[]; blast?: BridgeResponse["needsConfirm"] extends infer T ? (T extends { blast: infer B } ? B : never) : never }, doc: ResolvedDoc | null, r: AddinResult | null): ToolOutcome {
  const plan: ConfirmPlan = {
    tool: ctx.tool,
    key: ctx.key,
    op: ctx.op,
    args: { ...ctx.args },
    docKey: doc?.key ?? null,
    instanceId: doc?.instanceId ?? null,
    stamp: nc.stamp,
    deleteSet: nc.deleteSet,
    rule: nc.rule,
    plan: nc.plan,
    blast: nc.blast,
  };
  delete plan.args.confirm;
  delete plan.args.preview;
  const token = ctx.session.confirms.issue(plan);
  const data: Record<string, unknown> = {};
  if (nc.blast) {
    if (nc.blast.deleteTotal) data.delete = { total: nc.blast.deleteTotal, by_category: nc.blast.byCategory, sample: nc.blast.sample?.slice(0, MAX_LIST_IDS) };
    if (nc.blast.modifyTotal) data.modify = nc.blast.modifyTotal;
    if (nc.blast.createTotal) data.create = nc.blast.createTotal;
  }
  if (r && isObject(r.data)) Object.assign(data, r.data);
  data.rule = nc.rule ?? undefined;
  data.confirm = token;
  data.expires_min = 10;
  return {
    status: "not_applied",
    summary: nc.plan || `${describeKey(ctx.key)} needs the user's OK`,
    data,
    next: `ask the user, then ${formatCall(ctx.tool, confirmArgs(ctx, token))}`,
  };
}

function confirmArgs(ctx: ToolContext, token: string): Record<string, unknown> {
  const disc = ctx.spec.discriminator;
  return disc && ctx.op ? { [disc]: ctx.op, confirm: token } : { confirm: token };
}

// ------------------------------------------------------------------------------------------------ result mapping

const isObject = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === "object" && !Array.isArray(v);

export function outcomeFromResult(ctxIn: ToolContext, r: AddinResult): ToolOutcome {
  const ctx = ctxIn as ToolContextImpl;
  const session = ctx.session;
  const notices = (r.notices ?? []).map((n) => `${n.code} ${n.text}`.trim());
  const warnings = (r.warnings ?? []).map((w) => `${w.code} ${w.text}${w.ids && w.ids.length ? ` ids ${w.ids.slice(0, 20).join(",")}` : ""}${w.n && w.n > 1 ? ` (x${w.n})` : ""}`.trim());
  const doc = outcomeDoc(ctx, r.doc ?? null);
  const base = { notices, warnings, doc };
  const meta = byKey.get(r.key)?.entry ?? ctx.meta;
  const isWrite = WRITE_KINDS.has(meta.kind);
  const resolved = ctx.resolvedCache;

  if (!r.ok) {
    const code = r.code ?? "INTERNAL_ERROR";
    if (code === "READ_STILL_RUNNING" || code === "WRITE_STILL_RUNNING") {
      const ref = (r.details?.ref as string | undefined) ?? r.jobRef ?? r.writeId;
      const fix = renderFix({ code, tool: ctx.tool, op: ctx.op, key: ctx.key, args: ctx.effectiveArgs, details: { ...(r.details ?? {}), ref }, doc: resolved });
      return {
        ...base,
        status: "running",
        summary: `${r.message ?? describeKey(r.key)}${ref ? ` as ${ref}` : ""}`,
        data: { code, ref },
        next: fix.fix,
      };
    }
    if (code === "TARGET_CLOSED" || code === "DOC_NOT_OPEN") ctx.services.registry.invalidate();
    const out = ctx.failFrom(r);
    return { ...out, ...base, doc: out.doc ?? doc, notices: [...notices], warnings };
  }

  // Jobs still running at the budget.
  if (r.job && r.jobRef) {
    const job = r.job;
    const progress = job.total ? ` (${job.stage ? job.stage + " " : ""}${job.done ?? 0}/${job.total})` : job.stage ? ` (${job.stage})` : "";
    return {
      ...base,
      status: "running",
      summary: `${describeKey(r.key)}${progress} as job ${r.jobRef}`,
      data: { job: r.jobRef, state: job.state },
      next: `job_status {"id":"${r.jobRef}"} - do not repeat this call`,
    };
  }

  const preview = isWrite && (ctx.args.preview === true || (meta.kind === "code" && ctx.args.mode !== "commit"));
  if (r.needsConfirm && !preview) {
    return { ...notApplied(ctx, { rule: r.needsConfirm.rule ?? null, plan: r.needsConfirm.plan, stamp: r.needsConfirm.stamp ?? 0, deleteSet: r.needsConfirm.deleteSet ?? [], blast: r.needsConfirm.blast }, resolved, r), ...base };
  }

  let data: unknown = r.data;
  const summaryParts: string[] = [r.summary || `${describeKey(r.key)} done`];
  let more: string | undefined;
  let next: string | undefined;
  const images: ToolOutcome["images"] = [];

  // Preview: token for exactly this plan.
  if (preview) {
    const nc = r.needsConfirm;
    const dataObj = isObject(r.data) ? r.data : {};
    const token = ctx.session.confirms.issue({
      tool: ctx.tool,
      key: ctx.key,
      op: ctx.op,
      args: Object.fromEntries(Object.entries(ctx.args).filter(([k]) => k !== "preview" && k !== "confirm")),
      docKey: resolved?.key ?? null,
      instanceId: resolved?.instanceId ?? null,
      stamp: nc?.stamp ?? (typeof dataObj.stamp === "number" ? dataObj.stamp : 0),
      deleteSet: nc?.deleteSet ?? (Array.isArray(dataObj.deleteSet) ? (dataObj.deleteSet as number[]) : []),
      rule: nc?.rule ?? null,
      plan: nc?.plan ?? r.summary ?? "",
      blast: nc?.blast,
    });
    const extra: Record<string, unknown> = {};
    if (nc?.blast?.deleteTotal) extra.delete = { total: nc.blast.deleteTotal, by_category: nc.blast.byCategory, sample: nc.blast.sample?.slice(0, MAX_LIST_IDS) };
    data = { ...dataObj, ...extra, confirm: token, expires_min: 10 };
    summaryParts[0] = `preview: ${r.summary || nc?.plan || describeKey(r.key)}; nothing was changed`;
    next = `${formatCall(ctx.tool, confirmArgs(ctx, token))} applies exactly this plan${nc?.rule ? " (needs the user's OK)" : ""}`;
    return { ...base, status: "ok", summary: summaryParts.join(", "), data, next };
  }

  // Handles from full id lists (find_elements wantIds).
  let handle: string | undefined;
  if (r.page?.ids && resolved?.key && resolved.rid !== null) {
    handle = session.handles.add({
      docKey: resolved.key,
      rid: resolved.rid,
      instanceId: resolved.instanceId,
      ids: r.page.ids.slice(0, 100_000),
      total: r.page.total,
      link: typeof ctx.args.link === "string" ? ctx.args.link : undefined,
      tool: ctx.tool,
      args: { ...ctx.effectiveArgs },
    });
  } else if (ctx.pageEntry?.handle) {
    handle = ctx.pageEntry.handle;
  }

  // Paging.
  const page = r.page;
  const pageOffset = typeof ctx.args.page_offset === "number" ? (ctx.args.page_offset as number) : (page?.offset ?? 0);
  const pageTotal = typeof ctx.args.page_total === "number" ? (ctx.args.page_total as number) : (page?.total ?? 0);
  if (page && pageOffset + page.count < pageTotal) {
    const id = session.pages.add({
      tool: ctx.tool,
      key: ctx.key,
      args: Object.fromEntries(Object.entries(ctx.effectiveArgs).filter(([k]) => k !== "page")),
      handle,
      offset: pageOffset + page.count,
      total: pageTotal,
      docKey: resolved?.key ?? null,
    });
    more = `${ctx.tool} {"page":"${id}"}`;
    summaryParts.push(`showing ${fmtCount(pageOffset + 1)}-${fmtCount(pageOffset + page.count)} of ${fmtCount(pageTotal)}, more ${id}`);
  }
  if (r.partial) {
    const id = session.pages.add({
      tool: ctx.tool,
      key: ctx.key,
      args: Object.fromEntries(Object.entries(ctx.effectiveArgs).filter(([k]) => k !== "page")),
      handle,
      offset: page ? pageOffset + page.count : 0,
      resume: r.partial.resume,
      docKey: resolved?.key ?? null,
    });
    more = `${ctx.tool} {"page":"${id}"}`;
    warnings.push(`PARTIAL_RESULT stopped at the ${r.partial.reason === "deadline" ? "time budget" : "limit"}; continue with page ${id}`);
    summaryParts.push(`partial, more ${id}`);
  }
  if (handle && !ctx.pageEntry) summaryParts.push(`handle ${handle}`);
  if (handle && isObject(data)) data = { ...data, handle };
  else if (handle && data === undefined) data = { handle };

  // Images (capture): the file must be under <home>\captures; c# remembers it for compare.
  if (r.file && typeof r.file.mime === "string" && r.file.mime.startsWith("image/")) {
    const fileInfo = r.file;
    const img = readCaptureImage(ctx.services.home, fileInfo.path, fileInfo.mime);
    if (!img.ok) return { ...ctx.fail(img.code, img.message, { path: fileInfo.path }), notices, warnings };
    const capId = session.captures.add({ file: img.path, mime: img.mime, view: ctx.args.view, size: ctx.args.size, region: ctx.args.region, docKey: resolved?.key ?? null, meta: fileInfo.meta, w: fileInfo.w, h: fileInfo.h });
    images.push(img.block);
    data = isObject(data) ? { capture: capId, ...data } : { capture: capId };
    summaryParts[0] = `${summaryParts[0]} as ${capId}`;
  }

  // Writes: created/modified/deleted/warnings/undo/handle/write are always present (D1 §7.3 rule 4).
  if (isWrite && r.writeId) {
    const changes: BridgeChanges = r.changes ?? { created: [], modified: [], deleted: [], createdTotal: 0, modifiedTotal: 0, deletedTotal: 0 };
    const deleted = new Set(changes.deleted ?? []);
    const touched = [...(changes.created ?? []), ...(changes.modified ?? [])].filter((id) => !deleted.has(id));
    let writeHandle: string | null = null;
    if (resolved?.key && resolved.rid !== null) {
      if (touched.length > 0) {
        writeHandle = session.handles.add({ docKey: resolved.key, rid: resolved.rid, instanceId: resolved.instanceId, ids: touched, total: touched.length, tool: ctx.tool, args: {} });
        session.lastWrites.set(resolved.key, { ids: touched, write: r.writeId, at: Date.now(), rid: resolved.rid, instanceId: resolved.instanceId });
      }
      void ctx.services.resolver.noteWrite(session, resolved);
    }
    const undoable = meta.kind === "write" || (meta.kind === "code" && ctx.args.mode === "commit");
    const undoCall = undoable ? (session.pin && resolved?.key && session.pin.key !== resolved.key && resolved.n ? `undo {"doc":"#${resolved.n}"}` : "undo {}") : null;
    const writeData: Record<string, unknown> = {
      ...(isObject(data) ? data : data !== undefined ? { result: data } : {}),
      created: (changes.created ?? []).slice(0, MAX_LIST_IDS),
      modified: (changes.modified ?? []).slice(0, MAX_LIST_IDS),
      deleted: (changes.deleted ?? []).slice(0, MAX_LIST_IDS),
      warnings: (r.warnings ?? []).length,
      undo: undoCall,
      handle: writeHandle,
      write: r.writeId,
    };
    if ((changes.createdTotal ?? 0) > MAX_LIST_IDS) writeData.created_total = changes.createdTotal;
    if ((changes.modifiedTotal ?? 0) > MAX_LIST_IDS) writeData.modified_total = changes.modifiedTotal;
    if ((changes.deletedTotal ?? 0) > MAX_LIST_IDS) writeData.deleted_total = changes.deletedTotal;
    data = writeData;
    if (writeHandle) summaryParts.push(`handle ${writeHandle}`);
    if (meta.kind === "write" && ctx.tool !== "change_set") next ??= `capture {"from":"last"} to check`;
  }

  // Re-pin and close bookkeeping (§7.4).
  if (REPIN_KEYS.has(r.key) && r.doc && r.instanceId && r.pid !== undefined && r.year !== undefined) {
    const pin = session.pin;
    const changed = !pin || pin.key !== r.doc.key || pin.mode !== "explicit";
    if (r.key !== "ui.activate_view" || changed) notices.push(ctx.services.resolver.repin(session, r.doc, { instanceId: r.instanceId, pid: r.pid, year: r.year }));
  }
  if (CLOSE_KEYS.has(r.key) && resolved?.key) {
    const n = ctx.services.resolver.onClosed(session, resolved.key);
    if (n) notices.push(n);
  }

  // Default hints.
  if (!next && handle && ctx.tool === "find_elements" && !ctx.pageEntry) next = `describe_elements {"from":"${handle}"} or capture {"from":"${handle}"}`;
  return { ...base, status: "ok", summary: summaryParts.join(", "), data, more, next, images: images.length ? images : undefined };
}

function outcomeDoc(ctx: ToolContextImpl, doc: ResponseDoc | null): ToolOutcome["doc"] {
  if (doc && doc.title) return { title: doc.title, year: doc.year, n: doc.key ? ctx.session.docNumber(doc.key) : null };
  const resolved = ctx.resolvedCache;
  if (resolved?.title) return { title: resolved.title, year: resolved.year, n: resolved.n };
  return undefined;
}

function lastExitedDetails(ctx: ToolContext): Record<string, unknown> | undefined {
  const last = ctx.services.registry.lastExited();
  return last ? { lastExited: { year: last.year, pid: last.pid, doc: last.docs[0], at: new Date(last.at).toISOString() } } : undefined;
}

export function brokerFailure(code: string, message: string, details: Record<string, unknown> | null = null): BridgeResponse {
  return failure("", code, message, details);
}

/** "#3 Tower_A (Revit 2024)" for an outcome doc. */
export function docText(doc: { n?: number | null; title?: string | null; year?: number | null } | null | undefined): string {
  if (!doc?.title) return "";
  return `${doc.n ? `#${doc.n} ` : ""}${stripExt(doc.title)} (Revit ${doc.year})`;
}

export { expiresMinutes };
