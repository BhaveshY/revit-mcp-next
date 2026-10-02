// Catalog entry for `export` (SPEC Appendix A/B). The advertised part must stay equal to docs/design/SPEC-catalog.mjs
// unless the lead approves a surface change (e2e/budgets.json baseline). Lanes extend examples, help and errors.
import { B, E, I, LIFE, meta, op, P, px, S, SA, syn } from "../params.js";
import type { ToolSpec } from "../types.js";

const M = meta(LIFE, { tx: "none", blast: ["file_overwrite"] });

export const tool: ToolSpec = {
  name: "export",
  title: "Export files",
  description: "Export pdf (sheets/views, combined or not), dwg, dxf, ifc, nwc, image (hi-res), csv (schedules), fbx, gbxml to a folder (default <home>/exports/<doc>). Long exports continue as a job.",
  group: "write",
  annotations: {"readOnlyHint":false,"destructiveHint":false,"openWorldHint":true},
  discriminator: "format",
  profiles: ["full"],
  properties: {
    format: syn(E(["pdf","dwg","dxf","ifc","nwc","image","csv","fbx","gbxml"], "File format."), {"navisworks":"nwc","autocad":"dwg","png":"image","jpg":"image","picture":"image","xml":"gbxml"}),
    views: SA("Views/sheets (names, numbers, ids), or ['all_sheets'], ['set:<sheet set>']. Default active view."),
    folder: S("Output folder. Default <home>/exports/<doc>."),
    name: S("File name pattern, e.g. '{number} - {name}'."),
    combine: B("pdf: one combined file. Default false."),
    setup: S("dwg/dxf/ifc: export setup name."),
    size: px(I("image: long edge px. Default 3000.")),
    color: syn(E(["color","gray","bw"], "pdf/image: default color."), {"colour":"color","grey":"gray","grayscale":"gray","greyscale":"gray","blackwhite":"bw","blackandwhite":"bw","monochrome":"bw"}),
    overwrite: B("Replace existing files. Default false."),
    doc: P.doc,
    confirm: P.confirm,
  },
  required: ["format"],
  ops: {
    pdf: op("", "views,folder,name,combine,color,overwrite,doc,confirm", M, [{"format":"pdf","views":["all_sheets"],"combine":true}], { help: "Default folder <home>/exports/<doc>. Existing files are kept unless overwrite; overwriting needs confirm. Long exports continue as a job j#." }),
    dwg: op("", "views,folder,name,setup,overwrite,doc,confirm", M, [{"format":"dwg","views":["A101","A102"]}]),
    dxf: op("", "views,folder,name,setup,overwrite,doc,confirm", M, [{"format":"dxf","views":["Level 1"]}]),
    ifc: op("", "views,folder,name,setup,overwrite,doc,confirm", meta(M, { tx: "temp" }), [{"format":"ifc","setup":"IFC4 Reference View"}]),
    nwc: op("", "views,folder,name,overwrite,doc,confirm", M, [{"format":"nwc","views":["{3D}"]}]),
    image: op("", "views,folder,name,size,color,overwrite,doc,confirm", M, [{"format":"image","views":["Level 1"],"size":4000}]),
    csv: op("", "views,folder,name,overwrite,doc,confirm", M, [{"format":"csv","views":["Door Schedule"]}]),
    fbx: op("", "views,folder,name,overwrite,doc,confirm", M, [{"format":"fbx","views":["{3D}"]}]),
    gbxml: op("", "folder,name,overwrite,doc,confirm", M, [{"format":"gbxml"}]),
  },
};
