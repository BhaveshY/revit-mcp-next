// The t.call contract (SPEC §13.1, D4 §8.2): 55 s client timeout; parses the text contract (status, doc, notice/warn,
// JSON line, more/next/fix/options); decodes images; fails on structuredContent unless the session is structured;
// marks coverage for (tool, op) on success or on a declared expected error; guards writes to the run folder.

import { byName } from "@revit-mcp-next/contracts/catalog";
import { AssertionFailure } from "./assert.mjs";

export const CLIENT_TIMEOUT_MS = 55_000;

const STATUS_LINE = /^(ok|NOT APPLIED - needs the user's OK|running|ERROR ([A-Z][A-Z0-9_]*)): ?(.*)$/;
const DOC_SUFFIX = / - doc: (.+) \(Revit (\d{4})\)$/;

/** Parse one result text block into the contract fields. */
export function parseText(text) {
  const lines = String(text ?? "").split(/\r?\n/);
  const out = { status: null, code: null, summary: "", doc: null, notices: [], warnings: [], json: undefined, jsonText: null, more: null, next: null, fix: null, options: null, lines };
  const first = STATUS_LINE.exec(lines[0] ?? "");
  if (first) {
    out.status = first[1].startsWith("ERROR") ? "error" : first[1] === "ok" ? "ok" : first[1] === "running" ? "running" : "not_applied";
    out.code = first[2] ?? null;
    let summary = first[3] ?? "";
    const doc = DOC_SUFFIX.exec(summary);
    if (doc) {
      out.doc = { title: doc[1], year: Number(doc[2]) };
      summary = summary.slice(0, doc.index);
    }
    out.summary = summary;
  }
  for (const line of lines.slice(1)) {
    if (line.startsWith("notice: ")) out.notices.push(line.slice(8));
    else if (line.startsWith("warn: ")) out.warnings.push(line.slice(6));
    else if (line.startsWith("more: ")) out.more = line.slice(6);
    else if (line.startsWith("next: ")) out.next = line.slice(6);
    else if (line.startsWith("fix: ")) out.fix = line.slice(5);
    else if (line.startsWith("options: ")) out.options = line.slice(9);
    else if ((line.startsWith("{") || line.startsWith("[")) && out.jsonText === null) {
      out.jsonText = line;
      try {
        out.json = JSON.parse(line);
      } catch {
        out.json = undefined;
      }
    }
  }
  return out;
}

/** "find_elements {"page":"p4"}" → {tool, args}. */
export function parseCallLine(text) {
  const m = /^([a-z_]+) (\{.*\})/.exec(String(text ?? ""));
  if (!m) return null;
  try {
    return { tool: m[1], args: JSON.parse(m[2]) };
  } catch {
    return null;
  }
}

export class Coverage {
  constructor() {
    this.marks = new Map();
  }
  mark(tool, op, year) {
    const key = `${tool}${op ? "." + op : ""}`;
    const entry = this.marks.get(key) ?? { tool, op: op ?? null, years: new Set() };
    if (year) entry.years.add(year);
    this.marks.set(key, entry);
  }
  toJSON() {
    return [...this.marks.values()].map((e) => ({ tool: e.tool, op: e.op, years: [...e.years] }));
  }
}

/**
 * Make the t.call function for one MCP session.
 * @param {{ session: any, structured?: boolean, coverage?: Coverage, year?: number, record?: (entry: any) => void,
 *           writeGuard?: (tool: string, args: any, op: string|null) => void }} o
 */
export function makeCaller(o) {
  const call = async (tool, args = {}, options = {}) => {
    const spec = byName.get(tool);
    const op = spec?.discriminator ? args?.[spec.discriminator] ?? spec.defaultOp ?? null : null;
    if (o.writeGuard && spec && !spec.annotations.readOnlyHint) o.writeGuard(tool, args, op);
    const started = Date.now();
    let result;
    try {
      result = await o.session.client.callTool({ name: tool, arguments: args }, undefined, { timeout: options.timeoutMs ?? CLIENT_TIMEOUT_MS, onprogress: options.onprogress, resetTimeoutOnProgress: false });
    } catch (error) {
      throw new AssertionFailure(`${tool} failed at the protocol level (no tool result): ${error.message}`, { tool, args });
    }
    const ms = Date.now() - started;
    const textBlocks = (result.content ?? []).filter((c) => c.type === "text").map((c) => c.text);
    const images = (result.content ?? []).filter((c) => c.type === "image").map((c) => ({ mimeType: c.mimeType, bytes: Buffer.from(c.data, "base64"), base64Length: c.data.length, meta: c._meta }));
    const parsed = parseText(textBlocks[0] ?? "");
    const r = { ...parsed, tool, op, args, ms, isError: result.isError === true, text: textBlocks[0] ?? "", blocks: textBlocks.slice(1), images, raw: result, bytes: Buffer.byteLength(textBlocks.join("\n"), "utf8") };
    o.record?.({ tool, op, ms, bytes: r.bytes, isError: r.isError, code: r.code, status: r.status });
    if (result.structuredContent !== undefined && !o.structured) throw new AssertionFailure(`${tool} returned structuredContent (Codex would drop the content blocks)`, { tool });
    if (r.status === null) throw new AssertionFailure(`${tool} result does not follow the text contract: ${JSON.stringify(r.text.slice(0, 200))}`, { tool });
    if (r.isError !== (r.status === "error")) throw new AssertionFailure(`${tool}: isError (${r.isError}) does not match status ${r.status}`, { text: r.text.slice(0, 300) });
    if (r.status === "error" && !r.fix) throw new AssertionFailure(`${tool}: ERROR ${r.code} has no fix: line`, { text: r.text.slice(0, 300) });
    const expected = options.expectError;
    if (expected) {
      const codes = Array.isArray(expected) ? expected : [expected];
      if (r.status !== "error" || (codes[0] !== true && !codes.includes(r.code)))
        throw new AssertionFailure(`${tool}: expected ERROR ${codes.join("|")}, got ${r.status}${r.code ? " " + r.code : ""}: ${r.summary.slice(0, 200)}`, { text: r.text.slice(0, 400) });
      o.coverage?.mark(tool, op, o.year);
    } else if (options.allowError !== true && r.status === "error") {
      throw new AssertionFailure(`${tool}: ERROR ${r.code}: ${r.summary.slice(0, 300)}`, { fix: r.fix, text: r.text.slice(0, 600) });
    } else if (r.status !== "error") {
      o.coverage?.mark(tool, op, o.year);
    }
    return r;
  };
  call.expectError = (tool, args, codes, options = {}) => call(tool, args, { ...options, expectError: codes });
  return call;
}
