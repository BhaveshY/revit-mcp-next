using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitMcpNext.Addin.Revit
{
    internal static class CanonicalJson
    {
        public static string Serialize(object value)
        {
            var builder = new StringBuilder();
            Append(builder, value);
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                builder.Append('{');
                bool first = true;
                foreach (string key in dictionary.Keys.OrderBy(key => key, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    AppendString(builder, key);
                    builder.Append(':');
                    Append(builder, dictionary[key]);
                    first = false;
                }
                builder.Append('}');
                return;
            }

            if (value is string text)
            {
                AppendString(builder, text);
                return;
            }

            if (value is char character)
            {
                AppendString(builder, character.ToString());
                return;
            }

            if (value is bool boolean)
            {
                builder.Append(boolean ? "true" : "false");
                return;
            }

            if (IsNumeric(value))
            {
                if (value is double doubleValue && (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)))
                {
                    throw new InvalidOperationException("Canonical JSON does not support non-finite numbers.");
                }
                if (value is float floatValue && (float.IsNaN(floatValue) || float.IsInfinity(floatValue)))
                {
                    throw new InvalidOperationException("Canonical JSON does not support non-finite numbers.");
                }

                // "R" round-trips identically on net48 and net10 (D3 B20); other numeric types format invariantly.
                if (value is double roundTrip) builder.Append(roundTrip.ToString("R", CultureInfo.InvariantCulture));
                else if (value is float single) builder.Append(single.ToString("R", CultureInfo.InvariantCulture));
                else builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is IEnumerable enumerable)
            {
                builder.Append('[');
                bool first = true;
                foreach (object item in enumerable)
                {
                    if (!first) builder.Append(',');
                    Append(builder, item);
                    first = false;
                }
                builder.Append(']');
                return;
            }

            AppendString(builder, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        private static void AppendString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            builder.Append('"');
        }

        private static bool IsNumeric(object value)
        {
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Single:
                    return true;
                default:
                    return false;
            }
        }
    }
}
