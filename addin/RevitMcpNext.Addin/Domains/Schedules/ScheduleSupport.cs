using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>A parsed schedule filter "Field op value".</summary>
    internal sealed class ScheduleFilterSpec
    {
        public string Field;
        public ScheduleFilterType Type;
        public string Value;
        public string Text;
    }

    /// <summary>
    /// Shared helpers of edit_schedules and read_schedule (P-SHEETS-ANNO): schedule and category resolution, field
    /// lookup by English or localized name, filter parsing and typing, and the field/filter/sort descriptions.
    /// </summary>
    internal static class ScheduleSupport
    {
        private static readonly Regex FilterPattern = new Regex(
            @"^\s*(?<field>.+?)\s*(?<op>!=|<>|>=|<=|=|>|<|\s(?:not\s+contains|contains|not\s+begins\s+with|begins\s+with|starts\s+with|startswith|not\s+ends\s+with|ends\s+with|endswith|has\s+no\s+value|has\s+value|is\s+empty|is\s+not\s+empty)\b)\s*(?<value>.*?)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public const int MaxFilters = 8;

        // ---------------------------------------------------------------------------------------------------------
        // Schedules and categories
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>A user schedule (not a template, titleblock revision or keynote schedule) by id or name.</summary>
        public static ViewSchedule Schedule(Document doc, string value, string param = "schedule")
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) throw OpException.InvalidArgs(param, "is required (a schedule name or id)");
            ViewSchedule byId = Resolve.ById<ViewSchedule>(doc, text);
            if (byId != null) return byId;
            List<ViewSchedule> schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !IsSpecial(s)).ToList();
            return Resolve.Single(schedules, s => s.Name, text, param, "schedule");
        }

        public static bool IsSpecial(ViewSchedule schedule)
        {
            try
            {
                return schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        /// <summary>The category id for a new schedule ("multi-category" → InvalidElementId).</summary>
        public static ElementId CategoryId(Document doc, string value, string param = "category")
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) throw OpException.InvalidArgs(param, "is required (a category such as Doors)");
            string compact = text.Replace(" ", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            if (compact == "multicategory" || compact == "multi" || compact == "all" || compact == "<multicategory>") return ElementId.InvalidElementId;
            return new ElementId(Resolve.CategoryId(doc, text, param));
        }

        public static string CategoryLabel(Document doc, ElementId categoryId)
        {
            if (categoryId == null || categoryId == ElementId.InvalidElementId) return "Multi-Category";
            try
            {
                Category category = Category.GetCategory(doc, categoryId);
                return Rows.CategoryLabel(category) ?? categoryId.Value.ToString(CultureInfo.InvariantCulture);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return categoryId.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static string KindOf(ViewSchedule schedule)
        {
            ScheduleDefinition definition = schedule.Definition;
            try
            {
                if (definition.IsMaterialTakeoff) return "material_takeoff";
                if (definition.IsKeySchedule) return "key";
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Older special schedules.
            }
            return "regular";
        }

        // ---------------------------------------------------------------------------------------------------------
        // Fields
        // ---------------------------------------------------------------------------------------------------------

        public static List<ScheduleField> Fields(ViewSchedule schedule)
        {
            ScheduleDefinition definition = schedule.Definition;
            return definition.GetFieldOrder().Select(id => definition.GetField(id)).Where(f => f != null).ToList();
        }

        public static string FieldName(ScheduleField field)
        {
            try
            {
                string name = field.GetName();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Fall back to the heading.
            }
            return field.ColumnHeading ?? string.Empty;
        }

        public static string SchedulableName(Document doc, SchedulableField field)
        {
            try
            {
                return field.GetName(doc) ?? string.Empty;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return string.Empty;
            }
        }

        /// <summary>The schedule field named <paramref name="name"/> (name, heading or English built-in label), or null.</summary>
        public static ScheduleField FindField(ViewSchedule schedule, string name)
        {
            string text = (name ?? string.Empty).Trim();
            if (text.Length == 0) return null;
            List<ScheduleField> fields = Fields(schedule);
            ScheduleField match = fields.FirstOrDefault(f => string.Equals(FieldName(f), text, StringComparison.Ordinal))
                                  ?? fields.FirstOrDefault(f => Resolve.Eq(FieldName(f), text))
                                  ?? fields.FirstOrDefault(f => Resolve.Eq(f.ColumnHeading, text));
            if (match != null) return match;
            var builtIns = new HashSet<long>(EnglishAliases.Parameters(text).Select(b => (long)(int)b));
            return builtIns.Count == 0 ? null : fields.FirstOrDefault(f => f.ParameterId != null && builtIns.Contains(f.ParameterId.Value));
        }

        /// <summary>Like <see cref="FindField"/> but NOT_FOUND with the schedule's fields as candidates.</summary>
        public static ScheduleField RequireField(ViewSchedule schedule, string name, string param)
        {
            ScheduleField field = FindField(schedule, name);
            if (field != null) return field;
            throw OpException.NotFound(param, "field of '" + schedule.Name + "'", name, Resolve.Closest(name, Fields(schedule).Select(FieldName)));
        }

        /// <summary>A schedulable field of the schedule's category by name (localized or English built-in label).</summary>
        public static SchedulableField FindSchedulable(Document doc, ViewSchedule schedule, string name, string param)
        {
            string text = (name ?? string.Empty).Trim();
            if (text.Length == 0) throw OpException.InvalidArgs(param, "is empty (a field name such as Mark)");
            IList<SchedulableField> candidates = schedule.Definition.GetSchedulableFields();
            var named = candidates.Select(f => new { Field = f, Name = SchedulableName(doc, f) }).ToList();
            var exact = named.Where(n => string.Equals(n.Name, text, StringComparison.Ordinal)).ToList();
            if (exact.Count >= 1) return Prefer(exact.Select(n => n.Field).ToList());
            var loose = named.Where(n => Resolve.Eq(n.Name, text)).ToList();
            if (loose.Count >= 1) return Prefer(loose.Select(n => n.Field).ToList());
            var builtIns = new HashSet<long>(EnglishAliases.Parameters(text).Select(b => (long)(int)b));
            List<SchedulableField> byBuiltIn = candidates.Where(f => f.ParameterId != null && builtIns.Contains(f.ParameterId.Value)).ToList();
            if (byBuiltIn.Count >= 1) return Prefer(byBuiltIn);
            throw OpException.NotFound(param, "schedulable field for '" + schedule.Name + "'", name, Resolve.Closest(text, named.Select(n => n.Name)));
        }

        /// <summary>Instance fields first when a name exists as instance and type field.</summary>
        private static SchedulableField Prefer(List<SchedulableField> fields)
        {
            return fields.FirstOrDefault(f => f.FieldType == ScheduleFieldType.Instance) ?? fields[0];
        }

        /// <summary>Adds the fields (skipping ones already present); returns the added fields.</summary>
        public static List<ScheduleField> AddFields(Document doc, ViewSchedule schedule, IList<string> names, PayloadReader args, List<string> skipped)
        {
            var added = new List<ScheduleField>();
            for (int index = 0; index < names.Count; index++)
            {
                string field = (args == null ? "fields" : args.Field("fields")) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                if (FindField(schedule, names[index]) != null)
                {
                    skipped?.Add(names[index]);
                    continue;
                }
                SchedulableField schedulable;
                try
                {
                    schedulable = FindSchedulable(doc, schedule, names[index], field);
                }
                catch (OpException ex) when (args != null && (ex.Code == ErrorCodes.NotFound || ex.Code == ErrorCodes.AmbiguousName))
                {
                    throw ListArgs.Rewrap(ex, args, "fields", names.ToList(), index);
                }
                added.Add(schedule.Definition.AddField(schedulable));
            }
            return added;
        }

        public static Dictionary<string, object> FieldData(Document doc, ScheduleField field)
        {
            var data = new Dictionary<string, object>
            {
                ["name"] = FieldName(field),
                ["heading"] = field.ColumnHeading,
                ["hidden"] = field.IsHidden
            };
            try
            {
                if (field.DisplayType != ScheduleFieldDisplayType.Standard) data["display"] = field.DisplayType.ToString().ToLowerInvariant();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Field kinds without a display type.
            }
            return data;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Filters
        // ---------------------------------------------------------------------------------------------------------

        public static ScheduleFilterSpec ParseFilter(string text, string param)
        {
            Match match = FilterPattern.Match(text ?? string.Empty);
            if (!match.Success)
            {
                throw OpException.InvalidArgs(param, "'" + text + "' is not 'Field op value' (ops: = != > >= < <= contains, not contains, begins with, ends with, has value, has no value)");
            }
            string op = Regex.Replace(match.Groups["op"].Value.Trim().ToLowerInvariant(), @"\s+", " ");
            ScheduleFilterType type;
            switch (op)
            {
                case "=": type = ScheduleFilterType.Equal; break;
                case "!=": case "<>": type = ScheduleFilterType.NotEqual; break;
                case ">": type = ScheduleFilterType.GreaterThan; break;
                case ">=": type = ScheduleFilterType.GreaterThanOrEqual; break;
                case "<": type = ScheduleFilterType.LessThan; break;
                case "<=": type = ScheduleFilterType.LessThanOrEqual; break;
                case "contains": type = ScheduleFilterType.Contains; break;
                case "not contains": type = ScheduleFilterType.NotContains; break;
                case "begins with": case "starts with": case "startswith": type = ScheduleFilterType.BeginsWith; break;
                case "not begins with": type = ScheduleFilterType.NotBeginsWith; break;
                case "ends with": case "endswith": type = ScheduleFilterType.EndsWith; break;
                case "not ends with": type = ScheduleFilterType.NotEndsWith; break;
                case "has value": case "is not empty": type = ScheduleFilterType.HasValue; break;
                case "has no value": case "is empty": type = ScheduleFilterType.HasNoValue; break;
                default: throw OpException.InvalidArgs(param, "unknown operator '" + op + "'");
            }
            string value = match.Groups["value"].Value;
            if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[value.Length - 1] == value[0]) value = value.Substring(1, value.Length - 2);
            bool needsValue = type != ScheduleFilterType.HasValue && type != ScheduleFilterType.HasNoValue;
            if (needsValue && value.Length == 0) throw OpException.InvalidArgs(param, "'" + text + "' has no value after the operator");
            return new ScheduleFilterSpec { Field = match.Groups["field"].Value.Trim(), Type = type, Value = value, Text = text };
        }

        /// <summary>
        /// Builds a typed ScheduleFilter for a field of the schedule. The value type follows the parameter's storage type
        /// (sampled from an element of the schedule, else derived from the field's spec): lengths in mm (units allowed),
        /// element ids by name (e.g. Level = Level 1), yes/no as 1/0.
        /// </summary>
        public static ScheduleFilter BuildFilter(Document doc, ViewSchedule schedule, ScheduleField field, ScheduleFilterSpec spec, string param)
        {
            if (spec.Type == ScheduleFilterType.HasValue || spec.Type == ScheduleFilterType.HasNoValue) return new ScheduleFilter(field.FieldId, spec.Type);
            ForgeTypeId specType = SafeSpec(field);
            StorageType storage = StorageOf(doc, schedule, field, specType);
            switch (storage)
            {
                case StorageType.Double:
                {
                    double number;
                    ValueKind kind = Units.KindOf(specType);
                    if (kind == ValueKind.Length && Units.TryParseLength(spec.Value, 1.0, out double mm)) number = Units.MmToFt(mm);
                    else if (PayloadReader.TryNumber(spec.Value, out double raw)) number = Units.ToInternal(specType, raw, doc);
                    else throw OpException.InvalidArgs(param, "'" + spec.Field + "' is numeric; '" + spec.Value + "' is not a number" + (kind == ValueKind.Length ? " (mm or with a unit)" : string.Empty));
                    return new ScheduleFilter(field.FieldId, spec.Type, number);
                }
                case StorageType.Integer:
                {
                    string lower = spec.Value.Trim().ToLowerInvariant();
                    int value;
                    if (lower == "yes" || lower == "true" || lower == "ja") value = 1;
                    else if (lower == "no" || lower == "false" || lower == "nein") value = 0;
                    else if (!int.TryParse(spec.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        throw OpException.InvalidArgs(param, "'" + spec.Field + "' is an integer or yes/no field; '" + spec.Value + "' is not");
                    }
                    return new ScheduleFilter(field.FieldId, spec.Type, value);
                }
                case StorageType.ElementId:
                    return new ScheduleFilter(field.FieldId, spec.Type, ElementValue(doc, schedule, field, spec, param));
                default:
                    return new ScheduleFilter(field.FieldId, spec.Type, spec.Value);
            }
        }

        private static ElementId ElementValue(Document doc, ViewSchedule schedule, ScheduleField field, ScheduleFilterSpec spec, string param)
        {
            long? id = Wire.ToLong(spec.Value.Trim());
            if (id.HasValue && Resolve.SafeGet(doc, new ElementId(id.Value)) != null) return new ElementId(id.Value);
            if (field.ParameterId != null && (field.ParameterId.Value == (int)BuiltInParameter.SCHEDULE_LEVEL_PARAM || field.ParameterId.Value == (int)BuiltInParameter.LEVEL_PARAM ||
                                              field.ParameterId.Value == (int)BuiltInParameter.FAMILY_LEVEL_PARAM || field.ParameterId.Value == (int)BuiltInParameter.ROOM_LEVEL_ID))
            {
                return Resolve.Level(doc, spec.Value, param).Id;
            }
            // Other element-valued fields: match the names of the values the schedule's elements use.
            var names = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            foreach (Element element in Sample(doc, schedule, 2000))
            {
                Parameter parameter = ParameterOf(doc, element, field);
                if (parameter == null || parameter.StorageType != StorageType.ElementId) continue;
                ElementId value = parameter.AsElementId();
                Element target = Resolve.SafeGet(doc, value);
                if (target != null && !names.ContainsKey(target.Name)) names[target.Name] = value;
            }
            if (names.TryGetValue(spec.Value.Trim(), out ElementId found)) return found;
            throw OpException.NotFound(param, "value of '" + spec.Field + "'", spec.Value, Resolve.Closest(spec.Value, names.Keys));
        }

        /// <summary>Whether the field supports this kind of filter (value, substring or presence).</summary>
        public static bool CanFilter(ScheduleDefinition definition, ScheduleFieldId fieldId, ScheduleFilterType type)
        {
            try
            {
                switch (type)
                {
                    case ScheduleFilterType.HasValue:
                    case ScheduleFilterType.HasNoValue:
                        return definition.CanFilterByValuePresence(fieldId);
                    case ScheduleFilterType.Contains:
                    case ScheduleFilterType.NotContains:
                    case ScheduleFilterType.BeginsWith:
                    case ScheduleFilterType.NotBeginsWith:
                    case ScheduleFilterType.EndsWith:
                    case ScheduleFilterType.NotEndsWith:
                        return definition.CanFilterBySubstring(fieldId);
                    default:
                        return definition.CanFilterByValue(fieldId);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        public static string DescribeFilter(Document doc, ScheduleDefinition definition, ScheduleFilter filter)
        {
            ScheduleField field = definition.GetField(filter.FieldId);
            string name = field == null ? "?" : FieldName(field);
            string op;
            switch (filter.FilterType)
            {
                case ScheduleFilterType.Equal: op = "="; break;
                case ScheduleFilterType.NotEqual: op = "!="; break;
                case ScheduleFilterType.GreaterThan: op = ">"; break;
                case ScheduleFilterType.GreaterThanOrEqual: op = ">="; break;
                case ScheduleFilterType.LessThan: op = "<"; break;
                case ScheduleFilterType.LessThanOrEqual: op = "<="; break;
                case ScheduleFilterType.Contains: op = "contains"; break;
                case ScheduleFilterType.NotContains: op = "not contains"; break;
                case ScheduleFilterType.BeginsWith: op = "begins with"; break;
                case ScheduleFilterType.NotBeginsWith: op = "not begins with"; break;
                case ScheduleFilterType.EndsWith: op = "ends with"; break;
                case ScheduleFilterType.NotEndsWith: op = "not ends with"; break;
                case ScheduleFilterType.HasValue: return name + " has value";
                case ScheduleFilterType.HasNoValue: return name + " has no value";
                default: op = filter.FilterType.ToString(); break;
            }
            string value;
            if (filter.IsStringValue) value = filter.GetStringValue();
            else if (filter.IsIntegerValue) value = filter.GetIntegerValue().ToString(CultureInfo.InvariantCulture);
            else if (filter.IsDoubleValue)
            {
                object output = field == null ? filter.GetDoubleValue() : Units.ToOutput(SafeSpec(field), filter.GetDoubleValue(), doc);
                value = Convert.ToString(output, CultureInfo.InvariantCulture);
            }
            else if (filter.IsElementIdValue)
            {
                ElementId id = filter.GetElementIdValue();
                value = Resolve.SafeGet(doc, id)?.Name ?? id.Value.ToString(CultureInfo.InvariantCulture);
            }
            else value = string.Empty;
            return name + " " + op + " " + value;
        }

        public static List<object> SortData(ScheduleDefinition definition)
        {
            var list = new List<object>();
            foreach (ScheduleSortGroupField sort in definition.GetSortGroupFields())
            {
                ScheduleField field = definition.GetField(sort.FieldId);
                string text = (sort.SortOrder == ScheduleSortOrder.Descending ? "-" : string.Empty) + (field == null ? "?" : FieldName(field));
                if (sort.ShowHeader) text += " (header)";
                if (sort.ShowFooter) text += " (footer)";
                list.Add(text);
            }
            return list;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Elements and parameter values
        // ---------------------------------------------------------------------------------------------------------

        public static IEnumerable<Element> Sample(Document doc, ViewSchedule schedule, int max)
        {
            int n = 0;
            foreach (Element element in new FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType())
            {
                if (n++ >= max) yield break;
                yield return element;
            }
        }

        /// <summary>The element's (or its type's) parameter behind a schedule field, or null.</summary>
        public static Parameter ParameterOf(Document doc, Element element, ScheduleField field)
        {
            ElementId parameterId = field.ParameterId;
            if (parameterId == null || parameterId == ElementId.InvalidElementId) return null;
            Element type = null;
            try
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId) type = doc.GetElement(typeId);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                type = null;
            }
            try
            {
                if (parameterId.Value < 0)
                {
                    var builtIn = (BuiltInParameter)(int)parameterId.Value;
                    return element.get_Parameter(builtIn) ?? type?.get_Parameter(builtIn);
                }
                if (doc.GetElement(parameterId) is ParameterElement parameterElement)
                {
                    Definition definition = parameterElement.GetDefinition();
                    return element.get_Parameter(definition) ?? type?.get_Parameter(definition);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
            return null;
        }

        private static StorageType StorageOf(Document doc, ViewSchedule schedule, ScheduleField field, ForgeTypeId spec)
        {
            foreach (Element element in Sample(doc, schedule, 50))
            {
                Parameter parameter = ParameterOf(doc, element, field);
                if (parameter != null) return parameter.StorageType;
            }
            try
            {
                if (spec != null && !spec.Empty())
                {
                    if (spec == SpecTypeId.Boolean.YesNo || spec == SpecTypeId.Int.Integer) return StorageType.Integer;
                    if (UnitUtils.IsMeasurableSpec(spec) || spec == SpecTypeId.Number) return StorageType.Double;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Unknown spec.
            }
            if (field.ParameterId != null && (field.ParameterId.Value == (int)BuiltInParameter.SCHEDULE_LEVEL_PARAM || field.ParameterId.Value == (int)BuiltInParameter.LEVEL_PARAM ||
                                              field.ParameterId.Value == (int)BuiltInParameter.FAMILY_LEVEL_PARAM))
            {
                return StorageType.ElementId;
            }
            return StorageType.String;
        }

        private static ForgeTypeId SafeSpec(ScheduleField field)
        {
            try
            {
                return field.GetSpecTypeId();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------------------------------------------

        public static Dictionary<string, object> ScheduleData(Document doc, ViewSchedule schedule)
        {
            ScheduleDefinition definition = schedule.Definition;
            return new Dictionary<string, object>
            {
                ["id"] = schedule.Id.Value,
                ["name"] = schedule.Name,
                ["category"] = CategoryLabel(doc, definition.CategoryId),
                ["kind"] = KindOf(schedule),
                ["itemized"] = definition.IsItemized,
                ["totals"] = definition.ShowGrandTotal,
                ["fields"] = Fields(schedule).Select(f => FieldName(f)).Cast<object>().ToList()
            };
        }

        public static string Count(int n, string noun)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "s");
        }
    }
}
