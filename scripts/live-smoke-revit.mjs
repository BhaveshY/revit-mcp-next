#!/usr/bin/env node

import { spawn } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import process from "node:process";
import { fileURLToPath, pathToFileURL } from "node:url";

const DEFAULT_WALL_LENGTH_MM = 4000;
const DEFAULT_MOVE_Y_MM = 250;
const DEFAULT_WALL_HEIGHT_MM = 3000;
const DEFAULT_TRANSACTION_PREFIX = "Revit MCP Next smoke";
const REQUIRED_TOOLS = [
  "revit.status",
  "revit.get_levels",
  "revit.get_views",
  "revit.get_sheets",
  "revit.get_current_view",
  "revit.get_current_view_elements",
  "revit.get_selection",
  "revit.analyze_model",
  "revit.get_model_readiness",
  "revit.get_model_context",
  "revit.get_material_quantities",
  "revit.get_warnings",
  "revit.get_rooms",
  "revit.catalog",
  "revit.query",
  "revit.describe_parameters",
  "revit.preview_change_set",
  "revit.apply_change_set",
  "revit.cancel_request",
];
const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));

main().catch((error) => {
  console.error("");
  console.error("Live Revit smoke failed.");
  console.error(formatError(error));
  process.exitCode = 1;
});

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    printHelp();
    return;
  }

  validateOptions(options);

  const launcherPath = resolveLauncherPath(options.launcherPath);
  if (!existsSync(launcherPath)) {
    throw new Error(
      [
        `MCP launcher was not found at: ${launcherPath}`,
        "Install Revit MCP Next first, or pass --launcher-path with the launcher to use.",
        "Default launcher: %LOCALAPPDATA%\\RevitMcpNext\\launch-revit-mcp-next.cmd",
      ].join("\n")
    );
  }

  console.log("Revit MCP Next live smoke");
  console.log(`Launcher: ${launcherPath}`);
  console.log(`Wall length: ${options.wallLengthMm} mm`);
  console.log(`Wall height: ${options.wallHeightMm} mm`);
  console.log(`Move Y: ${options.moveYMm} mm`);
  console.log(`Transaction prefix: ${options.transactionPrefix}`);
  console.log(`Require type change: ${options.requireTypeChange ? "yes" : "no"}`);
  console.log(`Require room tag: ${options.requireRoomTag ? "yes" : "no"}`);
  console.log(`Require element tag: ${options.requireElementTag ? "yes" : "no"}`);
  const roomTagSelector = makeTagTypeSelector(options.roomTagTypeId, options.roomTagTypeNameContains);
  const elementTagSelector = makeTagTypeSelector(options.elementTagTypeId, options.elementTagTypeNameContains);
  if (roomTagSelector) {
    console.log(`Room tag selector: ${formatTagTypeSelector(roomTagSelector)}`);
  }
  if (elementTagSelector) {
    console.log(`Element tag selector: ${formatTagTypeSelector(elementTagSelector)}`);
  }
  if (options.expectedRevitYear) {
    console.log(`Expected Revit year: ${options.expectedRevitYear}`);
  }
  if (options.summaryPath) {
    console.log(`Smoke summary: ${path.resolve(options.summaryPath)}`);
  }
  if (options.statusOnly) {
    console.log("Mode: status-only");
  }
  if (options.documentFingerprint) {
    console.log(`Document fingerprint: ${options.documentFingerprint}`);
  }

  const { Client, StdioClientTransport } = await loadMcpSdk(launcherPath);
  const transport = makeTransport(launcherPath, StdioClientTransport);
  const stderr = [];
  if (transport.stderr) {
    transport.stderr.on("data", (chunk) => {
      appendBounded(stderr, chunk.toString("utf8"), 8000);
    });
  }

  const client = new Client({ name: "revit-mcp-next-live-smoke", version: "0.1.0" });
  const summary = {
    schemaVersion: 1,
    status: "failed",
    startedAtUtc: new Date().toISOString(),
    completedAtUtc: null,
    launcherPath,
    mode: options.statusOnly ? "status-only" : "full",
    expectedRevitYear: options.expectedRevitYear ?? null,
    revit: null,
    addinAssembly: null,
    activeDocument: null,
    documentFingerprint: options.documentFingerprint ?? null,
    coveredTools: [],
    coveredOperations: [],
    skippedOperations: [],
    operationKindGuard: null,
    tagPreflight: null,
    requiredCoverage: {
      typeChange: options.requireTypeChange,
      roomTag: options.requireRoomTag,
      elementTag: options.requireElementTag,
    },
    tagSelectors: compactObject({
      room: roomTagSelector,
      element: elementTagSelector,
    }),
    result: null,
    error: null,
  };

  try {
    console.log("Connecting to MCP server over stdio...");
    await client.connect(transport);

    await verifyRequiredTools(client);

    const status = await callRequiredTool(client, "revit.status", {});
    summary.coveredTools.push("revit.status");
    assert(status.connected === true, "revit.status did not report a connected Revit bridge.");
    assert(status.activeDocument, "Revit is connected but there is no active project document.");
    summary.revit = status.revit ?? null;
    summary.addinAssembly = status.addinAssembly ? compactObject(status.addinAssembly) : null;
    assertExpectedRevitYear(status, options.expectedRevitYear);

    const activeDocument = status.activeDocument;
    summary.activeDocument = compactObject({
      title: activeDocument.title,
      fingerprint: activeDocument.fingerprint,
      generation: activeDocument.generation,
      path: activeDocument.path,
    });
    const documentFingerprint = options.documentFingerprint ?? activeDocument.fingerprint;
    summary.documentFingerprint = documentFingerprint ?? null;
    assert(
      documentFingerprint,
      "No document fingerprint was supplied and revit.status did not return one for the active document."
    );
    if (options.documentFingerprint) {
      assert(
        activeDocument.fingerprint === options.documentFingerprint,
        [
          "The supplied document fingerprint does not match the active Revit document.",
          `Active: ${activeDocument.fingerprint ?? "(missing)"}`,
          `Expected: ${options.documentFingerprint}`,
        ].join("\n")
      );
    }

    const startingGeneration = numericOrUndefined(activeDocument.generation);
    console.log(
      `Status OK: ${activeDocument.title ?? "(untitled)"} at generation ${startingGeneration ?? "(unknown)"}`
    );

    const cancelProbeRequestId = `live-smoke-noop-${Date.now()}`;
    const cancelProbe = await callRequiredTool(client, "revit.cancel_request", {
      requestId: cancelProbeRequestId,
      reason: "live smoke no-op probe",
    });
    summary.coveredTools.push("revit.cancel_request");
    assert(cancelProbe.cancelled === false, "revit.cancel_request no-op probe unexpectedly reported cancelled=true.");
    assert(
      cancelProbe.requestId === cancelProbeRequestId,
      "revit.cancel_request did not echo the no-op probe requestId."
    );
    assert(typeof cancelProbe.message === "string" && cancelProbe.message.length > 0, "revit.cancel_request did not return a message.");
    console.log(`Cancel no-op OK: ${cancelProbe.message}`);

    const operationKindProbe = await probeOperationKindMismatch(launcherPath);
    summary.coveredTools.push("revitctl.operation_kind_mismatch");
    summary.operationKindGuard = operationKindProbe;
    console.log(`Operation-kind guard OK: ${operationKindProbe.errorCode}`);

    if (options.statusOnly) {
      summary.status = "passed";
      summary.coveredTools = ["revit.status", "revit.cancel_request", "revitctl.operation_kind_mismatch"];
      console.log("Status-only smoke passed.");
      return;
    }

    const documentGuard = compactObject({
      documentFingerprint,
      expectedGeneration: startingGeneration,
    });

    const tagPreflight = await preflightRequiredTagCoverage(client, {
      documentFingerprint,
      requireRoomTag: options.requireRoomTag,
      requireElementTag: options.requireElementTag,
      roomTagSelector,
      elementTagSelector,
    });
    summary.tagPreflight = tagPreflight;
    if (tagPreflight.status !== "passed") {
      throw new Error(formatTagPreflightFailure(tagPreflight));
    }
    if (options.requireRoomTag || options.requireElementTag) {
      console.log("Required tag preflight OK");
    }

    const currentView = await callRequiredTool(client, "revit.get_current_view", {
      ...documentGuard,
      includeCropBox: false,
    });
    assert(currentView?.document?.fingerprint === documentFingerprint, "revit.get_current_view returned a different document.");
    assert(typeof currentView?.view?.id === "string" && currentView.view.id.length > 0, "revit.get_current_view did not return a view id.");
    console.log(`Current view OK: ${currentView.view.name ?? "(unnamed view)"} (${currentView.view.id})`);

    const views = await callRequiredTool(client, "revit.get_views", {
      ...documentGuard,
      filter: { isTemplate: false },
      preset: "summary",
      limit: 5,
      includeTotalCount: true,
    });
    assert(Array.isArray(views.items), "revit.get_views did not return an items array.");
    assert(views.returnedCount === views.items.length, "revit.get_views returnedCount did not match items length.");
    console.log(`Views OK: ${views.returnedCount}${views.totalCount === undefined ? "" : ` of ${views.totalCount}`} sample view(s)`);

    const sheets = await callRequiredTool(client, "revit.get_sheets", {
      ...documentGuard,
      preset: "summary",
      limit: 5,
      includeTotalCount: true,
    });
    assert(Array.isArray(sheets.items), "revit.get_sheets did not return an items array.");
    assert(sheets.returnedCount === sheets.items.length, "revit.get_sheets returnedCount did not match items length.");
    console.log(`Sheets OK: ${sheets.returnedCount}${sheets.totalCount === undefined ? "" : ` of ${sheets.totalCount}`} sample sheet(s)`);

    const currentViewElements = await callRequiredTool(client, "revit.get_current_view_elements", {
      ...documentGuard,
      preset: "summary",
      limit: 5,
      includeTotalCount: false,
    });
    assert(Array.isArray(currentViewElements.items), "revit.get_current_view_elements did not return an items array.");
    assert(currentViewElements.returnedCount === currentViewElements.items.length, "revit.get_current_view_elements returnedCount did not match items length.");
    assert(Number.isInteger(currentViewElements.limit) && currentViewElements.limit > 0, "revit.get_current_view_elements did not return a positive limit.");
    console.log(`Current view elements OK: ${currentViewElements.returnedCount} sample element(s)`);

    const selection = await callRequiredTool(client, "revit.get_selection", {
      ...documentGuard,
      preset: "summary",
      limit: 5,
      includeTotalCount: true,
    });
    assert(Array.isArray(selection.items), "revit.get_selection did not return an items array.");
    assert(selection.selection && typeof selection.selection.available === "boolean", "revit.get_selection did not return selection metadata.");
    console.log(`Selection OK: ${selection.selection.count ?? selection.items.length} selected element(s)`);

    const modelStats = await callRequiredTool(client, "revit.analyze_model", {
      ...documentGuard,
      bucketLimit: 10,
      maxElementsScanned: 10000,
    });
    assert(Number.isInteger(modelStats?.totals?.elements), "revit.analyze_model did not return totals.elements.");
    assert(Number.isInteger(modelStats?.scannedElements), "revit.analyze_model did not return scannedElements.");
    console.log(`Model analysis OK: scanned ${modelStats.scannedElements} element(s)`);

    const modelReadiness = await callRequiredTool(client, "revit.get_model_readiness", {
      ...documentGuard,
      includeHints: true,
    });
    assert(Array.isArray(modelReadiness.scenarios), "revit.get_model_readiness did not return a scenarios array.");
    assert(Number.isInteger(modelReadiness.readyCount), "revit.get_model_readiness did not return readyCount.");
    assert(
      modelReadiness.scenarios.some((scenario) => scenario.name === "familyPlacement"),
      "revit.get_model_readiness did not include familyPlacement scenario."
    );
    console.log(`Model readiness OK: ${modelReadiness.readyCount} of ${modelReadiness.totalCount} scenario(s) ready`);

    const modelContext = await callRequiredTool(client, "revit.get_model_context", {
      ...documentGuard,
      phaseLimit: 10,
      worksetLimit: 10,
      designOptionLimit: 10,
      revitLinkLimit: 10,
      includeTotalCount: true,
    });
    assert(modelContext.document?.fingerprint === documentFingerprint, "revit.get_model_context returned the wrong document fingerprint.");
    assert(modelContext.projectInfo && typeof modelContext.projectInfo === "object", "revit.get_model_context did not return projectInfo.");
    assert(Array.isArray(modelContext.phases?.items), "revit.get_model_context did not return phases.items.");
    assert(Number.isInteger(modelContext.phases?.returnedCount), "revit.get_model_context did not return phases.returnedCount.");
    assert(modelContext.worksets && typeof modelContext.worksets.available === "boolean", "revit.get_model_context did not return workset availability.");
    assert(Array.isArray(modelContext.designOptions?.items), "revit.get_model_context did not return designOptions.items.");
    assert(Array.isArray(modelContext.revitLinks?.items), "revit.get_model_context did not return revitLinks.items.");
    console.log(
      `Model context OK: ${modelContext.phases.returnedCount} phase(s), ${modelContext.worksets.returnedCount} workset(s), ${modelContext.designOptions.returnedCount} design option(s), ${modelContext.revitLinks.returnedCount} link(s)`
    );

    const materialQuantities = await callRequiredTool(client, "revit.get_material_quantities", {
      ...documentGuard,
      limit: 5,
      maxElementsScanned: 10000,
      includeTotalCount: true,
    });
    assert(Array.isArray(materialQuantities.items), "revit.get_material_quantities did not return an items array.");
    assert(materialQuantities.returnedCount === materialQuantities.items.length, "revit.get_material_quantities returnedCount did not match items length.");
    assert(materialQuantities.units?.area === "m2" && materialQuantities.units?.volume === "m3", "revit.get_material_quantities did not return normalized units.");
    console.log(`Material quantities OK: ${materialQuantities.returnedCount} material row(s)`);

    const modelWarnings = await callRequiredTool(client, "revit.get_warnings", {
      ...documentGuard,
      preset: "summary",
      limit: 5,
      includeTotalCount: true,
    });
    assert(Array.isArray(modelWarnings.items), "revit.get_warnings did not return an items array.");
    assert(modelWarnings.returnedCount === modelWarnings.items.length, "revit.get_warnings returnedCount did not match items length.");
    assert(Array.isArray(modelWarnings.fields), "revit.get_warnings did not return a field projection.");
    assert(modelWarnings.fields.includes("description"), "revit.get_warnings summary projection did not include description.");
    console.log(
      `Model warnings OK: ${modelWarnings.returnedCount}${modelWarnings.totalCount === undefined ? "" : ` of ${modelWarnings.totalCount}`} warning row(s)`
    );

    const levels = await callRequiredTool(client, "revit.get_levels", { documentFingerprint });
    assert(Array.isArray(levels), "revit.get_levels returned an unexpected result shape.");
    assert(levels.length > 0, "The active document has no levels to host a smoke wall.");
    const level = chooseLevel(levels);
    const levelElevationMm = numericOrDefault(level?.elevation?.value, 0);
    console.log(`Levels OK: using ${level.name ?? "(unnamed level)"} (${level.id}) at ${levelElevationMm} mm`);

    const wallTypes = await catalog(client, {
      kind: "elementTypes",
      filter: { classes: ["WallType"], categories: ["OST_Walls"] },
      preset: "compact",
      limit: 1,
      includeTotalCount: true,
    });
    assertCatalogPage(wallTypes, "elementTypes");
    assert(wallTypes.returnedCount >= 1, "revit.catalog did not return any wall types for this project.");
    const floorTypes = await catalog(client, {
      kind: "elementTypes",
      filter: { classes: ["FloorType"], categories: ["OST_Floors"] },
      preset: "compact",
      limit: 1,
      includeTotalCount: true,
    });
    assertCatalogPage(floorTypes, "elementTypes");
    assert(floorTypes.returnedCount >= 1, "revit.catalog did not return any floor types for this project.");
    console.log(
      `Catalog OK: wall type ${wallTypes.items[0].id}, floor type ${floorTypes.items[0].id}`
    );

    const runId = makeRunId();
    const smokeLevelElevationMm = chooseSmokeLevelElevationMm(levels, levelElevationMm);
    const levelName = `MCP Level ${runId}`;
    const levelOperation = {
      id: "create-smoke-level",
      type: "create_level",
      name: levelName,
      elevation: unitMm(smokeLevelElevationMm),
    };
    const levelTransaction = makeTransactionName(options.transactionPrefix, "create level", runId);
    const levelChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: startingGeneration,
      transactionName: levelTransaction,
      operations: [levelOperation],
    });

    const levelPreview = await previewChangeSet(client, levelChangeSet, "create_level");
    const levelApply = await applyChangeSet(client, levelChangeSet, levelPreview, "create_level");
    const levelChange = findChange(levelApply, "create_level");
    const smokeLevelId = getCreatedElementId(levelChange);
    assert(smokeLevelId, "create_level applied but the created level element ID was not returned.");
    await queryElementById(client, "Level", smokeLevelId);
    console.log(`Create level OK: ${levelName} (${smokeLevelId}) at ${smokeLevelElevationMm} mm`);

    const gridName = `MCP-${runId}`;
    const gridGeneration = numericOrUndefined(levelApply.generation);
    const gridOperation = {
      id: "create-smoke-grid",
      type: "create_grid",
      name: gridName,
      start: pointMm(-1000, -1000, smokeLevelElevationMm),
      end: pointMm(options.wallLengthMm + 1000, -1000, smokeLevelElevationMm),
    };
    const gridTransaction = makeTransactionName(options.transactionPrefix, "create grid", runId);
    const gridChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: gridGeneration,
      transactionName: gridTransaction,
      operations: [gridOperation],
    });

    const gridPreview = await previewChangeSet(client, gridChangeSet, "create_grid");
    const gridApply = await applyChangeSet(client, gridChangeSet, gridPreview, "create_grid");
    const gridChange = findChange(gridApply, "create_grid");
    const gridId = getCreatedElementId(gridChange);
    assert(gridId, "create_grid applied but the created grid element ID was not returned.");
    await queryElementById(client, "Grid", gridId);
    console.log(`Create grid OK: ${gridName} (${gridId})`);

    const floorGeneration = numericOrUndefined(gridApply.generation);
    await previewBlockedChangeSet(
      client,
      compactObject({
        documentFingerprint,
        expectedGeneration: floorGeneration,
        transactionName: makeTransactionName(options.transactionPrefix, "duplicate grid preview", runId),
        operations: [{ ...gridOperation, id: "duplicate-smoke-grid" }],
      }),
      "duplicate create_grid"
    );

    const floorOperation = {
      id: "create-smoke-floor",
      type: "create_floor",
      levelId: String(smokeLevelId),
      outline: [
        pointMm(0, -options.wallLengthMm - 1000, smokeLevelElevationMm),
        pointMm(options.wallLengthMm, -options.wallLengthMm - 1000, smokeLevelElevationMm),
        pointMm(options.wallLengthMm, -1000, smokeLevelElevationMm),
        pointMm(0, -1000, smokeLevelElevationMm),
      ],
      structural: false,
    };
    const floorTransaction = makeTransactionName(options.transactionPrefix, "create floor", runId);
    const floorChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: floorGeneration,
      transactionName: floorTransaction,
      operations: [floorOperation],
    });

    const floorPreview = await previewChangeSet(client, floorChangeSet, "create_floor");
    const floorApply = await applyChangeSet(client, floorChangeSet, floorPreview, "create_floor");
    const floorChange = findChange(floorApply, "create_floor");
    const floorId = getCreatedElementId(floorChange);
    assert(floorId, "create_floor applied but the created floor element ID was not returned.");
    await queryElementById(client, "Floor", floorId);
    console.log(`Create floor OK: element ${floorId}`);

    const createWallGeneration = numericOrUndefined(floorApply.generation);
    const wallOperation = {
      id: "create-smoke-wall",
      type: "create_wall",
      levelId: String(smokeLevelId),
      start: pointMm(0, 0, smokeLevelElevationMm),
      end: pointMm(options.wallLengthMm, 0, smokeLevelElevationMm),
      height: unitMm(options.wallHeightMm),
      structural: false,
      flip: false,
    };
    const createTransaction = makeTransactionName(options.transactionPrefix, "create wall", runId);
    const createChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: createWallGeneration,
      transactionName: createTransaction,
      operations: [wallOperation],
    });

    const createPreview = await previewChangeSet(client, createChangeSet, "create_wall");
    const createApply = await applyChangeSet(client, createChangeSet, createPreview, "create_wall");
    const createChange = findChange(createApply, "create_wall");
    const wallId = getCreatedElementId(createChange);
    assert(wallId, "create_wall applied but the created wall element ID was not returned.");
    console.log(`Create wall OK: element ${wallId}`);

    const queriedWall = await queryWallById(client, wallId);
    const wallUniqueId = String(queriedWall.uniqueId ?? "");
    assert(wallUniqueId.length > 0, "Created smoke wall query did not return a uniqueId.");
    console.log(`Query OK: wall ${queriedWall.id} (${queriedWall.name ?? queriedWall.class ?? "Wall"})`);

    const wallGeometry = await queryElementGeometryById(client, wallId);
    assert(wallGeometry.location, "geometrySummary did not return location for the created wall.");
    assert(wallGeometry.bounds, "geometrySummary did not return bounds for the created wall.");
    console.log(`Geometry summary OK: wall ${wallId} location/bounds returned`);

    await previewBlockedChangeSet(
      client,
      compactObject({
        documentFingerprint,
        expectedGeneration: numericOrUndefined(createApply.generation),
        transactionName: makeTransactionName(options.transactionPrefix, "mismatched wall uniqueId preview", runId),
        operations: [
          {
            id: "mismatched-wall-unique-id-preview",
            type: "set_parameter",
            elementId: wallId,
            expectedUniqueId: `${wallUniqueId}-mismatch`,
            parameterName: "Comments",
            value: "blocked-by-unique-id-guard",
          },
        ],
      }),
      "mismatched wall uniqueId guard"
    );

    const wallParameters = await callRequiredTool(client, "revit.describe_parameters", {
      documentFingerprint,
      expectedGeneration: numericOrUndefined(createApply.generation),
      filter: { elementIds: [String(wallId)] },
      preset: "full",
      limit: 1,
      parameterLimit: 40,
      includeTotalCount: true,
    });
    assert(Array.isArray(wallParameters.items), "revit.describe_parameters did not return an items array.");
    assert(wallParameters.items.length === 1, "revit.describe_parameters did not return the created wall parameter set.");
    assert(Array.isArray(wallParameters.items[0].parameters), "revit.describe_parameters did not return parameter metadata.");
    console.log(`Parameter discovery OK: wall ${wallId} has ${wallParameters.items[0].parameterCount} described parameter(s)`);

    const familyPlacementResult = await tryPlaceFamilyInstance(client, {
      documentFingerprint,
      expectedGeneration: numericOrUndefined(createApply.generation),
      transactionPrefix: options.transactionPrefix,
      runId,
      wallId,
      wallUniqueId,
      levelId: String(smokeLevelId),
      levelElevationMm: smokeLevelElevationMm,
      wallLengthMm: options.wallLengthMm,
    });
    const familyPlacementApply = familyPlacementResult.apply;
    const familyInstanceId = familyPlacementApply ? getCreatedElementId(findChange(familyPlacementApply, "place_family_instance")) : undefined;
    if (familyPlacementApply) {
      assert(familyInstanceId, "place_family_instance applied but the created family instance ID was not returned.");
      await queryElementById(client, "FamilyInstance", familyInstanceId);
      console.log(`Place family instance OK: element ${familyInstanceId}`);
    } else {
      console.log(`Place family instance skipped: ${familyPlacementResult.reason}`);
    }

    const roomOriginX = 0;
    const roomOriginY = options.wallLengthMm + 1500;
    const roomWidthMm = options.wallLengthMm;
    const roomDepthMm = Math.max(2500, Math.min(options.wallLengthMm, 5000));
    const roomMinX = roomOriginX;
    const roomMaxX = roomOriginX + roomWidthMm;
    const roomMinY = roomOriginY;
    const roomMaxY = roomOriginY + roomDepthMm;
    const roomBoundaryOperations = [
      {
        id: "create-room-boundary-south",
        type: "create_wall",
        levelId: String(smokeLevelId),
        start: pointMm(roomMinX, roomMinY, smokeLevelElevationMm),
        end: pointMm(roomMaxX, roomMinY, smokeLevelElevationMm),
        height: unitMm(options.wallHeightMm),
        structural: false,
        flip: false,
      },
      {
        id: "create-room-boundary-east",
        type: "create_wall",
        levelId: String(smokeLevelId),
        start: pointMm(roomMaxX, roomMinY, smokeLevelElevationMm),
        end: pointMm(roomMaxX, roomMaxY, smokeLevelElevationMm),
        height: unitMm(options.wallHeightMm),
        structural: false,
        flip: false,
      },
      {
        id: "create-room-boundary-north",
        type: "create_wall",
        levelId: String(smokeLevelId),
        start: pointMm(roomMaxX, roomMaxY, smokeLevelElevationMm),
        end: pointMm(roomMinX, roomMaxY, smokeLevelElevationMm),
        height: unitMm(options.wallHeightMm),
        structural: false,
        flip: false,
      },
      {
        id: "create-room-boundary-west",
        type: "create_wall",
        levelId: String(smokeLevelId),
        start: pointMm(roomMinX, roomMaxY, smokeLevelElevationMm),
        end: pointMm(roomMinX, roomMinY, smokeLevelElevationMm),
        height: unitMm(options.wallHeightMm),
        structural: false,
        flip: false,
      },
    ];
    const roomBoundaryTransaction = makeTransactionName(options.transactionPrefix, "create room boundary", runId);
    const roomBoundaryChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: numericOrUndefined(familyPlacementApply?.generation ?? createApply.generation),
      transactionName: roomBoundaryTransaction,
      operations: roomBoundaryOperations,
    });

    const roomBoundaryPreview = await previewChangeSet(client, roomBoundaryChangeSet, "create room boundary walls");
    const roomBoundaryApply = await applyChangeSet(client, roomBoundaryChangeSet, roomBoundaryPreview, "create room boundary walls");
    const roomBoundaryWallIds = roomBoundaryOperations.map((operation) => {
      const change = findChangeByOperationId(roomBoundaryApply, operation.id, "create_wall");
      const createdId = getCreatedElementId(change);
      assert(createdId, `create_wall ${operation.id} applied but did not return a created wall ID.`);
      return createdId;
    });
    console.log(`Create room boundary OK: ${roomBoundaryWallIds.length} wall(s)`);

    const roomNumber = `MCP-${runId}`;
    const roomName = `MCP Room ${runId}`;
    const roomDepartment = "MCP Smoke";
    const roomOperation = {
      id: "create-smoke-room",
      type: "create_room",
      levelId: String(smokeLevelId),
      location: point2Mm((roomMinX + roomMaxX) / 2, (roomMinY + roomMaxY) / 2),
      number: roomNumber,
      name: roomName,
      department: roomDepartment,
    };
    const roomTransaction = makeTransactionName(options.transactionPrefix, "create room", runId);
    const roomChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: numericOrUndefined(roomBoundaryApply.generation),
      transactionName: roomTransaction,
      operations: [roomOperation],
    });

    const roomPreview = await previewChangeSet(client, roomChangeSet, "create_room");
    const roomApply = await applyChangeSet(client, roomChangeSet, roomPreview, "create_room");
    const roomChange = findChange(roomApply, "create_room");
    const roomId = getCreatedElementId(roomChange);
    assert(roomId, "create_room applied but the created room element ID was not returned.");
    assert(unitValueNumber(roomChange?.after?.area) > 0, "create_room applied but the room area was not greater than zero.");
    assert(roomChange?.after?.isPlaced === true, "create_room applied but did not report isPlaced=true.");

    const createdRoom = await getRoomByNumber(client, {
      documentFingerprint,
      levelId: String(smokeLevelId),
      number: roomNumber,
    });
    assert(String(createdRoom.id) === String(roomId), "revit.get_rooms did not return the room created by create_room.");
    assert(createdRoom.name === roomName, `revit.get_rooms returned room name ${String(createdRoom.name)}, expected ${roomName}.`);
    assert(createdRoom.department === roomDepartment, "revit.get_rooms did not return the created room department.");
    assert(unitValueNumber(createdRoom.area) > 0, "revit.get_rooms returned the created room without positive area.");
    assert(createdRoom.isPlaced === true, "revit.get_rooms returned the created room without isPlaced=true.");
    assert(createdRoom.isEnclosed === true, "revit.get_rooms returned the created room without isEnclosed=true.");
    const createdRoomUniqueId = String(createdRoom.uniqueId ?? "");
    assert(createdRoomUniqueId.length > 0, "revit.get_rooms did not return the created room uniqueId.");
    console.log(`Create/read room OK: room ${roomNumber} (${roomId})`);

    const documentationResult = await tryDocumentationWorkflows(client, {
      documentFingerprint,
      expectedGeneration: numericOrUndefined(roomApply.generation),
      transactionPrefix: options.transactionPrefix,
      runId,
      currentView,
      levelElevationMm,
    });
    const sheetApply = documentationResult.sheet?.apply;
    const placedViewApply = documentationResult.placedView?.apply;
    const textNoteApply = documentationResult.textNote?.apply;
    const createdSheetId = documentationResult.sheet?.sheetId;
    const createdViewportId = documentationResult.placedView?.viewportId;
    const createdTextNoteId = documentationResult.textNote?.textNoteId;

    let annotationGeneration = numericOrUndefined(
      textNoteApply?.generation ?? placedViewApply?.generation ?? sheetApply?.generation ?? roomApply.generation
    );
    const roomTagResult = await tryTagRoom(client, {
      documentFingerprint,
      expectedGeneration: annotationGeneration,
      transactionPrefix: options.transactionPrefix,
      runId,
      preferredRoomId: roomId,
      preferredRoomUniqueId: createdRoomUniqueId,
      preferredLevelId: String(smokeLevelId),
      preferredLocation: roomOperation.location,
      tagTypeSelector: roomTagSelector,
    });
    if (roomTagResult.apply) {
      annotationGeneration = numericOrUndefined(roomTagResult.apply.generation);
    } else {
      summary.skippedOperations.push(compactObject({ type: "tag_room", reason: roomTagResult.reason, evidence: roomTagResult.evidence }));
      console.log(`Tag room skipped: ${roomTagResult.reason}`);
    }

    const elementTagResult = await tryTagElement(client, {
      documentFingerprint,
      expectedGeneration: annotationGeneration,
      transactionPrefix: options.transactionPrefix,
      runId,
      preferredElementId: wallId,
      levelElevationMm: smokeLevelElevationMm,
      tagTypeSelector: elementTagSelector,
    });
    if (elementTagResult.apply) {
      annotationGeneration = numericOrUndefined(elementTagResult.apply.generation);
    } else {
      summary.skippedOperations.push(
        compactObject({ type: "tag_element", reason: elementTagResult.reason, evidence: elementTagResult.evidence })
      );
      console.log(`Tag element skipped: ${elementTagResult.reason}`);
    }

    const roomTagApply = roomTagResult.apply;
    const elementTagApply = elementTagResult.apply;
    const createdRoomTagId = roomTagResult.roomTagId;
    const createdElementTagId = elementTagResult.elementTagId;
    assertRequiredCoverage("tag_room", options.requireRoomTag, roomTagResult.apply, roomTagResult.reason);
    assertRequiredCoverage("tag_element", options.requireElementTag, elementTagResult.apply, elementTagResult.reason);

    const parameterGeneration = annotationGeneration;
    const parameterValue = `MCP-${runId}`;
    const parameterResult = await applyFirstReadySetParameter(client, {
      documentFingerprint,
      expectedGeneration: parameterGeneration,
      transactionName: makeTransactionName(options.transactionPrefix, "set wall parameter", runId),
      elementId: wallId,
      expectedUniqueId: wallUniqueId,
      value: parameterValue,
      candidateNames: ["Comments", "Kommentare", "Mark", "Kennzeichen", "Markierung"],
    });
    const parameterApply = parameterResult.apply;
    const parameterChange = findChange(parameterApply, "set_parameter");
    assertParameterValue(parameterChange, parameterValue);
    await queryElementByParameter(client, "Wall", wallId, parameterResult.parameterName, parameterValue);
    console.log(`Set parameter OK: wall ${wallId} ${parameterResult.parameterName}=${parameterValue}`);

    const compatibleWallTypes = await catalog(client, {
      kind: "elementTypes",
      filter: { forElementId: wallId },
      preset: "typeChange",
      limit: 200,
      includeTotalCount: true,
    });
    assertCatalogPage(compatibleWallTypes, "elementTypes");
    assert(
      compatibleWallTypes.target?.elementId === String(wallId),
      "revit.catalog typeChange result did not identify the smoke wall target."
    );
    const alternateWallType = compatibleWallTypes.items.find(
      (item) => item.validForTarget !== false && item.isCurrentType !== true && typeof item.id === "string" && item.id.length > 0
    );

    let changeTypeApply = undefined;
    if (alternateWallType) {
      const changeTypeGeneration = numericOrUndefined(parameterApply.generation);
      const changeTypeOperation = {
        id: "change-smoke-wall-type",
        type: "change_element_type",
        elementId: wallId,
        expectedUniqueId: wallUniqueId,
        typeId: alternateWallType.id,
      };
      const changeTypeTransaction = makeTransactionName(options.transactionPrefix, "change wall type", runId);
      const changeTypeChangeSet = compactObject({
        documentFingerprint,
        expectedGeneration: changeTypeGeneration,
        transactionName: changeTypeTransaction,
        operations: [changeTypeOperation],
      });
      const changeTypePreview = await previewChangeSet(client, changeTypeChangeSet, "change_element_type");
      changeTypeApply = await applyChangeSet(client, changeTypeChangeSet, changeTypePreview, "change_element_type");
      console.log(`Change wall type OK: element ${wallId} -> type ${alternateWallType.id}`);
    } else if (options.requireTypeChange) {
      throw new Error(
        [
          "revit.catalog did not return an alternate valid wall type for the smoke wall.",
          "Use a disposable release-smoke model with at least two compatible wall types, or omit --require-type-change for local smoke.",
        ].join("\n")
      );
    } else {
      console.log("Change wall type skipped: no alternate compatible wall type was available in this project.");
    }

    const moveGeneration = numericOrUndefined(changeTypeApply?.generation ?? parameterApply.generation);
    const moveOperation = {
      id: "move-smoke-wall",
      type: "move_element",
      elementId: wallId,
      expectedUniqueId: wallUniqueId,
      translation: pointMm(0, options.moveYMm, 0),
    };
    const moveTransaction = makeTransactionName(options.transactionPrefix, "move wall", runId);
    const moveChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: moveGeneration,
      transactionName: moveTransaction,
      operations: [moveOperation],
    });

    const movePreview = await previewChangeSet(client, moveChangeSet, "move_element");
    const moveApply = await applyChangeSet(client, moveChangeSet, movePreview, "move_element");
    const moveChange = findChange(moveApply, "move_element");
    assertMoveYChanged(moveChange, options.moveYMm);
    console.log(`Move wall OK: element ${wallId} moved by ${options.moveYMm} mm on Y`);

    const movedWall = await queryWallById(client, wallId);
    console.log(`Post-move query OK: wall ${movedWall.id} is still queryable`);

    const rotationGeneration = numericOrUndefined(moveApply.generation);
    const axisStart = pointFromLocationSnapshot(moveChange?.after?.location, pointMm(0, options.moveYMm, smokeLevelElevationMm));
    const axisEnd = offsetPointMm(axisStart, 0, 0, 1000);
    const rotateOperation = {
      id: "rotate-smoke-wall",
      type: "rotate_element",
      elementId: wallId,
      expectedUniqueId: wallUniqueId,
      axisStart,
      axisEnd,
      angle: { value: 5, unit: "degrees" },
    };
    const rotateTransaction = makeTransactionName(options.transactionPrefix, "rotate wall", runId);
    const rotateChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: rotationGeneration,
      transactionName: rotateTransaction,
      operations: [rotateOperation],
    });

    const rotatePreview = await previewChangeSet(client, rotateChangeSet, "rotate_element");
    const rotateApply = await applyChangeSet(client, rotateChangeSet, rotatePreview, "rotate_element");
    const rotateChange = findChange(rotateApply, "rotate_element");
    assert(rotateChange?.after?.location, "rotate_element apply did not include an after.location snapshot.");
    console.log(`Rotate wall OK: element ${wallId} rotated by 5 degrees`);

    const copyGeneration = numericOrUndefined(rotateApply.generation);
    const copyOperation = {
      id: "copy-smoke-wall",
      type: "copy_element",
      elementId: wallId,
      expectedUniqueId: wallUniqueId,
      translation: pointMm(options.wallLengthMm + 1000, 0, 0),
    };
    const copyTransaction = makeTransactionName(options.transactionPrefix, "copy wall", runId);
    const copyChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: copyGeneration,
      transactionName: copyTransaction,
      operations: [copyOperation],
    });

    const copyPreview = await previewChangeSet(client, copyChangeSet, "copy_element");
    const copyApply = await applyChangeSet(client, copyChangeSet, copyPreview, "copy_element");
    const copyChange = findChange(copyApply, "copy_element");
    const copiedWallId = getCopiedElementId(copyChange);
    assert(copiedWallId, "copy_element applied but no copied element ID was returned.");
    const copiedWall = await queryWallById(client, copiedWallId);
    const copiedWallUniqueId = String(copiedWall.uniqueId ?? "");
    assert(copiedWallUniqueId.length > 0, "Copied smoke wall query did not return a uniqueId.");
    console.log(`Copy wall OK: source ${wallId} copied to ${copiedWallId}`);

    const pinGeneration = numericOrUndefined(copyApply.generation);
    const pinOperation = {
      id: "pin-smoke-wall",
      type: "set_element_pinned",
      elementId: copiedWallId,
      expectedUniqueId: copiedWallUniqueId,
      pinned: true,
      expectedPinned: false,
    };
    const pinTransaction = makeTransactionName(options.transactionPrefix, "pin wall", runId);
    const pinChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: pinGeneration,
      transactionName: pinTransaction,
      operations: [pinOperation],
    });

    const pinPreview = await previewChangeSet(client, pinChangeSet, "set_element_pinned");
    const pinApply = await applyChangeSet(client, pinChangeSet, pinPreview, "set_element_pinned");
    assertPinnedState(findChange(pinApply, "set_element_pinned"), true);
    console.log(`Pin wall OK: element ${copiedWallId} pinned`);

    const unpinGeneration = numericOrUndefined(pinApply.generation);
    await previewBlockedChangeSet(
      client,
      compactObject({
        documentFingerprint,
        expectedGeneration: unpinGeneration,
        transactionName: makeTransactionName(options.transactionPrefix, "move pinned wall preview", runId),
        operations: [
          {
            id: "move-pinned-smoke-wall-preview",
            type: "move_element",
            elementId: copiedWallId,
            expectedUniqueId: copiedWallUniqueId,
            translation: pointMm(0, options.moveYMm, 0),
          },
        ],
      }),
      "move pinned wall"
    );

    const unpinOperation = {
      id: "unpin-smoke-wall",
      type: "set_element_pinned",
      elementId: copiedWallId,
      expectedUniqueId: copiedWallUniqueId,
      pinned: false,
      expectedPinned: true,
    };
    const unpinTransaction = makeTransactionName(options.transactionPrefix, "unpin wall", runId);
    const unpinChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: unpinGeneration,
      transactionName: unpinTransaction,
      operations: [unpinOperation],
    });

    const unpinPreview = await previewChangeSet(client, unpinChangeSet, "set_element_pinned");
    await expectToolFailure(
      client,
      "revit.apply_change_set",
      makeApplyPayload(unpinChangeSet, unpinPreview, { changeSetHash: "sha256:bad-live-smoke-hash" }),
      "apply with mismatched changeSetHash",
      "CHANGE_SET_HASH_MISMATCH"
    );
    const unpinApply = await applyChangeSet(client, unpinChangeSet, unpinPreview, "set_element_pinned");
    assertPinnedState(findChange(unpinApply, "set_element_pinned"), false);
    console.log(`Unpin wall OK: element ${copiedWallId} unpinned`);

    const deleteGeneration = numericOrUndefined(unpinApply.generation);
    const deleteOperation = compactObject({
      id: "delete-copied-smoke-wall",
      type: "delete_element",
      elementId: copiedWallId,
      expectedUniqueId: copiedWallUniqueId,
      expectedPinned: false,
      allowDependentDeletes: true,
      dependentDeleteLimit: 20,
    });
    const deleteTransaction = makeTransactionName(options.transactionPrefix, "delete copied wall", runId);
    const deleteChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: deleteGeneration,
      transactionName: deleteTransaction,
      operations: [deleteOperation],
    });

    const deletePreview = await previewChangeSet(client, deleteChangeSet, "delete_element");
    const deleteApply = await applyChangeSet(client, deleteChangeSet, deletePreview, "delete_element");
    assertDeletedState(findChange(deleteApply, "delete_element"), copiedWallId);
    await assertElementDeletedById(client, copiedWallId);
    console.log(`Delete copied wall OK: element ${copiedWallId} removed`);

    summary.status = "passed";
    summary.coveredTools = [...REQUIRED_TOOLS, "revitctl.operation_kind_mismatch"];
    summary.coveredOperations = [
      "create_level",
      "create_grid",
      "create_floor",
      "create_wall",
      ...(familyPlacementApply ? ["place_family_instance"] : []),
      "create_room",
      ...(sheetApply ? ["create_sheet"] : []),
      ...(placedViewApply ? ["place_view_on_sheet"] : []),
      ...(textNoteApply ? ["create_text_note"] : []),
      ...(roomTagApply ? ["tag_room"] : []),
      ...(elementTagApply ? ["tag_element"] : []),
      "set_parameter",
      ...(changeTypeApply ? ["change_element_type"] : []),
      "move_element",
      "rotate_element",
      "copy_element",
      "set_element_pinned",
      "delete_element",
    ];
    if (!changeTypeApply) {
      summary.skippedOperations.push({
        type: "change_element_type",
        reason: "No alternate compatible wall type was available in this project.",
      });
    }
    if (!familyPlacementApply) {
      summary.skippedOperations.push({
        type: "place_family_instance",
        reason: familyPlacementResult.reason,
      });
    }
    for (const skipped of documentationResult.skippedOperations) {
      summary.skippedOperations.push(skipped);
    }
    summary.result = compactObject({
      runId,
      createdElementIds: {
        level: smokeLevelId,
        grid: gridId,
        floor: floorId,
        wall: wallId,
        familyInstance: familyInstanceId,
        roomBoundaryWalls: roomBoundaryWallIds,
        room: roomId,
        sheet: createdSheetId,
        viewport: createdViewportId,
        textNote: createdTextNoteId,
        roomTag: createdRoomTagId,
        elementTag: createdElementTagId,
        copiedWall: copiedWallId,
      },
      tagCoverage: {
        room: roomTagApply
          ? {
              roomId: roomTagResult.roomId,
              roomUniqueId: roomTagResult.roomUniqueId,
              viewId: roomTagResult.viewId,
              tagTypeId: roomTagResult.tagTypeId,
              tagTypeName: roomTagResult.tagTypeName,
              createdTagId: createdRoomTagId,
            }
          : undefined,
        element: elementTagApply
          ? {
              elementId: elementTagResult.elementId,
              elementUniqueId: elementTagResult.elementUniqueId,
              viewId: elementTagResult.viewId,
              tagTypeId: elementTagResult.tagTypeId,
              tagTypeName: elementTagResult.tagTypeName,
              createdTagId: createdElementTagId,
            }
          : undefined,
      },
      finalGeneration: numericOrUndefined(deleteApply.generation),
    });
    console.log("Live smoke passed.");
  } catch (error) {
    summary.error = formatError(error);
    if (stderr.length > 0) {
      console.error("");
      console.error("MCP server stderr:");
      console.error(stderr.join("").trim());
    }
    throw error;
  } finally {
    summary.completedAtUtc = new Date().toISOString();
    writeSmokeSummary(options.summaryPath, summary);
    await closeQuietly(client);
  }
}

function parseArgs(args) {
  const options = {
    documentFingerprint: undefined,
    wallLengthMm: DEFAULT_WALL_LENGTH_MM,
    moveYMm: DEFAULT_MOVE_Y_MM,
    wallHeightMm: DEFAULT_WALL_HEIGHT_MM,
    transactionPrefix: DEFAULT_TRANSACTION_PREFIX,
    launcherPath: undefined,
    expectedRevitYear: undefined,
    summaryPath: undefined,
    requireTypeChange: false,
    requireRoomTag: false,
    requireElementTag: false,
    roomTagTypeId: undefined,
    roomTagTypeNameContains: undefined,
    elementTagTypeId: undefined,
    elementTagTypeNameContains: undefined,
    statusOnly: false,
    help: false,
  };

  for (let index = 0; index < args.length; index++) {
    const arg = args[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }

    const [name, inlineValue] = splitOption(arg);
    switch (name) {
      case "--document-fingerprint":
      case "--fingerprint":
        options.documentFingerprint = readValue(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--wall-length-mm":
        options.wallLengthMm = readNumber(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--move-y-mm":
        options.moveYMm = readNumber(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--wall-height-mm":
        options.wallHeightMm = readNumber(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--transaction-prefix":
        options.transactionPrefix = readValue(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--launcher":
      case "--launcher-path":
        options.launcherPath = readValue(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--expected-revit-year":
      case "--revit-year":
        options.expectedRevitYear = readExpectedRevitYear(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--summary-path":
      case "--smoke-summary-path":
        options.summaryPath = readValue(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--require-type-change":
        options.requireTypeChange = true;
        break;
      case "--skip-type-change":
        options.requireTypeChange = false;
        break;
      case "--require-room-tag":
        options.requireRoomTag = true;
        break;
      case "--skip-room-tag":
        options.requireRoomTag = false;
        break;
      case "--require-element-tag":
        options.requireElementTag = true;
        break;
      case "--skip-element-tag":
        options.requireElementTag = false;
        break;
      case "--require-tags":
        options.requireRoomTag = true;
        options.requireElementTag = true;
        break;
      case "--skip-tags":
        options.requireRoomTag = false;
        options.requireElementTag = false;
        break;
      case "--room-tag-type-id":
        options.roomTagTypeId = readValue(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--room-tag-type-name-contains":
      case "--room-tag-type-name":
        options.roomTagTypeNameContains = readValue(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--element-tag-type-id":
        options.elementTagTypeId = readValue(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--element-tag-type-name-contains":
      case "--element-tag-type-name":
        options.elementTagTypeNameContains = readValue(args, ++index, inlineValue, name);
        if (inlineValue !== undefined) index--;
        break;
      case "--status-only":
        options.statusOnly = true;
        break;
      default:
        throw new Error(`Unknown argument: ${arg}\nRun with --help for usage.`);
    }
  }

  return options;
}

function splitOption(arg) {
  const equalsIndex = arg.indexOf("=");
  if (equalsIndex === -1) return [arg, undefined];
  return [arg.slice(0, equalsIndex), arg.slice(equalsIndex + 1)];
}

function readValue(args, index, inlineValue, name) {
  const value = inlineValue ?? args[index];
  if (value === undefined || value === "" || value.startsWith("--")) {
    throw new Error(`${name} requires a value.`);
  }
  return value;
}

function readNumber(args, index, inlineValue, name) {
  const raw = readValue(args, index, inlineValue, name);
  const parsed = Number(raw);
  if (!Number.isFinite(parsed)) {
    throw new Error(`${name} must be a finite number. Received: ${raw}`);
  }
  return parsed;
}

function readExpectedRevitYear(args, index, inlineValue, name) {
  const raw = readValue(args, index, inlineValue, name);
  if (!/^\d{4}$/.test(raw)) {
    throw new Error(`${name} must be a four-digit Revit year. Received: ${raw}`);
  }
  return raw;
}

function validateOptions(options) {
  assert(options.wallLengthMm > 0, "--wall-length-mm must be greater than zero.");
  assert(options.wallHeightMm > 0, "--wall-height-mm must be greater than zero.");
  assert(options.moveYMm !== 0, "--move-y-mm must be non-zero because Revit rejects zero-length moves.");
  assert(
    typeof options.transactionPrefix === "string" && options.transactionPrefix.trim().length >= 3,
    "--transaction-prefix must contain at least 3 non-whitespace characters."
  );
}

function assertExpectedRevitYear(status, expectedRevitYear) {
  if (!expectedRevitYear) return;
  const actualVersion = String(status.revit?.version ?? "");
  const match = actualVersion.match(/\b(20\d{2})\b/);
  const actualYear = match?.[1] ?? actualVersion.slice(0, 4);
  assert(
    actualYear === expectedRevitYear,
    [
      "Connected Revit version does not match the expected smoke year.",
      `Expected: ${expectedRevitYear}`,
      `Actual revit.status.revit.version: ${actualVersion || "(missing)"}`,
    ].join("\n")
  );
}

function assertRequiredCoverage(operationType, required, applyResult, reason) {
  if (!required || applyResult) return;
  throw new Error(
    [
      `Required live smoke coverage for ${operationType} was not achieved.`,
      reason ? `Last skip/block reason: ${reason}` : "No skip reason was recorded.",
      "Use a curated disposable model with the required tag family, view, and target element/room, or rerun without the corresponding require flag for preview-only evidence.",
    ].join("\n")
  );
}

function writeSmokeSummary(summaryPath, summary) {
  if (!summaryPath) return;
  const resolved = path.resolve(summaryPath);
  mkdirSync(path.dirname(resolved), { recursive: true });
  writeFileSync(resolved, `${JSON.stringify(summary, null, 2)}\n`, "utf8");
}

function resolveLauncherPath(launcherPath) {
  if (launcherPath) return path.resolve(launcherPath);
  const localAppData = process.env.LOCALAPPDATA;
  if (!localAppData) {
    throw new Error("LOCALAPPDATA is not set. Pass --launcher-path explicitly.");
  }
  return path.join(localAppData, "RevitMcpNext", "launch-revit-mcp-next.cmd");
}

async function loadMcpSdk(launcherPath) {
  const installRoot = path.dirname(launcherPath);
  const candidateRoots = uniquePaths([
    process.cwd(),
    path.resolve(SCRIPT_DIR, ".."),
    path.resolve(SCRIPT_DIR, "..", "broker"),
    path.resolve(SCRIPT_DIR, "..", "payload", "broker"),
    installRoot,
    path.join(installRoot, "broker"),
    process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, "RevitMcpNext", "broker") : undefined,
  ]);
  const requireFromScript = createRequire(import.meta.url);

  for (const root of candidateRoots) {
    try {
      const clientPath = requireFromScript.resolve("@modelcontextprotocol/sdk/client/index.js", { paths: [root] });
      const transportPath = requireFromScript.resolve("@modelcontextprotocol/sdk/client/stdio.js", { paths: [root] });
      const clientModule = await import(pathToFileURL(clientPath).href);
      const transportModule = await import(pathToFileURL(transportPath).href);
      return {
        Client: clientModule.Client,
        StdioClientTransport: transportModule.StdioClientTransport,
      };
    } catch {
      // Try the next dependency root.
    }
  }

  throw new Error(
    [
      "Unable to resolve @modelcontextprotocol/sdk for the live smoke client.",
      "Run npm install in the repo, use a packaged release with broker production dependencies, or reinstall Revit MCP Next.",
      `Checked roots: ${candidateRoots.join(", ")}`,
    ].join("\n")
  );
}

async function probeOperationKindMismatch(launcherPath) {
  const installRoot = path.dirname(launcherPath);
  const revitCtlPath = path.join(installRoot, process.platform === "win32" ? "revitctl.cmd" : "revitctl");
  if (!existsSync(revitCtlPath)) {
    throw new Error(`revitctl launcher was not found beside the MCP launcher: ${revitCtlPath}`);
  }

  const result = await runRevitCtlProbe(revitCtlPath, [
    "call",
    "apply_change_set",
    "--operation-kind",
    "read",
    "--confirm",
    "--payload",
    "{}",
    "--install-root",
    installRoot,
    "--timeout-ms",
    "10000",
    "--json",
  ]);

  assert(
    result.exitCode === 2,
    `operationKind mismatch probe expected revitctl exit code 2 but received ${result.exitCode}.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`
  );

  let response;
  try {
    response = JSON.parse(result.stdout.trim());
  } catch (error) {
    throw new Error(
      `operationKind mismatch probe did not return JSON: ${error instanceof Error ? error.message : String(error)}\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`
    );
  }

  assert(response.ok === false, "operationKind mismatch probe unexpectedly returned ok=true.");
  assert(
    response.error?.code === "OPERATION_KIND_MISMATCH",
    `operationKind mismatch probe returned ${response.error?.code ?? "(missing error code)"} instead of OPERATION_KIND_MISMATCH.`
  );

  return compactObject({
    command: "revitctl call apply_change_set --operation-kind read",
    exitCode: result.exitCode,
    errorCode: response.error.code,
    message: stringOrUndefined(response.error?.message),
  });
}

function runRevitCtlProbe(revitCtlPath, args) {
  return new Promise((resolve, reject) => {
    const command = process.platform === "win32" ? process.env.ComSpec || "cmd.exe" : revitCtlPath;
    const spawnArgs =
      process.platform === "win32"
        ? ["/d", "/c", "call", revitCtlPath, ...args]
        : args;
    const child = spawn(command, spawnArgs, {
      cwd: process.cwd(),
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error(`revitctl probe timed out after 15000 ms: ${revitCtlPath}`));
    }, 15000);
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString("utf8");
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString("utf8");
    });
    child.on("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    child.on("close", (exitCode) => {
      clearTimeout(timeout);
      resolve({
        exitCode: exitCode ?? -1,
        stdout,
        stderr,
      });
    });
  });
}

function uniquePaths(values) {
  const seen = new Set();
  const result = [];
  for (const value of values) {
    if (!value) continue;
    const full = path.resolve(value);
    const key = process.platform === "win32" ? full.toLowerCase() : full;
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(full);
  }
  return result;
}

function makeTransport(launcherPath, StdioClientTransport) {
  if (process.platform === "win32") {
    const command = process.env.ComSpec || "cmd.exe";
    return new StdioClientTransport({
      command,
      args: ["/d", "/c", "call", launcherPath],
      stderr: "pipe",
    });
  }

  return new StdioClientTransport({
    command: launcherPath,
    args: [],
    stderr: "pipe",
  });
}

async function verifyRequiredTools(client) {
  const listed = await client.listTools();
  const toolNames = new Set((listed.tools ?? []).map((tool) => tool.name));
  const missing = REQUIRED_TOOLS.filter((name) => !toolNames.has(name));
  assert(missing.length === 0, `MCP server is missing required tools: ${missing.join(", ")}`);
  console.log(`Tools OK: ${REQUIRED_TOOLS.join(", ")}`);
}

async function callRequiredTool(client, name, args) {
  const result = await client.callTool({ name, arguments: args });
  if (result.isError) {
    throw new Error(formatToolFailure(name, result));
  }

  const data = result.structuredContent?.data;
  if (data === undefined) {
    throw new Error(`${name} did not return structuredContent.data.`);
  }

  return data;
}

async function catalog(client, args) {
  return callRequiredTool(client, "revit.catalog", args);
}

function assertCatalogPage(result, expectedKind) {
  assert(result?.kind === expectedKind, `revit.catalog returned kind ${String(result?.kind)}, expected ${expectedKind}.`);
  assert(Array.isArray(result.items), "revit.catalog did not return an items array.");
  assert(result.returnedCount === result.items.length, "revit.catalog returnedCount did not match items length.");
  assert(Number.isInteger(result.limit) && result.limit > 0, "revit.catalog did not return a positive limit.");
  assert(typeof result.truncated === "boolean", "revit.catalog did not return truncated boolean.");
  for (const item of result.items) {
    assert(typeof item.id === "string" && item.id.length > 0, "revit.catalog item is missing id.");
    assert(typeof item.class === "string" && item.class.length > 0, "revit.catalog item is missing class.");
    assert(typeof item.name === "string", "revit.catalog item is missing name.");
  }
}

async function previewChangeSet(client, changeSet, operationName) {
  const preview = await callRequiredTool(client, "revit.preview_change_set", changeSet);
  assert(preview.ready === true, `${operationName} preview was blocked:\n${formatChanges(preview.changes)}`);
  assert(Array.isArray(preview.changes), `${operationName} preview did not return changes.`);
  assert(
    preview.changes.every((change) => change.status === "ready"),
    `${operationName} preview returned non-ready changes:\n${formatChanges(preview.changes)}`
  );
  assert(preview.previewId, `${operationName} preview did not return previewId.`);
  assert(preview.changeSetHash, `${operationName} preview did not return changeSetHash.`);
  console.log(`Preview OK: ${operationName} (${preview.previewId})`);
  return preview;
}

async function previewBlockedChangeSet(client, changeSet, operationName) {
  const preview = await callRequiredTool(client, "revit.preview_change_set", changeSet);
  assert(preview.ready === false, `${operationName} preview unexpectedly returned ready=true.`);
  assert(
    Array.isArray(preview.changes) && preview.changes.some((change) => change.status === "blocked"),
    `${operationName} preview did not return a blocked change:\n${formatChanges(preview.changes)}`
  );
  console.log(`Blocked preview OK: ${operationName}`);
  return preview;
}

async function applyChangeSet(client, changeSet, preview, operationName) {
  const applyPayload = makeApplyPayload(changeSet, preview);

  const apply = await callRequiredTool(client, "revit.apply_change_set", applyPayload);
  assert(apply.applied === true, `${operationName} apply did not report applied=true.`);
  assert(apply.changedCount === changeSet.operations.length, `${operationName} apply changed an unexpected count.`);
  assert(Array.isArray(apply.changes), `${operationName} apply did not return changes.`);
  console.log(`Apply OK: ${operationName} (${apply.changedCount} change)`);
  return apply;
}

function makeApplyPayload(changeSet, preview, overrides = {}) {
  const applyPayload = compactObject({
    ...changeSet,
    previewId: preview.previewId,
    confirm: true,
    changeSetHash: preview.changeSetHash,
    baseGeneration: preview.baseGeneration,
    expiresAt: preview.expiresAt,
    ...overrides,
  });
  delete applyPayload.expectedGeneration;
  return applyPayload;
}

async function expectToolFailure(client, name, args, operationName, expectedText) {
  const result = await client.callTool({ name, arguments: args });
  assert(result.isError === true, `${operationName} unexpectedly succeeded.`);
  const failure = formatToolFailure(name, result);
  if (expectedText) {
    assert(
      failure.includes(expectedText),
      `${operationName} failed, but did not include ${expectedText}.\n${failure}`
    );
  }
  console.log(`Expected failure OK: ${operationName}`);
  return result;
}

async function queryWallById(client, wallId) {
  return queryElementById(client, "Wall", wallId);
}

async function applyFirstReadySetParameter(
  client,
  { documentFingerprint, expectedGeneration, transactionName, elementId, expectedUniqueId, value, candidateNames }
) {
  const blockedMessages = [];
  for (const parameterName of candidateNames) {
    const operation = {
      id: `set-smoke-wall-${parameterName.toLowerCase().replace(/[^a-z0-9]+/g, "-")}`,
      type: "set_parameter",
      elementId,
      expectedUniqueId,
      parameterName,
      value,
    };
    const changeSet = compactObject({
      documentFingerprint,
      expectedGeneration,
      transactionName,
      operations: [operation],
    });
    const preview = await callRequiredTool(client, "revit.preview_change_set", changeSet);
    assert(Array.isArray(preview.changes), "set_parameter preview did not return changes.");

    if (preview.ready === true && preview.changes.every((change) => change.status === "ready")) {
      assert(preview.previewId, "set_parameter preview did not return previewId.");
      assert(preview.changeSetHash, "set_parameter preview did not return changeSetHash.");
      console.log(`Preview OK: set_parameter (${preview.previewId}) using ${parameterName}`);
      const apply = await applyChangeSet(client, changeSet, preview, "set_parameter");
      return { apply, parameterName };
    }

    blockedMessages.push(`${parameterName}: ${formatChanges(preview.changes)}`);
  }

  throw new Error(
    [
      "No candidate writable text parameter was available for set_parameter smoke.",
      "Tried: " + candidateNames.join(", "),
      blockedMessages.join("\n"),
    ].join("\n")
  );
}

async function tryPlaceFamilyInstance(
  client,
  { documentFingerprint, expectedGeneration, transactionPrefix, runId, wallId, wallUniqueId, levelId, levelElevationMm, wallLengthMm }
) {
  const hostedSymbols = await catalog(client, {
    kind: "familySymbols",
    filter: { categories: ["OST_Doors", "OST_Windows"] },
    preset: "placement",
    limit: 50,
    includeTotalCount: true,
  });
  assertCatalogPage(hostedSymbols, "familySymbols");

  const hostedResult = await tryFamilyPlacementCandidates(
    client,
    {
      documentFingerprint,
      expectedGeneration,
      transactionPrefix,
      runId,
      operationLabel: "hosted family instance",
    },
    hostedSymbols.items.filter(isWallHostedPlacementSymbol),
    (symbol, index) => ({
      id: `place-hosted-family-${index + 1}`,
      type: "place_family_instance",
      familySymbolId: String(symbol.id),
      hostElementId: wallId,
      expectedHostUniqueId: wallUniqueId,
      levelId,
      location: pointMm(Math.max(600, Math.min(wallLengthMm / 2, wallLengthMm - 600)), 0, levelElevationMm),
      rotation: { value: 0, unit: "degrees" },
    })
  );
  if (hostedResult.apply || hostedResult.tried > 0) return hostedResult;

  const levelBasedSymbols = await catalog(client, {
    kind: "familySymbols",
    filter: {
      categories: [
        "OST_Furniture",
        "OST_ElectricalEquipment",
        "OST_MechanicalEquipment",
        "OST_PlumbingFixtures",
        "OST_ElectricalFixtures",
        "OST_LightingFixtures",
        "OST_SpecialityEquipment",
      ],
    },
    preset: "placement",
    limit: 50,
    includeTotalCount: true,
  });
  assertCatalogPage(levelBasedSymbols, "familySymbols");

  return tryFamilyPlacementCandidates(
    client,
    {
      documentFingerprint,
      expectedGeneration,
      transactionPrefix,
      runId,
      operationLabel: "level-based family instance",
    },
    levelBasedSymbols.items.filter(isLevelBasedPlacementSymbol),
    (symbol, index) => ({
      id: `place-level-family-${index + 1}`,
      type: "place_family_instance",
      familySymbolId: String(symbol.id),
      levelId,
      location: pointMm(wallLengthMm + 1500, 1500 + index * 300, levelElevationMm),
      rotation: { value: 0, unit: "degrees" },
    })
  );
}

async function tryFamilyPlacementCandidates(client, context, candidates, buildOperation) {
  if (!Array.isArray(candidates) || candidates.length === 0) {
    return {
      apply: undefined,
      tried: 0,
      reason: `No compatible ${context.operationLabel} symbols were returned by revit.catalog.`,
    };
  }

  const blockedMessages = [];
  for (let index = 0; index < candidates.length; index++) {
    const symbol = candidates[index];
    const operation = buildOperation(symbol, index);
    const changeSet = compactObject({
      documentFingerprint: context.documentFingerprint,
      expectedGeneration: context.expectedGeneration,
      transactionName: makeTransactionName(context.transactionPrefix, context.operationLabel, context.runId),
      operations: [operation],
    });

    const preview = await callRequiredTool(client, "revit.preview_change_set", changeSet);
    assert(Array.isArray(preview.changes), "place_family_instance preview did not return changes.");
    if (preview.ready === true && preview.changes.every((change) => change.status === "ready")) {
      assert(preview.previewId, "place_family_instance preview did not return previewId.");
      assert(preview.changeSetHash, "place_family_instance preview did not return changeSetHash.");
      console.log(`Preview OK: place_family_instance (${preview.previewId}) using ${symbol.familyName ?? symbol.name ?? symbol.id}`);
      const apply = await applyChangeSet(client, changeSet, preview, "place_family_instance");
      return {
        apply,
        tried: index + 1,
        symbol,
        reason: undefined,
      };
    }

    blockedMessages.push(`${symbol.familyName ?? symbol.name ?? symbol.id}: ${formatChanges(preview.changes)}`);
  }

  return {
    apply: undefined,
    tried: candidates.length,
    reason: `All ${context.operationLabel} candidates were blocked: ${blockedMessages.join(" | ")}`,
  };
}

function isWallHostedPlacementSymbol(item) {
  const placementType = String(item?.placementType ?? "");
  const builtInCategory = String(item?.builtInCategory ?? item?.category ?? "");
  return placementType.includes("Hosted") && ["OST_Doors", "OST_Windows"].includes(builtInCategory);
}

function isLevelBasedPlacementSymbol(item) {
  const placementType = String(item?.placementType ?? "");
  const builtInCategory = String(item?.builtInCategory ?? item?.category ?? "");
  return (
    placementType === "OneLevelBased" &&
    [
      "OST_Furniture",
      "OST_ElectricalEquipment",
      "OST_MechanicalEquipment",
      "OST_PlumbingFixtures",
      "OST_ElectricalFixtures",
      "OST_LightingFixtures",
      "OST_SpecialityEquipment",
    ].includes(builtInCategory)
  );
}

async function queryElementById(client, className, elementId) {
  let cursor = undefined;
  let scanned = 0;
  const seenCursors = new Set();
  do {
    const query = await callRequiredTool(client, "revit.query", {
      filter: { classes: [className] },
      fields: ["id", "uniqueId", "category", "class", "name", "typeId", "levelId"],
      limit: 500,
      cursor,
      includeTotalCount: true,
    });

    const items = Array.isArray(query.items) ? query.items : [];
    scanned += items.length;
    const match = items.find((item) => String(item.id) === String(elementId));
    if (match) return match;
    cursor = checkedNextCursor(query.cursor, seenCursors, "revit.query");
  } while (cursor);

  throw new Error(`Created ${className} ${elementId} was not found by revit.query after scanning ${scanned} item(s).`);
}

async function queryElementGeometryById(client, elementId) {
  const query = await callRequiredTool(client, "revit.query", {
    filter: { elementIds: [String(elementId)] },
    preset: "geometrySummary",
    limit: 1,
    includeTotalCount: true,
  });

  assert(Array.isArray(query.items), "geometrySummary did not return an items array.");
  assert(Array.isArray(query.fields), "geometrySummary did not return a field projection.");
  assert(query.fields.includes("location"), "geometrySummary projection did not include location.");
  assert(query.fields.includes("bounds"), "geometrySummary projection did not include bounds.");
  assert(query.units?.location === "mm", "geometrySummary did not report location units as mm.");
  assert(query.units?.bounds === "mm", "geometrySummary did not report bounds units as mm.");

  const match = query.items.find((item) => String(item.id) === String(elementId));
  assert(match, `geometrySummary did not return element ${elementId}.`);
  return match;
}

async function queryElementByParameter(client, className, elementId, parameterName, value) {
  let cursor = undefined;
  let scanned = 0;
  const seenCursors = new Set();
  do {
    const query = await callRequiredTool(client, "revit.query", {
      filter: {
        classes: [className],
        parameterEquals: {
          [parameterName]: value,
        },
      },
      fields: ["id", "class", "name", `param:${parameterName}`],
      limit: 500,
      cursor,
      includeTotalCount: true,
    });

    const items = Array.isArray(query.items) ? query.items : [];
    scanned += items.length;
    const match = items.find((item) => String(item.id) === String(elementId));
    if (match) return match;
    cursor = checkedNextCursor(query.cursor, seenCursors, "revit.query");
  } while (cursor);

  throw new Error(
    `${className} ${elementId} was not found by revit.query with ${parameterName}=${value} after scanning ${scanned} matching item(s).`
  );
}

function checkedNextCursor(cursor, seenCursors, toolName) {
  if (typeof cursor !== "string" || cursor.length === 0) return undefined;
  assert(cursor.startsWith("rvc1_"), `${toolName} returned a non-opaque cursor.`);
  assert(!seenCursors.has(cursor), `${toolName} returned a repeated cursor; refusing to replay a page.`);
  seenCursors.add(cursor);
  return cursor;
}

async function getRoomByNumber(client, { documentFingerprint, levelId, number }) {
  const rooms = await callRequiredTool(client, "revit.get_rooms", {
    documentFingerprint,
    filter: {
      levelIds: [levelId],
      numbers: [number],
    },
    fields: [
      "id",
      "uniqueId",
      "number",
      "name",
      "levelId",
      "levelName",
      "area",
      "volume",
      "location",
      "department",
      "isPlaced",
      "isEnclosed",
    ],
    preset: "schedule",
    limit: 10,
    includeTotalCount: true,
  });

  assert(Array.isArray(rooms.items), "revit.get_rooms did not return an items array.");
  assert(rooms.returnedCount === rooms.items.length, "revit.get_rooms returnedCount did not match items length.");
  assert(
    rooms.units?.area === "m2" && rooms.units?.volume === "m3",
    "revit.get_rooms did not return normalized room units."
  );
  const match = rooms.items.find((item) => String(item.number) === String(number) && String(item.levelId) === String(levelId));
  assert(match, `revit.get_rooms did not return room number ${number} on level ${levelId}.`);
  return match;
}

async function tryDocumentationWorkflows(
  client,
  { documentFingerprint, expectedGeneration, transactionPrefix, runId, currentView, levelElevationMm }
) {
  const result = {
    sheet: undefined,
    placedView: undefined,
    textNote: undefined,
    skippedOperations: [],
  };

  let generation = expectedGeneration;

  const titleBlocks = await catalog(client, {
    kind: "titleBlocks",
    preset: "sheet",
    limit: 20,
    includeTotalCount: true,
  });
  assertCatalogPage(titleBlocks, "titleBlocks");
  const titleBlockType = titleBlocks.items.find((item) => typeof item.id === "string" && item.id.length > 0);
  const sheetNumber = `MCP-${runId}`;
  const sheetOperation = compactObject({
    id: "create-smoke-sheet",
    type: "create_sheet",
    sheetNumber,
    name: `MCP Sheet ${runId}`,
    titleBlockTypeId: titleBlockType?.id,
  });
  const sheetChangeSet = compactObject({
    documentFingerprint,
    expectedGeneration: generation,
    transactionName: makeTransactionName(transactionPrefix, "create sheet", runId),
    operations: [sheetOperation],
  });
  const sheetPreview = await previewChangeSet(client, sheetChangeSet, "create_sheet");
  const sheetApply = await applyChangeSet(client, sheetChangeSet, sheetPreview, "create_sheet");
  const sheetChange = findChange(sheetApply, "create_sheet");
  const sheetId = getCreatedElementId(sheetChange);
  assert(sheetId, "create_sheet applied but did not return a sheet id.");
  await getSheetByNumber(client, { documentFingerprint, sheetNumber, expectedSheetId: sheetId });
  console.log(`Create/read sheet OK: sheet ${sheetNumber} (${sheetId})`);
  result.sheet = { apply: sheetApply, sheetId };
  generation = numericOrUndefined(sheetApply.generation);

  const placeViewResult = await tryPlaceViewOnSheet(client, {
    documentFingerprint,
    expectedGeneration: generation,
    transactionPrefix,
    runId,
    sheetId,
  });
  if (placeViewResult.apply) {
    result.placedView = placeViewResult;
    generation = numericOrUndefined(placeViewResult.apply.generation);
  } else {
    result.skippedOperations.push({ type: "place_view_on_sheet", reason: placeViewResult.reason });
  }

  const textNoteResult = await tryCreateTextNote(client, {
    documentFingerprint,
    expectedGeneration: generation,
    transactionPrefix,
    runId,
    sheetId,
    currentView,
    levelElevationMm,
  });
  if (textNoteResult.apply) {
    result.textNote = textNoteResult;
  } else {
    result.skippedOperations.push({ type: "create_text_note", reason: textNoteResult.reason });
  }

  return result;
}

async function getSheetByNumber(client, { documentFingerprint, sheetNumber, expectedSheetId }) {
  const sheets = await callRequiredTool(client, "revit.get_sheets", {
    documentFingerprint,
    filter: { numbers: [sheetNumber] },
    preset: "placement",
    includePlacedViews: true,
    limit: 10,
    includeTotalCount: true,
  });
  assert(Array.isArray(sheets.items), "revit.get_sheets did not return an items array.");
  const match = sheets.items.find((item) => String(item.sheetNumber) === String(sheetNumber));
  assert(match, `revit.get_sheets did not return sheet number ${sheetNumber}.`);
  assert(String(match.id) === String(expectedSheetId), `revit.get_sheets returned sheet id ${String(match.id)}, expected ${expectedSheetId}.`);
  return match;
}

async function tryPlaceViewOnSheet(client, { documentFingerprint, expectedGeneration, transactionPrefix, runId, sheetId }) {
  const existingSheets = await callRequiredTool(client, "revit.get_sheets", {
    documentFingerprint,
    preset: "placement",
    includePlacedViews: true,
    limit: 500,
    includeTotalCount: true,
  });
  assert(Array.isArray(existingSheets.items), "revit.get_sheets did not return an items array for view placement.");
  const placedViewIds = new Set();
  for (const sheet of existingSheets.items) {
    for (const placed of Array.isArray(sheet.placedViews) ? sheet.placedViews : []) {
      if (placed?.viewId) placedViewIds.add(String(placed.viewId));
    }
  }

  const candidateViews = await callRequiredTool(client, "revit.get_views", {
    documentFingerprint,
    filter: { isTemplate: false, isGraphical: true, canBePrinted: true },
    preset: "sheetPlacement",
    limit: 500,
    includeTotalCount: true,
  });
  assert(Array.isArray(candidateViews.items), "revit.get_views did not return an items array for view placement.");
  const view = candidateViews.items.find((item) => typeof item.id === "string" && item.id.length > 0 && !placedViewIds.has(String(item.id)));
  if (!view) {
    return {
      reason: `No unplaced printable non-template view was available. Sampled ${candidateViews.items.length} view(s) and found ${placedViewIds.size} already placed view id(s).`,
    };
  }

  const operation = {
    id: "place-smoke-view-on-sheet",
    type: "place_view_on_sheet",
    sheetId,
    viewId: String(view.id),
    center: point2Mm(250, 180),
  };
  const changeSet = compactObject({
    documentFingerprint,
    expectedGeneration,
    transactionName: makeTransactionName(transactionPrefix, "place view on sheet", runId),
    operations: [operation],
  });
  const preview = await previewChangeSet(client, changeSet, "place_view_on_sheet");
  const apply = await applyChangeSet(client, changeSet, preview, "place_view_on_sheet");
  const change = findChange(apply, "place_view_on_sheet");
  const viewportId = getCreatedElementId(change);
  assert(viewportId, "place_view_on_sheet applied but did not return a viewport id.");
  const sheet = await getSheetByNumber(client, { documentFingerprint, sheetNumber: `MCP-${runId}`, expectedSheetId: sheetId });
  assert(
    (sheet.placedViews ?? []).some((placed) => String(placed.viewId) === String(view.id)),
    "revit.get_sheets did not report the view placed by place_view_on_sheet."
  );
  console.log(`Place view on sheet OK: view ${view.id} on sheet ${sheetId}`);
  return { apply, viewportId, viewId: String(view.id) };
}

async function tryCreateTextNote(
  client,
  { documentFingerprint, expectedGeneration, transactionPrefix, runId, sheetId, currentView, levelElevationMm }
) {
  const textNoteTypes = await catalog(client, {
    kind: "textNoteTypes",
    preset: "annotation",
    limit: 20,
    includeTotalCount: true,
  });
  assertCatalogPage(textNoteTypes, "textNoteTypes");
  const textNoteType = textNoteTypes.items.find((item) => typeof item.id === "string" && item.id.length > 0);
  if (!textNoteType) {
    return { reason: "No TextNoteType was available in the active model." };
  }

  const current = currentView?.view;
  const target = sheetId
    ? {
        viewId: String(sheetId),
        position: pointMm(40, 40, 0),
        label: `sheet ${sheetId}`,
      }
    : current?.id && current.isTemplate !== true && current.isGraphical === true && current.viewType !== "ThreeD"
      ? {
          viewId: String(current.id),
          position: pointMm(1000, 1000, levelElevationMm),
          label: `view ${current.id}`,
        }
      : undefined;
  if (!target) {
    return { reason: "No graphical non-template view or created sheet was available to host a text note." };
  }

  const operation = {
    id: "create-smoke-text-note",
    type: "create_text_note",
    viewId: target.viewId,
    text: `MCP smoke note ${runId}`,
    position: target.position,
    textNoteTypeId: String(textNoteType.id),
    rotation: { value: 0, unit: "degrees" },
  };
  const changeSet = compactObject({
    documentFingerprint,
    expectedGeneration,
    transactionName: makeTransactionName(transactionPrefix, "create text note", runId),
    operations: [operation],
  });
  const preview = await previewChangeSet(client, changeSet, "create_text_note");
  const apply = await applyChangeSet(client, changeSet, preview, "create_text_note");
  const change = findChange(apply, "create_text_note");
  const textNoteId = getCreatedElementId(change);
  assert(textNoteId, "create_text_note applied but did not return a text note id.");
  console.log(`Create text note OK: text note ${textNoteId} in ${target.label}`);
  return { apply, textNoteId };
}

async function tryTagRoom(
  client,
  {
    documentFingerprint,
    expectedGeneration,
    transactionPrefix,
    runId,
    preferredRoomId,
    preferredRoomUniqueId,
    preferredLevelId,
    preferredLocation,
    tagTypeSelector,
  }
) {
  const roomTagTypes = await catalog(client, {
    kind: "tagTypes",
    filter: { categories: ["OST_RoomTags"] },
    preset: "annotation",
    limit: 50,
    includeTotalCount: true,
  });
  assertCatalogPage(roomTagTypes, "tagTypes");
  const evidence = {
    tagTypeCount: Array.isArray(roomTagTypes.items) ? roomTagTypes.items.length : 0,
    tagTypeSamples: sampleEvidenceItems(roomTagTypes.items),
    viewCount: 0,
    viewSamples: [],
    candidateRoomCount: 0,
    candidateRoomSamples: [],
    blockedPreviewMessages: [],
    tagTypeSelector,
  };
  const tagType = selectTagType(roomTagTypes.items, tagTypeSelector);
  if (!tagType) {
    return {
      reason: tagTypeSelector
        ? `No loaded room tag type matched ${formatTagTypeSelector(tagTypeSelector)}.`
        : "No room tag type was available in the active model.",
      evidence,
    };
  }

  const views = await listAnnotationViews(client, documentFingerprint, ["FloorPlan", "CeilingPlan", "EngineeringPlan", "Section"]);
  evidence.viewCount = views.length;
  evidence.viewSamples = sampleEvidenceItems(views);
  if (views.length === 0) {
    return { reason: "No printable non-template plan or section view was available for room tags.", evidence };
  }

  let generation = expectedGeneration;
  let lastBlockedReason = "";
  for (const view of prioritizeByLevel(views, preferredLevelId)) {
    const roomFilter = view.associatedLevelId ? { levelIds: [String(view.associatedLevelId)] } : {};
    const rooms = await callRequiredTool(client, "revit.get_rooms", {
      documentFingerprint,
      filter: roomFilter,
      fields: ["id", "uniqueId", "number", "name", "levelId", "location"],
      preset: "summary",
      includeUnplaced: false,
      limit: 100,
      includeTotalCount: true,
    });
    const candidates = prioritizeById(Array.isArray(rooms.items) ? rooms.items : [], preferredRoomId);
    evidence.candidateRoomCount += candidates.filter((room) => room?.id).length;
    appendEvidenceSamples(evidence.candidateRoomSamples, candidates);
    for (const room of candidates) {
      if (!room?.id) continue;
      const location = roomTagLocation(room, preferredRoomId, preferredLocation);
      if (!location) continue;
      const operation = {
        id: "tag-smoke-room",
        type: "tag_room",
        roomId: String(room.id),
        expectedUniqueId:
          String(room.id) === String(preferredRoomId) ? preferredRoomUniqueId : stringOrUndefined(room.uniqueId),
        viewId: String(view.id),
        location,
        tagTypeId: String(tagType.id),
        hasLeader: false,
        orientation: "Horizontal",
      };
      const changeSet = compactObject({
        documentFingerprint,
        expectedGeneration: generation,
        transactionName: makeTransactionName(transactionPrefix, "tag room", runId),
        operations: [operation],
      });
      const previewResult = await tryPreviewChangeSet(client, changeSet, "tag_room");
      if (!previewResult.ready) {
        lastBlockedReason = previewResult.reason;
        appendBoundedItem(evidence.blockedPreviewMessages, previewResult.reason, 5);
        continue;
      }
      const apply = await applyChangeSet(client, changeSet, previewResult.preview, "tag_room");
      const change = findChange(apply, "tag_room");
      const roomTagId = getCreatedElementId(change);
      assert(roomTagId, "tag_room applied but did not return a room tag id.");
      console.log(`Tag room OK: room ${room.id} tagged as ${roomTagId} in view ${view.id}`);
      return {
        apply,
        roomTagId,
        roomId: String(room.id),
        roomUniqueId: stringOrUndefined(room.uniqueId),
        viewId: String(view.id),
        tagTypeId: String(tagType.id),
        tagTypeName: stringOrUndefined(tagType.name),
        evidence,
      };
    }
  }

  return {
    reason: lastBlockedReason
      ? `No room tag candidate previewed ready. Last blocked reason: ${lastBlockedReason}`
      : "No placed room with a taggable location was available in a printable plan or section view.",
    evidence,
  };
}

async function tryTagElement(
  client,
  { documentFingerprint, expectedGeneration, transactionPrefix, runId, preferredElementId, levelElevationMm, tagTypeSelector }
) {
  let tagTypes = await catalog(client, {
    kind: "tagTypes",
    filter: { categories: ["OST_WallTags"] },
    preset: "annotation",
    limit: 50,
    includeTotalCount: true,
  });
  assertCatalogPage(tagTypes, "tagTypes");
  const evidence = {
    tagTypeCatalogs: [
      {
        categories: ["OST_WallTags"],
        tagTypeCount: Array.isArray(tagTypes.items) ? tagTypes.items.length : 0,
        tagTypeSamples: sampleEvidenceItems(tagTypes.items),
      },
    ],
    viewCount: 0,
    viewSamples: [],
    candidateElementCount: 0,
    candidateElementSamples: [],
    blockedPreviewMessages: [],
    tagTypeSelector,
  };
  let tagType = selectTagType(tagTypes.items, tagTypeSelector);
  if (!tagType) {
    tagTypes = await catalog(client, {
      kind: "tagTypes",
      filter: { categories: ["OST_MultiCategoryTags"] },
      preset: "annotation",
      limit: 50,
      includeTotalCount: true,
    });
    assertCatalogPage(tagTypes, "tagTypes");
    evidence.tagTypeCatalogs.push({
      categories: ["OST_MultiCategoryTags"],
      tagTypeCount: Array.isArray(tagTypes.items) ? tagTypes.items.length : 0,
      tagTypeSamples: sampleEvidenceItems(tagTypes.items),
    });
    tagType = selectTagType(tagTypes.items, tagTypeSelector);
  }
  if (!tagType) {
    return {
      reason: tagTypeSelector
        ? `No loaded wall or multi-category tag FamilySymbol matched ${formatTagTypeSelector(tagTypeSelector)}.`
        : "No wall or multi-category tag FamilySymbol was available in the active model.",
      evidence,
    };
  }

  const views = await listAnnotationViews(client, documentFingerprint, ["FloorPlan", "CeilingPlan", "EngineeringPlan", "Section", "DraftingView"]);
  evidence.viewCount = views.length;
  evidence.viewSamples = sampleEvidenceItems(views);
  if (views.length === 0) {
    return { reason: "No printable non-template graphical view was available for element tags.", evidence };
  }

  let generation = expectedGeneration;
  let lastBlockedReason = "";
  for (const view of views) {
    const query = await callRequiredTool(client, "revit.query", {
      documentFingerprint,
      filter: { viewId: String(view.id), categories: ["OST_Walls"] },
      fields: ["id", "uniqueId", "category", "class", "name"],
      preset: "summary",
      limit: 100,
      includeTotalCount: true,
    });
    const candidates = prioritizeById(Array.isArray(query.items) ? query.items : [], preferredElementId);
    evidence.candidateElementCount += candidates.filter((element) => element?.id).length;
    appendEvidenceSamples(evidence.candidateElementSamples, candidates);
    for (const element of candidates) {
      if (!element?.id) continue;
      const operation = {
        id: "tag-smoke-element",
        type: "tag_element",
        elementId: String(element.id),
        expectedUniqueId: stringOrUndefined(element.uniqueId),
        viewId: String(view.id),
        tagTypeId: String(tagType.id),
        position: pointMm(1000, 1000, numericOrDefault(levelElevationMm, 0)),
        hasLeader: true,
        orientation: "Horizontal",
      };
      const changeSet = compactObject({
        documentFingerprint,
        expectedGeneration: generation,
        transactionName: makeTransactionName(transactionPrefix, "tag element", runId),
        operations: [operation],
      });
      const previewResult = await tryPreviewChangeSet(client, changeSet, "tag_element");
      if (!previewResult.ready) {
        lastBlockedReason = previewResult.reason;
        appendBoundedItem(evidence.blockedPreviewMessages, previewResult.reason, 5);
        continue;
      }
      const apply = await applyChangeSet(client, changeSet, previewResult.preview, "tag_element");
      const change = findChange(apply, "tag_element");
      const elementTagId = getCreatedElementId(change);
      assert(elementTagId, "tag_element applied but did not return an element tag id.");
      console.log(`Tag element OK: element ${element.id} tagged as ${elementTagId} in view ${view.id}`);
      return {
        apply,
        elementTagId,
        elementId: String(element.id),
        elementUniqueId: stringOrUndefined(element.uniqueId),
        viewId: String(view.id),
        tagTypeId: String(tagType.id),
        tagTypeName: stringOrUndefined(tagType.name),
        evidence,
      };
    }
  }

  return {
    reason: lastBlockedReason
      ? `No element tag candidate previewed ready. Last blocked reason: ${lastBlockedReason}`
      : "No wall visible in a printable non-template graphical view was available for element tags.",
    evidence,
  };
}

async function listAnnotationViews(client, documentFingerprint, acceptedTypes) {
  const result = await callRequiredTool(client, "revit.get_views", {
    documentFingerprint,
    filter: { isTemplate: false, isGraphical: true, canBePrinted: true },
    preset: "summary",
    limit: 500,
    includeTotalCount: true,
  });
  assert(Array.isArray(result.items), "revit.get_views did not return an items array for annotation views.");
  const accepted = new Set(acceptedTypes);
  return result.items.filter((view) => typeof view?.id === "string" && accepted.has(viewTypeOf(view)));
}

async function preflightRequiredTagCoverage(
  client,
  { documentFingerprint, requireRoomTag, requireElementTag, roomTagSelector, elementTagSelector }
) {
  const result = {
    status: "passed",
    failures: [],
    room: null,
    element: null,
  };

  if (requireRoomTag) {
    const roomTagTypes = await catalog(client, {
      kind: "tagTypes",
      filter: { categories: ["OST_RoomTags"] },
      preset: "annotation",
      limit: 50,
      includeTotalCount: true,
    });
    assertCatalogPage(roomTagTypes, "tagTypes");
    const views = await listAnnotationViews(client, documentFingerprint, [
      "FloorPlan",
      "CeilingPlan",
      "EngineeringPlan",
      "Section",
    ]);
    const selected = selectTagType(roomTagTypes.items, roomTagSelector);
    result.room = compactObject({
      selector: roomTagSelector,
      tagTypeCount: Array.isArray(roomTagTypes.items) ? roomTagTypes.items.length : 0,
      selectedTagType: selected ? sampleEvidenceItems([selected], 1)[0] : undefined,
      tagTypeSamples: sampleEvidenceItems(roomTagTypes.items),
      viewCount: views.length,
      viewSamples: sampleEvidenceItems(views),
    });
    if (!selected) {
      result.failures.push(
        roomTagSelector
          ? `No loaded room tag type matched ${formatTagTypeSelector(roomTagSelector)}.`
          : "No loaded room tag type is available."
      );
    }
    if (views.length === 0) {
      result.failures.push("No printable non-template plan or section view is available for room tags.");
    }
  }

  if (requireElementTag) {
    const wallTagTypes = await catalog(client, {
      kind: "tagTypes",
      filter: { categories: ["OST_WallTags"] },
      preset: "annotation",
      limit: 50,
      includeTotalCount: true,
    });
    assertCatalogPage(wallTagTypes, "tagTypes");
    const multiTagTypes = await catalog(client, {
      kind: "tagTypes",
      filter: { categories: ["OST_MultiCategoryTags"] },
      preset: "annotation",
      limit: 50,
      includeTotalCount: true,
    });
    assertCatalogPage(multiTagTypes, "tagTypes");
    const tagCandidates = [...(wallTagTypes.items ?? []), ...(multiTagTypes.items ?? [])];
    const selected = selectTagType(tagCandidates, elementTagSelector);
    const views = await listAnnotationViews(client, documentFingerprint, [
      "FloorPlan",
      "CeilingPlan",
      "EngineeringPlan",
      "Section",
      "DraftingView",
    ]);
    result.element = compactObject({
      selector: elementTagSelector,
      tagTypeCatalogs: [
        {
          categories: ["OST_WallTags"],
          tagTypeCount: Array.isArray(wallTagTypes.items) ? wallTagTypes.items.length : 0,
          tagTypeSamples: sampleEvidenceItems(wallTagTypes.items),
        },
        {
          categories: ["OST_MultiCategoryTags"],
          tagTypeCount: Array.isArray(multiTagTypes.items) ? multiTagTypes.items.length : 0,
          tagTypeSamples: sampleEvidenceItems(multiTagTypes.items),
        },
      ],
      selectedTagType: selected ? sampleEvidenceItems([selected], 1)[0] : undefined,
      viewCount: views.length,
      viewSamples: sampleEvidenceItems(views),
    });
    if (!selected) {
      result.failures.push(
        elementTagSelector
          ? `No loaded wall or multi-category tag FamilySymbol matched ${formatTagTypeSelector(elementTagSelector)}.`
          : "No loaded wall or multi-category tag FamilySymbol is available."
      );
    }
    if (views.length === 0) {
      result.failures.push("No printable non-template graphical view is available for element tags.");
    }
  }

  if (result.failures.length > 0) {
    result.status = "failed";
  }

  return result;
}

function formatTagPreflightFailure(preflight) {
  return [
    "Required tag smoke preflight failed before model mutations.",
    ...preflight.failures.map((failure) => `- ${failure}`),
    "Use a curated disposable RVT with loaded room and wall/multi-category tag types plus printable plan/section views, or rerun without --require-tags for preview-only tag evidence.",
  ].join("\n");
}

async function tryPreviewChangeSet(client, changeSet, operationName) {
  const preview = await callRequiredTool(client, "revit.preview_change_set", changeSet);
  assert(Array.isArray(preview.changes), `${operationName} preview did not return changes.`);
  if (preview.ready === true && preview.changes.every((change) => change.status === "ready")) {
    assert(preview.previewId, `${operationName} preview did not return previewId.`);
    assert(preview.changeSetHash, `${operationName} preview did not return changeSetHash.`);
    console.log(`Preview OK: ${operationName} (${preview.previewId})`);
    return { ready: true, preview };
  }

  const blocked = preview.changes.find((change) => change.status === "blocked");
  return {
    ready: false,
    preview,
    reason: blocked?.message ?? formatChanges(preview.changes),
  };
}

function prioritizeByLevel(items, preferredLevelId) {
  if (!preferredLevelId) return items;
  return items.slice().sort((left, right) => {
    const leftMatch = String(left?.associatedLevelId ?? "") === String(preferredLevelId) ? 0 : 1;
    const rightMatch = String(right?.associatedLevelId ?? "") === String(preferredLevelId) ? 0 : 1;
    return leftMatch - rightMatch;
  });
}

function prioritizeById(items, preferredId) {
  if (!preferredId) return items;
  return items.slice().sort((left, right) => {
    const leftMatch = String(left?.id ?? "") === String(preferredId) ? 0 : 1;
    const rightMatch = String(right?.id ?? "") === String(preferredId) ? 0 : 1;
    return leftMatch - rightMatch;
  });
}

function roomTagLocation(room, preferredRoomId, preferredLocation) {
  if (preferredLocation && String(room?.id ?? "") === String(preferredRoomId)) return preferredLocation;
  if (room?.location?.x && room?.location?.y) {
    return point2Mm(unitValueNumber(room.location.x), unitValueNumber(room.location.y));
  }
  return undefined;
}

function viewTypeOf(view) {
  return String(view?.type ?? view?.viewType ?? "");
}

async function assertElementDeletedById(client, elementId) {
  const query = await callRequiredTool(client, "revit.query", {
    filter: { elementIds: [elementId] },
    fields: ["id", "uniqueId", "category", "class", "name"],
    limit: 10,
    includeTotalCount: true,
  });

  const items = Array.isArray(query.items) ? query.items : [];
  assert(
    !items.some((item) => String(item.id) === String(elementId)),
    `delete_element reported success, but revit.query still returned element ${elementId}.`
  );
}

function chooseLevel(levels) {
  const buildingStory = levels.find((level) => level?.isBuildingStory);
  return buildingStory ?? levels[0];
}

function chooseSmokeLevelElevationMm(levels, fallbackElevationMm) {
  const elevations = levels
    .map((level) => Number(level?.elevation?.value))
    .filter((value) => Number.isFinite(value));
  const highest = elevations.length > 0 ? Math.max(...elevations) : fallbackElevationMm;
  return highest + 3000;
}

function findChange(result, type) {
  const change = result.changes.find((item) => item.type === type);
  assert(change, `${type} result did not include a matching change item.`);
  return change;
}

function findChangeByOperationId(result, operationId, type) {
  const change = result.changes.find((item) => item.operationId === operationId && (!type || item.type === type));
  assert(change, `${type ?? "change"} result did not include operation ${operationId}.`);
  return change;
}

function getCreatedElementId(change) {
  return stringOrUndefined(change?.after?.id) ?? stringOrUndefined(change?.target?.elementId);
}

function getCopiedElementId(change) {
  const copiedIds = change?.after?.copiedElementIds;
  if (Array.isArray(copiedIds) && copiedIds.length > 0) return stringOrUndefined(String(copiedIds[0]));
  const copiedElements = change?.after?.copiedElements;
  if (Array.isArray(copiedElements) && copiedElements.length > 0) {
    return stringOrUndefined(String(copiedElements[0]?.id));
  }
  return undefined;
}

function assertPinnedState(change, expectedPinned) {
  assert(change?.after, "set_element_pinned apply did not include after state.");
  assert(
    change.after.pinned === expectedPinned,
    `Expected pinned=${expectedPinned} but observed pinned=${String(change.after.pinned)}.`
  );
}

function assertDeletedState(change, expectedElementId) {
  assert(change?.after, "delete_element apply did not include after state.");
  assert(change.after.deleted === true, "delete_element apply did not report deleted=true.");
  const deletedElementIds = Array.isArray(change.after.deletedElementIds) ? change.after.deletedElementIds.map(String) : [];
  assert(
    deletedElementIds.includes(String(expectedElementId)),
    `delete_element did not report expected deleted element id ${expectedElementId}.`
  );
}

function assertParameterValue(change, expectedValue) {
  assert(change?.after, "set_parameter apply did not include after state.");
  assert(
    String(change.after.value) === String(expectedValue),
    `Expected set_parameter after.value=${expectedValue} but observed ${String(change.after.value)}.`
  );
}

function assertMoveYChanged(change, expectedDeltaMm) {
  const beforeLocation = change?.before;
  const afterLocation = change?.after?.location;
  assert(beforeLocation, "move_element apply did not include a before location snapshot.");
  assert(afterLocation, "move_element apply did not include an after.location snapshot.");

  const beforeYs = extractLocationYValues(beforeLocation);
  const afterYs = extractLocationYValues(afterLocation);
  assert(beforeYs.length > 0, "move_element before location did not include Y coordinates.");
  assert(afterYs.length === beforeYs.length, "move_element before/after locations are not comparable.");

  const toleranceMm = 0.01;
  const deltas = afterYs.map((afterY, index) => afterY - beforeYs[index]);
  for (const delta of deltas) {
    assert(
      Math.abs(delta - expectedDeltaMm) <= toleranceMm,
      `Expected Y delta ${expectedDeltaMm} mm but observed ${round(delta)} mm.`
    );
  }
}

function extractLocationYValues(location) {
  if (location?.point?.y) return [unitValueNumber(location.point.y)];
  const values = [];
  if (location?.start?.y) values.push(unitValueNumber(location.start.y));
  if (location?.end?.y) values.push(unitValueNumber(location.end.y));
  if (location?.min?.y) values.push(unitValueNumber(location.min.y));
  if (location?.max?.y) values.push(unitValueNumber(location.max.y));
  return values.filter((value) => Number.isFinite(value));
}

function pointFromLocationSnapshot(location, fallbackPoint) {
  const source = location?.start ?? location?.point ?? location?.min;
  if (!source) return fallbackPoint;
  return pointMm(
    numericOrDefault(source?.x?.value, unitValueNumber(fallbackPoint.x)),
    numericOrDefault(source?.y?.value, unitValueNumber(fallbackPoint.y)),
    numericOrDefault(source?.z?.value, unitValueNumber(fallbackPoint.z))
  );
}

function offsetPointMm(point, x, y, z) {
  return pointMm(
    unitValueNumber(point.x) + x,
    unitValueNumber(point.y) + y,
    unitValueNumber(point.z) + z
  );
}

function unitValueNumber(unitValue) {
  return Number(unitValue?.value);
}

function pointMm(x, y, z) {
  return {
    x: unitMm(x),
    y: unitMm(y),
    z: unitMm(z),
  };
}

function point2Mm(x, y) {
  return {
    x: unitMm(x),
    y: unitMm(y),
  };
}

function unitMm(value) {
  return {
    value,
    unit: "mm",
    system: "metric",
  };
}

function makeRunId() {
  return new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 14);
}

function makeTransactionName(prefix, action, runId) {
  const raw = `${prefix.trim()} ${action} ${runId}`.replace(/\s+/g, " ");
  return raw.length <= 128 ? raw : raw.slice(0, 128);
}

function compactObject(value) {
  if (Array.isArray(value)) return value.map((entry) => compactObject(entry));
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(
    Object.entries(value)
      .filter(([, entry]) => entry !== undefined)
      .map(([key, entry]) => [key, compactObject(entry)])
  );
}

function numericOrUndefined(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : undefined;
}

function numericOrDefault(value, fallback) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function stringOrUndefined(value) {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function makeTagTypeSelector(id, nameContains) {
  const selector = compactObject({
    id: stringOrUndefined(String(id ?? "").trim()),
    nameContains: stringOrUndefined(String(nameContains ?? "").trim()),
  });
  return Object.keys(selector).length > 0 ? selector : undefined;
}

function selectTagType(items, selector) {
  const candidates = Array.isArray(items) ? items.filter((item) => typeof item?.id === "string" && item.id.length > 0) : [];
  if (selector?.id) {
    return candidates.find((item) => String(item?.id ?? "") === String(selector.id));
  }
  if (selector?.nameContains) {
    const needle = selector.nameContains.toLowerCase();
    return candidates.find((item) => tagTypeSearchText(item).includes(needle));
  }
  return candidates[0];
}

function tagTypeSearchText(item) {
  return [item?.name, item?.familyName, item?.type]
    .filter((value) => typeof value === "string" && value.length > 0)
    .join(" ")
    .toLowerCase();
}

function formatTagTypeSelector(selector) {
  if (!selector) return "auto";
  const parts = [];
  if (selector.id) parts.push(`id=${selector.id}`);
  if (selector.nameContains) parts.push(`name contains "${selector.nameContains}"`);
  return parts.join(", ") || "auto";
}

function sampleEvidenceItems(items, limit = 5) {
  if (!Array.isArray(items)) return [];
  return items.slice(0, limit).map((item) =>
    compactObject({
      id: item?.id !== undefined ? String(item.id) : undefined,
      uniqueId: stringOrUndefined(item?.uniqueId),
      name: stringOrUndefined(item?.name),
      number: stringOrUndefined(item?.number),
      category: stringOrUndefined(item?.category),
      class: stringOrUndefined(item?.class),
      type: stringOrUndefined(item?.type ?? item?.viewType),
      familyName: stringOrUndefined(item?.familyName),
      levelId: item?.levelId !== undefined ? String(item.levelId) : undefined,
      associatedLevelId: item?.associatedLevelId !== undefined ? String(item.associatedLevelId) : undefined,
    })
  );
}

function appendEvidenceSamples(buffer, items, limit = 5) {
  if (!Array.isArray(buffer) || buffer.length >= limit || !Array.isArray(items)) return;
  for (const sample of sampleEvidenceItems(items, limit)) {
    if (!sample.id || buffer.some((existing) => existing.id === sample.id)) continue;
    buffer.push(sample);
    if (buffer.length >= limit) break;
  }
}

function appendBoundedItem(buffer, item, limit) {
  if (!Array.isArray(buffer) || item === undefined || item === null || buffer.length >= limit) return;
  buffer.push(String(item));
}

function round(value) {
  return Math.round(value * 1000000) / 1000000;
}

function appendBounded(buffer, text, maxLength) {
  buffer.push(text);
  while (buffer.join("").length > maxLength && buffer.length > 1) {
    buffer.shift();
  }
}

function formatToolFailure(name, result) {
  const text = (result.content ?? [])
    .filter((item) => item.type === "text")
    .map((item) => item.text)
    .join("\n")
    .trim();
  const error = result.structuredContent?.data?.error;
  const action = error?.suggestedNextAction ? `\nSuggested next action: ${error.suggestedNextAction}` : "";
  return `${name} failed.${text ? `\n${text}` : ""}${action}`;
}

function formatChanges(changes) {
  if (!Array.isArray(changes)) return "(no changes returned)";
  return changes
    .map((change) => {
      const id = change.operationId ? ` ${change.operationId}` : "";
      const message = change.message ? `: ${change.message}` : "";
      return `- ${change.type}${id} ${change.status}${message}`;
    })
    .join("\n");
}

function formatError(error) {
  if (error instanceof Error) return error.message;
  return String(error);
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function closeQuietly(client) {
  try {
    await client.close();
  } catch {
    // Ignore close errors so the original smoke failure remains visible.
  }
}

function printHelp() {
  console.log(`Usage: node scripts/live-smoke-revit.mjs [options]

Runs a live Revit MCP smoke against the active Revit project:
  1. revit.status
  2. read smoke: views, sheets, current view, current-view elements, selection, model analysis, material quantities
  3. revit.get_levels
  4. revit.catalog for wall and floor types
  5. preview/apply create_level
  6. preview/apply create_grid
  7. blocked preview for duplicate create_grid
  8. preview/apply create_floor
  9. preview/apply create_wall
  10. revit.query and revit.describe_parameters for created elements
  11. blocked preview for mismatched expectedUniqueId
  12. preview/apply place_family_instance with expectedHostUniqueId for hosted symbols or levelId-only for level-based symbols
  13. preview/apply room boundary walls
  14. preview/apply create_room, then revit.get_rooms read-back with positive area
  15. preview/apply create_sheet and read it back with revit.get_sheets
  16. preview/apply place_view_on_sheet when an unplaced printable view exists
  17. preview/apply create_text_note when the current view supports annotations
  18. preview/apply tag_room with expectedUniqueId when a room tag type, room, and plan/section view are available
  19. preview/apply tag_element with expectedUniqueId when a wall/multi-category tag type and visible wall are available
  20. preview/apply guarded set_parameter on the created wall
  21. revit.catalog for compatible wall type changes
  22. preview/apply guarded change_element_type when an alternate valid type exists
  23. preview/apply guarded move_element
  24. assert the wall Y location changed by --move-y-mm
  25. preview/apply guarded rotate_element
  26. preview/apply guarded copy_element
  27. preview/apply guarded set_element_pinned true
  28. blocked preview for moving a pinned element
  29. rejected apply for mismatched changeSetHash
  30. preview/apply guarded set_element_pinned false
  31. preview/apply guarded delete_element for the copied smoke wall
  32. revit.cancel_request no-op probe
  33. revitctl direct bridge probe proving apply_change_set cannot be mislabeled as operationKind=read

Options:
  --document-fingerprint <value>  Optional active document fingerprint to pin the run.
  --wall-length-mm <number>       Wall baseline length in millimeters. Default: ${DEFAULT_WALL_LENGTH_MM}
  --move-y-mm <number>            Y translation in millimeters. Default: ${DEFAULT_MOVE_Y_MM}
  --wall-height-mm <number>       Wall height in millimeters. Default: ${DEFAULT_WALL_HEIGHT_MM}
  --transaction-prefix <text>     Prefix for Revit transaction names. Default: "${DEFAULT_TRANSACTION_PREFIX}"
  --launcher-path <path>          MCP launcher path. Default: %LOCALAPPDATA%\\RevitMcpNext\\launch-revit-mcp-next.cmd
  --launcher <path>               Alias for --launcher-path.
  --expected-revit-year <year>    Fail unless revit.status reports this Revit major year.
  --summary-path <path>           Write machine-readable smoke-summary.json evidence.
  --require-type-change           Fail when no alternate valid wall type is available for change_element_type.
  --skip-type-change              Allow type-change coverage to be skipped when no alternate type exists. Default.
  --require-room-tag              Fail when tag_room cannot be applied.
  --require-element-tag           Fail when tag_element cannot be applied.
  --require-tags                  Require both tag_room and tag_element coverage.
  --skip-room-tag                 Allow tag_room coverage to be skipped. Default.
  --skip-element-tag              Allow tag_element coverage to be skipped. Default.
  --skip-tags                     Allow both tag operations to be skipped. Default.
  --room-tag-type-id <id>         Require tag_room smoke to use this loaded room tag FamilySymbol id.
  --room-tag-type-name-contains <text>
                                  Require tag_room smoke to use a loaded room tag FamilySymbol whose name/family contains this text.
  --element-tag-type-id <id>      Require tag_element smoke to use this loaded wall or multi-category tag FamilySymbol id.
  --element-tag-type-name-contains <text>
                                  Require tag_element smoke to use a loaded wall or multi-category tag FamilySymbol whose name/family contains this text.
  --status-only                   Only verify tool discovery, revit.status, and active document readiness.
  -h, --help                      Show this help.
`);
}
