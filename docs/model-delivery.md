# Model delivery

`model_delivery` packages a project's Revit models for a consultant: standalone (non-workshared) RVT copies with
their links repathed inside the package, the agreed cleanup, optional IFC/DWG/NWC exports, QA, and a manifest. The
production source models are never changed, saved, synced or renamed. The decisions live in a per-project **recipe**,
so the second delivery of a project needs only the changes.

The tool is in the default (`full`) profile, not in `core`. All Revit work runs as a job (`job_status`).

## The flow

```
model_delivery {"op":"inspect","sources":["\\\\srv\\proj\\A.rvt","\\\\srv\\proj\\S.rvt"],"project":"P2026-017"}
model_delivery {"op":"preview","recipe":{...}}           -> plan + confirm token (NOT APPLIED)
   ask the user, then
model_delivery {"op":"execute","confirm":"K7QM2X"}        -> job j#; job_status {"id":"j#"} until done
model_delivery {"op":"save_recipe","recipe":{...}}        -> keep the approved decisions for next time
```

1. **inspect** opens isolated copies of the sources (the originals stay closed) and reports worksharing state, links,
   views and the decisions still missing. With `project`, it compares the sources with that project's saved recipe and
   lists what changed. Pass `recipe` to compare with a recipe that is not saved yet.
2. **Write the recipe.** `help {"topic":"recipe"}` returns the exact shape. Every destructive decision is explicit;
   nothing is defaulted. Unknown keys are rejected and errors name the JSON path.
3. **preview** checks the recipe against the sources and plans every model: output names, link actions, cleanup counts,
   exports, warnings and blockers. A ready plan returns `NOT APPLIED - needs the user's OK` with a confirm token;
   blockers say what to fix (for example `UNMAPPED_LINK`, `AMBIGUOUS_LINK_RULE`, `FINAL_PACKAGE_EXISTS`,
   `SOURCE_OUTPUT_COLLISION`, `PROTECTED_VIEW_NOT_FOUND`). Preview changes no model and no file.
4. **execute** takes only the confirm token; the stored plan is applied. It refuses (`REVIT_REFUSED`, reason `SOURCE_DOCUMENT_OPEN`) while a
   source is open in this Revit, re-checks the source fingerprints (`CONFIRM_STALE` if a source changed since the preview) and runs as a
   job, one phase per step, so other calls still get through.
5. **save_recipe** stores the recipe after a successful delivery. `get_recipe {"project":...}` and `list_recipes`
   read the library.

## What execute does

For each source model, in `<destinationRoot>\.revit-mcp-staging\<deliveryId>\`:

1. Copies the source next to the staging output, unloads its links in the copy and opens it; a workshared source is
   opened with *detach and discard worksets*.
2. Saves a standalone RVT under the recipe's `targetFileName` (never as central, never overwriting).
3. Reopens the staged models and applies the link rules: `repath` to another packaged model (relative path, existing
   instances and transforms kept), `unload`, `remove` or `retain`; `removeUnmappedLinks` handles links no rule names.
4. Applies the cleanup (sheets, views, schedules, legends, drafting views, view templates, unused filters, purge
   passes), keeping the protected and start views, and compact-saves.
5. Runs the configured exports (IFC, DWG with explicit `viewNames`, NWC when the Navisworks exporter is installed).
6. Closes and reopens every RVT and checks QA: not workshared, no central path, source hashes unchanged, cleanup equal
   to the preview, link paths and transforms, required exports present, warnings under `maxWarnings`.
7. Writes `manifest.json` and `audit.json`, then publishes the package by moving the staging folder to
   `<destinationRoot>\<packageName>` in one step.

If any required check fails, nothing is published: the staging folder stays for inspection and the result lists the
issues (`MODEL_QA_FAILED`, `FINAL_PACKAGE_QA_FAILED`, ...). A retry with the same `deliveryId` reconciles only its own
staging folder (`STALE_STAGING_WILL_BE_RECONCILED`); staging that belongs to another delivery is never removed
(`STAGING_NOT_OWNED`). An existing final package is never overwritten (`FINAL_PACKAGE_EXISTS`): pick a new
`packageName` or remove the old package yourself.

## Recipes

- Stored by the broker in `<home>\recipes\` as content-addressed JSON (sha-256 over the canonical recipe), so they
  work while Revit is closed. Saving an unchanged recipe is a no-op.
- `save_recipe` with `expected_sha` overwrites only that version; if someone saved a newer one meanwhile, it refuses
  instead of replacing their decisions.
- `deliveryId` is per run; `projectId` is the library key; `recipeVersion` is your own label.
- `coordinates.preserveLinkTransforms` and `packagedLinkPathType: "relative"` and the five `qa.require*` flags are
  fixed to `true`/`"relative"`; they are in the recipe so the plan states them.

## Paths and rights

Source and destination paths must be visible to the Revit process (local or UNC). Sources may not lie inside the
output, and two sources may not map to the same output name. The account running Revit needs write access to
`destinationRoot`.

## Testing

`model_delivery {"op":"fixture","template":"<.rte>","folder":"<new empty folder>","id":"fx1"}` creates two linked
central models and a matching recipe in a new folder. It is for disposable tests only (it always asks first). The e2e
scenario S97 uses it to run inspect, preview, execute and the QA checks on Revit 2024 and 2027.
