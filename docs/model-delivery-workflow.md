# Model Delivery Workflow

## Refined objective

Provide a Codex-first, button-free workflow that packages an arbitrary project-specific set of Revit models for consultant delivery. The production source models must never be renamed, cleaned, overwritten, synchronized, or published by the workflow. Every delivered RVT must be a standalone, non-workshared file with no connection to its source central model.

The same typed contract and C# implementation must build for Revit 2024 and Revit 2027. Codex owns the conversation, exception resolution, preview, and approval. The Revit add-in owns every Revit API call.

## User continuity

1. The architect opens Revit with Revit MCP Next loaded.
2. In Codex, the architect requests a delivery and gives the project ID plus the current source RVT paths.
3. `revit.inspect_model_delivery` automatically loads the latest saved recipe for that project. On first use, no recipe exists and Codex asks only for the missing project-specific decisions. On repeat use, it reports only detected changes and exceptions.
4. `revit.preview_model_delivery` reads the source files and returns one complete plan: source-to-output names, worksharing state, link actions, cleanup counts, exports, warnings, and blockers.
5. Codex asks only about blockers or ambiguous project-specific mappings.
6. The architect approves the exact preview once.
7. `revit.execute_model_delivery` executes that single-use plan and returns a per-model audit plus package manifest.
8. After a successful delivery, Codex saves the approved recipe with `revit.save_model_delivery_recipe`; unchanged retries are no-ops.
9. Codex reports success, failure, and the final output paths. No Revit button is required.

## Non-negotiable invariants

- Source and destination paths are canonicalized and must never be equal or nested in a way that can overwrite a source.
- Existing final deliverables are never overwritten silently.
- Source documents must not be open in the current Revit process during execution.
- File-based central/workshared sources are opened with `DetachFromCentralOption.DetachAndDiscardWorksets`.
- Delivered RVTs are saved without `WorksharingSaveAsOptions.SaveAsCentral` and must reopen with `Document.IsWorkshared == false`.
- Already-standalone source RVTs remain standalone in the delivery package.
- Cleanup and relinking apply only to staging documents.
- A preview token is bound to the canonical recipe, source file fingerprints, output paths, and broker session; it is single-use and expires.
- A retry with the same `deliveryId` reconciles or replaces only that delivery's staging folder. It never creates duplicate final packages.
- The final package is published only after every required RVT and configured export passes validation.
- Failure leaves source files untouched, publishes no partial final package, and returns a readable audit.

## Project recipe

The recipe is project-specific and supports any number of differently named models. It contains:

- `projectId`, `recipeVersion`, and a stable `deliveryId` supplied per run.
- Explicit source models with source path, target file name, and optional project role.
- Destination root and package name.
- Link policy: keep/repath, unload, or remove, matched by source file name or exact existing path.
- Positioning policy: preserve existing instances and verify their transforms; never recreate placement by guesswork.
- Protected start view and optional protected 3D view per model, by exact name.
- Cleanup policy for sheets, views, schedules, legends, drafting views, templates, filters, and non-delivery links.
- Configured RVT, IFC, DWG, or NWC outputs. Export setup names and view/sheet scope are explicit.
- Acceptance thresholds for expected cleanup counts, required links, allowed warnings, and required output files.

The connector does not infer destructive rules. Preview blocks until every destructive rule and ambiguous link mapping is explicit.

### Persistent recipe library

- Recipes are broker metadata and are usable even when Revit is closed.
- The default store is `%LOCALAPPDATA%\RevitMcpNext\recipes`; deployments can override it with `REVIT_MCP_NEXT_RECIPE_STORE`.
- Entries are immutable, content-addressed JSON files with a canonical SHA-256 integrity hash. Repeating the same save does not create a new active version.
- `revit.get_model_delivery_recipe` returns the latest full recipe for an exact project ID. `revit.list_model_delivery_recipes` returns compact summaries when the ID is not known.
- Recipe updates can include `expectedRecipeSha256`. The broker rejects the update if another process saved a newer recipe, preventing stale office decisions from being silently replaced.
- `revit.inspect_model_delivery` accepts `projectId`; when `previousRecipe` is omitted, the broker automatically loads the latest saved recipe.
- A recipe is saved only after a successful delivery or explicit approval of a recipe change. Failed or cancelled runs do not become the office default.

## Execution algorithm

### Preview

For every source model:

1. Validate file accessibility, extension, supported Revit version, target name, and path collisions.
2. Open read-only where possible and inventory worksharing state, links, views, sheets, templates, filters, warnings, and configured export prerequisites.
3. Resolve the project-specific link map against the complete source-to-output mapping.
4. Calculate cleanup counts and list protected items and unresolved exceptions.
5. Hash the canonical recipe plus source length/last-write fingerprints and issue a single-use preview token.

Preview makes no model or filesystem changes.

### Execute

1. Revalidate the preview token and every source fingerprint.
2. Reconcile a delivery-specific staging folder; refuse unrelated existing content.
3. Open each source with detach-and-discard-worksets when workshared.
4. Save an initial standalone RVT into staging with overwrite disabled.
5. Reopen staged RVTs, repath approved links to staged target files while preserving instances, remove/unload excluded links, apply protected cleanup rules, set the start view, and compact-save.
6. Run configured exports.
7. Close and reopen every RVT and validate non-workshared state, no central path, target name, required links, link paths/load state/transforms, protected content, cleanup thresholds, warnings, and export files.
8. Write `manifest.json` and `audit.json` into staging.
9. Promote staging to the final package only when all required checks pass.

## Version contract

- Revit 2024: `net48` add-in built against the Revit 2024 API.
- Revit 2027: `net10.0-windows` add-in built against the Revit 2027 API.
- Shared source is used wherever the API surface is common. Version conditionals are allowed only where Autodesk changed signatures or runtime behavior.
- A package is not production-proven for a Revit year until the exact year-specific artifact passes a live-host scenario.

## Focused verification

The implementation should add only tests that prove the delivery contract:

1. Broker/schema contract: arbitrary model count, strict paths/names, preview/apply metadata, and no hidden defaults for destructive cleanup.
2. Deterministic delivery simulator: project-specific names, central and standalone inputs, link remapping, duplicate/ambiguous links, stale preview, failed QA, retry after partial staging, and successful publish.
3. Recipe-store contract: idempotent save, integrity check, latest-version lookup, inventory, automatic inspection load, and stale-write rejection.
4. Add-in build for both 2024 and 2027.
5. Live disposable Revit scenario per available installed year: at least two differently named models, one link, standalone output assertion, reopen validation, and no source modification.

## Definition of done

- Codex can call preview and execute tools without a Revit button.
- First-time decisions can be saved and automatically reused by project ID on later deliveries.
- The workflow supports arbitrary project-specific source names and counts.
- No delivered RVT is workshared or connected to a source central model.
- Links resolve inside the delivered package according to the approved map.
- Cleanup matches the approved preview and preserves protected content.
- Final publication is all-or-nothing and retry-safe.
- Both year-specific add-ins build; available live hosts pass the disposable scenario, and unavailable host evidence is reported rather than implied.
