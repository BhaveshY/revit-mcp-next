// Minimal TOML text surgery for ~/.codex/config.toml (SPEC §12.2): the file is never re-serialized. Tables are
// located line by line (multi-line strings are skipped), whole tables are removed or inserted as text, and a few
// simple values (strings, numbers, booleans, string arrays) can be read back for the doctor.

export interface TomlTable {
  /** Dotted name with quotes removed, e.g. `mcp_servers.revit.tools.status` or `plugins.revit-mcp-cowork@claude-cowork`. */
  name: string;
  /** Path segments with quotes removed. */
  path: string[];
  /** Index of the header line. */
  start: number;
  /** Index after the last line of the table body. */
  end: number;
  array: boolean;
}

export interface TomlDoc {
  lines: string[];
  eol: "\r\n" | "\n";
  /** Body lines before the first header end at this index. */
  rootEnd: number;
  tables: TomlTable[];
  trailingNewline: boolean;
}

/** Split a dotted key (`a."b.c".d`, `a.'x y'`) into segments. */
export function splitKeyPath(key: string): string[] {
  const out: string[] = [];
  let cur = "";
  let quote: string | null = null;
  for (let i = 0; i < key.length; i++) {
    const ch = key[i]!;
    if (quote) {
      if (ch === "\\" && quote === '"' && i + 1 < key.length) {
        cur += key[++i];
        continue;
      }
      if (ch === quote) quote = null;
      else cur += ch;
      continue;
    }
    if (ch === '"' || ch === "'") quote = ch;
    else if (ch === ".") {
      out.push(cur.trim());
      cur = "";
    } else cur += ch;
  }
  out.push(cur.trim());
  return out.filter((s, i, a) => s.length > 0 || a.length === 1);
}

const HEADER = /^\s*(\[\[?)\s*((?:[^\]"'#]|"(?:[^"\\]|\\.)*"|'[^']*')+?)\s*(\]\]?)\s*(#.*)?$/;

export function parseToml(text: string): TomlDoc {
  const eol: "\r\n" | "\n" = text.includes("\r\n") ? "\r\n" : "\n";
  const trailingNewline = /\r?\n$/.test(text);
  const lines = text.replace(/\r?\n$/, "").split(/\r?\n/);
  if (text.length === 0) lines.length = 0;
  const tables: TomlTable[] = [];
  let multi: string | null = null;
  let rootEnd = lines.length;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    if (multi) {
      if (countDelims(line, multi) % 2 === 1) multi = null;
      continue;
    }
    const header = HEADER.exec(line);
    if (header && !/=/.test(line.split("#")[0]!.replace(/"[^"]*"|'[^']*'/g, ""))) {
      if (tables.length === 0) rootEnd = i;
      else tables[tables.length - 1]!.end = i;
      const path = splitKeyPath(header[2]!);
      tables.push({ name: path.join("."), path, start: i, end: lines.length, array: header[1] === "[[" });
      continue;
    }
    for (const delim of ['"""', "'''"]) {
      if (countDelims(line, delim) % 2 === 1) {
        multi = delim;
        break;
      }
    }
  }
  return { lines, eol, rootEnd, tables, trailingNewline };
}

function countDelims(line: string, delim: string): number {
  let n = 0;
  for (let i = line.indexOf(delim); i >= 0; i = line.indexOf(delim, i + delim.length)) n++;
  return n;
}

export function serialize(doc: TomlDoc): string {
  return doc.lines.join(doc.eol) + (doc.trailingNewline || doc.lines.length ? doc.eol : "");
}

export function tableBody(doc: TomlDoc, table: TomlTable): string[] {
  return doc.lines.slice(table.start + 1, table.end);
}

/** Tables whose path starts with `prefix` (segment-wise). */
export function tablesUnder(doc: TomlDoc, prefix: string[]): TomlTable[] {
  return doc.tables.filter((t) => prefix.every((seg, i) => t.path[i] === seg));
}

/** Read `key = value` from table lines (first match). */
export function readValue(lines: string[], key: string): unknown {
  for (const line of lines) {
    const m = /^\s*([A-Za-z0-9_\-]+|"[^"]*"|'[^']*')\s*=\s*(.*)$/.exec(line);
    if (!m) continue;
    const k = m[1]!.replace(/^["']|["']$/g, "");
    if (k !== key) continue;
    return parseValue(m[2]!.trim());
  }
  return undefined;
}

/** All `key = value` pairs of a table (simple values only; others are returned as raw text). */
export function readPairs(lines: string[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const line of lines) {
    const m = /^\s*([A-Za-z0-9_\-]+|"[^"]*"|'[^']*')\s*=\s*(.*)$/.exec(line);
    if (m) out[m[1]!.replace(/^["']|["']$/g, "")] = parseValue(m[2]!.trim());
  }
  return out;
}

export function parseValue(raw: string): unknown {
  const text = stripComment(raw).trim();
  if (text.startsWith("[")) {
    const items: unknown[] = [];
    const inner = text.replace(/^\[/, "").replace(/\]$/, "");
    const re = /\s*("(?:[^"\\]|\\.)*"|'[^']*'|[^,]+)\s*(,|$)/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(inner)) && m[0].length > 0) {
      if (m[1]!.trim()) items.push(parseValue(m[1]!));
      if (re.lastIndex >= inner.length) break;
    }
    return items;
  }
  if (text.startsWith("'")) return text.replace(/^'/, "").replace(/'$/, "");
  if (text.startsWith('"')) {
    try {
      return JSON.parse(text);
    } catch {
      return text.slice(1, -1);
    }
  }
  if (text === "true") return true;
  if (text === "false") return false;
  if (/^[+-]?\d+(\.\d+)?$/.test(text)) return Number(text);
  return text;
}

function stripComment(raw: string): string {
  let quote: string | null = null;
  for (let i = 0; i < raw.length; i++) {
    const ch = raw[i]!;
    if (quote) {
      if (ch === "\\" && quote === '"') i++;
      else if (ch === quote) quote = null;
    } else if (ch === '"' || ch === "'") quote = ch;
    else if (ch === "#") return raw.slice(0, i);
  }
  return raw;
}

/** A TOML string for a Windows path: literal '...' unless it contains a single quote. */
export function tomlString(value: string): string {
  if (!value.includes("'") && !/[\r\n]/.test(value)) return `'${value}'`;
  return JSON.stringify(value);
}

/** Header text for a path: segments that are not bare keys are quoted. */
export function tomlHeader(path: string[]): string {
  return path.map((s) => (/^[A-Za-z0-9_-]+$/.test(s) ? s : JSON.stringify(s))).join(".");
}

/**
 * Remove whole tables (and the managed-comment/blank lines right above the first one).
 * Returns the index where the first removed table started (after adjusting), or -1.
 */
export function removeTables(doc: TomlDoc, predicate: (t: TomlTable) => boolean, managedComment: RegExp): { removed: string[]; at: number } {
  const victims = doc.tables.filter(predicate);
  if (victims.length === 0) return { removed: [], at: -1 };
  const drop = new Set<number>();
  for (const t of victims) {
    // Comment lines at the end of a table usually describe the NEXT table: keep them (the last table keeps nothing).
    let end = t.end;
    if (t.end < doc.lines.length) {
      while (end - 1 > t.start && /^\s*(#.*)?$/.test(doc.lines[end - 1]!)) end--;
      while (end < t.end && doc.lines[end]!.trim() === "") end++;
    }
    for (let i = t.start; i < end; i++) drop.add(i);
    let j = t.start - 1;
    while (j >= 0 && !drop.has(j) && managedComment.test(doc.lines[j]!)) {
      drop.add(j);
      j--;
    }
  }
  const first = Math.min(...[...drop]);
  // Keep every other line untouched; where a removed block leaves two blank lines in a row, drop one of them.
  const kept: number[] = [];
  for (let i = 0; i < doc.lines.length; i++) if (!drop.has(i)) kept.push(i);
  const lines: string[] = [];
  let at = -1;
  for (let k = 0; k < kept.length; k++) {
    const i = kept[k]!;
    const line = doc.lines[i]!;
    const gapBefore = k > 0 && i - kept[k - 1]! > 1;
    if (at < 0 && i > first) at = lines.length;
    if (gapBefore && line.trim() === "" && lines.length > 0 && lines[lines.length - 1]!.trim() === "") continue;
    lines.push(line);
  }
  if (at < 0) at = lines.length;
  const next = parseToml(lines.join(doc.eol) + doc.eol);
  doc.lines = next.lines;
  doc.tables = next.tables;
  doc.rootEnd = next.rootEnd;
  return { removed: victims.map((t) => t.name), at: Math.min(at, doc.lines.length) };
}

/** Insert lines at `at` (or append with one blank line before). */
export function insertLines(doc: TomlDoc, at: number, block: string[]): void {
  const lines = [...doc.lines];
  if (at < 0 || at >= lines.length) {
    while (lines.length > 0 && lines[lines.length - 1]!.trim() === "") lines.pop();
    if (lines.length > 0) lines.push("");
    lines.push(...block);
  } else {
    const before = lines.slice(0, at);
    const after = lines.slice(at);
    while (before.length > 0 && before[before.length - 1]!.trim() === "") before.pop();
    const sep = before.length > 0 ? [""] : [];
    const tail = after.length > 0 && after[0]!.trim() !== "" ? [""] : [];
    lines.length = 0;
    lines.push(...before, ...sep, ...block, ...tail, ...after);
  }
  const next = parseToml(lines.join(doc.eol) + doc.eol);
  doc.lines = next.lines;
  doc.tables = next.tables;
  doc.rootEnd = next.rootEnd;
  doc.trailingNewline = true;
}

/** Set `key = value` inside a table (replace the first assignment or add it right after the header). */
export function setTableValue(doc: TomlDoc, table: TomlTable, key: string, valueText: string, comment?: string): { before: unknown; changed: boolean } {
  const body = tableBody(doc, table);
  const before = readValue(body, key);
  const re = new RegExp(`^(\\s*)(${key.replace(/[-]/g, "\\-")}|"${key}"|'${key}')\\s*=.*$`);
  let changed = false;
  let found = false;
  for (let i = table.start + 1; i < table.end; i++) {
    const m = re.exec(doc.lines[i]!);
    if (!m) continue;
    found = true;
    const next = `${m[1]}${key} = ${valueText}${comment ? ` # ${comment}` : ""}`;
    if (doc.lines[i] !== next) {
      doc.lines[i] = next;
      changed = true;
    }
    break;
  }
  if (!found) {
    doc.lines.splice(table.start + 1, 0, `${key} = ${valueText}${comment ? ` # ${comment}` : ""}`);
    const next = parseToml(doc.lines.join(doc.eol) + doc.eol);
    doc.tables = next.tables;
    doc.rootEnd = next.rootEnd;
    changed = true;
  }
  return { before, changed };
}
