// Broker home resolution (SPEC §3, D2 §2.2): env REVIT_MCP_NEXT_HOME, else the home that contains this broker
// (walk up for the marker), else %USERPROFILE%\.revit-mcp-next. Never touches Revit; never creates the marker.

import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { forbiddenHomeReason, resolveHome, type HomeSource } from "@revit-mcp-next/contracts/home";

export interface BrokerHome {
  home: string;
  source: HomeSource;
  /** The marker file exists. */
  installed: boolean;
  /** Why this location is not a safe home (status shows it), or null. */
  warning: string | null;
}

export function resolveBrokerHome(env: Record<string, string | undefined> = process.env): BrokerHome {
  const selfDir = dirname(fileURLToPath(import.meta.url));
  const resolved = resolveHome({ env, selfDir });
  let warning: string | null = null;
  try {
    warning = forbiddenHomeReason(resolved.home, env);
  } catch {
    warning = null;
  }
  return { ...resolved, warning };
}

export interface BuildInfo {
  version: string;
  gitSha: string;
  builtAt?: string;
  sdk?: string;
}

/** Version info: <home>/broker/build-info.json for installed bundles, else the package.json next to dist. */
export function readBuildInfo(): BuildInfo {
  const here = dirname(fileURLToPath(import.meta.url));
  const candidates = [join(here, "..", "..", "build-info.json"), join(here, "..", "..", "..", "build-info.json"), join(here, "build-info.json")];
  for (const path of candidates) {
    try {
      if (existsSync(path)) {
        const info = JSON.parse(readFileSync(path, "utf8")) as Partial<BuildInfo>;
        if (info.version) return { version: info.version, gitSha: info.gitSha ?? process.env.REVIT_MCP_NEXT_GIT_SHA ?? "", builtAt: info.builtAt, sdk: info.sdk };
      }
    } catch {
      // ignore unreadable build info
    }
  }
  let version = process.env.REVIT_MCP_NEXT_VERSION ?? "0.0.0";
  for (const path of [join(here, "..", "..", "package.json"), join(here, "..", "..", "..", "package.json")]) {
    try {
      if (existsSync(path)) {
        const pkg = JSON.parse(readFileSync(path, "utf8")) as { name?: string; version?: string };
        if (pkg.name === "@revit-mcp-next/broker" && pkg.version) {
          version = pkg.version;
          break;
        }
      }
    } catch {
      // ignore
    }
  }
  return { version, gitSha: process.env.REVIT_MCP_NEXT_GIT_SHA ?? "", sdk: sdkVersion() };
}

export function sdkVersion(): string {
  const here = dirname(fileURLToPath(import.meta.url));
  for (const path of [
    join(here, "..", "..", "node_modules", "@modelcontextprotocol", "server", "package.json"),
    join(here, "..", "..", "..", "node_modules", "@modelcontextprotocol", "server", "package.json"),
    join(here, "..", "..", "..", "..", "node_modules", "@modelcontextprotocol", "server", "package.json"),
  ]) {
    try {
      if (existsSync(path)) return (JSON.parse(readFileSync(path, "utf8")) as { version?: string }).version ?? "";
    } catch {
      // ignore
    }
  }
  return "";
}

/** Legacy roots that status detail:full reports (D2 §2.3): %LOCALAPPDATA%\RevitMcpNext and packaged LocalCache copies. */
export function findLegacyRoots(env: Record<string, string | undefined> = process.env): string[] {
  const found: string[] = [];
  const local = env.LOCALAPPDATA;
  if (!local) return found;
  const legacy = join(local, "RevitMcpNext");
  if (existsSync(legacy)) found.push(legacy);
  const packages = join(local, "Packages");
  try {
    for (const name of readdirSync(packages)) {
      const cache = join(packages, name, "LocalCache", "Local");
      try {
        for (const entry of readdirSync(cache)) if (/^RevitMcpNext/i.test(entry)) found.push(join(cache, entry));
      } catch {
        // not a package with LocalCache
      }
    }
  } catch {
    // no Packages folder
  }
  return found;
}
