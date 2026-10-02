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
        private static Dictionary<string, object> PreviewCreateSchedule(Document document, Dictionary<string, object> operation, int index)
        {
            string category = GetString(operation, "category");
            if (string.IsNullOrWhiteSpace(category)) return BlockedChange(operation, index, "create_schedule requires category.");

            string categoryError = TryResolveScheduleCategoryId(category, out ElementId categoryId);
            if (!string.IsNullOrWhiteSpace(categoryError)) return BlockedChange(operation, index, categoryError);

            string name = NormalizeOptionalText(GetString(operation, "name"));
            if (!string.IsNullOrWhiteSpace(name) && ViewNameExists(document, name))
            {
                return BlockedChange(operation, index, "A Revit view or schedule named '" + name + "' already exists.");
            }

            List<Dictionary<string, object>> fieldSpecs = GetDictionaryList(operation, "fields");
            if (fieldSpecs.Count > 32) return BlockedChange(operation, index, "create_schedule supports at most 32 initial fields.");

            string probeError = ProbeCreateSchedule(document, categoryId, fieldSpecs, out List<Dictionary<string, object>> fieldSummaries);
            if (!string.IsNullOrWhiteSpace(probeError)) return BlockedChange(operation, index, probeError);

            var after = new Dictionary<string, object>
            {
                ["category"] = BuildScheduleCategorySummary(document, categoryId),
                ["fieldCount"] = fieldSummaries.Count,
                ["fields"] = fieldSummaries.ToArray()
            };
            if (!string.IsNullOrWhiteSpace(name)) after["name"] = name;
            if (operation.ContainsKey("isItemized")) after["isItemized"] = GetBool(operation, "isItemized", true);

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["document"] = document.Title,
                    ["category"] = category,
                    ["categoryId"] = ToElementIdString(categoryId)
                },
                before: null,
                after: after);
        }

        private static Dictionary<string, object> ApplyCreateSchedule(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateSchedule(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_schedule preview failed.");
            }

            string category = GetString(operation, "category");
            string categoryError = TryResolveScheduleCategoryId(category, out ElementId categoryId);
            if (!string.IsNullOrWhiteSpace(categoryError)) throw new InvalidOperationException(categoryError);

            ViewSchedule schedule = ViewSchedule.CreateSchedule(document, categoryId);
            if (schedule == null)
            {
                throw new InvalidOperationException("Revit did not create a schedule for category " + category + ".");
            }

            string name = NormalizeOptionalText(GetString(operation, "name"));
            if (!string.IsNullOrWhiteSpace(name)) schedule.Name = name;
            if (operation.ContainsKey("isItemized")) schedule.Definition.IsItemized = GetBool(operation, "isItemized", true);

            foreach (Dictionary<string, object> fieldSpec in GetDictionaryList(operation, "fields"))
            {
                AddScheduleFieldFromSpec(document, schedule, fieldSpec);
            }

            document.Regenerate();
            return Change(operation, index, "applied",
                target: ElementTarget(schedule, null),
                before: null,
                after: BuildScheduleSummary(document, schedule, includeFields: true));
        }

        private static Dictionary<string, object> PreviewAddScheduleField(Document document, Dictionary<string, object> operation, int index)
        {
            string scheduleId = GetString(operation, "scheduleId");
            if (string.IsNullOrWhiteSpace(scheduleId)) return BlockedChange(operation, index, "add_schedule_field requires scheduleId.");

            ViewSchedule schedule = ResolveElement(document, scheduleId) as ViewSchedule;
            if (schedule == null) return BlockedChange(operation, index, "Schedule " + scheduleId + " was not found.");

            Dictionary<string, object> fieldSpec = new Dictionary<string, object>(operation, StringComparer.OrdinalIgnoreCase);
            string fieldError = TryResolveSchedulableField(document, schedule, fieldSpec, out SchedulableField schedulableField, out string fieldName);
            if (!string.IsNullOrWhiteSpace(fieldError)) return BlockedChange(operation, index, fieldError);

            if (ScheduleHasFieldNamed(schedule, fieldName))
            {
                return BlockedChange(operation, index, "Schedule " + scheduleId + " already contains field '" + fieldName + "'.");
            }

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["schedule"] = BuildScheduleSummary(document, schedule, includeFields: false),
                    ["fieldName"] = fieldName,
                    ["fieldId"] = SchedulableFieldIdString(schedulableField)
                },
                before: BuildScheduleSummary(document, schedule, includeFields: true),
                after: BuildSchedulableFieldSummary(document, schedulableField, schedule));
        }

        private static Dictionary<string, object> ApplyAddScheduleField(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewAddScheduleField(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "add_schedule_field preview failed.");
            }

            ViewSchedule schedule = ResolveElement(document, GetString(operation, "scheduleId")) as ViewSchedule;
            ScheduleField field = AddScheduleFieldFromSpec(document, schedule, new Dictionary<string, object>(operation, StringComparer.OrdinalIgnoreCase));
            document.Regenerate();

            return Change(operation, index, "applied",
                target: new Dictionary<string, object>
                {
                    ["schedule"] = BuildScheduleSummary(document, schedule, includeFields: false)
                },
                before: GetDictionary(preview, "before"),
                after: BuildScheduleFieldSummary(field, schedule, schedule.Definition.GetFieldCount() - 1));
        }

        private static Dictionary<string, object> PreviewPlaceScheduleOnSheet(Document document, Dictionary<string, object> operation, int index)
        {
            string sheetId = GetString(operation, "sheetId");
            string scheduleId = GetString(operation, "scheduleId");
            Dictionary<string, object> pointValue = GetDictionary(operation, "point");
            if (string.IsNullOrWhiteSpace(sheetId)) return BlockedChange(operation, index, "place_schedule_on_sheet requires sheetId.");
            if (string.IsNullOrWhiteSpace(scheduleId)) return BlockedChange(operation, index, "place_schedule_on_sheet requires scheduleId.");
            if (pointValue == null) return BlockedChange(operation, index, "place_schedule_on_sheet requires point.");

            ViewSheet sheet = ResolveElement(document, sheetId) as ViewSheet;
            if (sheet == null) return BlockedChange(operation, index, "Sheet " + sheetId + " was not found.");

            ViewSchedule schedule = ResolveElement(document, scheduleId) as ViewSchedule;
            if (schedule == null) return BlockedChange(operation, index, "Schedule " + scheduleId + " was not found.");
            if (schedule.IsTemplate) return BlockedChange(operation, index, "Schedule " + scheduleId + " is a template and cannot be placed on a sheet.");

            XYZ point;
            try
            {
                point = ToInternalSheetPoint(pointValue, "point");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string probeError = ProbePlaceScheduleOnSheet(document, sheet.Id, schedule.Id, point);
            if (!string.IsNullOrWhiteSpace(probeError)) return BlockedChange(operation, index, probeError);

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["sheet"] = SheetSnapshot(document, sheet),
                    ["schedule"] = BuildScheduleSummary(document, schedule, includeFields: false)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["sheetId"] = ToElementIdString(sheet.Id),
                    ["scheduleId"] = ToElementIdString(schedule.Id),
                    ["point"] = PointValue(point)
                });
        }

        private static Dictionary<string, object> ApplyPlaceScheduleOnSheet(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewPlaceScheduleOnSheet(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "place_schedule_on_sheet preview failed.");
            }

            ViewSheet sheet = ResolveElement(document, GetString(operation, "sheetId")) as ViewSheet;
            ViewSchedule schedule = ResolveElement(document, GetString(operation, "scheduleId")) as ViewSchedule;
            XYZ point = ToInternalSheetPoint(GetDictionary(operation, "point"), "point");
            ScheduleSheetInstance instance = ScheduleSheetInstance.Create(document, sheet.Id, schedule.Id, point);
            if (instance == null)
            {
                throw new InvalidOperationException("Revit did not create a schedule sheet instance.");
            }

            return Change(operation, index, "applied",
                target: ElementTarget(instance, null),
                before: null,
                after: ScheduleSheetInstanceSnapshot(instance, sheet.Id, schedule.Id, point));
        }

        private static Dictionary<string, object> ScheduleSheetInstanceSnapshot(ScheduleSheetInstance instance, ElementId sheetId, ElementId scheduleId, XYZ point)
        {
            return new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(instance.Id),
                ["uniqueId"] = instance.UniqueId,
                ["sheetId"] = ToElementIdString(sheetId),
                ["scheduleId"] = ToElementIdString(scheduleId),
                ["point"] = PointValue(point)
            };
        }

        private static Dictionary<string, object> BuildScheduleSummary(Document document, ViewSchedule schedule, bool includeFields)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(schedule.Id),
                ["uniqueId"] = schedule.UniqueId,
                ["name"] = SafeElementName(schedule),
                ["type"] = schedule.ViewType.ToString(),
                ["isTemplate"] = schedule.IsTemplate
            };

            ElementId categoryId = GetScheduleCategoryId(schedule);
            if (IsScheduleCategoryId(categoryId))
            {
                Dictionary<string, object> category = BuildScheduleCategorySummary(document, categoryId);
                summary["categoryId"] = ToElementIdString(categoryId);
                if (category.TryGetValue("name", out object name)) summary["category"] = name;
                if (category.TryGetValue("builtInCategory", out object builtInCategory)) summary["builtInCategory"] = builtInCategory;
            }

            try
            {
                summary["fieldCount"] = schedule.Definition.GetFieldCount();
                summary["isItemized"] = schedule.Definition.IsItemized;
                if (includeFields) summary["fields"] = GetExistingScheduleFields(schedule).ToArray();
            }
            catch
            {
                summary["fieldCount"] = 0;
            }

            return summary;
        }

        private static List<Dictionary<string, object>> GetExistingScheduleFields(ViewSchedule schedule)
        {
            var fields = new List<Dictionary<string, object>>();
            ScheduleDefinition definition = schedule.Definition;
            int count = definition.GetFieldCount();
            for (int index = 0; index < count; index++)
            {
                try
                {
                    fields.Add(BuildScheduleFieldSummary(definition.GetField(index), schedule, index));
                }
                catch
                {
                    // Preserve the rest of the schedule field list when Revit rejects one field.
                }
            }

            return fields;
        }

        private static Dictionary<string, object> BuildScheduleFieldSummary(ScheduleField field, ViewSchedule schedule, int fieldIndex)
        {
            string name = GetScheduleFieldName(field);
            var summary = new Dictionary<string, object>
            {
                ["id"] = ScheduleFieldIdString(field),
                ["fieldIndex"] = fieldIndex,
                ["name"] = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(field.ColumnHeading)) summary["heading"] = field.ColumnHeading;
            }
            catch
            {
                // Column heading can be unavailable for some calculated/internal fields.
            }

            try { summary["fieldType"] = field.FieldType.ToString(); } catch { }

            ElementId parameterId = SafeParameterId(field);
            if (IsScheduleCategoryId(parameterId) || IsValidElementId(parameterId)) summary["parameterId"] = ToElementIdString(parameterId);

            try
            {
                summary["isHidden"] = field.IsHidden;
            }
            catch
            {
                // Hidden state is not exposed on every schedule field variant.
            }

            try { summary["canTotal"] = field.CanTotal(); } catch { }

            return summary;
        }

        private static Dictionary<string, object> BuildSchedulableFieldSummary(Document document, SchedulableField field, ViewSchedule schedule)
        {
            string name = GetSchedulableFieldName(document, field);
            var summary = new Dictionary<string, object>
            {
                ["fieldId"] = SchedulableFieldIdString(field),
                ["name"] = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name
            };

            try { summary["fieldType"] = field.FieldType.ToString(); } catch { }

            ElementId parameterId = SafeParameterId(field);
            if (IsScheduleCategoryId(parameterId) || IsValidElementId(parameterId)) summary["parameterId"] = ToElementIdString(parameterId);

            if (schedule != null) summary["alreadyInSchedule"] = ScheduleHasFieldNamed(schedule, name);
            return summary;
        }

        private static string TryResolveScheduleCategoryId(string category, out ElementId categoryId)
        {
            categoryId = ElementId.InvalidElementId;
            string trimmed = (category ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return "Schedule category is required.";

            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long rawId))
            {
                categoryId = CreateElementId(rawId.ToString(CultureInfo.InvariantCulture));
                return null;
            }

            if (!TryParseBuiltInCategory(trimmed, out BuiltInCategory builtInCategory))
            {
                return "Schedule category '" + category + "' is not a recognized BuiltInCategory. Use values such as OST_Walls, OST_Doors, OST_Rooms, or OST_Floors.";
            }

            categoryId = CreateCategoryElementId(builtInCategory);
            return null;
        }

        private static ElementId CreateCategoryElementId(BuiltInCategory category)
        {
            try
            {
                return (ElementId)Activator.CreateInstance(typeof(ElementId), category);
            }
            catch
            {
                return CreateElementId(Convert.ToInt64(category, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }
        }

        private static bool IsScheduleCategoryId(ElementId id)
        {
            return id != null && GetElementIdValue(id) != GetElementIdValue(ElementId.InvalidElementId);
        }

        private static ElementId GetScheduleCategoryId(ViewSchedule schedule)
        {
            ElementId categoryId = schedule?.Definition?.CategoryId ?? ElementId.InvalidElementId;
            return IsScheduleCategoryId(categoryId) ? categoryId : ElementId.InvalidElementId;
        }

        private static Dictionary<string, object> BuildScheduleCategorySummary(Document document, ElementId categoryId)
        {
            var summary = new Dictionary<string, object>();
            if (!IsScheduleCategoryId(categoryId)) return summary;

            summary["id"] = ToElementIdString(categoryId);
            try
            {
                BuiltInCategory builtInCategory = (BuiltInCategory)GetElementIdValue(categoryId);
                summary["builtInCategory"] = builtInCategory.ToString();
            }
            catch
            {
                // Custom or future category ids may not map to a BuiltInCategory enum value.
            }

            try
            {
                foreach (Category category in document.Settings.Categories)
                {
                    if (category != null && GetElementIdValue(category.Id) == GetElementIdValue(categoryId))
                    {
                        summary["name"] = category.Name;
                        break;
                    }
                }
            }
            catch
            {
                // Category display names are best-effort metadata.
            }

            return summary;
        }

        private static string ProbeCreateSchedule(
            Document document,
            ElementId categoryId,
            List<Dictionary<string, object>> fieldSpecs,
            out List<Dictionary<string, object>> fieldSummaries)
        {
            fieldSummaries = new List<Dictionary<string, object>>();
            using (var transaction = new Transaction(document, "Revit MCP preview create_schedule"))
            {
                try
                {
                    transaction.Start();
                    ViewSchedule schedule = ViewSchedule.CreateSchedule(document, categoryId);
                    if (schedule == null)
                    {
                        transaction.RollBack();
                        return "Revit did not create a schedule for category " + ToElementIdString(categoryId) + ".";
                    }

                    foreach (Dictionary<string, object> fieldSpec in fieldSpecs)
                    {
                        ScheduleField field = AddScheduleFieldFromSpec(document, schedule, fieldSpec);
                        fieldSummaries.Add(BuildScheduleFieldSummary(field, schedule, schedule.Definition.GetFieldCount() - 1));
                    }

                    transaction.RollBack();
                    return null;
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted()) transaction.RollBack();
                    return "Revit could not preview create_schedule: " + ex.Message;
                }
            }
        }

        private static string ProbePlaceScheduleOnSheet(Document document, ElementId sheetId, ElementId scheduleId, XYZ point)
        {
            using (var transaction = new Transaction(document, "Revit MCP preview place_schedule_on_sheet"))
            {
                try
                {
                    transaction.Start();
                    ScheduleSheetInstance instance = ScheduleSheetInstance.Create(document, sheetId, scheduleId, point);
                    if (instance == null)
                    {
                        transaction.RollBack();
                        return "Revit did not create a schedule sheet instance.";
                    }
                    transaction.RollBack();
                    return null;
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted()) transaction.RollBack();
                    return "Revit could not preview place_schedule_on_sheet: " + ex.Message;
                }
            }
        }

        private static List<Dictionary<string, object>> GetSchedulableFieldSummaries(Document document, ViewSchedule schedule, ElementId categoryId)
        {
            if (schedule != null)
            {
                return schedule.Definition
                    .GetSchedulableFields()
                    .Select(field => BuildSchedulableFieldSummary(document, field, schedule))
                    .OrderBy(field => GetString(field, "name"), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var result = new List<Dictionary<string, object>>();
            using (var transaction = new Transaction(document, "Revit MCP preview schedule fields"))
            {
                try
                {
                    transaction.Start();
                    ViewSchedule temp = ViewSchedule.CreateSchedule(document, categoryId);
                    result = temp.Definition
                        .GetSchedulableFields()
                        .Select(field => BuildSchedulableFieldSummary(document, field, null))
                        .OrderBy(field => GetString(field, "name"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    transaction.RollBack();
                }
                catch
                {
                    if (transaction.HasStarted()) transaction.RollBack();
                    throw;
                }
            }

            return result;
        }

        private static ScheduleField AddScheduleFieldFromSpec(Document document, ViewSchedule schedule, Dictionary<string, object> fieldSpec)
        {
            string error = TryResolveSchedulableField(document, schedule, fieldSpec, out SchedulableField schedulableField, out string fieldName);
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);

            ScheduleField field = schedule.Definition.AddField(schedulableField);
            string heading = NormalizeOptionalText(GetString(fieldSpec, "heading"));
            if (!string.IsNullOrWhiteSpace(heading)) field.ColumnHeading = heading;
            if (fieldSpec.ContainsKey("hidden")) field.IsHidden = GetBool(fieldSpec, "hidden", false);
            return field;
        }

        private static string TryResolveSchedulableField(
            Document document,
            ViewSchedule schedule,
            Dictionary<string, object> fieldSpec,
            out SchedulableField schedulableField,
            out string fieldName)
        {
            schedulableField = null;
            fieldName = null;
            if (schedule == null) return "A target schedule is required.";

            string requestedName = NormalizeOptionalText(GetString(fieldSpec, "fieldName"));
            string requestedId = NormalizeOptionalText(GetString(fieldSpec, "fieldId"));
            if (string.IsNullOrWhiteSpace(requestedName) && string.IsNullOrWhiteSpace(requestedId))
            {
                return "Schedule field requires fieldName or fieldId.";
            }

            foreach (SchedulableField candidate in schedule.Definition.GetSchedulableFields())
            {
                string candidateName = GetSchedulableFieldName(document, candidate);
                string candidateId = SchedulableFieldIdString(candidate);
                if ((!string.IsNullOrWhiteSpace(requestedId) && string.Equals(requestedId, candidateId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(requestedName) && string.Equals(requestedName, candidateName, StringComparison.OrdinalIgnoreCase)))
                {
                    schedulableField = candidate;
                    fieldName = candidateName;
                    return null;
                }
            }

            return "Schedulable field was not found for schedule " + ToElementIdString(schedule.Id) + ": " +
                   (string.IsNullOrWhiteSpace(requestedName) ? requestedId : requestedName) + ".";
        }

        private static bool ScheduleHasFieldNamed(ViewSchedule schedule, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName)) return false;
            return GetExistingScheduleFields(schedule)
                .Any(field => string.Equals(GetString(field, "name"), fieldName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetScheduleFieldName(ScheduleField field)
        {
            try
            {
                string name = field.GetName();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch
            {
                // Use heading fallback below.
            }

            try
            {
                return field.ColumnHeading ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetSchedulableFieldName(Document document, SchedulableField field)
        {
            try
            {
                return field.GetName(document) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ScheduleFieldIdString(ScheduleField field)
        {
            try
            {
                return field.FieldId?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SchedulableFieldIdString(SchedulableField field)
        {
            ElementId parameterId = SafeParameterId(field);
            if (IsScheduleCategoryId(parameterId) || IsValidElementId(parameterId)) return ToElementIdString(parameterId);
            return field.ToString();
        }

        private static ElementId SafeParameterId(ScheduleField field)
        {
            try { return field?.ParameterId ?? ElementId.InvalidElementId; } catch { return ElementId.InvalidElementId; }
        }

        private static ElementId SafeParameterId(SchedulableField field)
        {
            try { return field?.ParameterId ?? ElementId.InvalidElementId; } catch { return ElementId.InvalidElementId; }
        }
    }
}
