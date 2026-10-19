using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// list.&lt;kind&gt; (34 kinds, SPEC Appendix A/B and §5.9): compact rows of one kind of item, sorted stably so offset
    /// pages are deterministic. Common filters: name (contains / * wildcard), ids, where ('Param op value', elements
    /// only), fields (extra columns via Rows.Field). Kind filters: category, level, view_type, family, for_element,
    /// placed, graphical, printable, class. Filters that do not apply to the kind give warn PARAM_IGNORED.
    /// </summary>
    internal static class ListOps
    {
        /// <summary>One list row: the element it came from (null for worksets, units, project info) and its values.</summary>
        private sealed class Item
        {
            public Element Element;
            public long? Id;
            public string Name;
            public List<object> Values;
        }

        private sealed class Spec
        {
            public string[] Cols;
            public string[] FullCols;
            public string[] Accepts = new string[0];
            /// <summary>Items with values for Cols (+FullCols when full).</summary>
            public Func<ListRun, IEnumerable<Item>> Items;
            public string Singular;
            public string Plural;
        }

        private sealed class ListRun
        {
            public RequestContext Ctx;
            public Document Doc;
            public bool Full;
            public string Kind;
        }

        private static readonly string[] Common = { "name", "ids", "where", "fields" };

        private static readonly Dictionary<string, Spec> Specs = new Dictionary<string, Spec>(StringComparer.Ordinal)
        {
            ["levels"] = new Spec { Cols = new[] { "id", "name", "elevation" }, FullCols = new[] { "story", "plan_views" }, Items = Levels, Singular = "level" },
            ["grids"] = new Spec { Cols = new[] { "id", "name", "start", "end" }, FullCols = new[] { "curved", "type" }, Items = Grids, Singular = "grid" },
            ["views"] = new Spec { Cols = new[] { "id", "name", "view_type", "level", "scale", "sheet" }, FullCols = new[] { "template", "discipline", "detail_level", "phase" }, Accepts = new[] { "view_type", "level", "placed", "graphical", "printable" }, Items = Views, Singular = "view" },
            ["sheets"] = new Spec { Cols = new[] { "id", "number", "name", "views", "revision" }, FullCols = new[] { "title_block", "placeholder", "issue_date" }, Items = Sheets, Singular = "sheet" },
            ["schedules"] = new Spec { Cols = new[] { "id", "name", "category", "sheets" }, FullCols = new[] { "fields", "itemized" }, Accepts = new[] { "category", "placed" }, Items = Schedules, Singular = "schedule" },
            ["rooms"] = new Spec { Cols = new[] { "id", "number", "name", "level", "area", "status" }, FullCols = new[] { "department", "volume", "perimeter", "phase" }, Accepts = new[] { "level", "placed" }, Items = Rooms, Singular = "room" },
            ["areas"] = new Spec { Cols = new[] { "id", "number", "name", "level", "area", "scheme", "status" }, FullCols = new[] { "perimeter" }, Accepts = new[] { "level", "placed" }, Items = Areas, Singular = "area" },
            ["spaces"] = new Spec { Cols = new[] { "id", "number", "name", "level", "area", "room", "status" }, FullCols = new[] { "volume", "phase" }, Accepts = new[] { "level", "placed" }, Items = Spaces, Singular = "space" },
            ["types"] = new Spec { Cols = new[] { "id", "category", "family", "name", "class" }, FullCols = new[] { "instances", "type_mark" }, Accepts = new[] { "category", "family", "for_element", "class" }, Items = Types, Singular = "type" },
            ["families"] = new Spec { Cols = new[] { "id", "name", "category", "types" }, FullCols = new[] { "in_place", "editable", "placement" }, Accepts = new[] { "category" }, Items = Families, Singular = "family", Plural = "families" },
            ["materials"] = new Spec { Cols = new[] { "id", "name", "class" }, FullCols = new[] { "color", "transparency", "category" }, Items = Materials, Singular = "material" },
            ["worksets"] = new Spec { Cols = new[] { "id", "name", "open", "editable", "owner" }, FullCols = new[] { "default", "visible_by_default" }, Items = Worksets, Singular = "workset" },
            ["phases"] = new Spec { Cols = new[] { "id", "name", "sequence" }, FullCols = new string[0], Items = Phases, Singular = "phase" },
            ["phase_filters"] = new Spec { Cols = new[] { "id", "name" }, FullCols = new string[0], Items = PhaseFilters, Singular = "phase filter" },
            ["design_options"] = new Spec { Cols = new[] { "id", "name", "set", "primary" }, FullCols = new string[0], Items = DesignOptions, Singular = "design option" },
            ["links"] = new Spec { Cols = new[] { "id", "name", "type_id", "file", "loaded" }, FullCols = new[] { "path", "pinned", "attachment" }, Items = Links, Singular = "link" },
            ["revisions"] = new Spec { Cols = new[] { "id", "sequence", "number", "date", "description", "issued" }, FullCols = new[] { "issued_to", "issued_by", "numbering" }, Items = Revisions, Singular = "revision" },
            ["view_templates"] = new Spec { Cols = new[] { "id", "name", "view_type" }, FullCols = new[] { "scale", "detail_level" }, Accepts = new[] { "view_type" }, Items = ViewTemplates, Singular = "view template" },
            ["view_filters"] = new Spec { Cols = new[] { "id", "name", "kind", "categories" }, FullCols = new string[0], Accepts = new[] { "category" }, Items = ViewFilters, Singular = "view filter" },
            ["view_types"] = new Spec { Cols = new[] { "id", "name", "view_family" }, FullCols = new[] { "template" }, Accepts = new[] { "view_type" }, Items = ViewTypes, Singular = "view type" },
            ["viewport_types"] = new Spec { Cols = new[] { "id", "name" }, FullCols = new string[0], Items = ViewportTypes, Singular = "viewport type" },
            ["title_blocks"] = new Spec { Cols = new[] { "id", "family", "name" }, FullCols = new[] { "width", "height", "instances" }, Accepts = new[] { "family" }, Items = TitleBlocks, Singular = "title block" },
            ["text_types"] = new Spec { Cols = new[] { "id", "name", "size", "font" }, FullCols = new[] { "bold", "italic", "width_factor" }, Items = TextTypes, Singular = "text type" },
            ["dimension_types"] = new Spec { Cols = new[] { "id", "name", "style" }, FullCols = new[] { "text_size" }, Items = DimensionTypes, Singular = "dimension type" },
            ["tag_types"] = new Spec { Cols = new[] { "id", "category", "family", "name" }, FullCols = new string[0], Accepts = new[] { "category", "family" }, Items = TagTypes, Singular = "tag type" },
            ["line_styles"] = new Spec { Cols = new[] { "id", "name", "weight", "color", "pattern" }, FullCols = new string[0], Items = LineStyles, Singular = "line style" },
            ["line_patterns"] = new Spec { Cols = new[] { "id", "name" }, FullCols = new string[0], Items = LinePatterns, Singular = "line pattern" },
            ["fill_patterns"] = new Spec { Cols = new[] { "id", "name", "target", "solid" }, FullCols = new string[0], Items = FillPatterns, Singular = "fill pattern" },
            ["scope_boxes"] = new Spec { Cols = new[] { "id", "name" }, FullCols = new[] { "bbox" }, Items = ScopeBoxes, Singular = "scope box", Plural = "scope boxes" },
            ["groups"] = new Spec { Cols = new[] { "id", "name", "kind", "instances" }, FullCols = new string[0], Items = Groups, Singular = "group type" },
            ["systems"] = new Spec { Cols = new[] { "id", "name", "class", "type" }, FullCols = new[] { "elements" }, Items = Systems, Singular = "system" },
            ["system_types"] = new Spec { Cols = new[] { "id", "name", "class", "classification" }, FullCols = new[] { "abbreviation" }, Items = SystemTypes, Singular = "system type" },
            ["project_info"] = new Spec { Cols = new[] { "param", "value" }, FullCols = new string[0], Items = ProjectInfoRows, Singular = "project information value" },
            ["units"] = new Spec { Cols = new[] { "spec", "unit", "accuracy", "symbol" }, FullCols = new string[0], Items = UnitRows, Singular = "unit setting" },
        };

        [Op("list.*")]
        public static OpResult List(RequestContext ctx)
        {
            string kind = ctx.Op ?? ctx.Args.Str("kind");
            if (string.IsNullOrWhiteSpace(kind) || !Specs.TryGetValue(kind, out Spec spec))
            {
                throw OpException.InvalidArgs("kind", "must be one of " + string.Join(", ", Specs.Keys));
            }
            Document doc = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document is open.");
            var run = new ListRun { Ctx = ctx, Doc = doc, Full = ReadKit.Full(ctx), Kind = kind };
            ReadKit.IgnoreOthers(ctx, "list " + kind, Common.Concat(spec.Accepts).ToArray());

            Func<string, bool> nameTest = ReadKit.NameMatcher(ctx.Args.Str("name"));
            List<long> onlyIds = ReadKit.SelectorIds(ctx, doc, "ids");
            HashSet<long> idSet = onlyIds == null ? null : new HashSet<long>(onlyIds);
            List<WhereRule> where = ctx.Args.Strs("where").Select(w => ElementQuery.ParseWhere(w, ctx.Args.Field("where"))).ToList();
            List<string> fields = ctx.Args.Strs("fields");

            var items = new List<Item>();
            int scanned = 0;
            bool truncated = false;
            foreach (Item item in spec.Items(run))
            {
                if (ctx.ShouldYield(scanned++))
                {
                    truncated = true;
                    break;
                }
                if (!nameTest(item.Name)) continue;
                if (idSet != null && (!item.Id.HasValue || !idSet.Contains(item.Id.Value))) continue;
                if (where.Count > 0)
                {
                    if (item.Element == null || !where.All(rule => ElementQuery.Evaluate(item.Element, rule))) continue;
                }
                items.Add(item);
            }
            if (truncated)
            {
                ctx.Warn(WarningCodes.PartialResult, "stopped at the time budget after " + scanned.ToString(CultureInfo.InvariantCulture) + " items; narrow the list with name= or a filter");
            }

            List<string> cols = spec.Cols.ToList();
            if (run.Full) cols.AddRange(spec.FullCols);
            int baseCount = cols.Count;
            cols.AddRange(fields.Where(f => !cols.Contains(f, StringComparer.OrdinalIgnoreCase)));
            var rowOptions = new RowOptions { Full = run.Full };
            List<string> extraFields = cols.Skip(baseCount).ToList();
            if (extraFields.Count > 0 && items.Any(i => i.Element == null))
            {
                ctx.Warn(WarningCodes.ParamIgnored, "fields do not apply to list " + kind);
            }

            string noun = items.Count == 1 ? spec.Singular : spec.Plural ?? spec.Singular + "s";
            string summary = items.Count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + Describe(ctx, kind);
            if (items.Count == 0 && kind == "worksets" && !doc.IsWorkshared) summary = "no worksets: '" + doc.Title + "' is not workshared";

            return ReadKit.Page(ctx, items, cols, item =>
            {
                var row = new List<object>(item.Values.Take(baseCount));
                while (row.Count < baseCount) row.Add(null);
                foreach (string field in extraFields) row.Add(item.Element == null ? null : Rows.Field(item.Element, field, rowOptions));
                return row;
            }, summary);
        }

        private static string Describe(RequestContext ctx, string kind)
        {
            var parts = new List<string>();
            string name = ctx.Args.Str("name");
            if (!string.IsNullOrWhiteSpace(name)) parts.Add("name " + name);
            foreach (string key in new[] { "view_type", "level", "family", "class", "for_element" })
            {
                string value = ctx.Args.Has(key) ? ctx.Args.Str(key) : null;
                if (!string.IsNullOrWhiteSpace(value)) parts.Add(key.Replace('_', ' ') + " " + value);
            }
            List<string> categories = ctx.Args.Strs("category");
            if (categories.Count > 0) parts.Add(string.Join(", ", categories));
            bool? placed = ctx.Args.BoolOpt("placed");
            if (placed.HasValue) parts.Add(placed.Value ? "placed" : "not placed");
            if (ctx.Args.Bool("graphical")) parts.Add("graphical");
            if (ctx.Args.Bool("printable")) parts.Add("printable");
            return parts.Count == 0 ? string.Empty : " (" + string.Join("; ", parts) + ")";
        }

        private static Item Row(Element element, string name, params object[] values)
        {
            return new Item { Element = element, Id = element?.Id.Value, Name = name, Values = values.ToList() };
        }

        private static IEnumerable<T> All<T>(Document doc) where T : Element
        {
            return new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>();
        }

        private static HashSet<BuiltInCategory> CategoryFilter(ListRun run)
        {
            List<string> values = run.Ctx.Args.Strs("category");
            if (values.Count == 0) return null;
            return new HashSet<BuiltInCategory>(Resolve.Categories(run.Doc, values, run.Ctx.Args.Field("category")));
        }

        private static bool InCategories(Element element, HashSet<BuiltInCategory> categories)
        {
            if (categories == null) return true;
            BuiltInCategory category = Resolve.SafeBuiltIn(SafeCategory(element));
            return category != BuiltInCategory.INVALID && categories.Contains(category);
        }

        private static Category SafeCategory(Element element)
        {
            try { return element?.Category; } catch { return null; }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Datums
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<Item> Levels(ListRun run)
        {
            Dictionary<long, int> planViews = null;
            if (run.Full)
            {
                planViews = new Dictionary<long, int>();
                foreach (ViewPlan plan in All<ViewPlan>(run.Doc))
                {
                    if (plan.IsTemplate || plan.GenLevel == null) continue;
                    long id = plan.GenLevel.Id.Value;
                    planViews.TryGetValue(id, out int count);
                    planViews[id] = count + 1;
                }
            }
            foreach (Level level in All<Level>(run.Doc).OrderBy(l => l.Elevation).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                object story = ReadKit.BipValue(level, BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                int views = 0;
                planViews?.TryGetValue(level.Id.Value, out views);
                yield return Row(level, level.Name, level.Id.Value, level.Name, Units.Mm(level.Elevation), story, views);
            }
        }

        private static IEnumerable<Item> Grids(ListRun run)
        {
            foreach (Grid grid in All<Grid>(run.Doc).OrderBy(g => g.Name, NaturalComparer.Instance))
            {
                Curve curve = null;
                try { curve = grid.Curve; } catch { curve = null; }
                bool curved = false;
                try { curved = grid.IsCurved; } catch { }
                yield return Row(grid, grid.Name, grid.Id.Value, grid.Name,
                    curve == null ? null : OutputJson.Point2(curve.GetEndPoint(0)),
                    curve == null ? null : OutputJson.Point2(curve.GetEndPoint(1)),
                    curved, Rows.TypeName(grid));
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Views, sheets, schedules
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>view id → sheet numbers (viewports and schedule instances).</summary>
        internal static Dictionary<long, List<string>> SheetsByView(Document doc)
        {
            var map = new Dictionary<long, List<string>>();
            var sheetNumbers = new Dictionary<long, string>();
            foreach (ViewSheet sheet in All<ViewSheet>(doc)) sheetNumbers[sheet.Id.Value] = sheet.SheetNumber;
            void Add(long viewId, long sheetId)
            {
                if (!sheetNumbers.TryGetValue(sheetId, out string number)) return;
                if (!map.TryGetValue(viewId, out List<string> list)) map[viewId] = list = new List<string>();
                if (!list.Contains(number)) list.Add(number);
            }
            foreach (Viewport viewport in All<Viewport>(doc))
            {
                try { Add(viewport.ViewId.Value, viewport.SheetId.Value); } catch { }
            }
            foreach (ScheduleSheetInstance instance in All<ScheduleSheetInstance>(doc))
            {
                try
                {
                    if (instance.IsTitleblockRevisionSchedule) continue;
                    Add(instance.ScheduleId.Value, instance.OwnerViewId.Value);
                }
                catch
                {
                    // Skip broken instances.
                }
            }
            return map;
        }

        internal static bool IsBrowserOrInternal(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Internal:
                case ViewType.Undefined:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Graphical: not a template, schedule, sheet list or browser/internal view (SPEC §5.9).</summary>
        internal static bool IsGraphical(View view)
        {
            if (view.IsTemplate || IsBrowserOrInternal(view) || view is ViewSchedule || view is TableView) return false;
            switch (view.ViewType)
            {
                case ViewType.Schedule:
                case ViewType.PanelSchedule:
                case ViewType.ColumnSchedule:
                case ViewType.Report:
                case ViewType.CostReport:
                case ViewType.LoadsReport:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>Printable: !IsTemplate &amp;&amp; CanBePrinted and not Schedule/ProjectBrowser/SystemBrowser/Internal (SPEC §5.9).</summary>
        internal static bool IsPrintable(View view)
        {
            if (view.IsTemplate || IsBrowserOrInternal(view) || view.ViewType == ViewType.Schedule) return false;
            try { return view.CanBePrinted; } catch { return false; }
        }

        private static bool ViewTypeMatches(string wanted, ViewType type)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return true;
            string w = Normalize(wanted);
            string t = Normalize(type.ToString());
            if (w == t) return true;
            switch (w)
            {
                case "3d": case "threed": case "3dview": return type == ViewType.ThreeD;
                case "plan": case "floor": return type == ViewType.FloorPlan;
                case "ceiling": case "rcp": return type == ViewType.CeilingPlan;
                case "sheet": case "sheets": return type == ViewType.DrawingSheet;
                case "drafting": return type == ViewType.DraftingView;
                case "structural": case "structuralplan": return type == ViewType.EngineeringPlan;
                case "schedules": return type == ViewType.Schedule;
                case "detail": case "callout": return type == ViewType.Detail;
                default: return false;
            }
        }

        private static string Normalize(string text)
        {
            return new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static ElementId LevelFilter(ListRun run)
        {
            string level = run.Ctx.Args.Str("level");
            return string.IsNullOrWhiteSpace(level) ? null : Resolve.Level(run.Doc, level, run.Ctx.Args.Field("level"), run.Ctx.UiDoc).Id;
        }

        private static IEnumerable<Item> Views(ListRun run)
        {
            Document doc = run.Doc;
            PayloadReader args = run.Ctx.Args;
            string viewType = args.Str("view_type");
            ElementId level = LevelFilter(run);
            bool? placed = args.BoolOpt("placed");
            bool graphical = args.Bool("graphical");
            bool printable = args.Bool("printable");
            Dictionary<long, List<string>> sheets = SheetsByView(doc);
            IEnumerable<View> views = All<View>(doc)
                .Where(v => !v.IsTemplate && !IsBrowserOrInternal(v) && !(v is ViewSheet))
                .Where(v => !(v is ViewSchedule s) || !SafeRevisionSchedule(s))
                .OrderBy(v => v.ViewType.ToString(), StringComparer.Ordinal)
                .ThenBy(v => v.Name, NaturalComparer.Instance);
            if (!string.IsNullOrWhiteSpace(viewType) && Normalize(viewType) == "sheet")
            {
                run.Ctx.Warn(WarningCodes.ParamIgnored, "sheets are listed with list {kind:\"sheets\"}");
            }
            foreach (View view in views)
            {
                if (!ViewTypeMatches(viewType, view.ViewType)) continue;
                if (graphical && !IsGraphical(view)) continue;
                if (printable && !IsPrintable(view)) continue;
                if (level != null && (view.GenLevel == null || view.GenLevel.Id != level)) continue;
                sheets.TryGetValue(view.Id.Value, out List<string> on);
                if (placed.HasValue && placed.Value != (on != null && on.Count > 0)) continue;
                object scale = null;
                try { if (IsGraphical(view)) scale = view.Scale; } catch { scale = null; }
                object discipline = null, detail = null, phase = null;
                if (run.Full)
                {
                    try { discipline = view.Discipline.ToString(); } catch { }
                    try { detail = view.DetailLevel.ToString(); } catch { }
                    phase = ReadKit.NameOf(doc, ReadKit.BipId(view, BuiltInParameter.VIEW_PHASE));
                }
                yield return Row(view, view.Name, view.Id.Value, view.Name, view.ViewType.ToString(), view.GenLevel?.Name, scale,
                    on == null ? null : (object)string.Join(", ", on),
                    ReadKit.NameOf(doc, view.ViewTemplateId), discipline, detail, phase);
            }
        }

        private static bool SafeRevisionSchedule(ViewSchedule schedule)
        {
            try { return schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule; } catch { return false; }
        }

        private static IEnumerable<Item> Sheets(ListRun run)
        {
            Document doc = run.Doc;
            Dictionary<long, string> titleBlocks = null;
            if (run.Full)
            {
                titleBlocks = new Dictionary<long, string>();
                foreach (FamilyInstance block in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    try
                    {
                        long owner = block.OwnerViewId.Value;
                        if (!titleBlocks.ContainsKey(owner)) titleBlocks[owner] = Rows.TypeName(block);
                    }
                    catch
                    {
                        // Skip.
                    }
                }
            }
            foreach (ViewSheet sheet in All<ViewSheet>(doc).Where(s => !s.IsTemplate).OrderBy(s => s.SheetNumber, NaturalComparer.Instance))
            {
                int views = 0;
                try { views = sheet.GetAllPlacedViews().Count; } catch { }
                string revision = null;
                try
                {
                    ElementId current = sheet.GetCurrentRevision();
                    if (current != null && current != ElementId.InvalidElementId && doc.GetElement(current) is Revision rev)
                    {
                        revision = string.IsNullOrEmpty(rev.RevisionNumber) ? rev.Description : rev.RevisionNumber;
                    }
                }
                catch
                {
                    revision = null;
                }
                string block = null;
                titleBlocks?.TryGetValue(sheet.Id.Value, out block);
                bool placeholder = false;
                try { placeholder = sheet.IsPlaceholder; } catch { }
                yield return Row(sheet, sheet.SheetNumber + " " + sheet.Name, sheet.Id.Value, sheet.SheetNumber, sheet.Name, views, revision,
                    block, placeholder, ReadKit.BipString(sheet, BuiltInParameter.SHEET_ISSUE_DATE));
            }
        }

        private static IEnumerable<Item> Schedules(ListRun run)
        {
            Document doc = run.Doc;
            HashSet<BuiltInCategory> categories = CategoryFilter(run);
            bool? placed = run.Ctx.Args.BoolOpt("placed");
            Dictionary<long, List<string>> sheets = SheetsByView(doc);
            foreach (ViewSchedule schedule in All<ViewSchedule>(doc).Where(s => !s.IsTemplate && !SafeRevisionSchedule(s)).OrderBy(s => s.Name, NaturalComparer.Instance))
            {
                string category = null;
                BuiltInCategory builtIn = BuiltInCategory.INVALID;
                ScheduleDefinition definition = null;
                try
                {
                    definition = schedule.Definition;
                    ElementId categoryId = definition.CategoryId;
                    if (categoryId != null && categoryId != ElementId.InvalidElementId)
                    {
                        Category cat = Category.GetCategory(doc, categoryId);
                        builtIn = Resolve.SafeBuiltIn(cat);
                        category = Rows.CategoryLabel(cat);
                    }
                    else
                    {
                        category = "Multi-Category";
                    }
                }
                catch
                {
                    category = null;
                }
                if (categories != null && (builtIn == BuiltInCategory.INVALID || !categories.Contains(builtIn))) continue;
                sheets.TryGetValue(schedule.Id.Value, out List<string> on);
                if (placed.HasValue && placed.Value != (on != null && on.Count > 0)) continue;
                object fieldCount = null, itemized = null;
                if (run.Full && definition != null)
                {
                    try { fieldCount = definition.GetFieldCount(); } catch { }
                    try { itemized = definition.IsItemized; } catch { }
                }
                yield return Row(schedule, schedule.Name, schedule.Id.Value, schedule.Name, category,
                    on == null ? null : (object)string.Join(", ", on), fieldCount, itemized);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Spatial elements
        // ---------------------------------------------------------------------------------------------------------

        private static string SpatialStatus(SpatialElement element)
        {
            if (element.Location == null) return "unplaced";
            double area = 0;
            try { area = element.Area; } catch { }
            if (area > 1e-9) return "placed";
            // Placed with zero area: not enclosed or redundant (Revit reports both as area 0).
            return "not_enclosed";
        }

        private static IEnumerable<T> Spatial<T>(ListRun run, BuiltInCategory category) where T : SpatialElement
        {
            ElementId level = LevelFilter(run);
            bool? placed = run.Ctx.Args.BoolOpt("placed");
            IEnumerable<T> items = new FilteredElementCollector(run.Doc).OfCategory(category).WhereElementIsNotElementType().OfType<T>();
            foreach (T element in items.OrderBy(e => SafeNumber(e), NaturalComparer.Instance).ThenBy(e => e.Id.Value))
            {
                if (level != null && element.LevelId != level) continue;
                if (placed.HasValue && placed.Value != (element.Location != null)) continue;
                yield return element;
            }
        }

        private static string SafeNumber(SpatialElement element)
        {
            try { return element.Number ?? string.Empty; } catch { return string.Empty; }
        }

        private static string SpatialName(SpatialElement element)
        {
            return ReadKit.BipString(element, BuiltInParameter.ROOM_NAME) ?? ReadKit.SafeName(element);
        }

        private static IEnumerable<Item> Rooms(ListRun run)
        {
            Document doc = run.Doc;
            foreach (Room room in Spatial<Room>(run, BuiltInCategory.OST_Rooms))
            {
                string name = SpatialName(room);
                yield return Row(room, room.Number + " " + name, room.Id.Value, room.Number, name, ReadKit.NameOf(doc, room.LevelId),
                    Units.Area(room.Area), SpatialStatus(room),
                    ReadKit.BipString(room, BuiltInParameter.ROOM_DEPARTMENT), Units.Volume(SafeVolume(room)), Units.Mm(SafePerimeter(room)),
                    ReadKit.NameOf(doc, ReadKit.BipId(room, BuiltInParameter.ROOM_PHASE)));
            }
        }

        private static double SafeVolume(SpatialElement element)
        {
            try
            {
                Parameter parameter = element.get_Parameter(BuiltInParameter.ROOM_VOLUME);
                return parameter != null && parameter.HasValue ? parameter.AsDouble() : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double SafePerimeter(SpatialElement element)
        {
            try { return element.Perimeter; } catch { return 0; }
        }

        private static IEnumerable<Item> Areas(ListRun run)
        {
            Document doc = run.Doc;
            foreach (Area area in Spatial<Area>(run, BuiltInCategory.OST_Areas))
            {
                string name = SpatialName(area);
                string scheme = null;
                try { scheme = area.AreaScheme?.Name; } catch { }
                yield return Row(area, area.Number + " " + name, area.Id.Value, area.Number, name, ReadKit.NameOf(doc, area.LevelId),
                    Units.Area(area.Area), scheme, SpatialStatus(area), Units.Mm(SafePerimeter(area)));
            }
        }

        private static IEnumerable<Item> Spaces(ListRun run)
        {
            Document doc = run.Doc;
            foreach (Space space in Spatial<Space>(run, BuiltInCategory.OST_MEPSpaces))
            {
                string name = SpatialName(space);
                string room = null;
                try { room = space.Room?.Number; } catch { }
                yield return Row(space, space.Number + " " + name, space.Id.Value, space.Number, name, ReadKit.NameOf(doc, space.LevelId),
                    Units.Area(space.Area), room, SpatialStatus(space), Units.Volume(SafeVolume(space)),
                    ReadKit.NameOf(doc, ReadKit.BipId(space, BuiltInParameter.ROOM_PHASE)));
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Types and families
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<Item> Types(ListRun run)
        {
            Document doc = run.Doc;
            PayloadReader args = run.Ctx.Args;
            HashSet<BuiltInCategory> categories = CategoryFilter(run);
            Func<string, bool> familyTest = ReadKit.NameMatcher(args.Str("family"));
            bool familyGiven = !string.IsNullOrWhiteSpace(args.Str("family"));
            string className = args.Str("class");
            Type classType = string.IsNullOrWhiteSpace(className) ? null : ResolveTypeClass(className, args.Field("class"));

            HashSet<long> valid = null;
            long currentType = -1;
            string forElement = args.Str("for_element");
            if (!string.IsNullOrWhiteSpace(forElement))
            {
                Element target = Resolve.ById<Element>(doc, forElement);
                if (target == null)
                {
                    try { target = doc.GetElement(forElement.Trim()); } catch { target = null; }
                }
                if (target == null) throw OpException.NotFound(args.Field("for_element"), "element", forElement, new string[0]);
                valid = new HashSet<long>();
                try
                {
                    foreach (ElementId id in target.GetValidTypes()) valid.Add(id.Value);
                }
                catch
                {
                    // Elements without switchable types.
                }
                if (target is FamilyInstance instance && valid.Count == 0)
                {
                    try
                    {
                        foreach (ElementId id in instance.Symbol.Family.GetFamilySymbolIds()) valid.Add(id.Value);
                    }
                    catch
                    {
                        // No family.
                    }
                }
                ElementId typeId = target.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    currentType = typeId.Value;
                    valid.Add(currentType);
                }
            }

            Dictionary<long, int> instances = run.Full ? InstanceCounts(doc, categories) : null;
            FilteredElementCollector collector = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (categories != null && categories.Count > 0)
            {
                collector = collector.WherePasses(new ElementMulticategoryFilter(categories.ToList()));
            }
            if (valid != null && valid.Count > 0)
            {
                collector = collector.WherePasses(new ElementIdSetFilter(valid.Select(id => new ElementId(id)).ToList()));
            }
            else if (valid != null)
            {
                yield break;
            }

            IEnumerable<ElementType> types = collector.OfType<ElementType>()
                .Where(t => classType != null || SafeCategory(t) != null)
                .OrderBy(t => Rows.CategoryLabel(t) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.FamilyName ?? string.Empty, NaturalComparer.Instance)
                .ThenBy(t => t.Name, NaturalComparer.Instance);
            foreach (ElementType type in types)
            {
                if (classType != null && !classType.IsInstanceOfType(type)) continue;
                if (familyGiven && !familyTest(type.FamilyName)) continue;
                int count = 0;
                instances?.TryGetValue(type.Id.Value, out count);
                string typeMark = run.Full ? ReadKit.BipString(type, BuiltInParameter.ALL_MODEL_TYPE_MARK) ?? ReadKit.BipString(type, BuiltInParameter.WINDOW_TYPE_ID) : null;
                Item item = Row(type, type.Name, type.Id.Value, Rows.CategoryLabel(type), type.FamilyName, type.Name, type.GetType().Name, count, typeMark);
                if (currentType == type.Id.Value) item.Values[4] = type.GetType().Name + " (current)";
                yield return item;
            }
        }

        private static Dictionary<long, int> InstanceCounts(Document doc, HashSet<BuiltInCategory> categories)
        {
            var counts = new Dictionary<long, int>();
            FilteredElementCollector collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (categories != null && categories.Count > 0) collector = collector.WherePasses(new ElementMulticategoryFilter(categories.ToList()));
            foreach (ElementId id in collector.ToElementIds())
            {
                Element element = doc.GetElement(id);
                ElementId typeId;
                try { typeId = element?.GetTypeId(); } catch { typeId = null; }
                if (typeId == null || typeId == ElementId.InvalidElementId) continue;
                counts.TryGetValue(typeId.Value, out int count);
                counts[typeId.Value] = count + 1;
            }
            return counts;
        }

        private static Type ResolveTypeClass(string name, string field)
        {
            string text = name.Trim();
            foreach (string ns in new[] { "Autodesk.Revit.DB.", "Autodesk.Revit.DB.Architecture.", "Autodesk.Revit.DB.Mechanical.",
                                          "Autodesk.Revit.DB.Plumbing.", "Autodesk.Revit.DB.Electrical.", "Autodesk.Revit.DB.Structure." })
            {
                Type type = typeof(Element).Assembly.GetType(ns + text, false, true);
                if (type != null && typeof(ElementType).IsAssignableFrom(type)) return type;
            }
            throw OpException.NotFound(field, "class", name, new[] { "WallType", "FloorType", "CeilingType", "RoofType", "FamilySymbol", "PipeType", "DuctType", "TextNoteType", "DimensionType", "ViewFamilyType", "GroupType" });
        }

        private static IEnumerable<Item> Families(ListRun run)
        {
            HashSet<BuiltInCategory> categories = CategoryFilter(run);
            foreach (Family family in All<Family>(run.Doc).OrderBy(f => SafeFamilyCategory(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, NaturalComparer.Instance))
            {
                Category category = null;
                try { category = family.FamilyCategory; } catch { }
                if (categories != null && !categories.Contains(Resolve.SafeBuiltIn(category))) continue;
                int types = 0;
                try { types = family.GetFamilySymbolIds().Count; } catch { }
                object inPlace = null, editable = null, placement = null;
                try { inPlace = family.IsInPlace; } catch { }
                try { editable = family.IsEditable; } catch { }
                try { placement = family.FamilyPlacementType.ToString(); } catch { }
                yield return Row(family, family.Name, family.Id.Value, family.Name, Rows.CategoryLabel(category), types, inPlace, editable, placement);
            }
        }

        private static string SafeFamilyCategory(Family family)
        {
            try { return Rows.CategoryLabel(family.FamilyCategory); } catch { return null; }
        }

        private static IEnumerable<Item> Materials(ListRun run)
        {
            foreach (Material material in All<Material>(run.Doc).OrderBy(m => m.Name, NaturalComparer.Instance))
            {
                object color = null;
                try
                {
                    Color c = material.Color;
                    if (c != null && c.IsValid) color = "#" + c.Red.ToString("X2", CultureInfo.InvariantCulture) + c.Green.ToString("X2", CultureInfo.InvariantCulture) + c.Blue.ToString("X2", CultureInfo.InvariantCulture);
                }
                catch
                {
                    color = null;
                }
                object transparency = null;
                try { transparency = material.Transparency; } catch { }
                yield return Row(material, material.Name, material.Id.Value, material.Name, material.MaterialClass, color, transparency, material.MaterialCategory);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Worksets, phases, options, links, revisions
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<Item> Worksets(ListRun run)
        {
            Document doc = run.Doc;
            if (!doc.IsWorkshared) yield break;
            WorksetId active = null;
            try { active = doc.GetWorksetTable().GetActiveWorksetId(); } catch { }
            foreach (Workset workset in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name, NaturalComparer.Instance))
            {
                yield return new Item
                {
                    Id = workset.Id.IntegerValue,
                    Name = workset.Name,
                    Values = new List<object>
                    {
                        workset.Id.IntegerValue, workset.Name, workset.IsOpen, workset.IsEditable,
                        string.IsNullOrEmpty(workset.Owner) ? null : workset.Owner,
                        workset.IsDefaultWorkset || (active != null && active == workset.Id), workset.IsVisibleByDefault
                    }
                };
            }
        }

        private static IEnumerable<Item> Phases(ListRun run)
        {
            int sequence = 0;
            foreach (Phase phase in run.Doc.Phases)
            {
                sequence++;
                yield return Row(phase, phase.Name, phase.Id.Value, phase.Name, sequence);
            }
        }

        private static IEnumerable<Item> PhaseFilters(ListRun run)
        {
            foreach (PhaseFilter filter in All<PhaseFilter>(run.Doc).OrderBy(f => f.Name, NaturalComparer.Instance))
            {
                yield return Row(filter, filter.Name, filter.Id.Value, filter.Name);
            }
        }

        private static IEnumerable<Item> DesignOptions(ListRun run)
        {
            Document doc = run.Doc;
            foreach (DesignOption option in All<DesignOption>(doc)
                         .OrderBy(o => ReadKit.NameOf(doc, ReadKit.BipId(o, BuiltInParameter.OPTION_SET_ID)) ?? string.Empty, NaturalComparer.Instance)
                         .ThenBy(o => o.Name, NaturalComparer.Instance))
            {
                string set = ReadKit.NameOf(doc, ReadKit.BipId(option, BuiltInParameter.OPTION_SET_ID));
                bool primary = false;
                try { primary = option.IsPrimary; } catch { }
                yield return Row(option, option.Name, option.Id.Value, option.Name, set, primary);
            }
        }

        private static IEnumerable<Item> Links(ListRun run)
        {
            Document doc = run.Doc;
            foreach (RevitLinkInstance link in All<RevitLinkInstance>(doc).OrderBy(l => l.Name, NaturalComparer.Instance))
            {
                ElementId typeId = link.GetTypeId();
                RevitLinkType type = doc.GetElement(typeId) as RevitLinkType;
                bool loaded = false;
                try { loaded = link.GetLinkDocument() != null; } catch { }
                string path = null;
                try
                {
                    ExternalFileReference reference = type?.GetExternalFileReference();
                    ModelPath modelPath = reference?.GetAbsolutePath();
                    if (modelPath != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                }
                catch
                {
                    path = null;
                }
                string file = string.IsNullOrEmpty(path) ? type?.Name : System.IO.Path.GetFileName(path);
                object attachment = null;
                try { attachment = type?.AttachmentType.ToString(); } catch { }
                yield return Row(link, link.Name, link.Id.Value, link.Name, typeId.Value, file, loaded, path, link.Pinned, attachment);
            }
        }

        private static IEnumerable<Item> Revisions(ListRun run)
        {
            Document doc = run.Doc;
            IList<ElementId> ordered;
            try { ordered = Revision.GetAllRevisionIds(doc); } catch { ordered = All<Revision>(doc).Select(r => r.Id).ToList(); }
            foreach (ElementId id in ordered)
            {
                if (!(doc.GetElement(id) is Revision revision)) continue;
                string numbering = null;
                try { numbering = ReadKit.NameOf(doc, revision.RevisionNumberingSequenceId); } catch { }
                yield return Row(revision, revision.Description, revision.Id.Value, revision.SequenceNumber, revision.RevisionNumber,
                    revision.RevisionDate, revision.Description, revision.Issued, revision.IssuedTo, revision.IssuedBy, numbering);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // View templates, filters, view types, viewport types
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<Item> ViewTemplates(ListRun run)
        {
            string viewType = run.Ctx.Args.Str("view_type");
            foreach (View view in All<View>(run.Doc).Where(v => v.IsTemplate).OrderBy(v => v.ViewType.ToString(), StringComparer.Ordinal).ThenBy(v => v.Name, NaturalComparer.Instance))
            {
                if (!ViewTypeMatches(viewType, view.ViewType)) continue;
                object scale = null, detail = null;
                try { scale = view.Scale; } catch { }
                try { detail = view.DetailLevel.ToString(); } catch { }
                yield return Row(view, view.Name, view.Id.Value, view.Name, view.ViewType.ToString(), scale, detail);
            }
        }

        private static IEnumerable<Item> ViewFilters(ListRun run)
        {
            Document doc = run.Doc;
            HashSet<BuiltInCategory> categories = CategoryFilter(run);
            foreach (FilterElement filter in All<FilterElement>(doc).OrderBy(f => f.Name, NaturalComparer.Instance))
            {
                string kind = filter is ParameterFilterElement ? "rule" : filter is SelectionFilterElement ? "selection" : filter.GetType().Name;
                var labels = new List<string>();
                var builtIns = new HashSet<BuiltInCategory>();
                if (filter is ParameterFilterElement parameterFilter)
                {
                    try
                    {
                        foreach (ElementId id in parameterFilter.GetCategories())
                        {
                            Category category = Category.GetCategory(doc, id);
                            builtIns.Add(Resolve.SafeBuiltIn(category));
                            string label = Rows.CategoryLabel(category);
                            if (!string.IsNullOrEmpty(label)) labels.Add(label);
                        }
                    }
                    catch
                    {
                        // No categories.
                    }
                }
                if (categories != null && !builtIns.Overlaps(categories)) continue;
                labels.Sort(StringComparer.OrdinalIgnoreCase);
                yield return Row(filter, filter.Name, filter.Id.Value, filter.Name, kind, labels.Count == 0 ? null : (object)labels.Cast<object>().ToList());
            }
        }

        private static IEnumerable<Item> ViewTypes(ListRun run)
        {
            Document doc = run.Doc;
            string wanted = run.Ctx.Args.Str("view_type");
            foreach (ViewFamilyType type in All<ViewFamilyType>(doc).OrderBy(t => t.ViewFamily.ToString(), StringComparer.Ordinal).ThenBy(t => t.Name, NaturalComparer.Instance))
            {
                if (!ViewFamilyMatches(wanted, type.ViewFamily)) continue;
                yield return Row(type, type.Name, type.Id.Value, type.Name, type.ViewFamily.ToString(), ReadKit.NameOf(doc, type.DefaultTemplateId));
            }
        }

        private static bool ViewFamilyMatches(string wanted, ViewFamily family)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return true;
            string w = Normalize(wanted);
            string f = Normalize(family.ToString());
            if (w == f) return true;
            switch (w)
            {
                case "3d": case "threed": case "3dview": return family == ViewFamily.ThreeDimensional;
                case "sheet": case "drawingsheet": return family == ViewFamily.Sheet;
                case "drafting": case "draftingview": return family == ViewFamily.Drafting;
                case "engineeringplan": case "structuralplan": return family == ViewFamily.StructuralPlan;
                case "detail": case "callout": return family == ViewFamily.Detail;
                case "schedules": return family == ViewFamily.Schedule;
                default: return false;
            }
        }

        private static IEnumerable<Item> ViewportTypes(ListRun run)
        {
            foreach (ElementType type in SheetSupport.AllViewportTypes(run.Doc).OrderBy(t => t.Name, NaturalComparer.Instance))
            {
                yield return Row(type, type.Name, type.Id.Value, type.Name);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Annotation types
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<Item> TitleBlocks(ListRun run)
        {
            Document doc = run.Doc;
            Func<string, bool> familyTest = ReadKit.NameMatcher(run.Ctx.Args.Str("family"));
            Dictionary<long, int> instances = run.Full ? InstanceCounts(doc, new HashSet<BuiltInCategory> { BuiltInCategory.OST_TitleBlocks }) : null;
            foreach (FamilySymbol symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>()
                         .OrderBy(s => s.FamilyName, NaturalComparer.Instance).ThenBy(s => s.Name, NaturalComparer.Instance))
            {
                if (!familyTest(symbol.FamilyName)) continue;
                int count = 0;
                instances?.TryGetValue(symbol.Id.Value, out count);
                yield return Row(symbol, symbol.Name, symbol.Id.Value, symbol.FamilyName, symbol.Name,
                    ReadKit.BipValue(symbol, BuiltInParameter.SHEET_WIDTH), ReadKit.BipValue(symbol, BuiltInParameter.SHEET_HEIGHT), count);
            }
        }

        private static IEnumerable<Item> TextTypes(ListRun run)
        {
            foreach (TextNoteType type in All<TextNoteType>(run.Doc).OrderBy(t => t.Name, NaturalComparer.Instance))
            {
                yield return Row(type, type.Name, type.Id.Value, type.Name,
                    ReadKit.BipValue(type, BuiltInParameter.TEXT_SIZE), ReadKit.BipString(type, BuiltInParameter.TEXT_FONT),
                    ReadKit.BipValue(type, BuiltInParameter.TEXT_STYLE_BOLD), ReadKit.BipValue(type, BuiltInParameter.TEXT_STYLE_ITALIC),
                    ReadKit.BipValue(type, BuiltInParameter.TEXT_WIDTH_SCALE));
            }
        }

        private static IEnumerable<Item> DimensionTypes(ListRun run)
        {
            foreach (DimensionType type in All<DimensionType>(run.Doc).OrderBy(t => t.StyleType.ToString(), StringComparer.Ordinal).ThenBy(t => t.Name, NaturalComparer.Instance))
            {
                if (string.IsNullOrWhiteSpace(type.Name)) continue;
                yield return Row(type, type.Name, type.Id.Value, type.Name, type.StyleType.ToString(), ReadKit.BipValue(type, BuiltInParameter.TEXT_SIZE));
            }
        }

        private static IEnumerable<Item> TagTypes(ListRun run)
        {
            Document doc = run.Doc;
            HashSet<BuiltInCategory> categories = CategoryFilter(run);
            Func<string, bool> familyTest = ReadKit.NameMatcher(run.Ctx.Args.Str("family"));
            IEnumerable<FamilySymbol> symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => IsTagCategory(SafeCategory(s)))
                .OrderBy(s => Rows.CategoryLabel(s) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.FamilyName, NaturalComparer.Instance).ThenBy(s => s.Name, NaturalComparer.Instance);
            foreach (FamilySymbol symbol in symbols)
            {
                if (!InCategories(symbol, categories)) continue;
                if (!familyTest(symbol.FamilyName)) continue;
                yield return Row(symbol, symbol.Name, symbol.Id.Value, Rows.CategoryLabel(symbol), symbol.FamilyName, symbol.Name);
            }
        }

        internal static bool IsTagCategory(Category category)
        {
            if (category == null) return false;
            try
            {
                return category.IsTagCategory;
            }
            catch
            {
                return category.CategoryType == CategoryType.Annotation && (category.Name ?? string.Empty).EndsWith("Tags", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Lines and patterns
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<Item> LineStyles(ListRun run)
        {
            Document doc = run.Doc;
            Category lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            if (lines == null) yield break;
            var subs = new List<Category>();
            foreach (Category sub in lines.SubCategories) subs.Add(sub);
            foreach (Category sub in subs.OrderBy(c => c.Name, NaturalComparer.Instance))
            {
                GraphicsStyle style = null;
                try { style = sub.GetGraphicsStyle(GraphicsStyleType.Projection); } catch { }
                object weight = null, color = null, pattern = null;
                try { weight = sub.GetLineWeight(GraphicsStyleType.Projection); } catch { }
                try
                {
                    Color c = sub.LineColor;
                    if (c != null && c.IsValid) color = "#" + c.Red.ToString("X2", CultureInfo.InvariantCulture) + c.Green.ToString("X2", CultureInfo.InvariantCulture) + c.Blue.ToString("X2", CultureInfo.InvariantCulture);
                }
                catch
                {
                    color = null;
                }
                try
                {
                    ElementId patternId = sub.GetLinePatternId(GraphicsStyleType.Projection);
                    pattern = patternId == LinePatternElement.GetSolidPatternId() ? "Solid" : ReadKit.NameOf(doc, patternId);
                }
                catch
                {
                    pattern = null;
                }
                yield return new Item { Element = style, Id = style?.Id.Value, Name = sub.Name, Values = new List<object> { style?.Id.Value, sub.Name, weight, color, pattern } };
            }
        }

        private static IEnumerable<Item> LinePatterns(ListRun run)
        {
            foreach (LinePatternElement pattern in All<LinePatternElement>(run.Doc).OrderBy(p => p.Name, NaturalComparer.Instance))
            {
                yield return Row(pattern, pattern.Name, pattern.Id.Value, pattern.Name);
            }
        }

        private static IEnumerable<Item> FillPatterns(ListRun run)
        {
            foreach (FillPatternElement element in All<FillPatternElement>(run.Doc).OrderBy(p => p.Name, NaturalComparer.Instance))
            {
                object target = null, solid = null;
                try
                {
                    FillPattern pattern = element.GetFillPattern();
                    target = pattern.Target == FillPatternTarget.Drafting ? "drafting" : "model";
                    solid = pattern.IsSolidFill;
                }
                catch
                {
                    // Unreadable pattern.
                }
                yield return Row(element, element.Name, element.Id.Value, element.Name, target, solid);
            }
        }

        private static IEnumerable<Item> ScopeBoxes(ListRun run)
        {
            foreach (Element box in new FilteredElementCollector(run.Doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                         .OrderBy(b => b.Name, NaturalComparer.Instance))
            {
                yield return Row(box, box.Name, box.Id.Value, box.Name, run.Full ? OutputJson.Box(Geometry.BoxOf(box)) : null);
            }
        }

        private static IEnumerable<Item> Groups(ListRun run)
        {
            foreach (GroupType type in All<GroupType>(run.Doc).OrderBy(t => t.Name, NaturalComparer.Instance))
            {
                BuiltInCategory category = Resolve.SafeBuiltIn(SafeCategory(type));
                string kind = category == BuiltInCategory.OST_IOSDetailGroups ? "detail" : category == BuiltInCategory.OST_IOSAttachedDetailGroups ? "attached_detail" : "model";
                int instances = 0;
                try { instances = type.Groups.Size; } catch { }
                yield return Row(type, type.Name, type.Id.Value, type.Name, kind, instances);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // MEP systems
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<Item> Systems(ListRun run)
        {
            Document doc = run.Doc;
            foreach (MEPSystem system in All<MEPSystem>(doc).OrderBy(s => s.GetType().Name, StringComparer.Ordinal).ThenBy(s => s.Name, NaturalComparer.Instance))
            {
                string kind = system is PipingSystem ? "piping" : system is MechanicalSystem ? "duct" : system is Autodesk.Revit.DB.Electrical.ElectricalSystem ? "electrical" : system.GetType().Name;
                object elements = null;
                if (run.Full)
                {
                    try
                    {
                        if (system is PipingSystem piping) elements = piping.PipingNetwork.Size;
                        else if (system is MechanicalSystem mechanical) elements = mechanical.DuctNetwork.Size;
                        else elements = system.Elements.Size;
                    }
                    catch
                    {
                        elements = null;
                    }
                }
                yield return Row(system, system.Name, system.Id.Value, system.Name, kind, Rows.TypeName(system), elements);
            }
        }

        private static IEnumerable<Item> SystemTypes(ListRun run)
        {
            foreach (MEPSystemType type in All<MEPSystemType>(run.Doc).OrderBy(t => t.GetType().Name, StringComparer.Ordinal).ThenBy(t => t.Name, NaturalComparer.Instance))
            {
                string kind = type is PipingSystemType ? "piping" : type is MechanicalSystemType ? "duct" : type.GetType().Name;
                object classification = null;
                try { classification = type.SystemClassification.ToString(); } catch { }
                yield return Row(type, type.Name, type.Id.Value, type.Name, kind, classification, type.Abbreviation);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Project information and units
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<Item> ProjectInfoRows(ListRun run)
        {
            Document doc = run.Doc;
            ProjectInfo info = doc.ProjectInformation;
            if (info == null) yield break;
            var values = new List<KeyValuePair<string, object>>
            {
                Pair("Project Name", Safe(() => info.Name)),
                Pair("Project Number", Safe(() => info.Number)),
                Pair("Client Name", Safe(() => info.ClientName)),
                Pair("Project Address", Safe(() => info.Address)),
                Pair("Project Status", Safe(() => info.Status)),
                Pair("Project Issue Date", Safe(() => info.IssueDate)),
                Pair("Author", Safe(() => info.Author)),
                Pair("Building Name", Safe(() => info.BuildingName)),
                Pair("Organization Name", Safe(() => info.OrganizationName)),
                Pair("Organization Description", Safe(() => info.OrganizationDescription)),
            };
            try
            {
                SiteLocation site = doc.SiteLocation;
                if (site != null)
                {
                    values.Add(Pair("Site Name", site.PlaceName));
                    values.Add(Pair("Latitude", Units.Number(Units.RadToDeg(site.Latitude), 6)));
                    values.Add(Pair("Longitude", Units.Number(Units.RadToDeg(site.Longitude), 6)));
                    values.Add(Pair("Time Zone", Units.Number(site.TimeZone, 2)));
                }
            }
            catch
            {
                // No site location.
            }
            var known = new HashSet<BuiltInParameter>
            {
                BuiltInParameter.PROJECT_NAME, BuiltInParameter.PROJECT_NUMBER, BuiltInParameter.CLIENT_NAME, BuiltInParameter.PROJECT_ADDRESS,
                BuiltInParameter.PROJECT_STATUS, BuiltInParameter.PROJECT_ISSUE_DATE, BuiltInParameter.PROJECT_AUTHOR, BuiltInParameter.PROJECT_BUILDING_NAME,
                BuiltInParameter.PROJECT_ORGANIZATION_NAME, BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION
            };
            // Other (shared/project) parameters of Project Information.
            var others = new List<KeyValuePair<string, object>>();
            foreach (Parameter parameter in info.Parameters)
            {
                if (parameter?.Definition == null) continue;
                BuiltInParameter? bip = ParamValues.BuiltInOf(parameter);
                if (bip.HasValue) continue;
                others.Add(Pair(ParamValues.KeyOf(parameter), ParamValues.Get(parameter, doc)));
            }
            values.AddRange(others.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase));
            foreach (KeyValuePair<string, object> pair in values)
            {
                yield return new Item { Element = info, Id = info.Id.Value, Name = pair.Key, Values = new List<object> { pair.Key, pair.Value } };
            }
        }

        private static KeyValuePair<string, object> Pair(string key, object value)
        {
            return new KeyValuePair<string, object>(key, value is string text && text.Length == 0 ? null : value);
        }

        private static object Safe(Func<object> get)
        {
            try { return get(); } catch { return null; }
        }

        private static IEnumerable<Item> UnitRows(ListRun run)
        {
            Autodesk.Revit.DB.Units units = run.Doc.GetUnits();
            var specs = new List<KeyValuePair<string, ForgeTypeId>>
            {
                new KeyValuePair<string, ForgeTypeId>("length", SpecTypeId.Length),
                new KeyValuePair<string, ForgeTypeId>("area", SpecTypeId.Area),
                new KeyValuePair<string, ForgeTypeId>("volume", SpecTypeId.Volume),
                new KeyValuePair<string, ForgeTypeId>("angle", SpecTypeId.Angle),
                new KeyValuePair<string, ForgeTypeId>("slope", SpecTypeId.Slope),
                new KeyValuePair<string, ForgeTypeId>("currency", SpecTypeId.Currency),
                new KeyValuePair<string, ForgeTypeId>("mass", SpecTypeId.Mass),
                new KeyValuePair<string, ForgeTypeId>("pipe_size", SpecTypeId.PipeSize),
                new KeyValuePair<string, ForgeTypeId>("duct_size", SpecTypeId.DuctSize),
                new KeyValuePair<string, ForgeTypeId>("flow", SpecTypeId.Flow),
                new KeyValuePair<string, ForgeTypeId>("air_flow", SpecTypeId.AirFlow),
                new KeyValuePair<string, ForgeTypeId>("hvac_temperature", SpecTypeId.HvacTemperature),
                new KeyValuePair<string, ForgeTypeId>("electrical_power", SpecTypeId.ElectricalPower),
                new KeyValuePair<string, ForgeTypeId>("force", SpecTypeId.Force),
            };
            foreach (KeyValuePair<string, ForgeTypeId> spec in specs)
            {
                object unit = null, accuracy = null, symbol = null;
                try
                {
                    FormatOptions options = units.GetFormatOptions(spec.Value);
                    unit = ReadKit.ShortTypeId(options.GetUnitTypeId());
                    accuracy = options.Accuracy;
                    ForgeTypeId symbolId = options.GetSymbolTypeId();
                    symbol = symbolId == null || symbolId.Empty() ? null : ReadKit.ShortTypeId(symbolId);
                }
                catch
                {
                    continue;
                }
                yield return new Item { Name = spec.Key, Values = new List<object> { spec.Key, unit, accuracy, symbol } };
            }
            string decimalSymbol = null;
            try { decimalSymbol = units.DecimalSymbol.ToString(); } catch { }
            if (decimalSymbol != null) yield return new Item { Name = "decimal_symbol", Values = new List<object> { "decimal_symbol", decimalSymbol, null, null } };
        }
    }

    /// <summary>Natural string order ("Level 2" before "Level 10"), case-insensitive.</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string x, string y)
        {
            x = x ?? string.Empty;
            y = y ?? string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string a = x.Substring(si, i - si).TrimStart('0'), b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    int digits = string.CompareOrdinal(a, b);
                    if (digits != 0) return digits;
                    continue;
                }
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
            int rest = (x.Length - i).CompareTo(y.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(x, y);
        }
    }
}
