// The broker seam (SPEC §9B): tool modules see only ToolContext; transport, targeting and jobs live behind it.
// Frozen in wave 2: tool lanes extend behaviour through ToolModule hooks, never by editing the framework.

import type { Kind, OpMeta, OpSpec, ToolSpec } from "@revit-mcp-next/contracts/catalog";
import type { Settings } from "@revit-mcp-next/contracts/home";
import type { BridgeResponse, ControlOp, DocKind, SnapshotDoc } from "@revit-mcp-next/contracts/protocol";
import type { InstanceInfo } from "../instances/InstanceRegistry.js";
import type { Deadline } from "../runtime/deadline.js";
import type { Session } from "./session.js";

export type { InstanceInfo } from "../instances/InstanceRegistry.js";

export interface ToolModule {
  /** Catalog tool name. */
  name: string;
  /** Omit to use the default handler for the op's kind. */
  handle?: (ctx: ToolContext) => Promise<ToolOutcome>;
  /** Optional pre-call hook (extra validation, arg rewriting). Return an outcome to answer without calling Revit. */
  prepare?: (ctx: ToolContext) => Promise<void | ToolOutcome>;
  /** Optional post-call hook. */
  finish?: (ctx: ToolContext, r: AddinResult, o: ToolOutcome) => Promise<ToolOutcome>;
}

/** The target doc (or, for scope "none", only the Revit instance) a call runs against. */
export interface ResolvedDoc {
  instanceId: string;
  pid: number;
  year: number;
  /** null for scope none (instance only). */
  rid: number | null;
  key: string | null;
  title: string | null;
  kind: DocKind | null;
  /** Session doc number (#n). */
  n: number | null;
  path?: string;
  readOnly?: boolean;
  modified?: boolean;
  generation?: number;
  workshared?: boolean;
  /** The doc is the UI-active doc of its Revit window. */
  active: boolean;
  snapshot?: SnapshotDoc;
}

/** Which instance a control op goes to. */
export type InstanceRef = string | ResolvedDoc | InstanceInfo;

export interface CallOptions {
  /** Target doc; default = ctx.resolveDoc(). null sends doc:null (scope none). */
  doc?: ResolvedDoc | null;
  /** write/code only; default from args.preview. */
  mode?: "apply" | "preview";
  /** Start as a job (job:always ops); the broker waits inline until the budget. */
  asJob?: boolean;
  /** Apply a confirmed plan. */
  confirmed?: { stamp: number; deleteSet?: number[] };
  /** Ask for page.ids (all matching ids) to keep a handle. */
  wantIds?: boolean;
  /** Override the request kind (default: the registry kind of the key). */
  kind?: Kind;
  /** Use this deadline instead of ctx.deadline (sub-calls). */
  deadline?: Deadline;
}

/** The add-in response plus broker-side facts. Broker-generated failures (NO_REVIT_RUNNING, BRIDGE_BUSY...) use the same shape. */
export interface AddinResult extends BridgeResponse {
  /** Registry key that was called. */
  key: string;
  instanceId?: string;
  year?: number;
  pid?: number;
  /** Session write id (w#) allocated for this call. */
  writeId?: string;
  /** Session job id (j#) when the call became a job or a still-running read. */
  jobRef?: string;
  coalesced?: boolean;
  recovered?: boolean;
  retried?: boolean;
  elapsedMs?: number;
}

export interface ImageBlock {
  type: "image";
  mimeType: string;
  data: string;
  _meta?: Record<string, unknown>;
}

export type OutcomeStatus = "ok" | "not_applied" | "running" | "error";

export interface OutcomeDoc {
  title: string;
  year: number;
  n?: number | null;
}

export interface ToolOutcome {
  status: OutcomeStatus;
  summary: string;
  code?: string;
  data?: unknown;
  /** Lines rendered as "notice: <text>" (each starts with its code). */
  notices?: string[];
  /** Lines rendered as "warn: <text>" (each starts with its code). */
  warnings?: string[];
  images?: ImageBlock[];
  /** Rendered as "more: <text>", e.g. find_elements {"page":"p4"}. */
  more?: string;
  /** Rendered as "next: <text>". */
  next?: string;
  /** Errors: the rendered fix (without "fix: "). Filled from the ErrorCatalog when absent. */
  fix?: string;
  /** Errors: numbered options. */
  options?: string[];
  details?: unknown;
  /** Doc suffix " - doc: <title> (Revit <year>)". Filled from the call's doc when absent. */
  doc?: OutcomeDoc | null;
  /** Extra text blocks after the main block (read_many). */
  blocks?: string[];
  /** Do not apply the size cap trimming (help topics). */
  raw?: boolean;
}

export interface ToolContext {
  readonly tool: string;
  readonly op: string | null;
  readonly key: string;
  readonly meta: OpMeta;
  /** Normalized + validated args (mutable in prepare). */
  args: Record<string, unknown>;
  readonly session: Session;
  readonly deadline: Deadline;
  readonly signal: AbortSignal;
  readonly settings: Settings;
  progress(message: string, done?: number, total?: number): void;
  notice(code: string, text: string): void;
  warn(code: string, text: string, ids?: number[]): void;
  /** Scope rules, doc param grammar, pin, gates (§7). Cached per call. */
  resolveDoc(): Promise<ResolvedDoc | null>;
  call(key?: string, args?: object, o?: CallOptions): Promise<AddinResult>;
  control(instance: InstanceRef, op: ControlOp, payload?: object, timeoutMs?: number): Promise<AddinResult>;
  /** Live instances with snapshots. */
  instances(): Promise<InstanceInfo[]>;
  /** Standard mapping incl. handles, pages, needsConfirm → NOT APPLIED, jobs, images. */
  outcomeFrom(r: AddinResult): ToolOutcome;

  // ---- additive members (beyond SPEC §9B) used by orchestrating tools
  readonly spec: ToolSpec;
  readonly opSpec: OpSpec;
  /** Args as received from the client (before normalization). */
  readonly rawArgs: Record<string, unknown>;
  /** The effective args used for fix rendering (confirm/page calls expand to the stored args). */
  readonly effectiveArgs: Record<string, unknown>;
  readonly services: BrokerServices;
  /** An error outcome whose fix comes from the ErrorCatalog. */
  fail(code: string, message: string, details?: Record<string, unknown>): ToolOutcome;
  /** Run another tool through the full pipeline (normalize, validate, resolve, handle) — read_many, status include. */
  invoke(tool: string, args: Record<string, unknown>, o?: { deadline?: Deadline }): Promise<ToolOutcome>;
  /** A context describing another call of this session (job_status renders a job's result like the original call). */
  forCall(tool: string, args: Record<string, unknown>, o?: { op?: string | null; doc?: ResolvedDoc | null }): ToolContext;
  /** An error outcome from a failure envelope (broker- or add-in-side). */
  failFrom(r: BridgeResponse): ToolOutcome;
  /** Expand r#/last selectors and drop broker-only params (what call() sends). */
  expandArgs(args: Record<string, unknown>, doc: ResolvedDoc | null): Promise<Record<string, unknown>>;
}

/** Process-wide services shared by every session. */
export interface BrokerServices {
  home: string;
  homeSource: string;
  homeWarning: string | null;
  homeInstalled: boolean;
  build: { version: string; gitSha: string; sdk?: string };
  startedAt: number;
  profile: "core" | "full";
  structured: boolean;
  /** run_csharp is listed (enableCodeExecution was true at start). */
  codeExecutionListed: boolean;
  settings: import("../runtime/settings.js").SettingsStore;
  auth: import("../runtime/auth.js").AuthStore;
  log: import("../runtime/log.js").BrokerLog;
  registry: import("../instances/InstanceRegistry.js").InstanceRegistry;
  channels: import("../instances/InstanceChannel.js").ChannelPool;
  resolver: import("../targeting/TargetResolver.js").TargetResolver;
  results: import("../jobs/ResultCache.js").ResultCache;
  journal: import("../state/InflightJournal.js").InflightJournal;
  tools: import("./registry.js").ToolRegistry;
}
