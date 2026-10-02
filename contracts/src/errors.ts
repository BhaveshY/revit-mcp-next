// ErrorCatalog (SPEC §4.3). The add-in returns only code, message (concrete names/values, never tool names) and
// structured details. The broker renders the `fix:` line from these templates and never uses add-in free text as a fix.
// Tool-specific codes live in ToolSpec.errors (e.g. edit_family) and are merged by mergeErrorCatalog().
//
// Template strings may contain placeholders resolved by the broker at render time:
//   ${details.<path>}  structured details of the error (array items by index: ${details.candidates.0})
//   ${args.<name>}     the effective args of the failing call
//   ${tool} ${op} ${key}  the failing call
//   ${doc.n} ${doc.title} ${doc.year}  the resolved target doc (#n, title, year)
// A string value that is exactly one placeholder keeps the value's JSON type; when it resolves to nothing the key is
// dropped. A placeholder embedded in text that resolves to nothing makes the template unusable, so the next template of
// a fallback array is tried.

export interface FixCondition {
  /** Placeholder path without ${}, e.g. "details.ref" or "details.viewKind". */
  path: string;
  /** When set, the value must equal this (strings compare case-insensitively). Otherwise the value must exist. */
  equals?: unknown;
}

interface FixBase {
  /** The template applies only when this holds. */
  when?: FixCondition;
  /** Appended after the rendered call, e.g. "never repeat the write". */
  note?: string;
}

export type FixTemplate =
  /** `<tool> <json args>`; args may be a single placeholder (e.g. "${details.example}") for a computed object. */
  | (FixBase & { kind: "call"; tool: string; args: Record<string, unknown> | string })
  /** The failing call with these args replaced (keys and values may be templated). */
  | (FixBase & { kind: "same_call_with"; set: Record<string, unknown> })
  /** The failing call without these args. */
  | (FixBase & { kind: "same_call_without"; drop: string[] })
  /** Renders options from details.options as "1) ... 2) ..." and the call `<tool> {<arg>: <option 1 value>}`. */
  | (FixBase & { kind: "choose"; tool: string; arg: string; base?: Record<string, unknown> })
  /** "ask the user: <text>" */
  | (FixBase & { kind: "ask"; text: string })
  /** "retry in <n> s" (transient states only, at most once) */
  | (FixBase & { kind: "retry_in"; seconds: number });

export interface ErrorSpec {
  code: string;
  meaning: string;
  /** True when the code guarantees that nothing in Revit changed. */
  nothingChanged: boolean;
  /** One template, or a fallback chain: the first template whose condition holds and whose placeholders resolve wins. */
  fix: FixTemplate | FixTemplate[];
  /** Optional alternative rendered as "..., or <alternative>". */
  or?: FixTemplate | FixTemplate[];
  /** Fields of details worth knowing (documentation for implementers and help {topic:"<CODE>"}). */
  details?: string;
}

const has = (path: string): FixCondition => ({ path });
const eq = (path: string, equals: unknown): FixCondition => ({ path, equals });

export const ERROR_SPECS: ErrorSpec[] = [
  // ---------------------------------------------------------------- transport, instances, auth
  {
    code: "NO_REVIT_RUNNING",
    meaning: "No running Revit has the revit-mcp-next add-in loaded.",
    nothingChanged: true,
    details: "lastExited? {year, pid, doc, at}",
    fix: [
      { kind: "ask", when: has("details.lastExited.doc"), text: "start Revit ${details.lastExited.year} and open ${details.lastExited.doc}; this session reconnects automatically" },
      { kind: "ask", text: "start Revit 2024 or 2027 with the revit-mcp-next add-in and open the model, then call status" },
    ],
  },
  {
    code: "REVIT_STARTING",
    meaning: "Revit is registered but still starting.",
    nothingChanged: true,
    details: "year",
    fix: { kind: "retry_in", seconds: 10 },
  },
  {
    code: "REVIT_EXITED",
    meaning: "The Revit instance exited before or during the call.",
    nothingChanged: false,
    details: "year, pid, doc",
    fix: [
      { kind: "ask", when: has("details.doc"), text: "restart Revit ${details.year} and open ${details.doc}; this session reconnects automatically" },
      { kind: "ask", text: "restart Revit and open the model; this session reconnects automatically" },
    ],
  },
  {
    code: "ADDIN_PIPE_MISSING",
    meaning: "Revit holds its registration lock but the add-in pipe is missing (the pipe host failed).",
    nothingChanged: true,
    details: "year, logPath",
    fix: [
      { kind: "ask", when: has("details.logPath"), text: "restart Revit ${details.year}; if it happens again, send the add-in log ${details.logPath}" },
      { kind: "ask", text: "restart Revit; if it happens again, run status {\"detail\":\"full\"} and send the add-in log" },
    ],
  },
  {
    code: "ADDIN_OUTDATED",
    meaning: "The running add-in speaks an older protocol or catalog than this broker.",
    nothingChanged: true,
    details: "addinVersion, brokerVersion, year",
    fix: [
      { kind: "ask", when: has("details.year"), text: "restart Revit ${details.year} so it loads the installed add-in" },
      { kind: "ask", text: "restart Revit so it loads the installed add-in" },
    ],
  },
  {
    code: "ADDIN_NEWER_THAN_BROKER",
    meaning: "The add-in is newer than this broker.",
    nothingChanged: true,
    details: "addinVersion, brokerVersion",
    fix: { kind: "ask", text: "re-run the revit-mcp-next installer, then start a new chat thread" },
  },
  {
    code: "AUTH_MISMATCH",
    meaning: "The broker and Revit use different auth tokens.",
    nothingChanged: true,
    details: "authFile, addinFp, brokerFp",
    fix: { kind: "ask", text: "re-run the revit-mcp-next installer (it keeps one token), or restart Revit" },
  },
  {
    code: "AUTH_NOT_CONFIGURED",
    meaning: "No auth token is available yet.",
    nothingChanged: true,
    details: "authFile",
    fix: { kind: "ask", text: "start Revit once with the add-in (it creates the token file), or re-run the revit-mcp-next installer" },
  },
  {
    code: "BRIDGE_ACCESS_DENIED",
    meaning: "Access to the Revit pipe was denied.",
    nothingChanged: true,
    fix: { kind: "ask", text: "run Revit and the AI client as the same Windows user, both non-elevated" },
  },
  {
    code: "BRIDGE_BUSY",
    meaning: "Too many calls are already waiting for this Revit; nothing was sent.",
    nothingChanged: true,
    details: "mine, others, executing",
    fix: { kind: "retry_in", seconds: 10 },
  },
  {
    code: "REVIT_QUEUE_FULL",
    meaning: "The add-in's queue is full; nothing was sent.",
    nothingChanged: true,
    details: "mine, others, executing",
    fix: { kind: "retry_in", seconds: 10 },
  },
  {
    code: "REVIT_BUSY",
    meaning: "Revit is executing other work (an MCP op, a job or a native operation); nothing ran.",
    nothingChanged: true,
    details: "executing{op, elapsedS, ref}, native?",
    fix: [
      { kind: "call", when: has("details.ref"), tool: "job_status", args: { id: "${details.ref}" } },
      { kind: "retry_in", seconds: 15 },
    ],
  },
  {
    code: "REVIT_DIALOG_OPEN",
    meaning: "A modal dialog blocks Revit; nothing ran.",
    nothingChanged: true,
    details: "instance, title, text, dialogId, buttons",
    fix: { kind: "call", tool: "ui", args: { op: "dialogs", instance: "${details.instance}" } },
  },
  {
    code: "REVIT_NOT_RESPONDING",
    meaning: "Revit's main window has not responded for 5 s or more; nothing ran.",
    nothingChanged: true,
    fix: { kind: "ask", text: "check Revit (it may be busy in a native operation), then retry once" },
  },
  {
    code: "REVIT_EDIT_MODE_OR_COMMAND",
    meaning: "Revit is probably in a command or edit mode (sketch, placement); nothing ran.",
    nothingChanged: true,
    fix: { kind: "ask", text: "press Esc twice or click Finish in Revit, then retry once" },
  },
  {
    code: "REQUEST_CANCELLED",
    meaning: "The client cancelled the call.",
    nothingChanged: true,
    fix: { kind: "ask", text: "nothing to do; repeat the call only if the user still wants it" },
  },
  {
    code: "REVIT_SHUTTING_DOWN",
    meaning: "Revit is closing; the call was not run.",
    nothingChanged: true,
    fix: { kind: "ask", text: "wait until Revit has closed or restart it, then retry" },
  },
  {
    code: "REQUEST_ID_CONFLICT",
    meaning: "A request id was reused with different content.",
    nothingChanged: true,
    fix: { kind: "retry_in", seconds: 1 },
  },
  {
    code: "RESPONSE_SERIALIZATION_FAILED",
    meaning: "The add-in could not serialize the result.",
    nothingChanged: true,
    fix: { kind: "same_call_with", set: { limit: 20 }, note: "or ask for fewer fields" },
  },
  {
    code: "RESPONSE_TOO_LARGE",
    meaning: "The result exceeded the 4 MiB frame.",
    nothingChanged: true,
    fix: [
      { kind: "same_call_with", when: has("details.suggestedLimit"), set: { limit: "${details.suggestedLimit}" } },
      { kind: "same_call_with", set: { limit: 20 } },
    ],
  },
  {
    code: "CATALOG_MISSING",
    meaning: "The add-in was built without the embedded catalog.",
    nothingChanged: true,
    fix: { kind: "ask", text: "rebuild the add-in (npm run build, then scripts/build-addin.ps1) and restart Revit" },
  },
  {
    code: "INTERNAL_ERROR",
    meaning: "Unexpected error (logged with the requestId).",
    nothingChanged: false,
    details: "requestId, logPath",
    fix: [
      { kind: "ask", when: has("details.requestId"), text: "report requestId ${details.requestId} to the developer; retry once" },
      { kind: "ask", text: "report this error to the developer; retry once" },
    ],
  },

  // ---------------------------------------------------------------- targeting
  {
    code: "NO_OPEN_DOCUMENT",
    meaning: "Revit is running with no document open.",
    nothingChanged: true,
    details: "year",
    fix: [
      { kind: "call", when: has("details.path"), tool: "manage_document", args: { op: "open", path: "${details.path}" } },
      { kind: "ask", when: has("details.year"), text: "open a model in Revit ${details.year}, or tell me its path so I can open it with manage_document" },
      { kind: "ask", text: "open a model in Revit, or tell me its path so I can open it with manage_document" },
    ],
  },
  {
    code: "TARGET_AMBIGUOUS",
    meaning: "Several documents match and none is pinned; nothing ran.",
    nothingChanged: true,
    details: "options[] (value, label)",
    fix: { kind: "choose", tool: "set_target", arg: "doc" },
  },
  {
    code: "TARGET_CLOSED",
    meaning: "The pinned document was closed.",
    nothingChanged: true,
    details: "title, path, options[]",
    fix: [
      { kind: "call", when: has("details.path"), tool: "manage_document", args: { op: "open", path: "${details.path}" } },
      { kind: "choose", when: has("details.options.0"), tool: "set_target", arg: "doc" },
      { kind: "call", tool: "status", args: {} },
    ],
  },
  {
    code: "DOC_NOT_OPEN",
    meaning: "The document is not open (for example after a Revit restart).",
    nothingChanged: true,
    details: "title, path, options[]",
    fix: [
      { kind: "call", when: has("details.path"), tool: "manage_document", args: { op: "open", path: "${details.path}" } },
      { kind: "choose", when: has("details.options.0"), tool: "set_target", arg: "doc" },
      { kind: "call", tool: "status", args: {} },
    ],
  },
  {
    code: "TARGET_CHANGED",
    meaning: "The target document changed since your last write; nothing was written.",
    nothingChanged: true,
    details: "from, to, reason, fromDoc",
    fix: { kind: "same_call_with", set: {}, note: "writes into ${details.to}" },
    or: [
      { kind: "call", when: has("details.fromDoc"), tool: "set_target", args: { doc: "${details.fromDoc}" } },
      { kind: "call", tool: "set_target", args: { doc: "active" } },
    ],
  },
  {
    code: "TARGET_STALE",
    meaning: "The document reference no longer matches (internal; the broker refreshes and retries once).",
    nothingChanged: true,
    fix: { kind: "call", tool: "status", args: {} },
  },
  {
    code: "FAMILY_DOC_REQUIRED",
    meaning: "This op needs a family document, but the target is a project.",
    nothingChanged: true,
    details: "family?, options[]",
    fix: [
      { kind: "call", when: has("details.family"), tool: "edit_family", args: { op: "open", family: "${details.family}" } },
      { kind: "choose", when: has("details.options.0"), tool: "set_target", arg: "doc" },
      { kind: "call", tool: "help", args: { tool: "edit_family", op: "open" } },
    ],
  },
  {
    code: "PROJECT_DOC_REQUIRED",
    meaning: "This op needs a project document, but the target is a family.",
    nothingChanged: true,
    details: "options[]",
    fix: [
      { kind: "choose", when: has("details.options.0"), tool: "set_target", arg: "doc" },
      { kind: "call", tool: "status", args: {} },
    ],
  },
  {
    code: "DOC_READ_ONLY",
    meaning: "The target copy is read-only.",
    nothingChanged: true,
    details: "editable? (# of the editable copy)",
    fix: [
      { kind: "call", when: has("details.editable"), tool: "set_target", args: { doc: "${details.editable}" } },
      { kind: "ask", text: "open an editable copy of the model, or save this one under a new name with manage_document save_as" },
    ],
  },
  {
    code: "LINK_READ_ONLY",
    meaning: "Elements of linked models are read-only.",
    nothingChanged: true,
    details: "link, path?",
    fix: [
      { kind: "call", when: has("details.path"), tool: "manage_document", args: { op: "open", path: "${details.path}" } },
      { kind: "ask", text: "open the linked model itself to edit it; find_elements link= is read-only" },
    ],
  },
  {
    code: "NEEDS_ACTIVE_DOC",
    meaning: "This op needs the target document to be the active document in Revit.",
    nothingChanged: true,
    details: "doc",
    fix: [
      { kind: "call", when: has("details.doc"), tool: "ui", args: { op: "activate_doc", doc: "${details.doc}" } },
      { kind: "call", tool: "ui", args: { op: "activate_doc", doc: "${doc.n}" } },
    ],
  },

  // ---------------------------------------------------------------- execution outcome
  {
    code: "WRITE_STILL_RUNNING",
    meaning: "The write is still running in Revit at the time budget.",
    nothingChanged: false,
    details: "ref (w#)",
    fix: { kind: "call", tool: "job_status", args: { id: "${details.ref}" }, note: "never repeat the write" },
  },
  {
    code: "READ_STILL_RUNNING",
    meaning: "A read that cannot yield is still running at the time budget.",
    nothingChanged: true,
    details: "ref (j#)",
    fix: { kind: "call", tool: "job_status", args: { id: "${details.ref}" }, note: "or repeat the same call: it returns the cached result" },
  },
  {
    code: "WRITE_OUTCOME_UNKNOWN",
    meaning: "The response was lost and Revit is gone; the write may or may not have committed.",
    nothingChanged: false,
    details: "ref, elements?",
    fix: [
      { kind: "ask", when: has("details.elements"), text: "reopen the model in Revit and check ${details.elements}; never repeat the write blindly" },
      { kind: "ask", text: "reopen the model in Revit and check whether the change is there; never repeat the write blindly" },
    ],
  },
  {
    code: "REVIT_TRANSACTION_ROLLED_BACK",
    meaning: "Revit rolled the change back; the failures are listed.",
    nothingChanged: true,
    details: "failures[] {text, ids}",
    fix: { kind: "call", tool: "help", args: { tool: "${tool}", op: "${op}" }, note: "then repeat the call with the named element or parameter fixed" },
  },
  {
    code: "REVIT_REFUSED",
    meaning: "The Revit API refused the operation; its message is quoted.",
    nothingChanged: true,
    details: "apiMessage",
    fix: { kind: "call", tool: "help", args: { tool: "${tool}", op: "${op}" } },
  },

  // ---------------------------------------------------------------- arguments and names
  {
    code: "INVALID_ARGS",
    meaning: "The arguments did not validate; nothing ran.",
    nothingChanged: true,
    details: "param, reason, example",
    fix: [
      { kind: "call", when: has("details.example"), tool: "${tool}", args: "${details.example}" },
      { kind: "call", tool: "help", args: { tool: "${tool}", op: "${op}" } },
    ],
  },
  {
    code: "UNKNOWN_OP",
    meaning: "The op is not one of this tool's ops.",
    nothingChanged: true,
    details: "ops[], closest",
    fix: [
      { kind: "same_call_with", when: has("details.closest"), set: { "${details.param}": "${details.closest}" } },
      { kind: "call", tool: "help", args: { tool: "${tool}" } },
    ],
  },
  {
    code: "NOT_FOUND",
    meaning: "A name or id did not resolve.",
    nothingChanged: true,
    details: "param, kind, value, candidates[]",
    fix: [
      { kind: "same_call_with", when: has("details.candidates.0"), set: { "${details.param}": "${details.candidates.0}" } },
      { kind: "ask", when: has("details.kind"), text: "check the ${details.kind} name; list or find_elements show what exists" },
      { kind: "ask", text: "check the name or id; list or find_elements show what exists" },
    ],
  },
  {
    code: "AMBIGUOUS_NAME",
    meaning: "A name matches several items.",
    nothingChanged: true,
    details: "param, kind, value, candidates[]",
    fix: [
      { kind: "same_call_with", when: has("details.candidates.0"), set: { "${details.param}": "${details.candidates.0}" }, note: "or another of the options" },
      { kind: "ask", text: "use the exact name or the id; list or find_elements show what exists" },
    ],
  },
  {
    code: "NAME_TAKEN",
    meaning: "The name or number is already used.",
    nothingChanged: true,
    details: "param, next",
    fix: [
      { kind: "same_call_with", when: has("details.next"), set: { "${details.param}": "${details.next}" } },
      { kind: "ask", text: "choose a name or number that is not used yet" },
    ],
  },
  {
    code: "UNSUPPORTED_VERSION",
    meaning: "This op needs a newer Revit.",
    nothingChanged: true,
    details: "min, year, alternative? {tool, args}",
    fix: [
      { kind: "call", when: has("details.alternative.tool"), tool: "${details.alternative.tool}", args: "${details.alternative.args}" },
      { kind: "ask", when: has("details.min"), text: "this needs Revit ${details.min} or newer; open the model in Revit ${details.min}" },
      { kind: "ask", text: "this needs a newer Revit version" },
    ],
  },
  {
    code: "UNSUPPORTED_OP",
    meaning: "The Revit API cannot do this.",
    nothingChanged: true,
    details: "alternative? {tool, args}, hint?",
    fix: [
      { kind: "call", when: has("details.alternative.tool"), tool: "${details.alternative.tool}", args: "${details.alternative.args}" },
      { kind: "ask", when: has("details.hint"), text: "${details.hint}" },
      { kind: "ask", text: "this step must be done by hand in Revit" },
    ],
  },
  {
    code: "NOT_EDITABLE",
    meaning: "The element is owned by another user or was updated in central.",
    nothingChanged: true,
    details: "owner, reason (owned_by_other | updated_in_central)",
    fix: [
      { kind: "call", when: eq("details.reason", "updated_in_central"), tool: "worksharing", args: { op: "reload_latest" }, note: "then retry" },
      { kind: "ask", when: has("details.owner"), text: "have ${details.owner} relinquish the element (or sync), then retry" },
      { kind: "ask", text: "have its owner relinquish the element (or sync), then retry" },
    ],
  },
  {
    code: "TEMPLATE_CONTROLLED",
    meaning: "The view's template controls this setting.",
    nothingChanged: true,
    details: "template",
    fix: [
      { kind: "same_call_with", when: has("details.template"), set: { view: "${details.template}" }, note: "changes the template, which affects every view using it" },
      { kind: "ask", text: "remove the view template (edit_views set template:'none') or change the template itself" },
    ],
  },
  {
    code: "NO_TAG_FAMILY",
    meaning: "No tag type is loaded for the category.",
    nothingChanged: true,
    details: "category",
    fix: [
      { kind: "ask", when: has("details.category"), text: "load a ${details.category} tag family first (place_family op=load with the tag .rfa path)" },
      { kind: "ask", text: "load a tag family for this category first (place_family op=load with the tag .rfa path)" },
    ],
  },
  {
    code: "LEGEND_NEEDS_SEED",
    meaning: "Revit cannot create a legend from nothing and none exists to copy.",
    nothingChanged: true,
    fix: { kind: "ask", text: "create one empty legend view in Revit once, then retry" },
  },
  {
    code: "CANNOT_CLOSE_ACTIVE",
    meaning: "The only open document is the active one; the API cannot close it.",
    nothingChanged: true,
    fix: { kind: "ask", text: "close it in Revit yourself" },
  },
  {
    code: "LAST_VIEW",
    meaning: "Revit refuses to close the last open view of a document.",
    nothingChanged: true,
    fix: { kind: "call", tool: "manage_document", args: { op: "close", doc: "${doc.n}" } },
  },
  {
    code: "SAVE_AS_REQUIRED",
    meaning: "The document was never saved.",
    nothingChanged: true,
    details: "path? (suggested)",
    fix: [
      { kind: "call", when: has("details.path"), tool: "manage_document", args: { op: "save_as", path: "${details.path}" } },
      { kind: "ask", text: "tell me where to save it, then I call manage_document save_as with that path" },
    ],
  },
  {
    code: "EXPORTER_MISSING",
    meaning: "The exporter for this format is not installed in this Revit.",
    nothingChanged: true,
    details: "format, year",
    fix: [
      { kind: "ask", when: has("details.format"), text: "install the ${details.format} exporter for this Revit version, or choose another format" },
      { kind: "ask", text: "install the exporter for this format, or choose another format" },
    ],
  },
  {
    code: "CAPTURE_GPU_UNAVAILABLE",
    meaning: "Revit could not render the image (DirectX lost, e.g. over RDP).",
    nothingChanged: true,
    fix: { kind: "same_call_with", set: { style: "hidden" } },
  },
  {
    code: "CAPTURE_NOT_EXPORTABLE",
    meaning: "This view cannot be exported as an image.",
    nothingChanged: true,
    details: "viewKind, view",
    fix: [
      { kind: "call", when: eq("details.viewKind", "Schedule"), tool: "read_schedule", args: { schedule: "${details.view}" } },
      { kind: "call", tool: "list", args: { kind: "views", graphical: true } },
    ],
  },

  // ---------------------------------------------------------------- confirm tokens and session caches
  {
    code: "CONFIRM_STALE",
    meaning: "Elements of the confirmed plan changed since the plan was made.",
    nothingChanged: true,
    details: "changedIds?",
    fix: { kind: "same_call_without", drop: ["confirm"], note: "gets a new plan" },
  },
  {
    code: "CONFIRM_EXPIRED",
    meaning: "The confirm token expired or is unknown (tokens live 10 min and are single use).",
    nothingChanged: true,
    fix: [
      { kind: "call", when: has("details.retry"), tool: "${tool}", args: "${details.retry}", note: "gets a new plan" },
      { kind: "ask", text: "repeat the original call without confirm to get a new plan" },
    ],
  },
  {
    code: "CONFIRM_MISMATCH",
    meaning: "The token belongs to a different call, or the re-sent args differ from the plan.",
    nothingChanged: true,
    details: "retry (the call to send)",
    fix: [
      { kind: "call", when: has("details.retry"), tool: "${tool}", args: "${details.retry}" },
      { kind: "same_call_without", drop: ["confirm"], note: "gets a new plan" },
    ],
  },
  {
    code: "PAGE_EXPIRED",
    meaning: "The page token expired (30 min) or is unknown.",
    nothingChanged: true,
    details: "repeat? {tool, args}",
    fix: [
      { kind: "call", when: has("details.repeat.tool"), tool: "${details.repeat.tool}", args: "${details.repeat.args}" },
      { kind: "ask", text: "repeat the original call without page" },
    ],
  },
  {
    code: "HANDLE_EXPIRED",
    meaning: "The handle expired or is unknown (r# lives 60 min).",
    nothingChanged: true,
    details: "repeat? {tool, args}",
    fix: [
      { kind: "call", when: has("details.repeat.tool"), tool: "${details.repeat.tool}", args: "${details.repeat.args}" },
      { kind: "ask", text: "repeat the find_elements call that produced the handle" },
    ],
  },
  {
    code: "CAPTURE_GONE",
    meaning: "The capture id is unknown or its file was cleaned up.",
    nothingChanged: true,
    details: "repeat? {tool, args}",
    fix: [
      { kind: "call", when: has("details.repeat.tool"), tool: "${details.repeat.tool}", args: "${details.repeat.args}" },
      { kind: "ask", text: "capture the view again, then compare with the new id" },
    ],
  },
  {
    code: "JOB_UNKNOWN",
    meaning: "The job or write id is unknown to this session (ids do not survive a broker restart).",
    nothingChanged: true,
    fix: { kind: "call", tool: "status", args: { include: ["writes"] } },
  },
  {
    code: "OUTCOME_EXPIRED",
    meaning: "The write outcome is no longer recorded.",
    nothingChanged: true,
    fix: { kind: "call", tool: "status", args: { include: ["writes"] } },
  },

  // ---------------------------------------------------------------- undo
  {
    code: "UNDO_BLOCKED",
    meaning: "A newer change that is not yours is on top of Revit's undo stack.",
    nothingChanged: true,
    details: "top, compensable?",
    fix: [
      { kind: "call", when: eq("details.compensable", true), tool: "undo", args: { mode: "compensate" } },
      { kind: "ask", when: has("details.top"), text: "press Ctrl+Z in Revit (the newest change is \"${details.top}\")" },
      { kind: "ask", text: "press Ctrl+Z in Revit" },
    ],
  },
  {
    code: "UNDO_UNCONFIRMED",
    meaning: "Revit did not confirm the undo within 10 s.",
    nothingChanged: false,
    fix: { kind: "call", tool: "get_changes", args: { since: "last" } },
  },
  {
    code: "IRREVERSIBLE",
    meaning: "This change cannot be compensated (delete, purge, family load or code).",
    nothingChanged: true,
    fix: { kind: "ask", text: "press Ctrl+Z in Revit to undo it" },
  },

  // ---------------------------------------------------------------- code execution
  {
    code: "CODE_EXECUTION_DISABLED",
    meaning: "run_csharp is not enabled on this computer.",
    nothingChanged: true,
    fix: { kind: "ask", text: "C# execution is off; only you can enable it in the revit-mcp-next settings, and only if you want it (an agent must never edit settings.json); otherwise I use a tool" },
  },
  {
    code: "CODE_EXECUTION_DECLINED",
    meaning: "The user clicked Block in Revit's code-execution prompt.",
    nothingChanged: true,
    fix: { kind: "ask", text: "allow code execution in the Revit prompt if you want it, or I use a tool instead" },
  },
  {
    code: "CODE_EXECUTION_UNAVAILABLE",
    meaning: "The C# engine could not be loaded in this Revit.",
    nothingChanged: true,
    fix: { kind: "call", tool: "status", args: { detail: "full" }, note: "shows why; use a tool instead" },
  },
  {
    code: "CODE_COMPILE_ERROR",
    meaning: "The C# code did not compile.",
    nothingChanged: true,
    details: "line, col, message",
    fix: { kind: "same_call_with", set: { code: "<corrected code>" }, note: "fix line ${details.line}, col ${details.col}" },
  },
  {
    code: "CODE_DENIED_API",
    meaning: "The code uses an API that is not allowed in this mode.",
    nothingChanged: true,
    details: "symbol, line, col",
    fix: { kind: "same_call_with", set: { code: "<code without ${details.symbol}>" } },
  },
  {
    code: "CODE_TIMEOUT",
    meaning: "The code exceeded its time limit and was stopped; everything was rolled back.",
    nothingChanged: true,
    fix: { kind: "same_call_with", set: { code: "<faster code>" } },
  },
  {
    code: "CODE_RUNTIME_ERROR",
    meaning: "The code threw an exception; everything was rolled back.",
    nothingChanged: true,
    details: "line, col, message",
    fix: { kind: "same_call_with", set: { code: "<corrected code>" } },
  },

  // ---------------------------------------------------------------- dialogs
  {
    code: "DIALOG_NOT_FOUND",
    meaning: "No such dialog is open.",
    nothingChanged: true,
    fix: { kind: "call", tool: "ui", args: { op: "dialogs" } },
  },
  {
    code: "DIALOG_PROTECTED",
    meaning: "This button could discard work or answers one of our own prompts; tools never press it.",
    nothingChanged: true,
    fix: { kind: "ask", text: "press the button yourself in Revit if you want it" },
  },
  {
    code: "BUTTON_NOT_FOUND",
    meaning: "The dialog has no such button.",
    nothingChanged: true,
    details: "options[] (buttons)",
    fix: [
      { kind: "choose", when: has("details.options.0"), tool: "ui", arg: "button", base: { op: "press", dialog: "${args.dialog}" } },
      { kind: "call", tool: "ui", args: { op: "dialogs" } },
    ],
  },
];

/** Warning codes (`warn:` lines). */
export const WARNING_CODES: Record<string, string> = {
  REVIT_WARNING: "Revit reported a warning; the change was applied.",
  REVIT_DIALOG_ANSWERED: "A known-safe Revit dialog was answered automatically.",
  VALUE_CLAMPED: "A number was outside its range and was clamped.",
  PARAM_IGNORED: "A param is not used by this op (or unknown) and was ignored.",
  PARAM_RENAMED: "A param name was recognized under another spelling.",
  PARTIAL_RESULT: "Only part of the result is shown.",
  FITTING_FAILED: "A fitting could not be placed; the runs were created.",
  IDS_GONE: "Some ids no longer exist and were skipped.",
  NON_FINITE_NUMBER: "A number was not finite and became null.",
  BRIDGE_RESPONSE_RECOVERED: "The response was lost on the pipe and recovered from the add-in ledger.",
  SETTINGS_INVALID: "settings.json is invalid; the last good values are used.",
  LATE_RESULT: "A call that was given up earlier completed.",
};

/** Notice codes (`notice:` lines). */
export const NOTICE_CODES: Record<string, string> = {
  AUTO_PINNED: "The target doc was chosen automatically and pinned.",
  TARGET_NOW: "The pin moved to this doc (explicit).",
  REBOUND: "The pinned doc was found again (Revit restarted or the doc was reopened).",
  SAVED_AS: "The pinned doc was saved under a new name; work continues on it.",
  PIN_MOVED: "The automatic pin moved to another doc.",
  ACTIVE_DIFFERS: "Revit's active doc is not the pinned target.",
  FAMILY_EDITING: "The user is editing a family in Revit.",
  RECOVERED_WRITE: "A previous session's write outcome was recovered.",
  CODE_EXECUTION_ENABLED: "C# code execution is enabled in settings.",
  SELECTION_STALE: "The selection is from when the doc was last active.",
};

/** Synonyms used by earlier designs (SPEC §19 C22) and older add-ins → the unified code. */
export const CODE_SYNONYMS: Record<string, string> = {
  TARGET_NOT_ACTIVE: "NEEDS_ACTIVE_DOC",
  LEVEL_NOT_FOUND: "NOT_FOUND",
  RELOAD_LATEST_REQUIRED: "NOT_EDITABLE",
  ELEMENT_OWNED_BY_OTHER: "NOT_EDITABLE",
  VIEW_TEMPLATE_CONTROLS: "TEMPLATE_CONTROLLED",
  LEGEND_TEMPLATE_REQUIRED: "LEGEND_NEEDS_SEED",
  NWC_EXPORTER_UNAVAILABLE: "EXPORTER_MISSING",
  INVALID_ARGUMENT: "INVALID_ARGS",
  REVIT_EDIT_MODE: "REVIT_EDIT_MODE_OR_COMMAND",
  DIALOG_DISMISSED: "REVIT_DIALOG_ANSWERED",
  REVIT_DIALOG: "REVIT_DIALOG_OPEN",
  NUMBER_TAKEN: "NAME_TAKEN",
  ZOOM_NO_VIEW: "NOT_FOUND",
};

/** Map an incoming code to the unified catalog code. */
export function canonicalErrorCode(code: string): string {
  if (/^OP_UNAVAILABLE_IN_REVIT_\d{4}$/.test(code)) return "UNSUPPORTED_VERSION";
  return CODE_SYNONYMS[code] ?? code;
}

export const ERROR_CATALOG: Readonly<Record<string, ErrorSpec>> = Object.freeze(
  Object.fromEntries(ERROR_SPECS.map((spec) => [spec.code, spec]))
);

/** Merge tool-specific error specs (ToolSpec.errors) into the global catalog. Global codes win on conflicts. */
export function mergeErrorCatalog(toolErrors: Iterable<Record<string, ErrorSpec> | undefined>): Record<string, ErrorSpec> {
  const merged: Record<string, ErrorSpec> = {};
  for (const errors of toolErrors) {
    if (!errors) continue;
    for (const [code, spec] of Object.entries(errors)) merged[code] = spec;
  }
  for (const spec of ERROR_SPECS) merged[spec.code] = spec;
  return merged;
}

/** Every template in a spec (fix and or), flattened. */
export function templatesOf(spec: ErrorSpec): FixTemplate[] {
  const list = (value: FixTemplate | FixTemplate[] | undefined): FixTemplate[] => (value === undefined ? [] : Array.isArray(value) ? value : [value]);
  return [...list(spec.fix), ...list(spec.or)];
}
