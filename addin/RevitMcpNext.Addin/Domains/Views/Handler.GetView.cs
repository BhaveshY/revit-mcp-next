using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// get_view (D1 §4B): one view or sheet with its settings; include= adds overrides, filters, hidden categories,
    /// viewports and revisions. Read-only, no transactions.
    /// </summary>
    internal static class GetViewHandler
    {
        private const int MaxOverrideScan = 5000;
        private const int MaxRows = 50;

        /// <summary>Template-controllable settings and the param names edit_views uses for them.</summary>
        internal static readonly KeyValuePair<BuiltInParameter, string>[] TemplateSettings =
        {
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC, "scale"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIEW_DETAIL_LEVEL, "detail_level"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.MODEL_GRAPHICS_STYLE, "style"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIEW_DISCIPLINE, "discipline"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIEW_PHASE_FILTER, "phase_filter"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.PLAN_VIEW_RANGE, "view_range"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR, "far_clip"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIS_GRAPHICS_MODEL, "categories"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIS_GRAPHICS_ANNOTATION, "annotation categories"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIS_GRAPHICS_FILTERS, "filters"),
            new KeyValuePair<BuiltInParameter, string>(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP, "scope_box")
        };

        [Op("get_view")]
        public static OpResult GetView(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            View view = Resolve.View(doc, ctx.Args.Str("view") ?? "active", "view", ctx.UiDoc);
            var include = new HashSet<string>(ctx.Args.Strs("include").Select(s => s.Trim().ToLowerInvariant()));

            Dictionary<string, object> data = Describe(ctx, view);
            if (include.Contains("overrides")) data["overrides"] = Overrides(ctx, view);
            if (include.Contains("filters")) data["filters"] = Filters(view);
            if (include.Contains("categories")) data["hidden_categories"] = HiddenCategories(view);
            if (include.Contains("viewports")) data["viewports"] = Viewports(view);
            if (include.Contains("revisions")) data["revisions"] = Revisions(view);

            string summary = ViewKit.Title(view);
            View template = ViewKit.TemplateOf(view);
            if (template != null) summary += ", template " + template.Name;
            if (view.IsTemplate) summary += ", view template";
            return ctx.Ok(data, summary).WithOutput("view", view.Id.Value);
        }

        internal static Dictionary<string, object> Describe(RequestContext ctx, View view)
        {
            Document doc = view.Document;
            Dictionary<string, object> data = ViewKit.Brief(view);
            data["uid"] = view.UniqueId;

            bool active = false;
            try { active = ctx.DocIsActive && ctx.UiDoc?.ActiveView?.Id == view.Id; } catch { active = false; }
            if (active) data["active"] = true;

            if (view is ViewSheet sheet)
            {
                DescribeSheet(sheet, data);
                return data;
            }

            Put(data, "detail_level", Safe(() => view.DetailLevel == ViewDetailLevel.Undefined ? null : view.DetailLevel.ToString().ToLowerInvariant()));
            Put(data, "style", Safe(() => view.DisplayStyle == DisplayStyle.Undefined ? null : ViewKit.StyleName(view.DisplayStyle)));
            Put(data, "discipline", DisciplineName(view));
            Put(data, "level", Safe(() => view.GenLevel?.Name));
            Put(data, "phase", ParamElementName(view, BuiltInParameter.VIEW_PHASE));
            Put(data, "phase_filter", ParamElementName(view, BuiltInParameter.VIEW_PHASE_FILTER));
            Put(data, "scope_box", ParamElementName(view, BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP));

            View template = ViewKit.TemplateOf(view);
            if (template != null)
            {
                data["template"] = template.Name;
                List<string> controls = TemplateControls(view);
                if (controls.Count > 0) data["template_controls"] = controls;
            }
            if (view.IsTemplate) data["is_template"] = true;

            ElementId primary = Safe(() => view.GetPrimaryViewId());
            if (primary != null && primary != ElementId.InvalidElementId) data["dependent_of"] = (doc.GetElement(primary) as View)?.Name;

            Dictionary<string, object> crop = Crop(view);
            if (crop != null) data["crop"] = crop;
            if (view is ViewPlan plan)
            {
                Dictionary<string, object> range = ViewRange(plan);
                if (range != null) data["view_range"] = range;
                Put(data, "underlay", Safe(() =>
                {
                    ElementId underlay = plan.GetUnderlayBaseLevel();
                    return underlay == null || underlay == ElementId.InvalidElementId ? null : (doc.GetElement(underlay) as Level)?.Name;
                }));
            }
            if (view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation || view.ViewType == ViewType.Detail)
            {
                Parameter far = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                Parameter clipping = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                if (far != null && far.HasValue && (clipping == null || clipping.AsInteger() != 0)) data["far_clip"] = OutputJson.Mm(far.AsDouble());
                data["looking"] = OutputJson.Point(view.ViewDirection.Negate());
            }
            if (view is View3D view3D)
            {
                data["perspective"] = view3D.IsPerspective;
                Put(data, "orient", ViewKit.OrientName(view3D));
                if (view3D.IsLocked) data["locked"] = true;
                if (view3D.IsSectionBoxActive)
                {
                    BoundingBoxXYZ box = view3D.GetSectionBox();
                    data["section_box"] = ModelBoxMm(box);
                }
            }

            ViewSheet placedOn = SheetOf(view);
            if (placedOn != null) data["sheet"] = placedOn.SheetNumber + " - " + placedOn.Name;
            if (Safe(() => view.IsTemporaryHideIsolateActive())) data["temporary_hide_isolate"] = true;
            int filters = Safe(() => view.AreGraphicsOverridesAllowed() ? view.GetFilters().Count : 0);
            if (filters > 0) data["filter_count"] = filters;
            return data;
        }

        private static void DescribeSheet(ViewSheet sheet, Dictionary<string, object> data)
        {
            Document doc = sheet.Document;
            data["number"] = sheet.SheetNumber;
            data["sheet_name"] = sheet.Name;
            List<FamilyInstance> titleBlocks = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            if (titleBlocks.Count > 0) data["titleblock"] = Resolve.FullTypeName(titleBlocks[0].Symbol);
            double[] outline = SheetOutlineMm(sheet);
            if (outline != null) data["size_mm"] = new List<object> { Units.Number(outline[2] - outline[0], 0), Units.Number(outline[3] - outline[1], 0) };
            data["viewports"] = Safe(() => sheet.GetAllViewports().Count);
            int schedules = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).GetElementCount();
            if (schedules > 0) data["schedules"] = schedules;
            ElementId current = Safe(() => sheet.GetCurrentRevision());
            if (current != null && current != ElementId.InvalidElementId && doc.GetElement(current) is Revision revision)
            {
                data["current_revision"] = RevisionLabel(revision);
            }
            if (sheet.IsPlaceholder) data["placeholder"] = true;
        }

        /// <summary>The sheet outline (titleblock area) in sheet mm [x0,y0,x1,y1]; null when unavailable.</summary>
        internal static double[] SheetOutlineMm(ViewSheet sheet)
        {
            try
            {
                BoundingBoxUV outline = sheet.Outline;
                if (outline == null) return null;
                double x0 = Units.FtToMm(outline.Min.U), y0 = Units.FtToMm(outline.Min.V), x1 = Units.FtToMm(outline.Max.U), y1 = Units.FtToMm(outline.Max.V);
                if (x1 - x0 < 1 || y1 - y0 < 1) return null;
                return new[] { x0, y0, x1, y1 };
            }
            catch
            {
                return null;
            }
        }

        internal static ViewSheet SheetOf(View view)
        {
            try
            {
                Parameter number = view.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER);
                string text = number?.AsString();
                if (string.IsNullOrWhiteSpace(text) || text == "---") return null;
                Viewport viewport = new FilteredElementCollector(view.Document).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(v => v.ViewId == view.Id);
                if (viewport != null) return view.Document.GetElement(viewport.SheetId) as ViewSheet;
                return new FilteredElementCollector(view.Document).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => s.SheetNumber == text);
            }
            catch
            {
                return null;
            }
        }

        internal static List<string> TemplateControls(View view)
        {
            var result = new List<string>();
            View template = ViewKit.TemplateOf(view);
            if (template == null) return result;
            try
            {
                var controllable = new HashSet<long>(template.GetTemplateParameterIds().Select(id => id.Value));
                var excluded = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(id => id.Value));
                foreach (KeyValuePair<BuiltInParameter, string> setting in TemplateSettings)
                {
                    long id = (long)setting.Key;
                    if (controllable.Contains(id) && !excluded.Contains(id)) result.Add(setting.Value);
                }
            }
            catch
            {
                // Templates that cannot be inspected report nothing.
            }
            return result;
        }

        internal static string DisciplineName(View view)
        {
            Parameter parameter = view.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE);
            if (parameter == null || !parameter.HasValue) return null;
            switch (parameter.AsInteger())
            {
                case 1: return "architectural";
                case 2: return "structural";
                case 4: return "mechanical";
                case 8: return "electrical";
                case 16: return "plumbing";
                case 4095: return "coordination";
                default: return null;
            }
        }

        private static Dictionary<string, object> Crop(View view)
        {
            if (!ViewKit.IsGraphical(view) || view is ViewSheet || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend) return null;
            try
            {
                var crop = new Dictionary<string, object> { ["active"] = view.CropBoxActive, ["visible"] = view.CropBoxVisible };
                double[] rect = ViewKit.CropRectXY(view);
                if (rect != null && ViewKit.IsPlanLike(view))
                {
                    crop["box"] = rect.Select(v => OutputJson.Mm(v)).ToList();
                }
                else
                {
                    BoundingBoxXYZ box = view.CropBox;
                    if (box != null)
                    {
                        crop["size"] = new List<object> { OutputJson.Mm(Math.Abs(box.Max.X - box.Min.X)), OutputJson.Mm(Math.Abs(box.Max.Y - box.Min.Y)) };
                    }
                }
                return crop;
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> ViewRange(ViewPlan plan)
        {
            try
            {
                PlanViewRange range = plan.GetViewRange();
                if (range == null) return null;
                Level level = plan.GenLevel;
                var data = new Dictionary<string, object>();
                foreach (KeyValuePair<string, PlanViewPlane> plane in new[]
                {
                    new KeyValuePair<string, PlanViewPlane>("top", PlanViewPlane.TopClipPlane),
                    new KeyValuePair<string, PlanViewPlane>("cut", PlanViewPlane.CutPlane),
                    new KeyValuePair<string, PlanViewPlane>("bottom", PlanViewPlane.BottomClipPlane),
                    new KeyValuePair<string, PlanViewPlane>("depth", PlanViewPlane.ViewDepthPlane)
                })
                {
                    ElementId levelId = range.GetLevelId(plane.Value);
                    double offset = range.GetOffset(plane.Value);
                    if (levelId == PlanViewRange.Unlimited)
                    {
                        data[plane.Key] = "unlimited";
                        continue;
                    }
                    Level planeLevel = levelId == PlanViewRange.Current || levelId == null || levelId == ElementId.InvalidElementId
                        ? level
                        : plan.Document.GetElement(levelId) as Level;
                    if (level != null && planeLevel != null && planeLevel.Id == level.Id)
                    {
                        data[plane.Key] = OutputJson.Mm(offset);
                    }
                    else if (planeLevel != null)
                    {
                        data[plane.Key] = planeLevel.Name + (Math.Abs(offset) > 1e-9 ? (offset > 0 ? " +" : " ") + Convert.ToString(OutputJson.Mm(offset), CultureInfo.InvariantCulture) : string.Empty);
                    }
                    else if (levelId == PlanViewRange.LevelAbove)
                    {
                        data[plane.Key] = "level above" + (Math.Abs(offset) > 1e-9 ? " " + Convert.ToString(OutputJson.Mm(offset), CultureInfo.InvariantCulture) : string.Empty);
                    }
                    else if (levelId == PlanViewRange.LevelBelow)
                    {
                        data[plane.Key] = "level below" + (Math.Abs(offset) > 1e-9 ? " " + Convert.ToString(OutputJson.Mm(offset), CultureInfo.InvariantCulture) : string.Empty);
                    }
                }
                return data;
            }
            catch
            {
                return null;
            }
        }

        private static object Overrides(RequestContext ctx, View view)
        {
            Document doc = view.Document;
            var categoryRows = new List<object>();
            var elementRows = new List<object>();
            if (!Safe(() => view.AreGraphicsOverridesAllowed())) return new Dictionary<string, object> { ["allowed"] = false };

            foreach (Category category in TopCategories(doc))
            {
                OverrideGraphicSettings ogs = Safe(() => view.GetCategoryOverrides(category.Id));
                Dictionary<string, object> described = GraphicsKit.Describe(doc, ogs);
                if (described == null) continue;
                described["category"] = Rows.CategoryLabel(category);
                categoryRows.Add(described);
                if (categoryRows.Count >= MaxRows) break;
            }

            int scanned = 0, found = 0;
            bool truncated = false;
            foreach (Element element in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (++scanned > MaxOverrideScan || ctx.ShouldYield(scanned))
                {
                    truncated = true;
                    break;
                }
                OverrideGraphicSettings ogs = Safe(() => view.GetElementOverrides(element.Id));
                Dictionary<string, object> described = GraphicsKit.Describe(doc, ogs);
                if (described == null) continue;
                found++;
                if (elementRows.Count < MaxRows)
                {
                    described["id"] = element.Id.Value;
                    described["category"] = Rows.CategoryLabel(element.Category);
                    elementRows.Add(described);
                }
            }
            var data = new Dictionary<string, object> { ["categories"] = categoryRows, ["elements"] = elementRows, ["elements_total"] = found };
            if (truncated) data["scan_limit"] = MaxOverrideScan;
            return data;
        }

        private static object Filters(View view)
        {
            Document doc = view.Document;
            var rows = new List<object>();
            if (!Safe(() => view.AreGraphicsOverridesAllowed())) return rows;
            foreach (ElementId id in view.GetFilters())
            {
                var row = new Dictionary<string, object>
                {
                    ["id"] = id.Value,
                    ["name"] = doc.GetElement(id)?.Name,
                    ["visible"] = Safe(() => view.GetFilterVisibility(id))
                };
                if (!Safe(() => view.GetIsFilterEnabled(id), true)) row["enabled"] = false;
                Dictionary<string, object> described = GraphicsKit.Describe(doc, Safe(() => view.GetFilterOverrides(id)));
                if (described != null) row["overrides"] = described;
                if (doc.GetElement(id) is ParameterFilterElement filter)
                {
                    row["categories"] = filter.GetCategories().Select(c => (object)Rows.CategoryLabel(Category.GetCategory(doc, c)) ?? c.Value).ToList();
                }
                rows.Add(row);
            }
            return rows;
        }

        private static object HiddenCategories(View view)
        {
            Document doc = view.Document;
            var hidden = new List<object>();
            foreach (Category category in TopCategories(doc))
            {
                bool isHidden = Safe(() => view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id));
                if (isHidden) hidden.Add(Rows.CategoryLabel(category));
            }
            var data = new Dictionary<string, object> { ["hidden"] = hidden };
            if (Safe(() => view.AreModelCategoriesHidden)) data["model_hidden"] = true;
            if (Safe(() => view.AreAnnotationCategoriesHidden)) data["annotations_hidden"] = true;
            return data;
        }

        private static object Viewports(View view)
        {
            Document doc = view.Document;
            var rows = new List<object>();
            if (view is ViewSheet sheet)
            {
                foreach (ElementId id in sheet.GetAllViewports())
                {
                    if (!(doc.GetElement(id) is Viewport viewport)) continue;
                    View placed = doc.GetElement(viewport.ViewId) as View;
                    var row = new Dictionary<string, object>
                    {
                        ["id"] = viewport.Id.Value,
                        ["view"] = placed?.Name,
                        ["view_id"] = viewport.ViewId.Value,
                        ["center"] = OutputJson.Point2(viewport.GetBoxCenter())
                    };
                    Outline outline = Safe(() => viewport.GetBoxOutline());
                    if (outline != null) row["box"] = new List<object> { OutputJson.Mm(outline.MinimumPoint.X), OutputJson.Mm(outline.MinimumPoint.Y), OutputJson.Mm(outline.MaximumPoint.X), OutputJson.Mm(outline.MaximumPoint.Y) };
                    string number = viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString();
                    if (!string.IsNullOrWhiteSpace(number)) row["detail_number"] = number;
                    rows.Add(row);
                }
                foreach (ScheduleSheetInstance schedule in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                {
                    if (schedule.IsTitleblockRevisionSchedule) continue;
                    rows.Add(new Dictionary<string, object>
                    {
                        ["id"] = schedule.Id.Value,
                        ["schedule"] = (doc.GetElement(schedule.ScheduleId) as View)?.Name,
                        ["view_id"] = schedule.ScheduleId.Value,
                        ["point"] = OutputJson.Point2(schedule.Point)
                    });
                }
                return rows;
            }
            foreach (Viewport viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Where(v => v.ViewId == view.Id))
            {
                ViewSheet on = doc.GetElement(viewport.SheetId) as ViewSheet;
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = viewport.Id.Value,
                    ["sheet"] = on == null ? null : on.SheetNumber + " - " + on.Name,
                    ["center"] = OutputJson.Point2(viewport.GetBoxCenter())
                });
            }
            return rows;
        }

        private static object Revisions(View view)
        {
            Document doc = view.Document;
            var rows = new List<object>();
            if (view is ViewSheet sheet)
            {
                var additional = new HashSet<long>(Safe(() => sheet.GetAdditionalRevisionIds(), new List<ElementId>()).Select(id => id.Value));
                foreach (ElementId id in Safe(() => sheet.GetAllRevisionIds(), new List<ElementId>()))
                {
                    if (!(doc.GetElement(id) is Revision revision)) continue;
                    var row = RevisionRow(revision);
                    if (additional.Contains(id.Value)) row["additional"] = true;
                    rows.Add(row);
                }
                return rows;
            }
            List<RevisionCloud> clouds = new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevisionCloud)).Cast<RevisionCloud>().ToList();
            foreach (IGrouping<long, RevisionCloud> group in clouds.GroupBy(c => c.RevisionId.Value))
            {
                if (!(doc.GetElement(new ElementId(group.Key)) is Revision revision)) continue;
                var row = RevisionRow(revision);
                row["clouds"] = group.Count();
                rows.Add(row);
            }
            return rows;
        }

        private static Dictionary<string, object> RevisionRow(Revision revision)
        {
            return new Dictionary<string, object>
            {
                ["id"] = revision.Id.Value,
                ["seq"] = revision.SequenceNumber,
                ["number"] = Safe(() => revision.RevisionNumber),
                ["date"] = revision.RevisionDate,
                ["description"] = revision.Description,
                ["issued"] = revision.Issued
            };
        }

        private static string RevisionLabel(Revision revision)
        {
            string number = Safe(() => revision.RevisionNumber);
            return (string.IsNullOrWhiteSpace(number) ? "Seq " + revision.SequenceNumber.ToString(CultureInfo.InvariantCulture) : number) +
                (string.IsNullOrWhiteSpace(revision.Description) ? string.Empty : " " + revision.Description);
        }

        internal static IEnumerable<Category> TopCategories(Document doc)
        {
            Categories categories;
            try { categories = doc.Settings.Categories; } catch { yield break; }
            foreach (Category category in categories)
            {
                if (category == null) continue;
                CategoryType type;
                try { type = category.CategoryType; } catch { continue; }
                if (type != CategoryType.Model && type != CategoryType.Annotation) continue;
                yield return category;
            }
        }

        internal static List<object> ModelBoxMm(BoundingBoxXYZ box)
        {
            Transform t = box.Transform ?? Transform.Identity;
            XYZ a = t.OfPoint(box.Min), b = t.OfPoint(box.Max);
            return new List<object>
            {
                OutputJson.Mm(Math.Min(a.X, b.X)), OutputJson.Mm(Math.Min(a.Y, b.Y)), OutputJson.Mm(Math.Min(a.Z, b.Z)),
                OutputJson.Mm(Math.Max(a.X, b.X)), OutputJson.Mm(Math.Max(a.Y, b.Y)), OutputJson.Mm(Math.Max(a.Z, b.Z))
            };
        }

        private static string ParamElementName(Element element, BuiltInParameter bip)
        {
            try
            {
                Parameter parameter = element.get_Parameter(bip);
                if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.ElementId) return null;
                ElementId id = parameter.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return null;
                return element.Document.GetElement(id)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static void Put(Dictionary<string, object> data, string key, object value)
        {
            if (value != null) data[key] = value;
        }

        internal static T Safe<T>(Func<T> read, T fallback = default(T))
        {
            try
            {
                return read();
            }
            catch
            {
                return fallback;
            }
        }
    }
}
