import { createHash } from "node:crypto";
import { mkdir, readFile, readdir, rename, rm, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import type { ModelDeliveryRecipe } from "@revit-mcp-next/contracts";

export const MODEL_DELIVERY_RECIPE_STORE_SCHEMA_VERSION = 1;

export interface StoredModelDeliveryRecipe {
  schemaVersion: typeof MODEL_DELIVERY_RECIPE_STORE_SCHEMA_VERSION;
  projectId: string;
  recipeVersion: string;
  recipeSha256: string;
  savedAtUtc: string;
  recipe: ModelDeliveryRecipe;
}

export interface ModelDeliveryRecipeSummary {
  projectId: string;
  recipeVersion: string;
  recipeSha256: string;
  savedAtUtc: string;
  sourceModelCount: number;
  packageName: string;
  destinationRoot: string;
}

export interface SaveModelDeliveryRecipeResult extends StoredModelDeliveryRecipe {
  changed: boolean;
}

function canonicalize(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value === null || typeof value !== "object") return value;

  return Object.fromEntries(
    Object.entries(value as Record<string, unknown>)
      .filter(([, child]) => child !== undefined)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, child]) => [key, canonicalize(child)])
  );
}

function sha256(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}

function recipeHash(recipe: ModelDeliveryRecipe): string {
  return sha256(JSON.stringify(canonicalize(recipe)));
}

function projectKey(projectId: string): string {
  return sha256(projectId).slice(0, 32);
}

function isStoredRecipe(value: unknown): value is StoredModelDeliveryRecipe {
  if (value === null || typeof value !== "object") return false;
  const candidate = value as Partial<StoredModelDeliveryRecipe>;
  return (
    candidate.schemaVersion === MODEL_DELIVERY_RECIPE_STORE_SCHEMA_VERSION &&
    typeof candidate.projectId === "string" &&
    typeof candidate.recipeVersion === "string" &&
    typeof candidate.recipeSha256 === "string" &&
    typeof candidate.savedAtUtc === "string" &&
    candidate.recipe !== null &&
    typeof candidate.recipe === "object"
  );
}

export function defaultModelDeliveryRecipeStoreRoot(): string {
  const configuredRoot = process.env.REVIT_MCP_NEXT_RECIPE_STORE?.trim();
  if (configuredRoot) return configuredRoot;

  const localAppData = process.env.LOCALAPPDATA?.trim();
  return localAppData
    ? join(localAppData, "RevitMcpNext", "recipes")
    : join(homedir(), ".revit-mcp-next", "recipes");
}

export class ModelDeliveryRecipeStore {
  public constructor(public readonly root: string = defaultModelDeliveryRecipeStoreRoot()) {}

  public async save(
    recipe: ModelDeliveryRecipe,
    expectedRecipeSha256?: string
  ): Promise<SaveModelDeliveryRecipeResult> {
    if (recipe.projectId.trim() !== recipe.projectId || recipe.projectId.length === 0) {
      throw new Error("projectId must be non-empty and must not have leading or trailing whitespace.");
    }

    const current = await this.get(recipe.projectId);
    const expectedHash = expectedRecipeSha256?.toLowerCase();
    if (expectedHash !== undefined && current?.recipeSha256 !== expectedHash) {
      throw new Error(
        `Recipe conflict for project '${recipe.projectId}'. Expected ${expectedHash}, current is ${current?.recipeSha256 ?? "missing"}.`
      );
    }

    const digest = recipeHash(recipe);
    if (current?.recipeSha256 === digest) return { ...current, changed: false };

    await mkdir(this.root, { recursive: true });
    const now = Date.now();
    const currentSavedAt = current ? Date.parse(current.savedAtUtc) : 0;
    const stored: StoredModelDeliveryRecipe = {
      schemaVersion: MODEL_DELIVERY_RECIPE_STORE_SCHEMA_VERSION,
      projectId: recipe.projectId,
      recipeVersion: recipe.recipeVersion,
      recipeSha256: digest,
      savedAtUtc: new Date(Math.max(now, currentSavedAt + 1)).toISOString(),
      recipe,
    };
    const target = join(this.root, `${projectKey(recipe.projectId)}.${digest}.json`);
    const temporary = `${target}.${process.pid}.${Date.now()}.tmp`;

    try {
      await writeFile(temporary, `${JSON.stringify(stored, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
      try {
        await rename(temporary, target);
      } catch (error) {
        const existing = await this.readStoredFile(target).catch(() => undefined);
        if (existing?.recipeSha256 !== digest) throw error;
      }
    } finally {
      await rm(temporary, { force: true }).catch(() => undefined);
    }

    return { ...stored, changed: true };
  }

  public async get(projectId: string): Promise<StoredModelDeliveryRecipe | undefined> {
    const prefix = `${projectKey(projectId)}.`;
    const names = await readdir(this.root).catch((error: NodeJS.ErrnoException) => {
      if (error.code === "ENOENT") return [];
      throw error;
    });
    const candidates: StoredModelDeliveryRecipe[] = [];
    for (const name of names.filter((value) => value.startsWith(prefix) && value.endsWith(".json"))) {
      const stored = await this.readStoredFile(join(this.root, name));
      if (stored.projectId === projectId) candidates.push(stored);
    }

    candidates.sort((left, right) => right.savedAtUtc.localeCompare(left.savedAtUtc));
    return candidates[0];
  }

  public async list(): Promise<ModelDeliveryRecipeSummary[]> {
    const names = await readdir(this.root).catch((error: NodeJS.ErrnoException) => {
      if (error.code === "ENOENT") return [];
      throw error;
    });
    const latest = new Map<string, StoredModelDeliveryRecipe>();
    for (const name of names.filter((value) => /^[a-f0-9]{32}\.[a-f0-9]{64}\.json$/.test(value))) {
      const stored = await this.readStoredFile(join(this.root, name));
      const current = latest.get(stored.projectId);
      if (!current || stored.savedAtUtc > current.savedAtUtc) latest.set(stored.projectId, stored);
    }

    return [...latest.values()]
      .sort((left, right) => right.savedAtUtc.localeCompare(left.savedAtUtc))
      .map((stored) => ({
        projectId: stored.projectId,
        recipeVersion: stored.recipeVersion,
        recipeSha256: stored.recipeSha256,
        savedAtUtc: stored.savedAtUtc,
        sourceModelCount: stored.recipe.sourceModels.length,
        packageName: stored.recipe.packageName,
        destinationRoot: stored.recipe.destinationRoot,
      }));
  }

  private async readStoredFile(path: string): Promise<StoredModelDeliveryRecipe> {
    const parsed: unknown = JSON.parse(await readFile(path, "utf8"));
    if (!isStoredRecipe(parsed)) throw new Error(`Invalid model-delivery recipe store entry: ${path}`);
    const actualHash = recipeHash(parsed.recipe);
    if (actualHash !== parsed.recipeSha256) throw new Error(`Recipe integrity check failed: ${path}`);
    if (parsed.recipe.projectId !== parsed.projectId) throw new Error(`Recipe projectId mismatch: ${path}`);
    return parsed;
  }
}
