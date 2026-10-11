// R5 busy (SPEC §8.8, §13.3; D4 R5, D2 E2). Owner P-REL-ADDIN.
// test.sleep_ui blocks the Revit UI thread for 30 s (raw pipe; test ops need config\e2e-test-ops.enable in the home).
// During the block: a doc read returns REVIT_BUSY in ≤10 s, 24 parallel reads all return in ≤12 s, a write is never
// executed (ledger has no committed entry), and the add-in's own health shows the executing op and a hung UI.
// Variants (only when the keys are bound): an export and a family load executing → a read from another session reports
// REVIT_BUSY (never a dialog or a hang).
//
// This file also exports the raw add-in helper used by R6, R7, R10, R12 and S99 (e2e/lib is frozen in wave 2).

import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import net from "node:net";
import { basename, join } from "node:path";
import { authFile, authFingerprint, e2eTestOpsEnableFile, instancesDir, isDefaultHome, ledgerFile, parseAuthEnv } from "@revit-mcp-next/contracts/home";
import { BRIDGE_PROTOCOL_VERSION, controlPipeName, pipePath, primaryPipeName } from "@revit-mcp-next/contracts/protocol";
import { makeCaller } from "../lib/call.mjs";

// ------------------------------------------------------------------------------------------------ raw add-in helper

const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/** A ULID-shaped request id (26 Crockford chars: 48-bit time + 80 random bits). */
export function ulid() {
  let time = Date.now();
  let head = "";
  for (let i = 0; i < 10; i++) {
    head = CROCKFORD[time % 32] + head;
    time = Math.floor(time / 32);
  }
  const bytes = randomBytes(16);
  let tail = "";
  for (let i = 0; i < 16; i++) tail += CROCKFORD[bytes[i] % 32];
  return head + tail;
}

/** One framed v3 exchange (4-byte big-endian length + UTF-8 JSON). Resolves with the parsed response. */
export function exchange(pipeName, request, timeoutMs) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(pipePath(pipeName));
    let buffer = Buffer.alloc(0);
    let done = false;
    const finish = (error, value) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      socket.destroy();
      if (error) reject(error);
      else resolve(value);
    };
    const timer = setTimeout(() => finish(new Error(`no response to ${request.op} within ${timeoutMs} ms`)), timeoutMs);
    socket.on("connect", () => {
      const body = Buffer.from(JSON.stringify(request), "utf8");
      const header = Buffer.alloc(4);
      header.writeUInt32BE(body.length, 0);
      socket.write(Buffer.concat([header, body]));
    });
    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.length < 4) return;
      const length = buffer.readUInt32BE(0);
      if (buffer.length < 4 + length) return;
      try {
        finish(null, JSON.parse(buffer.subarray(4, 4 + length).toString("utf8")));
      } catch (error) {
        finish(error);
      }
    });
    socket.on("error", (error) => finish(error));
    socket.on("close", () => finish(new Error(`the pipe closed before a response to ${request.op}`)));
  });
}

/** The registration of the harness Revit (year/pid) in the home: {instanceId, file, snapshot}. */
export function registration(home, year, pid) {
  const dir = instancesDir(home);
  const name = existsSync(dir) ? readdirSync(dir).find((n) => new RegExp(`^r${year}-${pid}-[0-9a-f]{6}\\.json$`).test(n)) : null;
  if (!name) throw new Error(`no registration for Revit ${year} pid ${pid} in ${dir}`);
  const file = join(dir, name);
  let snapshot = null;
  try {
    snapshot = JSON.parse(readFileSync(file, "utf8"));
  } catch {
    snapshot = null;
  }
  return { instanceId: basename(name, ".json"), file, snapshot };
}

/**
 * Raw access to the add-in of the scenario's Revit (test ops, control ops, internal keys).
 * call(op, args, {kind, timeoutMs, doc}) → BridgeResponse; control(op, args) → BridgeResponse.
 */
export function rawAddin(t) {
  const { instanceId } = registration(t.home, t.year, t.revit.pid);
  const auth = () => {
    const file = authFile(t.home);
    const parsed = parseAuthEnv(readFileSync(file, "utf8"));
    if (!parsed.token) throw new Error(`no auth token in ${file}`);
    return { token: parsed.token, fp: authFingerprint(parsed.token), file };
  };
  const clientKey = `e2e-raw-${t.id}#${process.pid}`;
  const request = (op, args, kind, timeoutMs, doc) => ({
    v: BRIDGE_PROTOCOL_VERSION,
    requestId: ulid(),
    clientKey,
    auth: op === "hello" ? undefined : auth(),
    op,
    kind,
    timeoutMs,
    doc: doc ?? null,
    args: args ?? {},
  });
  return {
    instanceId,
    async call(op, args = {}, o = {}) {
      const timeoutMs = o.timeoutMs ?? 30_000;
      return exchange(primaryPipeName(instanceId), request(op, args, o.kind ?? "read", timeoutMs, o.doc), timeoutMs + 5_000);
    },
    async control(op, args = {}, timeoutMs = 5_000) {
      return exchange(controlPipeName(instanceId), request(op, args, "control", timeoutMs, null), timeoutMs + 2_000);
    },
  };
}

/**
 * Makes sure the test.* ops are registered in this Revit session. They exist only when the home is not the default
 * home and <home>\config\e2e-test-ops.enable existed when Revit started; when the file is missing it is created here
 * and the scenario fails with a clear rerun instruction (the registry is built at Revit startup).
 */
export async function requireTestOps(t, addin) {
  const hello = await addin.control("hello");
  if (hello.ok && hello.data?.testOps) return hello.data;
  if (isDefaultHome(t.home)) throw new Error(`test ops are refused in the default home ${t.home}; run the e2e with an isolated --home`);
  const file = e2eTestOpsEnableFile(t.home);
  if (!existsSync(file)) {
    mkdirSync(join(t.home, "config"), { recursive: true });
    writeFileSync(file, "enabled by the e2e scenario " + t.id + "\r\n");
  }
  throw new Error(`test ops are not active in Revit ${t.year} (pid ${t.revit.pid}); ${file} exists now - rerun with --restart`);
}

/** Bound add-in keys (hello.capabilities.keys). */
export async function boundKeys(addin) {
  const hello = await addin.control("hello");
  return new Set(hello.data?.capabilities?.keys ?? []);
}

/** Health (control op) data or null. */
export async function health(addin) {
  const r = await addin.control("health");
  return r.ok ? r.data : null;
}

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Ledger lines of this instance written at or after `sinceMs`. */
export function ledgerSince(home, instanceId, sinceMs) {
  const file = ledgerFile(home, instanceId);
  if (!existsSync(file)) return [];
  return readFileSync(file, "utf8")
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => {
      try {
        return JSON.parse(line);
      } catch {
        return null;
      }
    })
    .filter((e) => e && Date.parse(e.acceptedAtUtc ?? e.completedAtUtc ?? 0) >= sinceMs - 1_000);
}

const BUSY_CODES = ["REVIT_BUSY", "BRIDGE_BUSY", "REVIT_QUEUE_FULL"];

/**
 * Busy variants (R5 and R12): start a long native-ish op in this session (as a background call) and read from a second
 * session; the read must answer REVIT_BUSY (or succeed when the op already finished), never a dialog or a hang.
 */
export async function busyVariants(t, addin) {
  const keys = await boundKeys(addin);
  const other = await t.connect();
  try {
    const call2 = makeCaller({ session: other, year: t.year });
    const variants = [];
    if (keys.has("export.ifc")) variants.push({ name: "export ifc", start: () => t.call("export", { format: "ifc" }, { allowError: true }) });
    else t.log("variant export skipped: export.ifc is not bound in this build (P-EXPORT)");
    const rfa = findLibraryFamily(t.year);
    if (keys.has("place_family.load") && rfa) variants.push({ name: "family load", start: () => t.call("place_family", { op: "load", path: rfa }, { allowError: true }) });
    else t.log(`variant family load skipped: ${keys.has("place_family.load") ? "no .rfa found under C:\\ProgramData\\Autodesk\\RVT " + t.year : "place_family.load is not bound (P-MODEL)"}`);
    for (const v of variants) {
      await t.step(`busy variant: ${v.name} executing → another session's read is REVIT_BUSY or ok, never a dialog`, async () => {
        const running = v.start();
        await sleep(400);
        const t0 = Date.now();
        const r = await call2("find_elements", { category: ["Walls"], count_only: true }, { allowError: true });
        const ms = Date.now() - t0;
        t.assert(r.status === "ok" || (r.status === "error" && BUSY_CODES.includes(r.code)), `read during ${v.name}: ${r.status} ${r.code ?? ""} ${r.summary}`);
        t.assert(!["REVIT_DIALOG_OPEN", "REVIT_NOT_RESPONDING", "REVIT_EDIT_MODE_OR_COMMAND"].includes(r.code), `no dialog/hang classification during ${v.name}`);
        t.assertLe(ms, 12_000, `read during ${v.name} ms`);
        const done = await running;
        t.log(`${v.name}: ${done.status} ${done.code ?? ""} (${done.ms} ms); concurrent read ${r.status} ${r.code ?? ""} in ${ms} ms`);
      });
    }
  } finally {
    await other.close();
  }
}

function findLibraryFamily(year) {
  const roots = [`C:\\ProgramData\\Autodesk\\RVT ${year}\\Libraries`, `C:\\ProgramData\\Autodesk\\RVT ${year}\\Libraries\\English`];
  for (const root of roots) {
    const found = walkFor(root, 0);
    if (found) return found;
  }
  return null;
}

function walkFor(dir, depth) {
  if (depth > 5 || !existsSync(dir)) return null;
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return null;
  }
  const file = entries.find((e) => e.isFile() && /\.rfa$/i.test(e.name));
  if (file) return join(dir, file.name);
  for (const e of entries) {
    if (!e.isDirectory()) continue;
    const found = walkFor(join(dir, e.name), depth + 1);
    if (found) return found;
  }
  return null;
}

// ------------------------------------------------------------------------------------------------ scenario

export default {
  id: "R5",
  title: "Busy: UI blocked 30 s → reads fail fast with REVIT_BUSY, writes never run, health shows it",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const addin = rawAddin(t);
    await requireTestOps(t, addin);
    const { path } = await t.newProject("R5");
    t.setTargetPath(path);

    const blockMs = 30_000;
    const blockStart = Date.now();
    const sleeping = addin.call("test.sleep_ui", { ms: blockMs }, { timeoutMs: blockMs + 20_000 });
    await sleep(1_000);

    await t.step("health shows test.sleep_ui executing (add-in side)", async () => {
      const h = await health(addin);
      t.assert(h, "health answers on the control pipe while the UI thread is blocked");
      t.assertEq(h.queue?.executing?.op, "test.sleep_ui", "executing op");
      t.assert((h.queue.executing.elapsedMs ?? 0) >= 500, "elapsedMs grows");
    });

    await t.step("a doc read during the block returns REVIT_BUSY in ≤10 s", async () => {
      const t0 = Date.now();
      const r = await t.call("find_elements", { category: ["Walls"], count_only: true }, { expectError: BUSY_CODES });
      const ms = Date.now() - t0;
      t.assertLe(ms, 10_000, "REVIT_BUSY latency ms");
      t.log(`read during block: ${r.code} in ${ms} ms (${r.summary})`);
    });

    await t.step("24 parallel reads all return in ≤12 s", async () => {
      const t0 = Date.now();
      const results = await Promise.all(
        Array.from({ length: 24 }, (_, i) => t.call("find_elements", { category: [i % 2 ? "Walls" : "Doors"], count_only: true }, { allowError: true }).then((r) => ({ r, ms: Date.now() - t0 })))
      );
      for (const { r, ms } of results) {
        t.assert(r.status === "error" && BUSY_CODES.includes(r.code), `parallel read: ${r.status} ${r.code ?? ""}`);
        t.assertLe(ms, 12_000, "parallel read ms");
      }
    });

    await t.step("the UI monitor reports the main window hung after ≥5 s", async () => {
      const wait = Math.max(0, blockStart + 7_000 - Date.now());
      await sleep(wait);
      const h = await health(addin);
      t.assertEq(h?.ui?.hung, true, "health.ui.hung");
    });

    const writeAt = Date.now();
    await t.step("a write sent during the block is not executed", async () => {
      const r = await t.call("set_parameters", { ids: ["project_info"], values: { "Project Name": "E2E-R5-must-not-apply" } }, { allowError: true });
      t.assert(r.status === "error", `the write did not apply during the block (${r.status} ${r.code ?? ""})`);
      t.assert(BUSY_CODES.includes(r.code) || r.code === "WRITE_STILL_RUNNING", `write code ${r.code}`);
    });

    await t.step("the block ends; test.sleep_ui returns and reads work again", async () => {
      const slept = await sleeping;
      t.assert(slept.ok, `test.sleep_ui ok: ${slept.code ?? ""} ${slept.message ?? ""}`);
      t.assert(slept.data.sleptMs >= blockMs - 100, "slept the whole block");
      const t0 = Date.now();
      const r = await t.call("find_elements", { category: ["Walls"], count_only: true });
      t.assertLe(Date.now() - t0, 5_000, "first read after the block ms");
      t.assertEq(r.status, "ok", "read after the block");
    });

    await t.step("the write never ran (no committed ledger entry; Project Name unchanged)", async () => {
      await sleep(3_000);
      const entries = ledgerSince(t.home, addin.instanceId, writeAt).filter((e) => String(e.key ?? "").startsWith("set_parameters"));
      t.assert(!entries.some((e) => e.state === "committed"), `no committed set_parameters entry (${JSON.stringify(entries.map((e) => e.state))})`);
      const d = await t.call("describe_elements", { ids: ["project_info"] }, { allowError: true });
      if (d.status === "ok") t.assert(!d.text.includes("E2E-R5-must-not-apply"), "Project Name unchanged");
    });

    await busyVariants(t, addin);
  },
};
