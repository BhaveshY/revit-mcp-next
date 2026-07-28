using System;
using System.Collections.Generic;
using System.IO;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace RevitMcpNext.Addin.Ipc
{
    internal static class JsonWireCodec
    {
        private const int MaxJsonLength = 4 * 1024 * 1024;
        private const int MaxDepth = 64;

#if !NETFRAMEWORK
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            MaxDepth = MaxDepth
        };
#endif

        public static object DeserializeObject(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            if (json.Length > MaxJsonLength)
            {
                throw new InvalidDataException("JSON payload exceeds the 4 MiB bridge limit.");
            }

#if NETFRAMEWORK
            return CreateLegacySerializer().DeserializeObject(json);
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

        public static string Serialize(object value)
        {
#if NETFRAMEWORK
            return CreateLegacySerializer().Serialize(value);
#else
            string json = JsonSerializer.Serialize(value, SerializerOptions);
            if (json.Length > MaxJsonLength)
            {
                throw new InvalidDataException("JSON payload exceeds the 4 MiB bridge limit.");
            }

            return json;
#endif
        }

#if NETFRAMEWORK
        private static JavaScriptSerializer CreateLegacySerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = MaxJsonLength,
                RecursionLimit = MaxDepth
            };
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
                    if (element.TryGetInt32(out int intValue))
                    {
                        return intValue;
                    }

                    if (element.TryGetInt64(out long longValue))
                    {
                        return longValue;
                    }

                    if (element.TryGetDecimal(out decimal decimalValue))
                    {
                        return decimalValue;
                    }

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
