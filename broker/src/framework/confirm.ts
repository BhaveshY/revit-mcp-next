// Confirm tokens (SPEC §6.4): 6 chars from ABCDEFGHJKMNPQRSTVWXYZ23456789, case-insensitive, single use, TTL 10 min,
// at most 32 per session. A token stores the exact plan: tool, registry key(s), normalized args, docKey, stamp and
// delete set. Applying it re-sends the stored args with confirmed{stamp, deleteSet}; re-sent args that differ give
// CONFIRM_MISMATCH. Elicitation (useElicitation) asks the user in the same call through inputRequired().
// Frozen in wave 2.

import { canonicalJson } from "@revit-mcp-next/contracts/catalog";
import type { BlastSummary } from "@revit-mcp-next/contracts/protocol";
import { confirmToken, normalizeToken } from "../runtime/ids.js";

export const CONFIRM_TTL_MS = 10 * 60_000;
export const MAX_TOKENS = 32;

export interface ConfirmPlan {
  tool: string;
  /** Registry key the confirm call executes (may differ from the key that produced the plan, e.g. model_delivery preview → execute). */
  key: string;
  op: string | null;
  /** Normalized args to send on apply (r#/last already expanded). */
  args: Record<string, unknown>;
  docKey: string | null;
  instanceId: string | null;
  stamp: number;
  deleteSet: number[];
  rule: string | null;
  plan: string;
  blast?: BlastSummary;
  /** Anything a tool module wants to keep with the plan (e.g. run_csharp code sha). */
  extra?: Record<string, unknown>;
}

export interface StoredPlan extends ConfirmPlan {
  token: string;
  createdAt: number;
}

export type ConfirmLookup =
  | { ok: true; plan: StoredPlan }
  | { ok: false; code: "CONFIRM_EXPIRED" | "CONFIRM_MISMATCH"; message: string; plan?: StoredPlan };

/** Params that never take part in the mismatch comparison of a confirm call. */
const NEUTRAL_PARAMS = new Set(["confirm", "doc", "preview", "units", "op", "kind", "check", "format", "detail"]);

export class ConfirmTokenStore {
  private readonly plans = new Map<string, StoredPlan>();

  /** Store a plan and return its token. */
  issue(plan: ConfirmPlan): string {
    this.prune();
    while (this.plans.size >= MAX_TOKENS) {
      const oldest = this.plans.keys().next().value;
      if (oldest === undefined) break;
      this.plans.delete(oldest);
    }
    let token = confirmToken();
    while (this.plans.has(token)) token = confirmToken();
    this.plans.set(token, { ...plan, token, createdAt: Date.now() });
    return token;
  }

  /** Look a token up for `tool` without consuming it. `sentArgs` = the confirm call's other args (for CONFIRM_MISMATCH). */
  check(token: string, tool: string, sentArgs: Record<string, unknown>, op: string | null): ConfirmLookup {
    this.prune();
    const plan = this.plans.get(normalizeToken(token));
    if (!plan) return { ok: false, code: "CONFIRM_EXPIRED", message: `confirm token ${normalizeToken(token)} is unknown or expired (tokens are single use and live 10 minutes)` };
    if (plan.tool !== tool) return { ok: false, code: "CONFIRM_MISMATCH", message: `token ${plan.token} belongs to ${plan.tool}${plan.op ? ` op=${plan.op}` : ""}, not ${tool}`, plan };
    if (op !== null && plan.op !== null && op !== plan.op && !opCompatible(plan, op))
      return { ok: false, code: "CONFIRM_MISMATCH", message: `token ${plan.token} is for op=${plan.op}, not op=${op}`, plan };
    for (const [key, value] of Object.entries(sentArgs)) {
      if (NEUTRAL_PARAMS.has(key) || value === undefined) continue;
      if (!(key in plan.args) || canonicalJson(plan.args[key]) !== canonicalJson(value))
        return { ok: false, code: "CONFIRM_MISMATCH", message: `the re-sent ${key} differs from the plan of token ${plan.token}; send only op and confirm`, plan };
    }
    return { ok: true, plan };
  }

  /** Consume a token (single use). */
  consume(token: string): StoredPlan | undefined {
    const key = normalizeToken(token);
    const plan = this.plans.get(key);
    this.plans.delete(key);
    return plan;
  }

  /** Remove without applying (e.g. the user declined an elicitation). */
  discard(token: string): void {
    this.plans.delete(normalizeToken(token));
  }

  get size(): number {
    this.prune();
    return this.plans.size;
  }

  list(): StoredPlan[] {
    this.prune();
    return [...this.plans.values()];
  }

  private prune(): void {
    const now = Date.now();
    for (const [token, plan] of this.plans) if (now - plan.createdAt > CONFIRM_TTL_MS) this.plans.delete(token);
  }
}

/** A plan issued by a preview op that applies to another op of the same tool (model_delivery preview → execute). */
function opCompatible(plan: StoredPlan, op: string): boolean {
  return plan.key.endsWith(`.${op}`);
}

export function expiresMinutes(plan: StoredPlan): number {
  return Math.max(0, Math.ceil((plan.createdAt + CONFIRM_TTL_MS - Date.now()) / 60_000));
}
