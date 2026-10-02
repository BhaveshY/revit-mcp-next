// ToolRegistry: catalog lookup, the module map (one module per catalog tool), the profile filter and the run_csharp
// gate (listed only when enableCodeExecution was true at broker start). Frozen in wave 2.

import { byName, CATALOG, listedTools, type Profile, type ToolSpec } from "@revit-mcp-next/contracts/catalog";
import { outcomeFromResult } from "./defaults.js";
import type { AddinResult, ToolContext, ToolModule, ToolOutcome } from "./types.js";

export interface RegistryConfig {
  profile: Profile;
  /** enableCodeExecution was true at broker start. */
  codeExecution: boolean;
}

export class ToolRegistry {
  private readonly modules = new Map<string, ToolModule>();
  readonly problems: string[] = [];

  constructor(modules: readonly ToolModule[], readonly config: RegistryConfig) {
    for (const m of modules) {
      if (!byName.has(m.name)) this.problems.push(`module ${m.name} has no catalog entry`);
      else if (this.modules.has(m.name)) this.problems.push(`duplicate module ${m.name}`);
      else this.modules.set(m.name, m);
    }
    for (const spec of CATALOG) if (!this.modules.has(spec.name)) this.problems.push(`catalog tool ${spec.name} has no module`);
  }

  /** Tools served by tools/list for this configuration (deterministic order). */
  listed(): ToolSpec[] {
    return listedTools({ profile: this.config.profile, includeOptIn: this.config.codeExecution });
  }

  isListed(name: string): boolean {
    return this.listed().some((t) => t.name === name);
  }

  /**
   * Tools that may run in this broker: every catalog tool except opt-in tools that are not enabled. Tools outside the
   * core profile stay callable through read_many and change_set (§15).
   */
  isCallable(name: string): boolean {
    const spec = byName.get(name);
    if (!spec) return false;
    if (spec.optIn === "enableCodeExecution") return this.config.codeExecution;
    return true;
  }

  spec(name: string): ToolSpec | undefined {
    return byName.get(name);
  }

  module(name: string): ToolModule | undefined {
    return this.modules.get(name);
  }

  outcomeFrom(ctx: ToolContext, r: AddinResult): ToolOutcome {
    return outcomeFromResult(ctx, r);
  }
}
