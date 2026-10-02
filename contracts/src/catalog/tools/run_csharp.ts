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
    "": op("code", "args,mode,timeout,doc,confirm", M, [{"code":"return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();","mode":"read"}, {"mode":"commit","confirm":"K7QM2X"}], { help: "Listed only when the user enabled code execution in settings.json. read and dry_run always roll back; commit needs the dry run's confirm token and the user's OK." }),
  },
};
