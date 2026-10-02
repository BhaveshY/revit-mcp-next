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
        private LegacyResponse HandleDescribeParameters(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.describe_parameters.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = GetDictionary(payload, "filter") ?? new Dictionary<string, object>();
            string preset = NormalizeParameterDescribePreset(GetString(payload, "preset"));
            int limit = Math.Min(MaxParameterElementLimit, Math.Max(1, GetInt(payload, "limit") ?? DefaultParameterElementLimit(preset)));
            int parameterLimit = Math.Min(MaxParameterLimit, Math.Max(1, GetInt(payload, "parameterLimit") ?? DefaultParameterLimit(preset)));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includeTypeParameters = GetBool(payload, "includeTypeParameters", DefaultIncludeTypeParameters(preset));
            bool includeReadOnly = GetBool(payload, "includeReadOnly", DefaultIncludeReadOnlyParameters(preset));
            bool includeValues = GetBool(payload, "includeValues", DefaultIncludeParameterValues(preset));
            string nameContains = GetString(payload, "nameContains");

            string scope;
            IEnumerable<Element> elements = CreateFilteredElements(app, document, filter, warnings, out scope);
            PageResult<Element> pageResult = PageItems(elements, offset, limit, includeTotalCount);
            List<Element> page = pageResult.Items;
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(element => BuildParameterTarget(
                    document,
                    element,
                    includeTypeParameters,
                    includeReadOnly,
                    includeValues,
                    nameContains,
                    parameterLimit)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = pageResult.Truncated,
                ["parameterLimit"] = parameterLimit,
                ["preset"] = preset,
                ["scope"] = scope,
                ["source"] = "revit-addin"
            };

            if (includeTotalCount && pageResult.TotalCount.HasValue) data["totalCount"] = pageResult.TotalCount.Value;
            if (pageResult.Truncated) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

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
                    TotalCount = pageResult.TotalCount
                },
                generation: generation);
        }

        private static Dictionary<string, object> BuildParameterTarget(
            Document document,
            Element element,
            bool includeTypeParameters,
            bool includeReadOnly,
            bool includeValues,
            string nameContains,
            int parameterLimit)
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element)
            };

            if (element.Category != null) item["category"] = element.Category.Name;

            ElementId typeId = element.GetTypeId();
            Element typeElement = null;
            if (IsValidElementId(typeId))
            {
                item["typeId"] = ToElementIdString(typeId);
                typeElement = document.GetElement(typeId);
                if (typeElement != null) item["typeName"] = SafeElementName(typeElement);
            }

            var parameters = new List<Dictionary<string, object>>();
            AddParameterSummaries(parameters, element, "instance", includeReadOnly, includeValues, nameContains);

            if (includeTypeParameters && typeElement != null && !string.Equals(ToElementIdString(typeElement.Id), ToElementIdString(element.Id), StringComparison.Ordinal))
            {
                AddParameterSummaries(parameters, typeElement, "type", includeReadOnly, includeValues, nameContains);
            }

            List<Dictionary<string, object>> ordered = parameters
                .OrderBy(parameter => Convert.ToString(parameter["source"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                .ThenBy(parameter => Convert.ToString(parameter["name"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                .ToList();

            item["parameters"] = ordered.Take(parameterLimit).ToArray();
            item["parameterCount"] = ordered.Count;
            item["truncated"] = ordered.Count > parameterLimit;
            return item;
        }

        private static string NormalizeParameterDescribePreset(string preset)
        {
            if (string.Equals(preset, "full", StringComparison.OrdinalIgnoreCase)) return "full";
            if (string.Equals(preset, "namesOnly", StringComparison.OrdinalIgnoreCase)) return "namesOnly";
            return "writableEdit";
        }

        private static int DefaultParameterElementLimit(string preset)
        {
            return string.Equals(preset, "full", StringComparison.Ordinal) ? 20 : 10;
        }

        private static int DefaultParameterLimit(string preset)
        {
            if (string.Equals(preset, "full", StringComparison.Ordinal)) return 80;
            if (string.Equals(preset, "namesOnly", StringComparison.Ordinal)) return 120;
            return 40;
        }

        private static bool DefaultIncludeTypeParameters(string preset)
        {
            return string.Equals(preset, "full", StringComparison.Ordinal) ||
                string.Equals(preset, "namesOnly", StringComparison.Ordinal);
        }

        private static bool DefaultIncludeReadOnlyParameters(string preset)
        {
            return string.Equals(preset, "full", StringComparison.Ordinal) ||
                string.Equals(preset, "namesOnly", StringComparison.Ordinal);
        }

        private static bool DefaultIncludeParameterValues(string preset)
        {
            return string.Equals(preset, "full", StringComparison.Ordinal);
        }

        private static void AddParameterSummaries(
            List<Dictionary<string, object>> target,
            Element element,
            string source,
            bool includeReadOnly,
            bool includeValues,
            string nameContains)
        {
            if (element == null || element.Parameters == null) return;

            foreach (Parameter parameter in element.Parameters.Cast<Parameter>())
            {
                Dictionary<string, object> summary = BuildParameterSummary(parameter, source, includeValues);
                string name = Convert.ToString(summary["name"], CultureInfo.InvariantCulture);
                if (!includeReadOnly && GetBool(summary, "isReadOnly", false)) continue;
                if (!string.IsNullOrWhiteSpace(nameContains) &&
                    (name ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                target.Add(summary);
            }
        }

        private static Dictionary<string, object> BuildParameterSummary(Parameter parameter, string source, bool includeValues)
        {
            string name = parameter?.Definition?.Name ?? "(unnamed)";
            var summary = new Dictionary<string, object>
            {
                ["name"] = name,
                ["storageType"] = parameter == null ? "None" : parameter.StorageType.ToString(),
                ["isReadOnly"] = parameter?.IsReadOnly ?? true
            };
            if (!string.IsNullOrWhiteSpace(source)) summary["source"] = source;

            if (parameter == null) return summary;

            try
            {
                object hasValue = typeof(Parameter).GetProperty("HasValue")?.GetValue(parameter, null);
                if (hasValue is bool hasValueBool) summary["hasValue"] = hasValueBool;
            }
            catch
            {
                // Older parameter flavors may not expose HasValue reliably.
            }

            try
            {
                long parameterId = GetParameterIdValue(parameter);
                if (parameterId != long.MinValue && parameterId != GetElementIdValue(ElementId.InvalidElementId))
                {
                    summary["definitionId"] = parameterId.ToString(CultureInfo.InvariantCulture);
                    if (parameterId >= int.MinValue && parameterId <= int.MaxValue)
                    {
                        var builtInParameter = (BuiltInParameter)Convert.ToInt32(parameterId, CultureInfo.InvariantCulture);
                        if (parameterId < 0 && Enum.IsDefined(typeof(BuiltInParameter), builtInParameter))
                        {
                            summary["builtInParameter"] = builtInParameter.ToString();
                        }
                    }
                }
            }
            catch
            {
                // Parameter ids are unavailable for some built-in/internal parameter flavors.
            }

            try
            {
                summary["isShared"] = parameter.IsShared;
            }
            catch
            {
                // Not all parameter sources expose shared state.
            }

            if (TryGetSharedParameterGuid(parameter, out Guid sharedGuid))
            {
                summary["guid"] = sharedGuid.ToString("D");
            }

            ForgeTypeId dataType = GetParameterDataType(parameter);
            string specTypeId = GetForgeTypeIdString(dataType);
            if (!string.IsNullOrWhiteSpace(specTypeId))
            {
                summary["specTypeId"] = specTypeId;
                summary["isYesNo"] = IsYesNoParameter(parameter, dataType);
            }

            string unitTypeId = GetForgeTypeIdString(GetParameterUnitTypeId(parameter));
            if (!string.IsNullOrWhiteSpace(unitTypeId))
            {
                summary["unitTypeId"] = unitTypeId;
            }

            if (includeValues)
            {
                object value = ParameterValue(parameter);
                summary["value"] = value;

                if (parameter.StorageType == StorageType.ElementId)
                {
                    ElementId elementId = parameter.AsElementId();
                    if (IsValidElementId(elementId)) summary["elementIdValue"] = ToElementIdString(elementId);
                }

                try
                {
                    string valueString = parameter.AsValueString();
                    if (!string.IsNullOrWhiteSpace(valueString)) summary["valueString"] = valueString;
                }
                catch
                {
                    // Some storage types do not provide display strings.
                }
            }

            return summary;
        }
    }
}
