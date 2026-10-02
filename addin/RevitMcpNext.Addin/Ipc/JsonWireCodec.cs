using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using RevitMcpNext.Contracts;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Encodings.Web;
using System.Text.Json;
#endif

namespace RevitMcpNext.Addin.Ipc
{
    /// <summary>
    /// JSON for the pipe, the registration file, the ledger and settings. Byte-based 4 MiB limit (SPEC §4.6.1).
    /// Serialize() first normalizes any value into a JSON tree: IWireObject.ToWire(), dictionaries, lists, primitives,
    /// DateTime (ISO 8601 UTC), enums (name), Guid, ElementId (number), XYZ (mm array), and other objects by their public
    /// properties (camelCase). Non-finite numbers become null and are counted (NON_FINITE_NUMBER).
    /// </summary>
    internal static class JsonWireCodec
    {
        public const int MaxBytes = BridgeProtocol.MaxFrameBytes;
        private const int MaxDepth = 64;

#if !NETFRAMEWORK
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            MaxDepth = MaxDepth + 8,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
#endif

        public static object DeserializeObject(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            {
                throw new InvalidDataException("JSON payload exceeds the 4 MiB bridge limit.");
            }

#if NETFRAMEWORK
            return Canonicalize(CreateLegacySerializer().DeserializeObject(json));
#else
            using (JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxDepth
                }))
            {
                return ConvertElement(document.RootElement);
            }
#endif
        }

        /// <summary>Parses a JSON object; throws InvalidDataException when the root is not an object.</summary>
        public static Dictionary<string, object> DeserializeMap(string json)
        {
            object parsed = DeserializeObject(json);
            if (parsed is Dictionary<string, object> map) return map;
            throw new InvalidDataException("Expected a JSON object.");
        }

        public static string Serialize(object value)
        {
            return Serialize(value, out _);
        }

        /// <summary>Serializes after normalization; <paramref name="nonFinite"/> counts NaN/Infinity values replaced by null.</summary>
        public static string Serialize(object value, out int nonFinite)
        {
            nonFinite = 0;
            object tree = Normalize(value, ref nonFinite, 0);
#if NETFRAMEWORK
            string json = CreateLegacySerializer().Serialize(tree);
#else
            string json = JsonSerializer.Serialize(tree, SerializerOptions);
#endif
            return json;
        }

        /// <summary>Converts any supported value into a JSON tree of Dictionary/List/primitives.</summary>
        public static object Normalize(object value, ref int nonFinite, int depth)
        {
            if (value == null) return null;
            if (depth > MaxDepth) return "(depth limit)";

            switch (value)
            {
                case string text: return text;
                case bool flag: return flag;
                case int i: return i;
                case long l: return l;
                case short s: return (int)s;
                case byte b: return (int)b;
                case sbyte sb: return (int)sb;
                case ushort us: return (int)us;
                case uint ui: return (long)ui;
                case ulong ul: return ul <= long.MaxValue ? (object)(long)ul : (double)ul;
                case decimal m: return m;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) { nonFinite++; return null; }
                    return d;
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) { nonFinite++; return null; }
                    return (double)f;
                case char c: return c.ToString();
                case Guid guid: return guid.ToString();
                case DateTime dateTime: return dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                case DateTimeOffset dateTimeOffset: return dateTimeOffset.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                case TimeSpan span: return (long)span.TotalMilliseconds;
                case Enum enumValue: return enumValue.ToString();
                case Autodesk.Revit.DB.ElementId elementId: return elementId.Value;
                case Autodesk.Revit.DB.XYZ point:
                    return new List<object>
                    {
                        RoundMm(point.X, ref nonFinite), RoundMm(point.Y, ref nonFinite), RoundMm(point.Z, ref nonFinite)
                    };
                case IWireObject wire: return Normalize(wire.ToWire(), ref nonFinite, depth + 1);
                case IDictionary dictionary:
                {
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                        map[key] = Normalize(entry.Value, ref nonFinite, depth + 1);
                    }
                    return map;
                }
                case IEnumerable enumerable:
                {
                    var list = new List<object>();
                    foreach (object item in enumerable) list.Add(Normalize(item, ref nonFinite, depth + 1));
                    return list;
                }
            }

            Type type = value.GetType();
            if (type.Namespace != null && type.Namespace.StartsWith("Autodesk.", StringComparison.Ordinal))
            {
                // Revit objects must be converted explicitly by the handler (OutputJson); never reflect over them.
                return value.ToString();
            }

            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                object propertyValue;
                try
                {
                    propertyValue = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }
                properties[CamelCase(property.Name)] = Normalize(propertyValue, ref nonFinite, depth + 1);
            }
            return properties;
        }

        private static object RoundMm(double feet, ref int nonFinite)
        {
            double mm = feet * 304.8;
            if (double.IsNaN(mm) || double.IsInfinity(mm)) { nonFinite++; return null; }
            double rounded = Math.Round(mm, 1);
            return rounded == Math.Floor(rounded) && Math.Abs(rounded) < 9e15 ? (object)(long)rounded : rounded;
        }

        private static string CamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0])) return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

#if NETFRAMEWORK
        private static JavaScriptSerializer CreateLegacySerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = MaxDepth + 8
            };
        }

        /// <summary>JavaScriptSerializer gives ArrayList/object[]; make arrays object[] and maps Dictionary everywhere.</summary>
        private static object Canonicalize(object value)
        {
            switch (value)
            {
                case Dictionary<string, object> map:
                {
                    var copy = new Dictionary<string, object>(map.Count, StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object> pair in map) copy[pair.Key] = Canonicalize(pair.Value);
                    return copy;
                }
                case object[] array:
                {
                    var copy = new object[array.Length];
                    for (int i = 0; i < array.Length; i++) copy[i] = Canonicalize(array[i]);
                    return copy;
                }
                case ArrayList list:
                {
                    var copy = new object[list.Count];
                    for (int i = 0; i < list.Count; i++) copy[i] = Canonicalize(list[i]);
                    return copy;
                }
                default:
                    return value;
            }
        }
#else
        private static object ConvertElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        dictionary[property.Name] = ConvertElement(property.Value);
                    }
                    return dictionary;

                case JsonValueKind.Array:
                    var items = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        items.Add(ConvertElement(item));
                    }
                    return items.ToArray();

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                    if (element.TryGetInt32(out int intValue)) return intValue;
                    if (element.TryGetInt64(out long longValue)) return longValue;
                    if (element.TryGetDecimal(out decimal decimalValue)) return decimalValue;
                    return element.GetDouble();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Null:
                    return null;

                default:
                    throw new InvalidDataException("Unsupported JSON token kind: " + element.ValueKind + ".");
            }
        }
#endif
    }
}
