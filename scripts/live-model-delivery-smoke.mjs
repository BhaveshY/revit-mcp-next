#!/usr/bin/env node

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { pathToFileURL } from "node:url";

const options = parseArgs(process.argv.slice(2));
const installRoot = options.installRoot ?? path.join(process.env.LOCALAPPDATA ?? "", "RevitMcpNext");
const expectedYear = required(options, "expectedYear");
const templatePath = path.resolve(required(options, "templatePath"));
const fixtureRoot = path.resolve(required(options, "fixtureRoot"));
const summaryPath = options.summaryPath ? path.resolve(options.summaryPath) : path.join(fixtureRoot, "live-smoke-summary.json");
const sessionId = `model-delivery-live-${expectedYear}-${process.pid}-${Date.now()}`;
const timeoutMs = 300_000;
let deliveryTarget;

const { NamedPipeBridgeClient } = await import(
  pathToFileURL(path.join(installRoot, "broker", "dist", "src", "ipc", "NamedPipeBridgeClient.js")).href
);
const { makeRequest } = await import(
  pathToFileURL(path.join(installRoot, "broker", "dist", "src", "ipc", "RequestFactory.js")).href
);

const authToken = readAuthToken(path.join(installRoot, "config", "auth.env"));
const bridge = new NamedPipeBridgeClient({
  pipeName: "revit-mcp-next",
  sessionId,
  defaultTimeoutMs: timeoutMs,
  authToken,
});

const summary = {
  schemaVersion: 1,
  status: "failed",
  expectedYear,
  fixtureRoot,
  startedAtUtc: new Date().toISOString(),
  checks: {},
};

try {
  const status = await call("status", "status", "read", {});
  assert(status.connected === true, "Bridge is not connected.");
  assert(String(status.revit?.version) === expectedYear, `Expected Revit ${expectedYear}, received ${status.revit?.version}.`);
  summary.revit = status.revit;
  summary.addinAssembly = status.addinAssembly;
  assert(status.instanceId, "Status did not report a runtime instance ID.");
  assert(status.activeDocument?.fingerprint, "Open and target a disposable pilot control RVT before running Model Delivery smoke.");
  deliveryTarget = {
    instanceId: status.instanceId,
    documentFingerprint: status.activeDocument.fingerprint,
    expectedGeneration: status.activeDocument.generation,
  };
  summary.target = deliveryTarget;

  assert(!fs.existsSync(fixtureRoot), `Fixture root already exists: ${fixtureRoot}`);
  const fixture = await call("createModelDeliveryFixture", "create_model_delivery_fixture", "write", {
    templatePath,
    fixtureRoot,
    fixtureId: `serious-project-${expectedYear}-${Date.now()}`,
    confirm: true,
  });
  assert(fixture.sourcePaths.length === 2, "Fixture did not create two source models.");
  const sourceHashesBefore = Object.fromEntries(fixture.sourcePaths.map((sourcePath) => [sourcePath, sha256(sourcePath)]));
  summary.fixture = fixture;
  summary.checks.fixtureCreated = true;

  const firstInspection = await call("inspectModelDelivery", "inspect_model_delivery", "read", {
    sourcePaths: fixture.sourcePaths,
  });
  assert(firstInspection.mode === "firstTime", "First inspection did not report firstTime mode.");
  assert(firstInspection.models.every((model) => model.isWorkshared === true), "Fixture sources are not workshared central models.");
  assert(firstInspection.missingDecisions.length > 0, "First-time inspection did not ask for missing decisions.");
  summary.checks.firstTimeInspection = {
    missingDecisionKeys: firstInspection.missingDecisions.map((decision) => decision.key),
  };

  const repeatInspection = await call("inspectModelDelivery", "inspect_model_delivery", "read", {
    sourcePaths: fixture.sourcePaths,
    previousRecipe: fixture.recipe,
  });
  assert(repeatInspection.mode === "repeat", "Recipe-backed inspection did not report repeat mode.");
  assert(repeatInspection.recipeReusable === true, "Unchanged recipe was not reusable.");
  assert(repeatInspection.detectedChanges.length === 0, "Unchanged fixture unexpectedly reported recipe differences.");
  summary.checks.repeatInspection = { recipeReusable: true, detectedChanges: [] };

  const cloudResponse = await rawCall("inspectModelDelivery", "inspect_model_delivery", "read", {
    sourcePaths: ["Autodesk Docs://Disposable/CloudModel.rvt"],
  });
  assert(cloudResponse.ok === false, "Cloud source was not rejected.");
  assert(cloudResponse.error?.code === "CLOUD_MODEL_UNSUPPORTED", `Unexpected cloud rejection: ${cloudResponse.error?.code}.`);
  summary.checks.cloudRejected = cloudResponse.error;

  const invalidRecipe = clone(fixture.recipe);
  invalidRecipe.deliveryId += "-invalid";
  invalidRecipe.packageName += "-invalid";
  invalidRecipe.sourceModels[0].startViewName = "VIEW-THAT-DOES-NOT-EXIST";
  const invalidPreview = await call("previewModelDelivery", "preview_model_delivery", "preview", { recipe: invalidRecipe });
  assert(invalidPreview.ready === false, "Invalid recipe was unexpectedly ready.");
  assert(invalidPreview.blockers.some((blocker) => blocker.code === "PROTECTED_VIEW_NOT_FOUND"), "Missing protected view was not blocked.");
  assert(!fs.existsSync(invalidPreview.packagePath), "Invalid preview published a package.");
  assert(!fs.existsSync(invalidPreview.stagingPath), "Invalid preview wrote staging content.");
  summary.checks.blockedInvalidInput = invalidPreview.blockers;

  const failurePreview = await previewReady(fixture.recipe);
  assertInside(fixtureRoot, failurePreview.packagePath);
  fs.mkdirSync(failurePreview.packagePath, { recursive: false });
  const collisionSentinel = path.join(failurePreview.packagePath, "pre-existing-owner.txt");
  fs.writeFileSync(collisionSentinel, "This directory predates connector publication.\n", "utf8");
  const failedJob = await executeAndWait(fixture.recipe, failurePreview);
  assert(failedJob.state === "failed", `Collision run ended as ${failedJob.state}, expected failed.`);
  assert(failedJob.published === false, "Collision run reported published=true.");
  assert(failedJob.errors.some((error) => error.code === "FINAL_PACKAGE_EXISTS"), "Collision failure did not report FINAL_PACKAGE_EXISTS.");
  assert(fs.existsSync(collisionSentinel), "Connector overwrote the pre-existing collision sentinel.");
  assert(fs.readdirSync(failurePreview.packagePath).length === 1, "Connector partially published into the pre-existing package directory.");
  summary.checks.failureNoOverwrite = {
    state: failedJob.state,
    errorCodes: failedJob.errors.map((error) => error.code),
    stagingRetained: fs.existsSync(failurePreview.stagingPath),
  };
  fs.rmSync(collisionSentinel, { force: false });
  fs.rmdirSync(failurePreview.packagePath);

  const retryPreview = await previewReady(fixture.recipe);
  assert(retryPreview.warnings.some((warning) => warning.code === "STALE_STAGING_WILL_BE_RECONCILED"), "Retry did not identify owned stale staging.");
  const succeededJob = await executeAndWait(fixture.recipe, retryPreview);
  assert(succeededJob.state === "succeeded", `Retry ended as ${succeededJob.state}.`);
  assert(succeededJob.published === true, "Successful retry did not report publication.");
  assert(fs.existsSync(succeededJob.packagePath), "Final package directory is missing.");
  assert(!fs.existsSync(succeededJob.stagingPath), "Owned staging remains after successful publication.");
  assert(succeededJob.models.every((model) => model.success && model.isWorkshared === false && !model.centralModelPath), "A delivered RVT failed standalone-model QA.");
  assert(succeededJob.models.every((model) => typeof model.outputSha256 === "string" && model.outputSha256.length === 64), "Output hashes are missing.");
  assert(succeededJob.models.every((model) => model.exports.length > 0 && model.exports.every(fs.existsSync)), "Required exports are missing.");
  const architectureResult = succeededJob.models.find((model) => model.modelId === "architecture");
  assert(architectureResult?.linksValidated === 1, "Architecture link was not validated from the final package location.");
  assert(architectureResult?.linkTransforms?.includes(fixture.expectedArchitectureLinkTransform), "Architecture link transform was not preserved through final-package validation.");
  assert(fs.existsSync(path.join(succeededJob.packagePath, "manifest.json")), "Manifest is missing.");
  assert(fs.existsSync(path.join(succeededJob.packagePath, "audit.json")), "Audit log is missing.");
  const manifest = JSON.parse(fs.readFileSync(path.join(succeededJob.packagePath, "manifest.json"), "utf8"));
  const audit = JSON.parse(fs.readFileSync(path.join(succeededJob.packagePath, "audit.json"), "utf8"));
  assert(manifest.published === true && audit.published === true, "Published reports do not record published=true.");
  summary.checks.safeRetrySucceeded = {
    finalValidationObserved: true,
    packagePath: succeededJob.packagePath,
    models: succeededJob.models,
    manifestPath: path.join(succeededJob.packagePath, "manifest.json"),
    auditPath: path.join(succeededJob.packagePath, "audit.json"),
  };

  const cancelRecipe = clone(fixture.recipe);
  cancelRecipe.deliveryId += "-cancel";
  cancelRecipe.packageName += "-cancel";
  const cancelPreview = await previewReady(cancelRecipe);
  const cancelAccepted = await execute(cancelRecipe, cancelPreview);
  await call("cancelModelDelivery", "cancel_model_delivery", "debug", {
    jobId: cancelAccepted.jobId,
    reason: "Live cancellation safety test.",
  });
  const cancelledJob = await waitForTerminal(cancelAccepted.jobId);
  assert(cancelledJob.state === "cancelled", `Cancellation run ended as ${cancelledJob.state}.`);
  assert(cancelledJob.published === false, "Cancelled run reported published=true.");
  assert(!fs.existsSync(cancelledJob.packagePath), "Cancelled run left a final package.");
  summary.checks.cancellation = { state: cancelledJob.state, packageAbsent: true };

  const sourceHashesAfter = Object.fromEntries(fixture.sourcePaths.map((sourcePath) => [sourcePath, sha256(sourcePath)]));
  assert(JSON.stringify(sourceHashesBefore) === JSON.stringify(sourceHashesAfter), "A production source file changed during delivery.");
  summary.checks.sourcesUnchanged = sourceHashesAfter;
  summary.status = "passed";
  summary.completedAtUtc = new Date().toISOString();
  writeSummary();
  console.log(JSON.stringify(summary, null, 2));
} catch (error) {
  summary.error = error instanceof Error ? { message: error.message, stack: error.stack } : { message: String(error) };
  summary.completedAtUtc = new Date().toISOString();
  writeSummary();
  throw error;
} finally {
  bridge.dispose();
}

async function previewReady(recipe) {
  const preview = await call("previewModelDelivery", "preview_model_delivery", "preview", { recipe });
  assert(preview.ready === true, `Preview was blocked: ${JSON.stringify(preview.blockers)}`);
  return preview;
}

async function execute(recipe, preview) {
  return call("executeModelDelivery", "execute_model_delivery", "destructive", {
    recipe,
    previewId: preview.previewId,
    planHash: preview.planHash,
    expiresAt: preview.expiresAt,
    confirm: true,
  });
}

async function executeAndWait(recipe, preview) {
  const accepted = await execute(recipe, preview);
  return waitForTerminal(accepted.jobId);
}

async function waitForTerminal(jobId) {
  const deadline = Date.now() + 15 * 60_000;
  let lastState = "";
  while (Date.now() < deadline) {
    const status = await call("getModelDeliveryStatus", "get_model_delivery_status", "read", { jobId });
    if (status.state !== lastState) {
      console.error(`[${expectedYear}] ${jobId}: ${status.state} (${status.progressPercent}%)`);
      lastState = status.state;
    }
    if (status.terminal) return status;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`Timed out waiting for delivery job ${jobId}.`);
}

async function call(method, operation, operationKind, payload) {
  const response = await rawCall(method, operation, operationKind, payload);
  if (!response.ok) throw new Error(`${operation} failed: ${response.error?.code}: ${response.error?.message}`);
  return response.data;
}

async function rawCall(method, operation, operationKind, payload) {
  const targetScoped = operation === "inspect_model_delivery" || operation === "preview_model_delivery" || operation === "execute_model_delivery";
  const request = makeRequest(
    sessionId,
    operation,
    operationKind,
    targetScoped ? { ...payload, ...deliveryTarget } : payload,
    timeoutMs
  );
  if (targetScoped) {
    request.instanceId = deliveryTarget.instanceId;
    request.documentFingerprint = deliveryTarget.documentFingerprint;
    request.expectedGeneration = deliveryTarget.expectedGeneration;
  }
  return bridge[method](request);
}

function readAuthToken(authPath) {
  const line = fs.readFileSync(authPath, "utf8").split(/\r?\n/).find((entry) => entry.startsWith("REVIT_MCP_NEXT_AUTH_TOKEN="));
  if (!line) throw new Error(`Auth token was not found in ${authPath}.`);
  return line.slice(line.indexOf("=") + 1).trim();
}

function sha256(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function assertInside(parent, child) {
  const relative = path.relative(path.resolve(parent), path.resolve(child));
  assert(relative && !relative.startsWith("..") && !path.isAbsolute(relative), `Unsafe fixture path: ${child}`);
}

function writeSummary() {
  fs.mkdirSync(path.dirname(summaryPath), { recursive: true });
  fs.writeFileSync(summaryPath, `${JSON.stringify(summary, null, 2)}\n`, "utf8");
}

function required(values, name) {
  const value = values[name];
  if (!value) throw new Error(`Missing --${name.replace(/[A-Z]/g, (match) => `-${match.toLowerCase()}`)}.`);
  return value;
}

function parseArgs(args) {
  const values = {};
  for (let index = 0; index < args.length; index += 1) {
    const key = args[index];
    if (!key.startsWith("--")) throw new Error(`Unexpected argument: ${key}`);
    const name = key.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
    const value = args[index + 1];
    if (!value || value.startsWith("--")) throw new Error(`Missing value for ${key}.`);
    values[name] = value;
    index += 1;
  }
  return values;
}
