// Server instructions (SPEC §5.7, exact; docs/design/SPEC-instructions.mjs). The head is self-contained within
// Codex's 512-character window. The same text is served by initialize and server/discover and heads help {}.

export const INSTRUCTIONS_HEAD =
  "Revit tools. Start with status: it lists open docs (#1, #2...), the target doc and the active view. Lengths mm, angles degrees, points [x,y] or [x,y,z]; names work where ids do. Writes apply at once and return ids plus an undo hint; preview:true is a dry run. NOT APPLIED means it needs the user's OK: show the plan; repeat with confirm only if the user asked for exactly this or approves. Every error ends with fix: do exactly that. To see the model, call capture. Never use screen automation for Revit.";

export const INSTRUCTIONS_TAIL =
  " Target: the first doc used is pinned and every result names it; set_target {doc:\"#2\"} switches; opening or activating a doc re-pins. find_elements returns a handle r#: pass from:\"r3\", \"selection\" or \"last\" instead of copying ids. Totals are exact; continue lists with the page token in a more: line. In op lists, params after ';' are optional; help {tool,op} gives a working example. change_set runs several ops as one undo step ('$0' = first result id). Long work returns a job j#: call job_status. Never repeat a write that is running or unknown (w#); call job_status. Never edit the revit-mcp-next settings.json yourself.";

export const INSTRUCTIONS = INSTRUCTIONS_HEAD + INSTRUCTIONS_TAIL;

/** Surface gates (§15): head ≤ 512, total ≤ 1,500. */
export const INSTRUCTIONS_HEAD_MAX = 512;
export const INSTRUCTIONS_TOTAL_MAX = 1500;
