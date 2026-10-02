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
        private BridgeResponseEnvelope HandleGetLevels(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_levels.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var collectorSw = Stopwatch.StartNew();
            var levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .Select(BuildLevelSummary)
                .ToArray();
            collectorSw.Stop();

            return Success(
                request,
                levels,
                sw,
                metrics: new BridgeMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = levels.Length,
                    TotalCount = levels.Length
                },
                generation: generation);
        }

        private static Dictionary<string, object> BuildLevelSummary(Level level)
        {
            return new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(level.Id),
                ["uniqueId"] = level.UniqueId,
                ["name"] = level.Name,
                ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters), "mm", "metric"),
                ["isBuildingStory"] = IsBuildingStory(level)
            };
        }

        private static Level GetAssociatedLevel(View view)
        {
            if (view == null) return null;

            try
            {
                object value = typeof(View).GetProperty("GenLevel")?.GetValue(view, null);
                Level level = value as Level;
                if (level != null) return level;
            }
            catch
            {
                // Fall through to parameter-based lookup.
            }

            try
            {
                if (Enum.IsDefined(typeof(BuiltInParameter), "PLAN_VIEW_LEVEL"))
                {
                    var builtInParameter = (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), "PLAN_VIEW_LEVEL");
                    Parameter parameter = view.get_Parameter(builtInParameter);
                    if (parameter != null && parameter.StorageType == StorageType.ElementId)
                    {
                        return view.Document.GetElement(parameter.AsElementId()) as Level;
                    }
                }
            }
            catch
            {
                // Some view types do not have an associated level.
            }

            return null;
        }
    }
}
