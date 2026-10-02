using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Lenient reader over normalized request args (SPEC §5.3, defense in depth for in-process callers): numeric strings,
    /// unit strings ("3.5 m", "12'6\"", "45°", "2%"), yes/no booleans, scalars where arrays are expected, comma-separated
    /// id strings, camelCase keys for snake_case params. Every problem throws INVALID_ARGS naming the field.
    /// Lengths: Mm() returns internal feet, MmRaw() millimetres. Angles: Deg() returns radians, DegRaw() degrees.
    /// A per-call "units" arg (mm|cm|m|in|ft) scales bare length numbers only when the reader is created without an
    /// explicit factor (in-process callers); broker requests arrive normalized to mm, so RequestContext passes 1.
    /// </summary>
    internal sealed class PayloadReader
    {
        private readonly IDictionary<string, object> _map;

        public PayloadReader(IDictionary<string, object> args, string path = null, double? bareLengthToMm = null)
        {
            _map = args ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Path = path ?? string.Empty;
            if (bareLengthToMm.HasValue)
            {
                BareLengthToMm = bareLengthToMm.Value;
            }
            else
            {
                string units = RawString("units");
                double? factor = string.IsNullOrWhiteSpace(units) ? 1.0 : Units.LengthUnitToMm(units);
                if (!factor.HasValue) throw OpException.InvalidArgs(Field("units"), "must be one of mm, cm, m, in, ft");
                BareLengthToMm = factor.Value;
            }
        }

        /// <summary>The underlying map (as received; $refs already resolved inside change_set).</summary>
        public IDictionary<string, object> Raw => _map;

        /// <summary>Field prefix for error messages, e.g. "ops[2]".</summary>
        public string Path { get; }

        /// <summary>mm per bare length number (1 unless the call passed units).</summary>
        public double BareLengthToMm { get; }

        public IEnumerable<string> Keys => _map.Keys;

        /// <summary>True when the key is present with a non-null value (exact, case-insensitive or camel/snake variant).</summary>
        public bool Has(string key)
        {
            return TryGet(key, out object value) && value != null && !(value is string text && text.Length == 0);
        }

        /// <summary>The raw value (null when absent).</summary>
        public object Get(string key)
        {
            return TryGet(key, out object value) ? value : null;
        }

        public bool TryGet(string key, out object value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;
            if (_map.TryGetValue(key, out value)) return true;
            foreach (KeyValuePair<string, object> pair in _map)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(SnakeCase(pair.Key), key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pair.Key, SnakeCase(key), StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            return false;
        }

        // ----------------------------------------------------------------------------------------------------------
        // Strings and enums
        // ----------------------------------------------------------------------------------------------------------

        public string Str(string key, string defaultValue = null)
        {
            object value = Get(key);
            if (value == null) return defaultValue;
            if (value is string text) return text.Length == 0 ? defaultValue : text;
            if (value is bool flag) return flag ? "true" : "false";
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            if (Wire.AsList(value) is IList<object> list && list.Count == 1) return Convert.ToString(list[0], CultureInfo.InvariantCulture);
            throw OpException.InvalidArgs(Field(key), "must be a string, got " + Describe(value));
        }

        public string ReqStr(string key)
        {
            string value = Str(key);
            if (string.IsNullOrWhiteSpace(value)) throw OpException.InvalidArgs(Field(key), "is required");
            return value;
        }

        /// <summary>Case-insensitive enum match returning the canonical spelling; INVALID_ARGS lists the values.</summary>
        public string Enum(string key, string defaultValue, params string[] allowed)
        {
            string value = Str(key);
            if (value == null) return defaultValue;
            string match = allowed.FirstOrDefault(option => string.Equals(option, value.Trim(), StringComparison.OrdinalIgnoreCase)) ??
                           allowed.FirstOrDefault(option => string.Equals(option.Replace("_", string.Empty), value.Trim().Replace("_", string.Empty).Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));
            if (match == null) throw OpException.InvalidArgs(Field(key), "must be one of " + string.Join(", ", allowed) + " (got '" + value + "')");
            return match;
        }

        /// <summary>A list of strings (a scalar becomes a one-item list; a JSON-array string is parsed).</summary>
        public List<string> Strs(string key)
        {
            var result = new List<string>();
            foreach (object item in Items(key))
            {
                if (item == null) continue;
                string text = item is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
            }
            return result;
        }

        // ----------------------------------------------------------------------------------------------------------
        // Numbers and booleans
        // ----------------------------------------------------------------------------------------------------------

        public double? NumOpt(string key)
        {
            object value = Get(key);
            if (value == null || (value is string empty && empty.Trim().Length == 0)) return null;
            if (TryNumber(value, out double number)) return number;
            throw OpException.InvalidArgs(Field(key), "must be a number, got " + Describe(value));
        }

        public double Num(string key, double? defaultValue = null)
        {
            double? value = NumOpt(key);
            if (value.HasValue) return value.Value;
            if (defaultValue.HasValue) return defaultValue.Value;
            throw OpException.InvalidArgs(Field(key), "is required (a number)");
        }

        public int? IntOpt(string key)
        {
            double? value = NumOpt(key);
            if (!value.HasValue) return null;
            double rounded = Math.Round(value.Value);
            if (Math.Abs(rounded - value.Value) > 1e-9) throw OpException.InvalidArgs(Field(key), "must be a whole number, got " + value.Value.ToString(CultureInfo.InvariantCulture));
            if (rounded > int.MaxValue || rounded < int.MinValue) throw OpException.InvalidArgs(Field(key), "is out of range");
            return (int)rounded;
        }

        public int Int(string key, int? defaultValue = null)
        {
            int? value = IntOpt(key);
            if (value.HasValue) return value.Value;
            if (defaultValue.HasValue) return defaultValue.Value;
            throw OpException.InvalidArgs(Field(key), "is required (a whole number)");
        }

        /// <summary>Clamped integer; reports VALUE_CLAMPED through <paramref name="onClamp"/> when it had to clamp.</summary>
        public int IntClamped(string key, int defaultValue, int min, int max, Action<string, string> onClamp = null)
        {
            int value = Int(key, defaultValue);
            if (value < min || value > max)
            {
                int clamped = Math.Max(min, Math.Min(max, value));
                onClamp?.Invoke(WarningCodes.ValueClamped, Field(key) + " " + value.ToString(CultureInfo.InvariantCulture) + " clamped to " + clamped.ToString(CultureInfo.InvariantCulture));
                return clamped;
            }
            return value;
        }

        public long? LongOpt(string key)
        {
            object value = Get(key);
            if (value == null) return null;
            long? parsed = Wire.ToLong(value is string text ? text.Trim() : value);
            if (parsed.HasValue) return parsed;
            throw OpException.InvalidArgs(Field(key), "must be a whole number, got " + Describe(value));
        }

        public bool? BoolOpt(string key)
        {
            object value = Get(key);
            if (value == null) return null;
            if (value is bool flag) return flag;
            if (TryNumber(value, out double number) && (number == 0 || number == 1)) return number == 1;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant();
            switch (text)
            {
                case "true": case "yes": case "y": case "on": case "1": case "ja": return true;
                case "false": case "no": case "n": case "off": case "0": case "nein": case "": return false;
            }
            throw OpException.InvalidArgs(Field(key), "must be true or false, got " + Describe(value));
        }

        public bool Bool(string key, bool defaultValue = false)
        {
            return BoolOpt(key) ?? defaultValue;
        }

        // ----------------------------------------------------------------------------------------------------------
        // Units
        // ----------------------------------------------------------------------------------------------------------

        /// <summary>A length in millimetres (bare numbers use the call's units), or null when absent.</summary>
        public double? MmRawOpt(string key)
        {
            object value = Get(key);
            if (value == null || (value is string empty && empty.Trim().Length == 0)) return null;
            return LengthToMm(value, Field(key));
        }

        public double MmRaw(string key, double? defaultMm = null)
        {
            double? value = MmRawOpt(key);
            if (value.HasValue) return value.Value;
            if (defaultMm.HasValue) return defaultMm.Value;
            throw OpException.InvalidArgs(Field(key), "is required (a length in mm, or with a unit such as \"3.5 m\")");
        }

        /// <summary>A length given in mm (or with a unit) converted to Revit internal feet.</summary>
        public double Mm(string key, double? defaultMm = null)
        {
            return Units.MmToFt(MmRaw(key, defaultMm));
        }

        public double? MmOpt(string key)
        {
            double? mm = MmRawOpt(key);
            return mm.HasValue ? Units.MmToFt(mm.Value) : (double?)null;
        }

        /// <summary>An angle in degrees (or "0.5 rad") or null when absent.</summary>
        public double? DegRawOpt(string key)
        {
            object value = Get(key);
            if (value == null || (value is string empty && empty.Trim().Length == 0)) return null;
            if (TryNumber(value, out double number)) return number;
            if (value is string text && Units.TryParseAngle(text, out double degrees)) return degrees;
            throw OpException.InvalidArgs(Field(key), "must be an angle in degrees, got " + Describe(value));
        }

        public double DegRaw(string key, double? defaultDegrees = null)
        {
            double? value = DegRawOpt(key);
            if (value.HasValue) return value.Value;
            if (defaultDegrees.HasValue) return defaultDegrees.Value;
            throw OpException.InvalidArgs(Field(key), "is required (degrees)");
        }

        /// <summary>An angle given in degrees converted to radians.</summary>
        public double Deg(string key, double? defaultDegrees = null)
        {
            return Units.DegToRad(DegRaw(key, defaultDegrees));
        }

        /// <summary>A percentage ("2%" or 2).</summary>
        public double Pct(string key, double? defaultPercent = null)
        {
            object value = Get(key);
            if (value == null) return defaultPercent ?? throw OpException.InvalidArgs(Field(key), "is required (percent)");
            if (TryNumber(value, out double number)) return number;
            if (value is string text && Units.TryParsePercent(text, out double percent)) return percent;
            throw OpException.InvalidArgs(Field(key), "must be a percentage such as 2 or \"2%\", got " + Describe(value));
        }

        /// <summary>An area in m² (or "120000 mm2").</summary>
        public double M2(string key, double? defaultValue = null)
        {
            object value = Get(key);
            if (value == null) return defaultValue ?? throw OpException.InvalidArgs(Field(key), "is required (m²)");
            if (TryNumber(value, out double number)) return number;
            if (value is string text && Units.TryParseArea(text, out double area)) return area;
            throw OpException.InvalidArgs(Field(key), "must be an area in m², got " + Describe(value));
        }

        /// <summary>A volume in m³ (or "1000 l").</summary>
        public double M3(string key, double? defaultValue = null)
        {
            object value = Get(key);
            if (value == null) return defaultValue ?? throw OpException.InvalidArgs(Field(key), "is required (m³)");
            if (TryNumber(value, out double number)) return number;
            if (value is string text && Units.TryParseVolume(text, out double volume)) return volume;
            throw OpException.InvalidArgs(Field(key), "must be a volume in m³, got " + Describe(value));
        }

        // ----------------------------------------------------------------------------------------------------------
        // Geometry
        // ----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A point [x,y] or [x,y,z] (also {x,y,z} and legacy {x:{value,unit}}) in mm, converted to internal feet.
        /// A missing z becomes <paramref name="defaultZft"/> (feet) or 0.
        /// </summary>
        public XYZ Point(string key, double? defaultZft = null)
        {
            XYZ point = PointOpt(key, defaultZft);
            if (point == null) throw OpException.InvalidArgs(Field(key), "is required (a point [x,y] or [x,y,z] in mm)");
            return point;
        }

        public XYZ PointOpt(string key, double? defaultZft = null)
        {
            object value = Get(key);
            if (value == null) return null;
            return ToPoint(value, Field(key), defaultZft, out _);
        }

        /// <summary>Like <see cref="PointOpt"/> but also reports whether z was given explicitly.</summary>
        public XYZ PointOpt(string key, double? defaultZft, out bool hasZ)
        {
            hasZ = false;
            object value = Get(key);
            if (value == null) return null;
            return ToPoint(value, Field(key), defaultZft, out hasZ);
        }

        /// <summary>A list of points; a single point becomes a one-item list.</summary>
        public List<XYZ> Points(string key, double? defaultZft = null, bool required = true)
        {
            object value = Get(key);
            if (value == null)
            {
                if (required) throw OpException.InvalidArgs(Field(key), "is required (a list of points [[x,y],...] in mm)");
                return new List<XYZ>();
            }
            IList<object> list = AsListOrParse(value) ?? throw OpException.InvalidArgs(Field(key), "must be a list of points");
            if (list.Count > 0 && IsScalarNumber(list[0]))
            {
                return new List<XYZ> { ToPoint(list, Field(key), defaultZft, out _) };
            }
            var points = new List<XYZ>(list.Count);
            for (int index = 0; index < list.Count; index++)
            {
                points.Add(ToPoint(list[index], Field(key) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", defaultZft, out _));
            }
            return points;
        }

        /// <summary>A list of loops (each a list of points); a flat list of points becomes one loop.</summary>
        public List<List<XYZ>> Loops(string key, double? defaultZft = null, bool required = true)
        {
            object value = Get(key);
            if (value == null)
            {
                if (required) throw OpException.InvalidArgs(Field(key), "is required (a list of loops [[[x,y],...],...] in mm)");
                return new List<List<XYZ>>();
            }
            IList<object> list = AsListOrParse(value) ?? throw OpException.InvalidArgs(Field(key), "must be a list of loops");
            if (list.Count == 0) return new List<List<XYZ>>();
            IList<object> first = Wire.AsList(list[0]);
            bool flat = first != null && first.Count > 0 && IsScalarNumber(first[0]);
            if (flat) return new List<List<XYZ>> { Points(key, defaultZft) };
            var loops = new List<List<XYZ>>(list.Count);
            for (int index = 0; index < list.Count; index++)
            {
                IList<object> loop = Wire.AsList(list[index]) ?? throw OpException.InvalidArgs(Field(key) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", "must be a list of points");
                var points = new List<XYZ>(loop.Count);
                for (int p = 0; p < loop.Count; p++)
                {
                    points.Add(ToPoint(loop[p], Field(key) + "[" + index.ToString(CultureInfo.InvariantCulture) + "][" + p.ToString(CultureInfo.InvariantCulture) + "]", defaultZft, out _));
                }
                loops.Add(points);
            }
            return loops;
        }

        /// <summary>A vector [dx,dy] or [dx,dy,dz] in mm → feet (dz defaults to 0).</summary>
        public XYZ Vector(string key, bool required = true)
        {
            object value = Get(key);
            if (value == null)
            {
                if (required) throw OpException.InvalidArgs(Field(key), "is required (a vector [dx,dy] or [dx,dy,dz] in mm)");
                return null;
            }
            return ToPoint(value, Field(key), 0, out _);
        }

        /// <summary>Numbers of a box [x0,y0,z0,x1,y1,z1] (or a rectangle [x0,y0,x1,y1]) in mm, converted to feet.</summary>
        public double[] BoxFt(string key, bool required = true)
        {
            object value = Get(key);
            if (value == null)
            {
                if (required) throw OpException.InvalidArgs(Field(key), "is required ([x0,y0,z0,x1,y1,z1] in mm)");
                return null;
            }
            IList<object> list = AsListOrParse(value) ?? throw OpException.InvalidArgs(Field(key), "must be a list of numbers");
            if (list.Count != 4 && list.Count != 6) throw OpException.InvalidArgs(Field(key), "must have 4 (rectangle) or 6 (box) numbers");
            var result = new double[list.Count];
            for (int index = 0; index < list.Count; index++)
            {
                result[index] = Units.MmToFt(LengthToMm(list[index], Field(key) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]"));
            }
            return result;
        }

        // ----------------------------------------------------------------------------------------------------------
        // Ids
        // ----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Numeric element ids: numbers, numeric strings, "1,2,3", nested arrays (flattened). Non-numeric tokens throw
        /// INVALID_ARGS unless <paramref name="others"/> collects them (UniqueIds or names for the caller to resolve).
        /// </summary>
        public List<long> IdValues(string key, List<string> others = null)
        {
            var result = new List<long>();
            foreach (object item in Flatten(Get(key)))
            {
                if (item == null) continue;
                long? id = Wire.ToLong(item is string text ? text.Trim() : item);
                if (id.HasValue)
                {
                    result.Add(id.Value);
                    continue;
                }
                if (item is string token)
                {
                    foreach (string part in token.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        long? partId = Wire.ToLong(part.Trim());
                        if (partId.HasValue) result.Add(partId.Value);
                        else if (others != null) others.Add(part.Trim());
                        else throw OpException.InvalidArgs(Field(key), "must contain element ids, got '" + part.Trim() + "'");
                    }
                    continue;
                }
                throw OpException.InvalidArgs(Field(key), "must contain element ids, got " + Describe(item));
            }
            return result;
        }

        /// <summary>Numeric element ids as ElementId (see <see cref="IdValues"/>).</summary>
        public List<ElementId> Ids(string key, List<string> others = null)
        {
            return IdValues(key, others).Select(id => new ElementId(id)).ToList();
        }

        public ElementId IdOpt(string key)
        {
            if (!Has(key)) return null;
            List<long> ids = IdValues(key);
            if (ids.Count != 1) throw OpException.InvalidArgs(Field(key), "must be one element id");
            return new ElementId(ids[0]);
        }

        // ----------------------------------------------------------------------------------------------------------
        // Structure
        // ----------------------------------------------------------------------------------------------------------

        /// <summary>A nested object as a reader (null when absent).</summary>
        public PayloadReader Sub(string key)
        {
            object value = Get(key);
            if (value == null) return null;
            IDictionary<string, object> map = Wire.AsMap(value) ?? throw OpException.InvalidArgs(Field(key), "must be an object");
            return new PayloadReader(map, Field(key), BareLengthToMm);
        }

        /// <summary>A list of objects as readers (an object becomes a one-item list).</summary>
        public List<PayloadReader> SubList(string key)
        {
            var result = new List<PayloadReader>();
            object value = Get(key);
            if (value == null) return result;
            if (Wire.AsMap(value) is IDictionary<string, object> single) return new List<PayloadReader> { new PayloadReader(single, Field(key), BareLengthToMm) };
            IList<object> list = AsListOrParse(value) ?? throw OpException.InvalidArgs(Field(key), "must be a list of objects");
            for (int index = 0; index < list.Count; index++)
            {
                IDictionary<string, object> map = Wire.AsMap(list[index]) ??
                    throw OpException.InvalidArgs(Field(key) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", "must be an object");
                result.Add(new PayloadReader(map, Field(key) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", BareLengthToMm));
            }
            return result;
        }

        public IDictionary<string, object> Dict(string key)
        {
            object value = Get(key);
            if (value == null) return null;
            return Wire.AsMap(value) ?? throw OpException.InvalidArgs(Field(key), "must be an object");
        }

        /// <summary>A list (a scalar becomes a one-item list; a JSON-array string is parsed).</summary>
        public List<object> List(string key)
        {
            return Items(key).ToList();
        }

        /// <summary>Field name with this reader's path prefix, for error messages.</summary>
        public string Field(string key)
        {
            return string.IsNullOrEmpty(Path) ? key : Path + "." + key;
        }

        public static string Describe(object value)
        {
            if (value == null) return "null";
            if (value is string text) return "'" + (text.Length > 60 ? text.Substring(0, 60) + "..." : text) + "'";
            if (value is IDictionary) return "an object";
            if (value is IEnumerable) return "a list";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // ----------------------------------------------------------------------------------------------------------
        // Internals
        // ----------------------------------------------------------------------------------------------------------

        private string RawString(string key)
        {
            object value = Get(key);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private IEnumerable<object> Items(string key)
        {
            object value = Get(key);
            if (value == null) return Enumerable.Empty<object>();
            IList<object> list = AsListOrParse(value);
            return list ?? (IEnumerable<object>)new[] { value };
        }

        private static IList<object> AsListOrParse(object value)
        {
            IList<object> list = Wire.AsList(value);
            if (list != null) return list;
            if (value is string text)
            {
                string trimmed = text.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    try
                    {
                        return Wire.AsList(Ipc.JsonWireCodec.DeserializeObject(trimmed));
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        private static IEnumerable<object> Flatten(object value)
        {
            if (value == null) yield break;
            IList<object> list = AsListOrParse(value);
            if (list == null)
            {
                yield return value;
                yield break;
            }
            foreach (object item in list)
            {
                foreach (object inner in Flatten(item)) yield return inner;
            }
        }

        private double LengthToMm(object value, string field)
        {
            if (TryNumber(value, out double number)) return number * BareLengthToMm;
            if (value is string text && Units.TryParseLength(text, BareLengthToMm, out double mm)) return mm;
            if (Wire.AsMap(value) is IDictionary<string, object> legacy && legacy.ContainsKey("value"))
            {
                // Legacy {value, unit} form.
                object raw = legacy["value"];
                string unit = legacy.TryGetValue("unit", out object unitValue) ? Convert.ToString(unitValue, CultureInfo.InvariantCulture) : "mm";
                double? factor = Units.LengthUnitToMm(unit);
                if (factor.HasValue && TryNumber(raw, out double legacyNumber)) return legacyNumber * factor.Value;
            }
            throw OpException.InvalidArgs(field, "must be a length in mm (or with a unit such as \"3.5 m\" or \"12'6\\\"\"), got " + Describe(value));
        }

        private XYZ ToPoint(object value, string field, double? defaultZft, out bool hasZ)
        {
            hasZ = false;
            if (Wire.AsMap(value) is IDictionary<string, object> map)
            {
                if (!map.ContainsKey("x") || !map.ContainsKey("y")) throw OpException.InvalidArgs(field, "must be [x,y] or [x,y,z] in mm");
                double x = Units.MmToFt(LengthToMm(map["x"], field + ".x"));
                double y = Units.MmToFt(LengthToMm(map["y"], field + ".y"));
                hasZ = map.TryGetValue("z", out object zValue) && zValue != null;
                double z = hasZ ? Units.MmToFt(LengthToMm(zValue, field + ".z")) : (defaultZft ?? 0);
                return new XYZ(x, y, z);
            }

            IList<object> list = AsListOrParse(value);
            if (list == null || list.Count < 2 || list.Count > 3) throw OpException.InvalidArgs(field, "must be [x,y] or [x,y,z] in mm, got " + Describe(value));
            double px = Units.MmToFt(LengthToMm(list[0], field + "[0]"));
            double py = Units.MmToFt(LengthToMm(list[1], field + "[1]"));
            hasZ = list.Count == 3 && list[2] != null;
            double pz = hasZ ? Units.MmToFt(LengthToMm(list[2], field + "[2]")) : (defaultZft ?? 0);
            return new XYZ(px, py, pz);
        }

        internal static bool TryNumber(object value, out double number)
        {
            number = 0;
            switch (value)
            {
                case null: return false;
                case bool _: return false;
                case double d: number = d; return Units.IsFinite(d);
                case float f: number = f; return Units.IsFinite(f);
                case decimal m: number = (double)m; return true;
                case int i: number = i; return true;
                case long l: number = l; return true;
                case short s: number = s; return true;
                case byte b: number = b; return true;
                case uint ui: number = ui; return true;
                case ulong ul: number = ul; return true;
                case string text:
                    string trimmed = text.Trim();
                    if (trimmed.IndexOf('.') < 0 && trimmed.Count(c => c == ',') == 1) trimmed = trimmed.Replace(',', '.');
                    return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && Units.IsFinite(number);
                default: return false;
            }
        }

        private static bool IsScalarNumber(object value)
        {
            return value != null && !(value is string) && TryNumber(value, out _) ||
                   value is string text && TryNumber(text, out _);
        }

        internal static string SnakeCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var builder = new System.Text.StringBuilder(name.Length + 4);
            for (int index = 0; index < name.Length; index++)
            {
                char character = name[index];
                if (char.IsUpper(character))
                {
                    if (index > 0 && name[index - 1] != '_') builder.Append('_');
                    builder.Append(char.ToLowerInvariant(character));
                }
                else
                {
                    builder.Append(character);
                }
            }
            return builder.ToString();
        }
    }
}
