import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const smokeSource = readFileSync(path.join(repositoryRoot, "scripts", "live-smoke-revit.mjs"), "utf8");
const workflowSource = readFileSync(
  path.join(repositoryRoot, ".github", "workflows", "live-revit-smoke.yml"),
  "utf8"
);

test("element-type live acceptance allows the workflow's Revit 2024 and 2027 targets", () => {
  assert.match(workflowSource, /REVIT_YEAR -notin @\("2024", "2027"\)/);
  assert.match(smokeSource, /assertExpectedRevitYear\(status, options\.expectedRevitYear\);/);
  assert.doesNotMatch(smokeSource, /assertExpectedRevitYear\(status, "2024"\)/);
  assert.doesNotMatch(smokeSource, /Revit 2024/);
  assert.doesNotMatch(workflowSource, /require_element_type_edit[^\n]*Revit 2024 project/);
});

test("element-type live acceptance retains destructive safeguards and release coverage", () => {
  assert.match(
    smokeSource,
    /--require-element-type-edit requires --acknowledge-disposable-model because it commits multiple model-changing transactions\./
  );
  assert.match(
    smokeSource,
    /--require-element-type-edit requires --wall-length-mm of at least \$\{ELEMENT_TYPE_ACCEPTANCE_WIDTH_MM \+ 1200\}\./
  );
  assert.match(
    workflowSource,
    /if \(\$env:REQUIRE_ELEMENT_TYPE_EDIT -ne "true"\) \{\s*throw "readiness_profile=\$profile requires require_element_type_edit=true/
  );
  assert.match(workflowSource, /"-RequireElementTypeEdit", "-AcknowledgeDisposableModel"/);
  assert.match(
    workflowSource,
    /require_element_type_edit=true requires revit_model_path for a disposable project matching revit_year\./
  );
  assert.match(
    workflowSource,
    /require_element_type_edit=true refuses an already-running Revit session\./
  );
});
