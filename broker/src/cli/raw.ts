// `revit-mcp-cli raw`: send one protocol v3 frame straight to an add-in pipe and print the response (diagnostics and
// e2e S95). Picks the instance by id, by Revit year or the only live one. The auth token is read from the home and
// never printed.
//
//   revit-mcp-cli raw hello                               control op on the control pipe
//   revit-mcp-cli raw health --year 2024
//   revit-mcp-cli raw find_elements.query --kind read --args '{"category":"Walls"}' --doc '{"rid":1,"key":"..."}'

import { readdirSync, readFileSync } from "node:fs";
import { basename, join } from "node:path";
import { authFile, authFingerprint, instancesDir, lockFile, parseAuthEnv } from "@revit-mcp-next/contracts/home";
import { BRIDGE_PROTOCOL_VERSION, CONTROL_OPS, INSTANCE_ID_PATTERN, controlPipeName, primaryPipeName, type BridgeRequest, type DocRef } from "@revit-mcp-next/contracts/protocol";
import { lockState } from "../instances/InstanceRegistry.js";
import { openExchange } from "../ipc/PipeClient.js";
import { ulid } from "../runtime/ids.js";
import { flagBool, flagString, type ParsedArgs } from "./common.js";

export interface LiveInstance {
  instanceId: string;
  year: number;
  pid: number;
  file: string;
  lock: "held" | "free" | "missing";
  snapshot: Record<string, unknown> | null;
}

export async function listInstances(home: string): Promise<LiveInstance[]> {
  const dir = instancesDir(home);
  let names: string[] = [];
  try {
    names = readdirSync(dir).filter((n) => n.endsWith(".json"));
  } catch {
    return [];
  }
  const out: LiveInstance[] = [];
  for (const name of names) {
    const instanceId = basename(name, ".json");
    const m = INSTANCE_ID_PATTERN.exec(instanceId);
    if (!m) continue;
    let snapshot: Record<string, unknown> | null = null;
    try {
      snapshot = JSON.parse(readFileSync(join(dir, name), "utf8"));
    } catch {
      snapshot = null;
    }
    out.push({ instanceId, year: Number(m[1]), pid: Number(m[2]), file: join(dir, name), lock: await lockState(lockFile(home, instanceId)), snapshot });
  }
  return out;
}

export async function rawCommand(args: ParsedArgs, home: string): Promise<number> {
  const op = args._[1];
  if (!op) {
    process.stderr.write("usage: revit-mcp-cli raw <op> [--instance <id>|--year <y>] [--kind read|write|...] [--args <json>] [--doc <json>] [--timeout <ms>] [--control]\n");
    return 2;
  }
  const live = (await listInstances(home)).filter((i) => i.lock === "held");
  const wantId = flagString(args, "instance");
  const wantYear = flagString(args, "year");
  const candidates = live.filter((i) => (!wantId || i.instanceId === wantId) && (!wantYear || String(i.year) === wantYear));
  if (candidates.length !== 1) {
    process.stderr.write(
      candidates.length === 0
        ? `no live Revit instance${wantId ? ` ${wantId}` : wantYear ? ` for ${wantYear}` : ""} in ${instancesDir(home)}\n`
        : `several live instances: ${candidates.map((c) => c.instanceId).join(", ")}; pass --instance <id>\n`,
    );
    return 3;
  }
  const instance = candidates[0]!;
  const control = flagBool(args, "control") || (CONTROL_OPS as readonly string[]).includes(op);
  const timeoutMs = Number(flagString(args, "timeout") ?? (control ? 5_000 : 30_000));
  let token: string | null = null;
  try {
    token = parseAuthEnv(readFileSync(authFile(home), "utf8")).token;
  } catch {
    token = null;
  }
  const parseJson = (key: string): unknown => {
    const text = flagString(args, key);
    if (!text) return undefined;
    try {
      return JSON.parse(text);
    } catch (error) {
      throw new Error(`--${key} is not valid JSON: ${(error as Error).message}`);
    }
  };
  const request: BridgeRequest = {
    v: BRIDGE_PROTOCOL_VERSION,
    requestId: ulid(),
    clientKey: `revit-mcp-cli-raw#${process.pid}`,
    auth: op === "hello" || !token ? undefined : { token, fp: authFingerprint(token), file: authFile(home) },
    op,
    kind: (flagString(args, "kind") ?? (control ? "control" : "read")) as BridgeRequest["kind"],
    timeoutMs,
    doc: (parseJson("doc") as DocRef | undefined) ?? null,
    args: (parseJson("args") as Record<string, unknown> | undefined) ?? {},
  };
  const mode = flagString(args, "mode");
  if (mode === "apply" || mode === "preview") request.mode = mode;
  const pipe = control ? controlPipeName(instance.instanceId) : primaryPipeName(instance.instanceId);
  const exchange = openExchange(pipe, request);
  const timer = new Promise<never>((_, reject) => setTimeout(() => reject(new Error(`no response within ${timeoutMs + 5_000} ms`)), timeoutMs + 5_000).unref());
  try {
    const response = await Promise.race([exchange.response, timer]);
    process.stdout.write(JSON.stringify({ instanceId: instance.instanceId, pipe, response }, null, flagBool(args, "compact") ? 0 : 2) + "\n");
    return (response as { ok?: boolean }).ok === false ? 1 : 0;
  } catch (error) {
    exchange.abandon("cli timeout");
    process.stderr.write(`${instance.instanceId} ${op}: ${(error as Error).message}\n`);
    return 1;
  }
}
