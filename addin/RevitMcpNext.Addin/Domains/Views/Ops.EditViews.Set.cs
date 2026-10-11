using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>edit_views set: view settings on one view (view=) or several (views=), with template-control checks.</summary>
    internal static partial class EditViewsOps
    {
        [Op("edit_views.set")]
        public static void Set(ChangeContext c)
        {
            Document doc = c.Doc;
            var views = new List<View>();
            if (c.Has("views"))
            {
                foreach (string name in c.Args.Strs("views"))
                {
                    View view = Resolve.View(doc, name, c.Args.Field("views"), c.Ctx.UiDoc);
                    if (views.All(v => v.Id != view.Id)) views.Add(view);
                }
            }
            else
            {
                views.Add(c.View("view"));
            }
            if (views.Count == 0) throw OpException.InvalidArgs("view", "give view or views");

            var changed = new List<string>();
            foreach (View view in views)
            {
                changed.Clear();
                ApplySettings(c, view, changed);
                if (changed.Count == 0)
                {
                    throw OpException.InvalidArgs("op", "set needs at least one setting (scale, detail_level, style, discipline, phase, phase_filter, template, crop, crop_box, crop_visible, view_range, far_clip, scope_box, orient, section_box, underlay)");
                }
                c.Modified(view, "view settings", null);
            }
            if (views.Count == 1) c.Output("view", views[0].Id);
            c.Data(new Dictionary<string, object> { ["views"] = views.Select(v => (object)v.Id.Value).ToList(), ["set"] = changed.ToList() });
            c.Summary("set " + string.Join(", ", changed) + " on " +
                (views.Count == 1 ? "'" + views[0].Name + "'" : views.Count.ToString(CultureInfo.InvariantCulture) + " views"));
        }

        private static void ApplySettings(ChangeContext c, View view, List<string> changed)
        {
            PayloadReader a = c.Args;
            Document doc = c.Doc;

            if (a.Has("template"))
            {
                ApplyTemplate(view, a.Str("template"), a.Field("template"));
                changed.Add("template " + (ViewKit.TemplateOf(view)?.Name ?? "none"));
            }
            if (a.Has("scale"))
            {
                SetScale(view, a.Int("scale"), a.Field("scale"));
                changed.Add("scale 1:" + view.Scale.ToString(CultureInfo.InvariantCulture));
            }
            if (a.Has("detail_level"))
            {
                RequireGraphical(view, "detail_level");
                ViewKit.EnsureNotTemplateControlled(view, "the detail level", BuiltInParameter.VIEW_DETAIL_LEVEL);
                string level = a.Enum("detail_level", null, "coarse", "medium", "fine");
                view.DetailLevel = level == "coarse" ? ViewDetailLevel.Coarse : level == "medium" ? ViewDetailLevel.Medium : ViewDetailLevel.Fine;
                changed.Add("detail " + level);
            }
            if (a.Has("style"))
            {
                RequireGraphical(view, "style");
                ViewKit.EnsureNotTemplateControlled(view, "the visual style", BuiltInParameter.MODEL_GRAPHICS_STYLE);
                ViewKit.SetStyle(view, ViewKit.ParseStyle(a.Str("style"), a.Field("style")));
                changed.Add("style " + ViewKit.StyleName(view.DisplayStyle));
            }
            if (a.Has("discipline"))
            {
                ViewKit.EnsureNotTemplateControlled(view, "the discipline", BuiltInParameter.VIEW_DISCIPLINE);
                string discipline = a.Enum("discipline", null, "architectural", "structural", "mechanical", "electrical", "plumbing", "coordination");
                int value = discipline == "architectural" ? 1 : discipline == "structural" ? 2 : discipline == "mechanical" ? 4 : discipline == "electrical" ? 8 : discipline == "plumbing" ? 16 : 4095;
                SetParam(view, BuiltInParameter.VIEW_DISCIPLINE, value, "discipline");
                changed.Add("discipline " + discipline);
            }
            if (a.Has("phase"))
            {
                Phase phase = Resolve.Phase(doc, a.Str("phase"), a.Field("phase"));
                SetParam(view, BuiltInParameter.VIEW_PHASE, phase.Id, "phase");
                changed.Add("phase " + phase.Name);
            }
            if (a.Has("phase_filter"))
            {
                ViewKit.EnsureNotTemplateControlled(view, "the phase filter", BuiltInParameter.VIEW_PHASE_FILTER);
                List<PhaseFilter> filters = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().ToList();
                PhaseFilter filter = Resolve.ById<PhaseFilter>(doc, a.Str("phase_filter")) ?? Resolve.Single(filters, f => f.Name, a.Str("phase_filter"), a.Field("phase_filter"), "phase filter");
                SetParam(view, BuiltInParameter.VIEW_PHASE_FILTER, filter.Id, "phase_filter");
                changed.Add("phase filter " + filter.Name);
            }
            if (a.Has("crop"))
            {
                RequireCroppable(view, "crop");
                view.CropBoxActive = a.Bool("crop");
                changed.Add(view.CropBoxActive ? "crop on" : "crop off");
            }
            if (a.Has("crop_box"))
            {
                RequireCroppable(view, "crop_box");
                SetCropBox(view, a.BoxFt("crop_box"), a.Field("crop_box"));
                if (!a.Has("crop")) view.CropBoxActive = true;
                changed.Add("crop box");
            }
            if (a.Has("crop_visible"))
            {
                RequireCroppable(view, "crop_visible");
                view.CropBoxVisible = a.Bool("crop_visible");
                changed.Add(view.CropBoxVisible ? "crop region shown" : "crop region hidden");
            }
            if (a.Has("view_range"))
            {
                SetViewRange(view, a.Sub("view_range"), a.Field("view_range"));
                changed.Add("view range");
            }
            if (a.Has("far_clip"))
            {
                if (!(view is ViewSection)) throw OpException.InvalidArgs(a.Field("far_clip"), "applies to sections and elevations, not " + view.ViewType + " views");
                ViewKit.EnsureNotTemplateControlled(view, "the far clip", BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                double far = a.Mm("far_clip");
                if (far <= 0) throw OpException.InvalidArgs(a.Field("far_clip"), "must be greater than 0");
                Parameter clipping = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                if (clipping != null && !clipping.IsReadOnly && clipping.AsInteger() == 0) clipping.Set(1);
                SetParam(view, BuiltInParameter.VIEWER_BOUND_OFFSET_FAR, far, "far_clip");
                changed.Add("far clip " + Convert.ToString(OutputJson.Mm(far), CultureInfo.InvariantCulture) + " mm");
            }
            if (a.Has("scope_box"))
            {
                ViewKit.EnsureNotTemplateControlled(view, "the scope box", BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                string text = a.Str("scope_box");
                ElementId id = ElementId.InvalidElementId;
                if (!string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
                {
                    List<Element> boxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().ToList();
                    id = (Resolve.ById<Element>(doc, text) ?? Resolve.Single(boxes, b => b.Name, text, a.Field("scope_box"), "scope box")).Id;
                }
                SetParam(view, BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP, id, "scope_box");
                changed.Add(id == ElementId.InvalidElementId ? "scope box none" : "scope box " + text);
            }
            if (a.Has("orient"))
            {
                if (!(view is View3D view3D)) throw OpException.InvalidArgs(a.Field("orient"), "applies to 3D views, not " + view.ViewType + " views");
                ViewKit.Orient(view3D, a.Str("orient"), null, null, a.Field("orient"));
                changed.Add("orient " + a.Str("orient"));
            }
            if (a.Has("section_box"))
            {
                if (!(view is View3D view3D)) throw OpException.InvalidArgs(a.Field("section_box"), "applies to 3D views, not " + view.ViewType + " views");
                SetSectionBox(view3D, a, "section_box");
                changed.Add(view3D.IsSectionBoxActive ? "section box" : "section box off");
            }
            if (a.Has("underlay"))
            {
                if (!(view is ViewPlan plan)) throw OpException.InvalidArgs(a.Field("underlay"), "applies to plan views, not " + view.ViewType + " views");
                string text = a.Str("underlay");
                ElementId levelId = string.Equals(text, "none", StringComparison.OrdinalIgnoreCase)
                    ? ElementId.InvalidElementId
                    : Resolve.Level(doc, text, a.Field("underlay"), c.Ctx.UiDoc).Id;
                plan.SetUnderlayBaseLevel(levelId);
                changed.Add(levelId == ElementId.InvalidElementId ? "underlay none" : "underlay " + text);
            }
        }

        // ------------------------------------------------------------------------------------------------- setters

        internal static void ApplyTemplate(View view, string value, string param)
        {
            Document doc = view.Document;
            string text = (value ?? string.Empty).Trim();
            if (view.IsTemplate) throw OpException.InvalidArgs(param, "'" + view.Name + "' is itself a view template");
            if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                view.ViewTemplateId = ElementId.InvalidElementId;
                return;
            }
            List<View> templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
            View template = Resolve.ById<View>(doc, text);
            if (template == null || !template.IsTemplate) template = Resolve.Single(templates, t => t.Name, text, param, "view template");
            if (!view.IsValidViewTemplate(template.Id))
            {
                throw OpException.InvalidArgs(param, "the template '" + template.Name + "' (" + template.ViewType + ") does not fit '" + view.Name + "' (" + view.ViewType + ")");
            }
            view.ViewTemplateId = template.Id;
        }

        internal static void SetScale(View view, int scale, string param)
        {
            if (scale < 1 || scale > 24000) throw OpException.InvalidArgs(param, "must be a scale denominator between 1 and 24000 (100 = 1:100)");
            if (view is ViewSheet || view.ViewType == ViewType.Schedule || (view is View3D v3 && v3.IsPerspective))
            {
                throw OpException.InvalidArgs(param, "'" + view.Name + "' (" + view.ViewType + ") has no settable scale");
            }
            ViewKit.EnsureNotTemplateControlled(view, "the scale", BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC, BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL, BuiltInParameter.VIEW_SCALE);
            view.Scale = scale;
        }

        internal static void SetSectionBox(View3D view, PayloadReader a, string key)
        {
            if (a.List(key).Count == 0)
            {
                view.IsSectionBoxActive = false;
                return;
            }
            double[] box = a.BoxFt(key);
            if (box.Length != 6) throw OpException.InvalidArgs(a.Field(key), "must be [x0,y0,z0,x1,y1,z1] mm, or [] to turn the section box off");
            BoundingBoxXYZ model = ViewKit.ModelBox(box);
            if (model.Max.X - model.Min.X < 1e-3 || model.Max.Y - model.Min.Y < 1e-3 || model.Max.Z - model.Min.Z < 1e-3)
            {
                throw OpException.InvalidArgs(a.Field(key), "must have a positive size in x, y and z");
            }
            view.SetSectionBox(model);
            view.IsSectionBoxActive = true;
        }

        private static void SetCropBox(View view, double[] ft, string param)
        {
            double a0, b0, a1, b1;
            if (ViewKit.IsPlanLike(view))
            {
                a0 = ft[0];
                b0 = ft[1];
                a1 = ft.Length == 6 ? ft[3] : ft[2];
                b1 = ft.Length == 6 ? ft[4] : ft[3];
            }
            else if (ft.Length == 6)
            {
                XYZ right = view.RightDirection;
                XYZ p0 = new XYZ(ft[0], ft[1], ft[2]), p1 = new XYZ(ft[3], ft[4], ft[5]);
                a0 = p0.DotProduct(right);
                a1 = p1.DotProduct(right);
                b0 = ft[2];
                b1 = ft[5];
            }
            else
            {
                a0 = ft[0];
                b0 = ft[1];
                a1 = ft[2];
                b1 = ft[3];
            }
            if (Math.Abs(a1 - a0) < 1e-3 || Math.Abs(b1 - b0) < 1e-3) throw OpException.InvalidArgs(param, "must have a positive width and height");
            if (view is View3D)
            {
                throw OpException.InvalidArgs(param, "3D views use section_box; crop_box is for plans, sections and elevations");
            }
            view.CropBox = ViewKit.CropFor(view, a0, b0, a1, b1);
        }

        private static void SetViewRange(View view, PayloadReader range, string param)
        {
            if (!(view is ViewPlan plan)) throw OpException.InvalidArgs(param, "applies to plan views, not " + view.ViewType + " views");
            if (range == null) throw OpException.InvalidArgs(param, "must be an object {top,cut,bottom,depth} in mm above the view level");
            ViewKit.EnsureNotTemplateControlled(view, "the view range", BuiltInParameter.PLAN_VIEW_RANGE);
            Level level = plan.GenLevel ?? throw OpException.InvalidArgs(param, "'" + view.Name + "' has no level");
            PlanViewRange viewRange = plan.GetViewRange();
            bool any = false;
            foreach (KeyValuePair<string, PlanViewPlane> plane in new[]
            {
                new KeyValuePair<string, PlanViewPlane>("top", PlanViewPlane.TopClipPlane),
                new KeyValuePair<string, PlanViewPlane>("cut", PlanViewPlane.CutPlane),
                new KeyValuePair<string, PlanViewPlane>("bottom", PlanViewPlane.BottomClipPlane),
                new KeyValuePair<string, PlanViewPlane>("depth", PlanViewPlane.ViewDepthPlane)
            })
            {
                double? offset = range.MmOpt(plane.Key);
                if (!offset.HasValue) continue;
                viewRange.SetLevelId(plane.Value, level.Id);
                viewRange.SetOffset(plane.Value, offset.Value);
                any = true;
            }
            if (!any) throw OpException.InvalidArgs(param, "give at least one of top, cut, bottom, depth (mm above the view level)");
            try
            {
                plan.SetViewRange(viewRange);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw OpException.InvalidArgs(param, "Revit refused the view range (needs top >= cut >= bottom >= depth): " + ex.Message);
            }
        }

        private static void SetParam(Element element, BuiltInParameter bip, object value, string param)
        {
            Parameter parameter = element.get_Parameter(bip);
            if (parameter == null) throw OpException.InvalidArgs(param, "'" + element.Name + "' has no " + param + " setting");
            if (parameter.IsReadOnly) throw OpException.InvalidArgs(param, "the " + param + " of '" + element.Name + "' is read-only here");
            bool ok;
            switch (value)
            {
                case int i: ok = parameter.Set(i); break;
                case double d: ok = parameter.Set(d); break;
                case ElementId id: ok = parameter.Set(id); break;
                default: ok = parameter.Set(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            }
            if (!ok) throw OpException.InvalidArgs(param, "Revit did not accept the " + param + " value for '" + element.Name + "'");
        }

        private static void RequireGraphical(View view, string param)
        {
            if (!ViewKit.IsGraphical(view) || view is ViewSheet) throw OpException.InvalidArgs(param, "does not apply to " + view.ViewType + " views");
        }

        private static void RequireCroppable(View view, string param)
        {
            if (!ViewKit.IsGraphical(view) || view is ViewSheet || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend)
            {
                throw OpException.InvalidArgs(param, "does not apply to " + view.ViewType + " views");
            }
        }
    }
}
