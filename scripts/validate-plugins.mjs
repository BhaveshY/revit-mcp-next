#!/usr/bin/env node
// Plugin/skill gate (CI): manifests parse, marketplace sources resolve, versions match package.json, every skill has a
// SKILL.md with name/description frontmatter, the Codex and Claude skill trees are identical, Codex skills carry
// agents/openai.yaml, no plugin ships its own MCP server (the installer registers the one `revit` server), and the
// AGENTS.md snippet stays under 700 characters and matches docs/skill-eval.md. Usage: node scripts/validate-plugins.mjs

import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const problems = [];
const fail = (msg) => problems.push(msg);
const rel = (p) => relative(repo, p).replace(/\\/g, "/");
const readJson = (path) => {
  try {
    return JSON.parse(readFileSync(path, "utf8").replace(/^﻿/, ""));
  } catch (error) {
    fail(`${rel(path)}: ${existsSync(path) ? `invalid JSON (${error.message})` : "missing"}`);
    return null;
  }
};
const files = (root) => {
  const out = [];
  const walk = (dir) => {
    for (const name of readdirSync(dir)) {
      const full = join(dir, name);
      if (statSync(full).isDirectory()) walk(full);
      else out.push(full);
    }
  };
  if (existsSync(root)) walk(root);
  return out.sort();
};
const sha = (path) => createHash("sha256").update(readFileSync(path, "utf8").replace(/\r\n/g, "\n")).digest("hex");

const version = readJson(join(repo, "package.json"))?.version;
const plugins = join(repo, "plugins");

// Marketplaces.
const codexMarket = readJson(join(plugins, ".agents", "plugins", "marketplace.json"));
const claudeMarket = readJson(join(plugins, ".claude-plugin", "marketplace.json"));
const repoMarket = readJson(join(repo, ".agents", "plugins", "marketplace.json"));
for (const [market, base, kind] of [
  [codexMarket, plugins, "codex"],
  [repoMarket, repo, "codex"],
  [claudeMarket, plugins, "claude"],
]) {
  if (!market) continue;
  if (!market.name) fail(`${kind} marketplace without a name`);
  for (const p of market.plugins ?? []) {
    const source = typeof p.source === "string" ? p.source : p.source?.path;
    const dir = resolve(base, source ?? "");
    const manifest = join(dir, kind === "codex" ? ".codex-plugin" : ".claude-plugin", "plugin.json");
    if (!source || !existsSync(manifest)) fail(`${kind} marketplace ${market.name}: plugin ${p.name} source ${source} has no ${rel(manifest)}`);
    if (p.version && p.version !== version) fail(`${kind} marketplace ${market.name}: ${p.name} version ${p.version} != package.json ${version}`);
  }
}

// Plugins.
const trees = {};
for (const kind of ["codex", "claude"]) {
  const root = join(plugins, kind, "revit-mcp-next");
  const manifest = readJson(join(root, kind === "codex" ? ".codex-plugin" : ".claude-plugin", "plugin.json"));
  if (manifest) {
    if (manifest.name !== "revit-mcp-next") fail(`${kind} plugin name is ${manifest.name}`);
    if (manifest.version !== version) fail(`${kind} plugin version ${manifest.version} != package.json ${version}`);
    if (manifest.mcpServers) fail(`${kind} plugin declares mcpServers: the installer registers the one revit server`);
  }
  if (existsSync(join(root, ".mcp.json"))) fail(`${rel(join(root, ".mcp.json"))}: plugins ship skills only (a second broker would start)`);
  const skillsDir = join(root, "skills");
  const skills = existsSync(skillsDir) ? readdirSync(skillsDir).filter((n) => statSync(join(skillsDir, n)).isDirectory()) : [];
  if (!skills.includes("revit") || !skills.includes("revit-setup")) fail(`${kind} plugin must have skills revit and revit-setup (has ${skills.join(", ") || "none"})`);
  for (const skill of skills) {
    const md = join(skillsDir, skill, "SKILL.md");
    if (!existsSync(md)) {
      fail(`${rel(md)} missing`);
      continue;
    }
    const text = readFileSync(md, "utf8").replace(/\r\n/g, "\n");
    const fm = /^---\n([\s\S]*?)\n---\n/.exec(text);
    const name = fm && /^name:\s*(.+)$/m.exec(fm[1])?.[1]?.trim();
    const description = fm && /^description:\s*(.+)$/m.exec(fm[1])?.[1]?.trim();
    if (!fm) fail(`${rel(md)}: no YAML frontmatter`);
    else {
      if (name !== skill) fail(`${rel(md)}: frontmatter name ${name} != folder ${skill}`);
      if (!description) fail(`${rel(md)}: frontmatter description missing`);
      else if (description.length > 1024) fail(`${rel(md)}: description is ${description.length} chars (max 1024)`);
    }
    if (kind === "codex") {
      const yaml = join(skillsDir, skill, "agents", "openai.yaml");
      if (!existsSync(yaml)) fail(`${rel(yaml)} missing`);
      else if (!/display_name:/.test(readFileSync(yaml, "utf8"))) fail(`${rel(yaml)}: interface.display_name missing`);
    }
  }
  trees[kind] = new Map(files(skillsDir).filter((f) => !/[\\/]agents[\\/]openai\.yaml$/.test(f)).map((f) => [relative(skillsDir, f).replace(/\\/g, "/"), sha(f)]));
}
if (trees.codex && trees.claude) {
  for (const [path, hash] of trees.codex) {
    if (!trees.claude.has(path)) fail(`skills/${path} exists for Codex but not for Claude`);
    else if (trees.claude.get(path) !== hash) fail(`skills/${path} differs between the Codex and Claude plugins`);
  }
  for (const path of trees.claude.keys()) if (!trees.codex.has(path)) fail(`skills/${path} exists for Claude but not for Codex`);
}

// AGENTS.md snippet.
const snippetFile = join(repo, "installer", "agents-snippet.md");
if (!existsSync(snippetFile)) fail("installer/agents-snippet.md missing");
else {
  const snippet = readFileSync(snippetFile, "utf8").replace(/\r\n/g, "\n").trim();
  if (snippet.length >= 700) fail(`installer/agents-snippet.md is ${snippet.length} chars (must stay under 700)`);
  const evalDoc = join(repo, "docs", "skill-eval.md");
  if (existsSync(evalDoc)) {
    const doc = readFileSync(evalDoc, "utf8").replace(/\r\n/g, "\n");
    const section = /## AGENTS snippet[\s\S]*?```\n([\s\S]*?)\n```/.exec(doc)?.[1]?.trim();
    if (section && section !== snippet) fail("installer/agents-snippet.md differs from the AGENTS snippet in docs/skill-eval.md");
  }
}

if (problems.length) {
  for (const p of problems) console.error(`validate-plugins: ${p}`);
  process.exit(1);
}
console.log(`validate-plugins: ok (version ${version}; skills ${[...(trees.codex?.keys() ?? [])].filter((p) => p.endsWith("SKILL.md")).join(", ")})`);
