using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Shared plumbing of the read tools (P-READ): paging args, element selectors (ids / from), name matching,
    /// PARAM_IGNORED reporting and table results. Built on the wave-1 helpers (PayloadReader, Resolve, ElementQuery,
    /// Rows, ParamValues); nothing here forks them.
    /// </summary>
    internal static class ReadKit
    {
        public const int DefaultLimit = 50;
        public const int MaxLimit = 500;

        /// <summary>Args every read accepts without a PARAM_IGNORED warning (broker bookkeeping included).</summary>
        private static readonly HashSet<string> Always = new HashSet<string>(StringComparer.Ordinal)
        {
            "doc", "detail", "limit", "page", "offset", "page_offset", "page_total", "resume", "units", "want_ids", "kind", "check", "op"
        };

        // ---------------------------------------------------------------------------------------------------------
        // Args
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Rows per page (limit, clamped with VALUE_CLAMPED).</summary>
        public static int Limit(RequestContext ctx, int defaultValue = DefaultLimit, int max = MaxLimit)
        {
            return ctx.Args.IntClamped("limit", defaultValue, 1, max, (code, text) => ctx.Warn(code, text));
        }

        /// <summary>The page offset the broker sends for "more:" pages (0 on the first call).</summary>
        public static int Offset(RequestContext ctx)
        {
            int? offset = ctx.Args.IntOpt("offset");
            return Math.Max(0, offset ?? 0);
        }

        /// <summary>True for a page call over a stored r# handle (the broker sends the id slice as ids).</summary>
        public static bool IsHandlePage(RequestContext ctx)
        {
            return ctx.Args.Has("page_offset");
        }

        public static bool Full(RequestContext ctx)
        {
            return string.Equals(ctx.Args.Enum("detail", "compact", "compact", "full"), "full", StringComparison.Ordinal);
        }

        /// <summary>Warns PARAM_IGNORED for every given arg not in <paramref name="accepted"/> (per-kind applicability).</summary>
        public static void IgnoreOthers(RequestContext ctx, string what, params string[] accepted)
        {
            var allowed = new HashSet<string>(accepted ?? new string[0], StringComparer.Ordinal);
            var ignored = new List<string>();
            foreach (string key in ctx.Args.Keys)
            {
                if (Always.Contains(key) || allowed.Contains(key)) continue;
                object value = ctx.Args.Get(key);
                if (value == null) continue;
                ignored.Add(key);
            }
            if (ignored.Count > 0)
            {
                ctx.Warn(WarningCodes.ParamIgnored, string.Join(", ", ignored) + (ignored.Count == 1 ? " does" : " do") + " not apply to " + what);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Element selectors
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Element ids named by <paramref name="key"/> (ids or from): numbers, UniqueIds, "selection" (the registry's last
        /// UI selection of the doc, notice SELECTION_STALE when the doc is not active). The broker has already expanded
        /// r# handles and 'last' into ids. Returns null when the key is absent. Unknown ids are kept (callers skip them
        /// with IDS_GONE). Order is preserved, duplicates dropped.
        /// </summary>
        public static List<long> SelectorIds(RequestContext ctx, Document doc, string key)
        {
            if (!ctx.Args.Has(key)) return null;
            var tokens = new List<string>();
            List<long> numeric = ctx.Args.IdValues(key, tokens);
            var result = new List<long>();
            var seen = new HashSet<long>();
            foreach (long id in numeric)
            {
                if (seen.Add(id)) result.Add(id);
            }
            foreach (string raw in tokens)
            {
                string token = raw.Trim();
                if (token.Equals("selection", StringComparison.OrdinalIgnoreCase))
                {
                    SelectionState selection = ctx.Registry?.GetSelection(doc) ?? new SelectionState();
                    if (!selection.IsActiveDoc && selection.AtUtc.HasValue)
                    {
                        ctx.Notice(NoticeCodes.SelectionStale, "selection of '" + doc.Title + "' as of " +
                            selection.AtUtc.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC (not the active document)");
                    }
                    if (selection.Ids.Count == 0) ctx.Notice(NoticeCodes.SelectionStale, "the selection of '" + doc.Title + "' is empty");
                    foreach (long id in selection.Ids)
                    {
                        if (seen.Add(id)) result.Add(id);
                    }
                    continue;
                }
                if (token.Equals("last", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(token, @"^r\d+$", RegexOptions.IgnoreCase))
                {
                    // The broker expands these; reaching the add-in means an in-process caller or a raw pipe test.
                    throw OpException.InvalidArgs(ctx.Args.Field(key), "'" + token + "' is a broker selector; pass element ids here");
                }
                Element byUnique = null;
                try { byUnique = doc.GetElement(token); } catch { byUnique = null; }
                if (byUnique == null)
                {
                    throw OpException.NotFound(ctx.Args.Field(key), "element", token, new string[0]);
                }
                if (seen.Add(byUnique.Id.Value)) result.Add(byUnique.Id.Value);
            }
            return result;
        }

        /// <summary>
        /// The ids of ids/from combined (intersection when both are given), or null when neither is given.
        /// </summary>
        public static List<long> CombinedSelector(RequestContext ctx, Document doc)
        {
            List<long> ids = SelectorIds(ctx, doc, "ids");
            List<long> from = SelectorIds(ctx, doc, "from");
            if (ids == null) return from;
            if (from == null) return ids;
            var keep = new HashSet<long>(from);
            return ids.Where(keep.Contains).ToList();
        }

        /// <summary>Existing elements for the ids (missing ones skipped with warn IDS_GONE).</summary>
        public static List<Element> Existing(RequestContext ctx, Document doc, IEnumerable<long> ids)
        {
            var result = new List<Element>();
            var gone = new List<long>();
            foreach (long id in ids ?? Enumerable.Empty<long>())
            {
                Element element = Resolve.SafeGet(doc, new ElementId(id));
                if (element == null) gone.Add(id);
                else result.Add(element);
            }
            if (gone.Count > 0)
            {
                ctx.Warn(WarningCodes.IdsGone, gone.Count.ToString(CultureInfo.InvariantCulture) + " id(s) do not exist in '" + doc.Title + "' and were skipped", gone.Take(20));
            }
            return result;
        }

        /// <summary>
        /// A reader over the request args with ids replaced by <paramref name="ids"/> and from removed, for
        /// ElementQuery.Compile (which only understands ids).
        /// </summary>
        public static PayloadReader WithIds(RequestContext ctx, IEnumerable<long> ids)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in ctx.Args.Raw)
            {
                if (pair.Key == "ids" || pair.Key == "from") continue;
                map[pair.Key] = pair.Value;
            }
            if (ids != null) map["ids"] = ids.Select(id => (object)id).ToList();
            return new PayloadReader(map, ctx.Args.Path, 1.0);
        }

        /// <summary>A reader over a subset of keys of the request args.</summary>
        public static PayloadReader Subset(RequestContext ctx, params string[] keys)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                if (ctx.Args.TryGet(key, out object value) && value != null) map[key] = value;
            }
            return new PayloadReader(map, ctx.Args.Path, 1.0);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Names
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A name test: '*' and '?' wildcards match the whole name; plain text matches when the name contains it.
        /// Case-insensitive. Null or empty pattern matches everything.
        /// </summary>
        public static Func<string, bool> NameMatcher(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return _ => true;
            string text = pattern.Trim();
            if (text.IndexOf('*') >= 0 || text.IndexOf('?') >= 0)
            {
                var regex = new Regex("^" + Regex.Escape(text).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return name => regex.IsMatch(name ?? string.Empty);
            }
            return name => (name ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string SafeName(Element element)
        {
            try { return element?.Name; } catch { return null; }
        }

        public static string CategoryOf(Element element)
        {
            return Rows.CategoryLabel(element);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Results
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// One page of a table: data {total, cols, rows, ...extra}, page {total, offset, count}. Rows are produced lazily
        /// for the page only.
        /// </summary>
        public static OpResult Page<T>(RequestContext ctx, IList<T> items, IReadOnlyList<string> cols, Func<T, List<object>> row,
            string summary, IDictionary<string, object> extra = null, int? limitOverride = null)
        {
            int limit = limitOverride ?? Limit(ctx);
            int offset = Offset(ctx);
            int total = items.Count;
            var rows = new List<object>();
            for (int index = offset; index < total && rows.Count < limit; index++)
            {
                rows.Add(row(items[index]));
            }
            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["total"] = total,
                ["cols"] = cols.Cast<object>().ToList(),
                ["rows"] = rows
            };
            if (extra != null)
            {
                foreach (KeyValuePair<string, object> pair in extra) data[pair.Key] = pair.Value;
            }
            return OpResult.Success(data, summary).WithPage(total, offset, rows.Count);
        }

        /// <summary>"3 doors" / "1 door".</summary>
        public static string Count(int count, string singular, string plural = null)
        {
            return count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural ?? singular + "s");
        }

        /// <summary>A number rounded for output (null for non-finite).</summary>
        public static object Round(double value, int decimals)
        {
            return Units.Number(value, decimals);
        }

        /// <summary>The element's parameter as a wire value (instance first, then type), or null.</summary>
        public static object ParamOrType(Element element, string key)
        {
            Parameter parameter = ParamValues.Resolve(element, key) ?? ParamValues.Resolve(Rows.TypeOf(element), key);
            return ParamValues.Get(parameter, element?.Document);
        }

        public static object BipValue(Element element, BuiltInParameter bip)
        {
            Parameter parameter;
            try { parameter = element?.get_Parameter(bip); } catch { parameter = null; }
            return ParamValues.Get(parameter, element?.Document);
        }

        public static string BipString(Element element, BuiltInParameter bip)
        {
            try
            {
                Parameter parameter = element?.get_Parameter(bip);
                if (parameter == null || !parameter.HasValue) return null;
                return parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
            }
            catch
            {
                return null;
            }
        }

        public static ElementId BipId(Element element, BuiltInParameter bip)
        {
            try
            {
                Parameter parameter = element?.get_Parameter(bip);
                return parameter != null && parameter.StorageType == StorageType.ElementId ? parameter.AsElementId() : null;
            }
            catch
            {
                return null;
            }
        }

        public static string NameOf(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            return SafeName(Resolve.SafeGet(doc, id));
        }

        /// <summary>The short name of a unit/symbol/spec ForgeTypeId ("autodesk.unit.unit:millimeters-1.0.1" → "millimeters").</summary>
        public static string ShortTypeId(ForgeTypeId id)
        {
            try
            {
                string text = id?.TypeId;
                if (string.IsNullOrEmpty(text)) return null;
                int colon = text.LastIndexOf(':');
                string tail = colon >= 0 ? text.Substring(colon + 1) : text;
                int dash = tail.IndexOf('-');
                return dash > 0 ? tail.Substring(0, dash) : tail;
            }
            catch
            {
                return null;
            }
        }
    }
}
