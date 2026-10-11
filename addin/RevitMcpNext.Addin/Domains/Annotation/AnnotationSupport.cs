using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Shared helpers of the annotate tool (P-SHEETS-ANNO): view checks, the view plane that annotation points are
    /// projected onto, element selection (ids or filter), tag categories and types, existing tags, and output rows.
    /// All coordinates are internal feet; the ChangeContext readers convert mm.
    /// </summary>
    internal static class AnnotationSupport
    {
        /// <summary>Explicit element category → tag category pairs where the enum names do not follow "X" → "XTags".</summary>
        private static readonly Dictionary<BuiltInCategory, BuiltInCategory> TagCategories = new Dictionary<BuiltInCategory, BuiltInCategory>
        {
            [BuiltInCategory.OST_Doors] = BuiltInCategory.OST_DoorTags,
            [BuiltInCategory.OST_Windows] = BuiltInCategory.OST_WindowTags,
            [BuiltInCategory.OST_Walls] = BuiltInCategory.OST_WallTags,
            [BuiltInCategory.OST_Rooms] = BuiltInCategory.OST_RoomTags,
            [BuiltInCategory.OST_Areas] = BuiltInCategory.OST_AreaTags,
            [BuiltInCategory.OST_MEPSpaces] = BuiltInCategory.OST_MEPSpaceTags,
            [BuiltInCategory.OST_Floors] = BuiltInCategory.OST_FloorTags,
            [BuiltInCategory.OST_Ceilings] = BuiltInCategory.OST_CeilingTags,
            [BuiltInCategory.OST_Roofs] = BuiltInCategory.OST_RoofTags,
            [BuiltInCategory.OST_Furniture] = BuiltInCategory.OST_FurnitureTags,
            [BuiltInCategory.OST_Casework] = BuiltInCategory.OST_CaseworkTags,
            [BuiltInCategory.OST_GenericModel] = BuiltInCategory.OST_GenericModelTags,
            [BuiltInCategory.OST_PlumbingFixtures] = BuiltInCategory.OST_PlumbingFixtureTags,
            [BuiltInCategory.OST_LightingFixtures] = BuiltInCategory.OST_LightingFixtureTags,
            [BuiltInCategory.OST_MechanicalEquipment] = BuiltInCategory.OST_MechanicalEquipmentTags,
            [BuiltInCategory.OST_ElectricalEquipment] = BuiltInCategory.OST_ElectricalEquipmentTags,
            [BuiltInCategory.OST_ElectricalFixtures] = BuiltInCategory.OST_ElectricalFixtureTags,
            [BuiltInCategory.OST_SpecialityEquipment] = BuiltInCategory.OST_SpecialityEquipmentTags,
            [BuiltInCategory.OST_StructuralColumns] = BuiltInCategory.OST_StructuralColumnTags,
            [BuiltInCategory.OST_StructuralFraming] = BuiltInCategory.OST_StructuralFramingTags,
            [BuiltInCategory.OST_StructuralFoundation] = BuiltInCategory.OST_StructuralFoundationTags,
            [BuiltInCategory.OST_DuctCurves] = BuiltInCategory.OST_DuctTags,
            [BuiltInCategory.OST_PipeCurves] = BuiltInCategory.OST_PipeTags,
            [BuiltInCategory.OST_FlexDuctCurves] = BuiltInCategory.OST_FlexDuctTags,
            [BuiltInCategory.OST_FlexPipeCurves] = BuiltInCategory.OST_FlexPipeTags,
            [BuiltInCategory.OST_DuctTerminal] = BuiltInCategory.OST_DuctTerminalTags,
            [BuiltInCategory.OST_CableTray] = BuiltInCategory.OST_CableTrayTags,
            [BuiltInCategory.OST_Conduit] = BuiltInCategory.OST_ConduitTags,
            [BuiltInCategory.OST_Stairs] = BuiltInCategory.OST_StairsTags,
            [BuiltInCategory.OST_StairsRailing] = BuiltInCategory.OST_StairsRailingTags,
            [BuiltInCategory.OST_CurtainWallPanels] = BuiltInCategory.OST_CurtainWallPanelTags,
            [BuiltInCategory.OST_Parking] = BuiltInCategory.OST_ParkingTags,
            [BuiltInCategory.OST_Planting] = BuiltInCategory.OST_PlantingTags,
            [BuiltInCategory.OST_Site] = BuiltInCategory.OST_SiteTags
        };

        // ---------------------------------------------------------------------------------------------------------
        // Views
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The view of an annotation op, checked: not a template, not a schedule or browser view; sheets and 3D views only
        /// when allowed (3D only when locked, as Revit requires for annotation there).
        /// </summary>
        public static View AnnotationView(ChangeContext c, string what, bool allowSheet, bool allow3D, string key = "view")
        {
            View view = c.View(key);
            string reason = null;
            if (view.IsTemplate) reason = "is a view template";
            else if (view is ViewSchedule || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.ColumnSchedule || view.ViewType == ViewType.PanelSchedule) reason = "is a schedule";
            else if (view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser || view.ViewType == ViewType.Internal || view.ViewType == ViewType.Undefined) reason = "is not a graphical view";
            else if (view is ViewSheet && !allowSheet) reason = "is a sheet";
            else if (view is View3D view3D && (!allow3D || !view3D.IsLocked)) reason = allow3D ? "is an unlocked 3D view (lock it first)" : "is a 3D view";
            if (reason != null)
            {
                throw OpException.InvalidArgs(c.Args.Field(key), "'" + view.Name + "' " + reason + "; " + what + " needs a plan, section, elevation, drafting or detail view" + (allowSheet ? " or a sheet" : string.Empty));
            }
            return view;
        }

        /// <summary>The plane annotation points of a view lie on (plans: the level elevation; others: the view's own plane).</summary>
        public static Plane ViewPlane(View view)
        {
            if (view is ViewPlan plan)
            {
                double z = 0;
                Level level = plan.GenLevel;
                if (level != null) z = level.ProjectElevation;
                else
                {
                    try
                    {
                        SketchPlane sketch = plan.SketchPlane;
                        if (sketch != null) z = sketch.GetPlane().Origin.Z;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // No sketch plane; keep 0.
                    }
                }
                return Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z));
            }
            XYZ normal = view.ViewDirection;
            if (normal == null || normal.IsZeroLength()) normal = XYZ.BasisZ;
            return Plane.CreateByNormalAndOrigin(normal.Normalize(), view.Origin ?? XYZ.Zero);
        }

        /// <summary>Projects a point onto the view plane.</summary>
        public static XYZ OnPlane(Plane plane, XYZ point)
        {
            double distance = (point - plane.Origin).DotProduct(plane.Normal);
            return point - distance * plane.Normal;
        }

        public static List<XYZ> OnPlane(Plane plane, IEnumerable<XYZ> points)
        {
            return points.Select(p => OnPlane(plane, p)).ToList();
        }

        /// <summary>Elements visible in the view (ids), for tagging checks.</summary>
        public static HashSet<long> VisibleIn(Document doc, View view)
        {
            var ids = new HashSet<long>();
            foreach (ElementId id in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds()) ids.Add(id.Value);
            return ids;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Selection (sel = ids | from | filter; the broker expands r#/last/from to ids)
        // ---------------------------------------------------------------------------------------------------------

        public static List<Element> Selected(ChangeContext c)
        {
            if (c.Has("ids")) return c.Elements("ids").ToList();
            if (c.Has("filter"))
            {
                PayloadReader filter = c.Args.Sub("filter");
                ElementQuery query = ElementQuery.Compile(c.Doc, filter, c.Ctx, c.Ctx.UiDoc);
                if (query.IsLink) throw new OpException(ErrorCodes.LinkReadOnly, "Elements of a linked model cannot be annotated through filter.link; tag them in the host view instead.");
                List<Element> elements = query.Elements(c.Ctx).Where(e => !(e is ElementType)).ToList();
                if (elements.Count == 0)
                {
                    throw new OpException(ErrorCodes.NotFound, "The filter matched no elements (" + string.Join(", ", query.Description) + ").",
                        new Dictionary<string, object> { ["param"] = "filter", ["kind"] = "element", ["value"] = string.Join(", ", query.Description), ["candidates"] = new List<object>() });
                }
                return elements;
            }
            throw OpException.InvalidArgs("ids", "give ids, from or filter (the elements to annotate)");
        }

        // ---------------------------------------------------------------------------------------------------------
        // Tags
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The tag category for elements of <paramref name="category"/> (null when none is known).</summary>
        public static BuiltInCategory? TagCategoryOf(Document doc, BuiltInCategory category)
        {
            if (TagCategories.TryGetValue(category, out BuiltInCategory known)) return known;
            string name = category.ToString();
            var candidates = new List<string> { name + "Tags" };
            if (name.EndsWith("s", StringComparison.Ordinal)) candidates.Add(name.Substring(0, name.Length - 1) + "Tags");
            if (name.EndsWith("Curves", StringComparison.Ordinal)) candidates.Add(name.Substring(0, name.Length - 6) + "Tags");
            foreach (string candidate in candidates)
            {
                if (!Enum.TryParse(candidate, false, out BuiltInCategory tagCategory) || !Enum.IsDefined(typeof(BuiltInCategory), tagCategory)) continue;
                Category resolved = null;
                try { resolved = Category.GetCategory(doc, tagCategory); } catch { }
                if (resolved != null && resolved.IsTagCategory) return tagCategory;
            }
            return null;
        }

        /// <summary>Tag types of a tag category (FamilySymbols, incl. room/area/space tag types).</summary>
        public static List<FamilySymbol> TagTypes(Document doc, BuiltInCategory tagCategory)
        {
            return new FilteredElementCollector(doc).OfCategory(tagCategory).WhereElementIsElementType().OfType<FamilySymbol>().ToList();
        }

        /// <summary>A tag type named by the caller (validated as a tag category type).</summary>
        public static FamilySymbol NamedTagType(Document doc, string value, string param = "type")
        {
            FamilySymbol symbol = Resolve.Type<FamilySymbol>(doc, value, param);
            if (symbol.Category == null || !symbol.Category.IsTagCategory)
            {
                throw OpException.InvalidArgs(param, "'" + Resolve.FullTypeName(symbol) + "' is not a tag type (it is " + (Rows.CategoryLabel(symbol) ?? "another category") + ")");
            }
            return symbol;
        }

        /// <summary>
        /// The tag type to use for an element: <paramref name="named"/> when given, else the document default (or the first
        /// loaded) tag of the element's tag category, else the default multi-category tag; NO_TAG_FAMILY when none exists.
        /// </summary>
        public static FamilySymbol TagTypeFor(Document doc, Element element, FamilySymbol named, Dictionary<BuiltInCategory, FamilySymbol> cache)
        {
            if (named != null) return named;
            BuiltInCategory category = Resolve.SafeBuiltIn(element.Category);
            if (cache.TryGetValue(category, out FamilySymbol cached)) return cached;

            FamilySymbol found = null;
            BuiltInCategory? tagCategory = category == BuiltInCategory.INVALID ? null : TagCategoryOf(doc, category);
            if (tagCategory.HasValue) found = DefaultOf(doc, tagCategory.Value);
            if (found == null && !(element is SpatialElement)) found = DefaultOf(doc, BuiltInCategory.OST_MultiCategoryTags);
            if (found == null)
            {
                string label = Rows.CategoryLabel(element) ?? category.ToString();
                throw new OpException(ErrorCodes.NoTagFamily,
                    "No tag family for " + label + " is loaded in '" + doc.Title + "'.",
                    new Dictionary<string, object> { ["category"] = label, ["tagCategory"] = tagCategory.HasValue ? (EnglishAliases.LabelOf(tagCategory.Value) ?? tagCategory.Value.ToString()) : null });
            }
            cache[category] = found;
            return found;
        }

        private static FamilySymbol DefaultOf(Document doc, BuiltInCategory tagCategory)
        {
            try
            {
                ElementId id = doc.GetDefaultFamilyTypeId(new ElementId(tagCategory));
                if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is FamilySymbol symbol) return symbol;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Not a category with a default family type.
            }
            return TagTypes(doc, tagCategory).OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        /// <summary>Ids of elements that already carry a tag in the view (element, room, area and space tags).</summary>
        public static HashSet<long> TaggedIn(Document doc, View view)
        {
            var tagged = new HashSet<long>();
            foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try
                {
                    foreach (ElementId id in tag.GetTaggedLocalElementIds()) tagged.Add(id.Value);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Orphaned tag.
                }
            }
            foreach (Element element in new FilteredElementCollector(doc, view.Id)
                         .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_RoomTags, BuiltInCategory.OST_AreaTags, BuiltInCategory.OST_MEPSpaceTags }))
                         .WhereElementIsNotElementType())
            {
                try
                {
                    switch (element)
                    {
                        case RoomTag roomTag:
                            if (roomTag.TaggedLocalRoomId != null) tagged.Add(roomTag.TaggedLocalRoomId.Value);
                            break;
                        case AreaTag areaTag:
                            if (areaTag.Area != null) tagged.Add(areaTag.Area.Id.Value);
                            break;
                        case SpaceTag spaceTag:
                            if (spaceTag.Space != null) tagged.Add(spaceTag.Space.Id.Value);
                            break;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Orphaned tag.
                }
            }
            return tagged;
        }

        /// <summary>The point a tag head goes to by default: the element's box centre in the view (else its location).</summary>
        public static XYZ CenterOf(Element element, View view)
        {
            if (element is SpatialElement spatial && spatial.Location is LocationPoint spatialPoint) return spatialPoint.Point;
            BoundingBoxXYZ box = Geometry.BoxOf(element, view) ?? Geometry.BoxOf(element);
            if (box != null) return (box.Min + box.Max) / 2.0;
            return Geometry.AnchorOf(element);
        }

        /// <summary>
        /// Tags one element at <paramref name="head"/>: rooms, areas and spaces get their spatial tags (plan views only),
        /// everything else an IndependentTag of <paramref name="type"/>. Returns the tag.
        /// </summary>
        public static Element CreateTag(Document doc, View view, Element element, FamilySymbol type, XYZ head, bool leader, string orientation)
        {
            if (type != null && !type.IsActive) type.Activate();
            var uv = new UV(head.X, head.Y);
            switch (element)
            {
                case Room room:
                {
                    if (!(view is ViewPlan)) throw OpException.InvalidArgs("view", "room tags need a plan view; '" + view.Name + "' is a " + view.ViewType);
                    if (room.Location == null || room.Area <= 0) throw OpException.InvalidArgs("ids", "room " + room.Id.Value.ToString(CultureInfo.InvariantCulture) + " is not placed or not enclosed");
                    RoomTag tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), uv, view.Id)
                        ?? throw new OpException(ErrorCodes.RevitRefused, "Revit did not tag room " + room.Id.Value.ToString(CultureInfo.InvariantCulture) + " in '" + view.Name + "'.");
                    FinishSpatialTag(tag, type, leader, orientation);
                    return tag;
                }
                case Area area:
                {
                    if (!(view is ViewPlan areaPlan) || view.ViewType != ViewType.AreaPlan) throw OpException.InvalidArgs("view", "area tags need an area plan; '" + view.Name + "' is a " + view.ViewType);
                    AreaTag tag = doc.Create.NewAreaTag(areaPlan, area, uv)
                        ?? throw new OpException(ErrorCodes.RevitRefused, "Revit did not tag area " + area.Id.Value.ToString(CultureInfo.InvariantCulture) + " in '" + view.Name + "'.");
                    FinishSpatialTag(tag, type, leader, orientation);
                    return tag;
                }
                case Space space:
                {
                    if (!(view is ViewPlan)) throw OpException.InvalidArgs("view", "space tags need a plan view; '" + view.Name + "' is a " + view.ViewType);
                    SpaceTag tag = doc.Create.NewSpaceTag(space, uv, view)
                        ?? throw new OpException(ErrorCodes.RevitRefused, "Revit did not tag space " + space.Id.Value.ToString(CultureInfo.InvariantCulture) + " in '" + view.Name + "'.");
                    FinishSpatialTag(tag, type, leader, orientation);
                    return tag;
                }
            }

            TagOrientation tagOrientation = orientation == "vertical" ? TagOrientation.Vertical
                : orientation == "model" ? TagOrientation.AnyModelDirection
                : TagOrientation.Horizontal;
            IndependentTag independent = type != null
                ? IndependentTag.Create(doc, type.Id, view.Id, new Reference(element), leader, tagOrientation, head)
                : IndependentTag.Create(doc, view.Id, new Reference(element), leader, TagMode.TM_ADDBY_CATEGORY, tagOrientation, head);
            return independent ?? throw new OpException(ErrorCodes.RevitRefused, "Revit did not tag element " + element.Id.Value.ToString(CultureInfo.InvariantCulture) + " in '" + view.Name + "'.");
        }

        private static void FinishSpatialTag(SpatialElementTag tag, FamilySymbol type, bool leader, string orientation)
        {
            if (type != null && tag.GetTypeId() != type.Id) tag.ChangeTypeId(type.Id);
            if (leader) tag.HasLeader = true;
            if (orientation != null)
            {
                tag.TagOrientation = orientation == "vertical" ? SpatialElementTagOrientation.Vertical
                    : orientation == "model" ? SpatialElementTagOrientation.Model
                    : SpatialElementTagOrientation.Horizontal;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Types and styles
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>A type of class T named by <paramref name="key"/>, else the document default of <paramref name="group"/>, else the first.</summary>
        public static T TypeOrDefault<T>(ChangeContext c, string key, ElementTypeGroup group) where T : ElementType
        {
            Document doc = c.Doc;
            if (c.Has(key)) return Resolve.Type<T>(doc, c.Str(key), c.Args.Field(key));
            try
            {
                ElementId id = doc.GetDefaultElementTypeId(group);
                if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is T typed) return typed;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Fall back to the first type.
            }
            T first = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (first == null)
            {
                throw new OpException(ErrorCodes.NotFound, "'" + doc.Title + "' has no " + typeof(T).Name + ".",
                    new Dictionary<string, object> { ["param"] = c.Args.Field(key), ["kind"] = "type", ["value"] = null, ["candidates"] = new List<object>() });
            }
            return first;
        }

        /// <summary>The line style named by <paramref name="key"/> (null when absent).</summary>
        public static GraphicsStyle LineStyle(ChangeContext c, string key = "line_style")
        {
            return c.Has(key) ? Resolve.LineStyle(c.Doc, c.Str(key), c.Args.Field(key)) : null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------------------------------------------

        public static string ViewLabel(View view)
        {
            return view is ViewSheet sheet ? "sheet " + sheet.SheetNumber + " - " + sheet.Name : "'" + view.Name + "'";
        }

        public static string Count(int n, string noun, string plural = null)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? noun : plural ?? noun + "s");
        }
    }
}
