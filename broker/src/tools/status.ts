// status (SPEC §5.9, D1 §7.4, D2 §17): reads only control `snapshot` + `health` of every live instance, in parallel,
// 800 ms each (budget 5 s); never waits for Revit's UI thread. Compact output: instances [year,pid,state], docs table
// [#, title, kind, year, flags], target, active view, selection count, levels of the target doc (≤30). No paths and no
// broker/auth internals unless detail:"full". include= adds sections from other reads with a shared 10 s budget.
// detail:"full" (D2 §17.3) adds broker facts (versions, home, auth, settings summary, code execution, legacy roots,
// Codex config findings, log file, transport counters, exits seen, in-flight and recovered writes), per-instance facts
// (add-in build and catalog match, pipes, registration health, this broker's limiter/coalescing/fail-fast counters,
// queue, pump, ui, native, listeners) and problems[].
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { catalogHash } from "@revit-mcp-next/contracts/catalog";
import { BRIDGE_PROTOCOL_VERSION, type DocSnapshot, type HealthData, type HelloData } from "@revit-mcp-next/contracts/protocol";
import type { InstanceInfo } from "../instances/InstanceRegistry.js";
import { findLegacyRoots } from "../runtime/home.js";
import { Deadline } from "../runtime/deadline.js";
import { faultCount } from "../runtime/crashGuards.js";
import { renderText } from "../framework/render.js";
import { docLabel, stripExt, type DocCandidate } from "../targeting/docParam.js";
import type { ToolContext, ToolModule, ToolOutcome } from "../framework/types.js";

interface InstanceView {
  info: InstanceInfo;
  snapshot: DocSnapshot | null;
  health: HealthData | null;
  hello: HelloData | null;
  state: string;
}

function stateOf(info: InstanceInfo, snapshot: DocSnapshot | null, health: HealthData | null): string {
  if (info.compat === "ADDIN_OUTDATED" || info.compat === "ADDIN_NEWER_THAN_BROKER") return info.compat === "ADDIN_OUTDATED" ? "add-in outdated" : "add-in newer than broker";
  const state = snapshot?.state ?? info.state;
  if (state === "starting") return "starting";
  if (state === "stopping") return "stopping";
  if (!snapshot && !health) return "unreachable";
  const ui = health?.ui ?? snapshot?.ui;
  if (ui && ui.mainWindowEnabled === false && !(ui.popup && ui.popup.isProgress)) return `dialog: ${ui.popup?.title ?? health?.lastDialog?.title ?? "open"}`;
  if (ui?.hung) return "not responding";
  const native = health?.native ?? snapshot?.native;
  if (native) return `busy: ${native.kind}`;
  const exec = health?.queue?.executing ?? null;
  if (exec) return `busy: ${exec.op} ${Math.round((exec.elapsedMs ?? 0) / 1000)} s`;
  if (snapshot?.executing) return `busy: ${snapshot.executing.op}`;
  return "idle";
}

function flagsOf(c: DocCandidate, target: DocCandidate | null, snap: DocSnapshot | null): string {
  const d = snap?.docs.find((x) => x.rid === c.rid);
  const flags = [target && target.key === c.key ? "target" : "", c.active ? "active" : "", d?.workshared ? "workshared" : "", d?.modified ? "modified" : "", c.readOnly ? "readonly" : ""].filter(Boolean);
  return flags.join(",");
}

async function handle(ctx: ToolContext): Promise<ToolOutcome> {
  const services = ctx.services;
  const full = ctx.args.detail === "full";
  const settingsState = services.settings.current();
  const settings = settingsState.settings;
  if (settingsState.invalid) ctx.warn("SETTINGS_INVALID", `${settingsState.invalid}; the last good values are used`);
  if (settings.enableCodeExecution) ctx.notice("CODE_EXECUTION_ENABLED", `since ${settingsState.mtimeMs ? new Date(settingsState.mtimeMs).toISOString() : "start"}${services.codeExecutionListed ? "" : " (start a new chat thread to list run_csharp)"}`);

  const live = await ctx.instances();
  const filter = ctx.args.instance !== undefined ? String(ctx.args.instance).trim() : null;
  const instances = filter ? live.filter((i) => String(i.year) === filter || String(i.pid) === filter || i.instanceId === filter) : live;
  if (instances.length === 0) {
    const last = services.registry.lastExited();
    const data: Record<string, unknown> = { revit: [] };
    if (last) data.lastExited = { year: last.year, pid: last.pid, doc: last.docs[0], at: new Date(last.at).toISOString() };
    if (full) data.broker = brokerFacts(ctx);
    return {
      status: "ok",
      summary: filter && live.length > 0 ? `no running Revit matches instance ${filter}` : "no Revit with the add-in is running",
      data,
      next: "ask the user: start Revit 2024 or 2027 with the revit-mcp-next add-in and open the model, then call status again",
      doc: null,
    };
  }

  // Snapshot + health of every live instance in parallel, 800 ms each.
  const views: InstanceView[] = await Promise.all(
    instances.map(async (info) => {
      const channel = services.channels.get(info);
      const [snapshot, health, hello] = await Promise.all([
        channel.snapshot(800).catch(() => null),
        channel.health(800).catch(() => null),
        full ? channel.hello(800).then((r) => (r.ok ? (r.data as HelloData) : null)).catch(() => null) : Promise.resolve(null),
      ]);
      const snap = snapshot ?? info.snapshot;
      return { info, snapshot: snap, health, hello, state: stateOf(info, snap, health) };
    })
  );

  const preview = await services.resolver.preview(ctx.session, ctx.deadline);
  const target = preview.target;
  const candidates = preview.world.candidates.filter((c) => instances.some((i) => i.instanceId === c.instanceId));
  const rows = candidates.map((c) => {
    const view = views.find((v) => v.info.instanceId === c.instanceId);
    return [c.n, stripExt(c.title), c.kind, c.year, flagsOf(c, target, view?.snapshot ?? null)];
  });
  const targetView = target ? views.find((v) => v.info.instanceId === target.instanceId) : undefined;
  const targetDoc = target ? targetView?.snapshot?.docs.find((d) => d.rid === target.rid) : undefined;

  const data: Record<string, unknown> = {
    revit: views.map((v) => [v.info.year, v.info.pid, v.state]),
    docs: { cols: ["#", "title", "kind", "year", "flags"], rows },
  };
  if (target) data.target = { doc: `#${target.n}`, mode: preview.mode === "auto?" ? "auto (pinned on first use)" : preview.mode };
  if (targetDoc?.activeView) data.view = targetDoc.activeView;
  if (targetDoc?.selection) data.selected = targetDoc.selection.count;
  if (targetDoc?.levels?.length) {
    // Compact status stays within its 2 KB budget on models with many levels (e2e R13 adds 160): the lowest levels
    // here, list {"kind":"levels"} for all of them.
    const shown = full ? 30 : 8;
    data.levels = { cols: ["name", "elevation"], rows: targetDoc.levels.slice(0, shown).map((l) => [l[1], l[2]]) };
    const more = Math.max(0, targetDoc.levels.length - shown) + (targetDoc.levelsMore ?? 0);
    if (more > 0) (data.levels as Record<string, unknown>).more = more;
  }

  // include sections (shared 10 s budget).
  const include = Array.isArray(ctx.args.include) ? (ctx.args.include as string[]) : [];
  if (include.length > 0) {
    const budget = Deadline.start(10_000, ctx.signal);
    const sections: Record<string, unknown> = {};
    const docArg = ctx.args.doc !== undefined ? { doc: ctx.args.doc } : target ? { doc: `#${target.n}` } : {};
    const run = async (name: string, tool: string, args: Record<string, unknown>) => {
      if (budget.remaining() < 1_000) {
        sections[name] = "unavailable: BUDGET";
        return;
      }
      const o = await ctx.invoke(tool, { ...docArg, ...args }, { deadline: budget.child(Math.max(1_000, budget.remaining())) });
      sections[name] = o.status === "error" ? `unavailable: ${o.code}${o.fix ? ` (fix: ${o.fix})` : ""}` : o.data ?? o.summary;
    };
    for (const name of include) {
      switch (name) {
        case "views":
          await run("views", "list", { kind: "views" });
          break;
        case "selection":
          await run("selection", "find_elements", { from: "selection" });
          break;
        case "view_elements":
          await run("view_elements", "find_elements", { view: "active", group_by: "category" });
          break;
        case "readiness":
          await run("readiness", "check_model", { check: "readiness" });
          break;
        case "warnings":
          await run("warnings", "check_model", { check: "warnings" });
          break;
        case "context": {
          const context: Record<string, unknown> = {};
          for (const kind of ["project_info", "phases", "worksets", "links"]) {
            if (budget.remaining() < 1_000) {
              context[kind] = "unavailable: BUDGET";
              continue;
            }
            const o = await ctx.invoke("list", { ...docArg, kind }, { deadline: budget.child(Math.max(1_000, budget.remaining())) });
            context[kind] = o.status === "error" ? `unavailable: ${o.code}` : o.data ?? o.summary;
          }
          sections.context = context;
          break;
        }
        case "writes": {
          if (!target || !targetView) {
            sections.writes = "unavailable: NO_OPEN_DOCUMENT";
            break;
          }
          const r = await ctx.control(targetView.info, "recent_writes", { docKey: target.key, limit: 20 }, 2_000);
          sections.writes = r.ok ? r.data : `unavailable: ${r.code}`;
          break;
        }
      }
    }
    data.sections = sections;
  }

  if (full) {
    data.broker = brokerFacts(ctx);
    data.instances = views.map((v) => instanceFacts(v, ctx));
    data.docsFull = candidates.map((c) => {
      const d = views.find((v) => v.info.instanceId === c.instanceId)?.snapshot?.docs.find((x) => x.rid === c.rid);
      return { n: c.n, title: c.title, key: c.key, aliases: c.aliases, path: c.path, central: c.central ?? undefined, rid: c.rid, generation: d?.generation, readOnly: c.readOnly, workshared: d?.workshared, cloud: d?.cloud };
    });
    data.session = {
      pin: ctx.session.pin ? { ...ctx.session.pin, n: ctx.session.peekDocNumber(ctx.session.pin.key) } : null,
      projectPin: ctx.session.projectPin ? { title: ctx.session.projectPin.title, year: ctx.session.projectPin.year } : null,
      confirmTokens: ctx.session.confirms.size,
      jobs: ctx.session.jobs.values().slice(-10).map(([id, j]) => [id, j.what]),
      writes: ctx.session.writes.values().slice(-10).map(([id, w]) => [id, w.key, w.state]),
    };
    const problems: string[] = [];
    for (const v of views) {
      if (v.info.compat !== "ok" && v.info.compat !== "unknown") problems.push(`Revit ${v.info.year}: ${v.info.compat}`);
      if (v.hello && v.hello.catalogHash && v.hello.catalogHash !== catalogHash()) problems.push(`Revit ${v.info.year}: catalog differs from the broker's (rebuild/restart Revit)`);
      if (v.hello?.capabilities?.mismatches?.length) problems.push(`Revit ${v.info.year}: ${v.hello.capabilities.mismatches.length} catalog/handler mismatches`);
      if (v.hello && v.hello.home && v.hello.home.toLowerCase() !== services.home.toLowerCase()) problems.push(`Revit ${v.info.year} uses home ${v.hello.home}, the broker uses ${services.home} (HOME_MISMATCH)`);
      const authFp = services.auth.current().fp;
      if (v.hello && v.hello.authFp && authFp && v.hello.authFp !== authFp) problems.push(`Revit ${v.info.year}: auth token differs (AUTH_MISMATCH)`);
      if (v.state.startsWith("dialog")) problems.push(`Revit ${v.info.year}: ${v.state}; calls wait until it is answered (ui {"op":"dialogs"})`);
      if (v.hello && v.hello.authState && v.hello.authState !== "ok") problems.push(`Revit ${v.info.year}: auth ${v.hello.authState} (AUTH_NOT_CONFIGURED); restart Revit or re-run the installer`);
      if (v.info.source === "derived" || v.info.source === "last-good") problems.push(`Revit ${v.info.year}: registration file unusable (${v.info.fileProblem ?? "unreadable"}); served from ${v.info.source === "derived" ? "the file name" : "the last good copy"}`);
    }
    const bundledNode = join(services.home, "runtime", "node.exe");
    if (services.homeInstalled && existsSync(bundledNode) && process.execPath.toLowerCase() !== bundledNode.toLowerCase())
      problems.push(`the broker runs on ${process.execPath}, not the installed ${bundledNode}; the client config may point at an old install`);
    if (services.log.isDisabled) problems.push("broker file logging is disabled (the logs folder is not writable)");
    if (!services.homeInstalled) problems.push(`home ${services.home} has no marker (HOME_NOT_INSTALLED)`);
    if (services.homeWarning) problems.push(`home: ${services.homeWarning}`);
    if (problems.length) data.problems = problems;
  }

  const years = views.map((v) => `${v.info.year} (pid ${v.info.pid})`);
  const summary = [
    `Revit ${years.join(" and ")}`,
    `${candidates.length} doc${candidates.length === 1 ? "" : "s"}`,
    target ? `target ${docLabel(target)}${preview.mode === "auto?" ? " (auto)" : ""}` : candidates.length ? "no target yet" : "no document open",
    targetDoc?.activeView ? `active view ${targetDoc.activeView.name} (${targetDoc.activeView.type})` : "",
    targetDoc?.selection ? `${targetDoc.selection.count} selected` : "",
  ].filter(Boolean);
  const firstLevel = targetDoc?.levels?.[0]?.[1];
  const next = candidates.length === 0
    ? `ask the user to open a model, or manage_document {"op":"open","path":"<file.rvt>"}`
    : `find_elements {"category":["Walls"]${firstLevel ? `,"level":${JSON.stringify(firstLevel)}` : ""}} or capture {}`;
  return {
    status: "ok",
    summary: summary.join("; "),
    data,
    next,
    doc: target ? { title: target.title, year: target.year, n: target.n } : null,
  };
}

function brokerFacts(ctx: ToolContext): Record<string, unknown> {
  const s = ctx.services;
  const auth = s.auth.current();
  const st = s.settings.current();
  return {
    version: s.build.version,
    gitSha: s.build.gitSha || undefined,
    sdk: s.build.sdk || undefined,
    node: process.version,
    pid: process.pid,
    protocol: BRIDGE_PROTOCOL_VERSION,
    client: { name: ctx.session.client.name, version: ctx.session.client.version, mcp: ctx.session.client.protocol },
    home: s.home,
    homeSource: s.homeSource,
    execPath: process.execPath,
    profile: s.profile,
    structured: s.structured,
    budgetS: Math.round(st.settings.callBudgetMs / 1000),
    auth: { file: auth.file, source: auth.source, fp: auth.fp ?? undefined },
    catalogHash: catalogHash().slice(0, 12),
    faults: faultCount(),
    settings: {
      file: s.settings.path,
      exists: st.exists,
      invalid: st.invalid ?? undefined,
      issues: st.issues.length ? st.issues : undefined,
      enableCodeExecution: st.settings.enableCodeExecution,
      codeExecutionListed: s.codeExecutionListed,
      consentPrompt: st.settings.codeExecution.consentPrompt,
      confirm: st.settings.confirm,
      useElicitation: st.settings.useElicitation,
    },
    legacyRoots: findLegacyRoots(),
    codex: codexFindings(ctx),
    log: { file: s.log.currentFile ?? undefined, level: s.log.level, disabled: s.log.isDisabled || undefined },
    transport: {
      registry: s.registry.stats,
      exited: s.registry.exitedSince(60 * 60_000).map((e) => ({ year: e.year, pid: e.pid, docs: e.docs.slice(0, 3), at: new Date(e.at).toISOString() })),
      retiredInFlight: s.channels.retiredInFlight() || undefined,
      readsKept: s.results.size || undefined,
      inflightWrites: s.journal.pending().length || undefined,
      journal: s.journal.stats,
      recoveredWrites: s.journal.recoveryFinished ? s.journal.peekRecovered().map((r) => ({ write: r.entry.shortId, key: r.entry.key, state: r.state, sent: r.entry.sentAt })) : "recovering",
    },
  };
}

/** Per-instance transport counters of this broker (limiter, coalescing, fail-fast, retries, recoveries). */
function channelFacts(ctx: ToolContext, instanceId: string): Record<string, unknown> | undefined {
  const channel = ctx.services.channels.peek(instanceId);
  if (!channel) return undefined;
  const latest = channel.poller.latest();
  return {
    limiter: channel.limiterState(),
    inFlight: channel.inFlight,
    counters: channel.stats,
    lastHealthAgoMs: latest ? Date.now() - latest.at : undefined,
  };
}

function instanceFacts(v: InstanceView, ctx: ToolContext): Record<string, unknown> {
  const h = v.health;
  return {
    year: v.info.year,
    pid: v.info.pid,
    instanceId: v.info.instanceId,
    state: v.state,
    build: v.snapshot?.build,
    language: v.snapshot?.language,
    addin: v.hello ? { version: v.hello.addinVersion, gitSha: v.hello.gitSha, payloadId: v.hello.payloadId, catalogMatch: v.hello.catalogHash ? v.hello.catalogHash === catalogHash() : undefined } : { version: v.snapshot?.addinVersion, gitSha: v.snapshot?.gitSha, payloadId: v.snapshot?.payloadId },
    protocol: v.snapshot?.protocol,
    compat: v.info.compat,
    home: v.hello?.home ?? v.snapshot?.home,
    auth: v.hello ? { state: v.hello.authState, fpMatch: v.hello.authFp ? v.hello.authFp === ctx.services.auth.current().fp : undefined } : undefined,
    pipes: { primary: v.info.pipe, control: v.info.controlPipe },
    snapshot: { source: v.info.source, seq: v.snapshot?.seq, fileAgeMs: v.info.fileAgeMs, problem: v.info.fileProblem },
    broker: channelFacts(ctx, v.info.instanceId),
    queue: h?.queue,
    pump: h?.pump,
    ui: h?.ui ?? v.snapshot?.ui,
    native: h?.native ?? v.snapshot?.native ?? undefined,
    lastIdlingAtUtc: h?.lastIdlingAtUtc ?? v.snapshot?.lastIdlingAtUtc ?? undefined,
    lastDialog: h?.lastDialog ?? undefined,
    listeners: h?.listeners,
    admissionRejections: h?.admissionRejections,
    codeExecution: v.snapshot?.codeExecution,
    mismatches: v.hello?.capabilities?.mismatches?.length ? v.hello.capabilities.mismatches : undefined,
    testOps: v.hello?.testOps || undefined,
  };
}

/** Codex config scan (D2 §16.4) when the client is Codex: tool_timeout_sec and conflicting entries. */
function codexFindings(ctx: ToolContext): string[] | undefined {
  if (!/codex/i.test(ctx.session.client.name)) return undefined;
  const codexHome = process.env.CODEX_HOME?.trim() || (process.env.USERPROFILE ?? process.env.HOME ? join((process.env.USERPROFILE ?? process.env.HOME)!, ".codex") : "");
  if (!codexHome) return undefined;
  const file = join(codexHome, "config.toml");
  if (!existsSync(file)) return [`${file} not found`];
  const findings: string[] = [];
  try {
    const sections = tomlSections(readFileSync(file, "utf8"));
    const revit = sections.get("mcp_servers.revit");
    if (!revit) findings.push("[mcp_servers.revit] not found");
    else {
      const timeout = /^\s*tool_timeout_sec\s*=\s*(\d+(?:\.\d+)?)/m.exec(revit)?.[1];
      if (!timeout || Number(timeout) < 60) findings.push(`tool_timeout_sec is ${timeout ?? "unset (Codex default 60)"}; 120 is recommended`);
      if (isDisabled(revit)) findings.push("[mcp_servers.revit] has enabled = false");
    }
    for (const [name, body] of sections) {
      if (name === "mcp_servers.revit-mcp-next" && !isDisabled(body)) findings.push("a legacy [mcp_servers.revit-mcp-next] entry is still enabled; remove it so only one broker drives Revit");
      if (/revit-mcp-cowork/i.test(name) && !isDisabled(body)) findings.push(`[${name}] is enabled; the revit-mcp-cowork plugin's hooks act on every revit tool call`);
    }
  } catch (error) {
    findings.push(`cannot read ${file}: ${(error as Error).message}`);
  }
  return findings;
}

/** Plain TOML section splitter: header name (quotes removed) → body text. Enough for the checks above. */
function tomlSections(text: string): Map<string, string> {
  const out = new Map<string, string>();
  let name = "";
  let body: string[] = [];
  const flush = () => {
    if (name) out.set(name, (out.get(name) ?? "") + body.join("\n"));
  };
  for (const line of text.split(/\r?\n/)) {
    const header = /^\s*\[\[?\s*([^\]]+?)\s*\]\]?\s*(#.*)?$/.exec(line);
    if (header) {
      flush();
      name = header[1]!.split(".").map((part) => part.trim().replace(/^["']|["']$/g, "")).join(".");
      body = [];
    } else body.push(line);
  }
  flush();
  return out;
}

function isDisabled(body: string): boolean {
  return /^\s*enabled\s*=\s*false\b/m.test(body);
}

export const module: ToolModule = { name: "status", handle };
void renderText;
