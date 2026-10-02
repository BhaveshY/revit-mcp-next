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
        private LegacyResponse HandleGetWarnings(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_warnings.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            int limit = Math.Min(MaxWarningLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            string[] fields = NormalizeWarningFields(GetStringList(payload, "fields"), GetString(payload, "preset"), warnings);

            List<FailureMessage> materialized = document.GetWarnings()
                .Where(failure => MatchesWarningFilter(failure, filter))
                .OrderBy(failure => WarningSeverity(failure), StringComparer.OrdinalIgnoreCase)
                .ThenBy(failure => WarningDescription(failure), StringComparer.OrdinalIgnoreCase)
                .ThenBy(failure => WarningDefinitionId(failure), StringComparer.OrdinalIgnoreCase)
                .ThenBy(failure => FirstWarningElementId(failure), StringComparer.OrdinalIgnoreCase)
                .ToList();

            PageResult<FailureMessage> pageResult = PageItems(materialized, offset, limit, includeTotalCount);
            List<FailureMessage> page = pageResult.Items;
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select((failure, index) => BuildWarningItem(failure, fields, offset + index)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = pageResult.Truncated,
                ["fields"] = fields,
                ["scope"] = "warnings",
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

        private static bool MatchesWarningFilter(FailureMessage failure, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> severities = GetStringList(filter, "severities");
            if (severities.Count > 0 && !severities.Contains(WarningSeverity(failure), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> failureDefinitionIds = GetStringList(filter, "failureDefinitionIds");
            if (failureDefinitionIds.Count > 0 && !failureDefinitionIds.Contains(WarningDefinitionId(failure), StringComparer.OrdinalIgnoreCase)) return false;

            string descriptionContains = GetString(filter, "descriptionContains");
            if (!string.IsNullOrWhiteSpace(descriptionContains) &&
                (WarningDescription(failure) ?? string.Empty).IndexOf(descriptionContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            IReadOnlyList<string> elementIds = GetStringList(filter, "elementIds");
            if (elementIds.Count > 0)
            {
                var ids = new HashSet<string>(WarningElementIds(failure, includeAdditional: true), StringComparer.OrdinalIgnoreCase);
                if (!elementIds.Any(id => ids.Contains(id))) return false;
            }

            return true;
        }

        private static Dictionary<string, object> BuildWarningItem(FailureMessage failure, IReadOnlyList<string> fields, int ordinal)
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = BuildWarningId(failure, ordinal)
            };

            List<string> failingElementIds = null;
            List<string> additionalElementIds = null;

            foreach (string field in fields)
            {
                switch (field)
                {
                    case "id":
                        break;
                    case "severity":
                        item["severity"] = WarningSeverity(failure);
                        break;
                    case "description":
                        item["description"] = WarningDescription(failure);
                        break;
                    case "failureDefinitionId":
                        string definitionId = WarningDefinitionId(failure);
                        if (!string.IsNullOrWhiteSpace(definitionId)) item["failureDefinitionId"] = definitionId;
                        break;
                    case "defaultResolution":
                        string resolution = WarningDefaultResolution(failure);
                        if (!string.IsNullOrWhiteSpace(resolution)) item["defaultResolution"] = resolution;
                        break;
                    case "failingElementIds":
                        failingElementIds = failingElementIds ?? WarningElementIds(failure, includeAdditional: false).ToList();
                        item["failingElementIds"] = failingElementIds.Take(MaxWarningElementIds).ToArray();
                        if (failingElementIds.Count > MaxWarningElementIds) item["failingElementIdsTruncated"] = true;
                        break;
                    case "additionalElementIds":
                        additionalElementIds = additionalElementIds ?? WarningAdditionalElementIds(failure).ToList();
                        item["additionalElementIds"] = additionalElementIds.Take(MaxWarningElementIds).ToArray();
                        if (additionalElementIds.Count > MaxWarningElementIds) item["additionalElementIdsTruncated"] = true;
                        break;
                    case "failingElementCount":
                        failingElementIds = failingElementIds ?? WarningElementIds(failure, includeAdditional: false).ToList();
                        item["failingElementCount"] = failingElementIds.Count;
                        break;
                    case "additionalElementCount":
                        additionalElementIds = additionalElementIds ?? WarningAdditionalElementIds(failure).ToList();
                        item["additionalElementCount"] = additionalElementIds.Count;
                        break;
                    case "failingElementIdsTruncated":
                        failingElementIds = failingElementIds ?? WarningElementIds(failure, includeAdditional: false).ToList();
                        item["failingElementIdsTruncated"] = failingElementIds.Count > MaxWarningElementIds;
                        break;
                    case "additionalElementIdsTruncated":
                        additionalElementIds = additionalElementIds ?? WarningAdditionalElementIds(failure).ToList();
                        item["additionalElementIdsTruncated"] = additionalElementIds.Count > MaxWarningElementIds;
                        break;
                }
            }

            return item;
        }

        private static string BuildWarningId(FailureMessage failure, int ordinal)
        {
            string definitionId = WarningDefinitionId(failure);
            string firstElementId = FirstWarningElementId(failure);
            string seed = (definitionId + "|" + WarningDescription(failure) + "|" + firstElementId + "|" + ordinal.ToString(CultureInfo.InvariantCulture))
                .Trim('|');
            if (string.IsNullOrWhiteSpace(seed)) seed = "warning|" + ordinal.ToString(CultureInfo.InvariantCulture);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                return "wrn_" + BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string WarningSeverity(FailureMessage failure)
        {
            try
            {
                return failure?.GetSeverity().ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WarningDescription(FailureMessage failure)
        {
            try
            {
                return failure?.GetDescriptionText() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WarningDefinitionId(FailureMessage failure)
        {
            try
            {
                return failure?.GetFailureDefinitionId()?.Guid.ToString("D") ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WarningDefaultResolution(FailureMessage failure)
        {
            try
            {
                return failure?.GetDefaultResolutionCaption() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FirstWarningElementId(FailureMessage failure)
        {
            return WarningElementIds(failure, includeAdditional: true).FirstOrDefault() ?? string.Empty;
        }

        private static IEnumerable<string> WarningElementIds(FailureMessage failure, bool includeAdditional)
        {
            IEnumerable<string> failing = SafeWarningElementIds(() => failure?.GetFailingElements());
            if (!includeAdditional) return failing;
            return failing.Concat(WarningAdditionalElementIds(failure)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> WarningAdditionalElementIds(FailureMessage failure)
        {
            return SafeWarningElementIds(() => failure?.GetAdditionalElements());
        }

        private static IEnumerable<string> SafeWarningElementIds(Func<ICollection<ElementId>> read)
        {
            try
            {
                ICollection<ElementId> ids = read();
                if (ids == null) return Enumerable.Empty<string>();
                return ids.Where(IsValidElementId).Select(ToElementIdString).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }
    }
}
