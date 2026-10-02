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
        private static Dictionary<string, object> GetOrCreateFields(Dictionary<string, object> item)
        {
            if (!item.TryGetValue("fields", out object existing) || !(existing is Dictionary<string, object> fields))
            {
                fields = new Dictionary<string, object>();
                item["fields"] = fields;
            }

            return fields;
        }

        private static string[] NormalizeFields(IReadOnlyList<string> requested, string preset, List<LegacyWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "schedule":
                    defaults = new[] { "id", "category", "name", "typeId", "levelId" };
                    break;
                case "geometrySummary":
                    defaults = GeometrySummaryFields();
                    break;
                default:
                    defaults = SummaryFields();
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedField(field))
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "UNSUPPORTED_FIELD",
                        Message = "Field '" + field + "' is not supported by the current query projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static string[] NormalizeRoomFields(IReadOnlyList<string> requested, string preset, List<LegacyWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "schedule":
                    defaults = new[] { "id", "number", "name", "levelId", "levelName", "area", "volume", "department" };
                    break;
                default:
                    defaults = new[] { "id", "uniqueId", "number", "name", "levelId", "area" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedRoomField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedRoomField(field))
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "UNSUPPORTED_ROOM_FIELD",
                        Message = "Room field '" + field + "' is not supported by the current room projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedRoomField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "number":
                case "name":
                case "levelId":
                case "levelName":
                case "phaseId":
                case "phaseName":
                case "area":
                case "volume":
                case "perimeter":
                case "location":
                case "isPlaced":
                case "isEnclosed":
                case "department":
                    return true;
                default:
                    return field.StartsWith("param:", StringComparison.OrdinalIgnoreCase) && field.Length > "param:".Length;
            }
        }

        private static string[] SummaryFields()
        {
            return new[] { "id", "uniqueId", "category", "class", "name", "typeId", "levelId" };
        }

        private static string[] GeometrySummaryFields()
        {
            return new[] { "id", "uniqueId", "category", "class", "name", "typeId", "levelId", "location", "bounds" };
        }

        private static bool IsSupportedField(string field)
        {
            switch (field)
            {
                case "id":
                case "ownerViewId":
                    case "viewSpecific":
                case "uniqueId":
                case "category":
                case "class":
                case "name":
                case "typeId":
                case "levelId":
                case "location":
                case "bounds":
                    return true;
                default:
                    return field.StartsWith("param:", StringComparison.OrdinalIgnoreCase) && field.Length > "param:".Length;
            }
        }

        private static string[] NormalizeWarningFields(IReadOnlyList<string> requested, string preset, List<LegacyWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "elements":
                    defaults = new[] { "id", "severity", "description", "failingElementIds", "additionalElementIds", "failingElementCount", "additionalElementCount" };
                    break;
                case "full":
                    defaults = new[]
                    {
                        "id",
                        "severity",
                        "description",
                        "failureDefinitionId",
                        "defaultResolution",
                        "failingElementIds",
                        "additionalElementIds",
                        "failingElementCount",
                        "additionalElementCount"
                    };
                    break;
                default:
                    defaults = new[] { "id", "severity", "description", "failingElementCount", "additionalElementCount" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedWarningField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedWarningField(field))
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "UNSUPPORTED_WARNING_FIELD",
                        Message = "Warning field '" + field + "' is not supported by the current warning projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedWarningField(string field)
        {
            switch (field)
            {
                case "id":
                case "severity":
                case "description":
                case "failureDefinitionId":
                case "defaultResolution":
                case "failingElementIds":
                case "additionalElementIds":
                case "failingElementCount":
                case "additionalElementCount":
                case "failingElementIdsTruncated":
                case "additionalElementIdsTruncated":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] NormalizeViewFields(IReadOnlyList<string> requested, string preset, bool includeCropBox, List<LegacyWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "sheetPlacement":
                    defaults = new[] { "id", "uniqueId", "name", "type", "isGraphical", "isTemplate", "canBePrinted", "viewTemplateId" };
                    break;
                default:
                    defaults = new[] { "id", "uniqueId", "name", "type", "isGraphical", "isTemplate", "canBePrinted", "scale", "detailLevel", "discipline" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (string.Equals(field, "cropBox", StringComparison.OrdinalIgnoreCase) && !includeCropBox)
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "CROP_BOX_NOT_INCLUDED",
                        Message = "Field cropBox requires includeCropBox=true."
                    });
                    continue;
                }

                if (IsSupportedViewField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedViewField(field))
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "UNSUPPORTED_VIEW_FIELD",
                        Message = "View field '" + field + "' is not supported by the current view projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedViewField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "name":
                case "type":
                case "isGraphical":
                case "isTemplate":
                case "canBePrinted":
                case "scale":
                case "detailLevel":
                case "discipline":
                case "viewTemplateId":
                case "viewTemplateName":
                case "associatedLevelId":
                case "associatedLevelName":
                case "cropBoxActive":
                case "cropBoxVisible":
                case "cropBox":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] NormalizeSheetFields(IReadOnlyList<string> requested, string preset, bool includePlacedViews, List<LegacyWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "placement":
                    defaults = new[] { "id", "uniqueId", "sheetNumber", "name", "titleBlockIds", "placedViews" };
                    break;
                default:
                    defaults = new[] { "id", "uniqueId", "sheetNumber", "name", "titleBlockIds" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (string.Equals(field, "placedViews", StringComparison.OrdinalIgnoreCase) && !includePlacedViews)
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "PLACED_VIEWS_NOT_INCLUDED",
                        Message = "Field placedViews requires includePlacedViews=true."
                    });
                    continue;
                }

                if (IsSupportedSheetField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedSheetField(field))
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "UNSUPPORTED_SHEET_FIELD",
                        Message = "Sheet field '" + field + "' is not supported by the current sheet projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedSheetField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "sheetNumber":
                case "name":
                case "titleBlockIds":
                case "placedViews":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] NormalizeScheduleFields(IReadOnlyList<string> requested, string preset, bool includeFields, List<LegacyWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "fields":
                    defaults = new[] { "id", "uniqueId", "name", "category", "builtInCategory", "fieldCount", "isItemized", "fields" };
                    break;
                default:
                    defaults = new[] { "id", "uniqueId", "name", "category", "builtInCategory", "fieldCount", "isItemized" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (string.Equals(field, "fields", StringComparison.OrdinalIgnoreCase) && !includeFields)
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "SCHEDULE_FIELDS_NOT_INCLUDED",
                        Message = "Field fields requires includeFields=true or preset=fields."
                    });
                    continue;
                }

                if (IsSupportedScheduleField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedScheduleField(field))
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "UNSUPPORTED_SCHEDULE_FIELD",
                        Message = "Schedule field '" + field + "' is not supported by the current schedule projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedScheduleField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "name":
                case "type":
                case "categoryId":
                case "category":
                case "builtInCategory":
                case "fieldCount":
                case "isItemized":
                case "isTemplate":
                case "fields":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] NormalizeCatalogFields(IReadOnlyList<string> requested, string preset, List<LegacyWarning> warnings)
        {
            string[] defaults;
            switch (preset)
            {
                case "idOnly":
                    defaults = new[] { "id" };
                    break;
                case "typeChange":
                    defaults = new[] { "id", "class", "category", "builtInCategory", "name", "familyName", "isCurrentType", "validForTarget" };
                    break;
                case "placement":
                    defaults = new[] { "id", "class", "category", "builtInCategory", "name", "familyName", "familyId", "isActive", "placementType" };
                    break;
                case "sheet":
                    defaults = new[] { "id", "class", "category", "builtInCategory", "name", "familyName", "familyId", "isActive" };
                    break;
                case "annotation":
                    defaults = new[] { "id", "class", "category", "builtInCategory", "name", "familyName", "familyId" };
                    break;
                default:
                    defaults = new[] { "id", "class", "category", "name", "familyName" };
                    break;
            }

            IReadOnlyList<string> source = requested.Count == 0 ? defaults : requested;
            var normalized = new List<string>();
            foreach (string rawField in source)
            {
                string field = rawField?.Trim();
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (IsSupportedCatalogField(field) && !normalized.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(field);
                }
                else if (!IsSupportedCatalogField(field))
                {
                    warnings.Add(new LegacyWarning
                    {
                        Code = "UNSUPPORTED_CATALOG_FIELD",
                        Message = "Catalog field '" + field + "' is not supported by the current catalog projection."
                    });
                }
            }

            return normalized.Count == 0 ? new[] { "id" } : normalized.ToArray();
        }

        private static bool IsSupportedCatalogField(string field)
        {
            switch (field)
            {
                case "id":
                case "uniqueId":
                case "class":
                case "category":
                case "builtInCategory":
                case "name":
                case "familyName":
                case "familyId":
                case "isCurrentType":
                case "validForTarget":
                case "isActive":
                case "placementType":
                case "dimensionStyle":
                case "viewFamily":
                    return true;
                default:
                    return field.StartsWith("param:", StringComparison.OrdinalIgnoreCase) && field.Length > "param:".Length;
            }
        }
    }
}
