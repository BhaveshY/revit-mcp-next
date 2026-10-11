using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// edit_schedules create | add_fields | remove_fields | set_field | order_fields | set_filters | set_sort | set_options |
    /// duplicate (D3 §3.6). Fields resolve by localized name, heading or English built-in label; filters are
    /// "Field op value" strings typed from the field's parameter (lengths mm, element ids by name, yes/no).
    /// </summary>
    internal static class ScheduleOps
    {
        private static readonly string[] DefaultRegularFields = { "Family and Type", "Count" };

        [Op("edit_schedules.create")]
        public static void Create(ChangeContext c)
        {
            Document doc = c.Doc;
            ElementId categoryId = ScheduleSupport.CategoryId(doc, c.Str("category"));
            string kind = c.Args.Enum("kind", "regular", "regular", "material_takeoff", "key");
            string label = ScheduleSupport.CategoryLabel(doc, categoryId);
            bool valid;
            switch (kind)
            {
                case "material_takeoff": valid = ViewSchedule.IsValidCategoryForMaterialTakeoff(categoryId); break;
                case "key": valid = categoryId != ElementId.InvalidElementId && ViewSchedule.IsValidCategoryForKeySchedule(categoryId); break;
                default: valid = ViewSchedule.IsValidCategoryForSchedule(categoryId); break;
            }
            if (!valid) throw OpException.InvalidArgs("category", label + " cannot have a " + kind.Replace('_', ' ') + " schedule");

            string name = c.Str("name")?.Trim();
            if (!string.IsNullOrEmpty(name)) EnsureNameFree(doc, name, "name", null);

            ViewSchedule schedule = kind == "material_takeoff" ? ViewSchedule.CreateMaterialTakeoff(doc, categoryId)
                : kind == "key" ? ViewSchedule.CreateKeySchedule(doc, categoryId)
                : ViewSchedule.CreateSchedule(doc, categoryId);
            if (schedule == null) throw c.Error(ErrorCodes.RevitRefused, "Revit did not create the " + label + " schedule.");
            if (!string.IsNullOrEmpty(name)) schedule.Name = name;
            c.Created(schedule);
            c.Output("schedule", schedule.Id);

            var skipped = new List<string>();
            List<string> fields = c.Args.Strs("fields");
            if (fields.Count > 0)
            {
                ScheduleSupport.AddFields(doc, schedule, fields, c.Args.Field("fields"), skipped);
            }
            else if (kind == "regular")
            {
                foreach (string field in DefaultRegularFields)
                {
                    if (ScheduleSupport.FindField(schedule, field) != null) continue;
                    try
                    {
                        ScheduleSupport.AddFields(doc, schedule, new[] { field }, "fields", null);
                    }
                    catch (OpException)
                    {
                        // The category has no such field.
                    }
                }
            }

            ScheduleDefinition definition = schedule.Definition;
            if (c.Has("filters")) SetFilters(c, schedule, c.Args.Strs("filters"));
            if (c.Has("sort")) SetSort(c, schedule, c.Args.Strs("sort"), headers: false, footers: false);
            if (c.Has("itemize")) definition.IsItemized = c.Bool("itemize", true);
            if (c.Has("totals")) definition.ShowGrandTotal = c.Bool("totals", false);

            var data = new Dictionary<string, object> { ["schedule"] = ScheduleSupport.ScheduleData(doc, schedule) };
            if (skipped.Count > 0) data["fields_already_present"] = skipped.Cast<object>().ToList();
            c.Data(data);
            c.Summary("created " + kind.Replace('_', ' ') + " schedule '" + schedule.Name + "' for " + label + " with " +
                      ScheduleSupport.Count(ScheduleSupport.Fields(schedule).Count, "field") +
                      (fields.Count == 0 && kind == "regular" ? " (default fields; add more with add_fields)" : string.Empty));
        }

        [Op("edit_schedules.add_fields")]
        public static void AddFields(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSchedule schedule = ScheduleSupport.Schedule(doc, c.Str("schedule"));
            List<string> fields = RequireList(c, "fields");
            var skipped = new List<string>();
            List<ScheduleField> added = ScheduleSupport.AddFields(doc, schedule, fields, c.Args.Field("fields"), skipped);
            if (added.Count > 0) c.Modified(schedule, "fields", null);
            if (skipped.Count > 0) c.Warn(WarningCodes.ParamIgnored, "fields already in '" + schedule.Name + "': " + string.Join(", ", skipped));
            Finish(c, doc, schedule, "added " + ScheduleSupport.Count(added.Count, "field") + " to '" + schedule.Name + "'");
        }

        [Op("edit_schedules.remove_fields")]
        public static void RemoveFields(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSchedule schedule = ScheduleSupport.Schedule(doc, c.Str("schedule"));
            List<string> fields = RequireList(c, "fields");
            ScheduleDefinition definition = schedule.Definition;
            var removed = new List<string>();
            var droppedRules = new List<string>();
            for (int index = 0; index < fields.Count; index++)
            {
                ScheduleField field = ScheduleSupport.RequireField(schedule, fields[index], c.Args.Field("fields") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                ScheduleFieldId fieldId = field.FieldId;
                string name = ScheduleSupport.FieldName(field);

                // Filters and sorting that use the field go first (Revit refuses to remove a field in use).
                List<ScheduleFilter> filters = definition.GetFilters().ToList();
                if (filters.Any(f => f.FieldId == fieldId))
                {
                    droppedRules.AddRange(filters.Where(f => f.FieldId == fieldId).Select(f => ScheduleSupport.DescribeFilter(doc, definition, f)));
                    definition.ClearFilters();
                    foreach (ScheduleFilter keep in filters.Where(f => f.FieldId != fieldId)) definition.AddFilter(keep);
                }
                List<ScheduleSortGroupField> sorts = definition.GetSortGroupFields().ToList();
                if (sorts.Any(s => s.FieldId == fieldId))
                {
                    droppedRules.Add("sort by " + name);
                    definition.ClearSortGroupFields();
                    foreach (ScheduleSortGroupField keep in sorts.Where(s => s.FieldId != fieldId)) definition.AddSortGroupField(keep);
                }
                definition.RemoveField(fieldId);
                removed.Add(name);
            }
            c.Modified(schedule, "fields", null);
            if (droppedRules.Count > 0) c.Warn(WarningCodes.PartialResult, "also removed rules that used these fields: " + string.Join("; ", droppedRules), new[] { schedule.Id });
            Finish(c, doc, schedule, "removed " + string.Join(", ", removed) + " from '" + schedule.Name + "'");
        }

        [Op("edit_schedules.set_field")]
        public static void SetField(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSchedule schedule = ScheduleSupport.Schedule(doc, c.Str("schedule"));
            ScheduleField field = ScheduleSupport.RequireField(schedule, c.Str("field"), "field");
            if (!c.Has("heading") && !c.Has("hidden") && !c.Has("totals"))
            {
                throw OpException.InvalidArgs("heading", "give heading, hidden or totals",
                    new Dictionary<string, object> { ["op"] = "set_field", ["schedule"] = schedule.Name, ["field"] = ScheduleSupport.FieldName(field), ["heading"] = ScheduleSupport.FieldName(field) });
            }
            var changed = new List<string>();
            if (c.Has("heading"))
            {
                string heading = c.Str("heading") ?? string.Empty;
                if (!string.Equals(field.ColumnHeading, heading, StringComparison.Ordinal))
                {
                    field.ColumnHeading = heading;
                    changed.Add("heading '" + heading + "'");
                }
            }
            if (c.Has("hidden"))
            {
                bool hidden = c.Bool("hidden", false);
                if (field.IsHidden != hidden)
                {
                    field.IsHidden = hidden;
                    changed.Add(hidden ? "hidden" : "shown");
                }
            }
            if (c.Has("totals"))
            {
                bool totals = c.Bool("totals", false);
                if (totals && !field.CanTotal())
                {
                    throw OpException.InvalidArgs("totals", "'" + ScheduleSupport.FieldName(field) + "' cannot be totalled (only numeric fields can)");
                }
                ScheduleFieldDisplayType wanted = totals ? ScheduleFieldDisplayType.Totals : ScheduleFieldDisplayType.Standard;
                if (field.DisplayType != wanted)
                {
                    field.DisplayType = wanted;
                    changed.Add(totals ? "column total on" : "column total off");
                }
            }
            if (changed.Count > 0) c.Modified(schedule, "field", null);
            c.Data(new Dictionary<string, object> { ["schedule"] = schedule.Name, ["field"] = ScheduleSupport.FieldData(doc, field) });
            c.Output("schedule", schedule.Id);
            c.Summary(changed.Count == 0
                ? "'" + ScheduleSupport.FieldName(field) + "' in '" + schedule.Name + "' already has these settings"
                : "'" + ScheduleSupport.FieldName(field) + "' in '" + schedule.Name + "': " + string.Join(", ", changed));
        }

        [Op("edit_schedules.order_fields")]
        public static void OrderFields(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSchedule schedule = ScheduleSupport.Schedule(doc, c.Str("schedule"));
            List<string> names = RequireList(c, "fields");
            ScheduleDefinition definition = schedule.Definition;
            var ordered = new List<ScheduleFieldId>();
            for (int index = 0; index < names.Count; index++)
            {
                ScheduleField field = ScheduleSupport.RequireField(schedule, names[index], c.Args.Field("fields") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                if (!ordered.Contains(field.FieldId)) ordered.Add(field.FieldId);
            }
            // Fields not named keep their relative order after the named ones.
            foreach (ScheduleFieldId id in definition.GetFieldOrder())
            {
                if (!ordered.Contains(id)) ordered.Add(id);
            }
            definition.SetFieldOrder(ordered);
            c.Modified(schedule, "field order", null);
            Finish(c, doc, schedule, "field order of '" + schedule.Name + "': " + string.Join(", ", ScheduleSupport.Fields(schedule).Select(ScheduleSupport.FieldName).Take(12)));
        }

        [Op("edit_schedules.set_filters")]
        public static void SetFiltersOp(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSchedule schedule = ScheduleSupport.Schedule(doc, c.Str("schedule"));
            if (!c.Args.TryGet("filters", out _)) throw OpException.InvalidArgs("filters", "is required ([] clears the filters)");
            List<string> filters = c.Args.Strs("filters");
            SetFilters(c, schedule, filters);
            c.Modified(schedule, "filters", null);
            Finish(c, doc, schedule, filters.Count == 0 ? "cleared the filters of '" + schedule.Name + "'" : "'" + schedule.Name + "' filtered by " + string.Join(" and ", filters));
        }

        [Op("edit_schedules.set_sort")]
        public static void SetSortOp(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSchedule schedule = ScheduleSupport.Schedule(doc, c.Str("schedule"));
            if (!c.Args.TryGet("sort", out _)) throw OpException.InvalidArgs("sort", "is required (fields in order, '-' prefix for descending; [] clears)");
            List<string> sort = c.Args.Strs("sort");
            bool totals = c.Bool("totals", false);
            SetSort(c, schedule, sort, c.Bool("headers", false), totals);
            if (c.Has("totals")) schedule.Definition.ShowGrandTotal = totals;
            c.Modified(schedule, "sort", null);
            Finish(c, doc, schedule, sort.Count == 0 ? "cleared the sorting of '" + schedule.Name + "'" : "'" + schedule.Name + "' sorted by " + string.Join(", ", sort));
        }

        [Op("edit_schedules.set_options")]
        public static void SetOptions(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSchedule schedule = ScheduleSupport.Schedule(doc, c.Str("schedule"));
            ScheduleDefinition definition = schedule.Definition;
            if (!c.Has("name") && !c.Has("itemize") && !c.Has("totals"))
            {
                throw OpException.InvalidArgs("name", "give name, itemize or totals",
                    new Dictionary<string, object> { ["op"] = "set_options", ["schedule"] = schedule.Name, ["itemize"] = true });
            }
            var changed = new List<string>();
            string name = c.Str("name")?.Trim();
            if (!string.IsNullOrEmpty(name) && !string.Equals(name, schedule.Name, StringComparison.Ordinal))
            {
                EnsureNameFree(doc, name, "name", schedule.Id);
                string before = schedule.Name;
                schedule.Name = name;
                changed.Add("renamed from '" + before + "'");
            }
            if (c.Has("itemize") && definition.IsItemized != c.Bool("itemize", true))
            {
                definition.IsItemized = c.Bool("itemize", true);
                changed.Add(definition.IsItemized ? "itemized" : "not itemized");
            }
            if (c.Has("totals") && definition.ShowGrandTotal != c.Bool("totals", false))
            {
                definition.ShowGrandTotal = c.Bool("totals", false);
                changed.Add(definition.ShowGrandTotal ? "grand totals on" : "grand totals off");
            }
            if (changed.Count > 0) c.Modified(schedule, "options", null);
            Finish(c, doc, schedule, changed.Count == 0 ? "'" + schedule.Name + "' already has these options" : "'" + schedule.Name + "': " + string.Join(", ", changed));
        }

        [Op("edit_schedules.duplicate")]
        public static void Duplicate(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSchedule source = ScheduleSupport.Schedule(doc, c.Str("schedule"));
            string name = (c.Str("name") ?? string.Empty).Trim();
            if (name.Length == 0) throw OpException.InvalidArgs("name", "is required (the new schedule name)");
            EnsureNameFree(doc, name, "name", null);
            if (!source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Revit cannot duplicate schedule '" + source.Name + "'.", new Dictionary<string, object> { ["schedule"] = source.Name });
            }
            ElementId newId = source.Duplicate(ViewDuplicateOption.Duplicate);
            ViewSchedule copy = doc.GetElement(newId) as ViewSchedule ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not duplicate schedule '" + source.Name + "'.");
            copy.Name = name;
            c.Created(copy);
            c.Output("schedule", copy.Id);
            c.Data(new Dictionary<string, object> { ["schedule"] = ScheduleSupport.ScheduleData(doc, copy), ["source"] = source.Name });
            c.Summary("duplicated schedule '" + source.Name + "' as '" + name + "'");
        }

        // ---------------------------------------------------------------------------------------------------------

        private static void Finish(ChangeContext c, Document doc, ViewSchedule schedule, string summary)
        {
            c.Output("schedule", schedule.Id);
            ScheduleDefinition definition = schedule.Definition;
            c.Data(new Dictionary<string, object>
            {
                ["schedule"] = ScheduleSupport.ScheduleData(doc, schedule),
                ["filters"] = definition.GetFilters().Select(f => (object)ScheduleSupport.DescribeFilter(doc, definition, f)).ToList(),
                ["sort"] = ScheduleSupport.SortData(definition)
            });
            c.Summary(summary);
        }

        private static List<string> RequireList(ChangeContext c, string key)
        {
            List<string> values = c.Args.Strs(key);
            if (values.Count == 0) throw OpException.InvalidArgs(key, "needs at least one field name");
            return values;
        }

        private static void EnsureNameFree(Document doc, string name, string param, ElementId self)
        {
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => self == null || s.Id != self).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(name)) return;
            string next = name;
            for (int n = 2; n < 1000 && names.Contains(next); n++) next = name + " " + n.ToString(CultureInfo.InvariantCulture);
            throw new OpException(ErrorCodes.NameTaken, "A schedule named '" + name + "' already exists in '" + doc.Title + "'.",
                new Dictionary<string, object> { ["param"] = param, ["value"] = name, ["next"] = next });
        }

        /// <summary>Replaces the filters (max 8, ANDed); fields used only for filtering are added hidden.</summary>
        private static void SetFilters(ChangeContext c, ViewSchedule schedule, List<string> texts)
        {
            Document doc = c.Doc;
            if (texts.Count > 0 && !schedule.Definition.CanFilter())
            {
                throw OpException.InvalidArgs("filters", "'" + schedule.Name + "' cannot be filtered");
            }
            if (texts.Count > ScheduleSupport.MaxFilters)
            {
                throw OpException.InvalidArgs("filters", "has " + texts.Count.ToString(CultureInfo.InvariantCulture) + " filters; Revit allows at most " + ScheduleSupport.MaxFilters.ToString(CultureInfo.InvariantCulture));
            }
            var filters = new List<ScheduleFilter>();
            var addedHidden = new List<string>();
            for (int index = 0; index < texts.Count; index++)
            {
                string param = c.Args.Field("filters") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                ScheduleFilterSpec spec = ScheduleSupport.ParseFilter(texts[index], param);
                ScheduleField field = ScheduleSupport.FindField(schedule, spec.Field);
                if (field == null)
                {
                    SchedulableField schedulable = ScheduleSupport.FindSchedulable(doc, schedule, spec.Field, param);
                    field = schedule.Definition.AddField(schedulable);
                    field.IsHidden = true;
                    addedHidden.Add(ScheduleSupport.FieldName(field));
                }
                if (!ScheduleSupport.CanFilter(schedule.Definition, field.FieldId, spec.Type))
                {
                    throw OpException.InvalidArgs(param, "'" + ScheduleSupport.FieldName(field) + "' cannot be filtered with '" + spec.Text + "' in this schedule");
                }
                filters.Add(ScheduleSupport.BuildFilter(doc, schedule, field, spec, param));
            }
            ScheduleDefinition definition = schedule.Definition;
            definition.ClearFilters();
            foreach (ScheduleFilter filter in filters) definition.AddFilter(filter);
            if (addedHidden.Count > 0)
            {
                c.Warn(WarningCodes.PartialResult, "added hidden field(s) " + string.Join(", ", addedHidden) + " to '" + schedule.Name + "' because filters need their fields in the schedule", new[] { schedule.Id });
            }
        }

        /// <summary>Replaces the sort/group fields ('-Field' = descending).</summary>
        private static void SetSort(ChangeContext c, ViewSchedule schedule, List<string> texts, bool headers, bool footers)
        {
            var sorts = new List<ScheduleSortGroupField>();
            for (int index = 0; index < texts.Count; index++)
            {
                string text = texts[index].Trim();
                bool descending = text.StartsWith("-", StringComparison.Ordinal);
                if (descending || text.StartsWith("+", StringComparison.Ordinal)) text = text.Substring(1).Trim();
                ScheduleField field = ScheduleSupport.RequireField(schedule, text, c.Args.Field("sort") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                if (!schedule.Definition.CanSortByField(field.FieldId))
                {
                    throw OpException.InvalidArgs(c.Args.Field("sort") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", "'" + ScheduleSupport.FieldName(field) + "' cannot be used for sorting");
                }
                sorts.Add(new ScheduleSortGroupField(field.FieldId, descending ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending)
                {
                    ShowHeader = headers,
                    ShowFooter = footers
                });
            }
            ScheduleDefinition definition = schedule.Definition;
            definition.ClearSortGroupFields();
            foreach (ScheduleSortGroupField sort in sorts) definition.AddSortGroupField(sort);
        }
    }
}
