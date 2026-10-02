// Wire protocol v3 between the broker and the Revit add-in (SPEC §4.6).
// Mirrors addin/RevitMcpNext.Contracts/BridgeContracts.cs. JSON names are camelCase on both sides.
// Frozen in wave 2: changes go through the lead and must land in the C# contract in the same commit.

import type { Blast, Kind } from "./catalog/types.js";

// ---------------------------------------------------------------------------------------------
// 4.6.1 Transport
// ---------------------------------------------------------------------------------------------

/** Protocol version sent in every request (`v`). The add-in accepts the [min,max] range it advertises in `hello`. */
export const BRIDGE_PROTOCOL_VERSION = "2026-10-01" as const;
export type BridgeProtocolVersion = typeof BRIDGE_PROTOCOL_VERSION;
/** The range this broker speaks. A broker older than the add-in's min gets ADDIN_NEWER_THAN_BROKER; newer than max gets ADDIN_OUTDATED. */
export const BROKER_PROTOCOL_RANGE = { min: BRIDGE_PROTOCOL_VERSION, max: BRIDGE_PROTOCOL_VERSION } as const;

/** Framing: 4-byte big-endian length + UTF-8 JSON. The cap is measured in bytes. */
export const MAX_FRAME_BYTES = 4_194_304;
export const FRAME_HEADER_BYTES = 4;
/** One request per connection. The add-in hosts this many listeners. */
export const PRIMARY_PIPE_INSTANCES = 16;
export const CONTROL_PIPE_INSTANCES = 4;
/** The add-in drops a connection that has not delivered its request frame within this time. */
export const HANDSHAKE_TIMEOUT_MS = 5_000;

/** instanceId = r<year>-<pid>-<6 hex>, e.g. r2024-19356-a1b2c3. */
export const INSTANCE_ID_PATTERN = /^r(\d{4})-(\d+)-([0-9a-f]{6})$/;
export interface ParsedInstanceId {
  year: number;
  pid: number;
  suffix: string;
}
export function parseInstanceId(instanceId: string): ParsedInstanceId | null {
  const match = INSTANCE_ID_PATTERN.exec(instanceId);
  if (!match) return null;
  return { year: Number(match[1]), pid: Number(match[2]), suffix: match[3]! };
}
export function primaryPipeName(instanceId: string): string {
  return `revit-mcp-next-${instanceId}`;
}
export function controlPipeName(instanceId: string): string {
  return `revit-mcp-next-${instanceId}-control`;
}
/** Windows named-pipe path for a pipe name (`\\.\pipe\<name>`). Full paths pass through. */
export function pipePath(pipeName: string): string {
  return pipeName.startsWith("\\\\") ? pipeName : `\\\\.\\pipe\\${pipeName}`;
}
/** clientKey = clientInfo.name + '#' + broker pid (diagnostics and per-client fairness). */
export function clientKeyOf(clientName: string, brokerPid: number): string {
  return `${clientName || "unknown-client"}#${brokerPid}`;
}

// ---------------------------------------------------------------------------------------------
// 4.6.2 Request
// ---------------------------------------------------------------------------------------------

/** Registry kind of the op, or "control" for control-pipe ops. The add-in compares it with its embedded catalog. */
export type RequestKind = Kind;
/** write/code only. */
export type RequestMode = "apply" | "preview";

export interface AuthBlock {
  token: string;
  /** First 8 hex chars of sha256(token); diagnostics only. */
  fp: string;
  /** The auth file the broker read; diagnostics only. */
  file: string;
}

export interface DocRef {
  rid: number;
  key: string;
}

/** Sent only when applying a plan the user confirmed (§6.4). */
export interface ConfirmedPlan {
  stamp: number;
  deleteSet?: number[];
}

export interface BridgeRequest {
  v: string;
  /** ULID (26 chars), globally unique; ledger key. */
  requestId: string;
  clientKey: string;
  /** Omitted for hello. */
  auth?: AuthBlock;
  /** Registry key (e.g. "create_elements.wall"), or the control op name on the control pipe. */
  op: string;
  kind: RequestKind;
  /** write/lifecycle/code only. */
  mode?: RequestMode;
  /** Time left for this request, measured from receipt by the add-in. */
  timeoutMs: number;
  /** null for scope none. */
  doc: DocRef | null;
  /** Write, lifecycle and code-commit requests: the broker's session-unique short id (w17). */
  writeTag?: string;
  confirmed?: ConfirmedPlan;
  /** job_start variant (§4.6.4): the add-in enqueues the op as a job and answers {job:{jobId,state:"queued"}} at once. */
  asJob?: boolean;
  /** Reads: also return page.ids = every matching id (≤100,000) so the broker can keep a handle (§5.9 find_elements). */
  wantIds?: boolean;
  /**
   * Normalized args (§5.3); r#/last are already expanded to id arrays (`from` may still be "selection").
   * Plain length numbers are in args.units (default mm); strings with explicit units were converted by the broker.
   * Broker-only params (doc, preview, confirm, page) are never sent.
   */
  args: Record<string, unknown>;
}

// ---------------------------------------------------------------------------------------------
// 4.6.3 Response
// ---------------------------------------------------------------------------------------------

export interface BridgeWarning {
  code: string;
  text: string;
  ids?: number[];
  /** Occurrence count when the add-in grouped identical warnings. */
  n?: number;
}

export interface BridgeNotice {
  code: string;
  text: string;
}

export interface BridgeChanges {
  created: number[];
  modified: number[];
  deleted: number[];
  createdTotal: number;
  modifiedTotal: number;
  deletedTotal: number;
}

export interface BlastSummary {
  deleteTotal: number;
  byCategory: Record<string, number>;
  sample: number[];
  modifyTotal: number;
  createTotal: number;
}

/** Returned instead of committing when a blast rule triggers (§6.1 step 3) and in preview mode. */
export interface NeedsConfirm {
  rule: Blast | null;
  plan: string;
  blast: BlastSummary;
  /** Max per-element change stamp of the affected elements at plan time. */
  stamp: number;
  deleteSet: number[];
}

export type DocKind = "project" | "family";

export interface ResponseDoc {
  rid: number;
  key: string;
  title: string;
  year: number;
  kind: DocKind;
  generation: number;
  modified: boolean;
}

export interface PageInfo {
  total: number;
  offset: number;
  count: number;
  /** Only when the request carried wantIds. */
  ids?: number[];
}

export interface PartialInfo {
  reason: "deadline" | "limit";
  resume: Record<string, unknown>;
}

export interface FileInfo {
  path: string;
  mime: string;
  w?: number;
  h?: number;
  bytes: number;
  meta?: Record<string, unknown>;
}

export type JobState = "queued" | "running" | "succeeded" | "failed" | "cancelled" | "interrupted";
export const TERMINAL_JOB_STATES: readonly JobState[] = ["succeeded", "failed", "cancelled", "interrupted"];
export function isTerminalJobState(state: string | undefined | null): boolean {
  return state !== undefined && state !== null && (TERMINAL_JOB_STATES as readonly string[]).includes(state);
}

export interface JobInfo {
  jobId: string;
  state: JobState;
  stage?: string;
  done?: number;
  total?: number;
}

export interface ResponseMetrics {
  queueWaitMs?: number;
  raiseToExecMs?: number;
  execMs?: number;
  via?: "externalEvent" | "idling";
  cacheHit?: boolean;
}

export interface BridgeResponse {
  v: string;
  requestId: string;
  ok: boolean;
  /** When ok=false. */
  code: string | null;
  /** Concrete names and values; never tool names. */
  message: string | null;
  details: Record<string, unknown> | null;
  summary?: string | null;
  /** Op payload in the §5.5 output conventions. */
  data?: unknown;
  changes?: BridgeChanges | null;
  needsConfirm?: NeedsConfirm | null;
  /** Named outputs for change_set $refs: type, view, sheet, level, schedule, family, room. */
  outputs?: Record<string, unknown> | null;
  warnings?: BridgeWarning[] | null;
  notices?: BridgeNotice[] | null;
  doc?: ResponseDoc | null;
  page?: PageInfo | null;
  partial?: PartialInfo | null;
  file?: FileInfo | null;
  job?: JobInfo | null;
  metrics?: ResponseMetrics | null;
}

// ---------------------------------------------------------------------------------------------
// 4.6.4 Control ops (pipe threads only; all need auth except hello)
// ---------------------------------------------------------------------------------------------

export const CONTROL_OPS = [
  "hello",
  "snapshot",
  "health",
  "cancel_request",
  "get_request_result",
  "recent_writes",
  "job_status",
  "job_cancel",
  "dialogs",
  "press",
] as const;
export type ControlOp = (typeof CONTROL_OPS)[number];

export interface ProtocolRange {
  min: string;
  max: string;
}

export type AuthState = "ok" | "unwritable" | "missing" | string;
export type InstanceState = "starting" | "ready" | "stopping";

export interface HelloData {
  instanceId: string;
  pid: number;
  year: number;
  build: string;
  language: string;
  addinVersion: string;
  gitSha: string;
  payloadId: string;
  catalogHash: string;
  protocol: ProtocolRange;
  state: InstanceState;
  home: string;
  authFile: string;
  authFp: string;
  authState: AuthState;
  capabilities: { keys: string[]; mismatches: string[] };
  testOps: boolean;
}

export interface SnapshotPayload {
  sinceSeq?: number;
}
export type SnapshotData = DocSnapshot | { unchanged: true; seq?: number };

export interface ExecutingItem {
  requestId: string;
  op: string;
  clientKey: string;
  writeTag?: string;
  jobId?: string;
  startedAtUtc: string;
  elapsedMs: number;
}

export interface PopupInfo {
  hwnd?: number;
  title: string;
  class: string;
  isProgress: boolean;
}

export interface NativeActivity {
  kind: "sync" | "opening" | "saving" | "exporting" | "printing" | "reloading" | string;
  rid?: number | null;
  sinceUtc: string;
  progress?: { caption?: string; pos?: number; max?: number } | null;
}

export interface LatencyStats {
  p50: number;
  p90: number;
  p99: number;
  max: number;
  n: number;
  over1s: number;
  raiseResults?: Record<string, number>;
  wmNullPosts?: number;
  execViaIdling?: number;
  recreates?: number;
}

export interface LastDialog {
  dialogId?: string;
  title?: string;
  text?: string;
  answer?: string;
  atUtc?: string;
  ours?: boolean;
}

export interface HealthData {
  queue: {
    pending: number;
    executing?: ExecutingItem | null;
    byClient?: Record<string, number>;
  };
  pump?: LatencyStats;
  ui: {
    mainWindowEnabled: boolean;
    hung: boolean;
    minimized: boolean;
    foreground: boolean;
    popup?: PopupInfo | null;
  };
  native?: NativeActivity | null;
  lastIdlingAtUtc?: string | null;
  lastDialog?: LastDialog | null;
  listeners?: { primaryWaiting: number; primaryActive: number; controlWaiting: number };
  admissionRejections?: number;
  jobs?: Array<{ jobId: string; op: string; state: JobState; clientKey?: string; stage?: string; done?: number; total?: number }>;
}

export interface CancelRequestPayload {
  requestId: string;
}
export interface CancelRequestData {
  cancelled: "queued" | "cooperative" | "not_found" | "running_write";
}

export interface GetRequestResultPayload {
  requestId: string;
}

/** Ledger states. Anything other than accepted/queued/running is terminal. */
export type LedgerState = "accepted" | "queued" | "running" | "committed" | "rolledBack" | "failed" | "cancelled" | string;
export function isTerminalLedgerState(state: string | undefined | null): boolean {
  return state !== undefined && state !== null && !["accepted", "queued", "running", "executing"].includes(state);
}

/** Terminal write outcome recorded by the add-in (D2 §13.1); persisted to <home>\ledger\<instanceId>.jsonl. */
export interface LedgerEntry {
  requestId: string;
  writeTag?: string;
  clientKey?: string;
  key?: string;
  docKey?: string;
  rid?: number;
  state: LedgerState;
  acceptedAtUtc?: string;
  completedAtUtc?: string;
  txnNames?: string[];
  counts?: { created: number; modified: number; deleted: number };
  revitWarnings?: number;
  saved?: boolean;
  /** The recorded response (≤64 KB; trimmed responses carry trimmed:true). */
  response?: BridgeResponse | null;
  trimmed?: boolean;
}

export interface RecentWritesPayload {
  docKey: string;
  /** ≤20 */
  limit?: number;
}
export interface RecentWrite {
  requestId: string;
  writeTag?: string;
  clientKey?: string;
  key: string;
  state: LedgerState;
  atUtc: string;
  counts?: { created: number; modified: number; deleted: number };
  saved?: boolean;
}

export interface JobStatusPayload {
  jobId: string;
}
export interface JobStatusData {
  state: JobState;
  stage?: string;
  done?: number;
  total?: number;
  elapsedMs?: number;
  /** Terminal jobs: the job's final response envelope. */
  result?: BridgeResponse | null;
}

export interface JobCancelPayload {
  jobId: string;
}
export interface JobCancelData {
  cancelled: boolean | string;
}

export interface DialogButton {
  name: string;
  id: string | number;
}
export interface DialogInfo {
  dialog: string;
  hwnd: number;
  title: string;
  text: string;
  buttons: DialogButton[];
  dialogId?: string;
  since: string;
  ours: boolean;
}
export type DialogsData = DialogInfo[];

export interface PressPayload {
  dialog?: string;
  button: string;
  confirmed?: boolean;
}
export interface PressData {
  pressed: boolean;
  via: "uia" | "tdm" | "wm_command" | "bm_click";
}

export interface ControlPayloads {
  hello: Record<string, never>;
  snapshot: SnapshotPayload;
  health: Record<string, never>;
  cancel_request: CancelRequestPayload;
  get_request_result: GetRequestResultPayload;
  recent_writes: RecentWritesPayload;
  job_status: JobStatusPayload;
  job_cancel: JobCancelPayload;
  dialogs: Record<string, never>;
  press: PressPayload;
}

export interface ControlData {
  hello: HelloData;
  snapshot: SnapshotData;
  health: HealthData;
  cancel_request: CancelRequestData;
  get_request_result: LedgerEntry;
  recent_writes: RecentWrite[];
  job_status: JobStatusData;
  job_cancel: JobCancelData;
  dialogs: DialogsData;
  press: PressData;
}

// ---------------------------------------------------------------------------------------------
// 4.6.5 DocSnapshot (control `snapshot`; also the registration file body)
// ---------------------------------------------------------------------------------------------

export const DOC_SNAPSHOT_SCHEMA_VERSION = 3;
/** Registration files are capped at 64 KB and 50 docs. */
export const MAX_REGISTRATION_BYTES = 65_536;
export const MAX_SNAPSHOT_DOCS = 50;
export const MAX_SNAPSHOT_LEVELS = 30;

export interface SnapshotView {
  id: number;
  name: string;
  type: string;
  scale?: number;
}

/** [id, name, elevation mm] */
export type SnapshotLevel = [number, string, number];

export interface SnapshotDoc {
  rid: number;
  key: string;
  aliases: string[];
  title: string;
  kind: DocKind;
  path: string;
  central?: string | null;
  workshared: boolean;
  cloud: boolean;
  readOnly: boolean;
  modified: boolean;
  generation: number;
  closing: boolean;
  activeView?: SnapshotView | null;
  lastActivatedAtUtc?: string | null;
  levels?: SnapshotLevel[];
  levelsMore?: number;
  selection?: { count: number; atUtc?: string | null } | null;
  /** Family docs opened by EditFamily from a project. */
  familySourceRid?: number | null;
}

export interface SnapshotUi {
  foreground: boolean;
  lastForegroundAtUtc?: string | null;
  lastViewActivatedAtUtc?: string | null;
  minimized: boolean;
  mainWindowEnabled: boolean;
  hung: boolean;
  popup?: { title: string; class: string; isProgress: boolean } | null;
}

export interface SnapshotExecuting {
  op: string;
  sinceUtc: string;
  clientKey?: string;
  writeTag?: string;
  jobId?: string;
}

export interface DocSnapshot {
  schemaVersion: number;
  seq: number;
  atUtc: string;
  /** Registration files only. */
  writtenAtUtc?: string;
  instanceId: string;
  pid: number;
  year: number;
  build: string;
  language: string;
  addinVersion: string;
  gitSha: string;
  payloadId: string;
  catalogHash: string;
  protocol: ProtocolRange;
  pipe: string;
  controlPipe: string;
  home: string;
  state: InstanceState;
  ui: SnapshotUi;
  lastIdlingAtUtc?: string | null;
  native?: NativeActivity | null;
  executing?: SnapshotExecuting | null;
  activeRid?: number | null;
  lastActiveProjectRid?: number | null;
  codeExecution?: { enabled: boolean; consented: boolean };
  docs: SnapshotDoc[];
}

// ---------------------------------------------------------------------------------------------
// Document keys (D2 §9.2, adopted by SPEC §4.6.5)
// ---------------------------------------------------------------------------------------------

/** `<year>|cloud|<projGuid>|<modelGuid>`, `<year>|central|<norm central>`, `<year>|file|<norm path>`, `<year>|detached|<title>|<rid>`, `<year>|unsaved|<title>|<rid>`. */
export type DocKeyKind = "cloud" | "central" | "file" | "detached" | "unsaved" | "link";
export interface ParsedDocKey {
  year: number;
  kind: DocKeyKind | string;
  rest: string;
}
export function parseDocKey(key: string): ParsedDocKey | null {
  const first = key.indexOf("|");
  if (first < 0) return null;
  const second = key.indexOf("|", first + 1);
  if (second < 0) return null;
  const year = Number(key.slice(0, first));
  if (!Number.isInteger(year)) return null;
  return { year, kind: key.slice(first + 1, second), rest: key.slice(second + 1) };
}
/** Path normalization used inside keys: full path, '/'→'\', trailing '\' trimmed, lower case. */
export function normalizeKeyPath(path: string): string {
  let p = path.replace(/\//g, "\\");
  while (p.length > 3 && p.endsWith("\\")) p = p.slice(0, -1);
  return p.toLowerCase();
}

/** A doc is "restart-stable" when its key can be found again after Revit restarts. */
export function isRestartStableKey(key: string): boolean {
  const parsed = parseDocKey(key);
  return parsed !== null && (parsed.kind === "file" || parsed.kind === "central" || parsed.kind === "cloud");
}

// ---------------------------------------------------------------------------------------------
// Helpers shared by both sides
// ---------------------------------------------------------------------------------------------

/** Protocol range check: is `version` inside [range.min, range.max] (ISO dates compare lexically)? */
export function protocolInRange(version: string, range: ProtocolRange | null | undefined): boolean {
  if (!range || typeof range.min !== "string" || typeof range.max !== "string") return false;
  return version >= range.min && version <= range.max;
}

/** Compatibility of this broker with an add-in range: ok, ADDIN_OUTDATED (add-in older) or ADDIN_NEWER_THAN_BROKER. */
export function protocolCompat(range: ProtocolRange | null | undefined): "ok" | "ADDIN_OUTDATED" | "ADDIN_NEWER_THAN_BROKER" {
  if (protocolInRange(BRIDGE_PROTOCOL_VERSION, range)) return "ok";
  if (range && typeof range.max === "string" && BRIDGE_PROTOCOL_VERSION > range.max) return "ADDIN_OUTDATED";
  if (range && typeof range.min === "string" && BRIDGE_PROTOCOL_VERSION < range.min) return "ADDIN_NEWER_THAN_BROKER";
  return "ADDIN_OUTDATED";
}
