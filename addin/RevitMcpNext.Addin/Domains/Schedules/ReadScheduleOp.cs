using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// read_schedule (SPEC §5.9, C7): the schedule's fields, filters and sorting plus its body rows (paged) from
    /// GetCellText. An id column is emitted only when the key field (default Mark) is a visible field of an itemized
    /// schedule and its values map 1:1 onto the schedule's elements; otherwise data.elementIds lists the elements and a
    /// notice says why rows have no ids. category= without schedule= lists the fields a new schedule of that category
    /// could use (TempScope probe "MCP temp schedule_fields", cached per document generation and category).
    /// </summary>
    internal static class ReadScheduleOp
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;
        private const int MaxElementIds = 500;
        private static readonly ConcurrentDictionary<string, List<Tuple<string, string>>> AvailableCache = new ConcurrentDictionary<string, List<Tuple<string, string>>>(StringComparer.Ordinal);

        [Op("read_schedule")]
        public static OpResult Read(RequestContext ctx)
        {
            Document doc = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document to read.");
            PayloadReader args = ctx.Args;
            string scheduleText = args.Str("schedule");
            if (string.IsNullOrWhiteSpace(scheduleText))
            {
                if (!args.Has("category")) throw OpException.InvalidArgs("schedule", "give schedule (to read one) or category (fields for a new schedule)");
                return Available(ctx, doc, args);
            }

            ViewSchedule schedule = ScheduleSupport.Schedule(doc, scheduleText);
            if (args.Has("category")) ctx.Warn(WarningCodes.ParamIgnored, "category: ignored when schedule is given");
            ScheduleDefinition definition = schedule.Definition;
            List<ScheduleField> fields = ScheduleSupport.Fields(schedule);

            var data = new Dictionary<string, object>
            {
                ["schedule"] = new Dictionary<string, object>
                {
                    ["id"] = schedule.Id.Value,
                    ["name"] = schedule.Name,
                    ["category"] = ScheduleSupport.CategoryLabel(doc, definition.CategoryId),
                    ["kind"] = ScheduleSupport.KindOf(schedule),
                    ["itemized"] = definition.IsItemized,
                    ["totals"] = definition.ShowGrandTotal
                },
                ["fields"] = TableOf(doc, fields),
                ["filters"] = definition.GetFilters().Select(f => (object)ScheduleSupport.DescribeFilter(doc, definition, f)).ToList(),
                ["sort"] = ScheduleSupport.SortData(definition)
            };

            if (args.Bool("available", false))
            {
                var present = new HashSet<string>(fields.Select(ScheduleSupport.FieldName), StringComparer.OrdinalIgnoreCase);
                List<Tuple<string, string>> available = definition.GetSchedulableFields()
                    .Select(f => Tuple.Create(ScheduleSupport.SchedulableName(doc, f), KindOf(f)))
                    .Where(t => t.Item1.Length > 0 && !present.Contains(t.Item1)).ToList();
                data["available"] = AvailableTable(available, args.Str("name"));
            }

            OpResult result = OpResult.Success(data);
            string summary = "schedule '" + schedule.Name + "' (" + ScheduleSupport.Count(fields.Count, "field") + ")";
            if (args.Bool("rows", true)) summary = ReadRows(ctx, doc, schedule, fields, data, result, summary);
            return result.WithSummary(summary);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Rows
        // ---------------------------------------------------------------------------------------------------------

        private static string ReadRows(RequestContext ctx, Document doc, ViewSchedule schedule, List<ScheduleField> fields, Dictionary<string, object> data, OpResult result, string summary)
        {
            PayloadReader args = ctx.Args;
            int limit = args.IntClamped("limit", DefaultLimit, 1, MaxLimit, (code, text) => ctx.Warn(code, text));
            int offset = Math.Max(0, args.Int("offset", 0));

            TableSectionData body = schedule.GetTableData().GetSectionData(SectionType.Body);
            int rowCount = body?.NumberOfRows ?? 0;
            int columnCount = body?.NumberOfColumns ?? 0;
            List<ScheduleField> visible = fields.Where(f => !f.IsHidden).ToList();
            List<string> headings = visible.Count == columnCount
                ? visible.Select(f => string.IsNullOrWhiteSpace(f.ColumnHeading) ? ScheduleSupport.FieldName(f) : f.ColumnHeading).ToList()
                : Enumerable.Range(1, columnCount).Select(i => "c" + i.ToString(CultureInfo.InvariantCulture)).ToList();

            // Leading heading rows (and blank rows right after them) are not data.
            int first = 0;
            while (first < rowCount && IsHeadingRow(schedule, first, columnCount, headings)) first++;
            if (first > 0) while (first < rowCount && IsBlankRow(schedule, first, columnCount)) first++;
            int total = Math.Max(0, rowCount - first);
            if (offset > total) offset = total;
            int end = Math.Min(total, offset + limit);

            var rows = new List<List<object>>();
            bool stopped = false;
            for (int index = offset; index < end; index++)
            {
                if (ctx.ShouldYield(index - offset) && index > offset)
                {
                    stopped = true;
                    end = index;
                    break;
                }
                var row = new List<object>(columnCount);
                for (int column = 0; column < columnCount; column++) row.Add(Cell(schedule, first + index, column));
                rows.Add(row);
            }

            // Ids: only through a unique, visible key field of an itemized schedule (C7).
            string reason;
            Dictionary<string, long> byKey = KeyMap(ctx, doc, schedule, visible, out int keyColumn, out reason, out List<ElementId> elementIds);
            if (byKey != null)
            {
                foreach (List<object> row in rows)
                {
                    string key = Convert.ToString(row[keyColumn], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                    if (key.Length == 0) continue;
                    if (!byKey.ContainsKey(key))
                    {
                        reason = "row key '" + key + "' matches no element of the schedule";
                        byKey = null;
                        break;
                    }
                }
            }

            var table = new TableBuilder(byKey != null ? new[] { "id" }.Concat(headings) : headings) { Total = total };
            foreach (List<object> row in rows)
            {
                if (byKey == null)
                {
                    table.AddRow(row);
                    continue;
                }
                string key = Convert.ToString(row[keyColumn], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                var withId = new List<object>(row.Count + 1) { key.Length > 0 && byKey.TryGetValue(key, out long id) ? (object)id : null };
                withId.AddRange(row);
                table.AddRow(withId);
            }
            data["rows"] = table.ToWire();
            if (byKey == null)
            {
                data["elementIds"] = elementIds.Take(MaxElementIds).Select(id => (object)id.Value).ToList();
                if (elementIds.Count > MaxElementIds) data["elementIdsTotal"] = elementIds.Count;
                result.Notice("NO_ROW_IDS", "rows have no ids (" + reason + "); data.elementIds lists the schedule's elements");
            }
            result.WithPage(total, offset, rows.Count);
            if (stopped) result.WithPartial("deadline", new Dictionary<string, object> { ["offset"] = offset + rows.Count });

            string shown = total == 0 ? "no rows" : rows.Count == total ? ScheduleSupport.Count(total, "row") : "rows " + (offset + 1).ToString(CultureInfo.InvariantCulture) + "-" + (offset + rows.Count).ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture);
            return summary + ", " + shown + (byKey != null ? " with ids" : string.Empty);
        }

        /// <summary>key value → element id, or null with the reason when rows cannot carry ids.</summary>
        private static Dictionary<string, long> KeyMap(RequestContext ctx, Document doc, ViewSchedule schedule, List<ScheduleField> visible, out int keyColumn, out string reason, out List<ElementId> elementIds)
        {
            keyColumn = -1;
            elementIds = new FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType().ToElementIds().ToList();
            string keyName = ctx.Args.Str("key") ?? "Mark";
            ScheduleField keyField = ScheduleSupport.FindField(schedule, keyName);
            if (keyField == null)
            {
                reason = "key field '" + keyName + "' is not in the schedule";
                return null;
            }
            if (keyField.IsHidden)
            {
                reason = "key field '" + keyName + "' is hidden";
                return null;
            }
            if (!schedule.Definition.IsItemized)
            {
                reason = "the schedule is not itemized";
                return null;
            }
            keyColumn = visible.FindIndex(f => f.FieldId == keyField.FieldId);
            TableSectionData body = schedule.GetTableData().GetSectionData(SectionType.Body);
            if (keyColumn < 0 || body == null || visible.Count != body.NumberOfColumns)
            {
                reason = "the key column could not be located";
                return null;
            }

            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            int empty = 0;
            for (int index = 0; index < elementIds.Count; index++)
            {
                Element element = doc.GetElement(elementIds[index]);
                Parameter parameter = element == null ? null : ScheduleSupport.ParameterOf(doc, element, keyField);
                string value = parameter == null ? null : (parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString());
                value = value?.Trim() ?? string.Empty;
                if (value.Length == 0)
                {
                    empty++;
                    continue;
                }
                if (map.ContainsKey(value))
                {
                    reason = "key values are not unique ('" + value + "' repeats)";
                    return null;
                }
                map[value] = elementIds[index].Value;
            }
            if (empty > 0)
            {
                reason = ScheduleSupport.Count(empty, "element") + " have no '" + keyName + "' value";
                return null;
            }
            reason = null;
            return map;
        }

        private static string Cell(ViewSchedule schedule, int row, int column)
        {
            try
            {
                return schedule.GetCellText(SectionType.Body, row, column) ?? string.Empty;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return string.Empty;
            }
        }

        private static bool IsHeadingRow(ViewSchedule schedule, int row, int columns, List<string> headings)
        {
            if (columns == 0 || headings.Count != columns) return false;
            int matches = 0;
            for (int column = 0; column < columns; column++)
            {
                string text = Cell(schedule, row, column).Trim();
                if (text.Length == 0) continue;
                if (!string.Equals(text, headings[column].Trim(), StringComparison.Ordinal)) return false;
                matches++;
            }
            return matches > 0;
        }

        private static bool IsBlankRow(ViewSchedule schedule, int row, int columns)
        {
            for (int column = 0; column < columns; column++)
            {
                if (Cell(schedule, row, column).Trim().Length > 0) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Available fields
        // ---------------------------------------------------------------------------------------------------------

        private static OpResult Available(RequestContext ctx, Document doc, PayloadReader args)
        {
            ElementId categoryId = ScheduleSupport.CategoryId(doc, args.Str("category"));
            string label = ScheduleSupport.CategoryLabel(doc, categoryId);
            if (!ViewSchedule.IsValidCategoryForSchedule(categoryId)) throw OpException.InvalidArgs("category", label + " cannot be scheduled");

            string cacheKey = ctx.Rid.ToString(CultureInfo.InvariantCulture) + "|" + ctx.Generation.ToString(CultureInfo.InvariantCulture) + "|" + categoryId.Value.ToString(CultureInfo.InvariantCulture);
            bool hit = AvailableCache.TryGetValue(cacheKey, out List<Tuple<string, string>> fields);
            if (!hit)
            {
                fields = new List<Tuple<string, string>>();
                using (var temp = new TempScope(doc, Naming.Purposes.ScheduleFields))
                {
                    List<Tuple<string, string>> probed = null;
                    TxResult tx = temp.Run(() =>
                    {
                        ViewSchedule probe = ViewSchedule.CreateSchedule(doc, categoryId);
                        probed = probe.Definition.GetSchedulableFields()
                            .Select(f => Tuple.Create(ScheduleSupport.SchedulableName(doc, f), KindOf(f)))
                            .Where(t => t.Item1.Length > 0).ToList();
                    });
                    if (!tx.Committed && probed == null) throw tx.ToException("read_schedule");
                    fields = probed ?? fields;
                }
                if (AvailableCache.Count > 64) AvailableCache.Clear();
                AvailableCache[cacheKey] = fields;
            }
            var data = new Dictionary<string, object>
            {
                ["category"] = label,
                ["available"] = AvailableTable(fields, args.Str("name"))
            };
            if (args.Has("rows") && args.Bool("rows", false)) ctx.Warn(WarningCodes.ParamIgnored, "rows: a category has no rows; give schedule to read rows");
            OpResult result = OpResult.Success(data, ScheduleSupport.Count(fields.Count, "schedulable field") + " for a new " + label + " schedule");
            return hit ? result.WithCacheHit() : result;
        }

        private static Dictionary<string, object> AvailableTable(List<Tuple<string, string>> fields, string contains)
        {
            var table = new TableBuilder(new[] { "name", "kind" });
            foreach (Tuple<string, string> field in fields
                         .Where(f => string.IsNullOrWhiteSpace(contains) || f.Item1.IndexOf(contains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                         .GroupBy(f => f.Item1 + "|" + f.Item2).Select(g => g.First())
                         .OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase))
            {
                table.Add(field.Item1, field.Item2);
            }
            return table.ToWire();
        }

        private static Dictionary<string, object> TableOf(Document doc, List<ScheduleField> fields)
        {
            var table = new TableBuilder(new[] { "name", "heading", "hidden", "kind" });
            foreach (ScheduleField field in fields)
            {
                string kind;
                try { kind = KindOf(field.FieldType); } catch (Autodesk.Revit.Exceptions.ApplicationException) { kind = null; }
                table.Add(ScheduleSupport.FieldName(field), field.ColumnHeading, field.IsHidden, kind);
            }
            return table.ToWire();
        }

        private static string KindOf(SchedulableField field)
        {
            try { return KindOf(field.FieldType); } catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        private static string KindOf(ScheduleFieldType type)
        {
            switch (type)
            {
                case ScheduleFieldType.Instance: return "instance";
                case ScheduleFieldType.ElementType: return "type";
                case ScheduleFieldType.Count: return "count";
                case ScheduleFieldType.Formula: return "formula";
                case ScheduleFieldType.Percentage: return "percentage";
                case ScheduleFieldType.MaterialQuantity: return "material quantity";
                case ScheduleFieldType.Material: return "material";
                case ScheduleFieldType.Room: return "room";
                case ScheduleFieldType.FromRoom: return "from room";
                case ScheduleFieldType.ToRoom: return "to room";
                default: return type.ToString().ToLowerInvariant();
            }
        }
    }
}
