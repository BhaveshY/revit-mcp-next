using System;
using System.Collections.Generic;
using System.Linq;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Resolution of one item of a list param (sheets, views, fields, sort) with errors the broker can turn into a working
    /// fix: NOT_FOUND / AMBIGUOUS_NAME on item i are re-thrown naming the whole list param, and the first candidate is the
    /// list with item i replaced by the closest name (the fix line "same call with {param: candidates[0]}" then works).
    /// </summary>
    internal static class ListArgs
    {
        public static T Item<T>(PayloadReader args, string key, int index, Func<string, string, T> resolve, bool substitute = true, Func<string, string> decorate = null)
        {
            List<string> values = args.Strs(key);
            string field = args.Field(key) + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            try
            {
                return resolve(values[index], field);
            }
            catch (OpException ex) when (ex.Code == ErrorCodes.NotFound || ex.Code == ErrorCodes.AmbiguousName)
            {
                throw Rewrap(ex, args, key, values, index, substitute, decorate);
            }
        }

        public static OpException Rewrap(OpException ex, PayloadReader args, string key, List<string> values, int index, bool substitute = true, Func<string, string> decorate = null)
        {
            var details = new Dictionary<string, object>(StringComparer.Ordinal);
            if (Wire.AsMap(ex.Details) is IDictionary<string, object> original)
            {
                foreach (KeyValuePair<string, object> pair in original) details[pair.Key] = pair.Value;
            }
            details["param"] = string.IsNullOrEmpty(args.Path) ? key : args.Field(key);
            details["index"] = index;
            List<object> candidates = (Wire.AsList(details.TryGetValue("candidates", out object raw) ? raw : null) ?? new List<object>()).ToList();
            details["closest"] = candidates;
            if (substitute && candidates.Count > 0)
            {
                var corrected = values.Cast<object>().ToList();
                string first = Convert.ToString(candidates[0], System.Globalization.CultureInfo.InvariantCulture);
                corrected[index] = decorate == null ? first : decorate(first);
                details["candidates"] = new List<object> { corrected };
            }
            else
            {
                details["candidates"] = new List<object>();
            }
            return new OpException(ex.Code, ex.Message, details);
        }
    }
}
