#!/usr/bin/env node

import { existsSync } from "node:fs";
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
  "revit.catalog",
  "revit.query",
  "revit.preview_change_set",
  "revit.apply_change_set",
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

  try {
    console.log("Connecting to MCP server over stdio...");
    await client.connect(transport);

    await verifyRequiredTools(client);

    const status = await callRequiredTool(client, "revit.status", {});
    assert(status.connected === true, "revit.status did not report a connected Revit bridge.");
    assert(status.activeDocument, "Revit is connected but there is no active project document.");

    const activeDocument = status.activeDocument;
    const documentFingerprint = options.documentFingerprint ?? activeDocument.fingerprint;
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
    const gridName = `MCP-${runId}`;
    const gridOperation = {
      id: "create-smoke-grid",
      type: "create_grid",
      name: gridName,
      start: pointMm(-1000, -1000, levelElevationMm),
      end: pointMm(options.wallLengthMm + 1000, -1000, levelElevationMm),
    };
    const gridTransaction = makeTransactionName(options.transactionPrefix, "create grid", runId);
    const gridChangeSet = compactObject({
      documentFingerprint,
      expectedGeneration: startingGeneration,
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
      levelId: String(level.id),
      outline: [
        pointMm(0, -options.wallLengthMm - 1000, levelElevationMm),
        pointMm(options.wallLengthMm, -options.wallLengthMm - 1000, levelElevationMm),
        pointMm(options.wallLengthMm, -1000, levelElevationMm),
        pointMm(0, -1000, levelElevationMm),
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
      levelId: String(level.id),
      start: pointMm(0, 0, levelElevationMm),
      end: pointMm(options.wallLengthMm, 0, levelElevationMm),
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
    console.log(`Query OK: wall ${queriedWall.id} (${queriedWall.name ?? queriedWall.class ?? "Wall"})`);

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
      const changeTypeGeneration = numericOrUndefined(createApply.generation);
      const changeTypeOperation = {
        id: "change-smoke-wall-type",
        type: "change_element_type",
        elementId: wallId,
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

    const moveGeneration = numericOrUndefined(changeTypeApply?.generation ?? createApply.generation);
    const moveOperation = {
      id: "move-smoke-wall",
      type: "move_element",
      elementId: wallId,
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
    const axisStart = pointFromLocationSnapshot(moveChange?.after?.location, pointMm(0, options.moveYMm, levelElevationMm));
    const axisEnd = offsetPointMm(axisStart, 0, 0, 1000);
    const rotateOperation = {
      id: "rotate-smoke-wall",
      type: "rotate_element",
      elementId: wallId,
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
    await queryWallById(client, copiedWallId);
    console.log(`Copy wall OK: source ${wallId} copied to ${copiedWallId}`);

    const pinGeneration = numericOrUndefined(copyApply.generation);
    const pinOperation = {
      id: "pin-smoke-wall",
      type: "set_element_pinned",
      elementId: copiedWallId,
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
    console.log("Live smoke passed.");
  } catch (error) {
    if (stderr.length > 0) {
      console.error("");
      console.error("MCP server stderr:");
      console.error(stderr.join("").trim());
    }
    throw error;
  } finally {
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
    requireTypeChange: false,
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
      case "--require-type-change":
        options.requireTypeChange = true;
        break;
      case "--skip-type-change":
        options.requireTypeChange = false;
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

function validateOptions(options) {
  assert(options.wallLengthMm > 0, "--wall-length-mm must be greater than zero.");
  assert(options.wallHeightMm > 0, "--wall-height-mm must be greater than zero.");
  assert(options.moveYMm !== 0, "--move-y-mm must be non-zero because Revit rejects zero-length moves.");
  assert(
    typeof options.transactionPrefix === "string" && options.transactionPrefix.trim().length >= 3,
    "--transaction-prefix must contain at least 3 non-whitespace characters."
  );
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
      args: ["/d", "/c", launcherPath],
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

async function queryElementById(client, className, elementId) {
  let cursor = undefined;
  let scanned = 0;
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
    cursor = typeof query.cursor === "string" && query.cursor.length > 0 ? query.cursor : undefined;
  } while (cursor);

  throw new Error(`Created ${className} ${elementId} was not found by revit.query after scanning ${scanned} item(s).`);
}

function chooseLevel(levels) {
  const buildingStory = levels.find((level) => level?.isBuildingStory);
  return buildingStory ?? levels[0];
}

function findChange(result, type) {
  const change = result.changes.find((item) => item.type === type);
  assert(change, `${type} result did not include a matching change item.`);
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
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined));
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
  2. revit.get_levels
  3. revit.catalog for wall and floor types
  4. preview/apply create_grid
  5. blocked preview for duplicate create_grid
  6. preview/apply create_floor
  7. preview/apply create_wall
  8. revit.query for created elements
  9. revit.catalog for compatible wall type changes
  10. preview/apply change_element_type when an alternate valid type exists
  11. preview/apply move_element
  12. assert the wall Y location changed by --move-y-mm
  13. preview/apply rotate_element
  14. preview/apply copy_element
  15. preview/apply set_element_pinned true
  16. blocked preview for moving a pinned element
  17. rejected apply for mismatched changeSetHash
  18. preview/apply set_element_pinned false

Options:
  --document-fingerprint <value>  Optional active document fingerprint to pin the run.
  --wall-length-mm <number>       Wall baseline length in millimeters. Default: ${DEFAULT_WALL_LENGTH_MM}
  --move-y-mm <number>            Y translation in millimeters. Default: ${DEFAULT_MOVE_Y_MM}
  --wall-height-mm <number>       Wall height in millimeters. Default: ${DEFAULT_WALL_HEIGHT_MM}
  --transaction-prefix <text>     Prefix for Revit transaction names. Default: "${DEFAULT_TRANSACTION_PREFIX}"
  --launcher-path <path>          MCP launcher path. Default: %LOCALAPPDATA%\\RevitMcpNext\\launch-revit-mcp-next.cmd
  --launcher <path>               Alias for --launcher-path.
  --require-type-change           Fail when no alternate valid wall type is available for change_element_type.
  --skip-type-change              Allow type-change coverage to be skipped when no alternate type exists. Default.
  -h, --help                      Show this help.
`);
}
