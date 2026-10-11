using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// find_elements (SPEC Appendix A, §5.9): ElementQuery compiles the filter into native quick/slow filters (where
    /// rules become ElementParameterFilter where the parameter can be typed); the exact total, compact rows of one page,
    /// and - when the broker asks (wantIds) - every matching id (≤100,000) for the r# handle. Page calls over a handle
    /// arrive as an id slice (page_offset/page_total) and re-query rows for exactly those ids, so paging is stable.
    /// count_only / group_by answer from counts only. link= searches a linked model (read-only, host coordinates).
    /// </summary>
    internal static class FindOps
    {
        public const int MaxHandleIds = 100000;
        private const int MaxGroups = 200;

        [Op("find_elements")]
        public static OpResult Find(RequestContext ctx)
        {
            Document host = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document is open.");
            bool full = ReadKit.Full(ctx);
            bool countOnly = ctx.Args.Bool("count_only");
            string groupBy = ctx.Args.Str("group_by");
            if (!ctx.Args.Has("view") && ctx.Args.Bool("hidden")) ctx.Warn(WarningCodes.ParamIgnored, "hidden applies only with view=");

            Document searchDoc = LinkDocOrHost(ctx, host);
            List<long> selector = ReadKit.CombinedSelector(ctx, searchDoc);
            PayloadReader filterArgs = selector == null ? ctx.Args : ReadKit.WithIds(ctx, selector);
            ElementQuery query = ElementQuery.Compile(host, filterArgs, ctx, ctx.UiDoc);
            var rowOptions = new RowOptions { Full = full, LinkTransform = query.IsLink ? query.LinkTransform : null };
            List<string> cols = Columns(query, ctx, full);
            string what = What(query);

            // Page over a stored handle: rows for exactly this id slice, in handle order.
            if (ReadKit.IsHandlePage(ctx) && selector != null)
            {
                List<Element> slice = ReadKit.Existing(ctx, query.Doc, selector);
                int pageOffset = Math.Max(0, ctx.Args.IntOpt("page_offset") ?? 0);
                int pageTotal = Math.Max(slice.Count, ctx.Args.IntOpt("page_total") ?? slice.Count);
                TableBuilder pageTable = Rows.Table(slice, cols, rowOptions);
                pageTable.Total = pageTotal;
                return OpResult.Success(pageTable.ToWire(), what + " " + (pageOffset + 1).ToString("N0", CultureInfo.InvariantCulture) + "-" +
                        (pageOffset + slice.Count).ToString("N0", CultureInfo.InvariantCulture))
                    .WithPage(pageTotal, pageOffset, slice.Count);
            }

            // Fast exact count when nothing needs element-level evaluation.
            if (countOnly && string.IsNullOrWhiteSpace(groupBy) && !query.HasPostFilter)
            {
                int count = query.Count(ctx);
                return OpResult.Success(new Dictionary<string, object> { ["total"] = count }, ReadKit.Count(count, "element") + Describe(query, what));
            }

            var ids = new List<long>();
            var groups = string.IsNullOrWhiteSpace(groupBy) ? null : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Func<Element, string> groupKey = groups == null ? null : GroupKey(groupBy, query.Doc);
            int total = 0;
            foreach (Element element in query.Elements(ctx))
            {
                total++;
                if (ids.Count < MaxHandleIds) ids.Add(element.Id.Value);
                if (groups != null)
                {
                    string key = groupKey(element) ?? "(none)";
                    groups.TryGetValue(key, out int n);
                    groups[key] = n + 1;
                }
            }
            if (query.Truncated)
            {
                ctx.Warn(WarningCodes.PartialResult, "stopped at the time budget: the total is at least " + total.ToString("N0", CultureInfo.InvariantCulture) +
                    "; narrow the search (category, level, view) or use count_only");
            }
            if (total > MaxHandleIds)
            {
                ctx.Warn(WarningCodes.PartialResult, "the handle keeps the first " + MaxHandleIds.ToString("N0", CultureInfo.InvariantCulture) + " of " +
                    total.ToString("N0", CultureInfo.InvariantCulture) + " ids");
            }
            ids.Sort();

            var data = new Dictionary<string, object>(StringComparer.Ordinal) { ["total"] = total };
            if (groups != null) data["groups"] = GroupTable(groups, groupBy);
            string summary = ReadKit.Count(total, "element") + Describe(query, what);
            if (groups != null) summary += ", " + groups.Count.ToString("N0", CultureInfo.InvariantCulture) + " group" + (groups.Count == 1 ? string.Empty : "s") + " by " + groupBy;

            if (countOnly)
            {
                return OpResult.Success(data, summary);
            }

            int limit = ReadKit.Limit(ctx);
            int offset = ReadKit.Offset(ctx);
            List<Element> page = ids.Skip(offset).Take(limit).Select(id => Resolve.SafeGet(query.Doc, new ElementId(id))).Where(e => e != null).ToList();
            TableBuilder table = Rows.Table(page, cols, rowOptions);
            table.Total = total;
            foreach (KeyValuePair<string, object> pair in table.ToWire()) data[pair.Key] = pair.Value;
            if (query.IsLink) data["link"] = query.LinkName;
            return OpResult.Success(data, summary).WithPage(total, offset, page.Count, ctx.WantIds ? ids : null);
        }

        private static Document LinkDocOrHost(RequestContext ctx, Document host)
        {
            string link = ctx.Args.Str("link");
            if (string.IsNullOrWhiteSpace(link)) return host;
            Document linkDoc = Resolve.Link(host, link, ctx.Args.Field("link")).GetLinkDocument();
            return linkDoc ?? host;
        }

        private static List<string> Columns(ElementQuery query, RequestContext ctx, bool full)
        {
            List<string> cols;
            if (query.ElementTypes)
            {
                cols = new List<string> { "id", "category", "family", "name" };
                if (full) cols.AddRange(new[] { "type_mark", "comments" });
            }
            else
            {
                cols = Rows.DefaultColumns(query.SingleCategory, named: IsNamedCategory(query.SingleCategory));
                if (full)
                {
                    foreach (string extra in new[] { "comments", "workset", "phase", "uid" })
                    {
                        if (!cols.Contains(extra)) cols.Add(extra);
                    }
                }
            }
            foreach (string field in ctx.Args.Strs("fields"))
            {
                if (!cols.Contains(field, StringComparer.OrdinalIgnoreCase)) cols.Add(field);
            }
            return cols;
        }

        private static bool IsNamedCategory(BuiltInCategory? category)
        {
            switch (category)
            {
                case BuiltInCategory.OST_Levels:
                case BuiltInCategory.OST_Grids:
                case BuiltInCategory.OST_Views:
                case BuiltInCategory.OST_Sheets:
                case BuiltInCategory.OST_IOSModelGroups:
                case BuiltInCategory.OST_IOSDetailGroups:
                case BuiltInCategory.OST_VolumeOfInterest:
                case BuiltInCategory.OST_CLines:
                    return true;
                default:
                    return false;
            }
        }

        private static string What(ElementQuery query)
        {
            if (query.SingleCategory.HasValue) return EnglishAliases.LabelOf(query.SingleCategory.Value) ?? query.SingleCategory.Value.ToString();
            return query.ElementTypes ? "types" : "elements";
        }

        private static string Describe(ElementQuery query, string what)
        {
            var parts = query.Description.Where(d => !string.Equals(d, what, StringComparison.OrdinalIgnoreCase)).ToList();
            string head = what == "elements" ? string.Empty : " (" + what + ")";
            return head + (parts.Count == 0 ? string.Empty : ": " + string.Join(", ", parts));
        }

        /// <summary>Group key per element: category, type, family, level, workset, or any parameter's display value.</summary>
        internal static Func<Element, string> GroupKey(string groupBy, Document doc)
        {
            switch ((groupBy ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "category": return e => Rows.CategoryLabel(e);
                case "type": return e => Rows.TypeName(e);
                case "family": return e => Rows.FamilyName(e);
                case "level": return e => Rows.LevelName(e);
                case "workset":
                    return e =>
                    {
                        if (!e.Document.IsWorkshared) return null;
                        try { return e.Document.GetWorksetTable().GetWorkset(e.WorksetId)?.Name; } catch { return null; }
                    };
                case "phase": return e => ReadKit.NameOf(e.Document, ReadKit.BipId(e, BuiltInParameter.PHASE_CREATED));
                default:
                    return e =>
                    {
                        Parameter parameter = ParamValues.Resolve(e, groupBy) ?? ParamValues.Resolve(Rows.TypeOf(e), groupBy);
                        if (parameter == null || !parameter.HasValue) return null;
                        if (parameter.StorageType == StorageType.ElementId)
                        {
                            string name = ReadKit.NameOf(e.Document, parameter.AsElementId());
                            if (name != null) return name;
                        }
                        object value = ParamValues.Get(parameter, e.Document);
                        return value == null ? ParamValues.Display(parameter) : Convert.ToString(value, CultureInfo.InvariantCulture);
                    };
            }
        }

        /// <summary>{cols:[group,count], rows} sorted by count (desc), capped at 200 groups with an "(other N groups)" row.</summary>
        internal static Dictionary<string, object> GroupTable(Dictionary<string, int> groups, string groupBy)
        {
            List<KeyValuePair<string, int>> ordered = groups.OrderByDescending(p => p.Value).ThenBy(p => p.Key, NaturalComparer.Instance).ToList();
            var rows = new List<object>();
            foreach (KeyValuePair<string, int> pair in ordered.Take(MaxGroups)) rows.Add(new List<object> { pair.Key, pair.Value });
            if (ordered.Count > MaxGroups)
            {
                int rest = ordered.Skip(MaxGroups).Sum(p => p.Value);
                rows.Add(new List<object> { "(other " + (ordered.Count - MaxGroups).ToString(CultureInfo.InvariantCulture) + " groups)", rest });
            }
            return new Dictionary<string, object>
            {
                ["by"] = groupBy,
                ["total"] = groups.Count,
                ["cols"] = new List<object> { "group", "count" },
                ["rows"] = rows
            };
        }
    }
}
