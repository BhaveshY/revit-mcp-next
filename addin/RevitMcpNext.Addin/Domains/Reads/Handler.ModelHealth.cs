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
        private LegacyResponse HandleAnalyzeModel(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.analyze_model.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<LegacyWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            int bucketLimit = Math.Min(MaxStatisticsBucketLimit, Math.Max(1, GetInt(payload, "bucketLimit") ?? 50));
            int maxElementsScanned = Math.Min(MaxStatisticsScanLimit, Math.Max(100, GetInt(payload, "maxElementsScanned") ?? 50000));
            bool includeCategoryBreakdown = GetBool(payload, "includeCategoryBreakdown", true);
            bool includeClassBreakdown = GetBool(payload, "includeClassBreakdown", true);
            bool includeLevelBreakdown = GetBool(payload, "includeLevelBreakdown", true);

            var collectorSw = Stopwatch.StartNew();
            bool truncated = false;
            var elements = new List<Element>();
            foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
            {
                if (elements.Count >= maxElementsScanned)
                {
                    truncated = true;
                    break;
                }

                elements.Add(element);
            }

            if (truncated)
            {
                warnings.Add(new LegacyWarning
                {
                    Code = "MODEL_STATISTICS_TRUNCATED",
                    Message = "Model statistics were computed from the first " + elements.Count.ToString(CultureInfo.InvariantCulture) + " non-type elements. Increase maxElementsScanned for a deeper scan."
                });
            }

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["totals"] = new Dictionary<string, object>
                {
                    ["elements"] = elements.Count,
                    ["modelElements"] = elements.Count(IsModelElement),
                    ["elementTypes"] = CountCollectorElements(new FilteredElementCollector(document).WhereElementIsElementType()),
                    ["families"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(Family))),
                    ["views"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(View))),
                    ["sheets"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(ViewSheet))),
                    ["levels"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(Level))),
                    ["materials"] = CountCollectorElements(new FilteredElementCollector(document).OfClass(typeof(Material)))
                },
                ["scannedElements"] = elements.Count,
                ["bucketLimit"] = bucketLimit,
                ["truncated"] = truncated,
                ["source"] = "revit-addin"
            };

            if (includeCategoryBreakdown) data["byCategory"] = BuildCategoryBuckets(elements, bucketLimit);
            if (includeClassBreakdown) data["byClass"] = BuildClassBuckets(elements, bucketLimit);
            if (includeLevelBreakdown) data["byLevel"] = BuildLevelBuckets(document, elements, bucketLimit);
            collectorSw.Stop();

            return Success(
                request,
                data,
                sw,
                warnings,
                new LegacyMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = elements.Count,
                    TotalCount = truncated ? (int?)null : elements.Count
                },
                generation: generation);
        }

        private LegacyResponse HandleGetModelReadiness(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_model_readiness.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> data = BuildModelReadiness(app, document, generation, request.Payload);
            collectorSw.Stop();

            return Success(
                request,
                data,
                sw,
                metrics: new LegacyMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds,
                    ReturnedCount = 1,
                    TotalCount = 1
                },
                generation: generation);
        }

        private LegacyResponse HandleGetModelContext(UIApplication app, LegacyRequest request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_model_context.", sw);
            }

            LegacyResponse generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            bool includeProjectInfo = GetBool(payload, "includeProjectInfo", true);
            bool includePhases = GetBool(payload, "includePhases", true);
            bool includeWorksets = GetBool(payload, "includeWorksets", true);
            bool includeDesignOptions = GetBool(payload, "includeDesignOptions", true);
            bool includeRevitLinks = GetBool(payload, "includeRevitLinks", true);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            int phaseLimit = Math.Min(MaxModelContextLimit, Math.Max(1, GetInt(payload, "phaseLimit") ?? 50));
            int worksetLimit = Math.Min(MaxModelContextLimit, Math.Max(1, GetInt(payload, "worksetLimit") ?? 50));
            int designOptionLimit = Math.Min(MaxModelContextLimit, Math.Max(1, GetInt(payload, "designOptionLimit") ?? 50));
            int revitLinkLimit = Math.Min(MaxModelContextLimit, Math.Max(1, GetInt(payload, "revitLinkLimit") ?? 50));

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["source"] = "revit-addin"
            };

            if (includeProjectInfo) data["projectInfo"] = BuildProjectInfoSummary(document.ProjectInformation);
            if (includePhases) data["phases"] = BuildContextSection(BuildPhaseSummaries(document), phaseLimit, includeTotalCount);
            if (includeWorksets) data["worksets"] = BuildWorksetSection(document, worksetLimit, includeTotalCount);
            if (includeDesignOptions) data["designOptions"] = BuildContextSection(BuildDesignOptionSummaries(document), designOptionLimit, includeTotalCount);
            if (includeRevitLinks) data["revitLinks"] = BuildContextSection(BuildRevitLinkSummaries(document), revitLinkLimit, includeTotalCount);
            collectorSw.Stop();

            return Success(
                request,
                data,
                sw,
                new List<LegacyWarning>(),
                new LegacyMetrics
                {
                    ElapsedMs = sw.ElapsedMilliseconds,
                    CollectorElapsedMs = collectorSw.ElapsedMilliseconds
                },
                generation: generation);
        }

        private static Dictionary<string, object> BuildProjectInfoSummary(ProjectInfo projectInfo)
        {
            if (projectInfo == null) return new Dictionary<string, object>();
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(projectInfo.Id),
                ["uniqueId"] = projectInfo.UniqueId
            };

            AddIfNotBlank(summary, "number", projectInfo.Number);
            AddIfNotBlank(summary, "name", projectInfo.Name);
            AddIfNotBlank(summary, "clientName", projectInfo.ClientName);
            AddIfNotBlank(summary, "status", projectInfo.Status);
            AddIfNotBlank(summary, "issueDate", projectInfo.IssueDate);
            AddIfNotBlank(summary, "address", projectInfo.Address);
            AddIfNotBlank(summary, "buildingName", projectInfo.BuildingName);
            AddIfNotBlank(summary, "organizationName", projectInfo.OrganizationName);
            AddIfNotBlank(summary, "organizationDescription", projectInfo.OrganizationDescription);
            AddIfNotBlank(summary, "author", projectInfo.Author);
            return summary;
        }

        private static List<Dictionary<string, object>> BuildPhaseSummaries(Document document)
        {
            var phases = new List<Dictionary<string, object>>();
            int sequence = 0;
            foreach (Phase phase in document.Phases.Cast<Phase>())
            {
                phases.Add(new Dictionary<string, object>
                {
                    ["id"] = ToElementIdString(phase.Id),
                    ["name"] = phase.Name,
                    ["sequence"] = sequence++
                });
            }

            return phases;
        }

        private static Dictionary<string, object> BuildWorksetSection(Document document, int limit, bool includeTotalCount)
        {
            if (!document.IsWorkshared)
            {
                var unavailable = BuildContextSection(new List<Dictionary<string, object>>(), limit, includeTotalCount);
                unavailable["available"] = false;
                return unavailable;
            }

            var worksets = new FilteredWorksetCollector(document)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .OrderBy(workset => workset.Name, StringComparer.OrdinalIgnoreCase)
                .Select(BuildWorksetSummary)
                .ToList();
            var section = BuildContextSection(worksets, limit, includeTotalCount);
            section["available"] = true;
            return section;
        }

        private static Dictionary<string, object> BuildWorksetSummary(Workset workset)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToWorksetIdString(workset.Id),
                ["uniqueId"] = workset.UniqueId.ToString("D"),
                ["name"] = workset.Name,
                ["kind"] = workset.Kind.ToString(),
                ["isOpen"] = workset.IsOpen,
                ["isEditable"] = workset.IsEditable,
                ["isVisibleByDefault"] = workset.IsVisibleByDefault,
                ["isDefaultWorkset"] = workset.IsDefaultWorkset
            };
            AddIfNotBlank(summary, "owner", workset.Owner);
            return summary;
        }

        private static List<Dictionary<string, object>> BuildDesignOptionSummaries(Document document)
        {
            ElementId activeOptionId = null;
            try
            {
                activeOptionId = DesignOption.GetActiveDesignOptionId(document);
            }
            catch
            {
                activeOptionId = null;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(DesignOption))
                .Cast<DesignOption>()
                .OrderBy(option => GetDesignOptionSetName(option), StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => SafeElementName(option), StringComparer.OrdinalIgnoreCase)
                .Select(option => BuildDesignOptionSummary(option, activeOptionId))
                .ToList();
        }

        private static Dictionary<string, object> BuildDesignOptionSummary(DesignOption option, ElementId activeOptionId)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(option.Id),
                ["uniqueId"] = option.UniqueId,
                ["name"] = SafeElementName(option),
                ["isPrimary"] = option.IsPrimary,
                ["isActive"] = IsValidElementId(activeOptionId) && string.Equals(ToElementIdString(activeOptionId), ToElementIdString(option.Id), StringComparison.OrdinalIgnoreCase)
            };

            AddIfNotBlank(summary, "optionSetId", GetDesignOptionSetId(option));
            AddIfNotBlank(summary, "optionSetName", GetDesignOptionSetName(option));
            return summary;
        }

        private static string GetDesignOptionSetId(DesignOption option)
        {
            try
            {
                ElementId id = option.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
                return IsValidElementId(id) ? ToElementIdString(id) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string GetDesignOptionSetName(DesignOption option)
        {
            try
            {
                Parameter parameter = option.get_Parameter(BuiltInParameter.OPTION_SET_NAME);
                return parameter?.AsString() ?? parameter?.AsValueString();
            }
            catch
            {
                return null;
            }
        }

        private static List<Dictionary<string, object>> BuildRevitLinkSummaries(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .OrderBy(link => SafeElementName(link), StringComparer.OrdinalIgnoreCase)
                .Select(link => BuildRevitLinkSummary(document, link))
                .ToList();
        }

        private static Dictionary<string, object> BuildRevitLinkSummary(Document document, RevitLinkInstance link)
        {
            ElementId typeId = link.GetTypeId();
            RevitLinkType linkType = document.GetElement(typeId) as RevitLinkType;
            Document linkedDocument = null;
            try
            {
                linkedDocument = link.GetLinkDocument();
            }
            catch
            {
                linkedDocument = null;
            }

            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(link.Id),
                ["uniqueId"] = link.UniqueId,
                ["name"] = SafeElementName(link)
            };

            if (IsValidElementId(typeId)) summary["typeId"] = ToElementIdString(typeId);
            if (linkType != null)
            {
                AddIfNotBlank(summary, "typeName", SafeElementName(linkType));
                summary["isLoaded"] = IsRevitLinkLoaded(document, linkType);
                AddIfNotBlank(summary, "loadStatus", GetRevitLinkStatus(linkType));
            }
            else
            {
                summary["isLoaded"] = linkedDocument != null;
            }

            if (linkedDocument != null)
            {
                AddIfNotBlank(summary, "linkedDocumentTitle", linkedDocument.Title);
                AddIfNotBlank(summary, "linkedDocumentPath", linkedDocument.PathName);
            }

            return summary;
        }

        private static bool IsRevitLinkLoaded(Document document, RevitLinkType linkType)
        {
            try
            {
                return RevitLinkType.IsLoaded(document, linkType.Id);
            }
            catch
            {
                try
                {
                    return linkType.GetLinkedFileStatus() == LinkedFileStatus.Loaded;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string GetRevitLinkStatus(RevitLinkType linkType)
        {
            try
            {
                return linkType.GetLinkedFileStatus().ToString();
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> BuildContextSection(List<Dictionary<string, object>> items, int limit, bool includeTotalCount)
        {
            List<Dictionary<string, object>> page = items.Take(limit).ToList();
            var section = new Dictionary<string, object>
            {
                ["items"] = page.ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = page.Count < items.Count
            };
            if (includeTotalCount) section["totalCount"] = items.Count;
            return section;
        }

        private static void AddIfNotBlank(Dictionary<string, object> target, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) target[key] = value;
        }

        private static Dictionary<string, object> BuildModelReadiness(UIApplication app, Document document, long generation, Dictionary<string, object> payload)
        {
            IReadOnlyList<string> requestedScenarios = GetStringList(payload, "scenarios");
            HashSet<string> requestedScenarioSet = requestedScenarios.Count > 0
                ? new HashSet<string>(requestedScenarios, StringComparer.OrdinalIgnoreCase)
                : null;

            bool WantsScenario(string name)
            {
                return requestedScenarioSet == null || requestedScenarioSet.Contains(name);
            }

            Level[] levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToArray();

            View activeView = SafeActiveView(document);
            int? wallCount = null;
            int? wallTypeCount = null;
            int? floorTypeCount = null;
            int? roomCount = null;
            int? textNoteTypeCount = null;
            FamilySymbol[] familySymbols = null;
            FamilySymbol[] wallHostedSymbols = null;
            FamilySymbol[] levelBasedSymbols = null;

            int GetWallCount()
            {
                if (!wallCount.HasValue)
                {
                    wallCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfCategory(BuiltInCategory.OST_Walls)
                        .WhereElementIsNotElementType());
                }
                return wallCount.Value;
            }

            int GetWallTypeCount()
            {
                if (!wallTypeCount.HasValue)
                {
                    wallTypeCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfClass(typeof(WallType)));
                }
                return wallTypeCount.Value;
            }

            int GetFloorTypeCount()
            {
                if (!floorTypeCount.HasValue)
                {
                    floorTypeCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfClass(typeof(FloorType)));
                }
                return floorTypeCount.Value;
            }

            int GetRoomCount()
            {
                if (!roomCount.HasValue)
                {
                    roomCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType());
                }
                return roomCount.Value;
            }

            int GetTextNoteTypeCount()
            {
                if (!textNoteTypeCount.HasValue)
                {
                    textNoteTypeCount = CountCollectorElements(new FilteredElementCollector(document)
                        .OfClass(typeof(TextNoteType)));
                }
                return textNoteTypeCount.Value;
            }

            FamilySymbol[] GetFamilySymbols()
            {
                if (familySymbols == null)
                {
                    familySymbols = new FilteredElementCollector(document)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .ToArray();
                }
                return familySymbols;
            }

            FamilySymbol[] GetWallHostedSymbols()
            {
                if (wallHostedSymbols == null)
                {
                    wallHostedSymbols = GetFamilySymbols()
                        .Where(IsSupportedWallHostedFamilySymbol)
                        .ToArray();
                }
                return wallHostedSymbols;
            }

            FamilySymbol[] GetLevelBasedSymbols()
            {
                if (levelBasedSymbols == null)
                {
                    levelBasedSymbols = GetFamilySymbols()
                        .Where(IsSupportedLevelBasedFamilySymbol)
                        .ToArray();
                }
                return levelBasedSymbols;
            }

            var scenarios = new List<Dictionary<string, object>>();
            if (WantsScenario("levels"))
            {
                scenarios.Add(ScenarioReadiness(
                    "levels",
                    levels.Length > 0,
                    levels.Length > 0 ? Array.Empty<string>() : new[] { "At least one project level." },
                    levels.Length > 0 ? "Use revit.get_levels to pick exact level IDs." : "Create a level before level-based model operations.",
                    new Dictionary<string, object> { ["levelCount"] = levels.Length, ["defaultLevelId"] = levels.FirstOrDefault() == null ? null : ToElementIdString(levels.First().Id) }));
            }
            if (WantsScenario("wallCreation"))
            {
                int wallCreationWallTypeCount = GetWallTypeCount();
                int wallCreationWallCount = GetWallCount();
                scenarios.Add(ScenarioReadiness(
                    "wallCreation",
                    levels.Length > 0 && wallCreationWallTypeCount > 0,
                    MissingPrerequisites(
                        levels.Length > 0 ? null : "At least one project level.",
                        wallCreationWallTypeCount > 0 ? null : "At least one wall type."),
                    "Use create_wall with levelId, start, end, and optional wallTypeId discovered from revit.catalog.",
                    new Dictionary<string, object> { ["levelCount"] = levels.Length, ["wallTypeCount"] = wallCreationWallTypeCount, ["wallCount"] = wallCreationWallCount }));
            }
            if (WantsScenario("floorCreation"))
            {
                int floorCreationTypeCount = GetFloorTypeCount();
                scenarios.Add(ScenarioReadiness(
                    "floorCreation",
                    levels.Length > 0 && floorCreationTypeCount > 0,
                    MissingPrerequisites(
                        levels.Length > 0 ? null : "At least one project level.",
                        floorCreationTypeCount > 0 ? null : "At least one floor type."),
                    "Use create_floor with a closed outline on the target level elevation.",
                    new Dictionary<string, object> { ["levelCount"] = levels.Length, ["floorTypeCount"] = floorCreationTypeCount }));
            }
            if (WantsScenario("roomCreation"))
            {
                int roomCreationWallCount = GetWallCount();
                int roomCreationRoomCount = GetRoomCount();
                scenarios.Add(ScenarioReadiness(
                    "roomCreation",
                    levels.Length > 0,
                    levels.Length > 0 ? Array.Empty<string>() : new[] { "At least one project level." },
                    roomCreationWallCount > 0
                        ? "Create or reuse an enclosed room-bounding region, then preview create_room."
                        : "Create room-bounding walls or separators before expecting room area and enclosure.",
                    new Dictionary<string, object> { ["levelCount"] = levels.Length, ["roomCount"] = roomCreationRoomCount, ["wallCount"] = roomCreationWallCount }));
            }
            if (WantsScenario("roomReadback"))
            {
                int roomReadbackCount = GetRoomCount();
                scenarios.Add(ScenarioReadiness(
                    "roomReadback",
                    roomReadbackCount > 0,
                    roomReadbackCount > 0 ? Array.Empty<string>() : new[] { "At least one placed room." },
                    "Use revit.get_rooms with preset=schedule for compact room export.",
                    new Dictionary<string, object> { ["roomCount"] = roomReadbackCount }));
            }
            if (WantsScenario("typeChange"))
            {
                int typeChangeScanned;
                bool typeChangeScanTruncated;
                int typeChangeCandidateCount = CountTypeChangeCandidates(document, 250, out typeChangeScanned, out typeChangeScanTruncated);
                scenarios.Add(ScenarioReadiness(
                    "typeChange",
                    typeChangeCandidateCount > 0,
                    typeChangeCandidateCount > 0 ? Array.Empty<string>() : new[] { "A non-pinned model element with compatible alternate types." },
                    "Use revit.catalog with kind=elementTypes and filter.forElementId before change_element_type.",
                    new Dictionary<string, object> { ["sampledElements"] = typeChangeScanned, ["candidateElements"] = typeChangeCandidateCount, ["scanTruncated"] = typeChangeScanTruncated }));
            }
            if (WantsScenario("familyPlacement"))
            {
                int familyPlacementWallCount = GetWallCount();
                FamilySymbol[] familyPlacementWallHostedSymbols = GetWallHostedSymbols();
                FamilySymbol[] familyPlacementLevelBasedSymbols = GetLevelBasedSymbols();
                scenarios.Add(ScenarioReadiness(
                    "familyPlacement",
                    levels.Length > 0 && (familyPlacementLevelBasedSymbols.Length > 0 || (familyPlacementWallCount > 0 && familyPlacementWallHostedSymbols.Length > 0)),
                    MissingPrerequisites(
                        levels.Length > 0 ? null : "At least one project level.",
                        familyPlacementLevelBasedSymbols.Length > 0 || familyPlacementWallHostedSymbols.Length > 0 ? null : "At least one supported door/window/furniture/equipment/fixture FamilySymbol.",
                        familyPlacementWallHostedSymbols.Length == 0 || familyPlacementWallCount > 0 ? null : "At least one wall host for hosted door/window placement."),
                    "Use revit.catalog kind=familySymbols preset=placement to discover familySymbolId and placementType.",
                    new Dictionary<string, object>
                    {
                        ["wallHostedDoorWindowSymbols"] = familyPlacementWallHostedSymbols.Length,
                        ["levelBasedFurnitureEquipmentFixtureSymbols"] = familyPlacementLevelBasedSymbols.Length,
                        ["wallHostedReady"] = levels.Length > 0 && familyPlacementWallCount > 0 && familyPlacementWallHostedSymbols.Length > 0,
                        ["levelBasedReady"] = levels.Length > 0 && familyPlacementLevelBasedSymbols.Length > 0,
                        ["sampleHostedFamilySymbolId"] = familyPlacementWallHostedSymbols.FirstOrDefault() == null ? null : ToElementIdString(familyPlacementWallHostedSymbols.First().Id),
                        ["sampleLevelBasedFamilySymbolId"] = familyPlacementLevelBasedSymbols.FirstOrDefault() == null ? null : ToElementIdString(familyPlacementLevelBasedSymbols.First().Id)
                    }));
            }
            if (WantsScenario("selection"))
            {
                UIDocument uidocument = app.ActiveUIDocument;
                bool selectionAvailable = uidocument != null && ReferenceEquals(uidocument.Document, document);
                int selectionCount = selectionAvailable ? uidocument.Selection.GetElementIds().Count : 0;
                scenarios.Add(ScenarioReadiness(
                    "selection",
                    selectionAvailable && selectionCount > 0,
                    selectionAvailable
                        ? selectionCount > 0 ? Array.Empty<string>() : new[] { "At least one selected element." }
                        : new[] { "The requested document must be the active UI document." },
                    "Use revit.query filters when no active selection is available.",
                    new Dictionary<string, object> { ["available"] = selectionAvailable, ["selectionCount"] = selectionCount }));
            }
            if (WantsScenario("annotations"))
            {
                bool activeGraphicalView = IsGraphicalView(activeView);
                int annotationTextNoteTypeCount = GetTextNoteTypeCount();
                scenarios.Add(ScenarioReadiness(
                    "annotations",
                    activeGraphicalView,
                    activeGraphicalView ? Array.Empty<string>() : new[] { "An active graphical non-template view." },
                    "Annotation operations should be scoped to an explicit graphical view and valid references.",
                    new Dictionary<string, object> { ["activeGraphicalView"] = activeGraphicalView, ["textNoteTypeCount"] = annotationTextNoteTypeCount }));
            }

            if (!GetBool(payload, "includeHints", true))
            {
                foreach (Dictionary<string, object> scenario in scenarios)
                {
                    scenario.Remove("hints");
                }
            }

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["levels"] = new Dictionary<string, object>
                {
                    ["count"] = levels.Length,
                    ["sample"] = levels.Take(8).Select(BuildLevelSummary).ToArray()
                },
                ["activeView"] = activeView == null ? null : BuildViewSummary(activeView),
                ["scenarios"] = scenarios,
                ["readyCount"] = scenarios.Count(scenario => GetBool(scenario, "ready", false)),
                ["totalCount"] = scenarios.Count,
                ["source"] = "revit-addin"
            };

            return data;
        }

        private static Dictionary<string, object> ScenarioReadiness(
            string name,
            bool ready,
            IEnumerable<string> missingPrerequisites,
            string nextAction,
            Dictionary<string, object> hints)
        {
            string[] missing = (missingPrerequisites ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            var scenario = new Dictionary<string, object>
            {
                ["name"] = name,
                ["ready"] = ready,
                ["missing"] = missing,
                ["missingPrerequisites"] = missing
            };
            if (!string.IsNullOrWhiteSpace(nextAction)) scenario["nextAction"] = nextAction;
            if (hints != null && hints.Count > 0) scenario["hints"] = hints;
            return scenario;
        }

        private static IEnumerable<string> MissingPrerequisites(params string[] values)
        {
            return values == null ? Enumerable.Empty<string>() : values.Where(value => !string.IsNullOrWhiteSpace(value));
        }

        private static int CountTypeChangeCandidates(Document document, int maxScan, out int scanned, out bool truncated)
        {
            int candidates = 0;
            scanned = 0;
            truncated = false;

            foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
            {
                if (scanned >= maxScan)
                {
                    truncated = true;
                    break;
                }

                scanned++;
                if (element == null || element.Pinned) continue;
                ElementId currentTypeId = element.GetTypeId();
                if (!IsValidElementId(currentTypeId)) continue;

                try
                {
                    ICollection<ElementId> validTypeIds = element.GetValidTypes();
                    if (validTypeIds != null && validTypeIds.Count(id => IsValidElementId(id) && !string.Equals(ToElementIdString(id), ToElementIdString(currentTypeId), StringComparison.Ordinal)) > 0)
                    {
                        candidates++;
                    }
                }
                catch
                {
                    // Some element classes do not expose valid type sets; ignore them for readiness.
                }
            }

            return candidates;
        }

        private static bool IsGraphicalView(View view)
        {
            if (view == null || view.IsTemplate) return false;

            return SafeCanBePrinted(view);
        }

        private static bool SafeCanBePrinted(View view)
        {
            if (view == null) return false;

            try
            {
                return view.CanBePrinted;
            }
            catch
            {
                return false;
            }
        }

        private static object[] BuildCategoryBuckets(IEnumerable<Element> elements, int limit)
        {
            return elements
                .GroupBy(GetCategoryBucketKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(group =>
                {
                    Element sample = group.FirstOrDefault();
                    string builtInCategory = sample == null ? string.Empty : GetBuiltInCategoryName(sample);
                    var bucket = new Dictionary<string, object>
                    {
                        ["key"] = group.Key,
                        ["name"] = sample?.Category?.Name ?? group.Key,
                        ["count"] = group.Count()
                    };
                    if (!string.IsNullOrWhiteSpace(builtInCategory)) bucket["builtInCategory"] = builtInCategory;
                    return bucket;
                })
                .ToArray();
        }

        private static object[] BuildClassBuckets(IEnumerable<Element> elements, int limit)
        {
            return elements
                .GroupBy(element => element.GetType().Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(group => new Dictionary<string, object>
                {
                    ["key"] = group.Key,
                    ["count"] = group.Count()
                })
                .ToArray();
        }

        private static object[] BuildLevelBuckets(Document document, IEnumerable<Element> elements, int limit)
        {
            Dictionary<string, string> levelNames = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToDictionary(level => ToElementIdString(level.Id), level => level.Name, StringComparer.OrdinalIgnoreCase);

            return elements
                .Select(element => ToElementIdString(GetLevelId(element)))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(group => new Dictionary<string, object>
                {
                    ["key"] = group.Key,
                    ["name"] = levelNames.TryGetValue(group.Key, out string name) ? name : group.Key,
                    ["count"] = group.Count()
                })
                .ToArray();
        }

        private static string GetCategoryBucketKey(Element element)
        {
            string builtInCategory = GetBuiltInCategoryName(element);
            if (!string.IsNullOrWhiteSpace(builtInCategory)) return builtInCategory;
            return element.Category?.Name ?? "(none)";
        }
    }
}
