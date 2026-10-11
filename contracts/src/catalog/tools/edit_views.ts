// Catalog entry for `edit_views` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, I, meta, mm, N, O, op, ORIENT_SYN, P, PT, S, SA, STYLE_SYN, syn, VIEW_KIND_SYN, WD, WRITE, WRITE_TAIL } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(WRITE);

export const tool: ToolSpec = {
  name: "edit_views",
  title: "Create and edit views",
  description: "Create views (floor, ceiling, structural, area plan, section, elevation, 3D, callout, drafting, legend copy), duplicate, rename, delete, templates; set scale, detail, crop, view range, phase, 3D orientation, section box.",
  group: "write",
  annotations: WD,
  discriminator: "op",
  profiles: ["full"],
  properties: {
    kind: syn(E(["floor_plan","ceiling_plan","structural_plan","area_plan","section","elevation","3d","callout","drafting","legend"], "create: view kind."), VIEW_KIND_SYN),
    view: S("View name or id (callout/legend: the parent/source view)."),
    views: P.views,
    level: P.level,
    name: P.name,
    view_type: S("View family type name. Default: first of the kind."),
    template: S("View template name; 'none' removes it."),
    scale: I("Scale denominator: 100 = 1:100."),
    detail_level: syn(E(["coarse","medium","fine"], "Detail level."), {"low":"coarse","high":"fine"}),
    style: syn(E(["wireframe","hidden","shaded","consistent","realistic"], "Visual style."), STYLE_SYN),
    discipline: syn(E(["architectural","structural","mechanical","electrical","plumbing","coordination"], "View discipline."), {"arch":"architectural","struct":"structural","mech":"mechanical","hvac":"mechanical","elec":"electrical","plumb":"plumbing","coord":"coordination"}),
    phase: S("Phase name."),
    phase_filter: S("Phase filter name."),
    crop: B("Crop view on/off."),
    crop_box: mm(PT("Crop [x0,y0,x1,y1] mm (plan) or [x0,y0,z0,x1,y1,z1].")),
    crop_visible: B("Show crop region."),
    view_range: mm(O("Plans: {top,cut,bottom,depth} mm above the view level; or {cut:1200}.")),
    far_clip: mm(N("Sections/elevations: far clip depth mm.")),
    scope_box: S("Scope box name; 'none' removes."),
    orient: syn(E(["iso_se","iso_sw","iso_ne","iso_nw","top","front","back","left","right"], "3D direction."), ORIENT_SYN),
    section_box: mm(PT("3D: [x0,y0,z0,x1,y1,z1] mm; [] turns it off.")),
    ids: SA("create 3d/section: fit the box around these elements."),
    from: S("Like ids: r# handle or 'selection'."),
    start: mm(PT("section: cut line start [x,y].")),
    end: mm(PT("section: cut line end [x,y] (looks left of start->end).")),
    depth: mm(N("section: view depth mm. Default 3000.")),
    height: mm(N("section: height mm above level. Default level-to-level.")),
    at: mm(PT("elevation: marker point [x,y]; duplicate: viewport center on sheet.")),
    direction: syn(E(["north","south","east","west"], "elevation: looking direction."), {"n":"north","s":"south","e":"east","w":"west"}),
    region: mm(PT("callout: [x0,y0,x1,y1] mm in the parent view.")),
    area_scheme: S("area_plan: area scheme name."),
    perspective: B("3d: perspective camera. Default false (isometric)."),
    mode: syn(E(["copy","detailing","dependent"], "duplicate: default copy."), {"duplicate":"copy","withdetailing":"detailing","dependant":"dependent"}),
    sheet: S("duplicate: also place the copy on this sheet."),
    underlay: S("Underlay level name; 'none'."),
    ...WRITE_TAIL,
  },
  required: ["op"],
  ops: {
    create: op("kind", "level,name,view_type,template,scale,start,end,depth,height,at,direction,orient,section_box,ids,from,region,view,area_scheme,perspective", meta(M, { strict: true }), [{"op":"create","kind":"section","start":[0,4000],"end":[12000,4000],"depth":5000,"name":"Section A"}, {"op":"create","kind":"floor_plan","level":"Level 2","name":"Level 2 - Furniture"}, {"op":"create","kind":"3d","from":"r7","orient":"iso_sw","name":"Check 3D"}, {"op":"create","kind":"elevation","at":[6000,-3000],"direction":"north","level":"Level 1","name":"South Facade"}, {"op":"create","kind":"callout","view":"Level 1","region":[0,0,4000,3000],"name":"Detail Stair"}, {"op":"create","kind":"drafting","name":"Typical Detail","scale":10}, {"op":"create","kind":"ceiling_plan","level":"Level 1"}], {
      help: "section: looks to the left of start->end, cut plane on the line, depth = far clip; with ids/from and no start/end it fits around the elements looking north. elevation: marker at at=[x,y] in a plan of level= (or view=), looking direction=. callout: region in the parent view (plan x,y mm). legend copies view= or the first legend (LEGEND_NEEDS_SEED when none exists).",
      errors: ["NOT_FOUND", "NAME_TAKEN", "LEGEND_NEEDS_SEED", "TEMPLATE_CONTROLLED", "INVALID_ARGS"],
    }),
    duplicate: op("view", "mode,name,sheet,at", meta(M, { strict: true }), [{"op":"duplicate","view":"Level 1","mode":"dependent","name":"Level 1 - Part A"}, {"op":"duplicate","view":"Level 1","mode":"detailing","name":"Level 1 - Print","sheet":"A101","at":[200,150]}], { help: "at = viewport center in sheet mm (default: sheet center).", errors: ["UNSUPPORTED_OP", "NAME_TAKEN", "REVIT_REFUSED"] }),
    rename: op("view,name", "", M, [{"op":"rename","view":"Level 1","name":"EG Grundriss"}], { errors: ["NAME_TAKEN", "NOT_FOUND"] }),
    set: op("view/views", "scale,detail_level,style,discipline,phase,phase_filter,template,crop,crop_box,crop_visible,view_range,far_clip,scope_box,orient,section_box,underlay", M, [{"op":"set","view":"Level 1","scale":50,"detail_level":"fine","view_range":{"cut":1100}}, {"op":"set","views":["Level 1","Level 2"],"template":"none","crop":true,"crop_box":[0,0,20000,15000]}, {"op":"set","view":"{3D}","section_box":[0,0,0,12000,8000,7000],"orient":"iso_ne","style":"shaded"}], {
      help: "Settings a view template controls give TEMPLATE_CONTROLLED (get_view lists template_controls); set them on the template or pass template:'none'. crop_box turns crop on. section_box [] turns it off.",
      errors: ["TEMPLATE_CONTROLLED", "INVALID_ARGS", "NOT_FOUND"],
    }),
    create_template: op("view,name", "", M, [{"op":"create_template","view":"Level 1","name":"EBA Plan 1:50"}], { errors: ["NAME_TAKEN", "UNSUPPORTED_OP"] }),
    delete: op("views", "", meta(M, { blast: ["delete"], inproc: false }), [{"op":"delete","views":["Section A"]}], { help: "Deleting views also deletes their viewports, dependent views and view-specific annotation; more than the confirm limit needs the user's OK. The active view cannot be deleted.", errors: ["UNSUPPORTED_OP", "NOT_FOUND"] }),
  },
};
