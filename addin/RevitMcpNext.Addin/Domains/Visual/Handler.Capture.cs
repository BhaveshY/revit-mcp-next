using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// capture (SPEC §10.1, D3 §3.1): an image of a view or sheet, or of elements in a temporary 3D box.
    /// Temporary changes (focus view, region crop, highlight, annotations off, style, orient) follow the normative
    /// sequence TempScope("MCP temp capture") → inner transaction configures → commit inner → ExportImage with only the
    /// group open → roll the group back. If Revit refuses to export with the group open (V1), the add-in remembers it and
    /// uses fallback B: a committed temporary copy ("MCP temp capture") that is exported and deleted again.
    /// Sizing, JPEG/PNG choice, the 1.1 MB cap and the compare diff are raster work in <see cref="Raster"/>.
    /// </summary>
    internal static class CaptureHandler
    {
        private const long MaxFileBytes = 1_100_000;
        private const long PreferJpegAboveBytes = 400 * 1024;
        private const int DiffTolerance = 24;
        private const int MaxSheetExportPixels = 4000;
        private static bool _groupExportRefused;

        [Op("capture")]
        public static OpResult Capture(RequestContext ctx)
        {
            Document doc = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document to capture.");
            McpHome home = ctx.Home ?? throw new OpException(ErrorCodes.InternalError, "The add-in home is not initialized.");
            CaptureStore.EnsureRetention(home);
            CaptureOptions o = CaptureOptions.Read(ctx);

            View source = null;
            List<Element> focus = null;
            if (o.Focus)
            {
                focus = ViewKit.SelectedElements(ctx, ctx.Args, doc);
                if (ctx.Args.Has("view")) ctx.Warn(WarningCodes.ParamIgnored, "view is ignored when ids/from focus elements in a temporary 3D view");
            }
            else
            {
                source = Resolve.View(doc, ctx.Args.Str("view") ?? "active", "view", ctx.UiDoc);
                CheckExportable(source);
                if (o.Orient != null && !(source is View3D)) ctx.Warn(WarningCodes.ParamIgnored, "orient applies to 3D views; '" + source.Name + "' is " + source.ViewType);
            }

            Raster before = null;
            Dictionary<string, object> beforeMeta = null;
            if (o.CompareFile != null) before = LoadCompare(ctx, home, o, source, out beforeMeta);

            string cacheKey = before == null ? CacheKey(ctx, source, focus, o) : null;
            if (cacheKey != null)
            {
                CachedCapture hit = CaptureStore.Lookup(cacheKey);
                if (hit != null)
                {
                    var cachedData = new Dictionary<string, object>(hit.Data) { ["cached"] = true };
                    return ctx.Ok(cachedData, hit.Summary).WithFile(hit.Path, hit.Mime, hit.Width, hit.Height, hit.Bytes, hit.Meta).WithCacheHit();
                }
            }

            string folder = CaptureStore.NewFolder(home);
            try
            {
                ExportInfo info = Export(ctx, doc, source, focus, o, folder);
                return Finish(ctx, doc, source, o, folder, info, before, beforeMeta, cacheKey);
            }
            catch
            {
                TryDelete(folder);
                throw;
            }
        }

        // ------------------------------------------------------------------------------------------------- export

        private static ExportInfo Export(RequestContext ctx, Document doc, View source, List<Element> focus, CaptureOptions o, string folder)
        {
            bool needsTemp = o.Focus || NeedsChanges(source, o);
            if (!needsTemp)
            {
                var plain = new ExportInfo { View = source };
                Describe(source, plain);
                ExportView(ctx, doc, plain, o, folder);
                return plain;
            }

            if (!_groupExportRefused)
            {
                try
                {
                    using (var temp = new TempScope(doc, Naming.Purposes.Capture))
                    {
                        var info = new ExportInfo();
                        // Workshared: change a temporary copy instead of the view itself, so nothing is borrowed.
                        bool copyInTemp = doc.IsWorkshared && CanCopy(source);
                        TxResult tx = temp.Run(() => Configure(ctx, doc, source, focus, o, info, copy: copyInTemp));
                        if (!tx.Committed) throw tx.ToException("capture");
                        Describe(info.View, info);
                        ExportView(ctx, doc, info, o, folder);
                        return info;
                    }
                }
                catch (GroupExportRefusedException ex)
                {
                    DiagnosticsLogger.Warn("capture", "ExportImage refused with a TransactionGroup open (V1); trying fallback B: " + ex.Message);
                    foreach (string file in Directory.GetFiles(folder)) TryDeleteFile(file);
                    ExportInfo fallback = ExportWithCommittedCopy(ctx, doc, source, focus, o, folder);
                    _groupExportRefused = true;
                    return fallback;
                }
            }
            return ExportWithCommittedCopy(ctx, doc, source, focus, o, folder);
        }

        /// <summary>Fallback B: configure a committed temporary copy, export it, delete it.</summary>
        private static ExportInfo ExportWithCommittedCopy(RequestContext ctx, Document doc, View source, List<Element> focus, CaptureOptions o, string folder)
        {
            var info = new ExportInfo();
            RunTempCommitted(doc, () => Configure(ctx, doc, source, focus, o, info, copy: true));
            try
            {
                Describe(info.View, info);
                ExportView(ctx, doc, info, o, folder);
                return info;
            }
            finally
            {
                if (info.TempViewId != null)
                {
                    try
                    {
                        RunTempCommitted(doc, () => doc.Delete(info.TempViewId));
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLogger.Warn("capture", "Could not delete the temporary capture view: " + ex.Message);
                    }
                }
            }
        }

        private static void RunTempCommitted(Document doc, Action action)
        {
            var capture = new FailureCapture();
            using (var transaction = new Transaction(doc, Naming.Temp(Naming.Purposes.Capture)))
            {
                FailureCapture.Configure(transaction, capture);
                if (transaction.Start() != TransactionStatus.Started) throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start the capture transaction.");
                try
                {
                    action();
                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        throw new OpException(ErrorCodes.RevitTransactionRolledBack, "Revit rolled back the temporary capture view.",
                            new Dictionary<string, object> { ["failures"] = capture.Errors.Select(f => f.ToWire()).ToList() });
                    }
                }
                catch
                {
                    TempScope.RollBackQuietly(transaction);
                    throw;
                }
            }
        }

        /// <summary>Applies the temporary changes; <paramref name="copy"/> works on a duplicate (fallback B).</summary>
        private static void Configure(RequestContext ctx, Document doc, View source, List<Element> focus, CaptureOptions o, ExportInfo info, bool copy)
        {
            if (o.Focus)
            {
                BoundingBoxXYZ box = ViewKit.UnionBox(focus, Units.MmToFt(o.MarginMm));
                if (box == null) throw OpException.InvalidArgs(ctx.Args.Has("ids") ? "ids" : "from", "none of these elements has a 3D extent to capture");
                ViewFamilyType type = ViewKit.FamilyType(doc, ViewFamily.ThreeDimensional, null, "view", ElementTypeGroup.ViewType3D);
                View3D view = View3D.CreateIsometric(doc, type.Id);
                view.SetSectionBox(box);
                view.IsSectionBoxActive = true;
                ViewKit.Orient(view, o.Orient ?? "iso_se", (box.Min + box.Max) / 2, box.Min.DistanceTo(box.Max) * 1.5);
                ViewKit.SetStyle(view, o.Style ?? DisplayStyle.ShadingWithEdges);
                try { view.DetailLevel = ViewDetailLevel.Fine; } catch { /* keep the type default */ }
                if (!(o.Annotations ?? false)) view.AreAnnotationCategoriesHidden = true;
                Highlight(ctx, doc, view, o);
                info.View = view;
                info.TempViewId = copy ? view.Id : null;
                info.Title = "3D focus on " + focus.Count.ToString(CultureInfo.InvariantCulture) + " element" + (focus.Count == 1 ? string.Empty : "s");
                info.IsFocus = true;
                return;
            }

            View target = source;
            if (source is ViewSheet sheet)
            {
                if (copy && o.Highlight.Count > 0) ctx.Warn(WarningCodes.ParamIgnored, "highlight on sheets is not available in this Revit (fallback capture mode)");
                else if (o.Highlight.Count > 0)
                {
                    foreach (ElementId id in sheet.GetAllPlacedViews())
                    {
                        if (doc.GetElement(id) is View placed && Safe(() => placed.AreGraphicsOverridesAllowed())) Highlight(ctx, doc, placed, o);
                    }
                }
                info.View = sheet;
                return;
            }

            if (copy)
            {
                ViewDuplicateOption option = source.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing) ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate;
                if (!source.CanViewBeDuplicated(option))
                {
                    throw new OpException(ErrorCodes.UnsupportedOp, "'" + source.Name + "' cannot be copied for a temporary capture in this Revit; capture it without region/style/highlight/annotations.");
                }
                target = doc.GetElement(source.Duplicate(option)) as View;
                info.TempViewId = target.Id;
            }

            bool styleLocked = o.Style.HasValue && ViewKit.TemplateControls(target, BuiltInParameter.MODEL_GRAPHICS_STYLE);
            bool annotationLocked = o.Annotations == false && ViewKit.TemplateControls(target, BuiltInParameter.VIS_GRAPHICS_ANNOTATION);
            if (styleLocked || annotationLocked) DetachTemplate(target);
            if (o.Region != null) ApplyRegion(ctx, target, o.Region);
            if (o.Style.HasValue) ViewKit.SetStyle(target, o.Style.Value);
            if (o.Orient != null && target is View3D view3D)
            {
                if (view3D.IsLocked) ctx.Warn(WarningCodes.ParamIgnored, "'" + source.Name + "' is a locked 3D view, so orient was ignored");
                else ViewKit.Orient(view3D, o.Orient);
            }
            if (o.Annotations == false) target.AreAnnotationCategoriesHidden = true;
            Highlight(ctx, doc, target, o);
            info.View = target;
            info.Title = ViewKit.Title(source);
        }

        private static void DetachTemplate(View view)
        {
            View template = ViewKit.TemplateOf(view);
            if (template == null) return;
            view.ApplyViewTemplateParameters(template);
            view.ViewTemplateId = ElementId.InvalidElementId;
        }

        private static void ApplyRegion(RequestContext ctx, View view, double[] ft)
        {
            if (view is View3D view3D)
            {
                double z0, z1;
                if (ft.Length == 6)
                {
                    z0 = Math.Min(ft[2], ft[5]);
                    z1 = Math.Max(ft[2], ft[5]);
                }
                else
                {
                    BoundingBoxXYZ current = view3D.IsSectionBoxActive ? view3D.GetSectionBox() : null;
                    if (current != null)
                    {
                        Transform t = current.Transform ?? Transform.Identity;
                        XYZ a = t.OfPoint(current.Min), b = t.OfPoint(current.Max);
                        z0 = Math.Min(a.Z, b.Z);
                        z1 = Math.Max(a.Z, b.Z);
                    }
                    else
                    {
                        List<double> levels = new FilteredElementCollector(view.Document).OfClass(typeof(Level)).Cast<Level>().Select(l => l.ProjectElevation).ToList();
                        z0 = (levels.Count > 0 ? levels.Min() : 0) - Units.MmToFt(3000);
                        z1 = (levels.Count > 0 ? levels.Max() : 0) + Units.MmToFt(6000);
                    }
                }
                double x0 = ft[0], y0 = ft[1], x1 = ft.Length == 6 ? ft[3] : ft[2], y1 = ft.Length == 6 ? ft[4] : ft[3];
                view3D.SetSectionBox(ViewKit.ModelBox(new[] { x0, y0, z0, x1, y1, z1 }));
                view3D.IsSectionBoxActive = true;
                return;
            }
            if (view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend)
            {
                throw OpException.InvalidArgs("region", "does not apply to " + view.ViewType + " views (they have no crop); capture the whole view");
            }
            double a0 = ft[0], b0 = ft[1], a1 = ft.Length == 6 ? ft[3] : ft[2], b1 = ft.Length == 6 ? ft[4] : ft[3];
            if (Math.Abs(a1 - a0) < 1e-3 || Math.Abs(b1 - b0) < 1e-3) throw OpException.InvalidArgs("region", "must have a positive width and height");
            view.CropBox = ViewKit.CropFor(view, a0, b0, a1, b1);
            view.CropBoxActive = true;
            Parameter annotationCrop = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
            if (annotationCrop != null && !annotationCrop.IsReadOnly && annotationCrop.AsInteger() != 0) annotationCrop.Set(0);
        }

        private static void Highlight(RequestContext ctx, Document doc, View view, CaptureOptions o)
        {
            if (o.Highlight.Count == 0) return;
            if (!Safe(() => view.AreGraphicsOverridesAllowed()))
            {
                ctx.Warn(WarningCodes.ParamIgnored, "'" + view.Name + "' takes no graphic overrides, so highlight was ignored");
                return;
            }
            OverrideGraphicSettings red = GraphicsKit.Highlight(doc, new Color(230, 0, 0));
            foreach (ElementId id in o.Highlight)
            {
                try { view.SetElementOverrides(id, red); } catch (Autodesk.Revit.Exceptions.ArgumentException) { /* not in this view */ }
            }
        }

        private static void ExportView(RequestContext ctx, Document doc, ExportInfo info, CaptureOptions o, string folder)
        {
            double aspect = info.Aspect ?? 1.0;
            int pixels = SizeSpec.For(o.Size).LongEdgeFor(aspect);
            if (info.IsSheet && o.Region != null && info.SheetOutlineMm != null)
            {
                // A sheet region is cut from the sheet image afterwards, so export the sheet larger by the zoom factor.
                double[] outline = info.SheetOutlineMm;
                double w = Math.Abs(Units.FtToMm(o.Region.Length == 6 ? o.Region[3] : o.Region[2]) - Units.FtToMm(o.Region[0]));
                double h = Math.Abs(Units.FtToMm(o.Region.Length == 6 ? o.Region[4] : o.Region[3]) - Units.FtToMm(o.Region[1]));
                double zoom = Math.Min((outline[2] - outline[0]) / Math.Max(w, 1), (outline[3] - outline[1]) / Math.Max(h, 1));
                pixels = (int)Math.Min(MaxSheetExportPixels, pixels * Math.Max(1, zoom));
            }
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixels,
                FitDirection = aspect >= 1 ? FitDirectionType.Horizontal : FitDirectionType.Vertical,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                FilePath = Path.Combine(folder, "export")
            };
            options.SetViewsAndSheets(new List<ElementId> { info.View.Id });
            try
            {
                doc.ExportImage(options);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) when (TempScope.IsActive)
            {
                throw new GroupExportRefusedException(ex.Message);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw MapExportFailure(ctx, doc, ex.Message);
            }
            string raw = Directory.GetFiles(folder)
                .Where(f => !string.Equals(Path.GetFileName(f), "meta.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
            if (raw == null) throw MapExportFailure(ctx, doc, "Revit exported no image file");
            info.RawPath = raw;
        }

        private static OpException MapExportFailure(RequestContext ctx, Document doc, string message)
        {
            string lower = (message ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("directx") || lower.Contains("graphics") || lower.Contains("hardware") || lower.Contains("opengl") || lower.Contains("render"))
            {
                return new OpException(ErrorCodes.CaptureGpuUnavailable, "Revit could not render the image (" + message + "); this happens over RDP or with a minimized Revit.");
            }
            if (!ctx.DocIsActive)
            {
                // V3 fallback: views of a background document are exported after activating it.
                return new OpException(ErrorCodes.NeedsActiveDoc, "Revit did not export the view of '" + doc.Title + "' while it is in the background (" + message + ").",
                    new Dictionary<string, object> { ["doc"] = doc.Title, ["rid"] = ctx.Rid });
            }
            return new OpException(ErrorCodes.RevitRefused, "Revit did not export the image: " + message, new Dictionary<string, object> { ["apiMessage"] = message });
        }

        /// <summary>Aspect (width/height) and model mapping of the exported area, read while the temp changes exist.</summary>
        private static void Describe(View view, ExportInfo info)
        {
            if (info.Title == null) info.Title = ViewKit.Title(view);
            info.Style = Safe(() => view.DisplayStyle, DisplayStyle.Undefined);
            if (view is ViewSheet sheet)
            {
                info.IsSheet = true;
                double[] outline = GetViewHandler.SheetOutlineMm(sheet);
                if (outline != null)
                {
                    info.SheetOutlineMm = outline;
                    info.BoundsMm = outline;
                    info.Aspect = (outline[2] - outline[0]) / (outline[3] - outline[1]);
                }
                return;
            }
            bool cropped = Safe(() => view.CropBoxActive);
            if (cropped) info.Aspect = ViewKit.CropAspect(view);
            if (cropped && ViewKit.IsPlanLike(view))
            {
                Parameter annotationCrop = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                bool annotationCropOn = annotationCrop != null && annotationCrop.HasValue && annotationCrop.AsInteger() != 0;
                double[] rect = ViewKit.CropRectXY(view);
                if (!annotationCropOn && rect != null) info.BoundsMm = rect.Select(Units.FtToMm).ToArray();
            }
        }

        // -------------------------------------------------------------------------------------------------- finish

        private static OpResult Finish(RequestContext ctx, Document doc, View source, CaptureOptions o, string folder, ExportInfo info,
            Raster before, Dictionary<string, object> beforeMeta, string cacheKey)
        {
            Raster image = Raster.Load(info.RawPath);
            bool shaded = ViewKit.IsShadedStyle(info.Style);
            if (image.IsBlank(out bool black) && black && (shaded || info.IsFocus))
            {
                throw new OpException(ErrorCodes.CaptureGpuUnavailable, "Revit rendered an all-black image of " + info.Title + " (DirectX is not available, e.g. over RDP or with Revit minimized).");
            }

            double[] bounds = info.BoundsMm;
            if (info.IsSheet && o.Region != null)
            {
                if (info.SheetOutlineMm == null) throw OpException.InvalidArgs("region", "the sheet has no outline to map the region onto");
                double[] outline = info.SheetOutlineMm;
                double[] r = o.Region.Select(Units.FtToMm).ToArray();
                double rx0 = Math.Max(outline[0], Math.Min(r[0], r.Length == 6 ? r[3] : r[2]));
                double rx1 = Math.Min(outline[2], Math.Max(r[0], r.Length == 6 ? r[3] : r[2]));
                double ry0 = Math.Max(outline[1], Math.Min(r[1], r.Length == 6 ? r[4] : r[3]));
                double ry1 = Math.Min(outline[3], Math.Max(r[1], r.Length == 6 ? r[4] : r[3]));
                if (rx1 - rx0 < 1 || ry1 - ry0 < 1) throw OpException.InvalidArgs("region", "lies outside the sheet (sheet is " + Fmt(outline[2] - outline[0]) + " x " + Fmt(outline[3] - outline[1]) + " mm)");
                double sx = image.Width / (outline[2] - outline[0]), sy = image.Height / (outline[3] - outline[1]);
                int px0 = (int)Math.Floor((rx0 - outline[0]) * sx), px1 = (int)Math.Ceiling((rx1 - outline[0]) * sx);
                int py0 = (int)Math.Floor((outline[3] - ry1) * sy), py1 = (int)Math.Ceiling((outline[3] - ry0) * sy);
                image = image.Crop(px0, py0, px1 - px0, py1 - py0);
                bounds = new[] { rx0, ry0, rx1, ry1 };
            }

            SizeSpec spec = SizeSpec.For(o.Size);
            image = image.FitLongEdge(spec.LongEdgeFor(image.Aspect));

            Dictionary<string, object> changed = null;
            bool compare = before != null;
            if (compare)
            {
                if (image.Width != before.Width || image.Height != before.Height)
                {
                    double drift = Math.Abs(image.Aspect - before.Aspect) / before.Aspect;
                    if (drift > 0.01)
                    {
                        throw OpException.InvalidArgs("compare", "the views differ in shape (" + image.Width + "x" + image.Height + " vs " + before.Width + "x" + before.Height + "); compare needs the same view, size and region");
                    }
                    image = image.Resize(before.Width, before.Height);
                }
                image = Raster.Diff(before, image, DiffTolerance, out int changedPixels, out int[] bbox);
                changed = new Dictionary<string, object> { ["pixels"] = changedPixels };
                if (bbox != null)
                {
                    changed["bbox_px"] = bbox.Select(v => (object)v).ToList();
                    double[] baseBounds = bounds ?? MetaBounds(beforeMeta);
                    if (baseBounds != null)
                    {
                        double mmpp = (baseBounds[2] - baseBounds[0]) / image.Width;
                        double mmppY = (baseBounds[3] - baseBounds[1]) / image.Height;
                        changed["bbox_model"] = new List<object>
                        {
                            Units.Number(baseBounds[0] + bbox[0] * mmpp, 0), Units.Number(baseBounds[3] - bbox[3] * mmppY, 0),
                            Units.Number(baseBounds[0] + bbox[2] * mmpp, 0), Units.Number(baseBounds[3] - bbox[1] * mmppY, 0)
                        };
                    }
                }
            }

            string ext;
            byte[] bytes = Encode(ref image, o.Format, shaded && !info.IsSheet, compare, out ext);
            string path = Path.Combine(folder, "image." + ext);
            File.WriteAllBytes(path, bytes);
            if (!string.Equals(info.RawPath, path, StringComparison.OrdinalIgnoreCase)) TryDeleteFile(info.RawPath);
            string mime = ext == "jpg" ? "image/jpeg" : "image/png";

            var data = new Dictionary<string, object>
            {
                ["view"] = info.IsFocus ? info.Title : (source is ViewSheet sheet ? sheet.SheetNumber + " - " + sheet.Name : source?.Name),
                ["px"] = new List<object> { image.Width, image.Height },
                ["format"] = ext
            };
            if (source != null) data["view_id"] = source.Id.Value;
            if (bounds != null)
            {
                data["bounds"] = bounds.Select(v => Units.Number(v, 0)).ToList();
                data["mm_per_px"] = Units.Number((bounds[2] - bounds[0]) / image.Width, 2);
            }
            if (info.Style != DisplayStyle.Undefined && !info.IsSheet) data["style"] = ViewKit.StyleName(info.Style);
            if (changed != null) data["changed"] = changed;

            var meta = new Dictionary<string, object>
            {
                ["view"] = data["view"],
                ["view_id"] = source?.Id.Value,
                ["focus"] = info.IsFocus,
                ["size"] = o.Size,
                ["region"] = o.Region?.Select(v => Units.Number(Units.FtToMm(v), 1)).ToList(),
                ["bounds"] = bounds?.Select(v => (object)v).ToList(),
                ["px"] = new List<object> { image.Width, image.Height },
                ["format"] = ext,
                ["generation"] = ctx.Generation,
                ["docKey"] = SafeKey(ctx, doc),
                ["compare"] = compare,
                ["atUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            CaptureStore.WriteMeta(folder, meta);

            string summary = (compare ? "compared " : "captured ") + info.Title + " " + image.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                image.Height.ToString(CultureInfo.InvariantCulture) + " " + ext +
                (compare ? ": " + ((int)changed["pixels"]).ToString("N0", CultureInfo.InvariantCulture) + " changed pixels" : string.Empty);
            if (cacheKey != null)
            {
                CaptureStore.Remember(cacheKey, new CachedCapture
                {
                    Path = path, Mime = mime, Width = image.Width, Height = image.Height, Bytes = bytes.LongLength, Meta = meta, Data = data, Summary = summary
                });
            }
            return ctx.Ok(data, summary).WithFile(path, mime, image.Width, image.Height, bytes.LongLength, meta);
        }

        /// <summary>PNG for drawings, sheets and compare; JPEG q85 for shaded views over 400 KB or format=jpg; cap 1.1 MB.</summary>
        private static byte[] Encode(ref Raster image, string format, bool shaded, bool compare, out string ext)
        {
            byte[] bytes;
            if (format == "jpg" && !compare)
            {
                bytes = image.EncodeJpeg(85);
                ext = "jpg";
            }
            else
            {
                bytes = image.EncodePng();
                ext = "png";
                if (format == "auto" && shaded && !compare && bytes.LongLength > PreferJpegAboveBytes)
                {
                    bytes = image.EncodeJpeg(85);
                    ext = "jpg";
                }
            }
            for (int attempt = 0; bytes.LongLength > MaxFileBytes && attempt < 6; attempt++)
            {
                if (ext == "png" || attempt == 0)
                {
                    bytes = image.EncodeJpeg(80);
                    ext = "jpg";
                    continue;
                }
                image = image.Resize(Math.Max(1, image.Width / 2), Math.Max(1, image.Height / 2));
                bytes = image.EncodeJpeg(80);
            }
            return bytes;
        }

        // ------------------------------------------------------------------------------------------------- helpers

        private static void CheckExportable(View view)
        {
            string kind = null;
            if (view.IsTemplate) kind = "template";
            else if (view.ViewType == ViewType.Schedule || view.ViewType == ViewType.PanelSchedule || view.ViewType == ViewType.ColumnSchedule) kind = "schedule";
            else if (view is ViewSheet sheet && sheet.IsPlaceholder) kind = "placeholder sheet";
            else if (!ViewKit.IsGraphical(view)) kind = view.ViewType.ToString();
            if (kind == null) return;
            throw new OpException(ErrorCodes.CaptureNotExportable, "'" + view.Name + "' is a " + kind + " and cannot be captured as an image.",
                new Dictionary<string, object> { ["viewKind"] = kind, ["view"] = view.Name, ["schedule"] = kind == "schedule" ? view.Name : null });
        }

        private static bool CanCopy(View source)
        {
            if (source == null || source is ViewSheet) return false;
            return Safe(() => source.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing) || source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate));
        }

        private static bool NeedsChanges(View source, CaptureOptions o)
        {
            if (source is ViewSheet) return o.Highlight.Count > 0;
            return o.Region != null || o.Highlight.Count > 0 || o.Annotations == false || o.Style.HasValue || (o.Orient != null && source is View3D);
        }

        private static Raster LoadCompare(RequestContext ctx, McpHome home, CaptureOptions o, View source, out Dictionary<string, object> meta)
        {
            meta = null;
            if (!CaptureStore.IsInsideCaptures(home, o.CompareFile)) throw OpException.InvalidArgs("compare", "the earlier capture must be under " + home.CapturesDir);
            if (!File.Exists(o.CompareFile))
            {
                throw new OpException(ErrorCodes.CaptureGone, "The earlier capture " + (o.Compare ?? string.Empty) + " was cleaned up; capture again first.",
                    new Dictionary<string, object> { ["capture"] = o.Compare });
            }
            meta = CaptureStore.ReadMeta(o.CompareFile);
            if (meta != null && source != null && meta.TryGetValue("view_id", out object id) && id != null &&
                Convert.ToInt64(id, CultureInfo.InvariantCulture) != source.Id.Value)
            {
                throw OpException.InvalidArgs("compare", "the earlier capture shows another view (" + Convert.ToString(meta["view"], CultureInfo.InvariantCulture) + "); compare needs the same view, size and region");
            }
            return Raster.Load(o.CompareFile);
        }

        private static double[] MetaBounds(Dictionary<string, object> meta)
        {
            if (meta == null || !meta.TryGetValue("bounds", out object value)) return null;
            if (!(value is System.Collections.IList list) || list.Count != 4) return null;
            var result = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!PayloadReader.TryNumber(list[i], out result[i])) return null;
            }
            return result;
        }

        private static string CacheKey(RequestContext ctx, View source, List<Element> focus, CaptureOptions o)
        {
            string target = source != null ? "v" + source.Id.Value.ToString(CultureInfo.InvariantCulture)
                : "f" + string.Join(",", focus.Select(e => e.Id.Value).OrderBy(v => v).Select(v => v.ToString(CultureInfo.InvariantCulture)));
            return SafeKey(ctx, ctx.Doc) + "|" + target + "|" + ctx.Generation.ToString(CultureInfo.InvariantCulture) + "|" + o.Signature;
        }

        private static string SafeKey(RequestContext ctx, Document doc)
        {
            try { return ctx.Registry?.GetKey(doc) ?? doc.PathName ?? doc.Title; } catch { return doc.Title; }
        }

        private static string Fmt(double mm) => Math.Round(mm).ToString(CultureInfo.InvariantCulture);

        private static void TryDelete(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { /* retention cleans it later */ }
        }

        private static void TryDeleteFile(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* retention cleans it later */ }
        }

        private static T Safe<T>(Func<T> read, T fallback = default(T))
        {
            try { return read(); } catch { return fallback; }
        }

        private sealed class GroupExportRefusedException : Exception
        {
            public GroupExportRefusedException(string message) : base(message) { }
        }

        private sealed class ExportInfo
        {
            public View View { get; set; }
            public ElementId TempViewId { get; set; }
            public string Title { get; set; }
            public bool IsFocus { get; set; }
            public bool IsSheet { get; set; }
            public DisplayStyle Style { get; set; } = DisplayStyle.Undefined;
            public double? Aspect { get; set; }
            public double[] BoundsMm { get; set; }
            public double[] SheetOutlineMm { get; set; }
            public string RawPath { get; set; }
        }
    }

    /// <summary>The capture size table (SPEC §10.1).</summary>
    internal sealed class SizeSpec
    {
        private SizeSpec(int longEdge, double megapixels)
        {
            LongEdge = longEdge;
            Megapixels = megapixels;
        }

        public int LongEdge { get; }
        public double Megapixels { get; }

        public static SizeSpec For(string size)
        {
            switch (size)
            {
                case "small": return new SizeSpec(768, 0.40);
                case "large": return new SizeSpec(1568, 1.15);
                default: return new SizeSpec(1280, 1.00);
            }
        }

        /// <summary>L_eff = min(L, floor(sqrt(MPcap · long/short))).</summary>
        public int LongEdgeFor(double aspect)
        {
            double ratio = aspect >= 1 ? aspect : 1 / Math.Max(aspect, 1e-6);
            if (double.IsNaN(ratio) || double.IsInfinity(ratio)) ratio = 1;
            return Math.Max(16, Math.Min(LongEdge, (int)Math.Floor(Math.Sqrt(Megapixels * 1_000_000 * ratio))));
        }
    }

    /// <summary>Parsed capture args.</summary>
    internal sealed class CaptureOptions
    {
        public bool Focus { get; private set; }
        public string Size { get; private set; }
        public string Format { get; private set; }
        public double[] Region { get; private set; }
        public string Orient { get; private set; }
        public DisplayStyle? Style { get; private set; }
        public double MarginMm { get; private set; }
        public List<ElementId> Highlight { get; private set; } = new List<ElementId>();
        public bool? Annotations { get; private set; }
        public string Compare { get; private set; }
        public string CompareFile { get; private set; }
        public string Signature { get; private set; }

        public static CaptureOptions Read(RequestContext ctx)
        {
            PayloadReader a = ctx.Args;
            CaptureSettings settings = ctx.Settings?.Capture ?? new CaptureSettings();
            var o = new CaptureOptions
            {
                Focus = a.Has("ids") || a.Has("from"),
                Size = a.Enum("size", settings.Size ?? "medium", "small", "medium", "large"),
                Format = a.Enum("format", settings.Format ?? "auto", "auto", "png", "jpg"),
                Region = a.Has("region") ? a.BoxFt("region") : null,
                Orient = a.Has("orient") ? a.Str("orient").Trim().ToLowerInvariant() : null,
                Style = a.Has("style") ? ViewKit.ParseStyle(a.Str("style")) : (DisplayStyle?)null,
                MarginMm = a.MmRaw("margin", 1000),
                Annotations = a.BoolOpt("annotations"),
                Compare = a.Str("compare"),
                CompareFile = a.Str("compare_file")
            };
            if (o.Orient != null) ViewKit.Forward(o.Orient);
            if (o.MarginMm < 0) throw OpException.InvalidArgs("margin", "must be 0 or more (mm)");
            if (a.Has("highlight"))
            {
                o.Highlight = Resolve.Elements(ctx.Doc, a, "highlight", ctx).Select(e => e.Id).ToList();
            }
            if (o.Compare != null && o.CompareFile == null)
            {
                if (o.Compare.IndexOfAny(new[] { '\\', '/' }) >= 0) o.CompareFile = o.Compare;
                else throw OpException.InvalidArgs("compare", "capture ids (c#) are resolved by the MCP broker; in-process callers pass the earlier image path");
            }
            if (o.CompareFile != null && o.Format == "jpg") o.Format = "png";
            o.Signature = string.Join(";", new[]
            {
                o.Size, o.Format, o.Region == null ? "-" : string.Join(",", o.Region.Select(v => v.ToString("R", CultureInfo.InvariantCulture))),
                o.Orient ?? "-", o.Style?.ToString() ?? "-", o.MarginMm.ToString("R", CultureInfo.InvariantCulture),
                string.Join(",", o.Highlight.Select(id => id.Value).OrderBy(v => v)), o.Annotations?.ToString() ?? "-"
            });
            return o;
        }
    }
}
