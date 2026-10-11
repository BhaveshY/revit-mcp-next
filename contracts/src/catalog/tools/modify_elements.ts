// Catalog entry for `modify_elements` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, deg, E, I, meta, mm, N, op, P, PT, PTS, S, SA, syn, WD, WRITE, WRITE_TAIL } from "../params.js";
import type { ErrorSpec } from "../../errors.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE, { blast: ["bulk"] });

/** modify_elements codes, merged into the ErrorCatalog (add-in: Domains/Modify/ModifySupport.cs). */
const MODIFY_ERRORS: Record<string, ErrorSpec> = {
  ELEMENTS_PINNED: {
    code: "ELEMENTS_PINNED",
    meaning: "Some of the elements are pinned; nothing was changed.",
    nothingChanged: true,
    details: "ids[] (pinned, first 20), total",
    fix: { kind: "ask", text: "${details.total} element(s) are pinned; only if the user agrees, repeat the call with allow_pinned:true" },
  },
  EXPECT_MISMATCH: {
    code: "EXPECT_MISMATCH",
    meaning: "The delete set (targets + dependents) differs from expect_count/expect_ids; nothing was deleted.",
    nothingChanged: true,
    details: "param, expected, actual, extra[], missing[] or sample[]",
    fix: { kind: "same_call_with", set: { preview: true }, note: "shows the real delete set; ask the user before deleting it" },
  },
};

export const tool: ToolSpec = {
  name: "modify_elements",
  title: "Modify elements",
  description: "Edit existing elements chosen by ids, from= or filter=: move, copy, rotate, mirror, array, align, delete, pin, change_type, join/cut/attach, flip, split, reshape, group/ungroup. Deleting >20 asks to confirm.",
  group: "write",
  annotations: WD,
  discriminator: "op",
  profiles: ["core", "full"],
  properties: {
    ids: P.ids,
    from: P.from,
    filter: P.filter,
    by: P.by,
    to: mm(PT("Move/copy so the element's location point lands here [x,y(,z)] mm.")),
    levels: SA("copy: copy to these levels, aligned (vertical offset only)."),
    count: I("copy/array: number of copies (array includes original)."),
    angle: deg(N("Degrees counterclockwise (rotate, radial array).")),
    center: mm(PT("Rotation/radial center [x,y(,z)]. Default: element center.")),
    axis: PT("Rotation axis direction. Default [0,0,1]."),
    start: mm(PT("mirror: axis start; reshape: new curve start.")),
    end: mm(PT("mirror: axis end; reshape: new curve end.")),
    points: mm(PTS("reshape: new path for curve elements.")),
    through: mm(PT("reshape: point on arc.")),
    keep: B("mirror: keep original (mirror a copy). Default true."),
    kind: syn(E(["linear","radial"], "array: default linear."), {"line":"linear","lineararray":"linear","circular":"radial","polar":"radial","radialarray":"radial"}),
    target: S("align: grid, level, ref plane or element id to align to."),
    lock: B("align: lock the alignment."),
    with: S("join/cut/attach: the other element id or r# handle."),
    side: syn(E(["top","base","hand","facing","wall"], "attach/detach: top|base; flip: hand|facing|wall."), {"bottom":"base"}),
    at: mm(PT("split: split point; place_group: insertion point.")),
    type: P.type,
    pinned: B("pin: true pins (default), false unpins."),
    allow_pinned: B("Allow changing pinned elements."),
    expect_count: I("delete: abort unless exactly this many elements (incl. dependents) would be deleted."),
    expect_ids: SA("delete: abort unless the delete set (incl. dependents) is exactly these ids."),
    name: S("group/place_group: group name."),
    level: S("place_group: level."),
    ...WRITE_TAIL,
  },
  required: ["op"],
  ops: {
    move: op("ids/from/filter,by/to", "", M, [{"op":"move","from":"selection","by":[0,500]}, {"op":"move","ids":["304601"],"to":[12000,4000]}], {
      help: "by moves every element by the vector; to moves them so the first element's location point (curve start for walls/lines) lands there, z kept unless given. Pinned elements need allow_pinned:true.",
      api: "ElementTransformUtils.MoveElements",
      errors: ["ELEMENTS_PINNED", "INVALID_ARGS", "NOT_FOUND"],
    }),
    copy: op("ids/from/filter,by/to/levels", "count", M, [{"op":"copy","ids":["304601"],"levels":["Level 2","Level 3"]}, {"op":"copy","ids":["304601"],"by":[0,3000],"count":3}], {
      help: "by/to: count copies at by, 2*by, ...; levels: copies aligned to each level (the lowest source level maps to the target) with base/top constraints re-associated.",
      api: "ElementTransformUtils.CopyElements + level parameters",
    }),
    rotate: op("ids/from/filter,angle", "center,axis", M, [{"op":"rotate","ids":["304601"],"angle":90}, {"op":"rotate","from":"r3","angle":-45,"center":[5000,5000]}], {
      help: "Counterclockwise in plan for the default axis [0,0,1]; center defaults to the center of the elements' bounding box.",
      api: "ElementTransformUtils.RotateElements",
      errors: ["ELEMENTS_PINNED"],
    }),
    mirror: op("ids/from/filter,start,end", "keep", M, [{"op":"mirror","from":"r7","start":[6000,0],"end":[6000,8000]}, {"op":"mirror","ids":["306100"],"start":[0,4000],"end":[10000,4000],"keep":false}], {
      help: "Mirrors about the vertical plane through start→end. keep:true (default) mirrors a copy; keep:false mirrors the originals.",
      api: "ElementTransformUtils.MirrorElements",
      errors: ["UNSUPPORTED_OP", "ELEMENTS_PINNED"],
    }),
    array: op("ids/from/filter,count,by/angle", "center,kind", M, [{"op":"array","ids":["306000"],"count":5,"by":[1200,0]}, {"op":"array","ids":["306000"],"count":6,"angle":60,"center":[0,0],"kind":"radial"}], {
      help: "Unassociated copies; count includes the original. Linear: by is the spacing between members. Radial: angle between members about center (default: element center).",
      api: "ElementTransformUtils.CopyElements (+ RotateElements)",
    }),
    align: op("ids,target", "lock", meta(M, { blast: [] }), [{"op":"align","ids":["304601"],"target":"A"}, {"op":"align","ids":["306100"],"target":"Level 2"}], {
      help: "Moves each element so its location line/point lies on the target (grid, reference plane, straight wall/line: parallel move in plan; level: vertical move). lock:true adds a locked alignment where Revit offers a matching reference (family instances).",
      api: "ElementTransformUtils.MoveElement + Document.Create.NewAlignment",
      errors: ["NOT_FOUND", "UNSUPPORTED_OP", "ELEMENTS_PINNED"],
    }),
    delete: op("ids/from/filter", "allow_pinned,expect_count,expect_ids", meta(M, { blast: ["delete"], inproc: false }), [{"op":"delete","filter":{"category":["Furniture"],"level":"Level 3"}}, {"op":"delete","ids":["304601","304602"]}, {"op":"delete","ids":["304601"],"expect_ids":["304601","304650"]}], {
      help: "The delete set includes dependents; more than 20 returns NOT APPLIED with a confirm token. expect_ids aborts unless the delete set is exactly these ids.",
      api: "Document.Delete",
      errors: ["ELEMENTS_PINNED", "EXPECT_MISMATCH", "NOT_FOUND"],
    }),
    pin: op("ids/from/filter", "pinned", M, [{"op":"pin","from":"r3"}, {"op":"pin","ids":["304601"],"pinned":false}], { help: "pinned:false unpins. Elements already in the requested state are counted as unchanged." }),
    change_type: op("ids/from/filter,type", "", M, [{"op":"change_type","ids":["304601"],"type":"Basic Wall: Generic - 300mm"}, {"op":"change_type","filter":{"category":["Doors"],"level":"Level 1"},"type":"Single-Flush: 0915 x 2134mm"}], {
      help: "The type must fit the elements' category; elements it does not fit are skipped with a warning. NOT_FOUND lists the types that fit.",
      api: "Element.ChangeTypeId",
      errors: ["NOT_FOUND", "AMBIGUOUS_NAME"],
    }),
    join: op("ids,with", "", meta(M, { blast: [] }), [{"op":"join","ids":["304601"],"with":"305010"}], { help: "Joins the geometry of each id with each element in with; pairs already joined count as unchanged.", api: "JoinGeometryUtils.JoinGeometry" }),
    unjoin: op("ids,with", "", meta(M, { blast: [] }), [{"op":"unjoin","ids":["304601"],"with":"305010"}], { api: "JoinGeometryUtils.UnjoinGeometry" }),
    switch_join: op("ids,with", "", meta(M, { blast: [] }), [{"op":"switch_join","ids":["304601"],"with":"305010"}], { help: "Both elements must already be joined.", api: "JoinGeometryUtils.SwitchJoinOrder" }),
    cut: op("ids,with", "", meta(M, { blast: [] }), [{"op":"cut","ids":["305020"],"with":"304601"}], {
      help: "with is the cutting element; ids are cut. A void family instance always cuts, whichever side it is given on.",
      api: "InstanceVoidCutUtils.AddInstanceVoidCut / SolidSolidCutUtils.AddCutBetweenSolids",
    }),
    uncut: op("ids,with", "", meta(M, { blast: [] }), [{"op":"uncut","ids":["305020"],"with":"304601"}], { help: "Removes void and solid cuts between the elements in either direction." }),
    attach: op("ids,with", "side", meta(M, { min: 2027, blast: [] }), [{"op":"attach","ids":["304601"],"with":"305300","side":"top"}], {
      help: "Walls attach top or base to roofs, floors, ceilings, toposolids or walls; structural columns attach to beams, floors and roofs. side defaults to top. Revit 2027 only.",
      api: "Wall.AddAttachment / ColumnAttachment.AddColumnAttachment",
      errors: ["UNSUPPORTED_VERSION", "UNSUPPORTED_OP"],
    }),
    detach: op("ids", "with,side", meta(M, { min: 2027, blast: [] }), [{"op":"detach","ids":["304601"],"side":"top"}], {
      help: "Without with, removes every attachment on the side (both sides when side is omitted). Revit 2027 only.",
      api: "Wall.RemoveAttachment / ColumnAttachment.RemoveColumnAttachment",
      errors: ["UNSUPPORTED_VERSION"],
    }),
    flip: op("ids/from/filter,side", "", M, [{"op":"flip","ids":["306100"],"side":"hand"}, {"op":"flip","ids":["304601"],"side":"wall"}], {
      help: "Doors, windows and families flip hand or facing; walls flip their orientation with side wall.",
      api: "FamilyInstance.flipHand / flipFacing, Wall.Flip",
    }),
    split: op("ids,at", "", meta(M, { blast: [] }), [{"op":"split","ids":["304601"],"at":[6000,0]}], {
      help: "MEP pipes and ducts break at the point; walls, structural framing and lines are copied and trimmed, and each half keeps the doors/windows on its side. Inserts spanning the split point are refused.",
      api: "PlumbingUtils/MechanicalUtils.BreakCurve; CopyElement + LocationCurve",
      errors: ["UNSUPPORTED_OP", "INVALID_ARGS"],
    }),
    reshape: op("ids,start+end/points", "through", meta(M, { blast: [] }), [{"op":"reshape","ids":["304601"],"start":[0,0],"end":[10000,0]}, {"op":"reshape","ids":["304601"],"start":[0,0],"end":[10000,0],"through":[5000,2000]}], {
      help: "One curve-based element (wall, beam, pipe, duct, line) at a time: start+end gives a line, through or 3 points an arc; z keeps the old ends unless given. Sketch-based floors/roofs cannot be reshaped.",
      api: "LocationCurve.Curve",
      errors: ["UNSUPPORTED_OP", "INVALID_ARGS"],
    }),
    group: op("ids/from/filter", "name", meta(M, { blast: [] }), [{"op":"group","from":"r7","name":"Bathroom A"}], { help: "Elements already in a group are refused. The new group is output as group and its type as type.", api: "Document.Create.NewGroup", errors: ["NAME_TAKEN"] }),
    ungroup: op("ids/from", "", meta(M, { blast: [] }), [{"op":"ungroup","ids":["307000"]}], { help: "A member id ungroups the group it belongs to.", api: "Group.UngroupMembers" }),
    place_group: op("name,at", "level", meta(M, { blast: ["create"] }), [{"op":"place_group","name":"Bathroom A","at":[20000,0],"level":"Level 2"}], {
      help: "Model groups only; with level, at's z is the height above that level.",
      api: "Document.Create.PlaceGroup",
    }),
  },
  errors: MODIFY_ERRORS,
};
