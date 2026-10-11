// Tool module for `describe_elements` (P-READ). The add-in handler does the work through the default read handler
// (framework/defaults.ts: doc resolution, pages p#, handles r#, READ_STILL_RUNNING cache). The prepare hook only
// records the first generation this session saw for the target doc, so get_changes {since:"session"} has a start.
import type { ToolModule } from "../framework/types.js";
import { noteTarget } from "./get_changes.js";

export const module: ToolModule = { name: "describe_elements", prepare: noteTarget };
