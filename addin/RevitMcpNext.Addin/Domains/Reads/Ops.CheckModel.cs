using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// check_model.&lt;check&gt; (SPEC Appendix A/B, §5.9): stats (counts by category/level/type/workset), warnings
    /// (grouped by message, severity filter, element ids), readiness (what common tasks need and what is missing),
    /// purgeable (one GetUnusedElements pass filtered by CanDeleteElement) and clashes (exact element pairs between two
    /// sets, optionally against a linked model, with a penetration tolerance in mm).
    /// </summary>
    internal static class CheckModelOps
    {
        // ---------------------------------------------------------------------------------------------------------
        // stats
        // ---------------------------------------------------------------------------------------------------------

        [Op("check_model.stats")]
        public static OpResult Stats(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            ReadKit.IgnoreOthers(ctx, "check stats", "group_by");
            string groupBy = ctx.Args.Enum("group_by", "category", "category", "level", "type", "workset", "family");
            if (groupBy == "workset" && !doc.IsWorkshared) ctx.Warn(WarningCodes.ParamIgnored, "'" + doc.Title + "' is not workshared; grouping by category");
            if (groupBy == "workset" && !doc.IsWorkshared) groupBy = "category";
            Func<Element, string> key = FindOps.GroupKey(groupBy, doc);

            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int model = 0, annotation = 0, scanned = 0;
            bool truncated = false;
            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (ctx.ShouldYield(scanned++))
                {
                    truncated = true;
                    break;
                }
                Category category;
                try { category = element.Category; } catch { category = null; }
                if (category == null) continue;
                CategoryType categoryType;
                try { categoryType = category.CategoryType; } catch { continue; }
                if (categoryType == CategoryType.Model) model++;
                else if (categoryType == CategoryType.Annotation) annotation++;
                else continue;
                if (categoryType != CategoryType.Model && groupBy != "category") continue;
                string group = key(element) ?? "(none)";
                groups.TryGetValue(group, out int n);
                groups[group] = n + 1;
            }
            if (truncated) ctx.Warn(WarningCodes.PartialResult, "stopped at the time budget after " + scanned.ToString("N0", CultureInfo.InvariantCulture) + " elements; counts are partial");

            var totals = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["model_elements"] = model,
                ["annotation_elements"] = annotation,
                ["types"] = new FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount(),
                ["families"] = Count(doc, typeof(Family)),
                ["in_place_families"] = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Count(f => SafeInPlace(f)),
                ["views"] = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate && !(v is ViewSheet) && !ListOps.IsBrowserOrInternal(v)),
                ["view_templates"] = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => v.IsTemplate),
                ["sheets"] = Count(doc, typeof(ViewSheet)),
                ["levels"] = Count(doc, typeof(Level)),
                ["rooms"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().GetElementCount(),
                ["groups"] = Count(doc, typeof(Group)),
                ["links"] = Count(doc, typeof(RevitLinkInstance)),
                ["imports"] = Count(doc, typeof(ImportInstance)),
                ["warnings"] = SafeWarnings(doc).Count,
                ["workshared"] = doc.IsWorkshared
            };
            try
            {
                string path = doc.PathName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) totals["file_mb"] = Units.Number(new FileInfo(path).Length / 1048576.0, 1);
            }
            catch
            {
                // Cloud or unsaved.
            }
            if (doc.IsWorkshared) totals["worksets"] = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Count();

            List<KeyValuePair<string, int>> ordered = groups.OrderByDescending(p => p.Value).ThenBy(p => p.Key, NaturalComparer.Instance).ToList();
            string summary = model.ToString("N0", CultureInfo.InvariantCulture) + " model elements in " + ordered.Count.ToString("N0", CultureInfo.InvariantCulture) +
                             " " + (groupBy == "category" ? "categories" : groupBy + " groups") + ", " + Convert.ToString(totals["warnings"], CultureInfo.InvariantCulture) + " warnings";
            return ReadKit.Page(ctx, ordered, new[] { groupBy, "count" }, p => new List<object> { p.Key, p.Value }, summary,
                new Dictionary<string, object> { ["by"] = groupBy, ["totals"] = totals });
        }

        private static int Count(Document doc, Type type)
        {
            try { return new FilteredElementCollector(doc).OfClass(type).GetElementCount(); } catch { return 0; }
        }

        private static bool SafeInPlace(Family family)
        {
            try { return family.IsInPlace; } catch { return false; }
        }

        private static IList<FailureMessage> SafeWarnings(Document doc)
        {
            try { return doc.GetWarnings() ?? new List<FailureMessage>(); } catch { return new List<FailureMessage>(); }
        }

        // ---------------------------------------------------------------------------------------------------------
        // warnings
        // ---------------------------------------------------------------------------------------------------------

        private sealed class WarningGroup
        {
            public string Text;
            public string Severity;
            public int Count;
            public readonly List<long> Ids = new List<long>();
            public int IdTotal;
        }

        [Op("check_model.warnings")]
        public static OpResult Warnings(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            ReadKit.IgnoreOthers(ctx, "check warnings", "match", "severity", "ids");
            Func<string, bool> matchTest = ReadKit.NameMatcher(ctx.Args.Str("match"));
            string severity = ctx.Args.Has("severity") ? ctx.Args.Enum("severity", null, "warning", "error") : null;
            List<long> involving = ReadKit.SelectorIds(ctx, doc, "ids");
            HashSet<long> involvingSet = involving == null ? null : new HashSet<long>(involving);
            bool full = ReadKit.Full(ctx);

            IList<FailureMessage> all = SafeWarnings(doc);
            var groups = new Dictionary<string, WarningGroup>(StringComparer.Ordinal);
            var order = new List<WarningGroup>();
            int matched = 0;
            foreach (FailureMessage message in all)
            {
                string text = SafeText(message);
                string level = SeverityOf(message);
                if (severity != null && level != severity) continue;
                if (!matchTest(text)) continue;
                List<long> ids = ElementsOf(message);
                if (involvingSet != null && !ids.Any(involvingSet.Contains)) continue;
                matched++;
                string key = full ? matched.ToString(CultureInfo.InvariantCulture) : level + "|" + text;
                if (!groups.TryGetValue(key, out WarningGroup group))
                {
                    group = new WarningGroup { Text = text, Severity = level };
                    groups[key] = group;
                    order.Add(group);
                }
                group.Count++;
                foreach (long id in ids)
                {
                    group.IdTotal++;
                    if (group.Ids.Count < (full ? 200 : 20) && !group.Ids.Contains(id)) group.Ids.Add(id);
                }
            }
            List<WarningGroup> sorted = full ? order : order.OrderByDescending(g => g.Severity == "error").ThenByDescending(g => g.Count).ThenBy(g => g.Text, StringComparer.OrdinalIgnoreCase).ToList();
            int errors = order.Where(g => g.Severity == "error").Sum(g => g.Count);
            string summary = matched.ToString("N0", CultureInfo.InvariantCulture) + " warning" + (matched == 1 ? string.Empty : "s") +
                             (full ? string.Empty : " in " + sorted.Count.ToString("N0", CultureInfo.InvariantCulture) + " group" + (sorted.Count == 1 ? string.Empty : "s")) +
                             (errors > 0 ? ", " + errors.ToString("N0", CultureInfo.InvariantCulture) + " errors" : string.Empty) +
                             (matched != all.Count ? " (of " + all.Count.ToString("N0", CultureInfo.InvariantCulture) + ")" : string.Empty);
            return ReadKit.Page(ctx, sorted, new[] { "text", "severity", "count", "ids" },
                g => new List<object> { OutputJson.Cut(g.Text, full), g.Severity, g.Count, g.Ids.Cast<object>().ToList() }, summary,
                new Dictionary<string, object> { ["warnings"] = matched, ["all"] = all.Count });
        }

        private static string SafeText(FailureMessage message)
        {
            try { return message.GetDescriptionText() ?? string.Empty; } catch { return string.Empty; }
        }

        private static string SeverityOf(FailureMessage message)
        {
            try
            {
                FailureSeverity severity = message.GetSeverity();
                return severity == FailureSeverity.Warning ? "warning" : "error";
            }
            catch
            {
                return "warning";
            }
        }

        private static List<long> ElementsOf(FailureMessage message)
        {
            var ids = new List<long>();
            try
            {
                foreach (ElementId id in message.GetFailingElements()) if (id != null && !ids.Contains(id.Value)) ids.Add(id.Value);
                foreach (ElementId id in message.GetAdditionalElements()) if (id != null && !ids.Contains(id.Value)) ids.Add(id.Value);
            }
            catch
            {
                // No elements.
            }
            return ids;
        }

        // ---------------------------------------------------------------------------------------------------------
        // readiness
        // ---------------------------------------------------------------------------------------------------------

        private static readonly string[] DefaultScenarios = { "walls", "floors", "rooms", "families", "sheets", "tags" };
        private static readonly string[] AllScenarios =
        {
            "levels", "grids", "walls", "floors", "roofs", "ceilings", "rooms", "families", "doors", "windows", "views", "sheets",
            "tags", "dimensions", "text", "schedules", "mep", "structure", "worksharing", "links"
        };

        private sealed class Readiness
        {
            private readonly Document _doc;
            private readonly Dictionary<string, int> _cache = new Dictionary<string, int>(StringComparer.Ordinal);

            public Readiness(Document doc)
            {
                _doc = doc;
            }

            public int Levels => Cached("levels", () => new FilteredElementCollector(_doc).OfClass(typeof(Level)).GetElementCount());
            public int Grids => Cached("grids", () => new FilteredElementCollector(_doc).OfClass(typeof(Grid)).GetElementCount());
            public int Walls => Instances(BuiltInCategory.OST_Walls);
            public int Rooms => Instances(BuiltInCategory.OST_Rooms);
            public int Sheets => Cached("sheets", () => new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).GetElementCount());
            public int ViewportTypes => Cached("viewport_types", () => SheetSupport.AllViewportTypes(_doc).Count);

            public int Instances(BuiltInCategory category)
            {
                return Cached("i" + category, () => new FilteredElementCollector(_doc).OfCategory(category).WhereElementIsNotElementType().GetElementCount());
            }

            public int TypesOf(BuiltInCategory category)
            {
                return Cached("t" + category, () => new FilteredElementCollector(_doc).OfCategory(category).WhereElementIsElementType().GetElementCount());
            }

            public int TypesOf(Type type)
            {
                return Cached("c" + type.Name, () => new FilteredElementCollector(_doc).OfClass(type).GetElementCount());
            }

            public int PlanViews => Cached("plans", () => new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Count(v => !v.IsTemplate));

            public int ViewFamilyTypes(ViewFamily family)
            {
                return Cached("vft" + family, () => new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().Count(t => t.ViewFamily == family));
            }

            private int Cached(string key, Func<int> compute)
            {
                if (_cache.TryGetValue(key, out int value)) return value;
                try { value = compute(); } catch { value = 0; }
                _cache[key] = value;
                return value;
            }
        }

        [Op("check_model.readiness")]
        public static OpResult ReadinessCheck(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            ReadKit.IgnoreOthers(ctx, "check readiness", "scenarios");
            List<string> wanted = ctx.Args.Strs("scenarios").Select(s => s.Trim().ToLowerInvariant()).ToList();
            if (wanted.Count == 1 && wanted[0] == "all") wanted = AllScenarios.ToList();
            if (wanted.Count == 0) wanted = DefaultScenarios.ToList();
            foreach (string scenario in wanted)
            {
                string singular = scenario.TrimEnd('s');
                if (!AllScenarios.Contains(scenario) && !AllScenarios.Any(a => a.TrimEnd('s') == singular))
                {
                    throw OpException.InvalidArgs("scenarios", "'" + scenario + "' is not one of " + string.Join(", ", AllScenarios) + " (or all)");
                }
            }
            wanted = wanted.Select(s => AllScenarios.FirstOrDefault(a => a == s) ?? AllScenarios.First(a => a.TrimEnd('s') == s.TrimEnd('s'))).Distinct().ToList();

            var r = new Readiness(doc);
            var rows = new List<List<object>>();
            foreach (string scenario in wanted)
            {
                rows.Add(Scenario(ctx, doc, r, scenario));
            }
            int ready = rows.Count(row => true.Equals(row[1]));
            string summary = ready.ToString(CultureInfo.InvariantCulture) + " of " + rows.Count.ToString(CultureInfo.InvariantCulture) + " scenarios ready" +
                             (ready < rows.Count ? " (missing: " + string.Join(", ", rows.Where(row => !true.Equals(row[1])).Select(row => row[0])) + ")" : string.Empty);
            var data = new Dictionary<string, object>
            {
                ["total"] = rows.Count,
                ["cols"] = new List<object> { "scenario", "ready", "missing", "have", "next" },
                ["rows"] = rows.Cast<object>().ToList()
            };
            return OpResult.Success(data, summary);
        }

        private static List<object> Scenario(RequestContext ctx, Document doc, Readiness r, string scenario)
        {
            var missing = new List<string>();
            var have = new Dictionary<string, object>(StringComparer.Ordinal);
            string next;
            void Need(bool ok, string what)
            {
                if (!ok) missing.Add(what);
            }

            switch (scenario)
            {
                case "levels":
                    have["levels"] = r.Levels;
                    have["plan_views"] = r.PlanViews;
                    Need(r.Levels > 0, "a level");
                    next = r.Levels > 0 ? "list {\"kind\":\"levels\"}" : "create_elements {\"op\":\"level\",\"name\":\"Level 1\",\"elevation\":0}";
                    break;
                case "grids":
                    have["grids"] = r.Grids;
                    have["grid_types"] = r.TypesOf(BuiltInCategory.OST_Grids);
                    Need(r.TypesOf(BuiltInCategory.OST_Grids) > 0, "a grid type");
                    next = "list {\"kind\":\"grids\"}";
                    break;
                case "walls":
                    have["levels"] = r.Levels;
                    have["wall_types"] = r.TypesOf(typeof(WallType));
                    have["walls"] = r.Walls;
                    Need(r.Levels > 0, "a level");
                    Need(r.TypesOf(typeof(WallType)) > 0, "a wall type");
                    next = "list {\"kind\":\"types\",\"category\":[\"Walls\"]}";
                    break;
                case "floors":
                    have["levels"] = r.Levels;
                    have["floor_types"] = r.TypesOf(typeof(FloorType));
                    have["floors"] = r.Instances(BuiltInCategory.OST_Floors);
                    Need(r.Levels > 0, "a level");
                    Need(r.TypesOf(typeof(FloorType)) > 0, "a floor type");
                    next = "list {\"kind\":\"types\",\"category\":[\"Floors\"]}";
                    break;
                case "roofs":
                    have["levels"] = r.Levels;
                    have["roof_types"] = r.TypesOf(typeof(RoofType));
                    Need(r.Levels > 0, "a level");
                    Need(r.TypesOf(typeof(RoofType)) > 0, "a roof type");
                    next = "list {\"kind\":\"types\",\"category\":[\"Roofs\"]}";
                    break;
                case "ceilings":
                    have["levels"] = r.Levels;
                    have["ceiling_types"] = r.TypesOf(typeof(CeilingType));
                    Need(r.Levels > 0, "a level");
                    Need(r.TypesOf(typeof(CeilingType)) > 0, "a ceiling type");
                    next = "list {\"kind\":\"types\",\"category\":[\"Ceilings\"]}";
                    break;
                case "rooms":
                {
                    int bounding = r.Walls + r.Instances(BuiltInCategory.OST_RoomSeparationLines);
                    have["levels"] = r.Levels;
                    have["rooms"] = r.Rooms;
                    have["room_bounding"] = bounding;
                    have["plan_views"] = r.PlanViews;
                    have["phases"] = doc.Phases.Size;
                    Need(r.Levels > 0, "a level");
                    Need(bounding > 0, "room-bounding walls or room separation lines");
                    Need(doc.Phases.Size > 0, "a phase");
                    next = r.Rooms > 0 ? "list {\"kind\":\"rooms\"}" : "find_elements {\"category\":[\"Walls\"],\"count_only\":true}";
                    break;
                }
                case "families":
                {
                    int doors = r.TypesOf(BuiltInCategory.OST_Doors), windows = r.TypesOf(BuiltInCategory.OST_Windows);
                    int furniture = r.TypesOf(BuiltInCategory.OST_Furniture), generic = r.TypesOf(BuiltInCategory.OST_GenericModel);
                    have["door_types"] = doors;
                    have["window_types"] = windows;
                    have["furniture_types"] = furniture;
                    have["generic_model_types"] = generic;
                    have["walls"] = r.Walls;
                    Need(r.Levels > 0, "a level");
                    Need(doors + windows + furniture + generic > 0, "a loaded family type (door, window, furniture or generic model)");
                    Need(doors + windows == 0 || r.Walls > 0, "a wall to host doors/windows");
                    next = "list {\"kind\":\"types\",\"category\":[\"Doors\",\"Windows\",\"Furniture\"]}";
                    break;
                }
                case "doors":
                case "windows":
                {
                    BuiltInCategory category = scenario == "doors" ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows;
                    have[scenario.TrimEnd('s') + "_types"] = r.TypesOf(category);
                    have["walls"] = r.Walls;
                    Need(r.TypesOf(category) > 0, "a loaded " + scenario.TrimEnd('s') + " family");
                    Need(r.Walls > 0, "a wall to host it");
                    next = "list {\"kind\":\"types\",\"category\":[\"" + (scenario == "doors" ? "Doors" : "Windows") + "\"]}";
                    break;
                }
                case "views":
                    have["floor_plan_types"] = r.ViewFamilyTypes(ViewFamily.FloorPlan);
                    have["section_types"] = r.ViewFamilyTypes(ViewFamily.Section);
                    have["3d_types"] = r.ViewFamilyTypes(ViewFamily.ThreeDimensional);
                    have["levels"] = r.Levels;
                    Need(r.ViewFamilyTypes(ViewFamily.FloorPlan) > 0, "a floor plan view type");
                    Need(r.ViewFamilyTypes(ViewFamily.Section) > 0, "a section view type");
                    Need(r.ViewFamilyTypes(ViewFamily.ThreeDimensional) > 0, "a 3D view type");
                    next = "list {\"kind\":\"view_types\"}";
                    break;
                case "sheets":
                    have["title_block_types"] = r.TypesOf(BuiltInCategory.OST_TitleBlocks);
                    have["sheets"] = r.Sheets;
                    have["viewport_types"] = r.ViewportTypes;
                    Need(r.TypesOf(BuiltInCategory.OST_TitleBlocks) > 0, "a title block family (load one with place_family op=load)");
                    next = "list {\"kind\":\"title_blocks\"}";
                    break;
                case "tags":
                {
                    int doorTags = r.TypesOf(BuiltInCategory.OST_DoorTags), windowTags = r.TypesOf(BuiltInCategory.OST_WindowTags);
                    int roomTags = r.TypesOf(BuiltInCategory.OST_RoomTags), wallTags = r.TypesOf(BuiltInCategory.OST_WallTags);
                    have["door_tags"] = doorTags;
                    have["window_tags"] = windowTags;
                    have["room_tags"] = roomTags;
                    have["wall_tags"] = wallTags;
                    have["multi_category_tags"] = r.TypesOf(BuiltInCategory.OST_MultiCategoryTags);
                    Need(doorTags > 0, "a door tag family");
                    Need(windowTags > 0, "a window tag family");
                    Need(roomTags > 0, "a room tag family");
                    next = "list {\"kind\":\"tag_types\"}";
                    break;
                }
                case "dimensions":
                    have["dimension_types"] = r.TypesOf(typeof(DimensionType));
                    Need(r.TypesOf(typeof(DimensionType)) > 0, "a dimension type");
                    next = "list {\"kind\":\"dimension_types\"}";
                    break;
                case "text":
                    have["text_types"] = r.TypesOf(typeof(TextNoteType));
                    Need(r.TypesOf(typeof(TextNoteType)) > 0, "a text type");
                    next = "list {\"kind\":\"text_types\"}";
                    break;
                case "schedules":
                    have["schedules"] = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Count(s => !s.IsTemplate);
                    next = "list {\"kind\":\"schedules\"}";
                    break;
                case "mep":
                    have["pipe_types"] = r.TypesOf(typeof(PipeType));
                    have["duct_types"] = r.TypesOf(typeof(DuctType));
                    have["piping_system_types"] = r.TypesOf(typeof(PipingSystemType));
                    have["duct_system_types"] = r.TypesOf(typeof(MechanicalSystemType));
                    have["levels"] = r.Levels;
                    Need(r.Levels > 0, "a level");
                    Need(r.TypesOf(typeof(PipeType)) > 0 || r.TypesOf(typeof(DuctType)) > 0, "a pipe or duct type");
                    Need(r.TypesOf(typeof(PipingSystemType)) > 0 || r.TypesOf(typeof(MechanicalSystemType)) > 0, "an MEP system type");
                    next = "list {\"kind\":\"system_types\"}";
                    break;
                case "structure":
                    have["column_types"] = r.TypesOf(BuiltInCategory.OST_StructuralColumns);
                    have["framing_types"] = r.TypesOf(BuiltInCategory.OST_StructuralFraming);
                    have["foundation_types"] = r.TypesOf(BuiltInCategory.OST_StructuralFoundation);
                    have["levels"] = r.Levels;
                    have["grids"] = r.Grids;
                    Need(r.Levels > 0, "a level");
                    Need(r.TypesOf(BuiltInCategory.OST_StructuralColumns) > 0, "a structural column family");
                    Need(r.TypesOf(BuiltInCategory.OST_StructuralFraming) > 0, "a structural framing family");
                    next = "list {\"kind\":\"types\",\"category\":[\"Structural Columns\",\"Structural Framing\"]}";
                    break;
                case "worksharing":
                    have["workshared"] = doc.IsWorkshared;
                    Need(doc.IsWorkshared, "worksharing enabled");
                    if (doc.IsWorkshared)
                    {
                        have["worksets"] = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Count();
                        string central = null;
                        try
                        {
                            ModelPath path = doc.GetWorksharingCentralModelPath();
                            central = path == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
                        }
                        catch
                        {
                            central = null;
                        }
                        have["has_central"] = !string.IsNullOrEmpty(central);
                    }
                    next = "list {\"kind\":\"worksets\"}";
                    break;
                case "links":
                {
                    int links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount();
                    int unloaded = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Count(l => SafeLinkDoc(l) == null);
                    have["links"] = links;
                    have["unloaded"] = unloaded;
                    Need(unloaded == 0, unloaded.ToString(CultureInfo.InvariantCulture) + " unloaded link(s)");
                    next = "list {\"kind\":\"links\"}";
                    break;
                }
                default:
                    next = null;
                    break;
            }
            return new List<object> { scenario, missing.Count == 0, missing.Count == 0 ? null : missing.Cast<object>().ToList(), have, next };
        }

        private static Document SafeLinkDoc(RevitLinkInstance link)
        {
            try { return link.GetLinkDocument(); } catch { return null; }
        }

        // ---------------------------------------------------------------------------------------------------------
        // purgeable
        // ---------------------------------------------------------------------------------------------------------

        [Op("check_model.purgeable")]
        public static OpResult Purgeable(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            ReadKit.IgnoreOthers(ctx, "check purgeable");
            ctx.Progress("listing unused elements");
            List<ElementId> candidates = PurgeUtil.Candidates(doc);
            var items = candidates.Select(id => Resolve.SafeGet(doc, id)).Where(e => e != null)
                .Select(e => new { Element = e, Kind = KindOf(e), Category = Rows.CategoryLabel(e), Name = DisplayName(e) })
                .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, NaturalComparer.Instance)
                .ToList();
            Dictionary<string, int> byKind = PurgeUtil.ByKind(doc, candidates);
            string summary = ReadKit.Count(items.Count, "purgeable item") + (byKind.Count == 0 ? string.Empty :
                " (" + string.Join(", ", byKind.OrderByDescending(p => p.Value).Select(p => p.Value.ToString(CultureInfo.InvariantCulture) + " " + p.Key)) + ")");
            return ReadKit.Page(ctx, items, new[] { "id", "kind", "category", "name" },
                x => new List<object> { x.Element.Id.Value, x.Kind, x.Category, x.Name }, summary,
                new Dictionary<string, object> { ["by_kind"] = byKind, ["note"] = "one purge pass; purging can free further nested items" });
        }

        private static string KindOf(Element element)
        {
            switch (element)
            {
                case Family _: return "family";
                case Material _: return "material";
                case View _: return "view";
                case FillPatternElement _: return "fill_pattern";
                case LinePatternElement _: return "line_pattern";
                case GraphicsStyle _: return "style";
                case ElementType _: return "type";
                default: return "other";
            }
        }

        private static string DisplayName(Element element)
        {
            if (element is ElementType type) return Resolve.FullTypeName(type);
            return ReadKit.SafeName(element);
        }

        // ---------------------------------------------------------------------------------------------------------
        // clashes
        // ---------------------------------------------------------------------------------------------------------

        private sealed class ClashSet
        {
            public Document Doc;
            public Transform ToHost = Transform.Identity;
            public List<BuiltInCategory> Categories = new List<BuiltInCategory>();
            public List<long> Ids;
            public string Label;
        }

        private sealed class Clash
        {
            public long A;
            public long B;
            public string ACategory;
            public string BCategory;
            public double DepthFt;
            public double VolumeFt3;
            public XYZ Point;
        }

        private const double TouchVolumeFt3 = 1e-6;
        private const int MaxClashes = 10000;

        [Op("check_model.clashes")]
        public static OpResult Clashes(RequestContext ctx)
        {
            Document host = ctx.Doc;
            ReadKit.IgnoreOthers(ctx, "check clashes", "a", "b", "link", "tolerance");
            double toleranceFt = Units.MmToFt(Math.Max(0, ctx.Args.MmRawOpt("tolerance") ?? 0));
            ClashSet a = SetOf(ctx, host, "a", host);
            Document bDoc = host;
            Transform bToHost = Transform.Identity;
            string link = ctx.Args.Str("link");
            if (!string.IsNullOrWhiteSpace(link))
            {
                RevitLinkInstance instance = Resolve.Link(host, link, ctx.Args.Field("link"));
                bDoc = instance.GetLinkDocument() ?? throw new OpException(ErrorCodes.NotFound, "The link '" + instance.Name + "' is not loaded.",
                    new Dictionary<string, object> { ["param"] = "link", ["kind"] = "link", ["value"] = link, ["candidates"] = new List<object>() });
                bToHost = instance.GetTotalTransform();
            }
            ClashSet b = ctx.Args.Has("b") ? SetOf(ctx, bDoc, "b", host) : SetOf(ctx, bDoc, "a", host);
            b.ToHost = bToHost;

            List<Element> aElements = Members(a).OrderBy(e => e.Id.Value).ToList();
            int start = 0;
            IDictionary<string, object> resume = ctx.Args.Dict("resume");
            if (resume != null && resume.TryGetValue("a_index", out object startValue) && PayloadReader.TryNumber(startValue, out double startNumber))
            {
                start = Math.Max(0, Math.Min(aElements.Count, (int)startNumber));
            }

            ElementFilter bFilter = Filter(b);
            Transform hostToB = bToHost.Inverse;
            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
            var clashes = new List<Clash>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int noGeometry = 0;
            int index = start;
            bool partial = false;
            for (; index < aElements.Count; index++)
            {
                if (ctx.ShouldYield(index - start) && index > start)
                {
                    partial = true;
                    break;
                }
                if (clashes.Count >= MaxClashes)
                {
                    partial = true;
                    break;
                }
                ctx.Progress("clash check", index, aElements.Count);
                Element element = aElements[index];
                List<Solid> solidsA = Solids(element, options);
                if (solidsA.Count == 0)
                {
                    noGeometry++;
                    continue;
                }
                BoundingBoxXYZ boxA = Geometry.BoxOf(element);
                if (boxA == null) continue;
                Outline outline = OutlineInB(boxA, hostToB);
                var candidates = new FilteredElementCollector(bDoc).WhereElementIsNotElementType().WherePasses(bFilter)
                    .WherePasses(new BoundingBoxIntersectsFilter(outline));
                if (bDoc == host) candidates = candidates.Excluding(new List<ElementId> { element.Id });
                List<Solid> solidsAInB = bToHost.IsIdentity ? solidsA : solidsA.Select(s => SolidUtils.CreateTransformed(s, hostToB)).ToList();
                var solidFilters = solidsAInB.Select(s => (ElementFilter)new ElementIntersectsSolidFilter(s)).ToList();
                candidates = candidates.WherePasses(solidFilters.Count == 1 ? solidFilters[0] : new LogicalOrFilter(solidFilters));

                foreach (Element other in candidates)
                {
                    if (bDoc == host)
                    {
                        // One row per unordered pair when both sets live in the host (a=b or overlapping sets).
                        long lo = Math.Min(element.Id.Value, other.Id.Value), hi = Math.Max(element.Id.Value, other.Id.Value);
                        if (!seen.Add(lo.ToString(CultureInfo.InvariantCulture) + ":" + hi.ToString(CultureInfo.InvariantCulture))) continue;
                    }
                    Clash clash = Measure(element, other, solidsAInB, options, bToHost);
                    if (clash == null) continue;
                    if (clash.VolumeFt3 >= 0 && clash.VolumeFt3 <= TouchVolumeFt3) continue;
                    if (toleranceFt > 0 && clash.DepthFt >= 0 && clash.DepthFt < toleranceFt) continue;
                    clashes.Add(clash);
                }
            }

            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["a"] = a.Label,
                ["b"] = b.Label + (bDoc != host ? " in link " + link : string.Empty),
                ["tolerance_mm"] = Units.Mm(toleranceFt),
                ["checked"] = index - start
            };
            if (noGeometry > 0) data["no_geometry"] = noGeometry;
            if (bDoc != host) data["b_ids_are_in_link"] = true;
            OpResult result = ReadKit.Page(ctx, clashes, new[] { "a", "b", "a_category", "b_category", "depth", "volume", "point" },
                c => new List<object>
                {
                    c.A, c.B, c.ACategory, c.BCategory, c.DepthFt < 0 ? null : Units.Mm(c.DepthFt), c.VolumeFt3 < 0 ? null : Units.Volume(c.VolumeFt3),
                    c.Point == null ? null : OutputJson.Point(c.Point)
                },
                ReadKit.Count(clashes.Count, "clash", "clashes") + " between " + a.Label + " and " + b.Label + (bDoc != host ? " (link " + link + ")" : string.Empty) +
                (toleranceFt > 0 ? ", tolerance " + Convert.ToString(Units.Mm(toleranceFt), CultureInfo.InvariantCulture) + " mm" : string.Empty), data);
            if (partial)
            {
                result.WithPartial("deadline", new Dictionary<string, object> { ["a_index"] = index });
                ctx.Warn(WarningCodes.PartialResult, "checked " + (index - start).ToString("N0", CultureInfo.InvariantCulture) + " of " +
                    (aElements.Count - start).ToString("N0", CultureInfo.InvariantCulture) + " elements of set a; continue with the more: page");
            }
            return result;
        }

        private static ClashSet SetOf(RequestContext ctx, Document doc, string key, Document host)
        {
            var tokens = new List<string>();
            List<long> ids = ctx.Args.IdValues(key, tokens);
            var set = new ClashSet { Doc = doc };
            if (ids.Count > 0)
            {
                if (doc != host && key == "a")
                {
                    throw OpException.InvalidArgs("b", "with link= and ids in a, give b as categories (or an r# handle from find_elements with the same link)");
                }
                set.Ids = ids;
            }
            foreach (string token in tokens)
            {
                set.Categories.Add(Resolve.CategoryId(doc, token, ctx.Args.Field(key)));
            }
            if (set.Ids == null && set.Categories.Count == 0) throw OpException.InvalidArgs(key, "give categories (e.g. [\"Ducts\"]) or an r# handle");
            var parts = new List<string>();
            if (set.Categories.Count > 0) parts.Add(string.Join(", ", set.Categories.Select(c => EnglishAliases.LabelOf(c) ?? c.ToString())));
            if (set.Ids != null) parts.Add(set.Ids.Count.ToString("N0", CultureInfo.InvariantCulture) + " ids");
            set.Label = string.Join(" + ", parts);
            return set;
        }

        private static ElementFilter Filter(ClashSet set)
        {
            var filters = new List<ElementFilter>();
            if (set.Categories.Count == 1) filters.Add(new ElementCategoryFilter(set.Categories[0]));
            else if (set.Categories.Count > 1) filters.Add(new ElementMulticategoryFilter(set.Categories));
            if (set.Ids != null && set.Ids.Count > 0) filters.Add(new ElementIdSetFilter(set.Ids.Select(id => new ElementId(id)).ToList()));
            if (filters.Count == 0) return new ElementIdSetFilter(new List<ElementId> { ElementId.InvalidElementId });
            return filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters);
        }

        private static IEnumerable<Element> Members(ClashSet set)
        {
            return new FilteredElementCollector(set.Doc).WhereElementIsNotElementType().WherePasses(Filter(set)).Where(e => e.Category != null);
        }

        private static Outline OutlineInB(BoundingBoxXYZ box, Transform hostToB)
        {
            XYZ min = box.Transform == null ? box.Min : box.Transform.OfPoint(box.Min);
            XYZ max = box.Transform == null ? box.Max : box.Transform.OfPoint(box.Max);
            var corners = new[]
            {
                new XYZ(min.X, min.Y, min.Z), new XYZ(max.X, min.Y, min.Z), new XYZ(min.X, max.Y, min.Z), new XYZ(max.X, max.Y, min.Z),
                new XYZ(min.X, min.Y, max.Z), new XYZ(max.X, min.Y, max.Z), new XYZ(min.X, max.Y, max.Z), new XYZ(max.X, max.Y, max.Z)
            }.Select(hostToB.OfPoint).ToList();
            return new Outline(corners.Aggregate(Geometry.Min), corners.Aggregate(Geometry.Max));
        }

        /// <summary>Solids with volume of an element (instances resolved to their placed geometry).</summary>
        private static List<Solid> Solids(Element element, Options options)
        {
            var solids = new List<Solid>();
            GeometryElement geometry;
            try { geometry = element.get_Geometry(options); } catch { geometry = null; }
            if (geometry == null) return solids;
            Collect(geometry, solids, 0);
            return solids;
        }

        private static void Collect(GeometryElement geometry, List<Solid> solids, int depth)
        {
            if (depth > 4) return;
            foreach (GeometryObject item in geometry)
            {
                switch (item)
                {
                    case Solid solid:
                        try
                        {
                            if (solid.Volume > 1e-9 && solid.Faces.Size > 0) solids.Add(solid);
                        }
                        catch
                        {
                            // Invalid solid.
                        }
                        break;
                    case GeometryInstance instance:
                        try
                        {
                            GeometryElement placed = instance.GetInstanceGeometry();
                            if (placed != null) Collect(placed, solids, depth + 1);
                        }
                        catch
                        {
                            // No instance geometry.
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Intersection of a (already in b's coordinates) and b: volume, penetration depth (smallest extent of the
        /// intersection box) and centroid in host coordinates. Boolean failures report the pair with unknown depth.
        /// </summary>
        private static Clash Measure(Element a, Element b, List<Solid> solidsAInB, Options options, Transform bToHost)
        {
            var clash = new Clash
            {
                A = a.Id.Value,
                B = b.Id.Value,
                ACategory = Rows.CategoryLabel(a),
                BCategory = Rows.CategoryLabel(b),
                DepthFt = -1,
                VolumeFt3 = -1
            };
            List<Solid> solidsB = Solids(b, options);
            if (solidsB.Count == 0) return null;
            double volume = 0;
            XYZ min = null, max = null;
            XYZ weighted = XYZ.Zero;
            bool failed = false;
            foreach (Solid sa in solidsAInB)
            {
                foreach (Solid sb in solidsB)
                {
                    Solid intersection;
                    try
                    {
                        intersection = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                    }
                    catch
                    {
                        failed = true;
                        continue;
                    }
                    if (intersection == null) continue;
                    double v;
                    try { v = intersection.Volume; } catch { v = 0; }
                    if (v <= 0) continue;
                    volume += v;
                    try
                    {
                        BoundingBoxXYZ box = intersection.GetBoundingBox();
                        XYZ lo = box.Transform.OfPoint(box.Min), hi = box.Transform.OfPoint(box.Max);
                        XYZ bmin = Geometry.Min(lo, hi), bmax = Geometry.Max(lo, hi);
                        min = min == null ? bmin : Geometry.Min(min, bmin);
                        max = max == null ? bmax : Geometry.Max(max, bmax);
                        weighted += intersection.ComputeCentroid() * v;
                    }
                    catch
                    {
                        // No box or centroid.
                    }
                }
            }
            if (volume > 0)
            {
                clash.VolumeFt3 = volume;
                if (min != null)
                {
                    XYZ size = max - min;
                    clash.DepthFt = Math.Min(size.X, Math.Min(size.Y, size.Z));
                }
                clash.Point = bToHost.OfPoint(weighted / volume);
                return clash;
            }
            if (failed)
            {
                // The geometry kernel could not intersect: report the pair (the native filter says they intersect).
                BoundingBoxXYZ box = Geometry.BoxOf(a);
                clash.Point = box == null ? null : (box.Min + box.Max) / 2;
                return clash;
            }
            return null;
        }
    }
}
