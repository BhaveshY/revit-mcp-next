
### Session, targeting, health

#### `status` (readOnly, idempotent; 920 B)

Start here. Lists running Revit versions and open docs (#1, #2...), the pinned target doc, active view, selection count, levels and busy/dialog state. include= adds sections; detail=full adds health.

| param | type | notes |
|---|---|---|
| include | [views\|selection\|view_elements\|readiness\|context\|warnings\|writes] | Extra sections for the target doc (writes = recent writes by any session). |
| detail | compact\|full | full adds versions, paths, queue and pipe health. |
| instance | string | Only this Revit: year (2024) or process id. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `set_target` (write (non-destructive), idempotent; 631 B)

Pin the doc that every call uses this session: 'active', a # from status, a title or a path. 'follow' tracks the active tab; 'none' unpins. Family docs work too; links are read with find_elements link=.

| param | type | notes |
|---|---|---|
| doc **(required)** | string | 'active', 'follow', 'none', #, title or path. |
| instance | string | Revit year or process id when the same file is open twice. |

### Reads

#### `list` (readOnly, idempotent; 2633 B)

List items of one kind (levels, views, sheets, schedules, rooms, types, families, materials, worksets, phases, links, revisions, filters, templates...) as compact rows. name= filters; for_element= valid types.

| param | type | notes |
|---|---|---|
| kind **(required)** | enum(34) | What to list. |
| name | string | Name contains (case-insensitive, * wildcard). |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| level | string | Level name or id (rooms, areas, spaces, views). |
| view_type | string | For views/view_types: FloorPlan, CeilingPlan, Section, Elevation, ThreeD, Drafting, Legend, AreaPlan, Sheet... |
| family | string | For types: family name. |
| for_element | string | For types: element id; returns only types it can switch to. |
| placed | boolean | Views: on a sheet or not. Rooms/areas: placed or not. |
| graphical | boolean | Views: only graphical views (no schedules or browser-only views). |
| printable | boolean | Views: only views that can be printed or exported. |
| class | string | Types: Revit API class, e.g. WallType, FloorType, FamilySymbol. |
| where | string[] | Conditions 'Param op value' like find_elements. |
| ids | string[] | Only these ids. |
| fields | string[] | Extra columns: location, bbox, host, room, workset, phase, or any parameter name. |
| detail | compact\|full | Default compact. |
| limit | integer | Rows per page, 1-500. Default 50. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `find_elements` (readOnly, idempotent; 2683 B)

Find elements by category, level, type, view, selection, parameter values, box or link. Returns the exact total, compact rows and a handle r# for from= in later calls. count_only/group_by for fast counts.

| param | type | notes |
|---|---|---|
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| class | string | Revit API class, e.g. Wall, FamilyInstance, Floor. |
| level | string[] | Level names or ids (a string is fine). |
| type | string | Type name, 'Family: Type' or id; * wildcard. |
| family | string | Family name; * wildcard. |
| view | string | Only elements visible in this view ('active' ok). |
| from | string | Restrict to 'selection' or an r# handle. |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| where | string[] | Conditions 'Param op value', op: = != > >= < <= contains startswith empty notempty. Lengths mm. |
| box | point/box | Intersects box [x0,y0,x1,y1] (plan) or [x0,y0,z0,x1,y1,z1] mm. |
| link | string | Search inside this linked model (name or id); read-only, host coordinates. |
| workset | string[] | Workset names. |
| phase | string | Phase created name. |
| design_option | string[] | Design option names, or 'main'. |
| hidden | boolean | With view=: include elements hidden in the view. |
| types | boolean | Find element types instead of instances. |
| fields | string[] | Extra columns: location, bbox, host, room, workset, phase, or any parameter name. |
| group_by | string | Count per category, type, family, level, workset or a parameter name. |
| count_only | boolean | Only the total (and group counts). |
| detail | compact\|full | Default compact. |
| limit | integer | Rows per page, 1-500. Default 50. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `describe_elements` (readOnly, idempotent; 1305 B)

Details for up to 50 elements: parameters with values, units and writability, type parameters, geometry, host/hosted, room, joins, group, MEP connectors; dimensions show witness refs. Use before set_parameters.

| param | type | notes |
|---|---|---|
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| params | writable\|all\|names\|none | Instance parameters: writable (default) values, all values, names only, none. |
| include | [type\|geometry\|relations\|connectors] | Extra blocks. type = type parameters. |
| match | string | Only parameters whose name contains this. |
| link | string | Elements are inside this linked model. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `get_view` (readOnly, idempotent; 720 B)

One view or sheet: type, scale, detail level, template, crop, view range, phase, discipline; include= overrides, filters, hidden categories, viewports, revisions. Default: the active view.

| param | type | notes |
|---|---|---|
| view | string | View name, id, sheet number, or 'active' (default). |
| include | [overrides\|filters\|categories\|viewports\|revisions] | Extra blocks. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `read_schedule` (readOnly, idempotent; 1166 B)

Read a schedule as a table (paged) with its fields, filters and sorting. category= without schedule= lists the fields available for a new schedule of that category.

| param | type | notes |
|---|---|---|
| schedule | string | Schedule name or id. |
| category | string | Category for available fields before edit_schedules create. |
| rows | boolean | Include body rows. Default true. |
| available | boolean | Include fields that could be added. Default false. |
| name | string | available: field name contains. |
| key | string | Field unique per row that maps rows to element ids. Default Mark; no id column if not unique. |
| limit | integer | Rows per page, 1-1000. Default 100. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `read_family` (readOnly, idempotent; 896 B)

Family parameters (name, type/instance, group, data type, formula, shared), family types with per-type values, category and nested families. family= loaded family, path= an .rfa file, or doc= an open family doc.

| param | type | notes |
|---|---|---|
| family | string | Loaded family name or id (read without opening the UI). |
| path | string | Path to an .rfa file (opened hidden, read-only). |
| types | string[] | Only these family types' values. Default all (up to 30). |
| detail | compact\|full | Default compact. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `check_model` (readOnly, idempotent; 1758 B)

Model health: stats (counts by category/level), warnings (grouped, element ids), readiness for common tasks, purgeable (unused items), clashes (set a vs b, links ok). Default check=stats.

| param | type | notes |
|---|---|---|
| check | stats\|warnings\|readiness\|purgeable\|clashes | What to check. Default stats. |
| a | string[] | Clashes: categories or an r# handle. |
| b | string[] | Clashes: categories or an r# handle. Default = a. |
| link | string | Clashes: set b is inside this linked model. |
| tolerance | number | Clashes: ignore overlaps smaller than this, mm. Default 0. |
| match | string | Warnings: text contains. |
| severity | warning\|error | Warnings: only this severity. |
| ids | string[] | Warnings involving these elements. |
| group_by | string | Stats: category, level, type, workset. |
| scenarios | string[] | Readiness: walls, floors, rooms, families, sheets, tags... Default common set. |
| detail | compact\|full | Default compact. |
| limit | integer | Rows per page, 1-500. Default 50. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `get_quantities` (readOnly, idempotent; 1283 B)

Quantity takeoff: count, length (m), area (m2), volume (m3) summed by material, type, level, category or family, for all or filtered elements. Default by=material.

| param | type | notes |
|---|---|---|
| by | material\|type\|level\|category\|family | Group rows by. Default material. |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| level | string | Level name or id. |
| view | string | Only elements visible in this view. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| material | string | Material name contains. |
| paint | boolean | Include painted areas. Default false. |
| limit | integer | Rows per page, 1-500. Default 50. |
| page | string | Page token from a 'more:' line, e.g. p2. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `get_changes` (readOnly, idempotent; 655 B)

What changed since a point: added, modified and deleted elements (id, category) and transaction names, by you or the user. since='session' (default), 'last' (your last write) or a mark m#.

| param | type | notes |
|---|---|---|
| since | string | 'session', 'last', or a mark like m12 from an earlier result. |
| limit | integer | Rows per page, 1-500. Default 50. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `read_many` (readOnly, idempotent; 823 B)

Run up to 8 read calls in one round trip, sharing one time budget (max 2 images): calls=[{tool:'list',args:{kind:'levels'}},{tool:'capture',args:{}}]. One failure does not stop the rest.

| param | type | notes |
|---|---|---|
| calls **(required)** | object[] | Up to 8 {tool, args}; args as for that tool. |

### Visual

#### `capture` (readOnly; 1845 B)

See the model: returns an image of a view or sheet, or of elements (ids/from) in a temporary 3D box. region= zooms; size small|medium|large; compare= c# shows what changed. Nothing is kept in the model.

| param | type | notes |
|---|---|---|
| view | string | View name, id, sheet number, or 'active' (default). |
| ids | string[] | Focus these elements: temp 3D view boxed around them. |
| from | string | Focus an element set: r# handle, 'selection' or 'last'. |
| region | point/box | Zoom to [x0,y0,x1,y1] mm (model coords; sheet mm on sheets). |
| orient | enum(9) | 3D direction. Default iso_se. |
| style | wireframe\|hidden\|shaded\|consistent\|realistic | Default: view's own; temp 3D shaded. |
| size | small\|medium\|large | Long edge at most 768, 1280 or 1568 px. Default medium. |
| margin | number | Space around focused elements, mm. Default 1000. |
| highlight | string[] | Tint these element ids red in the image. |
| annotations | boolean | Show annotations. Default true for views, false for focus. |
| compare | string | Earlier capture id (c#): marks changed pixels. |
| format | auto\|png\|jpg | Default auto (png drawings, jpg shaded). |
| doc | string | Doc title, path or # from status. Default: pinned target. |

### UI control

#### `ui` (write (non-destructive), idempotent; 2477 B)

Drive the Revit window without changing the model: select, zoom_to, temporary isolate/hide, reset, activate_view, activate_doc, list/close open views, list dialogs and press a dialog button.

| param | type | notes |
|---|---|---|
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| view | string | View name, id, sheet number, or 'active'. |
| views | string[] | close_views: names/ids, or ['all_but_active']. |
| region | point/box | [x0,y0,x1,y1] mm. |
| fit | boolean | zoom_to: zoom to fit the whole view. |
| mode | replace\|add\|remove | select: default replace. |
| zoom | boolean | select: also zoom to the selection. |
| dialog | string | Dialog id from op=dialogs. Default: the open one. |
| button | string | Button text or id. Cancel/Close apply at once; other buttons need the user's OK (confirm). |
| instance | string | dialogs/press: Revit year or process id. Default: every Revit. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
select(sel;mode,zoom)
zoom_to(ids/from/region/fit;view)
isolate(ids/from/category;view)
hide(ids/from/category;view)
reset(;view)
activate_view(view)
activate_doc(doc)
open_views()
close_views(views)
dialogs(;instance)
press(button;dialog,instance,confirm)
```

### Domain write tools

#### `create_elements` (write (non-destructive); 5992 B)

Create levels, grids, walls, floors, roofs, ceilings, rooms, areas, separators, openings, shafts, ref planes, model lines, stairs, railings, curtain grids, toposolids. mm; applies now, returns ids and undo.

| param | type | notes |
|---|---|---|
| level | string | Base level name or id. |
| top | string | Top level (wall top constraint, shaft, stairs). |
| elevation | number | Level elevation mm. |
| name | string | Name. |
| number | string | Room/area number. |
| department | string | Room department. |
| plans | boolean | level: also create floor and ceiling plans. Default true. |
| count | integer | level/grid: how many to create. Default 1. |
| spacing | number | level/grid: distance between copies mm; curtain_grid: grid spacing. |
| start | point/box | Start point [x,y(,z)] mm. |
| end | point/box | End point [x,y(,z)] mm. |
| through | point/box | Point on the arc: makes an arc (single segment). |
| points | points | [[x,y],...] mm: wall path, outline, stair path; toposolid [x,y,z] (z above level if set, else absolute). |
| profile | points | roof_extrusion: profile [[s,z],...] mm; s along start->end, z above level. |
| holes | loops | Inner loops [[[x,y],...],...]. |
| closed | boolean | Close the loop back to the first point. |
| from | string | floor/ceiling: follow room boundaries: r# handle or 'selection' of rooms. |
| type | string | Type: 'Family: Type', type name, or id. |
| height | number | wall: unconnected height mm when no top (default 3000); opening: opening height mm. |
| offset | number | Height above level mm (wall opening: sill height). Default 0. |
| top_offset | number | Offset from top level mm. |
| line | center\|core_center\|finish_ext\|finish_int\|core_ext\|core_int | wall location line. Default center. |
| structural | boolean | Structural wall/floor. |
| flip | boolean | wall: flip orientation. |
| slope | number | Degrees (floor, roof). |
| slope_edges | integer[] | roof: edge indexes that slope. Default all when slope set. |
| overhang | number | roof: overhang mm. |
| depth | number | roof_extrusion: extrusion length mm (left of start->end). |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| all | boolean | room: place rooms in every enclosed area of the level. |
| allow_duplicate | boolean | room: allow a duplicate number. |
| view | string | Plan view (separators, areas, ref plane). |
| host | string | Host id: wall/floor/roof/ceiling (opening), stairs (railing), curtain wall (curtain_grid). |
| width | number | stairs: run width mm. |
| risers | integer | stairs: riser count. Default from height. |
| direction | vertical\|horizontal | curtain_grid: direction of the new grid lines. |
| positions | number[] | curtain_grid: offsets mm from the wall start (vertical) or base (horizontal). |
| mullion | string | curtain_grid: mullion type to add on new lines. |
| line_style | string | Line style name. |
| survey | points | toposolid: extra [[x,y,z]] points inside the outline. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
level(elevation;name,plans,count,spacing)
grid(start,end;through,name,count,spacing)
wall(level,points/start+end;type,height,top,offset,top_offset,line,structural,flip,closed,through)
floor(level,points/from;type,holes,offset,slope,structural)
roof(level,points;type,slope,slope_edges,overhang,offset)
roof_extrusion(level,start,end,profile,depth;type)
ceiling(level,points/from;type,holes,offset)
room(level,at/all;name,number,department,allow_duplicate)
room_separator(view,points;closed)
area(view,at;name,number)
area_boundary(view,points;closed)
opening(host,points/start+end;offset,height)
shaft(level,top,points;offset,top_offset)
ref_plane(start,end;name,view)
model_line(points;level,line_style,closed,through)
stairs(level,top,points;type,width,risers)
railing(points/host;level,type)
curtain_grid(host,direction,positions/spacing;mullion)
toposolid(points;type,level,survey)
```

#### `place_family` (write (non-destructive); 2793 B)

Place instances of any loadable family (doors, windows, furniture, fixtures, equipment, generic, detail-free) at points, on hosts or along lines; load .rfa families. Hosts are found automatically when omitted.

| param | type | notes |
|---|---|---|
| type | string | Family type: 'Family: Type', type name, or id. |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| points | points | Several insertion points [[x,y(,z)],...]: one instance each. |
| start | point/box | Start point [x,y(,z)] mm. |
| end | point/box | End point [x,y(,z)] mm. |
| level | string | Level for z; z of points is height above it. |
| host | string | Host id (wall, floor, ceiling, roof, face). 'auto' (default) finds the nearest valid host. |
| offset | number | Height above level, mm. Default 0. |
| rotation | number | Degrees, counterclockwise. Default 0. |
| flip_hand | boolean | Flip hand. |
| flip_facing | boolean | Flip facing. |
| allow_pinned | boolean | Allow a pinned host. |
| path | string | load: .rfa path visible to Revit. |
| symbols | string[] | load: only these types. Default all. |
| overwrite | boolean | load: overwrite existing parameter values. Default false. |
| categories | string[] | load: refuse unless the family is one of these categories. |
| sha256 | string | load: refuse unless the file hash matches. |
| allow_network | boolean | load: allow UNC/network paths. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
place(type,at/points/start+end;level,host,offset,rotation,flip_hand,flip_facing,allow_pinned)
load(path;symbols,overwrite,categories,sha256,allow_network)
```

#### `modify_elements` (destructive; 4615 B)

Edit existing elements chosen by ids, from= or filter=: move, copy, rotate, mirror, array, align, delete, pin, change_type, join/cut/attach, flip, split, reshape, group/ungroup. Deleting >20 asks to confirm.

| param | type | notes |
|---|---|---|
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| by | point/box | Offset vector [dx,dy(,dz)] mm. |
| to | point/box | Move/copy so the element's location point lands here [x,y(,z)] mm. |
| levels | string[] | copy: copy to these levels, aligned (vertical offset only). |
| count | integer | copy/array: number of copies (array includes original). |
| angle | number | Degrees counterclockwise (rotate, radial array). |
| center | point/box | Rotation/radial center [x,y(,z)]. Default: element center. |
| axis | point/box | Rotation axis direction. Default [0,0,1]. |
| start | point/box | mirror: axis start; reshape: new curve start. |
| end | point/box | mirror: axis end; reshape: new curve end. |
| points | points | reshape: new path for curve elements. |
| through | point/box | reshape: point on arc. |
| keep | boolean | mirror: keep original (mirror a copy). Default true. |
| kind | linear\|radial | array: default linear. |
| target | string | align: grid, level, ref plane or element id to align to. |
| lock | boolean | align: lock the alignment. |
| with | string | join/cut/attach: the other element id or r# handle. |
| side | top\|base\|hand\|facing\|wall | attach/detach: top\|base; flip: hand\|facing\|wall. |
| at | point/box | split: split point; place_group: insertion point. |
| type | string | Type: 'Family: Type', type name, or id. |
| pinned | boolean | pin: true pins (default), false unpins. |
| allow_pinned | boolean | Allow changing pinned elements. |
| expect_count | integer | delete: abort unless exactly this many elements (incl. dependents) would be deleted. |
| expect_ids | string[] | delete: abort unless the delete set (incl. dependents) is exactly these ids. |
| name | string | group/place_group: group name. |
| level | string | place_group: level. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
move(sel,by/to)
copy(sel,by/to/levels;count)
rotate(sel,angle;center,axis)
mirror(sel,start,end;keep)
array(sel,count,by/angle;center,kind)
align(ids,target;lock)
delete(sel;allow_pinned,expect_count,expect_ids)
pin(sel;pinned)
change_type(sel,type)
join(ids,with)
unjoin(ids,with)
switch_join(ids,with)
cut(ids,with)
uncut(ids,with)
attach(ids,with;side)
detach(ids;with,side)
flip(sel,side)
split(ids,at)
reshape(ids,start+end/points;through)
group(sel;name)
ungroup(ids/from)
place_group(name,at;level)
```

#### `set_parameters` (write (non-destructive); 1567 B)

Set parameter values on elements, their types, views, sheets or project info: values={Mark:'D1',Width:900} for every target or rows=[{id,values}] each. Lengths mm, areas m2, angles deg; '{Param}' copies values.

| param | type | notes |
|---|---|---|
| ids | string[] | Element, type, view or sheet ids; ['project_info'] for project info. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| values | object | {param name: value}. Text may use '{Other Param}' and '{n}' (1,2,3...). |
| rows | object[] | Per element: [{id, values:{...}}]. |
| on | instance\|type | type = set on the targets' types. Default instance. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

#### `edit_types` (destructive; 2184 B)

Types and materials: duplicate, rename or delete a type; set wall/floor/roof/ceiling layers; default type per category; create or edit materials (color, transparency, patterns); rename a family.

| param | type | notes |
|---|---|---|
| type | string | Type: 'Family: Type', type name, or id. |
| name | string | New name. |
| category | string | set_default: category. |
| family | string | Family name. |
| material | string | Material name (material_create: copy from this one). |
| layers | object[] | [{function:structure\|substrate\|thermal\|finish1\|finish2\|membrane, material, thickness}] exterior first. |
| color | string | Color: #RRGGBB, 'r,g,b' or a name like red. |
| transparency | integer | 0-100. |
| surface_pattern | string | Fill pattern name for surfaces. |
| cut_pattern | string | Fill pattern name for cut. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
duplicate_type(type,name)
rename_type(type,name)
delete_type(type)
set_layers(type,layers)
set_default(category,type)
material_create(name;color,transparency,surface_pattern,cut_pattern,material)
material_edit(material;name,color,transparency,surface_pattern,cut_pattern)
rename_family(family,name)
```

#### `edit_views` (destructive; 4848 B)

Create views (floor, ceiling, structural, area plan, section, elevation, 3D, callout, drafting, legend copy), duplicate, rename, delete, templates; set scale, detail, crop, view range, phase, 3D orientation, section box.

| param | type | notes |
|---|---|---|
| kind | enum(10) | create: view kind. |
| view | string | View name or id (callout/legend: the parent/source view). |
| views | string[] | View/sheet names or ids. |
| level | string | Level name or id. |
| name | string | Name. |
| view_type | string | View family type name. Default: first of the kind. |
| template | string | View template name; 'none' removes it. |
| scale | integer | Scale denominator: 100 = 1:100. |
| detail_level | coarse\|medium\|fine | Detail level. |
| style | wireframe\|hidden\|shaded\|consistent\|realistic | Visual style. |
| discipline | architectural\|structural\|mechanical\|electrical\|plumbing\|coordination | View discipline. |
| phase | string | Phase name. |
| phase_filter | string | Phase filter name. |
| crop | boolean | Crop view on/off. |
| crop_box | point/box | Crop [x0,y0,x1,y1] mm (plan) or [x0,y0,z0,x1,y1,z1]. |
| crop_visible | boolean | Show crop region. |
| view_range | object | Plans: {top,cut,bottom,depth} mm above the view level; or {cut:1200}. |
| far_clip | number | Sections/elevations: far clip depth mm. |
| scope_box | string | Scope box name; 'none' removes. |
| orient | enum(9) | 3D direction. |
| section_box | point/box | 3D: [x0,y0,z0,x1,y1,z1] mm; [] turns it off. |
| ids | string[] | create 3d/section: fit the box around these elements. |
| from | string | Like ids: r# handle or 'selection'. |
| start | point/box | section: cut line start [x,y]. |
| end | point/box | section: cut line end [x,y] (looks left of start->end). |
| depth | number | section: view depth mm. Default 3000. |
| height | number | section: height mm above level. Default level-to-level. |
| at | point/box | elevation: marker point [x,y]; duplicate: viewport center on sheet. |
| direction | north\|south\|east\|west | elevation: looking direction. |
| region | point/box | callout: [x0,y0,x1,y1] mm in the parent view. |
| area_scheme | string | area_plan: area scheme name. |
| perspective | boolean | 3d: perspective camera. Default false (isometric). |
| mode | copy\|detailing\|dependent | duplicate: default copy. |
| sheet | string | duplicate: also place the copy on this sheet. |
| underlay | string | Underlay level name; 'none'. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
create(kind;level,name,view_type,template,scale,start,end,depth,height,at,direction,orient,section_box,ids,from,region,view,area_scheme,perspective)
duplicate(view;mode,name,sheet,at)
rename(view,name)
set(view/views;scale,detail_level,style,discipline,phase,phase_filter,template,crop,crop_box,crop_visible,view_range,far_clip,scope_box,orient,section_box,underlay)
create_template(view,name)
delete(views)
```

#### `view_graphics` (write (non-destructive); 3144 B)

Change how a view shows things: override color, lines, pattern, transparency, halftone for elements, categories or filters; permanent hide/unhide; view filters with rules; color by parameter; reset.

| param | type | notes |
|---|---|---|
| view | string | View name/id or 'active'; a template name also works. |
| views | string[] | View/sheet names or ids. |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| view_filter | string | Revit view filter name. |
| categories | string[] | filter_create/edit: categories the filter applies to. |
| rules | string[] | Filter rules 'Param op value' (ANDed), op: = != > >= < <= contains startswith. |
| param | string | color_by: parameter whose values get distinct colors. |
| color | string | Color: #RRGGBB, 'r,g,b' or a name like red. |
| fill | boolean | Solid surface fill in color. Default true for elements, false for filters. |
| line_weight | integer | 1-16. |
| fill_pattern | string | Fill pattern name. Default Solid fill. |
| transparency | integer | 0-100. |
| halftone | boolean | Halftone. |
| visible | boolean | filter_apply: filtered elements visible. Default true. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
override(view,sel/category/view_filter;color,fill,line_weight,fill_pattern,transparency,halftone)
reset(view;ids/from/category/view_filter)
hide(view,ids/from/category)
unhide(view,ids/from/category)
color_by(view,category,param)
filter_create(view_filter,categories,rules;view,visible,color,fill,transparency,halftone)
filter_edit(view_filter;categories,rules)
filter_apply(view_filter,view/views;visible,color,fill,line_weight,fill_pattern,transparency,halftone)
filter_remove(view_filter,view/views)
```

#### `edit_sheets` (destructive; 3042 B)

Sheets: create (one or many), duplicate, renumber/rename, set titleblock; place, move or remove views, schedules and legends; viewport type; revisions create/edit and add/remove on sheets. Sheet mm from origin.

| param | type | notes |
|---|---|---|
| sheet | string | Sheet number, name or id. |
| sheets | string[] | Several sheets (numbers/ids). |
| number | string | Sheet number (duplicate/rename: the new number). |
| name | string | Name. |
| items | object[] | create many: [{number,name}]. |
| titleblock | string | Titleblock type. Default: first loaded. |
| mode | empty\|with_views\|with_detailing | duplicate: default with_views. |
| prefix | string | duplicate: prefix for copied view names. |
| view | string | View, schedule or legend name/id (placed or to place). |
| at | point/box | Viewport center [x,y] mm on the sheet. Default: sheet center. |
| by | point/box | move_viewport: shift [dx,dy] mm. |
| type | string | Viewport type name. |
| revision | string | Revision description, number or id. |
| description | string | Revision description. |
| date | string | Revision date text. |
| issued_by | string | Issued by. |
| issued_to | string | Issued to. |
| issued | boolean | Mark revision issued. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
create(number/items;name,titleblock)
duplicate(sheet,number;name,mode,prefix)
rename(sheet;number,name)
set_titleblock(sheet/sheets,titleblock)
place(sheet,view;at,type)
move_viewport(sheet,view,at/by)
remove_viewport(sheet,view)
set_viewport_type(sheet,view,type)
revision_create(description;date,issued_by,issued_to)
revision_edit(revision;description,date,issued_by,issued_to,issued)
revision_add(revision,sheet/sheets)
revision_remove(revision,sheet/sheets)
```

#### `annotate` (write (non-destructive); 4517 B)

Annotation in a view: tag elements or tag_all by category, text notes, dimensions (refs, grids, walls), spot elevations/coordinates, detail lines, filled regions, detail items/symbols, revision clouds, copy to views.

| param | type | notes |
|---|---|---|
| view | string | View name/id or 'active'. |
| views | string[] | copy_to_views: target views. |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| category | string[] | Categories, e.g. ["Walls","Doors"] (a string is fine). |
| type | string | Tag/text/dimension/region/detail type. Default: category default. |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| start | point/box | Dimension line start / line-based item start. |
| end | point/box | Dimension line end / line-based item end. |
| through | point/box | Point on arc. |
| points | points | Line/loop points [[x,y],...] mm. |
| holes | loops | filled_region inner loops. |
| text | string | Text note content. |
| width | number | Text wrap width mm (paper). |
| rotation | number | Degrees, counterclockwise. Default 0. |
| leader | boolean | Add a leader. |
| orientation | horizontal\|vertical\|model | Tag orientation. |
| untagged_only | boolean | tag_all: skip elements already tagged. Default true. |
| offset | point/box | tag_all: tag head offset [dx,dy] mm from element center. |
| refs | string[] | dimension: stable reference strings from describe_elements. |
| mode | centers\|faces\|exterior\|core | dimension with ids: which references. Default centers. |
| overrides | object[] | Dimension text: [{segment,value,prefix,suffix,above,below}]. |
| by | point/box | Offset vector [dx,dy(,dz)] mm. |
| kind | elevation\|coordinate\|slope | spot kind. |
| host | string | spot: element id whose face is picked at 'at'. |
| revision | string | Revision description or number. |
| line_style | string | Line style name. |
| closed | boolean | Close the loop. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
tag(view,sel;type,at,leader,orientation)
tag_all(view,category;type,untagged_only,leader,offset)
text(view,at,text;type,width,rotation,leader)
dimension(view,refs/ids,start,end;type,mode,overrides)
dimension_edit(ids;type,by,overrides,refs,start,end)
spot(view,kind,host,at;type,leader)
detail_line(view,points;line_style,closed,through)
filled_region(view,points;type,holes,line_style)
detail_item(view,type,at/start+end;rotation)
revision_cloud(view,points,revision)
copy_to_views(view,views,ids/from)
```

#### `edit_schedules` (write (non-destructive); 2376 B)

Create schedules (regular, material takeoff, key) with fields, filters, sorting; add, remove, order, rename or hide fields; set filters, sort/group, itemize, totals; duplicate. Read rows with read_schedule.

| param | type | notes |
|---|---|---|
| schedule | string | Schedule name or id. |
| category | string | create: category, e.g. Doors. |
| name | string | Name. |
| kind | regular\|material_takeoff\|key | create: default regular. |
| fields | string[] | Field names, e.g. ['Mark','Type','Width']. |
| field | string | One field name. |
| heading | string | Column heading. |
| hidden | boolean | Hide the column. |
| filters | string[] | ['Level = Level 1', 'Width > 900'] (max 8, ANDed); [] clears. |
| sort | string[] | Sort/group fields in order; prefix '-' for descending. |
| headers | boolean | set_sort: group headers. Default false. |
| itemize | boolean | Itemize every instance. Default true. |
| totals | boolean | Grand totals (set_field: column total). |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
create(category;name,kind,fields,filters,sort,itemize,totals)
add_fields(schedule,fields)
remove_fields(schedule,fields)
set_field(schedule,field;heading,hidden,totals)
order_fields(schedule,fields)
set_filters(schedule,filters)
set_sort(schedule,sort;headers,totals)
set_options(schedule;name,itemize,totals)
duplicate(schedule,name)
```

#### `edit_family` (destructive; 3414 B)

Edit a family's parameters and types: add/remove/rename params, formulas, values per type, add/rename/delete types. family= a loaded family (edited, then reloaded) or doc= an open .rfa; save/save_as.

| param | type | notes |
|---|---|---|
| family | string | Loaded family: edited in the background and reloaded. open: keep it open as doc #n. |
| name | string | Parameter name (type ops: the type name). |
| new_name | string | New name. |
| kind | type\|instance | Default type. |
| data | string | Data type: text, length, area, volume, angle, number, integer, yesno, material, url, image, or family_type:<Category>. |
| group | string | Properties group, e.g. Dimensions, Identity Data, Constraints. Default Other. |
| shared | string | Shared parameter name or GUID (from the shared parameter file). |
| reporting | boolean | Reporting parameter. |
| formula | string | Formula, e.g. 'Width / 2'; '' clears. |
| value | string | Value (lengths mm; '900' ok). |
| values | object | add_type: {param: value}. |
| types | string[] | set_values: family types to change; ['*'] = all. Required when >1 type. |
| rows | object[] | set_values: [{type, values:{param: value}}]. |
| copy_from | string | add_type: copy values from this type. |
| category | string | set_category: family category. |
| reload | boolean | With family=: reload into the project after the edit. Default true. |
| overwrite | boolean | On reload: overwrite project type parameter values. Default: only if this call changed type values. |
| into | string[] | load_into: open projects to load into. Default: the pinned project only; more than one asks to confirm. |
| path | string | save_as: .rfa path. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
open(family)
add_param(name;kind,data,group,shared,formula,value,reporting)
remove_param(name)
rename_param(name,new_name)
set_param(name;kind,group,reporting)
set_formula(name,formula)
set_values(name+value/rows;types)
add_type(name;copy_from,values)
rename_type(name,new_name)
delete_type(name)
set_category(category)
load_into(;into,overwrite)
save()
save_as(path)
```

#### `mep` (write (non-destructive); 3181 B)

MEP: pipes, ducts, conduits, cable trays and flex runs along points with automatic elbows; connect elements; create/edit piping, duct and electrical systems, circuits; insulation; spaces and zones.

| param | type | notes |
|---|---|---|
| points | points | Run path [[x,y],...] or [[x,y,z],...] (z above level). |
| level | string | Level name or id. |
| offset | number | Run centerline height above level mm. Default 2700. |
| type | string | Pipe/duct/conduit/tray/insulation type name. |
| system | string | System type (e.g. Domestic Cold Water, Supply Air) or system name. |
| size | number | Diameter mm. |
| width | number | Duct/tray width mm. |
| height | number | Duct/tray height mm. |
| slope | number | Pipes: slope in percent. Default 0. |
| fittings | boolean | Add elbows/tees at bends. Default true. |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| kind | piping\|duct\|electrical | system_create kind. |
| name | string | Name. |
| number | string | Space number. |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| all | boolean | space: in every enclosed area of the level. |
| thickness | number | insulate: mm. |
| panel | string | circuit: panel name or id. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
pipe(points,level;type,system,size,offset,slope,fittings)
duct(points,level;type,system,size/width+height,offset,fittings)
conduit(points,level;type,size,offset,fittings)
cable_tray(points,level;type,width,height,offset,fittings)
flex_pipe(points,level;type,system,size,offset)
flex_duct(points,level;type,system,size,offset)
connect(ids)
system_create(ids,kind;name,type)
system_add(system,ids)
system_remove(system,ids)
circuit(ids;panel)
insulate(ids/from,type,thickness)
space(level,at/all;name,number)
zone(name,ids;level)
```

#### `structure` (write (non-destructive); 2879 B)

Structure: columns at points or grid intersections, beams along lines or between columns, braces, beam systems, isolated/wall/slab foundations. type='Family: Type'; lengths mm.

| param | type | notes |
|---|---|---|
| type | string | Type: 'Family: Type', type name, or id. |
| level | string | Base or reference level. |
| top | string | column: top level. Default next level up. |
| at | point/box | Point [x,y] or [x,y,z] mm. |
| points | points | Several points (columns) or a chain (beams, beam_system outline). |
| grids | string[] | column: at intersections of these grids; ['*'] = all. |
| between | string[] | beam: column ids to connect in order. |
| start | number[] | Beam/brace or slanted column start [x,y,z]. |
| end | number[] | Beam/brace or slanted column end [x,y,z]. |
| offset | number | Offset from level mm (beam z, column base). |
| top_offset | number | column: top offset mm. |
| rotation | number | Degrees, counterclockwise. Default 0. |
| spacing | number | beam_system: spacing mm. |
| direction | number | beam_system: beam direction degrees. Default 0 (x axis). |
| kind | isolated\|wall\|slab | foundation kind. |
| ids | string[] | foundation: columns (isolated) or walls (wall). |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
column(type,level,at/points/grids;top,offset,top_offset,rotation,start,end)
beam(type,level,start+end/points/between;offset)
brace(type,level,start,end)
beam_system(type,level,points;spacing,direction)
foundation(kind,type;ids/from,points,level)
```

#### `manage_document` (destructive, openWorld; 2975 B)

Files: open (rvt/rfa; detach, audit, worksets), activate, save, save_as, close, new project from template, new family from .rft, purge unused, project units, base/survey point. Risky ones ask to confirm.

| param | type | notes |
|---|---|---|
| path | string | File path (.rvt, .rfa). |
| template | string | Template .rte/.rft path or name. new_project default: settings template. |
| activate | boolean | open/new_project: show it in the UI. Default true. |
| local | string | open (central model): path of the new local copy. Default Documents\<name>_<user>.rvt. |
| central | boolean | open: open the central file itself instead of a local copy (asks to confirm). |
| detach | no\|preserve\|discard | open: detach from central. Default no. |
| audit | boolean | open: audit. |
| worksets | all\|none\|last\|editable | open: worksets to open. Default last. |
| overwrite | boolean | Replace an existing file. |
| as_central | boolean | save_as: save as central model. |
| compact | boolean | Compact the file. |
| save | boolean | close: save first. Default false (unsaved changes ask to confirm). |
| passes | integer | purge: repeat passes 1-3. Default 3. |
| unit | mm\|cm\|m\|in\|ft | set_units: project length unit. |
| accuracy | number | set_units: rounding, e.g. 1 or 0.1. |
| base_point | point/box | coordinates: project base point [x,y,z] mm. |
| survey_point | point/box | coordinates: survey point [x,y,z] mm. |
| true_north | number | coordinates: true north angle degrees. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
open(path;activate,detach,audit,worksets,local,central)
activate(doc)
save(;compact)
save_as(path;overwrite,as_central,compact)
close(doc;save)
new_project(path;template,overwrite,activate)
new_family(template,path)
purge(;passes)
set_units(unit;accuracy)
coordinates(base_point/survey_point/true_north)
```

#### `worksharing` (destructive, openWorld; 1873 B)

Workshared models: sync with central (comment, relinquish), reload latest, relinquish all, create/rename worksets, set active workset, move elements to a workset, borrow elements, enable worksharing.

| param | type | notes |
|---|---|---|
| comment | string | sync: comment. |
| relinquish | boolean | sync: relinquish everything after. Default true. |
| compact | boolean | sync: compact central. |
| workset | string | Workset name. |
| name | string | New workset name (enable: name for the default workset). |
| ids | string[] | Element ids (numbers ok) or UniqueIds. |
| from | string | Element set: r# handle from find_elements, 'selection', or 'last' (your last write). |
| filter | object | Select like find_elements: {category,level,type,family,view,where:["Mark = D1"]}. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

`sel` = one of `ids`, `from`, `filter`.

```
sync(;comment,relinquish,compact)
reload_latest()
relinquish()
workset_create(name)
workset_rename(workset,name)
set_active(workset)
move_to(workset,sel)
borrow(sel)
enable(;name)
```

#### `links` (destructive, openWorld; 1683 B)

Linked models and CAD: link RVT/IFC/DWG (origin, center or shared coordinates), import CAD, reload, reload from a new path, unload, remove, acquire coordinates. Read link contents via find_elements link=.

| param | type | notes |
|---|---|---|
| path | string | File path. |
| link | string | Link name or id. |
| position | origin\|center\|shared\|base_point | Placement. Default origin. |
| view | string | CAD: view to place in. Default active. |
| cad_units | auto\|mm\|cm\|m\|in\|ft | CAD import units. Default auto. |
| this_view_only | boolean | CAD: current view only. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
link_rvt(path;position)
link_ifc(path;position)
link_cad(path;position,view,cad_units,this_view_only)
import_cad(path;position,view,cad_units,this_view_only)
reload(link)
reload_from(link,path)
unload(link)
remove(link)
acquire_coordinates(link)
```

#### `export` (write (non-destructive), openWorld; 1483 B)

Export pdf (sheets/views, combined or not), dwg, dxf, ifc, nwc, image (hi-res), csv (schedules), fbx, gbxml to a folder (default <home>/exports/<doc>). Long exports continue as a job.

| param | type | notes |
|---|---|---|
| format **(required)** | enum(9) | File format. |
| views | string[] | Views/sheets (names, numbers, ids), or ['all_sheets'], ['set:<sheet set>']. Default active view. |
| folder | string | Output folder. Default <home>/exports/<doc>. |
| name | string | File name pattern, e.g. '{number} - {name}'. |
| combine | boolean | pdf: one combined file. Default false. |
| setup | string | dwg/dxf/ifc: export setup name. |
| size | integer | image: long edge px. Default 3000. |
| color | color\|gray\|bw | pdf/image: default color. |
| overwrite | boolean | Replace existing files. Default false. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

#### `model_delivery` (destructive, openWorld; 1513 B)

Delivery packages of standalone RVTs (links, cleanup, exports, QA) from a saved recipe: inspect sources, preview (gives confirm), execute (job), save/get/list recipes, test fixture. Recipe shape: help topic=recipe.

| param | type | notes |
|---|---|---|
| sources | string[] | inspect: source .rvt paths. |
| project | string | Project id (recipe library key). |
| recipe | object | Delivery recipe; shape in help {topic:'recipe'}. |
| expected_sha | string | save_recipe: only overwrite this version. |
| template | string | fixture: .rte template path. |
| folder | string | fixture: new empty folder. |
| id | string | fixture: fixture id. |
| limit | integer | list_recipes: default 100. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |

Ops (`op` required; params before `;` required, `/` = alternatives, `+` = together):

```
inspect(sources;project,recipe)
preview(recipe)
execute(confirm)
save_recipe(recipe;expected_sha)
get_recipe(project)
list_recipes(;limit)
fixture(template,folder,id)
```

### Batch, undo, jobs, help

#### `change_set` (destructive; 1400 B)

Run up to 100 write ops in one undo step, all or nothing; later ops use earlier results: '$0' (first id), '$0.ids', '$1.type'. ops=[{tool:'create_elements',op:'level',...}]. Not for open/save/sync/close.

| param | type | notes |
|---|---|---|
| ops | object[] | [{tool, op, ...that op's params}]. |
| name | string | Undo history name. Default 'change_set'. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| preview | boolean | true = dry run: returns the plan and a confirm token, changes nothing. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
| units | mm\|cm\|m\|in\|ft | Unit of input lengths. Default mm. |

#### `undo` (destructive; 727 B)

Undo your last write(s) in the target doc while they are still Revit's newest changes (steps 1-10, default 1); redo=true redoes. If the user edited since, explains how to revert instead.

| param | type | notes |
|---|---|---|
| steps | integer | 1-10. Default 1. |
| redo | boolean | Redo instead. |
| mode | auto\|compensate | auto (default): Revit undo; compensate: apply the inverse change as a new write. |
| doc | string | Doc title, path or # from status. Default: pinned target. |

#### `job_status` (readOnly, idempotent; 536 B)

State, progress and result of a job (j#: export, sync, open, delivery, long scan) or of a write whose outcome was unclear (w#). wait= seconds to wait, 0-45, default 20. No id: your recent jobs.

| param | type | notes |
|---|---|---|
| id | string | Job id j# or write id w#. |
| wait | integer | Seconds to wait for completion, 0-45. Default 20. |

#### `cancel_job` (write (non-destructive), idempotent; 504 B)

Cancel a queued or running job or queued request. A running Revit step finishes or rolls back safely; partial exports and delivery staging are removed.

| param | type | notes |
|---|---|---|
| id **(required)** | string | Job id j# or request id w#. |
| reason | string | Why (logged). |

#### `help` (readOnly, idempotent; 572 B)

Params and a working example for a tool or op: help {tool:'create_elements',op:'wall'}. topic= units, targeting, selectors, confirm, errors, recipe, run_csharp, workflow:<audit|sheets|family|rooms|visual>, error codes.

| param | type | notes |
|---|---|---|
| tool | string | Tool name. |
| op | string | Op name. |
| topic | string | Topic, or an error code. |

### Opt-in

#### `run_csharp` (destructive, openWorld; 987 B)

Run C# on the Revit API when no tool fits. Off unless the user enabled it locally. read and dry_run (default) always roll back; dry_run reports changes and a token; commit needs that token and the user's OK.

| param | type | notes |
|---|---|---|
| code **(required)** | string | C# method body with doc, uidoc, app, args (IDictionary), log(object); return a value. |
| args | object | Values passed as args. |
| mode | read\|dry_run\|commit | Default dry_run. |
| timeout | integer | Seconds 1-45. Default 30. |
| doc | string | Doc title, path or # from status. Default: pinned target. |
| confirm | string | Token from a NOT APPLIED or preview result; applies exactly that plan. |
