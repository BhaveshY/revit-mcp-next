// Help topics (SPEC §5.7, §5.8, D1 §4B help). Served by help {topic}, the start_workflow/workflow prompts and the
// revit://help/{name} resources. Error codes are topics too (help {topic:"TARGET_CHANGED"}), rendered from the ErrorCatalog.
// Owner: W1-BROKER (wave 1), W3-DOCS (wave 3). Frozen in wave 2.

export interface HelpTopic {
  name: string;
  title: string;
  aliases?: string[];
  /** Plain text, one paragraph per line. */
  text: string;
  /** Optional structured content (e.g. the recipe schema). */
  data?: unknown;
}

export const WORKFLOW_NAMES = [
  "audit",
  "selection-update",
  "sheet-planning",
  "family-placement",
  "room-layout",
  "visual-check",
  "family-edit",
  "documentation",
] as const;
export type WorkflowName = (typeof WORKFLOW_NAMES)[number];

/** Model delivery recipe (the old ModelDeliveryRecipe schema, validated server-side). */
export const RECIPE_SCHEMA = {
  recipe: {
    projectId: "string 1-128, stable project id (recipe library key)",
    recipeVersion: "string 1-128",
    deliveryId: "string 1-64, stable per run (reconciles retry staging)",
    packageName: "directory name (no path), 1-128",
    destinationRoot: "absolute or UNC path visible to Revit, 1-1024",
    sourceModels: [
      {
        id: "string 1-64, used by link rules",
        sourcePath: "absolute or UNC .rvt path visible to Revit",
        targetFileName: "*.rvt file name inside the package (no path), 5-240",
        "role?": "string label shown in previews",
        "startViewName?": "exact protected opening view name",
        "start3dViewName?": "exact protected 3D view name",
      },
    ],
    "sourceModels.length": "1-64",
    linkRules: [
      {
        sourceModelId: "id of a source model whose existing link types this rule governs",
        "matchPath?": "exact existing link path (canonicalized); matchPath or matchFileName required",
        "matchFileName?": "linked file name when the path cannot be matched",
        action: "repath | unload | remove | retain",
        "targetModelId?": "required for repath: another source model id",
        "expectedInstanceCount?": "0-256 exact instance-count guard",
        "required?": "boolean: block preview when the rule matches no link (default false)",
      },
    ],
    "linkRules.length": "0-512",
    coordinates: { preserveLinkTransforms: "true (fixed)", packagedLinkPathType: "\"relative\" (fixed)" },
    cleanup: {
      deleteSheets: "boolean",
      deleteViews: "boolean",
      deleteSchedules: "boolean",
      deleteLegends: "boolean",
      deleteDraftingViews: "boolean",
      deleteViewTemplates: "boolean",
      deleteUnusedFilters: "boolean",
      removeUnmappedLinks: "boolean",
      purgeUnusedPasses: "integer 0-3",
      protectedViewNames: "string[] (≤128 names, each ≤256)",
      note: "every cleanup decision is explicit; nothing destructive is defaulted",
    },
    exports: [
      {
        format: "ifc | dwg | nwc",
        "modelIds?": "source model ids (1-64); omit = every packaged model",
        "setupName?": "export setup name",
        "outputSubdirectory?": "directory name (no path)",
        "viewNames?": "string[] ≤256",
        required: "boolean",
      },
    ],
    "exports.length": "0-24",
    qa: {
      requireStandalone: "true (fixed)",
      requireNoCentralPath: "true (fixed)",
      requireSourceHashUnchanged: "true (fixed)",
      requireCleanupMatchesPreview: "true (fixed)",
      requireAllRequiredExports: "true (fixed)",
      "maxWarnings?": "integer 0-100000",
    },
  },
  rules: [
    "No unknown keys anywhere (strict objects).",
    "save_recipe expected_sha only overwrites that version (optimistic concurrency).",
    "preview returns the plan and a confirm token; execute takes only {op:'execute', confirm} and runs as job j#.",
  ],
};

const lines = (...parts: string[]) => parts.join("\n");

export const TOPICS: HelpTopic[] = [
  {
    name: "profiles",
    title: "Tool profiles",
    text: lines(
      "full (default): all 37 tools (run_csharp is added only when the user enabled code execution).",
      "core (REVIT_MCP_NEXT_PROFILE=core): 17 tools: status, set_target, list, find_elements, describe_elements, get_view, capture, ui, create_elements, place_family, modify_elements, set_parameters, change_set, undo, job_status, help, read_many.",
      "core is lossy: every read is reachable through read_many and every non-lifecycle write through change_set, but documents (open/save/close), worksharing, links reload, export, model delivery, cancel_job and run_csharp are not reachable.",
      "tools/list never varies per connection, only per server configuration."
    ),
  },
  {
    name: "confirm",
    title: "Writes, preview and confirm tokens",
    aliases: ["preview", "not_applied", "blast"],
    text: lines(
      "Writes apply at once and return created/modified/deleted ids, warnings, an undo hint, a handle r# and a write id w#.",
      "preview:true runs the same op inside a rolled-back scope: nothing is committed; the result gives the plan, counts, a delete sample, expected warnings and a confirm token.",
      "NOT APPLIED - needs the user's OK: a high-blast call (delete > 20 incl. dependents, > 200 existing elements modified, > 500 created, file overwrite, close with unsaved changes, loading a family into several docs, opening a central, sync/enable worksharing, coordinates, link remove, model delivery, C# commit, pressing a non-Cancel dialog button). Nothing was changed. Show the plan to the user; repeat with confirm only if the user asked for exactly this or approves.",
      "Apply a plan: <tool> {\"op\":\"<op>\",\"confirm\":\"K7QM2X\"}. Do not re-send the other args; the stored plan is applied. Tokens are 6 characters, single use, valid 10 minutes, at most 32 per session.",
      "CONFIRM_STALE: an affected element changed since the plan; repeat without confirm for a new plan. Unrelated edits do not invalidate a token. CONFIRM_MISMATCH: send only op and confirm. CONFIRM_EXPIRED: repeat the original call without confirm.",
      "change_set is confirmed once for the whole set.",
      "Workshared models: a NOT APPLIED write may have borrowed elements while it was evaluated; they are released at the next sync. preview:true never borrows (elements owned by others get static validation only: 'not probed').",
      "Family reloads inside a change_set are rolled back with the set when the add-in reports family segments as atomic; otherwise they run last and are not rolled back, and ops that depend on new family params need a second call."
    ),
  },
  {
    name: "targeting",
    title: "Target document",
    aliases: ["target", "doc", "set_target"],
    text: lines(
      "The first doc a call uses is pinned for this session and every result names it (- doc: <title> (Revit <year>)).",
      "Auto choice: the only open project; else the active project of the most recently used Revit window; else the only family doc; else NO_OPEN_DOCUMENT. Family docs are never auto-picked while a project is open.",
      "doc grammar (per-call doc= and set_target): #3 or 3 (numbers from status), active, exact title (extension optional, case-insensitive), absolute path, title@2027 (same title open in two Revit versions); set_target also takes follow (track the active tab) and none (unpin).",
      "Reads also accept a unique title prefix or contains match; writes need an exact match, otherwise TARGET_AMBIGUOUS with numbered options. A per-call doc never moves the pin.",
      "Re-pin: set_target, ui activate_doc, ui activate_view (when it had to activate the doc), manage_document activate/open/new_project/new_family and edit_family open move the pin explicitly (notice TARGET_NOW). Closing the pinned doc with manage_document close clears it.",
      "TARGET_CHANGED: the automatic pin moved (doc closed or not reopened after a restart), follow mode changed doc, or the user switched the active project under an automatic pin since your last write. Nothing was written. Repeat the identical call to write into the new doc, or set_target the doc you meant.",
      "Explicit pins are never gated; when Revit's active doc differs you get notice ACTIVE_DIFFERS once per change.",
      "Project-only tools use the session's last project pin while the pin is a family doc; edit_family load_into defaults to that project only."
    ),
  },
  {
    name: "units",
    title: "Units, points and geometry",
    aliases: ["geometry", "coordinates"],
    text: lines(
      "Lengths are mm in and out; areas m2, volumes m3, angles degrees (counterclockwise from +X), floor/roof slope degrees, pipe slope percent.",
      "Strings with units are accepted where a number is: \"3.5 m\", \"900mm\", \"12'6\\\"\", \"90°\", \"2%\". The per-call units param (mm|cm|m|in|ft) sets the unit of plain numbers for that call.",
      "Points are [x,y] or [x,y,z] in Revit internal coordinates (mm). z is height above level when level is given, otherwise absolute (also for toposolids). Level-based sketch elements use offset instead of z.",
      "Vectors [dx,dy(,dz)]; rectangles [x0,y0,x1,y1]; boxes [x0,y0,z0,x1,y1,z1]; sheet coordinates are mm from the sheet origin.",
      "Wall openings use start/end as model XY points on the wall (projected onto it), offset = sill height, height = opening height. roof_extrusion profile is [[s,z],...] along start->end.",
      "Outputs: coordinates/lengths 0.1 mm, areas 2 decimals, volumes 3, angles 0.1 degree; ids are plain integers. status detail:full shows the base point, survey point and true north."
    ),
  },
  {
    name: "selectors",
    title: "Choosing elements",
    aliases: ["handles", "ids", "from", "filter"],
    text: lines(
      "Write ops that act on existing elements take exactly one of: ids (element ids, numbers ok, or UniqueIds), from (an r# handle, 'selection' or 'last'), filter (find_elements keys: {category,class,level,type,family,view,workset,phase,design_option,where,box}).",
      "find_elements returns a handle r# with the full id list (up to 100,000, valid 60 min): pass from:\"r3\" instead of copying ids. Write results also return a handle of the touched elements.",
      "last = the elements your last successful write in this doc created or modified. selection = the Revit selection of the target doc (notice SELECTION_STALE when the doc is not active).",
      "Ids deleted meanwhile are skipped with warn IDS_GONE. Handles of linked elements are read-only (LINK_READ_ONLY)."
    ),
  },
  {
    name: "paging",
    title: "Totals and pages",
    aliases: ["pages", "more"],
    text: lines(
      "Totals are exact. Lists return {total, cols, rows}; the summary says 'showing 1-50 of 1,234, more p4'.",
      "Continue with the line: more: <tool> {\"page\":\"p4\"}. Page tokens are short, valid 30 minutes, and replay the original args (find_elements pages re-query the handle's id slices, so paging is stable while the model changes).",
      "limit sets rows per page (1-500, default 50). detail:\"full\" and fields= add columns.",
      "PAGE_EXPIRED or HANDLE_EXPIRED: repeat the original call."
    ),
  },
  {
    name: "jobs",
    title: "Time budget and jobs",
    aliases: ["job", "budget", "running"],
    text: lines(
      "Every call answers within the budget (50 s by default). Exports, syncs, opens, model delivery and long scans may continue as a job: 'running: ... as job j3' with next: job_status {\"id\":\"j3\"}. Do not repeat that call.",
      "job_status {id, wait} waits up to wait seconds (0-45, default 20) and returns the final result when done; job_status {} lists your recent jobs. cancel_job stops a queued or running job between steps; partial exports are removed.",
      "READ_STILL_RUNNING j#: a read that cannot yield is still running; job_status j# (or repeating the identical call) returns the cached result.",
      "WRITE_STILL_RUNNING w#: the write is still running; never repeat it, call job_status {\"id\":\"w#\"}. WRITE_OUTCOME_UNKNOWN: Revit exited mid-write; reopen and check before doing anything else.",
      "REVIT_BUSY: Revit is busy with other work; nothing ran. Follow the fix line (job_status for your own work, otherwise retry once later)."
    ),
  },
  {
    name: "images",
    title: "Seeing the model (capture)",
    aliases: ["capture", "visual"],
    text: lines(
      "capture returns an image block (PNG for drawings and sheets, JPEG for shaded styles) plus a text line naming the view, the pixel size and a capture id c#.",
      "size small|medium|large caps the long edge at 768/1280/1568 px; use region=[x0,y0,x1,y1] (mm) rather than a larger size for detail. bounds and mm_per_px in the JSON map pixels back to model coordinates.",
      "ids/from focus elements in a temporary 3D box; highlight tints elements red; compare:\"c4\" marks pixels that changed since capture c4 (same view, size and region).",
      "Nothing is kept in the model: temporary views and overrides are rolled back. Images are capped at about 1.5 MB base64; read_many returns at most 2 images (2 MB).",
      "Never use screen automation or screenshots of Revit; capture is the only way to look."
    ),
  },
  {
    name: "errors",
    title: "Errors and fix lines",
    aliases: ["error", "fix"],
    text: lines(
      "Errors look like: ERROR <CODE>: <what happened> / fix: <exact next call or 'ask the user: ...'> / options: 1) ... 2) ... / {\"details\":...}.",
      "Do exactly what the fix line says. 'ask the user: ...' means stop and ask; 'retry in 10 s' means retry at most once.",
      "help {\"topic\":\"<CODE>\"} explains any code. Codes say whether nothing changed (e.g. REVIT_BUSY, REVIT_DIALOG_OPEN, TARGET_CHANGED, INVALID_ARGS) or the outcome is uncertain (WRITE_STILL_RUNNING, WRITE_OUTCOME_UNKNOWN).",
      "Warnings (warn: lines) never fail a call: REVIT_WARNING, REVIT_DIALOG_ANSWERED, VALUE_CLAMPED, PARAM_IGNORED, PARAM_RENAMED, PARTIAL_RESULT, IDS_GONE and others."
    ),
  },
  {
    name: "dialogs",
    title: "Revit dialogs",
    aliases: ["dialog", "REVIT_DIALOG"],
    text: lines(
      "REVIT_DIALOG_OPEN: a modal dialog blocks Revit and nothing ran. Call ui {\"op\":\"dialogs\"} to see its title, text and buttons.",
      "Press only Cancel or Close yourself: ui {\"op\":\"press\",\"button\":\"Cancel\"}. Any other button returns NOT APPLIED and needs the user's OK. Buttons that could discard work (No / Do not save / Yes on save, sync or delete dialogs) and our own prompts are never pressed by tools; ask the user.",
      "Known-safe dialogs that appear during MCP work are answered automatically and reported as warn: REVIT_DIALOG_ANSWERED."
    ),
  },
  {
    name: "change_set",
    title: "Change sets and references",
    aliases: ["refs", "batch"],
    text: lines(
      "change_set {ops:[{tool, op, ...that op's params}], name?, doc?, preview?} runs up to 100 write ops as one undo step: all or nothing.",
      "References to earlier results: \"$0\" (first created id of op 0, else first modified), \"$0.ids\" (all created ids), \"$0.ids[2]\", \"$1.type\" / \"$1.view\" / \"$1.sheet\" / \"$1.level\" (named outputs), \"$prev\" (the previous op), \"$$\" (a literal $). Only earlier ops can be referenced.",
      "One doc per set: put doc on the change_set, not on the ops. Lifecycle ops (open, save, save_as, close, sync, reload_latest, relinquish, enable, borrow, link reload/unload, export, model_delivery, edit_family open/save/save_as/load_into, undo, run_csharp) are refused; call them alone.",
      "Errors name the op index: '[3] create_elements.floor: ...'. Warnings are tagged the same way."
    ),
  },
  {
    name: "run_csharp",
    title: "C# execution (opt-in)",
    aliases: ["csharp", "code"],
    text: lines(
      "run_csharp exists only when the user enabled it locally (enableCodeExecution in the revit-mcp-next settings.json). Only the user changes that setting; an agent must never edit settings.json or enable code execution. Prefer a tool whenever one fits.",
      "code is a C# method body with doc, uidoc, app, args (IDictionary) and log(object); return a value. Only framework, RevitAPI and RevitAPIUI types are available.",
      "mode read: always rolled back; creating Transactions is refused. mode dry_run (default): runs in a rolled-back group and reports changes, warnings and a confirm token. mode commit: {\"mode\":\"commit\",\"confirm\":\"<token>\"} applies exactly the dry-run code and args (not re-sent) after the user agreed; it is one undo step.",
      "Refused always: processes, network, registry, reflection emit, threads, file writes, PostCommand, UI automation. Refused in read/dry_run: save, close, sync, export, open, EditFamily and other lifecycle APIs.",
      "timeout 1-45 s (default 30). Compile errors report line:col of your code. Every run is audited in <home>\\logs\\code-exec-audit.jsonl."
    ),
  },
  {
    name: "recipe",
    title: "Model delivery recipe",
    aliases: ["model_delivery", "delivery"],
    text: lines(
      "model_delivery packages standalone RVTs from a saved recipe: inspect sources, save_recipe, preview (returns the plan and a confirm token), then execute {\"op\":\"execute\",\"confirm\":\"<token>\"} as job j#.",
      "The recipe shape is in data.recipe below; it is validated strictly (unknown keys are rejected, validation errors name the JSON path)."
    ),
    data: RECIPE_SCHEMA,
  },
  // ------------------------------------------------------------------ workflows (also prompts)
  {
    name: "workflow:audit",
    title: "Model audit",
    aliases: ["audit"],
    text: lines(
      "1) status {} (docs, target, levels). 2) check_model {\"check\":\"stats\"} and {\"check\":\"warnings\"}. 3) check_model {\"check\":\"readiness\"} and {\"check\":\"purgeable\"}.",
      "4) list {\"kind\":\"links\"}, {\"kind\":\"worksets\"}, {\"kind\":\"phases\"} or read_many to batch them. 5) find_elements with group_by for any category that needs detail. 6) capture {} to look at the active view.",
      "Report counts and problems; do not change anything unless the user asks."
    ),
  },
  {
    name: "workflow:selection-update",
    title: "Update the selected elements",
    aliases: ["selection"],
    text: lines(
      "1) find_elements {\"from\":\"selection\"} (handle r#). 2) describe_elements {\"from\":\"r#\"} to see writable parameters.",
      "3) set_parameters {\"from\":\"r#\",\"values\":{...}} (or modify_elements change_type). 4) If NOT APPLIED, show the plan and ask. 5) capture {\"from\":\"last\"} to check."
    ),
  },
  {
    name: "workflow:sheet-planning",
    title: "Sheets and views",
    aliases: ["sheets", "workflow:sheets"],
    text: lines(
      "1) list {\"kind\":\"sheets\"}, {\"kind\":\"views\",\"placed\":false,\"printable\":true}, {\"kind\":\"title_blocks\"}.",
      "2) edit_sheets create (items for many), then edit_sheets place {sheet, view} per view; edit_views create/duplicate for missing views.",
      "3) Schedules: read_schedule {category, available:true} for fields, edit_schedules create, then edit_sheets place. 4) capture {\"view\":\"A101\"} to check the sheet. Use change_set to do it in one undo step."
    ),
  },
  {
    name: "workflow:family-placement",
    title: "Placing families",
    aliases: ["family", "workflow:family", "placement"],
    text: lines(
      "1) list {\"kind\":\"types\",\"category\":[\"Doors\"]} (or families) to find the type. If missing: place_family {\"op\":\"load\",\"path\":\"...rfa\"}.",
      "2) list {\"kind\":\"levels\"}; hosts are found automatically for doors/windows (or pass host).",
      "3) place_family {\"op\":\"place\",\"type\":\"Family: Type\",\"at\":[x,y],\"level\":\"Level 1\"} (points for many). 4) capture {\"from\":\"last\"}."
    ),
  },
  {
    name: "workflow:room-layout",
    title: "Rooms",
    aliases: ["rooms", "workflow:rooms"],
    text: lines(
      "1) list {\"kind\":\"rooms\",\"level\":\"Level 1\"} to avoid duplicate numbers. 2) create_elements walls or room_separator lines where boundaries are missing.",
      "3) create_elements {\"op\":\"room\",\"level\":\"Level 1\",\"all\":true} or at=[x,y] with name/number. 4) annotate tag_all {\"category\":[\"Rooms\"]}. 5) capture to check."
    ),
  },
  {
    name: "workflow:visual-check",
    title: "Visual check",
    aliases: ["visual", "workflow:visual"],
    text: lines(
      "1) capture {} (active view) or capture {\"view\":\"<name>\"}; capture {\"from\":\"r#\"} focuses elements in a temporary 3D box.",
      "2) Zoom with region=[x0,y0,x1,y1] using bounds and mm_per_px from the previous capture. 3) After a change, capture {\"view\":\"...\",\"compare\":\"c#\"} to see what changed.",
      "Never use screenshots or screen automation of Revit."
    ),
  },
  {
    name: "workflow:family-edit",
    title: "Editing family parameters",
    aliases: ["family_edit", "workflow:family_edit"],
    text: lines(
      "1) read_family {\"family\":\"<name>\"} (params, formulas, per-type values). 2) edit_family {\"op\":\"add_param\",\"family\":\"<name>\",\"name\":\"...\",\"data\":\"length\"} etc.: the family is edited in the background and reloaded.",
      "3) set_values with types (\"*\" = all types) or rows per type; set_formula; add_type/rename_type/delete_type.",
      "Project type values are overwritten only when the call changed type values or overwrite:true. For several edits use change_set or edit_family open (keeps doc #n), edit with doc:\"#n\", then load_into."
    ),
  },
  {
    name: "workflow:documentation",
    title: "Documentation set",
    aliases: ["documentation", "workflow:docs"],
    text: lines(
      "1) edit_views create plans/sections/elevations; edit_views set scale, detail level, crop and view range.",
      "2) annotate tag_all, dimension (ids with mode, or refs from describe_elements), text, detail lines.",
      "3) edit_sheets create and place; revisions with revision_create and revision_add. 4) export {\"format\":\"pdf\",\"views\":[\"all_sheets\"],\"combine\":true} (job)."
    ),
  },
];

const byTopicName = new Map<string, HelpTopic>();
for (const topic of TOPICS) {
  byTopicName.set(topic.name.toLowerCase(), topic);
  for (const alias of topic.aliases ?? []) byTopicName.set(alias.toLowerCase(), topic);
}

/** Find a topic by name or alias (case-insensitive; "workflow:" prefix optional for workflows). */
export function findTopic(name: string): HelpTopic | undefined {
  const key = name.trim().toLowerCase().replace(/\s+/g, "");
  return byTopicName.get(key) ?? byTopicName.get(`workflow:${key}`) ?? byTopicName.get(key.replace(/^workflow:/, ""));
}

export const TOPIC_NAMES: readonly string[] = Object.freeze(TOPICS.map((topic) => topic.name));

/** The workflow topic for a prompt name (start_workflow / workflow {name}). */
export function workflowTopic(name: WorkflowName | string): HelpTopic | undefined {
  return byTopicName.get(`workflow:${name.toLowerCase()}`);
}
