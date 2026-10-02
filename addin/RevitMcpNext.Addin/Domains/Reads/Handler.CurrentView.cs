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
        private BridgeResponseEnvelope HandleGetCurrentView(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_current_view.", sw);
            }
            if (!IsUiActiveDocument(app, document))
            {
                return Failure(request, "TARGET_DOCUMENT_NOT_ACTIVE", "The targeted document is open but is not the UI-active Revit document required by revit.get_current_view.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            View view = SafeActiveView(document);
            if (view == null)
            {
                return Failure(request, "NO_ACTIVE_VIEW", "The active Revit document does not expose an active view.", sw);
            }

            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            bool includeCropBox = GetBool(payload, "includeCropBox", false);
            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["view"] = BuildViewInfo(document, view, includeCropBox),
                ["source"] = "revit-addin"
            };

            return Success(
                request,
                data,
                sw,
                metrics: new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    ReturnedCount = 1,
                    TotalCount = 1
                },
                generation: generation);
        }

        private BridgeResponseEnvelope HandleGetCurrentViewElements(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_current_view_elements.", sw);
            }
            if (!IsUiActiveDocument(app, document))
            {
                return Failure(request, "TARGET_DOCUMENT_NOT_ACTIVE", "The targeted document is open but is not the UI-active Revit document required by revit.get_current_view_elements.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            View view = SafeActiveView(document);
            if (view == null)
            {
                return Failure(request, "NO_ACTIVE_VIEW", "The active Revit document does not expose an active view.", sw);
            }

            var warnings = new List<BridgeWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            filter["viewId"] = ToElementIdString(view.Id);

            if (GetBool(payload, "includeHidden", false))
            {
                warnings.Add(new BridgeWarning
                {
                    Code = "INCLUDE_HIDDEN_LIMITED",
                    Message = "Revit view collectors only return elements visible to the collector; hidden element expansion is not available in this release."
                });
            }

            return HandleScopedElementList(
                app,
                request,
                sw,
                document,
                generation,
                payload,
                filter,
                warnings,
                "activeView",
                view,
                includeSelection: false);
        }

        private BridgeResponseEnvelope HandleGetSelection(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_selection.", sw);
            }
            if (!IsUiActiveDocument(app, document))
            {
                return Failure(request, "TARGET_DOCUMENT_NOT_ACTIVE", "The targeted document is open but is not the UI-active Revit document required by revit.get_selection.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            filter["selectionOnly"] = true;

            return HandleScopedElementList(
                app,
                request,
                sw,
                document,
                generation,
                payload,
                filter,
                warnings,
                "selection",
                null,
                includeSelection: true);
        }

        private BridgeResponseEnvelope HandleScopedElementList(
            UIApplication app,
            BridgeRequestEnvelope request,
            Stopwatch sw,
            Document document,
            long generation,
            Dictionary<string, object> payload,
            Dictionary<string, object> filter,
            List<BridgeWarning> warnings,
            string scopeOverride,
            View view,
            bool includeSelection)
        {
            var collectorSw = Stopwatch.StartNew();
            int limit = Math.Min(MaxQueryLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            string preset = GetString(payload, "preset");
            string[] fields = NormalizeFields(GetStringList(payload, "fields"), preset, warnings);

            string scope;
            IEnumerable<Element> elements = CreateFilteredElements(app, document, filter, warnings, out scope);
            PageResult<Element> pageResult = PageItems(elements, offset, limit, includeTotalCount);
            List<Element> page = pageResult.Items;
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(element => BuildQueryItem(element, fields)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = pageResult.Truncated,
                ["fields"] = fields,
                ["units"] = new Dictionary<string, object>
                {
                    ["elevation"] = "mm",
                    ["length"] = "mm",
                    ["location"] = "mm",
                    ["bounds"] = "mm"
                },
                ["scope"] = string.IsNullOrWhiteSpace(scopeOverride) ? scope : scopeOverride,
                ["source"] = "revit-addin"
            };

            if (view != null) data["view"] = BuildViewSummary(view);
            if (includeSelection)
            {
                UIDocument uidocument = app.ActiveUIDocument;
                bool available = uidocument != null && ReferenceEquals(uidocument.Document, document);
                data["selection"] = new Dictionary<string, object>
                {
                    ["count"] = available ? uidocument.Selection.GetElementIds().Count : 0,
                    ["available"] = available
                };
            }

            if (includeTotalCount && pageResult.TotalCount.HasValue) data["totalCount"] = pageResult.TotalCount.Value;
            if (pageResult.Truncated) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

            return Success(
                request,
                data,
                sw,
                warnings,
                new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = page.Count,
                    TotalCount = pageResult.TotalCount
                },
                generation: generation);
        }
    }
}
