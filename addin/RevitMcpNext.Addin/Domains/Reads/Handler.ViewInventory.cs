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
        private LegacyResponse HandleGetViews(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_views.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            int limit = Math.Min(MaxViewLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includeCropBox = GetBool(payload, "includeCropBox", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeViewFields(GetStringList(payload, "fields"), preset, includeCropBox, warnings);

            List<View> materialized = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !(view is ViewSheet))
                .Where(view => MatchesViewFilter(view, filter))
                .OrderBy(view => view.ViewType.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(SafeElementName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(view => GetElementIdValue(view.Id))
                .ToList();

            int totalCount = materialized.Count;
            List<View> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(view => BuildViewItem(document, view, fields, includeCropBox)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["scope"] = "views",
                ["source"] = "revit-addin"
            };

            if (includeTotalCount) data["totalCount"] = totalCount;
            if (offset + page.Count < totalCount) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

            return Success(
                request,
                data,
                sw,
                warnings,
                new LegacyMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                },
                generation: generation);
        }

        private LegacyResponse HandleGetSheets(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_sheets.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            int limit = Math.Min(MaxSheetLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includePlacedViews = GetBool(payload, "includePlacedViews", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeSheetFields(GetStringList(payload, "fields"), preset, includePlacedViews, warnings);

            List<ViewSheet> materialized = new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(sheet => MatchesSheetFilter(document, sheet, filter))
                .OrderBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(SafeElementName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(sheet => GetElementIdValue(sheet.Id))
                .ToList();

            int totalCount = materialized.Count;
            List<ViewSheet> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(sheet => BuildSheetItem(document, sheet, fields, includePlacedViews)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["scope"] = "sheets",
                ["source"] = "revit-addin"
            };

            if (includeTotalCount) data["totalCount"] = totalCount;
            if (offset + page.Count < totalCount) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

            return Success(
                request,
                data,
                sw,
                warnings,
                new LegacyMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                },
                generation: generation);
        }

        private LegacyResponse HandleGetSchedules(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_schedules.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            int limit = Math.Min(MaxScheduleLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includeFields = GetBool(payload, "includeFields", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeScheduleFields(GetStringList(payload, "fields"), preset, includeFields, warnings);

            List<ViewSchedule> materialized = new FilteredElementCollector(document)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(schedule => MatchesScheduleFilter(document, schedule, filter))
                .OrderBy(schedule => SafeElementName(schedule), StringComparer.OrdinalIgnoreCase)
                .ThenBy(schedule => GetElementIdValue(schedule.Id))
                .ToList();

            int totalCount = materialized.Count;
            List<ViewSchedule> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(schedule => BuildScheduleItem(document, schedule, fields, includeFields)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["scope"] = "schedules",
                ["source"] = "revit-addin"
            };

            if (includeTotalCount) data["totalCount"] = totalCount;
            if (offset + page.Count < totalCount) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

            return Success(
                request,
                data,
                sw,
                warnings,
                new LegacyMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                },
                generation: generation);
        }

        private LegacyResponse HandleGetScheduleFields(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_schedule_fields.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            string scheduleId = GetString(payload, "scheduleId");
            string category = GetString(payload, "category");
            string nameContains = GetString(payload, "nameContains");
            int limit = Math.Min(MaxScheduleFieldLimit, Math.Max(1, GetInt(payload, "limit") ?? 100));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includeExistingFields = GetBool(payload, "includeExistingFields", true);
            bool includeAvailableFields = GetBool(payload, "includeAvailableFields", true);

            ViewSchedule schedule = null;
            ElementId categoryId = ElementId.InvalidElementId;
            if (!string.IsNullOrWhiteSpace(scheduleId))
            {
                schedule = ResolveElement(document, scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    return Failure(request, "SCHEDULE_NOT_FOUND", "Schedule " + scheduleId + " was not found.", sw);
                }

                categoryId = GetScheduleCategoryId(schedule);
            }
            else if (!string.IsNullOrWhiteSpace(category))
            {
                string categoryError = TryResolveScheduleCategoryId(category, out categoryId);
                if (!string.IsNullOrWhiteSpace(categoryError))
                {
                    return Failure(request, "SCHEDULE_CATEGORY_INVALID", categoryError, sw);
                }
            }
            else
            {
                return Failure(request, "SCHEDULE_TARGET_REQUIRED", "Pass scheduleId for an existing schedule or category for new schedule field planning.", sw);
            }

            List<Dictionary<string, object>> existingFields = schedule != null && includeExistingFields
                ? GetExistingScheduleFields(schedule)
                : new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> availableFields = includeAvailableFields
                ? GetSchedulableFieldSummaries(document, schedule, categoryId)
                : new List<Dictionary<string, object>>();

            if (!string.IsNullOrWhiteSpace(nameContains))
            {
                existingFields = existingFields
                    .Where(field => ContainsIgnoreCase(GetString(field, "name"), nameContains) || ContainsIgnoreCase(GetString(field, "heading"), nameContains))
                    .ToList();
                availableFields = availableFields
                    .Where(field => ContainsIgnoreCase(GetString(field, "name"), nameContains))
                    .ToList();
            }

            List<Dictionary<string, object>> pagedAvailable = availableFields.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            int totalCount = includeAvailableFields ? availableFields.Count : existingFields.Count;
            int returnedCount = includeAvailableFields ? pagedAvailable.Count : existingFields.Count;
            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["returnedCount"] = returnedCount,
                ["limit"] = limit,
                ["truncated"] = includeAvailableFields && offset + pagedAvailable.Count < availableFields.Count,
                ["scope"] = schedule == null ? "category:" + category : "schedule:" + ToElementIdString(schedule.Id),
                ["source"] = "revit-addin"
            };

            if (schedule != null) data["schedule"] = BuildScheduleSummary(document, schedule, includeFields: includeExistingFields);
            if (IsScheduleCategoryId(categoryId)) data["category"] = BuildScheduleCategorySummary(document, categoryId);
            if (includeExistingFields) data["existingFields"] = existingFields.ToArray();
            if (includeAvailableFields) data["availableFields"] = pagedAvailable.ToArray();
            if (includeTotalCount) data["totalCount"] = totalCount;
            if (includeAvailableFields && offset + pagedAvailable.Count < availableFields.Count)
            {
                data["cursor"] = (offset + pagedAvailable.Count).ToString(CultureInfo.InvariantCulture);
            }

            return Success(
                request,
                data,
                sw,
                warnings,
                new LegacyMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = returnedCount,
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                },
                generation: generation);
        }

        private static bool MatchesViewFilter(View view, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> viewIds = GetStringList(filter, "viewIds");
            if (viewIds.Count > 0 && !viewIds.Contains(ToElementIdString(view.Id), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> uniqueIds = GetStringList(filter, "uniqueIds");
            if (uniqueIds.Count > 0 && !uniqueIds.Contains(view.UniqueId, StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> viewTypes = GetStringList(filter, "viewTypes");
            if (viewTypes.Count > 0 && !viewTypes.Contains(view.ViewType.ToString(), StringComparer.OrdinalIgnoreCase)) return false;

            string nameContains = GetString(filter, "nameContains");
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                (SafeElementName(view) ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            bool? isTemplate = GetNullableBool(filter, "isTemplate");
            if (isTemplate.HasValue && view.IsTemplate != isTemplate.Value) return false;

            bool? isGraphical = GetNullableBool(filter, "isGraphical");
            if (isGraphical.HasValue && IsGraphicalView(view) != isGraphical.Value) return false;

            bool? canBePrinted = GetNullableBool(filter, "canBePrinted");
            if (canBePrinted.HasValue && SafeCanBePrinted(view) != canBePrinted.Value) return false;

            return true;
        }

        private static bool MatchesSheetFilter(Document document, ViewSheet sheet, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> sheetIds = GetStringList(filter, "sheetIds");
            if (sheetIds.Count > 0 && !sheetIds.Contains(ToElementIdString(sheet.Id), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> uniqueIds = GetStringList(filter, "uniqueIds");
            if (uniqueIds.Count > 0 && !uniqueIds.Contains(sheet.UniqueId, StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> numbers = GetStringList(filter, "numbers");
            if (numbers.Count > 0 && !numbers.Contains(sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)) return false;

            string numberContains = GetString(filter, "numberContains");
            if (!string.IsNullOrWhiteSpace(numberContains) &&
                (sheet.SheetNumber ?? string.Empty).IndexOf(numberContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string nameContains = GetString(filter, "nameContains");
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                (SafeElementName(sheet) ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            IReadOnlyList<string> titleBlockIds = GetStringList(filter, "titleBlockIds");
            if (titleBlockIds.Count > 0)
            {
                string[] sheetTitleBlockIds = GetSheetTitleBlockIds(document, sheet).ToArray();
                if (!titleBlockIds.Any(id => sheetTitleBlockIds.Contains(id, StringComparer.OrdinalIgnoreCase))) return false;
            }

            return true;
        }

        private static bool MatchesScheduleFilter(Document document, ViewSchedule schedule, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> scheduleIds = GetStringList(filter, "scheduleIds");
            if (scheduleIds.Count > 0 && !scheduleIds.Contains(ToElementIdString(schedule.Id), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> uniqueIds = GetStringList(filter, "uniqueIds");
            if (uniqueIds.Count > 0 && !uniqueIds.Contains(schedule.UniqueId, StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> categories = GetStringList(filter, "categories");
            if (categories.Count > 0)
            {
                Dictionary<string, object> category = BuildScheduleCategorySummary(document, GetScheduleCategoryId(schedule));
                string categoryName = GetString(category, "name");
                string builtInCategory = GetString(category, "builtInCategory");
                if (!categories.Any(candidate =>
                    string.Equals(candidate, categoryName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate, builtInCategory, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            string nameContains = GetString(filter, "nameContains");
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                (SafeElementName(schedule) ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            bool? isTemplate = GetNullableBool(filter, "isTemplate");
            if (isTemplate.HasValue && schedule.IsTemplate != isTemplate.Value) return false;

            return true;
        }

        private static Dictionary<string, object> BuildViewItem(Document document, View view, IReadOnlyList<string> fields, bool includeCropBox)
        {
            Dictionary<string, object> full = BuildViewInfo(document, view, includeCropBox);
            var item = new Dictionary<string, object> { ["id"] = ToElementIdString(view.Id) };

            foreach (string field in fields)
            {
                if (string.Equals(field, "id", StringComparison.OrdinalIgnoreCase)) continue;
                if (full.TryGetValue(field, out object value))
                {
                    item[field] = value;
                }
            }

            return item;
        }

        private static Dictionary<string, object> BuildSheetItem(Document document, ViewSheet sheet, IReadOnlyList<string> fields, bool includePlacedViews)
        {
            var item = new Dictionary<string, object> { ["id"] = ToElementIdString(sheet.Id) };

            foreach (string field in fields)
            {
                switch (field)
                {
                    case "id":
                        break;
                    case "uniqueId":
                        item["uniqueId"] = sheet.UniqueId;
                        break;
                    case "sheetNumber":
                        item["sheetNumber"] = sheet.SheetNumber;
                        break;
                    case "name":
                        item["name"] = SafeElementName(sheet);
                        break;
                    case "titleBlockIds":
                        item["titleBlockIds"] = GetSheetTitleBlockIds(document, sheet).ToArray();
                        break;
                    case "placedViews":
                        if (includePlacedViews) item["placedViews"] = GetSheetPlacedViews(document, sheet).ToArray();
                        break;
                }
            }

            return item;
        }

        private static Dictionary<string, object> BuildScheduleItem(Document document, ViewSchedule schedule, IReadOnlyList<string> fields, bool includeFields)
        {
            Dictionary<string, object> full = BuildScheduleSummary(document, schedule, includeFields);
            var item = new Dictionary<string, object> { ["id"] = ToElementIdString(schedule.Id) };

            foreach (string field in fields)
            {
                if (string.Equals(field, "id", StringComparison.OrdinalIgnoreCase)) continue;
                if (full.TryGetValue(field, out object value))
                {
                    item[field] = value;
                }
            }

            return item;
        }

        private static IEnumerable<string> GetSheetTitleBlockIds(Document document, ViewSheet sheet)
        {
            try
            {
                return new FilteredElementCollector(document, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Where(IsValidElementId)
                    .Select(ToElementIdString)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<Dictionary<string, object>> GetSheetPlacedViews(Document document, ViewSheet sheet)
        {
            ICollection<ElementId> viewportIds;
            try
            {
                viewportIds = sheet.GetAllViewports();
            }
            catch
            {
                return Array.Empty<Dictionary<string, object>>();
            }

            var placedViews = new List<Dictionary<string, object>>();
            foreach (ElementId viewportId in viewportIds)
            {
                Viewport viewport = document.GetElement(viewportId) as Viewport;
                if (viewport == null) continue;
                View view = document.GetElement(viewport.ViewId) as View;
                var item = new Dictionary<string, object>
                {
                    ["viewportId"] = ToElementIdString(viewport.Id),
                    ["viewId"] = ToElementIdString(viewport.ViewId)
                };

                if (view != null)
                {
                    item["viewName"] = SafeElementName(view);
                    item["viewType"] = view.ViewType.ToString();
                }

                try
                {
                    item["center"] = PointValue(viewport.GetBoxCenter());
                }
                catch
                {
                    // Viewport center can be unavailable for unusual sheet contents.
                }

                placedViews.Add(item);
            }

            return placedViews;
        }

        private static Dictionary<string, object> BuildViewSummary(View view)
        {
            bool canBePrinted = SafeCanBePrinted(view);

            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(view.Id),
                ["uniqueId"] = view.UniqueId,
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString(),
                ["isGraphical"] = !view.IsTemplate && canBePrinted,
                ["isTemplate"] = view.IsTemplate,
                ["canBePrinted"] = canBePrinted
            };

            try
            {
                if (view.Scale > 0) summary["scale"] = view.Scale;
            }
            catch
            {
                // Some Revit view-like elements do not expose scale.
            }

            try
            {
                summary["detailLevel"] = view.DetailLevel.ToString();
            }
            catch
            {
                // Detail level is not available on every view type.
            }

            try
            {
                summary["discipline"] = view.Discipline.ToString();
            }
            catch
            {
                // Discipline is not available on every view type.
            }

            return summary;
        }

        private static Dictionary<string, object> BuildViewInfo(Document document, View view, bool includeCropBox)
        {
            Dictionary<string, object> info = BuildViewSummary(view);

            try
            {
                ElementId viewTemplateId = view.ViewTemplateId;
                if (IsValidElementId(viewTemplateId))
                {
                    info["viewTemplateId"] = ToElementIdString(viewTemplateId);
                    Element viewTemplate = document.GetElement(viewTemplateId);
                    if (viewTemplate != null) info["viewTemplateName"] = SafeElementName(viewTemplate);
                }
            }
            catch
            {
                // View templates are unavailable for some views.
            }

            Level associatedLevel = GetAssociatedLevel(view);
            if (associatedLevel != null)
            {
                info["associatedLevelId"] = ToElementIdString(associatedLevel.Id);
                info["associatedLevelName"] = associatedLevel.Name;
            }

            try
            {
                info["cropBoxActive"] = view.CropBoxActive;
            }
            catch
            {
                // Crop settings are not exposed on every view type.
            }

            try
            {
                info["cropBoxVisible"] = view.CropBoxVisible;
            }
            catch
            {
                // Crop settings are not exposed on every view type.
            }

            if (includeCropBox)
            {
                try
                {
                    BoundingBoxXYZ cropBox = view.CropBox;
                    if (cropBox != null)
                    {
                        info["cropBox"] = new Dictionary<string, object>
                        {
                            ["min"] = PointValue(cropBox.Min),
                            ["max"] = PointValue(cropBox.Max)
                        };
                    }
                }
                catch
                {
                    // Crop boxes are unavailable for non-graphical views.
                }
            }

            return info;
        }
    }
}
