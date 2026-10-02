// The `doc` parameter and set_target grammar (SPEC §7.3):
//   #3 or 3 (session doc number) · active · follow (set_target only) · none (set_target only) ·
//   exact title (case-insensitive, extension optional) · absolute path · title@2027.
// Reads also accept a unique title prefix or a unique contains match; writes accept exact matches only.

import { normalizeKeyPath } from "@revit-mcp-next/contracts/protocol";

export type DocSelector =
  | { kind: "active" }
  | { kind: "follow" }
  | { kind: "none" }
  | { kind: "number"; n: number }
  | { kind: "path"; path: string }
  | { kind: "title"; title: string; year?: number };

export function parseDocParam(raw: unknown): DocSelector | null {
  if (raw === undefined || raw === null) return null;
  const text = String(raw).trim();
  if (!text) return null;
  const lower = text.toLowerCase();
  if (lower === "active" || lower === "current") return { kind: "active" };
  if (lower === "follow") return { kind: "follow" };
  if (lower === "none" || lower === "auto" || lower === "clear") return { kind: "none" };
  const num = /^#?\s*(\d{1,4})$/.exec(text);
  if (num) return { kind: "number", n: Number(num[1]) };
  if (/^[a-z]:[\\/]/i.test(text) || text.startsWith("\\\\")) return { kind: "path", path: text };
  const at = /^(.*)@(\d{4})$/.exec(text);
  if (at && at[1]!.trim()) return { kind: "title", title: at[1]!.trim(), year: Number(at[2]) };
  return { kind: "title", title: text };
}

export interface DocCandidate {
  n: number;
  title: string;
  path: string;
  central?: string | null;
  key: string;
  aliases: string[];
  year: number;
  kind: "project" | "family";
  instanceId: string;
  pid: number;
  rid: number;
  /** UI-active doc of its Revit instance. */
  active: boolean;
  readOnly: boolean;
}

export type MatchQuality = "exact" | "prefix" | "contains" | "none";

export interface DocMatch {
  matches: DocCandidate[];
  quality: MatchQuality;
}

const EXT = /\.(rvt|rfa|rte|rft)$/i;
export const stripExt = (title: string) => title.replace(EXT, "");
const normTitle = (title: string) => stripExt(title.trim()).toLowerCase();

/** Match a title/path/number selector against candidates. `allowFuzzy` = reads (prefix/contains). */
export function matchDocs(sel: DocSelector, candidates: DocCandidate[], allowFuzzy: boolean): DocMatch {
  switch (sel.kind) {
    case "number": {
      const hit = candidates.filter((c) => c.n === sel.n);
      return { matches: hit, quality: hit.length ? "exact" : "none" };
    }
    case "path": {
      const p = normalizeKeyPath(sel.path);
      const hit = candidates.filter(
        (c) =>
          (c.path && normalizeKeyPath(c.path) === p) ||
          (c.central && normalizeKeyPath(c.central) === p) ||
          keyPath(c.key) === p ||
          c.aliases.some((a) => keyPath(a) === p)
      );
      return { matches: hit, quality: hit.length ? "exact" : "none" };
    }
    case "title": {
      const pool = sel.year ? candidates.filter((c) => c.year === sel.year) : candidates;
      const want = normTitle(sel.title);
      const exact = pool.filter((c) => normTitle(c.title) === want);
      if (exact.length || !allowFuzzy) return { matches: exact, quality: exact.length ? "exact" : "none" };
      const prefix = pool.filter((c) => normTitle(c.title).startsWith(want));
      if (prefix.length) return { matches: prefix, quality: "prefix" };
      const contains = pool.filter((c) => normTitle(c.title).includes(want));
      return { matches: contains, quality: contains.length ? "contains" : "none" };
    }
    default:
      return { matches: [], quality: "none" };
  }
}

/** The path part of a file/central key (`2024|file|c:\p\a.rvt` → `c:\p\a.rvt`). */
function keyPath(key: string): string | null {
  const parts = key.split("|");
  if (parts.length >= 3 && (parts[1] === "file" || parts[1] === "central")) return parts.slice(2).join("|");
  return null;
}

/** "#3 Tower_A (Revit 2024)" */
export function docLabel(c: Pick<DocCandidate, "n" | "title" | "year">): string {
  return `#${c.n} ${stripExt(c.title)} (Revit ${c.year})`;
}
