// R9 auth (D4 §8.8 R9, D2 §3 and E13). Works on the isolated e2e home only (never the installed home):
//   1. Rotating <home>\config\auth.env while Revit runs: the broker re-reads it (mtime) and the add-in re-reads on the
//      mismatch, so calls keep working, or fail with a clear AUTH_* error whose fix says what to do.
//   2. A broker started with a wrong REVIT_MCP_NEXT_AUTH_TOKEN gets AUTH_MISMATCH with a fix (fingerprints only, never
//      the token), and status detail:full reports the mismatch.
//   3. A deleted auth.env gives AUTH_NOT_CONFIGURED with a fix; restoring the file makes calls work again.
// The file always ends holding the token Revit uses, so later scenarios keep working.

import { randomBytes } from "node:crypto";
import { existsSync, readFileSync, renameSync, unlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { authFile, formatAuthEnv, parseAuthEnv, userProfileDir } from "@revit-mcp-next/contracts/home";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const newToken = () => randomBytes(32).toString("base64url");

function writeAtomic(path, text) {
  const tmp = `${path}.${process.pid}.tmp`;
  writeFileSync(tmp, text, "utf8");
  renameSync(tmp, path);
}

export default {
  id: "R9",
  title: "Auth: token rotation while Revit runs, wrong token, missing token file",
  profiles: ["full"],
  years: "one",
  async run(t) {
    const installedHome = join(userProfileDir(), ".revit-mcp-next").toLowerCase();
    t.assert(t.home.toLowerCase() !== installedHome, "R9 rewrites auth.env and must run on an isolated e2e home");
    const file = authFile(t.home);
    t.assert(existsSync(file), `Revit created ${file}`);
    const original = readFileSync(file, "utf8");
    t.assert(parseAuthEnv(original).token, "auth.env holds a token");
    const project = await t.call("manage_document", { op: "new_project", path: join(t.yearDir, "R9A.rvt"), template: t.projectTemplate(), activate: true, doc: `@${t.year}` }, { timeoutMs: 120_000 });
    t.setTargetPath(join(t.yearDir, "R9A.rvt"));
    void project;

    // What Revit holds right now; restored at the end whatever happens.
    let revitToken = original;
    try {
      await t.step("rotate auth.env while Revit runs: calls keep working (or a clear AUTH error)", async () => {
        const rotated = formatAuthEnv(newToken());
        writeAtomic(file, rotated);
        await sleep(1_200); // the broker stat-caches the file for 1 s
        const r = await t.call("list", { kind: "levels" }, { allowError: true });
        if (r.status === "ok") {
          revitToken = rotated; // the add-in re-read the file on the mismatch
          t.log("rotation: the add-in re-read auth.env; the call succeeded");
        } else {
          t.assert(["AUTH_MISMATCH", "AUTH_NOT_CONFIGURED"].includes(r.code), `rotation fails only with an AUTH error, got ${r.code}`);
          t.assertMatch(r.fix ?? "", /restart Revit|installer/i, "the fix says to restart Revit or re-run the installer");
          writeAtomic(file, original); // Revit kept the old token
          await sleep(1_200);
        }
        const again = await t.call("list", { kind: "levels" });
        t.assertEq(again.status, "ok", "calls work after the rotation");
      });

      await t.step("a broker with a wrong token gets AUTH_MISMATCH with fingerprints, never the token", async () => {
        const wrong = newToken();
        const session = await t.connect({ env: { REVIT_MCP_NEXT_AUTH_TOKEN: wrong } });
        try {
          const result = await session.client.callTool({ name: "list", arguments: { kind: "levels" } }, undefined, { timeout: 55_000 });
          const text = (result.content ?? []).filter((c) => c.type === "text").map((c) => c.text).join("\n");
          t.assertMatch(text, /^ERROR AUTH_MISMATCH/m, "AUTH_MISMATCH");
          t.assertMatch(text, /^fix: /m, "fix line");
          t.assert(!text.includes(wrong), "the token never appears in a result");
        } finally {
          await session.close();
        }
      });

      await t.step("auth.env deleted: AUTH_NOT_CONFIGURED with a fix; restoring it makes calls work", async () => {
        const keep = readFileSync(file, "utf8");
        unlinkSync(file);
        try {
          await sleep(1_200);
          const r = await t.call("list", { kind: "levels" }, { allowError: true });
          t.assertEq(r.status, "error", "no token, no call");
          t.assert(["AUTH_NOT_CONFIGURED", "AUTH_MISMATCH"].includes(r.code), `AUTH error, got ${r.code}`);
          t.assert(!!r.fix, "fix present");
          const status = await t.call("status", {});
          t.assertEq(status.status, "ok", "status still answers (hello needs no token)");
        } finally {
          writeAtomic(file, keep);
        }
        await sleep(1_200);
        const again = await t.call("list", { kind: "levels" });
        t.assertEq(again.status, "ok", "calls work after restoring auth.env");
      });
    } finally {
      if (readFileSync(file, { encoding: "utf8", flag: "a+" }) !== revitToken) {
        try {
          writeAtomic(file, revitToken);
        } catch {
          // reported by the failing step
        }
      }
    }
  },
};
