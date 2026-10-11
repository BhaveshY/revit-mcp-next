# Revit recipes (revit-mcp-next)

Step-by-step calls for common and complex tasks. Every call is `tool {json}`: send the JSON as the tool's arguments. Replace names in quotes (`"Level 1"`, `"Generic - 200mm"`, `"A101"`) with the names the model really uses: `list` shows them. Replace `r3`, `c1`, `#2` with the handles your results returned.

General pattern for every recipe:
1. `status {}` and make sure the `- doc:` line is the model the user means (`set_target` if not).
2. Read what exists (`list`, `find_elements`, `read_many`) before creating anything.
3. Write in batches. If a result says NOT APPLIED, show the plan and ask the user.
4. Verify with `capture` and `find_elements`/`describe_elements`. Report what you checked.
5. On any `ERROR`, do what its `fix:` line says (see the error table in SKILL.md).

Contents: 1 audit · 2 small building · 3 views and sheets · 4 tags and dimensions · 5 schedules · 6 family parameters · 7 bulk parameter changes · 8 view graphics · 9 MEP · 10 structure · 11 documents and worksharing · 12 links · 13 exports · 14 visual QA loop · 15 undo and revert · 16 dialogs · 17 C# (last resort).

---

## 1. Model audit (read-only)

Preconditions: a model is open. Change nothing unless the user asks.

1. `status {}`
2. `read_many {"calls":[{"tool":"check_model","args":{"check":"stats"}},{"tool":"check_model","args":{"check":"warnings"}},{"tool":"check_model","args":{"check":"readiness","scenarios":["all"]}},{"tool":"list","args":{"kind":"links"}},{"tool":"list","args":{"kind":"worksets"}}]}`
3. Unused content: `check_model {"check":"purgeable"}` (may become a job: follow `next:`).
4. Detail where needed: `find_elements {"category":["Walls"],"group_by":"type"}`, `check_model {"check":"warnings","severity":"error"}`.
5. Look: `capture {}` (active view).

Report counts, warnings grouped by message, and problems. Purging is `manage_document {"op":"purge","preview":true}` and only on request.

---

## 2. Model a small building (levels, grids, walls, floors, roof, doors, windows, rooms)

Example: 12 x 8 m, two storeys of 3.5 m, hip roof, one interior wall, one door, two windows, rooms.

Preconditions: a project doc is the target. Units mm.

1. Read what exists in one call:
   `read_many {"calls":[{"tool":"list","args":{"kind":"levels"}},{"tool":"list","args":{"kind":"types","category":["Walls","Floors","Roofs"]}},{"tool":"list","args":{"kind":"types","category":["Doors","Windows"]}},{"tool":"list","args":{"kind":"grids"}}]}`
   Note the exact level names and type names (`"Family: Type"` or the type name).
2. Levels (only the missing ones; NAME_TAKEN means it already exists):
   `create_elements {"op":"level","elevation":3500,"name":"Level 2"}`
   `create_elements {"op":"level","elevation":7000,"name":"Roof"}`
   Plans are created with each level (`plans` default true).
3. Grids. Copies go to the **left** of start->end, so draw the first grid so that left points where the next ones go:
   `create_elements {"op":"grid","start":[0,10000],"end":[0,-2000],"name":"1","count":3,"spacing":6000}` (grids 1, 2, 3 at x = 0, 6000, 12000)
   `create_elements {"op":"grid","start":[-2000,0],"end":[14000,0],"name":"A","count":2,"spacing":8000}` (grids A, B at y = 0, 8000)
4. Exterior walls, one call, closed loop, up to Level 2:
   `create_elements {"op":"wall","level":"Level 1","points":[[0,0],[12000,0],[12000,8000],[0,8000]],"closed":true,"type":"Generic - 200mm","top":"Level 2"}`
   Interior wall:
   `create_elements {"op":"wall","level":"Level 1","start":[6000,0],"end":[6000,8000],"type":"Generic - 200mm","top":"Level 2"}`
   Upper storey: find the Level 1 walls, then copy them to Level 2 in one call:
   `find_elements {"category":["Walls"],"level":"Level 1"}` -> `r4`
   `modify_elements {"op":"copy","from":"r4","levels":["Level 2"]}`
5. Floors:
   `create_elements {"op":"floor","level":"Level 1","points":[[0,0],[12000,0],[12000,8000],[0,8000]],"type":"Generic 300mm"}`
   `create_elements {"op":"floor","level":"Level 2","points":[[0,0],[12000,0],[12000,8000],[0,8000]],"type":"Generic 300mm"}`
6. Roof (footprint, 30 degrees on every edge, 500 mm overhang):
   `create_elements {"op":"roof","level":"Roof","points":[[0,0],[12000,0],[12000,8000],[0,8000]],"slope":30,"overhang":500}`
   Gable: slope only the long edges: `"slope_edges":[0,2]`.
7. Door and windows (hosts are found automatically within 2 m of the point; windows get a sill height with `offset`):
   `place_family {"op":"place","type":"Single-Flush: 0915 x 2134mm","at":[3000,0],"level":"Level 1"}`
   `place_family {"op":"place","type":"Fixed: 0915 x 1220mm","points":[[9000,0],[3000,8000],[9000,8000]],"level":"Level 1","offset":900}`
8. Rooms in every enclosed area, then name them:
   `create_elements {"op":"room","level":"Level 1","all":true}`
   `list {"kind":"rooms","level":"Level 1"}`
   `set_parameters {"rows":[{"id":"<room id>","values":{"Name":"Living","Number":"001"}},{"id":"<room id>","values":{"Name":"Kitchen","Number":"002"}}]}`
9. Verify:
   `capture {"view":"Level 1"}` (plan with walls, doors, rooms)
   `find_elements {"category":["Walls","Floors","Roofs","Doors","Windows"],"group_by":"category"}` (counts)
   `find_elements {"category":["Walls","Floors","Roofs"]}` then `capture {"from":"r5","orient":"iso_se"}` (3D look at the shell)
   `check_model {"check":"warnings"}` (overlaps, rooms not enclosed)

One undo step instead: put steps 4-7 into one change_set. `$0` is the first id that op 0 created:
`change_set {"name":"Ground floor shell","ops":[{"tool":"create_elements","op":"wall","level":"Level 1","points":[[0,0],[12000,0],[12000,8000],[0,8000]],"closed":true,"type":"Generic - 200mm","top":"Level 2"},{"tool":"create_elements","op":"floor","level":"Level 1","points":[[0,0],[12000,0],[12000,8000],[0,8000]]},{"tool":"place_family","op":"place","type":"Single-Flush: 0915 x 2134mm","at":[3000,0],"level":"Level 1","host":"$0"}]}`

Common errors: NOT_FOUND on a type (use the name from step 1), NAME_TAKEN on a level (it exists: use it), REVIT_TRANSACTION_ROLLED_BACK on a floor (the outline crosses itself: check the points), a room "not_enclosed" in `list rooms` (a wall gap: add a wall or `create_elements {"op":"room_separator","view":"Level 1","points":[[6000,0],[6000,8000]]}`).

---

## 3. Views and sheets (create views, sheets, place views, titleblock)

1. What exists:
   `read_many {"calls":[{"tool":"list","args":{"kind":"sheets"}},{"tool":"list","args":{"kind":"views","placed":false,"printable":true}},{"tool":"list","args":{"kind":"title_blocks"}},{"tool":"list","args":{"kind":"view_templates"}}]}`
2. Missing views:
   `edit_views {"op":"create","kind":"floor_plan","level":"Level 2","name":"Level 2 - Furniture"}`
   `edit_views {"op":"create","kind":"section","start":[0,4000],"end":[12000,4000],"depth":5000,"name":"Section A"}` (looks to the left of start->end)
   `edit_views {"op":"create","kind":"elevation","at":[6000,-3000],"direction":"north","level":"Level 1","name":"South Facade"}`
   `edit_views {"op":"create","kind":"3d","from":"r7","orient":"iso_sw","name":"Check 3D"}`
3. View settings:
   `edit_views {"op":"set","view":"Level 1","scale":100,"detail_level":"medium"}`
   TEMPLATE_CONTROLLED: the view template locks that setting. Ask the user whether to change the template or remove it (`"template":"none"`).
4. Sheets, several in one call (titleblock name from step 1):
   `edit_sheets {"op":"create","items":[{"number":"A101","name":"Ground floor"},{"number":"A102","name":"First floor"}],"titleblock":"A1 metric"}`
5. Place views (centred on the titleblock unless `at` is given in sheet mm):
   `edit_sheets {"op":"place","sheet":"A101","view":"Level 1"}`
   `edit_sheets {"op":"place","sheet":"A102","view":"Level 2"}`
   A view can be on only one sheet: UNSUPPORTED_OP. Duplicate it onto the sheet instead:
   `edit_views {"op":"duplicate","view":"Level 1","mode":"dependent","name":"Level 1 - Part A","sheet":"A103"}`
6. Change the titleblock later: `edit_sheets {"op":"set_titleblock","sheets":["A101","A102"],"titleblock":"A1 metric"}`
7. Verify: `capture {"view":"A101"}` and `list {"kind":"sheets"}` (views column).

All in one undo step (`$0.sheet`, `$1.view` are the named outputs):
`change_set {"ops":[{"tool":"edit_sheets","op":"create","number":"A201","name":"Sections"},{"tool":"edit_views","op":"create","kind":"section","start":[0,4000],"end":[12000,4000]},{"tool":"edit_sheets","op":"place","sheet":"$0.sheet","view":"$1.view"}]}`

Revisions: `edit_sheets {"op":"revision_create","description":"Issue 2","date":"2026-10-02","issued_by":"EBA"}` then `edit_sheets {"op":"revision_add","revision":"Issue 2","sheets":["A101","A102"]}`.

---

## 4. Tags and dimensions

1. Tag every door and window visible in the view (already tagged ones are skipped):
   `annotate {"op":"tag_all","view":"Level 1","category":["Doors","Windows"]}`
   Rooms: `annotate {"op":"tag_all","view":"Level 1","category":["Rooms"]}`
   NO_TAG_FAMILY: `list {"kind":"tag_types","category":["Door Tags"]}`; if none, ask the user for a tag .rfa path and `place_family {"op":"load","path":"<path>"}`.
2. Tag specific elements: `annotate {"op":"tag","view":"Level 1","filter":{"category":"Doors","level":"Level 1"}}`
3. Grid dimension string: get grid ids with `list {"kind":"grids"}`, then
   `annotate {"op":"dimension","view":"Level 1","ids":["301001","301002","301003"],"start":[0,-2000],"end":[12000,-2000]}`
   The dimension line runs from start to end; put it outside the building.
4. Wall thickness: `annotate {"op":"dimension","view":"Level 1","ids":["304601"],"mode":"faces","start":[3000,-500],"end":[3000,500]}`
   Precise references: `describe_elements {"ids":["308000"]}` on an existing dimension shows its `refs`; pass `refs` instead of `ids`.
5. Text note: `annotate {"op":"text","view":"Level 1","at":[500,9000],"text":"Existing stair to remain"}`
6. Copy annotation to other levels: `annotate {"op":"copy_to_views","view":"Level 1","views":["Level 2"],"from":"r9"}` (`r9` = the handle the tag_all result returned)
7. Verify: `capture {"view":"Level 1"}`; zoom with `region` if text is small.

---

## 5. Schedules (create, fields, filters, read rows, export CSV, place on a sheet)

1. Fields available for the category: `read_schedule {"category":"Doors","available":true,"rows":false}`
2. Create with fields and sorting:
   `edit_schedules {"op":"create","category":"Doors","name":"Door Schedule","fields":["Mark","Type","Width","Height","Level"],"sort":["Level","Mark"]}`
3. Change it:
   `edit_schedules {"op":"add_fields","schedule":"Door Schedule","fields":["Fire Rating"]}`
   `edit_schedules {"op":"set_filters","schedule":"Door Schedule","filters":["Level = Level 1"]}` (`[]` clears)
   `edit_schedules {"op":"set_sort","schedule":"Door Schedule","sort":["Level","-Mark"],"headers":true}`
   `edit_schedules {"op":"set_field","schedule":"Door Schedule","field":"Mark","heading":"Door No."}`
   `edit_schedules {"op":"set_options","schedule":"Door Schedule","itemize":true,"totals":true}`
4. Read rows (paged, follow `more:`): `read_schedule {"schedule":"Door Schedule","limit":200}`
   An `id` column appears only when Mark is in the schedule and unique; otherwise `data.elementIds` lists the elements.
5. Export: `export {"format":"csv","views":["Door Schedule"]}` (writes to `<home>\exports\<doc>` unless `folder` is given; the result names the file).
6. On a sheet: `edit_sheets {"op":"place","sheet":"A101","view":"Door Schedule","at":[20,580]}` (top-left corner at `at`, sheet mm).
7. Material takeoff: `edit_schedules {"op":"create","category":"Walls","kind":"material_takeoff","name":"Wall Materials","fields":["Material: Name","Material: Area"]}`

Verify: `read_schedule {"schedule":"Door Schedule","limit":20}` shows the fields, filter and rows; `capture {"view":"A101"}` for the sheet.

---

## 6. Family parameters round trip (add param, formula, per-type values, reload)

`family=` edits a loaded family in the background and reloads it into the project in the same call. Project type values are kept unless the call changes type values.

If a `family=` call answers with `warn: PARAM_IGNORED family ...` (or FAMILY_DOC_REQUIRED), this server build does not pass `family` for that op: use the "Long editing session" route at the end of this recipe (open the family, edit with `doc`, `load_into`).

1. Read: `read_family {"family":"Single-Flush"}` (params, formulas, types, values that differ per type).
2. Add a parameter: `edit_family {"op":"add_param","family":"Single-Flush","name":"Leaf Width","data":"length","group":"Dimensions","value":"850"}`
   Yes/no instance parameter: `edit_family {"op":"add_param","family":"Single-Flush","name":"Kick Plate","data":"yesno","kind":"instance","group":"Construction"}`
   Shared parameter: `"shared":"Fire Rating"` (SHARED_PARAM_FILE_MISSING: ask the user to set the shared parameter file).
3. Formula (names are case-sensitive; lengths carry units, e.g. `'900 mm'`):
   `edit_family {"op":"set_formula","family":"Single-Flush","name":"Leaf Width","formula":"Width - 2 * Frame Width"}`
4. Values per type (`types` is required when the family has several types; `["*"]` = all):
   `edit_family {"op":"set_values","family":"Single-Flush","name":"Kick Plate","value":true,"types":["*"]}`
   `edit_family {"op":"set_values","family":"Single-Flush","rows":[{"type":"0915 x 2134mm","values":{"Width":915,"Height":2134}},{"type":"0864 x 2032mm","values":{"Width":864}}]}`
5. New type: `edit_family {"op":"add_type","family":"Single-Flush","name":"1010 x 2250mm","values":{"Width":1010,"Height":2250}}`
6. Verify: `read_family {"family":"Single-Flush"}` again, and in the project `list {"kind":"types","family":"Single-Flush"}`; placed doors: `find_elements {"family":"Single-Flush","fields":["Leaf Width"]}`.

Several edits as one reload: put them in one change_set (consecutive ops on the same family share one edit):
`change_set {"ops":[{"tool":"edit_family","op":"add_param","family":"Single-Flush","name":"Leaf Width","data":"length","group":"Dimensions"},{"tool":"edit_family","op":"set_formula","family":"Single-Flush","name":"Leaf Width","formula":"Width - 100 mm"}]}`

Long editing session on the family doc itself:
1. `edit_family {"op":"open","family":"Single-Flush"}` (returns doc `#3` and pins it)
2. Edit with `"doc":"#3"`, for example `edit_family {"op":"add_param","doc":"#3","name":"Fire Rating","shared":"Fire Rating","kind":"type"}`
3. `edit_family {"op":"load_into","doc":"#3"}` (into the session's project; several projects ask to confirm)
4. Optional file: `edit_family {"op":"save_as","doc":"#3","path":"C:\\Families\\EBA Door.rfa"}`
5. `set_target {"doc":"#1"}` to return to the project.

Errors: NEEDS_TYPES (add `types`), PARAM_HAS_FORMULA (the value comes from a formula), FORMULA_INVALID, NOT_EDITABLE_FAMILY (system or in-place family), PARAM_IN_USE (remove_param while used).

---

## 7. Bulk parameter changes from a query

1. Find the elements and keep the handle:
   `find_elements {"category":["Doors"],"level":"Level 1","where":["Width >= 900"]}` -> `r3`, total N
2. Check the parameter name and that it is writable:
   `describe_elements {"from":"r3","params":"names","match":"Fire"}`
3. Large or risky change: dry run first and show the user:
   `set_parameters {"from":"r3","values":{"Fire Rating":"EI30"},"preview":true}`
4. Apply: `set_parameters {"from":"r3","values":{"Fire Rating":"EI30"}}`
   More than 200 elements returns NOT APPLIED: show the plan, ask, then call its `next:` line.
5. Numbering with templates: `{Param}` copies a value, `{n}` counts 1, 2, 3 in order:
   `set_parameters {"filter":{"category":["Doors"],"level":"Level 1"},"values":{"Mark":"{Level}-D{n}"}}`
6. Different value per element: `set_parameters {"rows":[{"id":"304512","values":{"Mark":"D1"}},{"id":"304513","values":{"Mark":"D2"}}]}`
7. Type parameter (changes every instance of those types): `set_parameters {"from":"r3","on":"type","values":{"Fire Rating":"EI60"}}`
8. Project information: `set_parameters {"ids":["project_info"],"values":{"Project Number":"2026-017"}}`
9. Verify: `find_elements {"from":"r3","fields":["Mark","Fire Rating"]}` and `get_changes {"since":"last"}`.

Change type instead of a value: `modify_elements {"op":"change_type","from":"r3","type":"Single-Flush: 0915 x 2134mm"}`.

---

## 8. View graphics (color by parameter, filters, overrides)

- Color rooms by department (legend in `data.legend`): `view_graphics {"op":"color_by","view":"Level 1","category":["Rooms"],"param":"Department"}`
- Filter + override: `view_graphics {"op":"filter_create","view_filter":"Fire rated doors","categories":["Doors"],"rules":["Fire Rating notempty"],"view":"Level 1","color":"red"}`
- Apply an existing filter to more views: `view_graphics {"op":"filter_apply","view_filter":"Fire rated doors","views":["Level 1","Level 2"],"color":"#FF8800"}`
- Override found elements: `view_graphics {"op":"override","view":"Level 1","from":"r3","color":"#FF0000","fill":true}`
- Permanent hide: `view_graphics {"op":"hide","view":"Level 1","category":["Furniture"]}`. Temporary (no model change): `ui {"op":"hide","category":["Furniture"]}`, end with `ui {"op":"reset"}`.
- Reset: `view_graphics {"op":"reset","view":"Level 1"}`
- Verify: `capture {"view":"Level 1"}`. TEMPLATE_CONTROLLED: the view template controls graphics; ask the user.

---

## 9. MEP runs

1. System types and levels: `read_many {"calls":[{"tool":"list","args":{"kind":"system_types"}},{"tool":"list","args":{"kind":"levels"}},{"tool":"list","args":{"kind":"types","category":["Pipes","Ducts"]}}]}`
2. Pipe run with elbows (points without z sit at level + offset, default 2700):
   `mep {"op":"pipe","level":"Level 1","points":[[0,0],[5000,0],[5000,3000]],"system":"Domestic Cold Water","size":25,"offset":2800}`
   Sloped sanitary: `mep {"op":"pipe","level":"Level 1","points":[[0,0],[8000,0]],"system":"Sanitary","size":100,"offset":-300,"slope":2}`
3. Rectangular duct: `mep {"op":"duct","level":"Level 1","points":[[0,1000],[10000,1000]],"system":"Supply Air","width":400,"height":250}`
4. Connect runs or equipment in order: `mep {"op":"connect","ids":["309001","309002"]}`
5. Systems: `mep {"op":"system_create","ids":["309100","309101"],"kind":"duct","name":"SA-01"}`
6. Insulation: `mep {"op":"insulate","from":"r12","type":"Fibreglass","thickness":25}`
7. Spaces: `mep {"op":"space","level":"Level 1","all":true}`
8. Verify: `capture {"from":"last","orient":"iso_se"}`; `check_model {"check":"clashes","a":["Ducts"],"b":["Structural Framing"],"tolerance":10}`.

warn FITTING_FAILED: the segments exist but an elbow failed; tell the user or `mep connect` the two segments.

---

## 10. Structure

1. Column types: `list {"kind":"types","category":["Structural Columns","Structural Framing","Structural Foundations"]}`
2. Columns at every grid intersection, Level 1 to the next level: `structure {"op":"column","type":"Concrete-Rectangular-Column: 300 x 450mm","level":"Level 1","grids":["*"]}`
3. Beams along a chain: `structure {"op":"beam","type":"W-Wide Flange: W310X38.7","level":"Level 2","points":[[0,0],[6000,0],[12000,0]]}`
   Between columns of a handle: find them (`find_elements {"category":["Structural Columns"],"level":"Level 1"}` -> `r7`), then `structure {"op":"beam","type":"W-Wide Flange: W310X38.7","level":"Level 2","between":["r7"]}`
4. Beam system: `structure {"op":"beam_system","type":"W-Wide Flange: W310X38.7","level":"Level 2","points":[[0,0],[6000,0],[6000,8000],[0,8000]],"spacing":1500}`
5. Footings under the columns: `structure {"op":"foundation","kind":"isolated","type":"Footing-Rectangular: 1800 x 1200 x 450mm","from":"r7"}`
6. Verify: `capture {"from":"last","orient":"iso_se"}`; `find_elements {"category":["Structural Columns","Structural Framing"],"group_by":"level"}`.

---

## 11. Documents and worksharing (open, save as, sync, close, new project)

Only on the user's request. Each of these is called alone (not in change_set) and may run as a job: follow `next: job_status {...}`.

- Open (re-pins to it): `manage_document {"op":"open","path":"C:\\Projects\\Site.rvt"}`. A central model opens as a new local copy; `"central":true` asks to confirm.
- New project from a template (saved to path, activated, pinned): `manage_document {"op":"new_project","path":"C:\\Projects\\Study.rvt","template":"Default-Multi-Discipline_Metric"}`
- Save: `manage_document {"op":"save"}`. Never saved yet: SAVE_AS_REQUIRED -> ask for a path.
- Save as: `manage_document {"op":"save_as","path":"C:\\Temp\\Tower_A_copy.rvt"}` (an existing file asks to confirm; the pin follows the new name, notice SAVED_AS).
- Switch docs: `manage_document {"op":"activate","doc":"#2"}` or `ui {"op":"activate_doc","doc":"#2"}` (both re-pin).
- Close: `manage_document {"op":"close","doc":"#2","save":true}`. Unsaved changes with save false ask to confirm; the only open doc gives CANNOT_CLOSE_ACTIVE.
- Worksharing sync (always asks to confirm; runs as a job; clears Revit's undo history): `worksharing {"op":"sync","comment":"Doors renumbered"}`
- Get others' changes: `worksharing {"op":"reload_latest"}`. NOT_EDITABLE on a write often means this is needed.
- Worksets: `list {"kind":"worksets"}`, `worksharing {"op":"set_active","workset":"Facade"}`, `worksharing {"op":"move_to","workset":"Facade","from":"r3"}`.

Verify with `status {}` (the docs table and the target).

---

## 12. Links

- `list {"kind":"links"}` (loaded or not)
- Link: `links {"op":"link_rvt","path":"C:\\Projects\\Structure.rvt","position":"shared"}`; CAD: `links {"op":"link_cad","path":"C:\\Projects\\Survey.dwg","view":"Level 1","cad_units":"mm"}`
- Reload: `links {"op":"reload","link":"Structure.rvt"}`
- Read inside a link (read-only, host coordinates): `find_elements {"category":["Structural Columns"],"link":"Structure.rvt"}`
- Remove and acquire coordinates always ask to confirm.

---

## 13. Exports (PDF, DWG, IFC)

Exports write files; ask where to put them if the user cares (default `<home>\exports\<doc>`). They run as jobs when several files are written: follow `next: job_status {...}` until it is done. Existing files give NAME_TAKEN unless `"overwrite":true`, which asks to confirm.

- All sheets as one PDF: `export {"format":"pdf","views":["all_sheets"],"combine":true}`
- A sheet set in black and white: `export {"format":"pdf","views":["set:Issue 03"],"name":"{number}_{name}","color":"bw"}`
- DWG per sheet: `export {"format":"dwg","views":["A101","A102"]}` (`"setup":"<DWG export setup>"` optional)
- IFC: `export {"format":"ifc","setup":"IFC4 Reference View"}`
- Schedule CSV: `export {"format":"csv","views":["Door Schedule"]}`
- High-res image: `export {"format":"image","views":["Level 1"],"size":4000}`

Verify: the result (or the finished job) lists the files written. Tell the user the paths. EXPORTER_MISSING: ask the user.

---

## 14. Visual QA loop (capture, change, compare)

1. Before: `capture {"view":"Level 1"}` -> `c1`. The JSON gives `bounds` and `mm_per_px`.
2. Zoom on a detail with model coordinates (same view): `capture {"view":"Level 1","region":[0,0,6000,4000]}`
3. Make the change (any write).
4. After, same view, size and region as the first capture: `capture {"view":"Level 1","compare":"c1"}` -> changed pixels in magenta plus `changed{pixels, bbox_px, bbox_model}`.
5. Elements in 3D: `capture {"from":"last","orient":"iso_se"}`; mark elements red: `capture {"view":"Level 1","highlight":["304601"]}`.
6. Sheets: `capture {"view":"A101","size":"large"}`.

Rules: never screenshot Revit; `capture` changes nothing in the model. CAPTURE_GPU_UNAVAILABLE: repeat with `"style":"hidden"`. Max 2 images per `read_many`.

---

## 15. Undo and revert

1. Undo your own newest write (the doc must be active in Revit; the NEEDS_ACTIVE_DOC fix activates it): `undo {}`; several: `undo {"steps":3}`; redo: `undo {"redo":true}`.
2. UNDO_BLOCKED: the user (or another session) changed something after you. Follow the fix: `undo {"mode":"compensate"}` (deletes what your newest write created, as a new write) when offered; otherwise ask the user to press Ctrl+Z.
3. What changed: `get_changes {"since":"last"}` or `get_changes {"since":"session"}`.
4. Revert by hand when undo is not possible: delete what you created (`modify_elements {"op":"delete","ids":["304601","304602"]}`), or set old values back with `set_parameters` (values from `describe_elements` taken before the change).

Undo history is cleared by Revit after sync and reload_latest.

---

## 16. Dialogs blocking Revit

1. An error REVIT_DIALOG_OPEN means nothing ran. Call `ui {"op":"dialogs"}`.
2. If Cancel or Close is the right answer (the dialog is not about saving or deleting work): `ui {"op":"press","button":"Cancel"}`, then repeat the original call.
3. Any other button returns NOT APPLIED: show the user the dialog title, text and buttons, and ask. Buttons that could discard work (No / Do not save / Yes on save, sync or delete dialogs) give DIALOG_PROTECTED: the user presses them in Revit.

---

## 17. C# on the Revit API (last resort)

Only when no tool can do it, `run_csharp` is in your tool list (the user enabled it), and the user agreed to running code. Never edit settings.json yourself.

1. Read-only first: `run_csharp {"code":"return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();","mode":"read"}`
2. Change as a dry run (default mode, always rolled back): `run_csharp {"code":"var level = Level.Create(doc, Convert.ToDouble(args[\"elevation_mm\"]) / 304.8);\nlevel.Name = (string)args[\"name\"];\nreturn level;","args":{"elevation_mm":9000,"name":"Level 4"},"mode":"dry_run"}`
   Revit internal units are feet: divide mm by 304.8.
3. The dry run is NOT APPLIED with a token. Show the user the reported changes and ask. After a yes: `run_csharp {"mode":"commit","confirm":"K7QM2X"}` (the code is not sent again).
4. The first run in a Revit session shows a consent prompt in Revit; the result says to ask the user to answer it, then `job_status`. CODE_EXECUTION_DECLINED: use tools instead.
5. Verify with normal reads (`find_elements`, `capture`).
