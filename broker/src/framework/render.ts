// ToolOutcome → CallToolResult (SPEC §5.5, §5.6). The text is the contract:
//   <STATUS>: <summary> - doc: <title> (Revit <year>)
//   fix: ... / options: 1) ... 2) ...        (errors)
//   notice: ...                               (0..n)
//   warn: <CODE> ...                          (0..10, then "warn: +N more")
//   <one line minified JSON>                  ({"details":...} for errors)
//   more: <tool> {"page":"p7"}
//   next: <exact call>
// STATUS is ok | NOT APPLIED - needs the user's OK | running | ERROR <CODE>. isError only for ERROR.
// Caps: 12,000 B of text by default, 40,000 B with detail:"full". No structuredContent unless
// REVIT_MCP_NEXT_STRUCTURED=1, and never for image results. Frozen in wave 2 (W3-E2E may lower the full cap, §12.3).

import type { ImageBlock, ToolOutcome } from "./types.js";
import { stripExt } from "../targeting/docParam.js";

export const TEXT_CAP_BYTES = 12_000;
export const TEXT_CAP_FULL_BYTES = 40_000;
export const MAX_WARN_LINES = 10;

export interface RenderOptions {
  structured: boolean;
  detailFull: boolean;
}

export interface RenderedResult {
  [key: string]: unknown;
  content: Array<{ type: "text"; text: string } | ImageBlock>;
  isError?: boolean;
  structuredContent?: Record<string, unknown>;
}

export const bytesOf = (text: string) => Buffer.byteLength(text, "utf8");

export function statusWord(o: ToolOutcome): string {
  switch (o.status) {
    case "ok":
      return "ok";
    case "not_applied":
      return "NOT APPLIED - needs the user's OK";
    case "running":
      return "running";
    case "error":
      return `ERROR ${o.code ?? "INTERNAL_ERROR"}`;
  }
}

export function docSuffix(o: ToolOutcome): string {
  if (!o.doc || !o.doc.title) return "";
  return ` - doc: ${stripExt(o.doc.title)} (Revit ${o.doc.year})`;
}

function oneLine(text: string): string {
  return text.replace(/\s*\r?\n\s*/g, " ").trim();
}

/** Remove undefined values (JSON.stringify drops them anyway) and keep everything else as the add-in sent it. */
function jsonLine(value: unknown): string {
  return JSON.stringify(value, (_k, v) => (typeof v === "number" && !Number.isFinite(v) ? null : v));
}

function headLines(o: ToolOutcome): string[] {
  const lines: string[] = [];
  const summary = oneLine(o.summary || (o.status === "ok" ? "done" : o.status === "error" ? "failed" : ""));
  lines.push(`${statusWord(o)}: ${summary}${docSuffix(o)}`);
  if (o.status === "error") {
    if (o.fix) lines.push(`fix: ${oneLine(o.fix)}`);
    if (o.options && o.options.length) lines.push(`options: ${o.options.map(oneLine).join(" ")}`);
  }
  for (const n of o.notices ?? []) lines.push(`notice: ${oneLine(n)}`);
  const warnings = o.warnings ?? [];
  for (const w of warnings.slice(0, MAX_WARN_LINES)) lines.push(`warn: ${oneLine(w)}`);
  if (warnings.length > MAX_WARN_LINES) lines.push(`warn: +${warnings.length - MAX_WARN_LINES} more (check_model warnings or get_changes)`);
  return lines;
}

function tailLines(o: ToolOutcome): string[] {
  const lines: string[] = [];
  if (o.more) lines.push(`more: ${oneLine(o.more)}`);
  if (o.next) lines.push(`next: ${oneLine(o.next)}`);
  return lines;
}

function dataOf(o: ToolOutcome): unknown {
  if (o.status === "error") return o.details !== undefined && o.details !== null && !(typeof o.details === "object" && Object.keys(o.details as object).length === 0) ? { details: o.details } : undefined;
  return o.data;
}

/** Shrink the largest arrays of `data` until the JSON fits `budget` bytes. Returns the cut description or null. */
function shrinkToFit(data: unknown, budget: number): { data: unknown; cut: string | null } {
  let current = structuredClone(data);
  let text = jsonLine(current);
  if (bytesOf(text) <= budget) return { data: current, cut: null };
  let cut: string | null = null;
  const originals = new Map<string, number>();
  for (let round = 0; round < 24 && bytesOf(text) > budget; round++) {
    const target = largestArray(current, "$", 0);
    if (!target || target.array.length <= 1) break;
    const keep = Math.max(1, Math.floor(target.array.length / 2));
    if (!originals.has(target.path)) originals.set(target.path, target.array.length);
    target.array.splice(keep);
    cut = `${target.path}: kept ${keep} of ${originals.get(target.path)}`;
    text = jsonLine(current);
  }
  if (bytesOf(text) > budget) {
    current = { cut: `result too large (${bytesOf(jsonLine(data))} B); narrow it with limit, fields or page` };
    cut = "whole result";
  }
  return { data: current, cut };
}

function largestArray(node: unknown, path: string, depth: number): { array: unknown[]; path: string } | null {
  if (depth > 4 || node === null || typeof node !== "object") return null;
  let best: { array: unknown[]; path: string } | null = null;
  const consider = (candidate: { array: unknown[]; path: string } | null) => {
    if (candidate && (!best || bytesOf(jsonLine(candidate.array)) > bytesOf(jsonLine(best.array)))) best = candidate;
  };
  if (Array.isArray(node)) {
    if (node.length > 1) consider({ array: node, path });
    node.slice(0, 8).forEach((child, i) => consider(largestArray(child, `${path}[${i}]`, depth + 1)));
  } else {
    for (const [k, v] of Object.entries(node as Record<string, unknown>)) consider(largestArray(v, `${path}.${k}`, depth + 1));
  }
  return best;
}

/** Render the text block of an outcome within the cap. */
export function renderText(o: ToolOutcome, opts: RenderOptions): string {
  const cap = opts.detailFull ? TEXT_CAP_FULL_BYTES : TEXT_CAP_BYTES;
  const head = headLines(o);
  const tail = tailLines(o);
  const data = dataOf(o);
  if (data === undefined) return [...head, ...tail].join("\n");
  const fixed = bytesOf([...head, ...tail].join("\n")) + 1;
  let json = jsonLine(data);
  if (fixed + bytesOf(json) > cap && !o.raw) {
    const { data: shrunk, cut } = shrinkToFit(data, Math.max(512, cap - fixed - 160));
    json = jsonLine(shrunk);
    if (cut) head.push(`warn: PARTIAL_RESULT the result was cut to fit (${cut}); use limit, fields or page for the rest`);
  }
  return [...head, json, ...tail].join("\n");
}

export function renderOutcome(o: ToolOutcome, opts: RenderOptions): RenderedResult {
  const text = renderText(o, opts);
  const content: RenderedResult["content"] = [{ type: "text", text }];
  for (const block of o.blocks ?? []) content.push({ type: "text", text: block });
  for (const image of o.images ?? []) content.push(image);
  const result: RenderedResult = { content };
  if (o.status === "error") result.isError = true;
  if (opts.structured && (o.images ?? []).length === 0) {
    result.structuredContent = {
      status: o.status,
      summary: o.summary,
      ...(o.code ? { code: o.code } : {}),
      ...(o.fix ? { fix: o.fix } : {}),
      notices: o.notices ?? [],
      warnings: o.warnings ?? [],
      data: o.status === "error" ? (o.details ?? null) : (o.data ?? null),
      more: o.more ?? null,
      next: o.next ?? null,
      doc: o.doc ?? null,
    };
  }
  return result;
}

/** Number formatting for summaries: 1234 → "1,234". */
export function fmtCount(n: number): string {
  return n.toLocaleString("en-US");
}
