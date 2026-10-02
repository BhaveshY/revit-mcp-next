using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Addin.Ipc;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Output conventions (D1 §7.3, §6.9): minified JSON, numbers rounded by kind (lengths 0.1 mm, areas 2 decimals,
    /// volumes 3, angles 0.1°; integers when whole), points as mm arrays, null/empty/false values omitted by
    /// <see cref="Compact"/> (write keys created/modified/deleted are kept by the caller), long strings cut at 200
    /// characters (2,000 with detail:"full"), non-finite numbers → null (the pipe adds NON_FINITE_NUMBER).
    /// </summary>
    internal static class OutputJson
    {
        public const int DefaultStringCut = 200;
        public const int FullStringCut = 2000;

        /// <summary>Internal feet → mm number.</summary>
        public static object Mm(double feet) => Units.Mm(feet);

        /// <summary>A point as [x,y,z] in mm.</summary>
        public static List<object> Point(XYZ point)
        {
            if (point == null) return null;
            return new List<object> { Units.Mm(point.X), Units.Mm(point.Y), Units.Mm(point.Z) };
        }

        /// <summary>A point as [x,y] in mm.</summary>
        public static List<object> Point2(XYZ point)
        {
            if (point == null) return null;
            return new List<object> { Units.Mm(point.X), Units.Mm(point.Y) };
        }

        /// <summary>A curve as [[x,y,z],[x,y,z]] (end points) in mm.</summary>
        public static List<object> Curve(Curve curve)
        {
            if (curve == null) return null;
            try
            {
                return new List<object> { Point(curve.GetEndPoint(0)), Point(curve.GetEndPoint(1)) };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A bounding box as [x0,y0,z0,x1,y1,z1] in mm (model coordinates).</summary>
        public static List<object> Box(BoundingBoxXYZ box)
        {
            if (box == null) return null;
            XYZ min = box.Transform == null ? box.Min : box.Transform.OfPoint(box.Min);
            XYZ max = box.Transform == null ? box.Max : box.Transform.OfPoint(box.Max);
            XYZ low = Geometry.Min(min, max), high = Geometry.Max(min, max);
            return new List<object> { Units.Mm(low.X), Units.Mm(low.Y), Units.Mm(low.Z), Units.Mm(high.X), Units.Mm(high.Y), Units.Mm(high.Z) };
        }

        /// <summary>An element id as a number (null for invalid ids).</summary>
        public static object Id(ElementId id)
        {
            return id == null || id == ElementId.InvalidElementId ? null : (object)id.Value;
        }

        public static List<object> Ids(IEnumerable<ElementId> ids)
        {
            return (ids ?? Enumerable.Empty<ElementId>()).Where(id => id != null && id != ElementId.InvalidElementId).Select(id => (object)id.Value).ToList();
        }

        /// <summary>Cuts long text: "...(+N)".</summary>
        public static string Cut(string text, bool full = false)
        {
            int max = full ? FullStringCut : DefaultStringCut;
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max) + "...(+" + (text.Length - max).ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// Removes null values, empty strings/lists/maps and false booleans from a JSON tree (recursively), except keys in
        /// <paramref name="keep"/> (e.g. created/modified/deleted of write results).
        /// </summary>
        public static object Compact(object value, ISet<string> keep = null)
        {
            switch (value)
            {
                case null:
                    return null;
                case IDictionary<string, object> map:
                {
                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object> pair in map)
                    {
                        object compacted = Compact(pair.Value, keep);
                        if (keep != null && keep.Contains(pair.Key)) { result[pair.Key] = compacted ?? pair.Value; continue; }
                        if (IsEmpty(compacted)) continue;
                        result[pair.Key] = compacted;
                    }
                    return result;
                }
                case string _:
                    return value;
                case IEnumerable enumerable:
                {
                    var list = new List<object>();
                    foreach (object item in enumerable) list.Add(Compact(item, keep));
                    return list;
                }
                default:
                    return value;
            }
        }

        /// <summary>Serializes with the wire codec (normalization included).</summary>
        public static string Serialize(object value, out int nonFinite)
        {
            return JsonWireCodec.Serialize(value, out nonFinite);
        }

        private static bool IsEmpty(object value)
        {
            switch (value)
            {
                case null: return true;
                case bool flag: return !flag;
                case string text: return text.Length == 0;
                case ICollection collection: return collection.Count == 0;
                default: return false;
            }
        }
    }
}
