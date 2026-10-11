// Catalog entry for `run_csharp` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { CODE, E, I, meta, O, op, P, S, sec, syn, WD_OPEN } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(CODE);

export const tool: ToolSpec = {
  name: "run_csharp",
  title: "Run C# (opt-in)",
  description: "Run C# on the Revit API when no tool fits. Off unless the user enabled it locally. read and dry_run (default) always roll back; dry_run reports changes and a token; commit needs that token and the user's OK.",
  group: "optin",
  annotations: WD_OPEN,
  discriminator: null,
  profiles: ["full"],
  optIn: "enableCodeExecution",
  properties: {
    code: S("C# method body with doc, uidoc, app, args (IDictionary), log(object); return a value."),
    args: O("Values passed as args."),
    mode: syn(E(["read","dry_run","commit"], "Default dry_run."), {"dry":"dry_run","test":"dry_run","readonly":"read"}),
    timeout: sec(I("Seconds 1-45. Default 30.")),
    doc: P.doc,
    confirm: P.confirm,
  },
  required: ["code"],
  ops: {
    "": op("code", "args,mode,timeout,doc,confirm", M, [{"code":"return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();","mode":"read"}, {"mode":"commit","confirm":"K7QM2X"}, {"code":"var level = Level.Create(doc, Convert.ToDouble(args[\"elevation_mm\"]) / 304.8);\nlevel.Name = (string)args[\"name\"];\nreturn level;","args":{"elevation_mm":9000,"name":"Level 4"},"mode":"dry_run"}, {"code":"foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Take(5)) log(w.Name);\nreturn \"listed\";","mode":"read","timeout":10}], {
      help: "Listed only when the user enabled code execution in settings.json. code is a method body over doc, uidoc, app, args, log; return a value (Element -> {id,category,name}, XYZ -> mm). Internal units are feet; args numbers are long or double (use Convert.ToDouble). read and dry_run always roll back; a dry run that changed the model is NOT APPLIED with a token: ask the user, then commit with it (the code is not re-sent). Files, processes, network, reflection, save/sync/close/export are refused.",
      api: "Roslyn (Microsoft.CodeAnalysis.CSharp 4.8) from the add-in's scripting folder; TransactionGroup 'MCP temp run_csharp' / 'MCP <wtag> run_csharp'",
      errors: ["CODE_EXECUTION_DISABLED", "CODE_EXECUTION_DECLINED", "CODE_EXECUTION_UNAVAILABLE", "CODE_COMPILE_ERROR", "CODE_DENIED_API", "CODE_TIMEOUT", "CODE_RUNTIME_ERROR", "REVIT_TRANSACTION_ROLLED_BACK", "CONFIRM_EXPIRED", "CONFIRM_MISMATCH"],
    }),
  },
};
