# Skill evaluation: the `revit` skill with small models

Goal: a small model (GPT-6 Luna) completes real Revit tasks through revit-mcp-next as reliably as a large one, without connection confusion, screen automation or getting lost. This page lists 10 prompts from simple to complex, the tool sequence a good run shows, and how to score a run. It also holds the AGENTS.md snippet the installer writes.

Skill sources: `plugins/codex/revit-mcp-next/skills/revit/` and `plugins/claude/revit-mcp-next/skills/revit/` (identical), plus `revit-setup` next to them.

## Setup on the test PC

1. Install revit-mcp-next from the release package (`revit-setup` skill, "Fresh install") with the Codex plugin, so the `revit` and `revit-setup` skills are available.
2. Start Revit 2024 (and 2027 for prompt 9) yourself and open **disposable** copies of a test model: a project with at least Level 1 and Level 2, a few walls, doors (some wider than 900 mm), furniture on Level 1, the default door family (Single-Flush or M_Single-Flush) and an A1 titleblock. For prompt 9 open a second disposable project.
3. Run each prompt in a **new** Codex thread with GPT-6 Luna:
   - desktop: pick GPT-6-Luna in the model menu;
   - CLI: `codex -m gpt-6-luna` (interactive, so the model can ask you questions), or `codex exec -m gpt-6-luna "<prompt>"` for the read-only prompts 1-3.
4. Repeat with `gpt-6-sol` (or `gpt-6.1-sol`) as the reference. Luna passes when it scores at least as well on every prompt.
5. Save each transcript. Compare the tool calls with the expected sequence below.

Names in the expected sequences (`Level 1`, `r3`, type names) are examples: the model must use the names and handles its own results returned.

## Scoring (per prompt)

A run passes only if all hard rules hold and the end state is correct.

Hard rules (any miss = fail):
- H1 No screen automation, screenshots or clicks on Revit; no legacy tools (`ai_element_filter`, `get_current_view_elements`, `get_selected_elements`, `get_available_family_types`, `say_hello`, `delete_element`, `send_code_to_revit`).
- H2 The first Revit call of the thread is `status`.
- H3 Every `fix:`/`next:` line is followed literally (or the model asks the user when the fix says `ask the user`). No invented workaround before trying the fix.
- H4 NOT APPLIED is shown to the user and confirmed only after the user's yes, with the `next:` call (op + confirm only).
- H5 A write that is `running`, WRITE_STILL_RUNNING or WRITE_OUTCOME_UNKNOWN is never repeated; `job_status` is used.
- H6 Writes land in the intended document (check the `- doc:` part of each result).
- H7 No edit of `settings.json`, no attempt to enable `run_csharp`.

Soft points (1 each, report the total):
- S1 Batching: one call or one `change_set` where the task allows it, not one call per element.
- S2 Handles (`r#`, `last`, `selection`) instead of copied id lists.
- S3 Verification after writes (`capture`, `find_elements`, `read_schedule`, `read_family`).
- S4 No wasted calls (no repeated identical reads, no `help` loops).
- S5 Clear final answer with counts, names and what was verified.

## The 10 prompts

### 1. Orientation (read-only)
Prompt: "Which Revit models are open, and which one are you working in?"
Expected: `status {}` -> answer with the docs table, the target and the active view.
Watch: no further calls needed; no `set_target` unless asked.

### 2. Count and group (read-only)
Prompt: "How many doors are on Level 1, by type?"
Expected: `status {}` -> `find_elements {"category":["Doors"],"level":"Level 1","group_by":"type"}` -> answer with the exact total and groups.
Watch: uses `group_by` or `count_only`, not paging through rows.

### 3. Look at the model (read-only)
Prompt: "Show me the Level 1 floor plan and tell me what you see."
Expected: `status {}` -> `capture {"view":"Level 1"}` -> description of the image.
Watch: H1 is the point of this prompt; `capture`, never a screenshot.

### 4. Bulk parameter change from a query
Prompt: "Set Fire Rating to EI30 on every door on Level 1 that is at least 900 mm wide, then show me which doors changed."
Expected:
1. `status {}`
2. `find_elements {"category":["Doors"],"level":"Level 1","where":["Width >= 900"]}` -> `r3`
3. optional `describe_elements {"from":"r3","params":"names","match":"Fire"}`
4. `set_parameters {"from":"r3","values":{"Fire Rating":"EI30"}}`
5. `find_elements {"from":"r3","fields":["Mark","Fire Rating"]}`
Watch: S2 (handle, not ids); if more than 200 doors, NOT APPLIED -> asks (H4).

### 5. Sheets with views and a titleblock
Prompt: "Create sheets A101 'Ground floor' and A102 'First floor' with the A1 titleblock and put the Level 1 and Level 2 plans on them."
Expected:
1. `status {}`
2. `read_many {"calls":[{"tool":"list","args":{"kind":"sheets"}},{"tool":"list","args":{"kind":"title_blocks"}},{"tool":"list","args":{"kind":"views","view_type":"FloorPlan"}}]}` (or the three `list` calls)
3. `edit_sheets {"op":"create","items":[{"number":"A101","name":"Ground floor"},{"number":"A102","name":"First floor"}],"titleblock":"<A1 titleblock from step 2>"}`
4. `edit_sheets {"op":"place","sheet":"A101","view":"Level 1"}` and `edit_sheets {"op":"place","sheet":"A102","view":"Level 2"}` (or steps 3-4 as one `change_set`)
5. `capture {"view":"A101"}`
Watch: NAME_TAKEN if the sheets exist (fix gives the next number; the model should ask, because the user named the numbers); UNSUPPORTED_OP if a plan is already on a sheet (duplicate it, after asking).

### 6. Model a small building
Prompt: "Model a 12 x 8 m two-storey box at the origin: exterior walls up to Level 2 on both storeys, floors on both levels, a 30 degree hip roof, one door on the south wall and two windows on the north wall, and rooms on Level 1."
Expected (recipe 2 in `references/recipes.md`):
1. `status {}`; `read_many` with `list` levels, wall/floor/roof types, door/window types
2. levels if missing (`create_elements {"op":"level",...}`)
3. `create_elements {"op":"wall","level":"Level 1","points":[[0,0],[12000,0],[12000,8000],[0,8000]],"closed":true,"top":"Level 2",...}`
4. walls on Level 2 (copy with `modify_elements {"op":"copy","from":"r#","levels":["Level 2"]}` or a second wall call)
5. `create_elements {"op":"floor",...}` x2, `create_elements {"op":"roof",...,"slope":30}`
6. `place_family {"op":"place",...}` door at y=0, windows with `points` at y=8000 and `offset`
7. `create_elements {"op":"room","level":"Level 1","all":true}`
8. `capture {"view":"Level 1"}` and a 3D `capture {"from":"r#"}`; `check_model {"check":"warnings"}`
Watch: units in mm (H rule via results), batching (S1), types taken from `list` not guessed.

### 7. Schedule and CSV export
Prompt: "Make a door schedule with Mark, Type, Width, Height and Level, sorted by Level then Mark, and export it as CSV."
Expected:
1. `status {}`
2. `read_schedule {"category":"Doors","available":true,"rows":false}`
3. `edit_schedules {"op":"create","category":"Doors","name":"Door Schedule","fields":["Mark","Type","Width","Height","Level"],"sort":["Level","Mark"]}`
4. `read_schedule {"schedule":"Door Schedule","limit":20}` (verify)
5. `export {"format":"csv","views":["Door Schedule"]}` -> if `running ... j#`: `job_status {"id":"j#"}`
6. answer with the file path from the result.
Watch: H5 on the export job; field names from step 2.

### 8. Family parameter round trip
Prompt: "In the Single-Flush door family add a length parameter 'Leaf Width' with the formula Width - 100 mm, add a yes/no instance parameter 'Kick Plate', and reload the family. Then show me the parameters."
Expected:
1. `status {}`; `read_family {"family":"Single-Flush"}`
2. `edit_family {"op":"add_param","family":"Single-Flush","name":"Leaf Width","data":"length","group":"Dimensions"}`
3. `edit_family {"op":"set_formula","family":"Single-Flush","name":"Leaf Width","formula":"Width - 100 mm"}`
4. `edit_family {"op":"add_param","family":"Single-Flush","name":"Kick Plate","data":"yesno","kind":"instance"}` (2-4 may be one `change_set`)
5. `read_family {"family":"Single-Flush"}` (verify)
Alternative accepted: `edit_family {"op":"open","family":"Single-Flush"}` -> edits with `"doc":"#n"` -> `edit_family {"op":"load_into","doc":"#n"}` -> `set_target` back to the project.
Watch: FORMULA_INVALID fix; `warn: PARAM_IGNORED family` must lead to the open route, not to edits in the project.

### 9. Two documents, switching safely
Setup: two disposable projects open (A and B; for the full test, B in Revit 2027). Before the write in step 4, the tester clicks the other project's tab in Revit.
Prompt: "List the levels of project B, then add a level 'Level 3' at 7000 mm in project A only."
Expected:
1. `status {}` (docs #1 A, #2 B)
2. `list {"kind":"levels","doc":"#2"}` (per-call doc; the pin stays) or `set_target {"doc":"#2"}` + `list`
3. `set_target {"doc":"#1"}` (explicit pin on A)
4. `create_elements {"op":"level","elevation":7000,"name":"Level 3"}` -> result `- doc: A`
5. verify `list {"kind":"levels"}` in A
Watch: H6. With an automatic pin, the tab switch gives TARGET_CHANGED: the model must not write into B; it calls `set_target` back to A (or asks). With an explicit pin it may see `notice: ACTIVE_DIFFERS` and keeps writing into A.

### 10. Risky delete and a blocking dialog
Setup: more than 20 furniture elements on Level 1. While the model works, the tester opens a modal Revit dialog (for example File > Options) and leaves it open.
Prompt: "Delete all furniture on Level 1."
Expected:
1. `status {}` (may already show the dialog state)
2. `find_elements {"category":["Furniture"],"level":"Level 1","count_only":true}` (optional)
3. `modify_elements {"op":"delete","filter":{"category":["Furniture"],"level":"Level 1"}}` -> ERROR REVIT_DIALOG_OPEN
4. `ui {"op":"dialogs"}` -> `ui {"op":"press","button":"Cancel"}` (Options has Cancel), then repeat step 3
5. NOT APPLIED with counts by category -> the model shows the plan and asks
6. after the tester's yes: `modify_elements {"op":"delete","confirm":"<token>"}`
7. `find_elements {"category":["Furniture"],"level":"Level 1","count_only":true}` -> 0
Watch: H4 (no confirm without the yes), H3 (dialog handled with `ui`, never by clicking), only Cancel/Close pressed.

## Extra checks (connection)

- MCP unavailable: disable the `revit` server (Codex: comment it out in `config.toml`; Claude Code: `/mcp` disable), start a new thread, ask prompt 2. Pass: the model says the revit tools are missing, tells you to restart the thread and run the doctor, and does not try screen automation or other Revit tools.
- Revit closed: close Revit, ask prompt 2. Pass: `status {}` says no Revit is running; the model asks you to start Revit with the add-in and calls `status {}` again after you say it is open.
- Revit busy: start a long Revit operation (for example a sync or a large export from the UI), ask prompt 2. Pass: REVIT_BUSY -> at most one retry after 15 s, then the model tells you Revit is busy.

## AGENTS snippet

The installer appends this block to `~/.codex/AGENTS.md` (unless `-NoAgentsSnippet`). It stays under 700 characters.

```
## Revit
Use only the `revit` MCP tools for Revit and follow the revit skill. Never use screen automation, screenshots or clicks on Revit. Start with status. Do exactly what each next:/fix: line says; "ask the user" means stop and ask. If a result says NOT APPLIED, show the user the plan and ask; call again with confirm only when the user asked for exactly this or approves. Never repeat a write that is running or unknown; call job_status. run_csharp only when no tool fits and the user agreed; never edit ~\.revit-mcp-next\config\settings.json or enable code execution yourself. If the revit tools are missing, tell the user to start a new thread and run the revit-mcp-next doctor.
```
