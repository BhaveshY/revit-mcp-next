using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private static string NormalizeOptionalText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static List<Dictionary<string, object>> GetOperations(Dictionary<string, object> payload)
        {
            var operations = new List<Dictionary<string, object>>();
            if (payload == null || !payload.TryGetValue("operations", out object value) || value == null) return operations;

            if (value is object[] array)
            {
                foreach (object item in array)
                {
                    if (item is Dictionary<string, object> operation) operations.Add(operation);
                }
            }
            else if (value is ArrayList list)
            {
                foreach (object item in list)
                {
                    if (item is Dictionary<string, object> operation) operations.Add(operation);
                }
            }

            return operations;
        }

        private static List<Dictionary<string, object>> GetPointList(Dictionary<string, object> payload, string key)
        {
            var points = new List<Dictionary<string, object>>();
            if (payload == null || !payload.TryGetValue(key, out object value) || value == null) return points;

            if (value is object[] array)
            {
                foreach (object item in array)
                {
                    if (item is Dictionary<string, object> point) points.Add(point);
                }
            }
            else if (value is ArrayList list)
            {
                foreach (object item in list)
                {
                    if (item is Dictionary<string, object> point) points.Add(point);
                }
            }

            return points;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? value as Dictionary<string, object> : null;
        }

        private static List<Dictionary<string, object>> GetDictionaryList(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return new List<Dictionary<string, object>>();
            if (value is object[] array) return array.OfType<Dictionary<string, object>>().ToList();
            if (value is ArrayList list) return list.Cast<object>().OfType<Dictionary<string, object>>().ToList();
            return new List<Dictionary<string, object>>();
        }

        private static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            return source == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(source, StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> GetStringList(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return Array.Empty<string>();
            if (value is string single) return new[] { single };
            if (value is object[] array) return array.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            if (value is ArrayList list) return list.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return Array.Empty<string>();
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            return root != null && root.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static int? GetInt(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static long? GetLong(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static double? GetDouble(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(Dictionary<string, object> root, string key, bool defaultValue)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return defaultValue;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static bool? GetNullableBool(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value) || value == null) return null;
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
    }
}
