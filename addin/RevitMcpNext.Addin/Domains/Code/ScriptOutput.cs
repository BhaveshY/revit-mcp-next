using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin.Code
{
    /// <summary>
    /// Converts script values to output JSON (SPEC §11.9): depth 4, 200 items per list/map; Element → {id, category,
    /// name}; ElementId → number; XYZ → [x,y,z] mm; curves and boxes in mm; anything else → its string form.
    /// </summary>
    internal static class ScriptOutput
    {
        public const int MaxDepth = 4;
        public const int MaxItems = 200;
        public const int MaxString = 8000;

        public static object Project(object value, Document doc)
        {
            return Project(value, doc, 0);
        }

        private static object Project(object value, Document doc, int depth)
        {
            switch (value)
            {
                case null:
                    return null;
                case string text:
                    return text.Length > MaxString ? text.Substring(0, MaxString) + "...(+" + (text.Length - MaxString).ToString(CultureInfo.InvariantCulture) + " chars)" : text;
                case bool flag:
                    return flag;
                case char c:
                    return c.ToString();
                case byte _:
                case sbyte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                case ulong big:
                    return big <= long.MaxValue ? (object)(long)big : big.ToString(CultureInfo.InvariantCulture);
                case float single:
                    return Finite(single);
                case double number:
                    return Finite(number);
                case decimal dec:
                    return (double)dec;
                case Enum e:
                    return e.ToString();
                case Guid guid:
                    return guid.ToString();
                case DateTime time:
                    return time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                case TimeSpan span:
                    return span.TotalMilliseconds;
                case ElementId id:
                    return id.Value;
                case XYZ point:
                    return OutputJson.Point(point);
                case UV uv:
                    return new List<object> { Units.Mm(uv.U), Units.Mm(uv.V) };
                case Curve curve:
                    return SafeOrText(() => (object)OutputJson.Curve(curve), curve);
                case BoundingBoxXYZ box:
                    return SafeOrText(() => (object)OutputJson.Box(box), box);
                case Parameter parameter:
                    return ProjectParameter(parameter);
                case Element element:
                    return ProjectElement(element);
                case Reference reference:
                    return new Dictionary<string, object> { ["element"] = reference.ElementId?.Value, ["type"] = reference.ElementReferenceType.ToString() };
            }

            if (depth >= MaxDepth) return Text(value);

            if (value is IDictionary dictionary)
            {
                var map = new Dictionary<string, object>(StringComparer.Ordinal);
                int count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count++ >= MaxItems)
                    {
                        map["..."] = "+" + (dictionary.Count - MaxItems).ToString(CultureInfo.InvariantCulture) + " more";
                        break;
                    }
                    map[Key(entry.Key)] = Project(entry.Value, doc, depth + 1);
                }
                return map;
            }

            if (value is IEnumerable sequence)
            {
                var list = new List<object>();
                int count = 0;
                bool more = false;
                foreach (object item in sequence)
                {
                    if (count++ >= MaxItems)
                    {
                        more = true;
                        break;
                    }
                    list.Add(Project(item, doc, depth + 1));
                }
                if (more) list.Add("... more items (only the first " + MaxItems.ToString(CultureInfo.InvariantCulture) + " are shown)");
                return list;
            }

            Type type = value.GetType();
            if (IsAnonymousOrTuple(type))
            {
                var map = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (System.Reflection.PropertyInfo property in type.GetProperties().Take(MaxItems))
                {
                    object item;
                    try { item = property.GetValue(value); } catch (Exception ex) { item = "<" + ex.GetType().Name + ">"; }
                    map[property.Name] = Project(item, doc, depth + 1);
                }
                foreach (System.Reflection.FieldInfo field in type.GetFields().Take(MaxItems))
                {
                    map[field.Name] = Project(field.GetValue(value), doc, depth + 1);
                }
                return map;
            }
            return Text(value);
        }

        private static object ProjectElement(Element element)
        {
            try
            {
                if (!element.IsValidObject) return "<deleted element>";
                return new Dictionary<string, object>
                {
                    ["id"] = element.Id.Value,
                    ["category"] = Rows.CategoryLabel(element),
                    ["name"] = SafeName(element)
                };
            }
            catch (Exception ex)
            {
                return "<element: " + ex.Message + ">";
            }
        }

        private static object ProjectParameter(Parameter parameter)
        {
            try
            {
                string name = parameter.Definition?.Name ?? "?";
                object value;
                switch (parameter.StorageType)
                {
                    case StorageType.String: value = parameter.AsString(); break;
                    case StorageType.Integer: value = parameter.AsValueString() ?? parameter.AsInteger().ToString(CultureInfo.InvariantCulture); break;
                    case StorageType.ElementId: value = parameter.AsElementId()?.Value; break;
                    default: value = parameter.AsValueString(); break;
                }
                return new Dictionary<string, object> { ["name"] = name, ["value"] = value };
            }
            catch (Exception ex)
            {
                return "<parameter: " + ex.Message + ">";
            }
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; } catch { return null; }
        }

        private static object Finite(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            return value;
        }

        private static object SafeOrText(Func<object> project, object value)
        {
            try { return project(); } catch { return Text(value); }
        }

        private static string Key(object key)
        {
            if (key == null) return "null";
            if (key is ElementId id) return id.Value.ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string Text(object value)
        {
            try
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return text.Length > MaxString ? text.Substring(0, MaxString) + "..." : text;
            }
            catch (Exception ex)
            {
                return "<" + value.GetType().Name + ": " + ex.Message + ">";
            }
        }

        private static bool IsAnonymousOrTuple(Type type)
        {
            if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)) return true;
            string full = type.FullName ?? string.Empty;
            return full.StartsWith("System.ValueTuple`", StringComparison.Ordinal) || full.StartsWith("System.Tuple`", StringComparison.Ordinal);
        }
    }

    /// <summary>The script's log(object) sink: one line per call, capped at codeExecution.maxOutputKB.</summary>
    internal sealed class ScriptLog
    {
        private readonly int _maxBytes;
        private readonly Document _doc;
        private readonly StringBuilder _text = new StringBuilder();
        private int _bytes;

        public ScriptLog(int maxKb, Document doc)
        {
            _maxBytes = Math.Max(1, maxKb) * 1024;
            _doc = doc;
        }

        public bool Truncated { get; private set; }
        public int Lines { get; private set; }

        public void Write(object value)
        {
            if (Truncated) return;
            string line;
            if (value == null) line = "null";
            else if (value is string s) line = s;
            else
            {
                object projected = ScriptOutput.Project(value, _doc);
                line = projected is string text ? text : OutputJson.Serialize(projected, out _);
            }
            int size = Encoding.UTF8.GetByteCount(line) + 1;
            if (_bytes + size > _maxBytes)
            {
                Truncated = true;
                _text.Append("... (log truncated at ").Append((_maxBytes / 1024).ToString(CultureInfo.InvariantCulture)).Append(" KB)\n");
                return;
            }
            _bytes += size;
            Lines++;
            _text.Append(line).Append('\n');
        }

        public string Text => _text.ToString().TrimEnd('\n');
    }
}
