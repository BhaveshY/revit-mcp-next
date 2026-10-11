#!/usr/bin/env node
// esbuild bundle of the broker (SPEC §12.1): one self-contained ESM file per entry, no node_modules at runtime.
//   artifacts/broker/revit-mcp.mjs       MCP stdio server   (entry broker/dist/src/index.js)
//   artifacts/broker/revit-mcp-cli.mjs   doctor | clients | raw | support | settings (entry broker/dist/src/cli/main.js)
//   artifacts/broker/build-info.json     {version, gitSha, builtAt, sdk, node}; the broker reads it next to the bundle
// Run after `npm run build` (tsc). Usage: node broker/build/bundle.mjs [--out <dir>]

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..", "..");
const args = process.argv.slice(2);
const outIndex = args.indexOf("--out");
const outDir = resolve(outIndex >= 0 ? args[outIndex + 1] : join(repo, "artifacts", "broker"));

const entries = [
  { entry: join(repo, "broker", "dist", "src", "index.js"), out: "revit-mcp.mjs" },
  { entry: join(repo, "broker", "dist", "src", "cli", "main.js"), out: "revit-mcp-cli.mjs" },
];
for (const { entry } of entries) {
  if (!existsSync(entry)) {
    console.error(`bundle: ${entry} is missing; run "npm run build" first`);
    process.exit(1);
  }
}

const readJson = (path) => JSON.parse(readFileSync(path, "utf8"));
const version = readJson(join(repo, "package.json")).version;
const sdk = (() => {
  try {
    return readJson(join(repo, "node_modules", "@modelcontextprotocol", "server", "package.json")).version;
  } catch {
    return "";
  }
})();
const git = (...a) => {
  try {
    return execFileSync("git", a, { cwd: repo, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true }).trim();
  } catch {
    return "";
  }
};
const gitSha = git("rev-parse", "--short=12", "HEAD");
const dirty = git("status", "--porcelain", "--untracked-files=no").length > 0;

mkdirSync(outDir, { recursive: true });
// ESM output still meets CommonJS dependencies that call require(): give them a real one.
const banner = [
  `import { createRequire as __rmnCreateRequire } from "node:module";`,
  `const require = __rmnCreateRequire(import.meta.url);`,
].join("\n");

for (const { entry, out } of entries) {
  const outfile = join(outDir, out);
  await build({
    entryPoints: [entry],
    outfile,
    bundle: true,
    platform: "node",
    format: "esm",
    target: "node24",
    banner: { js: banner },
    legalComments: "none",
    sourcemap: false,
    minify: false,
    logLevel: "warning",
  });
  console.log(`bundle: ${outfile} (${Math.round(statSync(outfile).size / 1024)} KB)`);
}

const info = {
  version,
  gitSha: dirty && gitSha ? `${gitSha}-dirty` : gitSha,
  builtAt: new Date().toISOString(),
  sdk,
  node: ">=24",
};
writeFileSync(join(outDir, "build-info.json"), JSON.stringify(info, null, 2) + "\n", "utf8");
console.log(`bundle: build-info ${info.version} ${info.gitSha}`);
