// The environment Codex forwards to stdio MCP servers on Windows (D4 §6, codex rmcp-client default forwarding),
// compared case-insensitively. One module shared by the e2e harness and (wave 3) the doctor so they cannot drift.

export const CODEX_ENV_WHITELIST = [
  "PATH",
  "PATHEXT",
  "COMSPEC",
  "SYSTEMROOT",
  "WINDIR",
  "SYSTEMDRIVE",
  "USERNAME",
  "USERDOMAIN",
  "USERPROFILE",
  "HOMEDRIVE",
  "HOMEPATH",
  "PROGRAMFILES",
  "PROGRAMFILES(X86)",
  "PROGRAMW6432",
  "PROGRAMDATA",
  "LOCALAPPDATA",
  "APPDATA",
  "TEMP",
  "TMP",
  "TMPDIR",
  "POWERSHELL",
  "PWSH",
  "SHELL",
];

/**
 * Build a child environment like Codex does: only whitelisted variables from `base`, then `extras` on top
 * (server env from the config, e.g. REVIT_MCP_NEXT_HOME). Undefined extras remove a variable.
 */
export function codexEnv(base = process.env, ...extras) {
  const allowed = new Set(CODEX_ENV_WHITELIST.map((k) => k.toUpperCase()));
  const env = {};
  for (const [key, value] of Object.entries(base)) {
    if (value !== undefined && allowed.has(key.toUpperCase())) env[key] = value;
  }
  for (const extra of extras) {
    if (!extra) continue;
    for (const [key, value] of Object.entries(extra)) {
      for (const existing of Object.keys(env)) if (existing.toUpperCase() === key.toUpperCase()) delete env[existing];
      if (value !== undefined && value !== null) env[key] = String(value);
    }
  }
  return env;
}
