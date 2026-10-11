// Weak-model checks on every result line a model is told to follow (SPEC §13.3 W1/W2, D4 §8.9):
//   W1 hint following: every call named in a fix:, next:, more: or options: line names an existing tool, op and params,
//      and (unless it carries a confirm or page token) passes the broker's own validation, so following it works.
//   W2 no copying rituals: hint lines never make the model copy add-in internals (ULIDs, GUIDs, long hex) or re-send
//      the whole original call; NOT APPLIED asks the user and then sends only op + a 6-char confirm token; more: lines
//      are only a short page token that the summary repeats; running results point at job_status with a short j#.
// Used by t.call for every call of a run (runtime) and by the offline scenarios W1/W2.

import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { byName, fullProperties } from "@revit-mcp-next/contracts/catalog";
import { REPO } from "./mcp.mjs";

let brokerFns = null;
/** The broker's own normalize/validate (built broker/dist), loaded lazily. Null when the broker is not built. */
export async function brokerValidation() {
  if (brokerFns !== null) return brokerFns || null;
  try {
    const norm = await import(pathToFileURL(join(REPO, "broker", "dist", "src", "framework", "normalize.js")).href);
    const val = await import(pathToFileURL(join(REPO, "broker", "dist", "src", "framework", "validate.js")).href);
    brokerFns = { normalizeArgs: norm.normalizeArgs, resolveOp: val.resolveOp, validateOp: val.validateOp, parseWithUnit: norm.parseWithUnit };
  } catch {
    brokerFns = false;
  }
  return brokerFns || null;
}

/** Start index of the JSON object at `open` → index after its closing brace (strings and escapes respected), or -1. */
function balancedEnd(text, open) {
  let depth = 0;
  let inString = false;
  for (let i = open; i < text.length; i++) {
    const ch = text[i];
    if (inString) {
      if (ch === "\\") i++;
      else if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') inString = true;
    else if (ch === "{" || ch === "[") depth++;
    else if (ch === "}" || ch === "]") {
      depth--;
      if (depth === 0) return i + 1;
    }
  }
  return -1;
}

/**
 * Every `<tool> {json}` call in a hint line. `atStart` marks the call the line leads with (after "ask the user, then ").
 * @returns {{tool:string, args:any, raw:string, atStart:boolean, parseError?:string}[]}
 */
export function extractCalls(line) {
  const text = String(line ?? "");
  const lead = /^(?:ask the user, then |then )?/.exec(text)[0].length;
  const out = [];
  const re = /(^|[\s(,;:])([a-z][a-z0-9_]{1,40}) \{/g;
  let m;
  while ((m = re.exec(text))) {
    const start = m.index + m[1].length;
    const open = start + m[2].length + 1;
    const end = balancedEnd(text, open);
    if (end < 0) {
      if (byName.has(m[2])) out.push({ tool: m[2], args: null, raw: text.slice(start), atStart: start === lead, parseError: "unbalanced JSON" });
      continue;
    }
    const raw = text.slice(open, end);
    let args = null;
    let parseError;
    try {
      args = JSON.parse(raw);
    } catch (error) {
      parseError = error.message;
    }
    out.push({ tool: m[2], args, raw: text.slice(start, end), atStart: start === lead, parseError });
    re.lastIndex = end;
  }
  return out;
}

const CALL_LINE_KINDS = ["fix", "next", "more"];

/**
 * W1 problems of one call: unknown tool/op/params, unparsable JSON, and (when the broker is built) failing validation.
 * @returns {Promise<string[]>}
 */
export async function callProblems(call) {
  const problems = [];
  const spec = byName.get(call.tool);
  if (!spec) {
    if (call.atStart) problems.push(`names unknown tool ${call.tool}`);
    return problems;
  }
  if (call.parseError) return [`${call.tool} args are not valid JSON (${call.parseError}): ${call.raw.slice(0, 160)}`];
  const args = call.args;
  if (!args || typeof args !== "object" || Array.isArray(args)) return [`${call.tool} args are not an object`];
  const props = fullProperties(spec);
  if (spec.discriminator && args[spec.discriminator] !== undefined) {
    const op = args[spec.discriminator];
    if (typeof op !== "string" || !(op in spec.ops)) problems.push(`${call.tool} names unknown ${spec.discriminator} ${JSON.stringify(op)}`);
  }
  for (const key of Object.keys(args)) if (!(key in props)) problems.push(`${call.tool} passes unknown param ${key}`);
  if (problems.length > 0) return problems;
  // Following the hint must work: the broker validates it (confirm/page calls replay stored args and skip validation).
  if ("confirm" in args || "page" in args) return problems;
  const v = await brokerValidation();
  if (!v) return problems;
  const norm = v.normalizeArgs(spec, structuredClone(args));
  if (norm.error) return [`${call.tool} ${JSON.stringify(args).slice(0, 160)} fails normalization: ${norm.error.message}`];
  const op = v.resolveOp(spec, norm.args);
  if (op.error) return [`${call.tool} ${JSON.stringify(args).slice(0, 160)}: ${op.error.code} ${op.error.message}`];
  const val = v.validateOp(spec, op.op, norm.args);
  if (val.error) problems.push(`${call.tool} ${JSON.stringify(args).slice(0, 160)} fails validation: ${val.error.message}`);
  return problems;
}

// ------------------------------------------------------------------------------------------------ W2

const ULID = /\b[0-9A-HJKMNP-TV-Z]{26}\b/;
const GUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i;
const LONG_HEX = /\b[0-9a-f]{24,}\b/i;
const CONFIRM_TOKEN = /^[ABCDEFGHJKMNPQRSTVWXYZ23456789]{6}$/i;
const MAX_HINT_CHARS = 1_600;

function ritualProblemsOfLine(kind, line) {
  const problems = [];
  if (line.length > MAX_HINT_CHARS) problems.push(`${kind}: line is ${line.length} chars (> ${MAX_HINT_CHARS}); a weak model cannot copy it reliably`);
  // "ask the user: report requestId ..." is for the user, not a call the model has to assemble.
  if (line.startsWith("ask the user:")) return problems;
  if (ULID.test(line)) problems.push(`${kind}: line carries an add-in request id (ULID) the model would have to copy`);
  if (GUID.test(line)) problems.push(`${kind}: line carries a GUID/UniqueId the model would have to copy`);
  if (LONG_HEX.test(line)) problems.push(`${kind}: line carries a long hex token the model would have to copy`);
  return problems;
}

/** W2 problems of a parsed result (lib/call.mjs parseText shape plus tool). */
export function ritualProblems(r) {
  const problems = [];
  for (const kind of [...CALL_LINE_KINDS, "options"]) if (r[kind]) problems.push(...ritualProblemsOfLine(kind, r[kind]));
  if (ULID.test(r.summary ?? "")) problems.push("summary carries an add-in request id (ULID)");
  if (r.status === "not_applied") {
    const next = r.next ?? "";
    if (!next.startsWith("ask the user, then ")) problems.push(`NOT APPLIED next: line must start with "ask the user, then " (got ${JSON.stringify(next.slice(0, 80))})`);
    const call = extractCalls(next).find((c) => byName.has(c.tool));
    if (!call || !call.args) problems.push("NOT APPLIED next: line has no confirm call");
    else {
      const spec = byName.get(call.tool);
      const token = call.args.confirm;
      if (typeof token !== "string" || !CONFIRM_TOKEN.test(token)) problems.push(`NOT APPLIED confirm token ${JSON.stringify(token)} is not a 6-char token`);
      const allowed = new Set(["confirm", "doc", spec?.discriminator].filter(Boolean));
      const props = spec ? fullProperties(spec) : {};
      // A fixed enum value such as run_csharp {"mode":"commit"} is not a copy of the original call.
      const fixedEnum = (k) => Array.isArray(props[k]?.enum) && props[k].enum.includes(call.args[k]);
      const extra = Object.keys(call.args).filter((k) => !allowed.has(k) && !fixedEnum(k));
      if (extra.length) problems.push(`NOT APPLIED confirm call re-sends ${extra.join(", ")}; it must be only op + confirm`);
    }
  }
  if (r.more) {
    const call = extractCalls(r.more)[0];
    const keys = call?.args ? Object.keys(call.args) : [];
    if (!call || keys.length !== 1 || keys[0] !== "page" || !/^p\d+$/.test(String(call.args.page))) problems.push(`more: line is not <tool> {"page":"p#"}: ${r.more.slice(0, 120)}`);
    else if (!new RegExp(`\\bmore ${call.args.page}\\b`).test(r.summary ?? "")) problems.push(`paged summary does not repeat the page token ${call.args.page} (Codex middle-truncates long outputs)`);
  }
  if (r.status === "running") {
    const call = extractCalls(r.next ?? "").find((c) => c.tool === "job_status");
    if (!call || !/^[jw]\d+$/.test(String(call.args?.id ?? ""))) problems.push(`running result does not point at job_status with a short j#/w#: ${(r.next ?? "").slice(0, 120)}`);
  }
  return problems;
}

/**
 * All W1/W2 problems of a result. Async because W1 validates through the broker.
 * @returns {Promise<{rule:"W1"|"W2", problem:string}[]>}
 */
export async function hintProblems(r) {
  const out = [];
  for (const kind of CALL_LINE_KINDS) {
    const line = r[kind];
    if (!line) continue;
    for (const call of extractCalls(line)) for (const p of await callProblems(call)) out.push({ rule: "W1", problem: `${kind}: ${p}` });
  }
  for (const p of ritualProblems(r)) out.push({ rule: "W2", problem: p });
  return out;
}
