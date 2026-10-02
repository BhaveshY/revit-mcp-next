// Catalog entry for `capture` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, meta, mm, N, op, ORIENT_SYN, P, PT, READ, RO, S, SA, STYLE_SYN, syn } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(READ, { idle: false, tx: "temp", job: "auto" });

export const tool: ToolSpec = {
  name: "capture",
  title: "Capture image",
  description: "See the model: returns an image of a view or sheet, or of elements (ids/from) in a temporary 3D box. region= zooms; size small|medium|large; compare= c# shows what changed. Nothing is kept in the model.",
  group: "visual",
  annotations: RO,
  discriminator: null,
  profiles: ["core", "full"],
  properties: {
    view: S("View name, id, sheet number, or 'active' (default)."),
    ids: SA("Focus these elements: temp 3D view boxed around them."),
    from: S("Focus an element set: r# handle, 'selection' or 'last'."),
    region: mm(PT("Zoom to [x0,y0,x1,y1] mm (model coords; sheet mm on sheets).")),
    orient: syn(E(["iso_se","iso_sw","iso_ne","iso_nw","top","front","back","left","right"], "3D direction. Default iso_se."), ORIENT_SYN),
    style: syn(E(["wireframe","hidden","shaded","consistent","realistic"], "Default: view's own; temp 3D shaded."), STYLE_SYN),
    size: syn(E(["small","medium","large"], "Long edge at most 768, 1280 or 1568 px. Default medium."), {"s":"small","sm":"small","m":"medium","md":"medium","default":"medium","l":"large","lg":"large","big":"large"}),
    margin: mm(N("Space around focused elements, mm. Default 1000.")),
    highlight: SA("Tint these element ids red in the image."),
    annotations: B("Show annotations. Default true for views, false for focus."),
    compare: S("Earlier capture id (c#): marks changed pixels."),
    format: syn(E(["auto","png","jpg"], "Default auto (png drawings, jpg shaded)."), {"jpeg":"jpg","default":"auto"}),
    doc: P.doc,
  },
  ops: {
    "": op("", "view,ids,from,region,orient,style,size,margin,highlight,annotations,compare,format,doc", M, [{}, {"view":"A101","size":"large"}, {"from":"last","orient":"iso_se"}, {"view":"Level 1","region":[0,0,12000,8000],"highlight":["304601"]}, {"view":"Level 1","compare":"c4"}], { help: "Temporary views and overrides are rolled back; nothing is kept in the model. size small|medium|large caps the long edge at 768/1280/1568 px." }),
  },
};
