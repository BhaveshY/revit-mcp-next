// Auth token (D2 §3.2): read <home>\config\auth.env lazily on first send, stat-cached for 1 s, re-read on mtime change.
// The token never appears in logs or results; only its fingerprint (first 8 hex of sha256) and the file path do.

import { readFileSync, statSync } from "node:fs";
import { authFile, authFingerprint, AUTH_TOKEN_ENV, AUTH_TOKEN_PATTERN, parseAuthEnv } from "@revit-mcp-next/contracts/home";
import type { AuthBlock } from "@revit-mcp-next/contracts/protocol";

export type AuthSource = "file" | "env" | "none";

export interface AuthState {
  file: string;
  source: AuthSource;
  /** null when no token is available (AUTH_NOT_CONFIGURED). */
  block: AuthBlock | null;
  fp: string | null;
  mtimeMs: number | null;
}

const STAT_CACHE_MS = 1_000;

export class AuthStore {
  readonly file: string;
  private state: AuthState | null = null;
  private checkedAt = 0;

  constructor(home: string, private readonly env: Record<string, string | undefined> = process.env) {
    this.file = authFile(home);
  }

  /** Current token block (null when missing). Cheap: stat at most once per second. */
  get(): AuthBlock | null {
    return this.current().block;
  }

  current(): AuthState {
    if (!this.state || Date.now() - this.checkedAt >= STAT_CACHE_MS) this.refresh(false);
    return this.state!;
  }

  /** Force a re-read (after AUTH_MISMATCH). Returns true when the token changed. */
  reload(): boolean {
    const before = this.state?.fp ?? null;
    this.refresh(true);
    return (this.state?.fp ?? null) !== before;
  }

  private refresh(force: boolean): void {
    this.checkedAt = Date.now();
    const envToken = this.env[AUTH_TOKEN_ENV]?.trim();
    if (envToken && AUTH_TOKEN_PATTERN.test(envToken)) {
      const fp = authFingerprint(envToken);
      this.state = { file: this.file, source: "env", block: { token: envToken, fp, file: this.file }, fp, mtimeMs: null };
      return;
    }
    let mtimeMs: number;
    try {
      mtimeMs = statSync(this.file).mtimeMs;
    } catch {
      this.state = { file: this.file, source: "none", block: null, fp: null, mtimeMs: null };
      return;
    }
    if (!force && this.state && this.state.source === "file" && this.state.mtimeMs === mtimeMs) return;
    try {
      const parsed = parseAuthEnv(readFileSync(this.file, "utf8"));
      if (!parsed.token) {
        this.state = { file: this.file, source: "none", block: null, fp: null, mtimeMs };
        return;
      }
      const fp = authFingerprint(parsed.token);
      this.state = { file: this.file, source: "file", block: { token: parsed.token, fp, file: this.file }, fp, mtimeMs };
    } catch {
      this.state = { file: this.file, source: "none", block: null, fp: null, mtimeMs };
    }
  }
}
