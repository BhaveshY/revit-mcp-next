// settings.json reader (SPEC §3.1): stat-cached for 2 s; invalid JSON keeps the last good values and reports
// SETTINGS_INVALID. The broker never writes this file (agents must not edit it; only the installer or the user does).

import { readFileSync, statSync } from "node:fs";
import { defaultSettings, isDefaultHome, parseSettingsText, settingsFile, type Settings } from "@revit-mcp-next/contracts/home";

export interface SettingsState {
  settings: Settings;
  /** settings.json exists. */
  exists: boolean;
  /** Last modification time (ms) of settings.json, or null. */
  mtimeMs: number | null;
  /** SETTINGS_INVALID reason when the file does not parse (last good values are used). */
  invalid: string | null;
  /** Non-fatal problems (wrong types, clamped values). */
  issues: string[];
}

const STAT_CACHE_MS = 2_000;

export class SettingsStore {
  readonly path: string;
  private state: SettingsState;
  private checkedAt = 0;
  private readonly isolated: boolean;
  /** Settings as they were when the broker started (run_csharp listing uses this). */
  readonly atStart: SettingsState;

  constructor(readonly home: string) {
    this.path = settingsFile(home);
    this.isolated = !isDefaultHome(home);
    this.state = { settings: defaultSettings(), exists: false, mtimeMs: null, invalid: null, issues: [] };
    this.refresh(true);
    this.atStart = { ...this.state, settings: structuredClone(this.state.settings), issues: [...this.state.issues] };
  }

  get(): Settings {
    return this.current().settings;
  }

  current(): SettingsState {
    if (Date.now() - this.checkedAt >= STAT_CACHE_MS) this.refresh(false);
    return this.state;
  }

  private refresh(force: boolean): void {
    this.checkedAt = Date.now();
    let mtimeMs: number | null = null;
    try {
      mtimeMs = statSync(this.path).mtimeMs;
    } catch {
      if (this.state.exists || force) this.state = { settings: defaultSettings(), exists: false, mtimeMs: null, invalid: null, issues: [] };
      return;
    }
    if (!force && this.state.exists && this.state.mtimeMs === mtimeMs) return;
    let text: string;
    try {
      text = readFileSync(this.path, "utf8");
    } catch (error) {
      this.state = { ...this.state, exists: true, mtimeMs, invalid: `cannot read settings.json: ${(error as Error).message}` };
      return;
    }
    try {
      const parsed = parseSettingsText(text, { isolatedHome: this.isolated });
      this.state = { settings: parsed.settings, exists: true, mtimeMs, invalid: null, issues: parsed.issues };
    } catch (error) {
      // Keep the last good values (SETTINGS_INVALID).
      this.state = { ...this.state, exists: true, mtimeMs, invalid: `settings.json is not valid JSON: ${(error as Error).message}` };
    }
  }
}
