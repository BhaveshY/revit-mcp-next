using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// view_graphics (D1 §4B): per-view graphic overrides for elements, categories and filters, permanent hide/unhide,
    /// view filters with rules, color by parameter and reset. Writes run in the ChangeEngine transaction (tx "in");
    /// the bulk rule counts the elements whose graphics change. Template-controlled V/G settings give
    /// TEMPLATE_CONTROLLED naming the template (fix: run the same call on the template).
    /// </summary>
    internal static class ViewGraphicsOps
    {
        // -------------------------------------------------------------------------------------------------- override

        [Op("view_graphics.override")]
        public static void Override(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = GraphicsView(c);
            if (!GraphicsKit.HasOverrideArgs(c.Args))
            {
                throw OpException.InvalidArgs("color", "override needs at least one of color, fill, line_weight, fill_pattern, transparency, halftone");
            }
            var changed = new List<string>();
            string target;
            if (ViewKit.HasSelector(c.Args))
            {
                List<Element> elements = ViewKit.SelectedElements(c.Ctx, c.Args, doc);
                bool first = true;
                foreach (Element element in elements)
                {
                    OverrideGraphicSettings ogs = GraphicsKit.Build(doc, view.GetElementOverrides(element.Id), c.Args, true, first ? changed : null);
                    view.SetElementOverrides(element.Id, ogs);
                    first = false;
                }
                c.Affected(elements.Count);
                target = Count(elements.Count, "element");
                c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["elements"] = elements.Count, ["set"] = changed });
            }
            else if (c.Has("category"))
            {
                List<Category> categories = ViewKit.Categories(doc, c.Args);
                bool first = true;
                foreach (Category category in categories)
                {
                    if (!view.IsCategoryOverridable(category.Id)) throw OpException.InvalidArgs(c.Args.Field("category"), "'" + Rows.CategoryLabel(category) + "' cannot be overridden in '" + view.Name + "'");
                    ViewKit.EnsureNotTemplateControlled(view, "the graphics of " + Rows.CategoryLabel(category), ViewKit.VisibilityParameterFor(category));
                    view.SetCategoryOverrides(category.Id, GraphicsKit.Build(doc, view.GetCategoryOverrides(category.Id), c.Args, true, first ? changed : null));
                    first = false;
                }
                target = string.Join(", ", categories.Select(Rows.CategoryLabel));
                c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["categories"] = categories.Select(cat => (object)Rows.CategoryLabel(cat)).ToList(), ["set"] = changed });
            }
            else if (c.Has("view_filter"))
            {
                FilterElement filter = FindFilter(doc, c.Str("view_filter"), c.Args.Field("view_filter"));
                EnsureFilterApplied(view, filter, c);
                view.SetFilterOverrides(filter.Id, GraphicsKit.Build(doc, view.GetFilterOverrides(filter.Id), c.Args, false, changed));
                target = "filter '" + filter.Name + "'";
                c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["view_filter"] = filter.Name, ["set"] = changed });
            }
            else
            {
                throw OpException.InvalidArgs("ids", "override needs ids, from, filter, category or view_filter");
            }
            c.Modified(view, "graphics overrides", null);
            c.Output("view", view.Id);
            c.Summary("overrode " + target + " in '" + view.Name + "': " + string.Join(", ", changed));
        }

        // ----------------------------------------------------------------------------------------------------- reset

        [Op("view_graphics.reset")]
        public static void Reset(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = GraphicsView(c);
            var blank = new OverrideGraphicSettings();
            string target;
            if (ViewKit.HasSelector(c.Args))
            {
                List<Element> elements = ViewKit.SelectedElements(c.Ctx, c.Args, doc);
                foreach (Element element in elements) view.SetElementOverrides(element.Id, blank);
                c.Affected(elements.Count);
                target = Count(elements.Count, "element");
            }
            else if (c.Has("category"))
            {
                List<Category> categories = ViewKit.Categories(doc, c.Args);
                foreach (Category category in categories)
                {
                    ViewKit.EnsureNotTemplateControlled(view, "the graphics of " + Rows.CategoryLabel(category), ViewKit.VisibilityParameterFor(category));
                    view.SetCategoryOverrides(category.Id, blank);
                }
                target = string.Join(", ", categories.Select(Rows.CategoryLabel));
            }
            else if (c.Has("view_filter"))
            {
                FilterElement filter = FindFilter(doc, c.Str("view_filter"), c.Args.Field("view_filter"));
                if (!view.IsFilterApplied(filter.Id)) throw OpException.InvalidArgs(c.Args.Field("view_filter"), "the filter '" + filter.Name + "' is not applied to '" + view.Name + "'");
                ViewKit.EnsureNotTemplateControlled(view, "the filters", BuiltInParameter.VIS_GRAPHICS_FILTERS);
                view.SetFilterOverrides(filter.Id, blank);
                target = "filter '" + filter.Name + "'";
            }
            else
            {
                int elements = 0, categories = 0, filters = 0;
                foreach (Element element in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    if (GraphicsKit.Describe(doc, view.GetElementOverrides(element.Id)) == null) continue;
                    view.SetElementOverrides(element.Id, blank);
                    elements++;
                }
                bool modelLocked = ViewKit.TemplateControls(view, BuiltInParameter.VIS_GRAPHICS_MODEL);
                bool annotationLocked = ViewKit.TemplateControls(view, BuiltInParameter.VIS_GRAPHICS_ANNOTATION);
                foreach (Category category in GetViewHandler.TopCategories(doc))
                {
                    bool locked = category.CategoryType == CategoryType.Annotation ? annotationLocked : modelLocked;
                    if (locked) continue;
                    OverrideGraphicSettings current;
                    try { current = view.GetCategoryOverrides(category.Id); } catch { continue; }
                    if (GraphicsKit.Describe(doc, current) == null) continue;
                    view.SetCategoryOverrides(category.Id, blank);
                    categories++;
                }
                if (!ViewKit.TemplateControls(view, BuiltInParameter.VIS_GRAPHICS_FILTERS))
                {
                    foreach (ElementId id in view.GetFilters())
                    {
                        if (GraphicsKit.Describe(doc, view.GetFilterOverrides(id)) == null) continue;
                        view.SetFilterOverrides(id, blank);
                        filters++;
                    }
                }
                if (modelLocked || annotationLocked)
                {
                    c.Warn(WarningCodes.PartialResult, "category overrides of '" + view.Name + "' are controlled by its view template '" + ViewKit.TemplateOf(view)?.Name + "' and were kept");
                }
                c.Affected(elements);
                target = Count(elements, "element") + ", " + Count(categories, "category", "categories") + ", " + Count(filters, "filter");
            }
            c.Modified(view, "graphics overrides", null);
            c.Output("view", view.Id);
            c.Summary("reset overrides of " + target + " in '" + view.Name + "'");
        }

        // ------------------------------------------------------------------------------------------------ hide/unhide

        [Op("view_graphics.hide")]
        public static void Hide(ChangeContext c)
        {
            HideOrUnhide(c, hide: true);
        }

        [Op("view_graphics.unhide")]
        public static void Unhide(ChangeContext c)
        {
            HideOrUnhide(c, hide: false);
        }

        private static void HideOrUnhide(ChangeContext c, bool hide)
        {
            Document doc = c.Doc;
            View view = c.View("view");
            string verb = hide ? "hid " : "unhid ";
            string target;
            if (c.Has("ids") || c.Has("from") || c.Has("filter"))
            {
                List<Element> elements = ViewKit.SelectedElements(c.Ctx, c.Args, doc);
                List<ElementId> ids = hide
                    ? elements.Where(e => SafeBool(() => e.CanBeHidden(view)) && !SafeBool(() => e.IsHidden(view))).Select(e => e.Id).ToList()
                    : elements.Where(e => SafeBool(() => e.IsHidden(view))).Select(e => e.Id).ToList();
                int skipped = elements.Count - ids.Count;
                if (ids.Count > 0)
                {
                    if (hide) view.HideElements(ids);
                    else view.UnhideElements(ids);
                }
                if (skipped > 0)
                {
                    c.Warn(WarningCodes.PartialResult, Count(skipped, "element") + (hide ? " cannot be hidden or are already hidden" : " were not hidden") + " in '" + view.Name + "'",
                        elements.Where(e => !ids.Contains(e.Id)).Select(e => e.Id).Take(20));
                }
                c.Affected(ids.Count);
                target = Count(ids.Count, "element");
                c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["changed"] = ids.Count, ["skipped"] = skipped });
            }
            else if (c.Has("category"))
            {
                List<Category> categories = ViewKit.Categories(doc, c.Args);
                foreach (Category category in categories)
                {
                    if (!view.CanCategoryBeHidden(category.Id))
                    {
                        throw OpException.InvalidArgs(c.Args.Field("category"), "'" + Rows.CategoryLabel(category) + "' cannot be " + (hide ? "hidden" : "shown") + " in '" + view.Name + "'");
                    }
                    ViewKit.EnsureNotTemplateControlled(view, "the visibility of " + Rows.CategoryLabel(category), ViewKit.VisibilityParameterFor(category));
                    view.SetCategoryHidden(category.Id, hide);
                }
                target = string.Join(", ", categories.Select(Rows.CategoryLabel));
                c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["categories"] = categories.Select(cat => (object)Rows.CategoryLabel(cat)).ToList() });
            }
            else
            {
                throw OpException.InvalidArgs("ids", (hide ? "hide" : "unhide") + " needs ids, from or category");
            }
            c.Modified(view, hide ? "hidden" : "unhidden", null);
            c.Output("view", view.Id);
            c.Summary(verb + target + " in '" + view.Name + "'");
        }

        // -------------------------------------------------------------------------------------------------- color_by

        [Op("view_graphics.color_by")]
        public static void ColorBy(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = GraphicsView(c);
            string param = c.Str("param");
            if (string.IsNullOrWhiteSpace(param)) throw OpException.InvalidArgs(c.Args.Field("param"), "is required (the parameter whose values get colors)");
            List<Category> categories = ViewKit.Categories(doc, c.Args);
            if (categories.Count == 0) throw OpException.InvalidArgs(c.Args.Field("category"), "is required (e.g. [\"Rooms\"])");
            FillPatternElement solid = GraphicsKit.SolidFill(doc);

            var groups = new SortedDictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
            int total = 0, withParam = 0;
            foreach (Category category in categories)
            {
                foreach (Element element in new FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType())
                {
                    total++;
                    Parameter parameter = ParamValues.Resolve(element, param);
                    if (parameter == null)
                    {
                        ElementType type = Rows.TypeOf(element);
                        if (type != null) parameter = ParamValues.Resolve(type, param);
                    }
                    if (parameter != null) withParam++;
                    string value = parameter == null || !parameter.HasValue ? null : ParamValues.Display(parameter);
                    string key = string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
                    if (!groups.TryGetValue(key, out List<ElementId> ids)) groups[key] = ids = new List<ElementId>();
                    ids.Add(element.Id);
                }
            }
            if (total == 0)
            {
                throw new OpException(ErrorCodes.NotFound, "No " + string.Join(", ", categories.Select(Rows.CategoryLabel)) + " are visible in '" + view.Name + "'.",
                    new Dictionary<string, object> { ["param"] = "category", ["kind"] = "element", ["value"] = string.Join(", ", categories.Select(Rows.CategoryLabel)), ["candidates"] = new List<object>() });
            }
            if (withParam == 0)
            {
                throw OpException.NotFound(c.Args.Field("param"), "parameter", param, new string[0]);
            }

            var legend = new List<object>();
            int index = 0;
            foreach (KeyValuePair<string, List<ElementId>> group in groups)
            {
                Color color = group.Key == "(none)" ? new Color(200, 200, 200) : ViewKit.PaletteColor(index++);
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(color);
                ogs.SetCutLineColor(color);
                if (solid != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solid.Id);
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetSurfaceForegroundPatternVisible(true);
                    ogs.SetCutForegroundPatternId(solid.Id);
                    ogs.SetCutForegroundPatternColor(color);
                    ogs.SetCutForegroundPatternVisible(true);
                }
                foreach (ElementId id in group.Value) view.SetElementOverrides(id, ogs);
                legend.Add(new List<object> { group.Key, ViewKit.Hex(color), group.Value.Count });
            }
            ShowInteriorFill(c, view, categories);
            c.Affected(total);
            c.Modified(view, "color by " + param, null);
            c.Output("view", view.Id);
            c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["param"] = param, ["elements"] = total, ["legend"] = legend });
            c.Summary("colored " + Count(total, "element") + " in '" + view.Name + "' by " + param + " (" + Count(groups.Count, "value") + ")");
        }

        /// <summary>Rooms, areas and spaces show surface overrides only through their Interior Fill subcategory.</summary>
        private static void ShowInteriorFill(ChangeContext c, View view, List<Category> categories)
        {
            foreach (Category category in categories)
            {
                BuiltInCategory fill;
                switch (Resolve.SafeBuiltIn(category))
                {
                    case BuiltInCategory.OST_Rooms: fill = BuiltInCategory.OST_RoomInteriorFill; break;
                    case BuiltInCategory.OST_Areas: fill = BuiltInCategory.OST_AreaInteriorFill; break;
                    case BuiltInCategory.OST_MEPSpaces: fill = BuiltInCategory.OST_MEPSpaceInteriorFill; break;
                    default: continue;
                }
                Category sub = Category.GetCategory(c.Doc, fill);
                if (sub == null) continue;
                try
                {
                    if (!view.GetCategoryHidden(sub.Id)) continue;
                    if (ViewKit.TemplateControls(view, BuiltInParameter.VIS_GRAPHICS_MODEL) || !view.CanCategoryBeHidden(sub.Id))
                    {
                        c.Warn(WarningCodes.PartialResult, "the Interior Fill of " + Rows.CategoryLabel(category) + " is hidden in '" + view.Name + "', so the colors show on outlines only");
                        continue;
                    }
                    view.SetCategoryHidden(sub.Id, false);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    c.Warn(WarningCodes.PartialResult, "could not show the Interior Fill of " + Rows.CategoryLabel(category) + " in '" + view.Name + "'");
                }
            }
        }

        // --------------------------------------------------------------------------------------------------- filters

        [Op("view_graphics.filter_create")]
        public static void FilterCreate(ChangeContext c)
        {
            Document doc = c.Doc;
            string name = (c.Str("view_filter") ?? string.Empty).Trim();
            if (name.Length == 0) throw OpException.InvalidArgs(c.Args.Field("view_filter"), "is required (the new filter's name)");
            if (!ParameterFilterElement.IsNameUnique(doc, name))
            {
                throw new OpException(ErrorCodes.NameTaken, "A view filter named '" + name + "' already exists.",
                    new Dictionary<string, object> { ["param"] = "view_filter", ["value"] = name, ["next"] = NextFilterName(doc, name) });
            }
            List<ElementId> categories = ViewFilterRules.Categories(doc, c.Args.Strs("categories"), c.Args.Field("categories"));
            ElementFilter rules = ViewFilterRules.Build(doc, categories, c.Args.Strs("rules"), c.Args.Field("rules"));
            if (!ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(doc, new HashSet<ElementId>(categories), rules))
            {
                throw OpException.InvalidArgs(c.Args.Field("rules"), "Revit does not accept these rules for the categories " + string.Join(", ", c.Args.Strs("categories")));
            }
            ParameterFilterElement filter = ParameterFilterElement.Create(doc, name, categories, rules);
            c.Created(filter);
            c.Output("view_filter", filter.Id);
            string applied = string.Empty;
            if (c.Has("view"))
            {
                View view = GraphicsView(c);
                ApplyFilter(c, view, filter);
                applied = " and applied it to '" + view.Name + "'";
            }
            c.Data(new Dictionary<string, object> { ["view_filter"] = filter.Name, ["id"] = filter.Id.Value, ["categories"] = c.Args.Strs("categories"), ["rules"] = c.Args.Strs("rules") });
            c.Summary("created view filter '" + filter.Name + "'" + applied);
        }

        [Op("view_graphics.filter_edit")]
        public static void FilterEdit(ChangeContext c)
        {
            Document doc = c.Doc;
            FilterElement element = FindFilter(doc, c.Str("view_filter"), c.Args.Field("view_filter"));
            if (!(element is ParameterFilterElement filter))
            {
                throw OpException.InvalidArgs(c.Args.Field("view_filter"), "'" + element.Name + "' is a selection filter; only rule-based filters can be edited");
            }
            if (!c.Has("categories") && !c.Has("rules")) throw OpException.InvalidArgs("rules", "filter_edit needs categories and/or rules");
            var changed = new List<string>();
            List<ElementId> categories = c.Has("categories")
                ? ViewFilterRules.Categories(doc, c.Args.Strs("categories"), c.Args.Field("categories"))
                : filter.GetCategories().ToList();
            if (c.Has("categories"))
            {
                if (!c.Has("rules") && !ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(doc, new HashSet<ElementId>(categories), filter.GetElementFilter()))
                {
                    throw OpException.InvalidArgs(c.Args.Field("categories"), "the filter's rules use parameters these categories do not have; give new rules too");
                }
                changed.Add("categories");
            }
            if (c.Has("rules"))
            {
                ElementFilter rules = ViewFilterRules.Build(doc, categories, c.Args.Strs("rules"), c.Args.Field("rules"));
                if (!ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(doc, new HashSet<ElementId>(categories), rules))
                {
                    throw OpException.InvalidArgs(c.Args.Field("rules"), "Revit does not accept these rules for the filter's categories");
                }
                if (c.Has("categories")) filter.SetCategories(categories);
                filter.SetElementFilter(rules);
                changed.Add("rules");
            }
            else
            {
                filter.SetCategories(categories);
            }
            c.Modified(filter, "view filter", null);
            c.Output("view_filter", filter.Id);
            c.Summary("edited view filter '" + filter.Name + "' (" + string.Join(", ", changed) + ")");
        }

        [Op("view_graphics.filter_apply")]
        public static void FilterApply(ChangeContext c)
        {
            Document doc = c.Doc;
            FilterElement filter = FindFilter(doc, c.Str("view_filter"), c.Args.Field("view_filter"));
            List<View> views = TargetViews(c);
            foreach (View view in views) ApplyFilter(c, view, filter);
            c.Output("view_filter", filter.Id);
            c.Data(new Dictionary<string, object> { ["view_filter"] = filter.Name, ["views"] = views.Select(v => (object)v.Name).ToList() });
            c.Summary("applied view filter '" + filter.Name + "' to " + (views.Count == 1 ? "'" + views[0].Name + "'" : Count(views.Count, "view")));
        }

        [Op("view_graphics.filter_remove")]
        public static void FilterRemove(ChangeContext c)
        {
            Document doc = c.Doc;
            FilterElement filter = FindFilter(doc, c.Str("view_filter"), c.Args.Field("view_filter"));
            List<View> views = TargetViews(c);
            int removed = 0;
            foreach (View view in views)
            {
                if (!view.IsFilterApplied(filter.Id))
                {
                    c.Warn(WarningCodes.PartialResult, "the filter '" + filter.Name + "' is not applied to '" + view.Name + "'");
                    continue;
                }
                ViewKit.EnsureNotTemplateControlled(view, "the filters", BuiltInParameter.VIS_GRAPHICS_FILTERS);
                view.RemoveFilter(filter.Id);
                c.Modified(view, "filters", null);
                removed++;
            }
            c.Data(new Dictionary<string, object> { ["view_filter"] = filter.Name, ["removed_from"] = removed });
            c.Summary("removed view filter '" + filter.Name + "' from " + Count(removed, "view"));
        }

        // --------------------------------------------------------------------------------------------------- helpers

        private static void ApplyFilter(ChangeContext c, View view, FilterElement filter)
        {
            EnsureFilterApplied(view, filter, c);
            bool visible = c.Args.BoolOpt("visible") ?? true;
            if (view.GetFilterVisibility(filter.Id) != visible) view.SetFilterVisibility(filter.Id, visible);
            if (GraphicsKit.HasOverrideArgs(c.Args))
            {
                view.SetFilterOverrides(filter.Id, GraphicsKit.Build(c.Doc, view.GetFilterOverrides(filter.Id), c.Args, false, null));
            }
            c.Modified(view, "filters", null);
        }

        private static void EnsureFilterApplied(View view, FilterElement filter, ChangeContext c)
        {
            if (!view.AreGraphicsOverridesAllowed())
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + view.Name + "' (" + view.ViewType + ") does not take view filters.");
            }
            ViewKit.EnsureNotTemplateControlled(view, "the filters", BuiltInParameter.VIS_GRAPHICS_FILTERS);
            if (view.IsFilterApplied(filter.Id)) return;
            try
            {
                view.AddFilter(filter.Id);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw OpException.InvalidArgs(c.Args.Field("view_filter"), "the filter '" + filter.Name + "' cannot be applied to '" + view.Name + "': " + ex.Message);
            }
        }

        private static View GraphicsView(ChangeContext c)
        {
            View view = c.View("view");
            if (!view.AreGraphicsOverridesAllowed())
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + view.Name + "' (" + view.ViewType + ") does not take graphic overrides.");
            }
            return view;
        }

        private static List<View> TargetViews(ChangeContext c)
        {
            var views = new List<View>();
            if (c.Has("views"))
            {
                foreach (string name in c.Args.Strs("views"))
                {
                    View view = Resolve.View(c.Doc, name, c.Args.Field("views"), c.Ctx.UiDoc);
                    if (views.All(v => v.Id != view.Id)) views.Add(view);
                }
            }
            else
            {
                views.Add(c.View("view"));
            }
            if (views.Count == 0) throw OpException.InvalidArgs("view", "give view or views");
            return views;
        }

        internal static FilterElement FindFilter(Document doc, string value, string param)
        {
            if (string.IsNullOrWhiteSpace(value)) throw OpException.InvalidArgs(param, "is required (a view filter name)");
            FilterElement byId = Resolve.ById<FilterElement>(doc, value);
            if (byId != null) return byId;
            List<FilterElement> filters = new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>().ToList();
            return Resolve.Single(filters, f => f.Name, value, param, "view filter");
        }

        private static string NextFilterName(Document doc, string name)
        {
            for (int i = 2; i < 200; i++)
            {
                string candidate = name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                if (ParameterFilterElement.IsNameUnique(doc, candidate)) return candidate;
            }
            return name + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        private static string Count(int n, string noun, string plural = null)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? noun : plural ?? noun + "s");
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return false; }
        }
    }
}
