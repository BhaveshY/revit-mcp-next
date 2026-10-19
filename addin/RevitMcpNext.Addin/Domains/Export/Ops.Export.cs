using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// export.&lt;format&gt; (SPEC Appendix A/B, D3 §3.11): pdf, dwg, dxf, ifc, nwc, image, csv, fbx, gbxml to a folder
    /// (default &lt;home&gt;\exports\&lt;doc&gt;). One file per view/sheet (pdf combine:true gives one file), named by the
    /// name pattern ({name}, {number}, {doc}, {type}, {date}; default "{number} - {name}" for sheets and "{name}" for
    /// views). Existing files are never replaced silently: overwrite:false gives NAME_TAKEN, overwrite:true asks to
    /// confirm (rule file_overwrite). Two or more files run as a job (one file per step) so long exports report
    /// progress and return j# at the call budget. ifc runs inside a rolled-back "MCP temp export.ifc" transaction.
    /// </summary>
    internal static class ExportOps
    {
        private const int InlineMaxFiles = 1;

        [Op("export.pdf")] public static OpResult Pdf(RequestContext ctx) => Run(ctx, "pdf");
        [Op("export.dwg")] public static OpResult Dwg(RequestContext ctx) => Run(ctx, "dwg");
        [Op("export.dxf")] public static OpResult Dxf(RequestContext ctx) => Run(ctx, "dxf");
        [Op("export.ifc")] public static OpResult Ifc(RequestContext ctx) => Run(ctx, "ifc");
        [Op("export.nwc")] public static OpResult Nwc(RequestContext ctx) => Run(ctx, "nwc");
        [Op("export.image")] public static OpResult Image(RequestContext ctx) => Run(ctx, "image");
        [Op("export.csv")] public static OpResult Csv(RequestContext ctx) => Run(ctx, "csv");
        [Op("export.fbx")] public static OpResult Fbx(RequestContext ctx) => Run(ctx, "fbx");
        [Op("export.gbxml")] public static OpResult Gbxml(RequestContext ctx) => Run(ctx, "gbxml");

        /// <summary>One output file (and the view it comes from; null for model-wide exports).</summary>
        private sealed class ExportItem
        {
            public View View;
            public List<View> Views;
            public string BaseName;
            public string Extension;
            public string FullPath(string folder) => Path.Combine(folder, BaseName + Extension);
            public string Label => View != null ? Describe(View) : (Views != null && Views.Count > 0 ? Views.Count.ToString(CultureInfo.InvariantCulture) + " views" : "model");
        }

        private sealed class ExportPlan
        {
            public string Format;
            public string Folder;
            public List<ExportItem> Items = new List<ExportItem>();
            public bool Combine;
            public string Setup;
            public string Color;
            public int Size;
            public bool Overwrite;
        }

        private static OpResult Run(RequestContext ctx, string format)
        {
            Document doc = ctx.Doc;
            ExportPlan plan = BuildPlan(ctx, doc, format);
            CheckExporter(format);

            List<string> existing = plan.Items.Select(i => i.FullPath(plan.Folder)).Where(File.Exists).ToList();
            var planData = new Dictionary<string, object>
            {
                ["format"] = format,
                ["folder"] = plan.Folder,
                ["files"] = plan.Items.Select(i => (object)(i.BaseName + i.Extension)).Take(100).ToList(),
                ["total"] = plan.Items.Count,
                ["existing"] = existing.Select(p => (object)Path.GetFileName(p)).Take(50).ToList()
            };
            if (existing.Count > 0)
            {
                if (!plan.Overwrite)
                {
                    throw new OpException(ErrorCodes.NameTaken,
                        existing.Count.ToString(CultureInfo.InvariantCulture) + " of the " + plan.Items.Count.ToString(CultureInfo.InvariantCulture) + " file(s) already exist in " + plan.Folder +
                        " (" + string.Join(", ", existing.Take(3).Select(Path.GetFileName)) + (existing.Count > 3 ? ", ..." : string.Empty) + "); pass overwrite:true to replace them or another name/folder.",
                        new Dictionary<string, object>
                        {
                            ["param"] = "name",
                            ["value"] = ctx.Args.Str("name"),
                            ["next"] = SuggestPattern(ctx, plan),
                            ["existing"] = existing.Take(20).Cast<object>().ToList(),
                            ["folder"] = plan.Folder
                        });
                }
                OpResult confirm = ctx.ConfirmOrPlan("file_overwrite", planData,
                    "export would overwrite " + existing.Count.ToString(CultureInfo.InvariantCulture) + " existing file(s) in " + plan.Folder);
                if (confirm != null) return confirm;
            }
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("export " + plan.Items.Count.ToString(CultureInfo.InvariantCulture) + " " + format.ToUpperInvariant() + " file(s) to " + plan.Folder +
                                           (existing.Count > 0 ? ", replacing " + existing.Count.ToString(CultureInfo.InvariantCulture) : string.Empty), planData);
            }

            Directory.CreateDirectory(plan.Folder);
            var outcome = new ExportOutcome();
            if (plan.Items.Count <= InlineMaxFiles || ctx.IsInProcess)
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    if (index > 0 && ctx.CancelRequested) break;
                    ExportOne(ctx, doc, plan, plan.Items[index], outcome);
                }
                return Finish(ctx, plan, outcome);
            }

            var steps = new List<Func<JobStep, StepResult>>();
            for (int index = 0; index < plan.Items.Count; index++)
            {
                int current = index;
                steps.Add(step =>
                {
                    if (step.CancelRequested)
                    {
                        step.Context.Warn(WarningCodes.PartialResult, "cancelled after " + outcome.Files.Count.ToString(CultureInfo.InvariantCulture) + " file(s)");
                        return StepResult.Finish(Finish(step.Context, plan, outcome));
                    }
                    ExportItem item = plan.Items[current];
                    step.Progress(format + " " + item.Label, current, plan.Items.Count);
                    ExportOne(step.Context, step.Doc ?? doc, plan, item, outcome);
                    if (current == plan.Items.Count - 1) return StepResult.Finish(Finish(step.Context, plan, outcome));
                    return StepResult.Next(format + " " + plan.Items[current + 1].Label, current + 1, plan.Items.Count);
                });
            }
            return ctx.Jobs.Start(ctx, steps, format + " " + plan.Items[0].Label, plan.Items.Count);
        }

        private sealed class ExportOutcome
        {
            public readonly List<Dictionary<string, object>> Files = new List<Dictionary<string, object>>();
            public readonly List<Dictionary<string, object>> Failed = new List<Dictionary<string, object>>();
            public OpException FirstError;
        }

        private static void ExportOne(RequestContext ctx, Document doc, ExportPlan plan, ExportItem item, ExportOutcome outcome)
        {
            string target = item.FullPath(plan.Folder);
            try
            {
                List<string> produced = DocFiles.Native(ctx, "exporting", doc, () => ExportItemNow(ctx, doc, plan, item));
                foreach (string path in produced)
                {
                    outcome.Files.Add(new Dictionary<string, object>
                    {
                        ["path"] = path,
                        ["bytes"] = DocFiles.Bytes(path),
                        ["view"] = item.View == null ? null : item.View.Name
                    });
                }
                if (produced.Count == 0) throw new OpException(ErrorCodes.RevitRefused, "Revit reported success but wrote no file for " + item.Label + ".", new Dictionary<string, object> { ["path"] = target });
            }
            catch (Exception ex)
            {
                OpException mapped = Map(ex, plan.Format, target, item);
                if (outcome.FirstError == null) outcome.FirstError = mapped;
                outcome.Failed.Add(new Dictionary<string, object> { ["view"] = item.View?.Name ?? item.Label, ["code"] = mapped.Code, ["reason"] = mapped.Message });
                ctx.Warn(WarningCodes.PartialResult, item.Label + ": " + mapped.Message, item.View == null ? null : new[] { item.View.Id.Value });
            }
        }

        private static OpResult Finish(RequestContext ctx, ExportPlan plan, ExportOutcome outcome)
        {
            if (outcome.Files.Count == 0 && outcome.FirstError != null) throw outcome.FirstError;
            long bytes = outcome.Files.Sum(f => f["bytes"] is long b ? b : 0);
            var data = new Dictionary<string, object>
            {
                ["format"] = plan.Format,
                ["folder"] = plan.Folder,
                ["files"] = outcome.Files.Take(200).Cast<object>().ToList(),
                ["total"] = outcome.Files.Count,
                ["bytes"] = bytes
            };
            if (outcome.Failed.Count > 0) data["failed"] = outcome.Failed.Take(50).Cast<object>().ToList();
            string summary = "exported " + outcome.Files.Count.ToString(CultureInfo.InvariantCulture) + " " + plan.Format.ToUpperInvariant() + " file(s) to " + plan.Folder +
                             (outcome.Failed.Count > 0 ? " (" + outcome.Failed.Count.ToString(CultureInfo.InvariantCulture) + " failed)" : string.Empty);
            return OpResult.Success(data, summary);
        }

        // ---------------------------------------------------------------------------------------------- planning

        private static ExportPlan BuildPlan(RequestContext ctx, Document doc, string format)
        {
            PayloadReader args = ctx.Args;
            var plan = new ExportPlan
            {
                Format = format,
                Combine = args.Bool("combine", false),
                Setup = args.Str("setup"),
                Color = args.Enum("color", "color", "color", "gray", "bw"),
                Size = args.IntClamped("size", 3000, 64, 15000, (code, text) => ctx.Warn(code, text)),
                Overwrite = args.Bool("overwrite", false)
            };
            string folderArg = args.Str("folder");
            plan.Folder = folderArg != null
                ? DocFiles.FullPath(folderArg, "folder")
                : Path.Combine(ctx.Home?.ExportsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revit-mcp-next", "exports"),
                    DocFiles.SafeFilePart(Path.GetFileNameWithoutExtension(DocFiles.SafeTitle(doc))));
            if (File.Exists(plan.Folder)) throw OpException.InvalidArgs("folder", plan.Folder + " is a file, not a folder");

            if (format != "pdf" && args.Has("combine")) ctx.Warn(WarningCodes.ParamIgnored, "combine applies to pdf only");
            if (format == "image" && args.Has("color") && plan.Color != "color") ctx.Warn(WarningCodes.ParamIgnored, "color: image export uses the view's own graphics; set the view to grayscale/black-and-white for that");
            if (format != "image" && args.Has("size")) ctx.Warn(WarningCodes.ParamIgnored, "size applies to image only");
            if (!(format == "dwg" || format == "dxf" || format == "ifc") && args.Has("setup")) ctx.Warn(WarningCodes.ParamIgnored, "setup applies to dwg, dxf and ifc only");

            string pattern = args.Str("name");
            List<View> views = ResolveViews(ctx, doc, format, args);
            string extension = Extension(format);
            switch (format)
            {
                case "gbxml":
                    plan.Items.Add(new ExportItem { View = null, BaseName = Name(pattern ?? "{doc}", doc, null, 0), Extension = extension });
                    break;
                case "ifc":
                    if (views.Count > 1) ctx.Warn(WarningCodes.ParamIgnored, "ifc exports one file; only the first view is used as the visibility filter");
                    plan.Items.Add(new ExportItem { View = views.FirstOrDefault(), BaseName = Name(pattern ?? (views.Count > 0 ? "{doc} - {name}" : "{doc}"), doc, views.FirstOrDefault(), 0), Extension = extension });
                    break;
                case "nwc" when views.Count == 0:
                    // No 3D view given and none active: the whole model (NavisworksExportScope.Model).
                    plan.Items.Add(new ExportItem { View = null, BaseName = Name(pattern ?? "{doc}", doc, null, 0), Extension = extension });
                    break;
                case "pdf" when plan.Combine:
                    plan.Items.Add(new ExportItem { View = null, Views = views, BaseName = Name(pattern ?? (views.Count == 1 ? DefaultPattern(views[0]) : "{doc}"), doc, views.Count == 1 ? views[0] : null, 0), Extension = extension });
                    break;
                default:
                    for (int index = 0; index < views.Count; index++)
                    {
                        View view = views[index];
                        plan.Items.Add(new ExportItem { View = view, BaseName = Name(pattern ?? DefaultPattern(view), doc, view, index), Extension = extension });
                    }
                    break;
            }

            // Unique names inside the batch.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ExportItem item in plan.Items)
            {
                string name = item.BaseName;
                for (int n = 2; !seen.Add(name + item.Extension); n++) name = item.BaseName + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                item.BaseName = name;
            }
            if (plan.Items.Count == 0) throw OpException.InvalidArgs("views", "selects nothing to export");
            return plan;
        }

        private static List<View> ResolveViews(RequestContext ctx, Document doc, string format, PayloadReader args)
        {
            List<string> requested = args.Strs("views");
            var views = new List<View>();
            if (requested.Count == 0)
            {
                if (format == "gbxml") return views;
                if (format == "ifc") return views;
                View active = ctx.UiDoc?.ActiveView ?? SafeActiveView(doc);
                if (format == "nwc")
                {
                    if (active is View3D active3D && !active3D.IsTemplate) views.Add(active3D);
                    return views;
                }
                if (active == null) throw OpException.InvalidArgs("views", "is required: the document has no active view", new Dictionary<string, object> { ["format"] = format, ["views"] = new List<object> { "all_sheets" } });
                views.Add(active);
            }
            else
            {
                if (format == "gbxml") ctx.Warn(WarningCodes.ParamIgnored, "views: gbxml always exports the whole model");
                var seen = new HashSet<long>();
                foreach (string entry in requested)
                {
                    foreach (View view in Expand(ctx, doc, entry))
                    {
                        if (seen.Add(view.Id.Value)) views.Add(view);
                    }
                }
                if (format == "gbxml") return new List<View>();
            }
            foreach (View view in views) Validate(format, view);
            return views;
        }

        private static IEnumerable<View> Expand(RequestContext ctx, Document doc, string entry)
        {
            string text = entry.Trim();
            if (text.Equals("all_sheets", StringComparison.OrdinalIgnoreCase) || text.Equals("all sheets", StringComparison.OrdinalIgnoreCase))
            {
                List<View> sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder && !s.IsTemplate)
                    .OrderBy(s => s.SheetNumber, NaturalComparer.Instance)
                    .Cast<View>().ToList();
                if (sheets.Count == 0) throw new OpException(ErrorCodes.NotFound, "'" + doc.Title + "' has no sheets.", new Dictionary<string, object> { ["param"] = "views", ["kind"] = "sheet", ["value"] = "all_sheets", ["candidates"] = new List<object>() });
                return sheets;
            }
            if (text.StartsWith("set:", StringComparison.OrdinalIgnoreCase))
            {
                string name = text.Substring(4).Trim();
                List<ViewSheetSet> sets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().ToList();
                ViewSheetSet set = Resolve.Single(sets, s => s.Name, name, "views", "sheet set");
                var members = new List<View>();
                foreach (View view in set.Views) members.Add(view);
                return members.OrderBy(v => v is ViewSheet sheet ? sheet.SheetNumber : v.Name, NaturalComparer.Instance);
            }
            return new[] { Resolve.View(doc, text, "views", ctx.UiDoc) };
        }

        private static void Validate(string format, View view)
        {
            if (view.IsTemplate) throw InvalidView(view, "is a view template");
            switch (format)
            {
                case "csv":
                    if (!(view is ViewSchedule)) throw InvalidView(view, "is not a schedule; csv exports schedules (use read_schedule to read one)");
                    return;
                case "fbx":
                case "nwc":
                    if (!(view is View3D)) throw InvalidView(view, "is not a 3D view; " + format + " exports 3D views");
                    return;
                case "ifc":
                    return;
                default:
                    if (view is ViewSchedule && format != "pdf") throw InvalidView(view, "is a schedule; export it with format csv or place it on a sheet");
                    if (!SafeCanPrint(view)) throw InvalidView(view, "cannot be printed or exported (browser, internal or system view)");
                    return;
            }
        }

        private static OpException InvalidView(View view, string reason)
        {
            return OpException.InvalidArgs("views", "'" + view.Name + "' " + reason);
        }

        private static string DefaultPattern(View view)
        {
            return view is ViewSheet ? "{number} - {name}" : "{name}";
        }

        private static string Name(string pattern, Document doc, View view, int index)
        {
            string number = view is ViewSheet sheet ? sheet.SheetNumber : string.Empty;
            string name = view?.Name ?? string.Empty;
            string text = pattern
                .Replace("{doc}", Path.GetFileNameWithoutExtension(DocFiles.SafeTitle(doc)))
                .Replace("{name}", name)
                .Replace("{number}", number)
                .Replace("{type}", view == null ? string.Empty : view.ViewType.ToString())
                .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Replace("{n}", (index + 1).ToString(CultureInfo.InvariantCulture));
            text = text.Trim().Trim('-', ' ', '_');
            if (text.Length == 0) text = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(DocFiles.SafeTitle(doc));
            string safe = DocFiles.SafeFilePart(text);
            return safe.Length > 150 ? safe.Substring(0, 150).TrimEnd() : safe;
        }

        private static string SuggestPattern(RequestContext ctx, ExportPlan plan)
        {
            string current = ctx.Args.Str("name");
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(current)) return current + " " + stamp;
            if (plan.Items.Count == 1 && plan.Items[0].View == null) return "{doc} " + stamp;
            return plan.Items.Any(i => i.View is ViewSheet) ? "{number} - {name} " + stamp : "{name} " + stamp;
        }

        private static string Extension(string format)
        {
            switch (format)
            {
                case "image": return ".png";
                case "gbxml": return ".xml";
                default: return "." + format;
            }
        }

        // ---------------------------------------------------------------------------------------------- exporters

        private static void CheckExporter(string format)
        {
            bool available = true;
            try
            {
                switch (format)
                {
                    case "dwg": available = OptionalFunctionalityUtils.IsDWGExportAvailable(); break;
                    case "dxf": available = OptionalFunctionalityUtils.IsDXFExportAvailable(); break;
                    case "ifc": available = OptionalFunctionalityUtils.IsIFCAvailable(); break;
                    case "nwc": available = OptionalFunctionalityUtils.IsNavisworksExporterAvailable(); break;
                    case "fbx": available = OptionalFunctionalityUtils.IsFBXExportAvailable(); break;
                }
            }
            catch
            {
                available = true;
            }
            if (!available)
            {
                throw new OpException(ErrorCodes.ExporterMissing, "The " + format.ToUpperInvariant() + " exporter is not installed in this Revit.",
                    new Dictionary<string, object> { ["format"] = format.ToUpperInvariant() });
            }
        }

        private static List<string> ExportItemNow(RequestContext ctx, Document doc, ExportPlan plan, ExportItem item)
        {
            string folder = plan.Folder;
            string target = item.FullPath(folder);
            bool replace = File.Exists(target);
            switch (plan.Format)
            {
                case "pdf":
                {
                    var options = new PDFExportOptions
                    {
                        Combine = true,
                        FileName = item.BaseName,
                        ColorDepth = plan.Color == "gray" ? ColorDepthType.GrayScale : plan.Color == "bw" ? ColorDepthType.BlackLine : ColorDepthType.Color,
                        PaperFormat = ExportPaperFormat.Default,
                        ZoomType = ZoomType.Zoom,
                        ZoomPercentage = 100,
                        HideCropBoundaries = true,
                        HideScopeBoxes = true,
                        HideReferencePlane = true,
                        HideUnreferencedViewTags = true,
                        StopOnError = false
                    };
                    List<ElementId> ids = (item.Views ?? new List<View> { item.View }).Select(v => v.Id).ToList();
                    DeleteForReplace(target, replace);
                    if (!doc.Export(folder, ids, options)) throw new OpException(ErrorCodes.RevitRefused, "Revit did not export " + item.Label + " to PDF.", new Dictionary<string, object> { ["apiMessage"] = "Export returned false" });
                    return Existing(target);
                }
                case "dwg":
                {
                    DWGExportOptions options = DwgOptions(doc, plan.Setup);
                    options.MergedViews = true;
                    DeleteForReplace(target, replace);
                    if (!doc.Export(folder, item.BaseName, new List<ElementId> { item.View.Id }, options)) throw new OpException(ErrorCodes.RevitRefused, "Revit did not export " + item.Label + " to DWG.", new Dictionary<string, object> { ["apiMessage"] = "Export returned false" });
                    return Existing(target);
                }
                case "dxf":
                {
                    DXFExportOptions options = DxfOptions(doc, plan.Setup);
                    DeleteForReplace(target, replace);
                    if (!doc.Export(folder, item.BaseName, new List<ElementId> { item.View.Id }, options)) throw new OpException(ErrorCodes.RevitRefused, "Revit did not export " + item.Label + " to DXF.", new Dictionary<string, object> { ["apiMessage"] = "Export returned false" });
                    return Existing(target);
                }
                case "ifc":
                {
                    var options = new IFCExportOptions { FileVersion = IfcVersion(plan.Setup) };
                    if (item.View != null) options.FilterViewId = item.View.Id;
                    options.ExportBaseQuantities = true;
                    DeleteForReplace(target, replace);
                    bool ok;
                    using (var temp = new TempScope(doc, Naming.Purposes.ExportIfc))
                    {
                        ok = temp.Run(() => doc.Export(folder, item.BaseName, options), out TxResult _);
                    }
                    if (!ok) throw new OpException(ErrorCodes.RevitRefused, "Revit did not export the model to IFC.", new Dictionary<string, object> { ["apiMessage"] = "Export returned false" });
                    return Existing(target);
                }
                case "nwc":
                {
                    var options = new NavisworksExportOptions
                    {
                        ExportScope = item.View != null ? NavisworksExportScope.View : NavisworksExportScope.Model,
                        ExportLinks = false,
                        ExportRoomGeometry = false,
                        ConvertElementProperties = true
                    };
                    if (item.View != null) options.ViewId = item.View.Id;
                    DeleteForReplace(target, replace);
                    doc.Export(folder, item.BaseName, options);
                    return Existing(target);
                }
                case "image":
                    return ExportImage(doc, plan, item, target, replace);
                case "csv":
                {
                    var options = new ViewScheduleExportOptions
                    {
                        FieldDelimiter = ",",
                        TextQualifier = ExportTextQualifier.DoubleQuote,
                        ColumnHeaders = ExportColumnHeaders.OneRow,
                        HeadersFootersBlanks = false,
                        Title = false
                    };
                    DeleteForReplace(target, replace);
                    ((ViewSchedule)item.View).Export(folder, item.BaseName + item.Extension, options);
                    return Existing(target);
                }
                case "fbx":
                {
                    var set = new ViewSet();
                    set.Insert(item.View);
                    DeleteForReplace(target, replace);
                    if (!doc.Export(folder, item.BaseName, set, new FBXExportOptions { StopOnError = false })) throw new OpException(ErrorCodes.RevitRefused, "Revit did not export " + item.Label + " to FBX.", new Dictionary<string, object> { ["apiMessage"] = "Export returned false" });
                    return Existing(target);
                }
                case "gbxml":
                {
                    var options = new GBXMLExportOptions();
#if REVIT2026_OR_GREATER
                    // Revit 2026+ exports the energy model of the document's energy settings (no per-export model type).
#else
                    bool spatial = HasPlacedSpatialElements(doc);
                    options.ExportEnergyModelType = spatial ? ExportEnergyModelType.SpatialElement : ExportEnergyModelType.BuildingElement;
                    if (!spatial) ctx.Notice("GBXML_BUILDING_ELEMENTS", "no placed rooms or spaces: the energy model is built from building elements (slower)");
#endif
                    DeleteForReplace(target, replace);
                    if (!doc.Export(folder, item.BaseName, options)) throw new OpException(ErrorCodes.RevitRefused, "Revit did not export gbXML.", new Dictionary<string, object> { ["apiMessage"] = "Export returned false" });
                    return Existing(target);
                }
                default:
                    throw new OpException(ErrorCodes.UnsupportedOp, "Unknown export format " + plan.Format + ".");
            }
        }

        /// <summary>Revit names image files itself; export into a fresh temp folder and move the single result.</summary>
        private static List<string> ExportImage(Document doc, ExportPlan plan, ExportItem item, string target, bool replace)
        {
            string temp = Path.Combine(plan.Folder, ".rmn-image-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            try
            {
                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(temp, "image"),
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = plan.Size,
                    FitDirection = FitDirectionType.Horizontal,
                    ImageResolution = ImageResolution.DPI_300,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ShouldCreateWebSite = false
                };
                options.SetViewsAndSheets(new List<ElementId> { item.View.Id });
                doc.ExportImage(options);
                string produced = Directory.GetFiles(temp).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (produced == null) return new List<string>();
                DeleteForReplace(target, replace);
                File.Move(produced, target);
                return new List<string> { target };
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        private static DWGExportOptions DwgOptions(Document doc, string setup)
        {
            if (string.IsNullOrWhiteSpace(setup)) return ActiveSetupOr(doc, name => DWGExportOptions.GetPredefinedOptions(doc, name)) ?? new DWGExportOptions();
            DWGExportOptions options = DWGExportOptions.GetPredefinedOptions(doc, SetupName(doc, setup));
            return options ?? throw OpException.NotFound("setup", "DWG export setup", setup, SetupNames(doc));
        }

        private static DXFExportOptions DxfOptions(Document doc, string setup)
        {
            if (string.IsNullOrWhiteSpace(setup)) return ActiveSetupOr(doc, name => DXFExportOptions.GetPredefinedOptions(doc, name)) ?? new DXFExportOptions();
            DXFExportOptions options = DXFExportOptions.GetPredefinedOptions(doc, SetupName(doc, setup));
            return options ?? throw OpException.NotFound("setup", "DXF export setup", setup, SetupNames(doc));
        }

        private static T ActiveSetupOr<T>(Document doc, Func<string, T> load) where T : class
        {
            try
            {
                ExportDWGSettings active = ExportDWGSettings.GetActivePredefinedSettings(doc);
                return active == null ? null : load(active.Name);
            }
            catch
            {
                return null;
            }
        }

        private static string SetupName(Document doc, string setup)
        {
            List<string> names = SetupNames(doc);
            string exact = names.FirstOrDefault(n => string.Equals(n, setup.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            throw OpException.NotFound("setup", "export setup", setup, Resolve.Closest(setup, names));
        }

        private static List<string> SetupNames(Document doc)
        {
            try { return ExportDWGSettings.ListNames(doc).ToList(); } catch { return new List<string>(); }
        }

        /// <summary>IFC setup names of the Revit dialog mapped to IFC schema versions.</summary>
        private static IFCVersion IfcVersion(string setup)
        {
            string text = (setup ?? string.Empty).Replace(" ", string.Empty).Replace("_", string.Empty).Replace(".", string.Empty).ToLowerInvariant();
            if (text.Length == 0) return IFCVersion.IFC2x3CV2;
            if (text.Contains("4x3")) return IFCVersion.IFC4x3;
            if (text.Contains("ifc4") && text.Contains("design")) return IFCVersion.IFC4DTV;
            if (text.Contains("ifc4") && (text.Contains("reference") || text.Contains("rv"))) return IFCVersion.IFC4RV;
            if (text.Contains("ifc4")) return IFCVersion.IFC4;
            if (text.Contains("cobie")) return IFCVersion.IFCCOBIE;
            if (text.Contains("bfm") || text.Contains("basicfm")) return IFCVersion.IFC2x3BFM;
            if (text.Contains("2x3") && text.Contains("fm")) return IFCVersion.IFC2x3FM;
            if (text.Contains("2x3") && (text.Contains("coordination") || text.Contains("cv"))) return IFCVersion.IFC2x3CV2;
            if (text.Contains("2x3")) return IFCVersion.IFC2x3;
            if (text.Contains("2x2")) return IFCVersion.IFC2x2;
            throw OpException.NotFound("setup", "IFC setup", setup, new[] { "IFC2x3 Coordination View 2.0", "IFC2x3 Basic FM Handover View", "IFC4 Reference View", "IFC4 Design Transfer View", "IFC4x3" });
        }

        private static bool HasPlacedSpatialElements(Document doc)
        {
            try
            {
                return new FilteredElementCollector(doc).OfClass(typeof(SpatialElement)).Cast<SpatialElement>().Any(e => e.Location != null && e.Area > 0);
            }
            catch
            {
                return false;
            }
        }

        private static void DeleteForReplace(string target, bool replace)
        {
            if (!replace) return;
            try
            {
                File.Delete(target);
            }
            catch (Exception ex)
            {
                throw new OpException(ErrorCodes.RevitRefused, Path.GetFileName(target) + " cannot be replaced: " + ex.Message + " (is it open in another program?)",
                    new Dictionary<string, object> { ["path"] = target, ["reason"] = "file_locked", ["apiMessage"] = ex.Message });
            }
        }

        private static List<string> Existing(string target)
        {
            return File.Exists(target) ? new List<string> { target } : new List<string>();
        }

        private static OpException Map(Exception ex, string format, string target, ExportItem item)
        {
            if (ex is OpException op) return op;
            string what = "Exporting " + item.Label + " to " + format.ToUpperInvariant();
            if (ex is Autodesk.Revit.Exceptions.OptionalFunctionalityNotAvailableException)
            {
                return new OpException(ErrorCodes.ExporterMissing, "The " + format.ToUpperInvariant() + " exporter is not available in this Revit.",
                    new Dictionary<string, object> { ["format"] = format.ToUpperInvariant() });
            }
            return DocFiles.MapFileException(ex, target, what);
        }

        private static string Describe(View view)
        {
            return view is ViewSheet sheet ? "sheet " + sheet.SheetNumber + " - " + sheet.Name : "'" + view.Name + "'";
        }

        private static bool SafeCanPrint(View view)
        {
            try
            {
                if (view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser || view.ViewType == ViewType.Internal || view.ViewType == ViewType.Undefined) return false;
                return view.CanBePrinted;
            }
            catch
            {
                return false;
            }
        }

        private static View SafeActiveView(Document doc)
        {
            try { return doc.ActiveView; } catch { return null; }
        }

        /// <summary>Natural order for sheet numbers (A2 before A10).</summary>
        private sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new NaturalComparer();

            public int Compare(string x, string y)
            {
                if (x == null || y == null) return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                int i = 0, j = 0;
                while (i < x.Length && j < y.Length)
                {
                    if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                    {
                        int si = i, sj = j;
                        while (i < x.Length && char.IsDigit(x[i])) i++;
                        while (j < y.Length && char.IsDigit(y[j])) j++;
                        string a = x.Substring(si, i - si).TrimStart('0'), b = y.Substring(sj, j - sj).TrimStart('0');
                        int byLength = a.Length.CompareTo(b.Length);
                        if (byLength != 0) return byLength;
                        int byValue = string.CompareOrdinal(a, b);
                        if (byValue != 0) return byValue;
                    }
                    else
                    {
                        int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                        if (c != 0) return c;
                        i++;
                        j++;
                    }
                }
                return (x.Length - i).CompareTo(y.Length - j);
            }
        }
    }
}
