// Read-only access to the add-in write ledgers (D2 §13.1, SPEC §8.10): <home>\ledger\<instanceId>.jsonl.
// After an instance died the broker searches all ledger files by requestId.

import { readdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import { ledgerDir } from "@revit-mcp-next/contracts/home";
import type { LedgerEntry } from "@revit-mcp-next/contracts/protocol";

/** Find the terminal ledger entry for a requestId in any ledger file (newest line wins). */
export async function searchLedgers(home: string, requestId: string, preferInstanceId?: string): Promise<(LedgerEntry & { instanceId: string }) | null> {
  const dir = ledgerDir(home);
  let names: string[];
  try {
    names = (await readdir(dir)).filter((n) => n.endsWith(".jsonl"));
  } catch {
    return null;
  }
  if (preferInstanceId) names.sort((a, b) => (a.startsWith(preferInstanceId) ? -1 : b.startsWith(preferInstanceId) ? 1 : 0));
  for (const name of names) {
    let text: string;
    try {
      text = await readFile(join(dir, name), "utf8");
    } catch {
      continue;
    }
    if (!text.includes(requestId)) continue;
    const lines = text.split(/\r?\n/);
    for (let i = lines.length - 1; i >= 0; i--) {
      const line = lines[i]!;
      if (!line.includes(requestId)) continue;
      try {
        const entry = JSON.parse(line) as LedgerEntry;
        if (entry.requestId === requestId) return { ...entry, instanceId: name.replace(/\.jsonl$/, "").replace(/\.\d+$/, "") };
      } catch {
        // partial line
      }
    }
  }
  return null;
}

/** Recent terminal entries of a doc across ledgers (fallback when the instance is gone). */
export async function recentLedgerEntries(home: string, docKey: string, limit = 20): Promise<LedgerEntry[]> {
  const dir = ledgerDir(home);
  const out: LedgerEntry[] = [];
  let names: string[];
  try {
    names = (await readdir(dir)).filter((n) => n.endsWith(".jsonl"));
  } catch {
    return out;
  }
  for (const name of names) {
    try {
      const text = await readFile(join(dir, name), "utf8");
      for (const line of text.split(/\r?\n/)) {
        if (!line.includes(docKey)) continue;
        try {
          const entry = JSON.parse(line) as LedgerEntry;
          if (entry.docKey === docKey) out.push(entry);
        } catch {
          // ignore
        }
      }
    } catch {
      // ignore
    }
  }
  out.sort((a, b) => String(b.completedAtUtc ?? b.acceptedAtUtc ?? "").localeCompare(String(a.completedAtUtc ?? a.acceptedAtUtc ?? "")));
  return out.slice(0, limit);
}
