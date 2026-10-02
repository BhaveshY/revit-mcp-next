// Targeting (SPEC §7, D2 §9.4, §10): pin model, auto-resolve, rebind by rid/key/alias, re-pin rules, TARGET_CHANGED
// gates, exact-only doc matching for writes, set_target and projectPin. Resolution reads snapshots only; it never
// waits for Revit's UI thread. Basic-complete implementation; P-REL-BROKER owns hardening.

import type { Kind, Scope } from "@revit-mcp-next/contracts/catalog";
import type { ResponseDoc, SnapshotDoc } from "@revit-mcp-next/contracts/protocol";
import type { ChannelPool } from "../instances/InstanceChannel.js";
import type { InstanceInfo, InstanceRegistry } from "../instances/InstanceRegistry.js";
import { failure } from "../ipc/errors.js";
import type { Deadline } from "../runtime/deadline.js";
import type { BrokerLog } from "../runtime/log.js";
import type { BridgeResponse } from "@revit-mcp-next/contracts/protocol";
import type { Pin, Session } from "../framework/session.js";
import type { ResolvedDoc } from "../framework/types.js";
import { docLabel, matchDocs, parseDocParam, stripExt, type DocCandidate, type DocSelector } from "./docParam.js";

export interface ResolveRequest {
  session: Session;
  /** Per-call doc param (raw). A per-call doc never moves the pin. */
  doc?: unknown;
  /** Per-call instance filter: Revit year or process id. */
  instance?: unknown;
  scope: Scope;
  kind: Kind;
  key: string;
  tool: string;
  deadline: Deadline;
  /** Refresh snapshots through the control pipe first (writes, D2 §8.4). */
  fresh?: boolean;
  /** For FAMILY_DOC_REQUIRED details (edit_family family=). */
  familyHint?: string;
  progress?: (message: string) => void;
}

export type ResolveOutcome = { ok: true; doc: ResolvedDoc | null; notices: string[] } | { ok: false; error: BridgeResponse; notices: string[] };

interface World {
  /** Live, compatible instances with a snapshot. */
  usable: InstanceInfo[];
  /** Live instances whose protocol is incompatible. */
  outdated: InstanceInfo[];
  /** Live instances still starting or without a readable snapshot. */
  starting: InstanceInfo[];
  candidates: DocCandidate[];
}

const isGated = (kind: Kind) => kind === "write" || kind === "lifecycle" || kind === "code";
const exactOnly = (kind: Kind) => kind !== "read" && kind !== "control";

export class TargetResolver {
  constructor(
    private readonly registry: InstanceRegistry,
    private readonly channels: ChannelPool,
    private readonly log: BrokerLog
  ) {}

  // ------------------------------------------------------------------------------------------------ world

  async world(session: Session, deadline: Deadline, fresh = false, instanceFilter?: unknown): Promise<World> {
    let live = await this.registry.list();
    if (fresh && live.length > 0) {
      await Promise.all(
        live.map(async (info) => {
          if (deadline.remaining() < 1_000) return;
          await this.channels.get(info).snapshot(800).catch(() => null);
        })
      );
      live = await this.registry.list({ maxAgeMs: 0 });
    }
    this.channels.sync(live);
    const filter = parseInstanceFilter(instanceFilter);
    if (filter) live = live.filter((i) => (filter.kind === "year" ? i.year === filter.value : i.pid === filter.value));
    const usable: InstanceInfo[] = [];
    const outdated: InstanceInfo[] = [];
    const starting: InstanceInfo[] = [];
    for (const info of live) {
      if (!info.snapshot) starting.push(info);
      else if (info.compat === "ADDIN_OUTDATED" || info.compat === "ADDIN_NEWER_THAN_BROKER") outdated.push(info);
      else if (info.state === "starting" && info.snapshot.docs.length === 0) starting.push(info);
      else usable.push(info);
    }
    const candidates: DocCandidate[] = [];
    for (const info of usable) {
      for (const d of info.snapshot!.docs) {
        if (d.closing) continue;
        candidates.push(candidateOf(session, info, d));
      }
    }
    candidates.sort((a, b) => a.n - b.n);
    return { usable, outdated, starting, candidates };
  }

  // ------------------------------------------------------------------------------------------------ resolve

  async resolve(req: ResolveRequest): Promise<ResolveOutcome> {
    const notices: string[] = [];
    const session = req.session;
    let world = await this.world(session, req.deadline, req.fresh === true && isGated(req.kind), req.instance);

    // Revit still starting: wait inside the budget (D2 §10.5).
    while (world.usable.length === 0 && world.starting.length > 0 && req.deadline.remaining() > 2_500) {
      req.progress?.(`Revit ${world.starting[0]!.year} is still starting`);
      if (!(await req.deadline.sleep(1_000))) break;
      this.registry.invalidate();
      world = await this.world(session, req.deadline, false, req.instance);
    }

    if (world.usable.length === 0) return { ok: false, error: this.noRevit(world, req), notices };

    // ---------------------------------------------------------------- scope none
    if (req.scope === "none") {
      if (req.doc !== undefined && req.doc !== null && String(req.doc).trim() !== "") {
        const picked = this.pickBySelector(req, world, notices);
        if (!picked.ok) return picked;
        return { ok: true, doc: picked.doc ?? null, notices };
      }
      const info = this.instanceForNone(session, world, notices);
      return { ok: true, doc: instanceOnly(info), notices };
    }

    // ---------------------------------------------------------------- per-call doc
    if (req.doc !== undefined && req.doc !== null && String(req.doc).trim() !== "") {
      const picked = this.pickBySelector(req, world, notices);
      if (!picked.ok) return picked;
      return this.finishDoc(req, world, picked.candidate!, notices, { perCall: true });
    }

    // ---------------------------------------------------------------- pin
    const pin = session.pin;
    if (pin) {
      if (pin.mode === "follow") {
        const active = this.activeCandidate(world, null);
        if (!active) return { ok: false, error: this.noDocument(world, req.key), notices };
        if (isGated(req.kind) && session.lastWriteDocKey && session.lastWriteDocKey !== active.key && !session.isAcked(active.key)) {
          session.ack(active.key);
          const previous = world.candidates.find((c) => c.key === session.lastWriteDocKey);
          return {
            ok: false,
            error: failure("", "TARGET_CHANGED", `the active doc changed to ${docLabel(active)} since your last write (follow mode); nothing was written`, {
              from: previous ? docLabel(previous) : session.lastWriteDocKey,
              to: docLabel(active),
              reason: "follow mode: the active doc changed",
              fromDoc: previous ? `#${previous.n}` : undefined,
            }),
            notices,
          };
        }
        return this.finishDoc(req, world, active, notices, {});
      }
      const rebound = this.rebind(session, pin, world, notices);
      if (rebound.kind === "found") return this.finishDoc(req, world, rebound.candidate, notices, { checkActiveSwitch: true });
      if (rebound.kind === "ambiguous") return { ok: false, error: rebound.error, notices };
      if (pin.mode === "explicit") return { ok: false, error: rebound.error, notices };
      // Auto pin lost: clear, auto-resolve, PIN_MOVED + write gate.
      const lostTitle = pin.title;
      session.pin = null;
      const auto = this.autoResolve(session, world, req, notices);
      if (!auto.ok) return auto;
      const moved = auto.candidate!;
      notices.push(`PIN_MOVED ${stripExt(lostTitle)} is ${rebound.kind === "closed" ? "closed" : "not open"}; now working on ${docLabel(moved)}`);
      if (isGated(req.kind) && !session.isAcked(moved.key)) {
        session.ack(moved.key);
        return {
          ok: false,
          error: failure("", "TARGET_CHANGED", `the target changed from ${stripExt(lostTitle)} to ${docLabel(moved)} because ${stripExt(lostTitle)} is ${rebound.kind === "closed" ? "closed" : "not open"}; nothing was written`, {
            from: stripExt(lostTitle),
            to: docLabel(moved),
            reason: rebound.kind === "closed" ? "the pinned doc was closed" : "the pinned doc is not open after a restart",
          }),
          notices,
        };
      }
      return this.finishDoc(req, world, moved, notices, {});
    }

    // ---------------------------------------------------------------- auto-resolve
    const auto = this.autoResolve(session, world, req, notices);
    if (!auto.ok) return auto;
    return this.finishDoc(req, world, auto.candidate!, notices, {});
  }

  /** Scope rules, read-only check, active-switch gate and notices for a chosen doc. */
  private finishDoc(
    req: ResolveRequest,
    world: World,
    chosen: DocCandidate,
    notices: string[],
    o: { perCall?: boolean; checkActiveSwitch?: boolean }
  ): ResolveOutcome {
    const session = req.session;
    let candidate = chosen;
    if ((req.scope === "project") && candidate.kind === "family") {
      const projectPin = session.projectPin;
      const fromPin = projectPin ? world.candidates.find((c) => c.key === projectPin.key || c.aliases.includes(projectPin.key)) : undefined;
      if (fromPin && !o.perCall) {
        if (session.once(`PROJECT_FALLBACK:${fromPin.key}:${candidate.key}`)) notices.push(`TARGET_NOW ${docLabel(fromPin)} (project op while the pin is the family ${stripExt(candidate.title)})`);
        candidate = fromPin;
      } else {
        const options = world.candidates.filter((c) => c.kind === "project").map(optionOf);
        return {
          ok: false,
          error: failure("", "PROJECT_DOC_REQUIRED", `${req.tool} needs a project document, but ${docLabel(candidate)} is a family`, { options }),
          notices,
        };
      }
    }
    if (req.scope === "family" && candidate.kind === "project") {
      const inst = world.usable.find((i) => i.instanceId === candidate.instanceId);
      const activeRid = inst?.snapshot?.activeRid;
      const activeFamily = world.candidates.find((c) => c.instanceId === candidate.instanceId && c.rid === activeRid && c.kind === "family");
      if (activeFamily && !o.perCall) {
        candidate = activeFamily;
      } else {
        const options = world.candidates.filter((c) => c.kind === "family").map(optionOf);
        return {
          ok: false,
          error: failure("", "FAMILY_DOC_REQUIRED", `${req.tool} needs a family document, but ${docLabel(candidate)} is a project`, { family: req.familyHint, options }),
          notices,
        };
      }
    }

    // The user switched the active project under an automatic pin since this session's last write (§7.4 item 5).
    const pin = session.pin;
    const inst = world.usable.find((i) => i.instanceId === candidate.instanceId);
    const activeDoc = inst ? activeDocOf(inst) : null;
    if (o.checkActiveSwitch && pin && pin.key === candidate.key && activeDoc && activeDoc.key !== candidate.key) {
      const activeCandidate = world.candidates.find((c) => c.key === activeDoc.key && c.instanceId === inst!.instanceId);
      if (pin.mode === "auto" && isGated(req.kind) && session.lastWriteAt > 0 && activeDoc.kind === "project") {
        const before = session.activeAtLastWrite.get(candidate.instanceId) ?? null;
        const ackKey = `${candidate.key}@active:${activeDoc.key}`;
        if (before !== activeDoc.key && !session.isAcked(ackKey)) {
          session.ack(ackKey);
          return {
            ok: false,
            error: failure("", "TARGET_CHANGED", `Revit's active doc is now ${activeCandidate ? docLabel(activeCandidate) : stripExt(activeDoc.title)}, but this session is pinned to ${docLabel(candidate)}; nothing was written`, {
              pinned: docLabel(candidate),
              active: activeCandidate ? docLabel(activeCandidate) : stripExt(activeDoc.title),
              to: docLabel(candidate),
              reason: "the user switched the active document",
            }),
            notices,
          };
        }
      }
      if (activeCandidate && session.once(`ACTIVE_DIFFERS:${candidate.key}:${activeDoc.key}`)) {
        if (activeCandidate.kind === "family" && activeDoc.familySourceRid === candidate.rid)
          notices.push(`FAMILY_EDITING the user is editing family ${stripExt(activeCandidate.title)} (#${activeCandidate.n}) in Revit ${activeCandidate.year}`);
        else notices.push(`ACTIVE_DIFFERS Revit's active doc is ${docLabel(activeCandidate)}; still working on ${docLabel(candidate)}. set_target {"doc":"active"} switches`);
      }
    }

    if (exactOnly(req.kind) && isGated(req.kind) && candidate.readOnly) {
      const editable = world.candidates.find((c) => c.key === candidate.key && !c.readOnly);
      return {
        ok: false,
        error: failure("", "DOC_READ_ONLY", `${docLabel(candidate)} is read-only`, { editable: editable ? `#${editable.n}` : undefined }),
        notices,
      };
    }
    return { ok: true, doc: resolvedOf(candidate, inst), notices };
  }

  private pickBySelector(req: ResolveRequest, world: World, notices: string[]): { ok: true; candidate?: DocCandidate; doc?: ResolvedDoc } | { ok: false; error: BridgeResponse; notices: string[] } {
    const sel = parseDocParam(req.doc);
    if (!sel) return { ok: false, error: failure("", "INVALID_ARGS", "doc is empty", { param: "doc", reason: "empty" }), notices };
    if (sel.kind === "follow" || sel.kind === "none")
      return { ok: false, error: failure("", "INVALID_ARGS", `doc '${sel.kind}' only works with set_target`, { param: "doc", reason: `${sel.kind} is a set_target mode` }), notices };
    if (sel.kind === "active") {
      const active = this.activeCandidate(world, null);
      if (!active) return { ok: false, error: this.noDocument(world, req.key), notices };
      return { ok: true, candidate: active, doc: resolvedOf(active, world.usable.find((i) => i.instanceId === active.instanceId)) };
    }
    const fuzzy = !exactOnly(req.kind);
    const match = matchDocs(sel, world.candidates, fuzzy);
    if (match.matches.length === 1) {
      const c = match.matches[0]!;
      return { ok: true, candidate: c, doc: resolvedOf(c, world.usable.find((i) => i.instanceId === c.instanceId)) };
    }
    if (match.matches.length > 1) {
      return {
        ok: false,
        error: failure("", "TARGET_AMBIGUOUS", `doc "${String(req.doc)}" matches ${match.matches.length} open documents${exactOnly(req.kind) ? " (writes need an exact match)" : ""}; nothing ran`, {
          options: match.matches.map(optionOf),
        }),
        notices,
      };
    }
    // No exact match for a write, but fuzzy candidates exist: TARGET_AMBIGUOUS with numbered options (§7.3).
    if (exactOnly(req.kind) && sel.kind === "title") {
      const fuzzyMatch = matchDocs(sel, world.candidates, true);
      if (fuzzyMatch.matches.length > 0)
        return {
          ok: false,
          error: failure("", "TARGET_AMBIGUOUS", `doc "${String(req.doc)}" is not an exact title; writes need an exact match; nothing ran`, { options: fuzzyMatch.matches.map(optionOf) }),
          notices,
        };
    }
    const outdated = world.outdated.find((i) => i.snapshot?.docs.some((d) => sel.kind === "title" && stripExt(d.title).toLowerCase() === stripExt(sel.title).toLowerCase()));
    if (outdated) return { ok: false, error: failure("", outdated.compat, `the doc is open in Revit ${outdated.year}, whose add-in does not match this broker`, { year: outdated.year }), notices };
    return {
      ok: false,
      error: failure("", "DOC_NOT_OPEN", `no open document matches "${String(req.doc)}"`, {
        title: sel.kind === "title" ? sel.title : undefined,
        path: sel.kind === "path" ? sel.path : undefined,
        options: world.candidates.map(optionOf),
      }),
      notices,
    };
  }

  private rebind(
    session: Session,
    pin: Pin,
    world: World,
    notices: string[]
  ): { kind: "found"; candidate: DocCandidate } | { kind: "ambiguous"; error: BridgeResponse } | { kind: "closed" | "not_open"; error: BridgeResponse } {
    // 1. fast path: same instance and rid, key or alias matches
    const fast = world.candidates.find((c) => c.instanceId === pin.instanceId && c.rid === pin.rid && (c.key === pin.key || c.aliases.includes(pin.key)));
    if (fast) {
      if (fast.key !== pin.key) {
        notices.push(`SAVED_AS ${stripExt(pin.title)} was saved as ${stripExt(fast.title)}; continuing on ${docLabel(fast)}`);
        this.setPin(session, fast, pin.mode);
      }
      return { kind: "found", candidate: fast };
    }
    // 2. key or alias match anywhere
    const C = world.candidates.filter((c) => c.key === pin.key || (c.aliases.includes(pin.key) && c.pid === pin.pid));
    if (C.length >= 1) {
      let pick: DocCandidate | undefined;
      if (C.length === 1) pick = C[0];
      else {
        pick = C.find((c) => c.pid === pin.pid) ?? (pin.path ? C.find((c) => c.path && c.path.toLowerCase() === pin.path!.toLowerCase()) : undefined);
        if (!pick) {
          const ranked = [...C].sort((a, b) => this.recency(world, b) - this.recency(world, a));
          if (pin.mode === "explicit" && this.recency(world, ranked[0]!) === this.recency(world, ranked[1]!))
            return {
              kind: "ambiguous",
              error: failure("", "TARGET_AMBIGUOUS", `${stripExt(pin.title)} is open in ${C.length} Revit sessions; nothing ran`, { options: C.map(optionOf) }),
            };
          pick = ranked[0];
        }
      }
      const restarted = pick!.pid !== pin.pid;
      notices.push(`REBOUND reconnected to ${docLabel(pick!)} (${restarted ? `Revit ${pick!.year} restarted` : "the doc was reopened"})`);
      this.setPin(session, pick!, pin.mode);
      return { kind: "found", candidate: pick! };
    }
    // 5. gone
    const instanceAlive = world.usable.some((i) => i.instanceId === pin.instanceId);
    const options = world.candidates.map(optionOf);
    if (instanceAlive)
      return { kind: "closed", error: failure("", "TARGET_CLOSED", `the pinned doc ${docLabel({ n: session.peekDocNumber(pin.key) ?? 0, title: pin.title, year: pin.year })} was closed`, { title: stripExt(pin.title), path: pin.path || undefined, options }) };
    return { kind: "not_open", error: failure("", "DOC_NOT_OPEN", `the pinned doc ${stripExt(pin.title)} is not open (Revit ${pin.year} restarted or exited)`, { title: stripExt(pin.title), path: pin.path || undefined, options }) };
  }

  private autoResolve(session: Session, world: World, req: ResolveRequest, notices: string[]): { ok: true; candidate?: DocCandidate } | { ok: false; error: BridgeResponse; notices: string[] } {
    const projects = world.candidates.filter((c) => c.kind === "project");
    const families = world.candidates.filter((c) => c.kind === "family");
    let pick: DocCandidate | undefined;
    if (projects.length === 1) pick = projects[0];
    else if (projects.length > 1) {
      const inst = this.mostRecentInstance(world.usable.filter((i) => projects.some((p) => p.instanceId === i.instanceId)));
      if (inst) {
        const snap = inst.snapshot!;
        pick =
          projects.find((p) => p.instanceId === inst.instanceId && p.rid === snap.activeRid) ??
          projects.find((p) => p.instanceId === inst.instanceId && p.rid === snap.lastActiveProjectRid) ??
          (projects.filter((p) => p.instanceId === inst.instanceId).length === 1 ? projects.find((p) => p.instanceId === inst.instanceId) : undefined);
      }
      if (!pick)
        return {
          ok: false,
          error: failure("", "TARGET_AMBIGUOUS", `${projects.length} projects are open and none is pinned; nothing ran`, { options: projects.map(optionOf) }),
          notices,
        };
    } else if (families.length === 1) pick = families[0];
    else if (families.length > 1) {
      const inst = this.mostRecentInstance(world.usable);
      pick = inst ? families.find((f) => f.instanceId === inst.instanceId && f.rid === inst.snapshot?.activeRid) : undefined;
      if (!pick) return { ok: false, error: failure("", "TARGET_AMBIGUOUS", `${families.length} family documents are open and none is pinned`, { options: families.map(optionOf) }), notices };
    }
    if (!pick) return { ok: false, error: this.noDocument(world, req.key), notices };
    this.setPin(session, pick, "auto");
    if (world.candidates.length > 1) {
      const others = world.candidates.filter((c) => c !== pick).slice(0, 4).map((c) => docLabel(c));
      notices.push(`AUTO_PINNED working on ${docLabel(pick)}${pick.active ? ", the active doc" : ""}; also open: ${others.join(", ")}${world.candidates.length - 1 > others.length ? "..." : ""}. set_target switches`);
    }
    return { ok: true, candidate: pick };
  }

  private activeCandidate(world: World, instanceId: string | null): DocCandidate | undefined {
    const inst = instanceId ? world.usable.find((i) => i.instanceId === instanceId) : this.mostRecentInstance(world.usable);
    if (!inst) return undefined;
    const snap = inst.snapshot!;
    return (
      world.candidates.find((c) => c.instanceId === inst.instanceId && c.rid === snap.activeRid) ??
      world.candidates.find((c) => c.instanceId === inst.instanceId && c.rid === snap.lastActiveProjectRid) ??
      world.candidates.find((c) => c.instanceId === inst.instanceId)
    );
  }

  private instanceForNone(session: Session, world: World, notices: string[]): InstanceInfo {
    const pinned = session.pin ? world.usable.find((i) => i.instanceId === session.pin!.instanceId) : undefined;
    if (pinned) return pinned;
    if (world.usable.length === 1) return world.usable[0]!;
    const recent = this.mostRecentInstance(world.usable)!;
    if (session.once(`INSTANCE_CHOICE:${recent.instanceId}`)) notices.push(`AUTO_PINNED using Revit ${recent.year} (pid ${recent.pid}), the most recently used Revit; pass a doc or set_target to choose`);
    return recent;
  }

  private mostRecentInstance(instances: InstanceInfo[]): InstanceInfo | undefined {
    return [...instances].sort((a, b) => Number(!!b.snapshot?.ui?.foreground) - Number(!!a.snapshot?.ui?.foreground) || b.lastActiveAt - a.lastActiveAt || b.pid - a.pid)[0];
  }

  private recency(world: World, c: DocCandidate): number {
    const inst = world.usable.find((i) => i.instanceId === c.instanceId);
    const doc = inst?.snapshot?.docs.find((d) => d.rid === c.rid);
    const t = doc?.lastActivatedAtUtc ? Date.parse(doc.lastActivatedAtUtc) : 0;
    return Math.max(Number.isFinite(t) ? t : 0, c.active ? (inst?.lastActiveAt ?? 0) : 0);
  }

  private noRevit(world: World, req: ResolveRequest): BridgeResponse {
    if (world.outdated.length > 0) {
      const o = world.outdated[0]!;
      return failure("", o.compat, `Revit ${o.year} runs an add-in that does not match this broker (protocol ${o.snapshot?.protocol?.min ?? "?"}..${o.snapshot?.protocol?.max ?? "?"})`, {
        year: o.year,
        addinVersion: o.snapshot?.addinVersion,
      });
    }
    if (world.starting.length > 0) return failure("", "REVIT_STARTING", `Revit ${world.starting[0]!.year} is still starting`, { year: world.starting[0]!.year });
    const last = this.registry.lastExited();
    const lastExited = last ? { year: last.year, pid: last.pid, doc: last.docs[0], at: new Date(last.at).toISOString() } : undefined;
    return failure(
      "",
      "NO_REVIT_RUNNING",
      last ? `no Revit with the add-in is running (Revit ${last.year}, pid ${last.pid}${last.docs[0] ? `, which had ${last.docs[0]} open,` : ""} exited)` : "no Revit with the add-in is running",
      lastExited ? { lastExited } : null
    );
  }

  private noDocument(world: World, key: string): BridgeResponse {
    const inst = this.mostRecentInstance(world.usable);
    return failure("", "NO_OPEN_DOCUMENT", `Revit ${inst?.year ?? ""} is running with no document open`.replace("  ", " "), { year: inst?.year });
  }

  // ------------------------------------------------------------------------------------------------ pin changes

  private setPin(session: Session, c: DocCandidate, mode: Pin["mode"]): void {
    session.pin = { key: c.key, rid: c.rid, instanceId: c.instanceId, pid: c.pid, year: c.year, mode, title: c.title, kind: c.kind, path: c.path || undefined, setAt: Date.now() };
    if (c.kind === "project") session.projectPin = { ...session.pin };
  }

  /** set_target (SPEC §7.3): 'active', 'follow', 'none', #n, title, path, title@year. Explicit pin + TARGET_NOW. */
  async setTarget(session: Session, raw: unknown, instance: unknown, deadline: Deadline): Promise<{ ok: true; pin: Pin | null; text: string; notices: string[]; doc: ResolvedDoc | null } | { ok: false; error: BridgeResponse }> {
    const sel = parseDocParam(raw);
    if (!sel) return { ok: false, error: failure("", "INVALID_ARGS", "set_target needs doc: 'active', 'follow', 'none', #, title or path", { param: "doc", reason: "missing", example: { doc: "active" } }) };
    if (sel.kind === "none") {
      session.pin = null;
      return { ok: true, pin: null, text: "unpinned; the next call picks the doc automatically", notices: [], doc: null };
    }
    const world = await this.world(session, deadline, true, instance);
    if (world.usable.length === 0) return { ok: false, error: this.noRevit(world, { session, scope: "any", kind: "control", key: "set_target", tool: "set_target", deadline }) };
    let candidate: DocCandidate | undefined;
    if (sel.kind === "active" || sel.kind === "follow") {
      candidate = this.activeCandidate(world, null);
      if (!candidate) return { ok: false, error: this.noDocument(world, "set_target") };
    } else {
      const match = matchDocs(sel as Exclude<DocSelector, { kind: "active" | "follow" | "none" }>, world.candidates, true);
      if (match.matches.length > 1) {
        // Prefer exact title duplicates resolved by instance filter; otherwise ask.
        return { ok: false, error: failure("", "TARGET_AMBIGUOUS", `"${String(raw)}" matches ${match.matches.length} open documents`, { options: match.matches.map(optionOf) }) };
      }
      candidate = match.matches[0];
      if (!candidate) return { ok: false, error: failure("", "DOC_NOT_OPEN", `no open document matches "${String(raw)}"`, { title: sel.kind === "title" ? sel.title : undefined, path: sel.kind === "path" ? sel.path : undefined, options: world.candidates.map(optionOf) }) };
    }
    this.setPin(session, candidate, sel.kind === "follow" ? "follow" : "explicit");
    const inst = world.usable.find((i) => i.instanceId === candidate!.instanceId);
    const label = docLabel(candidate);
    return {
      ok: true,
      pin: session.pin,
      text: sel.kind === "follow" ? `following the active doc, now ${label}` : `target ${label}`,
      notices: [`TARGET_NOW ${label}${sel.kind === "follow" ? " (follow mode)" : ""}`],
      doc: resolvedOf(candidate, inst),
    };
  }

  /** Explicit re-pin after a successful activate/open/new_project/new_family/edit_family open (§7.4 item 1). */
  repin(session: Session, doc: ResponseDoc, instance: { instanceId: string; pid: number; year: number }): string {
    const n = session.docNumber(doc.key);
    session.pin = { key: doc.key, rid: doc.rid, instanceId: instance.instanceId, pid: instance.pid, year: doc.year ?? instance.year, mode: "explicit", title: doc.title, kind: doc.kind, setAt: Date.now() };
    if (doc.kind === "project") session.projectPin = { ...session.pin };
    this.registry.invalidate();
    return `TARGET_NOW ${docLabel({ n, title: doc.title, year: doc.year ?? instance.year })}`;
  }

  /** Closing the pinned doc with manage_document close clears the pin (mode auto) (§7.4 item 2). */
  onClosed(session: Session, key: string | null): string | null {
    this.registry.invalidate();
    if (!key) return null;
    const notices: string[] = [];
    if (session.pin && session.pin.key === key) {
      notices.push(`PIN_MOVED ${stripExt(session.pin.title)} was closed; the next call picks the doc automatically`);
      session.pin = null;
    }
    if (session.projectPin && session.projectPin.key === key) session.projectPin = null;
    return notices[0] ?? null;
  }

  /** Record a successful write for the follow-mode and active-switch gates. */
  async noteWrite(session: Session, doc: ResolvedDoc): Promise<void> {
    if (!doc.key) return;
    session.lastWriteDocKey = doc.key;
    session.lastWriteAt = Date.now();
    const inst = (await this.registry.list()).find((i) => i.instanceId === doc.instanceId);
    session.activeAtLastWrite.set(doc.instanceId, inst ? (activeDocOf(inst)?.key ?? null) : null);
  }

  /** Doc candidates (status display) without pinning. */
  async preview(session: Session, deadline: Deadline): Promise<{ world: World; target: DocCandidate | null; mode: Pin["mode"] | "auto?" | null }> {
    const world = await this.world(session, deadline, false);
    const pin = session.pin;
    if (pin) {
      const c = world.candidates.find((x) => x.key === pin.key || x.aliases.includes(pin.key)) ?? null;
      if (pin.mode === "follow") return { world, target: this.activeCandidate(world, null) ?? null, mode: "follow" };
      return { world, target: c, mode: pin.mode };
    }
    const projects = world.candidates.filter((c) => c.kind === "project");
    let target: DocCandidate | null = null;
    if (projects.length === 1) target = projects[0]!;
    else if (projects.length > 1) {
      const inst = this.mostRecentInstance(world.usable.filter((i) => projects.some((p) => p.instanceId === i.instanceId)));
      target = inst ? (projects.find((p) => p.instanceId === inst.instanceId && p.rid === inst.snapshot?.activeRid) ?? projects.find((p) => p.instanceId === inst.instanceId && p.rid === inst.snapshot?.lastActiveProjectRid) ?? null) : null;
    } else {
      const families = world.candidates.filter((c) => c.kind === "family");
      if (families.length === 1) target = families[0]!;
    }
    return { world, target, mode: target ? "auto?" : null };
  }
}

// ------------------------------------------------------------------------------------------------ helpers

function candidateOf(session: Session, info: InstanceInfo, d: SnapshotDoc): DocCandidate {
  return {
    n: session.docNumber(d.key, d.aliases ?? []),
    title: d.title,
    path: d.path ?? "",
    central: d.central ?? null,
    key: d.key,
    aliases: d.aliases ?? [],
    year: info.year,
    kind: d.kind === "family" ? "family" : "project",
    instanceId: info.instanceId,
    pid: info.pid,
    rid: d.rid,
    active: info.snapshot?.activeRid === d.rid,
    readOnly: d.readOnly === true,
  };
}

function resolvedOf(c: DocCandidate, inst: InstanceInfo | undefined): ResolvedDoc {
  const snap = inst?.snapshot?.docs.find((d) => d.rid === c.rid);
  return {
    instanceId: c.instanceId,
    pid: c.pid,
    year: c.year,
    rid: c.rid,
    key: c.key,
    title: c.title,
    kind: c.kind,
    n: c.n,
    path: c.path || undefined,
    readOnly: c.readOnly,
    modified: snap?.modified,
    generation: snap?.generation,
    workshared: snap?.workshared,
    active: c.active,
    snapshot: snap,
  };
}

function instanceOnly(info: InstanceInfo): ResolvedDoc {
  return { instanceId: info.instanceId, pid: info.pid, year: info.year, rid: null, key: null, title: null, kind: null, n: null, active: false };
}

function activeDocOf(inst: InstanceInfo): SnapshotDoc | null {
  const snap = inst.snapshot;
  if (!snap || snap.activeRid === undefined || snap.activeRid === null) return null;
  return snap.docs.find((d) => d.rid === snap.activeRid) ?? null;
}

export interface DocOption {
  value: string;
  label: string;
}

function optionOf(c: DocCandidate): DocOption {
  const flags = [c.kind === "family" ? "family" : null, c.active ? "active" : null, c.readOnly ? "read-only" : null].filter(Boolean);
  return { value: `#${c.n}`, label: `#${c.n} ${stripExt(c.title)} (Revit ${c.year}${flags.length ? ", " + flags.join(", ") : ""})` };
}

function parseInstanceFilter(raw: unknown): { kind: "year" | "pid"; value: number } | null {
  if (raw === undefined || raw === null || String(raw).trim() === "") return null;
  const n = Number(String(raw).trim().replace(/^r(?=\d{4})/i, ""));
  if (!Number.isInteger(n) || n <= 0) return null;
  return n >= 2000 && n <= 2100 ? { kind: "year", value: n } : { kind: "pid", value: n };
}
