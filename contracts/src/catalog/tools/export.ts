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
    pdf: op("", "views,folder,name,combine,color,overwrite,doc,confirm", M, [{"format":"pdf","views":["all_sheets"],"combine":true},{"format":"pdf","views":["set:Issue 03"],"name":"{number}_{name}","color":"bw"}], { help: "One PDF per view/sheet (combine:true: one file). name tokens {number} {name} {doc} {type} {date} {n}; default '{number} - {name}' for sheets, '{name}' for views. Default folder <home>/exports/<doc>. Existing files give NAME_TAKEN unless overwrite:true, which needs confirm. Several files run as a job j#.", errors: ["NAME_TAKEN","NOT_FOUND","INVALID_ARGS"] }),
    dwg: op("", "views,folder,name,setup,overwrite,doc,confirm", M, [{"format":"dwg","views":["A101","A102"]},{"format":"dwg","views":["Level 1"],"setup":"EBA Layers"}], { help: "One DWG per view/sheet with its views merged in; setup = a DWG export setup of the model (default: the active setup).", errors: ["NAME_TAKEN","NOT_FOUND","EXPORTER_MISSING"] }),
    dxf: op("", "views,folder,name,setup,overwrite,doc,confirm", M, [{"format":"dxf","views":["Level 1"]}], { errors: ["NAME_TAKEN","NOT_FOUND","EXPORTER_MISSING"] }),
    ifc: op("", "views,folder,name,setup,overwrite,doc,confirm", meta(M, { tx: "temp" }), [{"format":"ifc","setup":"IFC4 Reference View"},{"format":"ifc","views":["{3D}"],"setup":"IFC2x3 Coordination View 2.0"}], { help: "One IFC of the model; views[0] limits it to the elements visible in that view. setup = IFC2x3 Coordination View 2.0 (default), IFC2x3 Basic FM, IFC4 Reference View, IFC4 Design Transfer View or IFC4x3.", errors: ["NAME_TAKEN","NOT_FOUND","EXPORTER_MISSING"] }),
    nwc: op("", "views,folder,name,overwrite,doc,confirm", M, [{"format":"nwc","views":["{3D}"]}], { help: "3D views only; without views the active 3D view, else the whole model. Needs the Navisworks exporter.", errors: ["EXPORTER_MISSING","INVALID_ARGS"] }),
    image: op("", "views,folder,name,size,color,overwrite,doc,confirm", M, [{"format":"image","views":["Level 1"],"size":4000}], { help: "PNG per view/sheet, long edge size px (default 3000). For a quick look use capture instead.", errors: ["NAME_TAKEN","NOT_FOUND"] }),
    csv: op("", "views,folder,name,overwrite,doc,confirm", M, [{"format":"csv","views":["Door Schedule"]}], { help: "Schedules only: one comma-separated file per schedule with one header row.", errors: ["INVALID_ARGS","NAME_TAKEN"] }),
    fbx: op("", "views,folder,name,overwrite,doc,confirm", M, [{"format":"fbx","views":["{3D}"]}], { help: "3D views only.", errors: ["INVALID_ARGS","EXPORTER_MISSING"] }),
    gbxml: op("", "folder,name,overwrite,doc,confirm", M, [{"format":"gbxml"}], { help: "The model's energy model as gbXML (<doc>.xml); placed rooms or spaces give the best result.", errors: ["NAME_TAKEN","REVIT_REFUSED"] }),
  },
};
