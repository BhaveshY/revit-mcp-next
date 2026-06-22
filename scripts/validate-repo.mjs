import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";

const root = process.cwd();
const bannedPatterns = [
  /\bnpx\b/i,
  /npm\s+install\s+-g/i,
];

const checkedExtensions = new Set([".json", ".toml", ".md", ".cmd", ".ps1", ".yml", ".yaml"]);
const failures = [];

for (const file of walk(root)) {
  if (file.includes("\\node_modules\\") || file.includes("\\.git\\")) continue;
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

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    const stat = statSync(path);
    if (stat.isDirectory()) {
      yield* walk(path);
    } else {
      yield path;
    }
  }
}

