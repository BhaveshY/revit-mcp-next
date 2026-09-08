import type { RevitTarget } from "@revit-mcp-next/contracts";

export class SessionTargetStore {
  private target?: RevitTarget;

  constructor(readonly sessionId: string) {}

  get(): RevitTarget | undefined {
    return this.target ? { ...this.target } : undefined;
  }

  set(target: RevitTarget): RevitTarget {
    this.target = { ...target, selectionMode: "explicit" };
    return this.get()!;
  }

  updateGeneration(instanceId: string, documentFingerprint: string, generation: number): void {
    if (!this.target || !Number.isInteger(generation) || generation < 0) return;
    if (this.target.instanceId !== instanceId || this.target.documentFingerprint !== documentFingerprint) return;
    this.target = { ...this.target, generation: Math.max(this.target.generation, generation) };
  }

  clear(): RevitTarget | undefined {
    const previous = this.get();
    this.target = undefined;
    return previous;
  }
}
