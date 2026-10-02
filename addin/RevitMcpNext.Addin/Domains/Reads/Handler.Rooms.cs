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
        private BridgeResponseEnvelope HandleGetRooms(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.get_rooms.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            var collectorSw = Stopwatch.StartNew();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            Dictionary<string, object> filter = CloneDictionary(GetDictionary(payload, "filter"));
            int limit = Math.Min(MaxRoomLimit, Math.Max(1, GetInt(payload, "limit") ?? 50));
            int offset = ParseCursor(GetString(payload, "cursor"), warnings);
            bool includeTotalCount = GetBool(payload, "includeTotalCount", false);
            bool includeUnplaced = GetBool(payload, "includeUnplaced", false);
            string[] fields = NormalizeRoomFields(GetStringList(payload, "fields"), GetString(payload, "preset"), warnings);

            List<Room> materialized = CreateRoomElements(document, filter, warnings)
                .Where(room => includeUnplaced || IsRoomPlaced(room))
                .Where(room => MatchesRoomFilter(document, room, filter))
                .OrderBy(room => GetRoomLevelName(document, room), StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetRoomNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(SafeElementName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(room => GetElementIdValue(room.Id))
                .ToList();
            int totalCount = materialized.Count;
            List<Room> page = materialized.Skip(offset).Take(limit).ToList();
            collectorSw.Stop();

            var data = new Dictionary<string, object>
            {
                ["document"] = BuildDocumentReference(document, generation),
                ["items"] = page.Select(room => BuildRoomItem(document, room, fields)).ToArray(),
                ["returnedCount"] = page.Count,
                ["limit"] = limit,
                ["truncated"] = offset + page.Count < totalCount,
                ["fields"] = fields,
                ["units"] = new Dictionary<string, object>
                {
                    ["area"] = "m2",
                    ["volume"] = "m3",
                    ["location"] = "mm"
                },
                ["scope"] = "rooms",
                ["source"] = "revit-addin"
            };

            if (includeTotalCount) data["totalCount"] = totalCount;
            if (offset + page.Count < totalCount) data["cursor"] = (offset + page.Count).ToString(CultureInfo.InvariantCulture);

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
                    TotalCount = includeTotalCount ? totalCount : (int?)null
                },
                generation: generation);
        }

        private static IEnumerable<Room> CreateRoomElements(Document document, Dictionary<string, object> filter, List<BridgeWarning> warnings)
        {
            IReadOnlyList<string> elementIds = GetStringList(filter, "elementIds");
            IReadOnlyList<string> uniqueIds = GetStringList(filter, "uniqueIds");
            if (elementIds.Count > 0 || uniqueIds.Count > 0)
            {
                var rooms = new List<Room>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string elementId in elementIds)
                {
                    Element element = null;
                    try
                    {
                        element = document.GetElement(CreateElementId(elementId));
                    }
                    catch
                    {
                        warnings.Add(new BridgeWarning
                        {
                            Code = "INVALID_ROOM_ID",
                            Message = "filter.elementIds contains an invalid room ElementId; it was ignored."
                        });
                    }

                    Room room = element as Room;
                    if (room == null) continue;
                    string key = ToElementIdString(room.Id);
                    if (seen.Add(key)) rooms.Add(room);
                }

                foreach (string uniqueId in uniqueIds)
                {
                    Element element = null;
                    try
                    {
                        element = document.GetElement(uniqueId);
                    }
                    catch
                    {
                        warnings.Add(new BridgeWarning
                        {
                            Code = "INVALID_ROOM_UNIQUE_ID",
                            Message = "filter.uniqueIds contains an invalid room UniqueId; it was ignored."
                        });
                    }

                    Room room = element as Room;
                    if (room == null) continue;
                    string key = ToElementIdString(room.Id);
                    if (seen.Add(key)) rooms.Add(room);
                }

                return rooms;
            }

            return new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>();
        }

        private static bool MatchesRoomFilter(Document document, Room room, Dictionary<string, object> filter)
        {
            IReadOnlyList<string> levelIds = GetStringList(filter, "levelIds");
            if (levelIds.Count > 0 && !levelIds.Contains(ToElementIdString(GetLevelId(room)), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> phaseIds = GetStringList(filter, "phaseIds");
            if (phaseIds.Count > 0 && !phaseIds.Contains(ToElementIdString(GetCreatedPhaseId(room)), StringComparer.OrdinalIgnoreCase)) return false;

            IReadOnlyList<string> numbers = GetStringList(filter, "numbers");
            string number = GetRoomNumber(room);
            if (numbers.Count > 0 && !numbers.Contains(number, StringComparer.OrdinalIgnoreCase)) return false;

            string numberContains = GetString(filter, "numberContains");
            if (!string.IsNullOrWhiteSpace(numberContains) &&
                (number ?? string.Empty).IndexOf(numberContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string nameContains = GetString(filter, "nameContains");
            if (!string.IsNullOrWhiteSpace(nameContains) &&
                (GetRoomName(room) ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string departmentContains = GetString(filter, "departmentContains");
            if (!string.IsNullOrWhiteSpace(departmentContains) &&
                (GetRoomDepartment(room) ?? string.Empty).IndexOf(departmentContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            return true;
        }

        private static Dictionary<string, object> BuildRoomItem(Document document, Room room, IReadOnlyList<string> fields)
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(room.Id)
            };

            foreach (string field in fields)
            {
                switch (field)
                {
                    case "id":
                        break;
                    case "uniqueId":
                        item["uniqueId"] = room.UniqueId;
                        break;
                    case "number":
                        item["number"] = GetRoomNumber(room);
                        break;
                    case "name":
                        item["name"] = GetRoomName(room);
                        break;
                    case "levelId":
                        ElementId levelId = GetLevelId(room);
                        if (IsValidElementId(levelId)) item["levelId"] = ToElementIdString(levelId);
                        break;
                    case "levelName":
                        string levelName = GetRoomLevelName(document, room);
                        if (!string.IsNullOrWhiteSpace(levelName)) item["levelName"] = levelName;
                        break;
                    case "phaseId":
                        ElementId phaseId = GetCreatedPhaseId(room);
                        if (IsValidElementId(phaseId)) item["phaseId"] = ToElementIdString(phaseId);
                        break;
                    case "phaseName":
                        ElementId phaseNameId = GetCreatedPhaseId(room);
                        Element phase = IsValidElementId(phaseNameId) ? document.GetElement(phaseNameId) : null;
                        if (phase != null) item["phaseName"] = SafeElementName(phase);
                        break;
                    case "area":
                        double area = SafeRoomArea(room);
                        if (area > 0) item["area"] = AreaValue(area);
                        break;
                    case "volume":
                        double volume = SafeRoomVolume(room);
                        if (volume > 0) item["volume"] = VolumeValue(volume);
                        break;
                    case "perimeter":
                        Parameter perimeter = room.get_Parameter(BuiltInParameter.ROOM_PERIMETER);
                        if (perimeter != null && perimeter.StorageType == StorageType.Double && perimeter.AsDouble() > 0)
                        {
                            item["perimeter"] = LengthValue(perimeter.AsDouble());
                        }
                        break;
                    case "location":
                        LocationPoint point = room.Location as LocationPoint;
                        if (point != null) item["location"] = PointValue(point.Point);
                        break;
                    case "isPlaced":
                        item["isPlaced"] = IsRoomPlaced(room);
                        break;
                    case "isEnclosed":
                        item["isEnclosed"] = SafeRoomArea(room) > 0;
                        break;
                    case "department":
                        string department = GetRoomDepartment(room);
                        if (!string.IsNullOrWhiteSpace(department)) item["department"] = department;
                        break;
                    default:
                        if (field.StartsWith("param:", StringComparison.OrdinalIgnoreCase))
                        {
                            string parameterName = field.Substring("param:".Length);
                            Parameter parameter = room.LookupParameter(parameterName);
                            if (parameter != null)
                            {
                                Dictionary<string, object> extras = GetOrCreateFields(item);
                                extras[parameterName] = ParameterValue(parameter);
                            }
                        }
                        break;
                }
            }

            return item;
        }

        private static string GetRoomNumber(Room room)
        {
            try
            {
                return room?.Number ?? string.Empty;
            }
            catch
            {
                Parameter parameter = room?.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                return parameter?.AsString() ?? string.Empty;
            }
        }

        private static string GetRoomName(Room room)
        {
            try
            {
                Parameter parameter = room?.get_Parameter(BuiltInParameter.ROOM_NAME);
                string parameterValue = parameter?.AsString();
                if (!string.IsNullOrWhiteSpace(parameterValue)) return parameterValue;
            }
            catch
            {
                // Fall through to the Revit display name.
            }

            return room == null ? string.Empty : SafeElementName(room);
        }

        private static string GetRoomDepartment(Room room)
        {
            try
            {
                Parameter parameter = room?.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                return parameter?.AsString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetRoomLevelName(Document document, Room room)
        {
            ElementId levelId = GetLevelId(room);
            Element level = IsValidElementId(levelId) ? document.GetElement(levelId) : null;
            return level == null ? string.Empty : SafeElementName(level);
        }

        private static ElementId GetCreatedPhaseId(Element element)
        {
            try
            {
                ElementId phaseId = element.CreatedPhaseId;
                return IsValidElementId(phaseId) ? phaseId : ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        private static double SafeRoomArea(Room room)
        {
            try
            {
                return room?.Area ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double SafeRoomVolume(Room room)
        {
            try
            {
                return room?.Volume ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsRoomPlaced(Room room)
        {
            try
            {
                return room != null && room.Location != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
