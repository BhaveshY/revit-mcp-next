using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpNext.Addin
{
    /// <summary>Options for row values.</summary>
    internal sealed class RowOptions
    {
        /// <summary>detail:"full": longer string cut (2,000).</summary>
        public bool Full { get; set; }
        /// <summary>For link rows: transform from link to host coordinates (location, bbox).</summary>
        public Transform LinkTransform { get; set; }
    }

    /// <summary>A list in table form {total, cols, rows} (D1 §7.3 rule 5).</summary>
    internal sealed class TableBuilder
    {
        public TableBuilder(IEnumerable<string> columns)
        {
            Cols = (columns ?? Enumerable.Empty<string>()).ToList();
        }

        public List<string> Cols { get; }
        public List<List<object>> Rows { get; } = new List<List<object>>();
        /// <summary>The exact total (may exceed Rows.Count for paged lists).</summary>
        public int Total { get; set; }

        public void Add(params object[] values)
        {
            Rows.Add(values.ToList());
        }

        public void AddRow(List<object> values)
        {
            Rows.Add(values);
        }

        public Dictionary<string, object> ToWire()
        {
            return new Dictionary<string, object>
            {
                ["total"] = Math.Max(Total, Rows.Count),
                ["cols"] = Cols.Cast<object>().ToList(),
                ["rows"] = Rows.Cast<object>().ToList()
            };
        }
    }

    /// <summary>
    /// Element rows for lists (SPEC §9.5): default columns per category (D1 §4B find_elements), special fields
    /// (location, bbox, host, room, workset, phase, pinned, uid, owner_view, ...), any parameter name as a column,
    /// D1 §6.9 rounding and English category labels on every UI language.
    /// </summary>
    internal static class Rows
    {
        private static readonly string[] Base = { "id", "category", "family", "type", "level" };

        /// <summary>The stable English label of a category (localized name when the alias table has none).</summary>
        public static string CategoryLabel(Category category)
        {
            if (category == null) return null;
            BuiltInCategory builtIn = Resolve.SafeBuiltIn(category);
            if (builtIn != BuiltInCategory.INVALID)
            {
                string english = EnglishAliases.LabelOf(builtIn);
                if (!string.IsNullOrEmpty(english)) return english;
            }
            try
            {
                return category.Name;
            }
            catch
            {
                return null;
            }
        }

        public static string CategoryLabel(Element element)
        {
            try { return CategoryLabel(element?.Category); } catch { return null; }
        }

        /// <summary>Default columns for elements of a category (null category: generic).</summary>
        public static List<string> DefaultColumns(BuiltInCategory? category, bool named = false)
        {
            var columns = new List<string>(Base);
            switch (category)
            {
                case BuiltInCategory.OST_Walls:
                    columns.AddRange(new[] { "length", "height" });
                    break;
                case BuiltInCategory.OST_Doors:
                case BuiltInCategory.OST_Windows:
                    columns.AddRange(new[] { "mark", "host", "width", "height" });
                    break;
                case BuiltInCategory.OST_Rooms:
                case BuiltInCategory.OST_Areas:
                case BuiltInCategory.OST_MEPSpaces:
                    columns.AddRange(new[] { "number", "name", "area" });
                    break;
                case BuiltInCategory.OST_Floors:
                case BuiltInCategory.OST_Ceilings:
                case BuiltInCategory.OST_Roofs:
                    columns.AddRange(new[] { "area", "offset" });
                    break;
                case BuiltInCategory.OST_PipeCurves:
                case BuiltInCategory.OST_DuctCurves:
                case BuiltInCategory.OST_Conduit:
                case BuiltInCategory.OST_CableTray:
                    columns.AddRange(new[] { "size", "length", "system" });
                    break;
                case BuiltInCategory.OST_StructuralColumns:
                    columns.Add("top_level");
                    break;
                default:
                    columns.Add("mark");
                    break;
            }
            if (named && !columns.Contains("name")) columns.Add("name");
            return columns;
        }

        /// <summary>True for elements whose name is meaningful (views, levels, grids, sheets, rooms, groups, ...).</summary>
        public static bool IsNamed(Element element)
        {
            return element is View || element is Level || element is Grid || element is SpatialElement || element is Group ||
                   element is ElementType || element is Material || element is Family || element is ReferencePlane;
        }

        /// <summary>Builds a table of the given elements and columns.</summary>
        public static TableBuilder Table(IEnumerable<Element> elements, IReadOnlyList<string> columns, RowOptions options = null)
        {
            var table = new TableBuilder(columns);
            foreach (Element element in elements ?? Enumerable.Empty<Element>())
            {
                table.AddRow(columns.Select(column => Field(element, column, options)).ToList());
            }
            table.Total = table.Rows.Count;
            return table;
        }

        /// <summary>One value of a row: a special field, or any parameter (instance first, then type).</summary>
        public static object Field(Element element, string field, RowOptions options = null)
        {
            if (element == null || string.IsNullOrWhiteSpace(field)) return null;
            options = options ?? new RowOptions();
            Document doc = element.Document;
            try
            {
                switch (field.Trim().ToLowerInvariant())
                {
                    case "id": return element.Id.Value;
                    case "category": return CategoryLabel(element);
                    case "family": return FamilyName(element);
                    case "type": return TypeName(element);
                    case "type_id": return OutputJson.Id(element.GetTypeId());
                    case "level": return LevelName(element);
                    case "name": return Cut(SafeName(element), options);
                    case "mark": return Param(element, BuiltInParameter.ALL_MODEL_MARK, options);
                    case "comments": return Param(element, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, options);
                    case "length": return Length(element);
                    case "height": return Dimension(element, "Height", BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    case "width": return Dimension(element, "Width", null);
                    case "area": return Measure(element, BuiltInParameter.HOST_AREA_COMPUTED, BuiltInParameter.ROOM_AREA);
                    case "volume": return Measure(element, BuiltInParameter.HOST_VOLUME_COMPUTED, BuiltInParameter.ROOM_VOLUME);
                    case "offset": return Offset(element);
                    case "size": return Param(element, BuiltInParameter.RBS_CALCULATED_SIZE, options);
                    case "system": return Param(element, BuiltInParameter.RBS_SYSTEM_NAME_PARAM, options);
                    case "number": return Number(element, options);
                    case "host": return (element as FamilyInstance)?.Host?.Id.Value;
                    case "room": return RoomNumber(element);
                    case "location": return Location(element, options);
                    case "bbox": return Box(element, options);
                    case "workset": return WorksetName(element);
                    case "phase": return ElementName(doc, ParamId(element, BuiltInParameter.PHASE_CREATED));
                    case "pinned": return element.Pinned;
                    case "uid": return element.UniqueId;
                    case "owner_view": return ElementName(doc, element.OwnerViewId);
                    case "top_level": return TopLevel(element);
                    default:
                        Parameter parameter = ParamValues.Resolve(element, field) ?? ParamValues.Resolve(TypeOf(element), field);
                        object value = ParamValues.Get(parameter, doc);
                        return value is string text ? Cut(text, options) : value;
                }
            }
            catch
            {
                return null;
            }
        }

        public static ElementType TypeOf(Element element)
        {
            try
            {
                ElementId typeId = element?.GetTypeId();
                return typeId == null || typeId == ElementId.InvalidElementId ? null : element.Document.GetElement(typeId) as ElementType;
            }
            catch
            {
                return null;
            }
        }

        public static string FamilyName(Element element)
        {
            if (element is FamilyInstance instance) return instance.Symbol?.FamilyName;
            if (element is ElementType type) return type.FamilyName;
            return TypeOf(element)?.FamilyName;
        }

        /// <summary>"Family: Type" (or the type name for system families without a family name).</summary>
        public static string TypeName(Element element)
        {
            if (element is ElementType type) return Resolve.FullTypeName(type);
            ElementType elementType = TypeOf(element);
            return elementType == null ? null : Resolve.FullTypeName(elementType);
        }

        public static string LevelName(Element element)
        {
            Document doc = element.Document;
            ElementId levelId = null;
            try { levelId = element.LevelId; } catch { }
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                foreach (BuiltInParameter bip in new[]
                {
                    BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT,
                    BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.RBS_START_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM
                })
                {
                    levelId = ParamId(element, bip);
                    if (levelId != null && levelId != ElementId.InvalidElementId) break;
                }
            }
            if ((levelId == null || levelId == ElementId.InvalidElementId) && element is FamilyInstance instance && instance.Host is Element host)
            {
                try { levelId = host.LevelId; } catch { }
            }
            return ElementName(doc, levelId);
        }

        private static object Length(Element element)
        {
            Parameter parameter = SafeParam(element, BuiltInParameter.CURVE_ELEM_LENGTH);
            if (parameter != null && parameter.HasValue) return Units.Mm(parameter.AsDouble());
            if (element.Location is LocationCurve curve) return Units.Mm(curve.Curve.Length);
            return null;
        }

        private static object Dimension(Element element, string englishLabel, BuiltInParameter? preferred)
        {
            if (preferred.HasValue)
            {
                Parameter direct = SafeParam(element, preferred.Value);
                if (direct != null && direct.HasValue) return ParamValues.Get(direct);
            }
            Parameter parameter = ParamValues.Resolve(element, englishLabel) ?? ParamValues.Resolve(TypeOf(element), englishLabel);
            return ParamValues.Get(parameter);
        }

        private static object Measure(Element element, params BuiltInParameter[] candidates)
        {
            foreach (BuiltInParameter bip in candidates)
            {
                Parameter parameter = SafeParam(element, bip);
                if (parameter != null && parameter.HasValue) return ParamValues.Get(parameter);
            }
            return null;
        }

        private static object Offset(Element element)
        {
            return Measure(element,
                BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM,
                BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.WALL_BASE_OFFSET,
                BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
        }

        private static object Number(Element element, RowOptions options)
        {
            if (element is ViewSheet sheet) return sheet.SheetNumber;
            object number = Param(element, BuiltInParameter.ROOM_NUMBER, options);
            return number ?? Param(element, BuiltInParameter.ALL_MODEL_MARK, options);
        }

        private static object RoomNumber(Element element)
        {
            try
            {
                if (element is FamilyInstance instance)
                {
                    Room room = instance.Room ?? instance.ToRoom ?? instance.FromRoom;
                    return room?.Number;
                }
                if (element is Room own) return own.Number;
            }
            catch
            {
                // No phase/room information.
            }
            return null;
        }

        private static object Location(Element element, RowOptions options)
        {
            Transform transform = options.LinkTransform;
            switch (element.Location)
            {
                case LocationPoint point:
                    return OutputJson.Point(transform == null ? point.Point : transform.OfPoint(point.Point));
                case LocationCurve curve:
                    XYZ start = curve.Curve.GetEndPoint(0), end = curve.Curve.GetEndPoint(1);
                    if (transform != null) { start = transform.OfPoint(start); end = transform.OfPoint(end); }
                    return new List<object> { OutputJson.Point(start), OutputJson.Point(end) };
                default:
                    return null;
            }
        }

        private static object Box(Element element, RowOptions options)
        {
            BoundingBoxXYZ box = Geometry.BoxOf(element);
            if (box == null) return null;
            if (options.LinkTransform != null)
            {
                XYZ a = options.LinkTransform.OfPoint(box.Transform == null ? box.Min : box.Transform.OfPoint(box.Min));
                XYZ b = options.LinkTransform.OfPoint(box.Transform == null ? box.Max : box.Transform.OfPoint(box.Max));
                return OutputJson.Box(new BoundingBoxXYZ { Min = Geometry.Min(a, b), Max = Geometry.Max(a, b) });
            }
            return OutputJson.Box(box);
        }

        private static object WorksetName(Element element)
        {
            Document doc = element.Document;
            if (!doc.IsWorkshared) return null;
            try
            {
                return doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static object TopLevel(Element element)
        {
            ElementId id = ParamId(element, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
            if (id == null || id == ElementId.InvalidElementId) id = ParamId(element, BuiltInParameter.WALL_HEIGHT_TYPE);
            return ElementName(element.Document, id);
        }

        private static object Param(Element element, BuiltInParameter bip, RowOptions options)
        {
            object value = ParamValues.Get(SafeParam(element, bip), element.Document);
            return value is string text ? Cut(text, options) : value;
        }

        private static ElementId ParamId(Element element, BuiltInParameter bip)
        {
            Parameter parameter = SafeParam(element, bip);
            if (parameter == null || parameter.StorageType != StorageType.ElementId) return null;
            try { return parameter.AsElementId(); } catch { return null; }
        }

        private static Parameter SafeParam(Element element, BuiltInParameter bip)
        {
            try { return element?.get_Parameter(bip); } catch { return null; }
        }

        private static string ElementName(Document doc, ElementId id)
        {
            Element element = Resolve.SafeGet(doc, id);
            return element == null ? null : SafeName(element);
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; } catch { return null; }
        }

        private static string Cut(string text, RowOptions options)
        {
            return OutputJson.Cut(text, options?.Full ?? false);
        }
    }
}
