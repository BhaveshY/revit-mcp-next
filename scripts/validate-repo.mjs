import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";

const root = process.cwd();
const bannedPatterns = [
  /\bnpx\b/i,
  /npm\s+install\s+-g/i,
];

const checkedExtensions = new Set([".json", ".toml", ".md", ".cmd", ".ps1", ".yml", ".yaml", ".dyn"]);
const failures = [];

validateAddinOperationKindGuard();
validateRevitVersionConfiguration();
validateMcpSdkConfiguration();

for (const file of walk(root)) {
  if (
    file.includes("\\node_modules\\") ||
    file.includes("\\.git\\") ||
    file.includes("\\artifacts\\")
  ) continue;
  const ext = file.slice(file.lastIndexOf("."));
  if (!checkedExtensions.has(ext)) continue;
  const text = readFileSync(file, "utf8");
  for (const pattern of bannedPatterns) {
    if (pattern.test(text) && !file.endsWith("validate-repo.mjs")) {
      failures.push(`${file}: contains banned startup pattern ${pattern}`);
    }
  }
}

if (failures.length) {
  console.error(failures.join("\n"));
  process.exit(1);
}

function validateAddinOperationKindGuard() {
  const handlerPath = join(root, "addin", "RevitMcpNext.Addin", "Revit", "RevitExternalEventHandler.cs");
  const handler = readFileSync(handlerPath, "utf8");
  const expected = {
    status: "read",
    list_documents: "read",
    create_project_from_template: "write",
    create_model_delivery_fixture: "write",
    get_levels: "read",
    get_views: "read",
    get_sheets: "read",
    get_schedules: "read",
    get_schedule_fields: "read",
    get_current_view: "read",
    get_current_view_elements: "read",
    get_selection: "read",
    analyze_model: "read",
    get_model_readiness: "read",
    get_model_context: "read",
    get_material_quantities: "read",
    get_warnings: "read",
    get_rooms: "read",
    catalog: "read",
    query: "read",
    describe_parameters: "read",
    inspect_model_delivery: "read",
    preview_model_delivery: "preview",
    execute_model_delivery: "destructive",
    get_model_delivery_status: "read",
    cancel_model_delivery: "debug",
    preview_change_set: "preview",
    apply_change_set: "write",
    cancel_request: "debug",
  };

  if (!handler.includes("OPERATION_KIND_MISMATCH")) {
    failures.push(`${handlerPath}: add-in dispatch must reject operationKind mismatches.`);
  }

  const mapBlock = handler.match(/ExpectedOperationKinds\s*=\s*[\s\S]*?\};\s*private readonly RevitRequestQueue/);
  if (!mapBlock) {
    failures.push(`${handlerPath}: ExpectedOperationKinds map was not found.`);
    return;
  }

  const actual = Object.fromEntries([...mapBlock[0].matchAll(/\["([^"]+)"\]\s*=\s*"([^"]+)"/g)].map((match) => [match[1], match[2]]));
  for (const [operation, kind] of Object.entries(expected)) {
    if (actual[operation] !== kind) {
      failures.push(`${handlerPath}: ExpectedOperationKinds must map ${operation} to ${kind}.`);
    }
  }
  for (const operation of Object.keys(actual)) {
    if (!(operation in expected)) {
      failures.push(`${handlerPath}: ExpectedOperationKinds contains unexpected operation ${operation}.`);
    }
  }

  const switchBlock = handler.match(/switch \(request\.Operation\)[\s\S]*?default:/);
  if (!switchBlock) {
    failures.push(`${handlerPath}: request.Operation dispatch switch was not found.`);
    return;
  }

  const dispatchOperations = [...switchBlock[0].matchAll(/case "([^"]+)":/g)].map((match) => match[1]);
  for (const operation of dispatchOperations) {
    if (!(operation in expected)) {
      failures.push(`${handlerPath}: dispatch operation ${operation} is missing from ExpectedOperationKinds validation.`);
    }
  }
  for (const operation of Object.keys(expected)) {
    if (!dispatchOperations.includes(operation)) {
      failures.push(`${handlerPath}: ExpectedOperationKinds operation ${operation} is not handled by request.Operation dispatch.`);
    }
  }
}

function validateRevitVersionConfiguration() {
  const projectPath = join(root, "addin", "RevitMcpNext.Addin", "RevitMcpNext.Addin.csproj");
  const buildPath = join(root, "scripts", "build-addin.ps1");
  const packagePath = join(root, "scripts", "package-release.ps1");
  const installerPath = join(root, "installer", "install-windows.ps1");
  const matrixPath = join(root, "docs", "version-matrix.md");
  const liveWorkflowPath = join(root, ".github", "workflows", "live-revit-smoke.yml");
  const localReleaseSmokePath = join(root, "scripts", "local-release-smoke.ps1");
  const pyRevitSmokePath = join(root, "scripts", "run-pyrevit-host-smoke.ps1");
  const pyRevitHostsPath = join(root, "scripts", "ensure-pyrevit-hosts-cache.ps1");

  const project = readFileSync(projectPath, "utf8");
  requireText(projectPath, project, [
    "<RevitYear Condition=",
    "'$(RevitYear)' == '2027'",
    "net10.0-windows",
    "'$(RevitYear)' == '2024' and '$(TargetFramework)' != 'net48'",
    "'$(RevitYear)' == '2027' and '$(TargetFramework)' != 'net10.0-windows'",
  ]);

  const build = readFileSync(buildPath, "utf8");
  requireText(buildPath, build, [
    "[ValidateSet(2024, 2027)]",
    "-p:RevitYear=$RevitYear",
    '"net10.0-windows"',
    '"artifacts\\addin"',
  ]);

  const packageScript = readFileSync(packagePath, "utf8");
  requireText(packagePath, packageScript, [
    "$year -notin @(2024, 2027)",
    'Join-Path $payloadRoot "addin\\$year"',
    "addinArtifacts",
    'path = "payload/addin/$_/RevitMcpNext.Addin.dll"',
  ]);

  const installer = readFileSync(installerPath, "utf8");
  requireText(installerPath, installer, [
    "$year -notin @(2024, 2027)",
    'Join-Path $installedAddin "$year"',
    "addinAssemblyPaths",
    'Join-Path $installedAddin "$year\\RevitMcpNext.Addin.dll"',
  ]);
  if (installer.includes('$assemblyPath = Join-Path $installedAddin "RevitMcpNext.Addin.dll"')) {
    failures.push(`${installerPath}: manifests must resolve the year-specific installed add-in DLL.`);
  }

  const matrix = readFileSync(matrixPath, "utf8");
  requireText(matrixPath, matrix, [
    "| 2027 | .NET 10 / `net10.0-windows` |",
    "First-class staged-package target",
    "Both supported years use the same private Revit bridge protocol",
    "Revit 2025 and 2026 remain excluded",
  ]);

  const liveWorkflow = readFileSync(liveWorkflowPath, "utf8");
  requireText(liveWorkflowPath, liveWorkflow, [
    "revit_year must be 2024 or 2027",
    "-RevitYear $env:REVIT_YEAR",
    "expectedApiMajor",
    "revitApiAssemblyVersion",
    "advanced_options_json",
    'installRoot = "INSTALL_ROOT_INPUT"',
    "advanced_options_json contains unsupported key",
    "foreach ($environmentName in $allowed.Values)",
    "advanced_options_json value must be a string or null",
  ]);
  const workflowDispatchBlock = liveWorkflow.match(/workflow_dispatch:\s*\n\s+inputs:\s*\n([\s\S]*?)\npermissions:/);
  if (!workflowDispatchBlock) {
    failures.push(`${liveWorkflowPath}: workflow_dispatch inputs block was not found.`);
  } else {
    const inputCount = [...workflowDispatchBlock[1].matchAll(/^ {6}[a-zA-Z0-9_]+:/gm)].length;
    if (inputCount > 25) {
      failures.push(`${liveWorkflowPath}: workflow_dispatch defines ${inputCount} inputs; GitHub allows at most 25.`);
    }
  }

  const localReleaseSmoke = readFileSync(localReleaseSmokePath, "utf8");
  requireText(localReleaseSmokePath, localReleaseSmoke, [
    "[ValidateSet(2024, 2027)]",
    '"build:addin", "--", "-RevitYear", "$RevitYear"',
    "[switch] $RequireElementTypeEdit",
    "[switch] $AcknowledgeDisposableModel",
    "requireElementTypeEdit = [bool] $RequireElementTypeEdit",
    "acknowledgeDisposableModel = [bool] $AcknowledgeDisposableModel",
    '$smokeArgs += @("-RequireElementTypeEdit", "-AcknowledgeDisposableModel")',
  ]);

  const pyRevitSmoke = readFileSync(pyRevitSmokePath, "utf8");
  requireText(pyRevitSmokePath, pyRevitSmoke, [
    '$hostsArgs = @("-RevitYear", "$RevitYear")',
  ]);

  const pyRevitHosts = readFileSync(pyRevitHostsPath, "utf8");
  requireText(pyRevitHostsPath, pyRevitHosts, [
    "[ValidateSet(2024, 2027)]",
    'if ($RevitYear -eq 2024) { "24.0.0.0" } else { "27.0.0.0" }',
    "Revit 2027 host cache seeding requires explicit -Builds values",
    "revitYear = $RevitYear",
    "version = $resolvedVersion",
  ]);
}

function validateMcpSdkConfiguration() {
  const brokerPackagePath = join(root, "broker", "package.json");
  const brokerEntryPath = join(root, "broker", "src", "index.ts");
  const brokerServerPath = join(root, "broker", "src", "server.ts");
  const liveSmokePath = join(root, "scripts", "live-smoke-revit.mjs");

  const brokerPackage = JSON.parse(readFileSync(brokerPackagePath, "utf8"));
  const dependencies = brokerPackage.dependencies ?? {};
  for (const packageName of [
    "@modelcontextprotocol/client",
    "@modelcontextprotocol/core",
    "@modelcontextprotocol/server",
  ]) {
    if (dependencies[packageName] !== "2.0.0") {
      failures.push(`${brokerPackagePath}: ${packageName} must be pinned to 2.0.0.`);
    }
  }
  if (dependencies["@modelcontextprotocol/sdk"]) {
    failures.push(`${brokerPackagePath}: the retired v1 SDK package must not be a dependency.`);
  }

  const brokerEntry = readFileSync(brokerEntryPath, "utf8");
  requireText(brokerEntryPath, brokerEntry, [
    'import { serveStdio } from "@modelcontextprotocol/server/stdio"',
    '{ legacy: "serve" }',
  ]);

  const brokerServer = readFileSync(brokerServerPath, "utf8");
  requireText(brokerServerPath, brokerServer, [
    '"2026-07-28"',
    "SUPPORTED_PROTOCOL_VERSIONS",
    '"server/discover"',
    '"tools/list"',
    'cacheScope: "private"',
  ]);

  const liveSmoke = readFileSync(liveSmokePath, "utf8");
  requireText(liveSmokePath, liveSmoke, [
    'requireFromScript.resolve("@modelcontextprotocol/client"',
    'requireFromScript.resolve("@modelcontextprotocol/client/stdio"',
  ]);
  if (liveSmoke.includes("@modelcontextprotocol/sdk")) {
    failures.push(`${liveSmokePath}: the live smoke must resolve the SDK v2 client package.`);
  }
}

function requireText(path, text, expectedFragments) {
  for (const fragment of expectedFragments) {
    if (!text.includes(fragment)) {
      failures.push(`${path}: missing required validation marker ${JSON.stringify(fragment)}.`);
    }
  }
}

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    const stat = statSync(path);
    if (stat.isDirectory()) {
      if (name === "node_modules" || name === ".git" || name === "artifacts") {
        continue;
      }
      yield* walk(path);
    } else {
      yield path;
    }
  }
}
