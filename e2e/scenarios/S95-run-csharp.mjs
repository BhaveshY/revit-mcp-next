// S95 run_csharp (SPEC §11, §13.3; owner P-CONTROL). Needs the e2e home with config\e2e-test-ops.enable (dev-install
// writes it) so codeExecution.e2ePreapproved is honoured. The scenario turns code execution on in the home's
// settings.json for its own broker session and restores the file afterwards.
//   gate        run_csharp is listed only for a broker started with enableCodeExecution; the add-in refuses on its own
//               (CODE_EXECUTION_DISABLED) once the setting is off again, even though this broker still lists the tool
//   modes       read returns a value and has no token; dry_run reports changes, rolls back and is NOT APPLIED with a
//               token; commit applies exactly that code (one undo step) and is audited
//   deny lists  read-mode doc.SynchronizeWithCentral, doc.Close and new Transaction(...).Commit() → CODE_DENIED_API;
//               Process.Start in any mode → CODE_DENIED_API
//   errors      compile error with the user's line, runtime error, cooperative timeout, commit without a token
//   audit       code-exec-audit.jsonl has a line per call with the code sha
//   consent     with E2E_CONSENT_PROMPT=1 (a person at the test PC answers): consentPrompt on and no pre-approval show the
//               add-in's TaskDialog, which ui dialogs lists and ui press refuses (DIALOG_PROTECTED)
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { makeCaller } from "../lib/call.mjs";
import { makeWriteGuard } from "../lib/fixtures.mjs";

const sha256 = (text) => createHash("sha256").update(text, "utf8").digest("hex");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function settingsFile(home) {
  return join(home, "config", "settings.json");
}

function readSettings(home) {
  const file = settingsFile(home);
  if (!existsSync(file)) return { exists: false, text: null, json: {} };
  const text = readFileSync(file, "utf8");
  let json = {};
  try {
    json = JSON.parse(text);
  } catch {
    json = {};
  }
  return { exists: true, text, json };
}

function writeSettings(home, base, patch) {
  const next = { ...base, ...patch, codeExecution: { ...(base.codeExecution ?? {}), ...(patch.codeExecution ?? {}) } };
  mkdirSync(join(home, "config"), { recursive: true });
  writeFileSync(settingsFile(home), JSON.stringify(next, null, 2) + "\n", "utf8");
}

export default {
  id: "S95",
  title: "run_csharp: opt-in gate, read/dry_run/commit, deny lists, errors, audit",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const original = readSettings(t.home);
    t.assert(existsSync(join(t.home, "config", "e2e-test-ops.enable")), "the e2e home enables test ops (dev-install writes config\\e2e-test-ops.enable)");
    const enabled = { enableCodeExecution: true, codeExecution: { e2ePreapproved: true, consentPrompt: false, timeoutSec: 30 } };
    writeSettings(t.home, original.json, enabled);
    let session = null;
    try {
      const { path } = await t.newProject("S95");
      t.setTargetPath(path);
      session = await t.connect();
      const call = makeCaller({ session, coverage: t.coverage, year: t.year, writeGuard: makeWriteGuard(t.runRoot, () => path) });

      await t.step("run_csharp is listed for a broker started with code execution on", async () => {
        const listed = await session.client.listTools();
        t.assert(listed.tools.some((tool) => tool.name === "run_csharp"), "run_csharp listed");
        const status = await call("status", {});
        t.assert(status.notices.some((n) => n.startsWith("CODE_EXECUTION_ENABLED")), "status notice CODE_EXECUTION_ENABLED");
      });

      const levelCount = "return new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();";
      const before = await t.step("read returns a value, changes nothing and has no token", async () => {
        const r = await call("run_csharp", { code: levelCount, mode: "read" });
        t.assertEq(r.status, "ok", "read status");
        t.assert(typeof r.json?.result === "number", "a number came back");
        t.assert(r.json.confirm === undefined, "no token for read");
        const title = await call("run_csharp", { code: "doc.Title", mode: "read" });
        t.assertMatch(String(title.json?.result), /S95/, "a lone expression returns its value");
        return r.json.result;
      });

      const create = 'var level = Level.Create(doc, Convert.ToDouble(args["elevation_mm"]) / 304.8);\nlevel.Name = (string)args["name"];\nlog("created " + level.Name);\nreturn level;';
      const dry = await t.step("dry_run reports the change, rolls back and is NOT APPLIED with a token", async () => {
        const r = await call("run_csharp", { code: create, args: { elevation_mm: 9000, name: "S95 L" } });
        t.assertEq(r.status, "not_applied", "dry run status");
        t.assertJson(r, (j) => j.changes?.added >= 1 && typeof j.confirm === "string", "changes + token");
        t.assertMatch(r.next, /^ask the user, then run_csharp \{"mode":"commit","confirm":"[A-Z0-9]{6}"\}$/, "next asks the user");
        t.assertMatch(r.json.log ?? "", /created S95 L/, "log output");
        const after = await call("run_csharp", { code: levelCount, mode: "read" });
        t.assertEq(after.json.result, before, "the dry run left no level behind");
        return r.json.confirm;
      });

      await t.step("commit applies exactly the dry-run code with its token (one undo step)", async () => {
        await call("run_csharp", { mode: "commit", code: levelCount }, { expectError: ["INVALID_ARGS"] });
        const r = await call("run_csharp", { mode: "commit", confirm: dry });
        t.assertEq(r.status, "ok", "commit status");
        t.assertJson(r, (j) => /^w\d+$/.test(j.write) && j.created.length >= 1, "a write that created the level");
        const after = await call("run_csharp", { code: levelCount, mode: "read" });
        t.assertEq(after.json.result, before + 1, "one more level");
        await call("run_csharp", { mode: "commit", confirm: dry }, { expectError: ["CONFIRM_EXPIRED"] });
        const u = await call("undo", {});
        t.assertEq(u.status, "ok", "the commit undoes as one step");
        const reverted = await call("run_csharp", { code: levelCount, mode: "read" });
        t.assertEq(reverted.json.result, before, "undo removed it");
      });

      await t.step("deny lists by mode", async () => {
        const sync = await call("run_csharp", { mode: "read", code: "doc.SynchronizeWithCentral(new TransactWithCentralOptions(), new SynchronizeWithCentralOptions());\nreturn 1;" }, { expectError: ["CODE_DENIED_API"] });
        t.assertMatch(sync.summary, /SynchronizeWithCentral/, "names the API");
        await call("run_csharp", { mode: "read", code: "doc.Close(false);\nreturn 1;" }, { expectError: ["CODE_DENIED_API"] });
        const tx = await call("run_csharp", { mode: "read", code: 'using (var t = new Transaction(doc, "x")) { t.Start(); t.Commit(); }\nreturn 1;' }, { expectError: ["CODE_DENIED_API"] });
        t.assertMatch(tx.summary, /Transaction/, "transactions are refused in read mode");
        await call("run_csharp", { mode: "dry_run", code: 'System.Diagnostics.Process.Start("notepad.exe");\nreturn 1;' }, { expectError: ["CODE_DENIED_API"] });
        await call("run_csharp", { mode: "dry_run", code: 'System.IO.File.WriteAllText(@"C:\\\\Temp\\\\s95.txt", "x");\nreturn 1;' }, { expectError: ["CODE_DENIED_API"] });
      });

      await t.step("compile error with the user's line, runtime error, timeout", async () => {
        const c = await call("run_csharp", { mode: "read", code: "int a = 1;\nreturn b;" }, { expectError: ["CODE_COMPILE_ERROR"] });
        t.assertMatch(c.summary, /line 2:/, "the line of the user's code");
        const rt = await call("run_csharp", { mode: "read", code: "int zero = 0;\nreturn 5 / zero;" }, { expectError: ["CODE_RUNTIME_ERROR"] });
        t.assertMatch(rt.summary, /DivideByZeroException/, "the exception type");
        const started = Date.now();
        await call("run_csharp", { mode: "read", code: "long i = 0;\nwhile (true) { i++; }", timeout: 2 }, { expectError: ["CODE_TIMEOUT"] });
        t.assertLe(Date.now() - started, 20_000, "the guard stopped the loop");
        const ok = await call("run_csharp", { code: levelCount, mode: "read" });
        t.assertEq(ok.json.result, before, "Revit is fine afterwards");
      });

      await t.step("every call is in code-exec-audit.jsonl", async () => {
        const file = join(t.home, "logs", "code-exec-audit.jsonl");
        t.assert(existsSync(file), "audit file exists");
        const lines = readFileSync(file, "utf8").trim().split(/\r?\n/).map((l) => JSON.parse(l));
        const mine = lines.filter((l) => l.codeSha256 === sha256(create));
        t.assert(mine.some((l) => l.mode === "commit" && l.outcome === "ok"), "the commit is audited");
        t.assert(mine.some((l) => l.mode === "dry_run" && l.outcome === "not_applied"), "the dry run is audited");
        t.assert(existsSync(join(t.home, "logs", "code", `${sha256(create)}.cs`)), "the source is kept by sha");
      });

      await t.step("the add-in refuses once the setting is off, whatever the broker lists", async () => {
        writeSettings(t.home, original.json, { enableCodeExecution: false });
        await sleep(1_000);
        await call("run_csharp", { code: levelCount, mode: "read" }, { expectError: ["CODE_EXECUTION_DISABLED"] });
      });

      if (process.env.E2E_CONSENT_PROMPT === "1") {
        await t.step("consent prompt (a person answers Allow in Revit)", async () => {
          writeSettings(t.home, original.json, { enableCodeExecution: true, codeExecution: { e2ePreapproved: false, consentPrompt: true } });
          await sleep(1_000);
          const pending = call("run_csharp", { code: levelCount, mode: "read" }, { allowError: true });
          await sleep(3_000);
          const dialogs = await call("ui", { op: "dialogs" });
          const consent = (dialogs.json?.dialogs ?? []).find((d) => /allow AI code execution/.test(String(d.title)));
          t.assert(consent, "the consent TaskDialog is listed");
          await call("ui", { op: "press", button: "Allow for this Revit session" }, { expectError: ["DIALOG_PROTECTED"] });
          t.log("answer 'Allow for this Revit session' in Revit now");
          const r = await pending;
          t.assert(r.status === "ok" || r.status === "running", `the run finished or became a job (${r.status})`);
        });
      }
    } finally {
      if (session) await session.close();
      if (original.exists) writeFileSync(settingsFile(t.home), original.text, "utf8");
      else writeSettings(t.home, {}, { enableCodeExecution: false });
    }
  },
};
