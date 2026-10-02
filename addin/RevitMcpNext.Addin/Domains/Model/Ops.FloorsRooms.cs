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
        private static Dictionary<string, object> PreviewCreateFloor(Document document, Dictionary<string, object> operation, int index)
        {
            string levelId = GetString(operation, "levelId");
            if (string.IsNullOrWhiteSpace(levelId)) return BlockedChange(operation, index, "create_floor requires levelId.");

            Level level = ResolveElement(document, levelId) as Level;
            if (level == null) return BlockedChange(operation, index, "Level " + levelId + " was not found.");

            FloorType floorType = ResolveFloorType(document, GetString(operation, "floorTypeId"));
            if (floorType == null) return BlockedChange(operation, index, "A usable floor type was not found.");

            List<Dictionary<string, object>> outline = GetPointList(operation, "outline");
            if (outline.Count < 3) return BlockedChange(operation, index, "create_floor requires at least three outline points.");

            List<XYZ> points;
            try
            {
                points = ToInternalPointList(outline, "outline");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string outlineError = ValidateFloorOutline(document, level, points);
            if (!string.IsNullOrWhiteSpace(outlineError)) return BlockedChange(operation, index, outlineError);

            bool structural = GetBool(operation, "structural", false);
            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["document"] = document.Title,
                    ["levelId"] = ToElementIdString(level.Id),
                    ["levelName"] = level.Name,
                    ["floorTypeId"] = ToElementIdString(floorType.Id),
                    ["floorTypeName"] = SafeElementName(floorType)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["levelId"] = ToElementIdString(level.Id),
                    ["floorTypeId"] = ToElementIdString(floorType.Id),
                    ["outline"] = PointArrayValue(NormalizeClosedLoop(points)),
                    ["area"] = AreaValue(PolygonAreaInternal(NormalizeClosedLoop(points))),
                    ["structural"] = structural
                });
        }

        private static Dictionary<string, object> ApplyCreateFloor(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateFloor(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_floor preview failed.");
            }

            Level level = ResolveElement(document, GetString(operation, "levelId")) as Level;
            FloorType floorType = ResolveFloorType(document, GetString(operation, "floorTypeId"));
            List<XYZ> points = NormalizeClosedLoop(ToInternalPointList(GetPointList(operation, "outline"), "outline"));
            bool structural = GetBool(operation, "structural", false);
#if REVIT2021
            CurveArray profile = BuildCurveArray(points);
#pragma warning disable CS0618
            Floor floor = document.Create.NewFloor(profile, floorType, level, structural);
#pragma warning restore CS0618
#else
            CurveLoop loop = BuildCurveLoop(points);
            Floor floor = Floor.Create(document, new List<CurveLoop> { loop }, floorType.Id, level.Id, structural, null, 0.0);
#endif

            return Change(operation, index, "applied",
                target: ElementTarget(floor, null),
                before: null,
                after: FloorSnapshot(floor, points));
        }

        private static Dictionary<string, object> PreviewCreateRoom(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string levelId = GetString(operation, "levelId");
            Dictionary<string, object> locationValue = GetDictionary(operation, "location");
            if (string.IsNullOrWhiteSpace(levelId)) return BlockedChange(operation, index, "create_room requires levelId.");
            if (locationValue == null) return BlockedChange(operation, index, "create_room requires location.");

            Level level = ResolveElement(document, levelId) as Level;
            if (level == null) return BlockedChange(operation, index, "Level " + levelId + " was not found.");

            UV location;
            try
            {
                location = ToInternalUv(locationValue, "location");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string number = NormalizeOptionalText(GetString(operation, "number"));
            bool allowDuplicateNumber = GetBool(operation, "allowDuplicateNumber", false);
            if (!string.IsNullOrWhiteSpace(number) && !allowDuplicateNumber)
            {
                if (RoomNumberExists(document, number))
                {
                    return BlockedChange(operation, index, "A room numbered '" + number + "' already exists.");
                }
                if (validationContext != null && !validationContext.TryAddRoomNumber(number))
                {
                    return BlockedChange(operation, index, "The change set creates duplicate room number '" + number + "'.");
                }
            }

            Room existingRoom = document.GetRoomAtPoint(new XYZ(location.U, location.V, level.Elevation));
            if (existingRoom != null)
            {
                return BlockedChange(operation, index, "Room " + GetRoomNumber(existingRoom) + " already contains the requested location.");
            }

            var after = new Dictionary<string, object>
            {
                ["levelId"] = ToElementIdString(level.Id),
                ["levelName"] = level.Name,
                ["location"] = Point2Value(location),
                ["allowDuplicateNumber"] = allowDuplicateNumber
            };

            string name = NormalizeOptionalText(GetString(operation, "name"));
            string department = NormalizeOptionalText(GetString(operation, "department"));
            if (!string.IsNullOrWhiteSpace(name)) after["name"] = name;
            if (!string.IsNullOrWhiteSpace(number)) after["number"] = number;
            if (!string.IsNullOrWhiteSpace(department)) after["department"] = department;

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["document"] = document.Title,
                    ["levelId"] = ToElementIdString(level.Id),
                    ["levelName"] = level.Name
                },
                before: null,
                after: after);
        }

        private static Dictionary<string, object> ApplyCreateRoom(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateRoom(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_room preview failed.");
            }

            Level level = ResolveElement(document, GetString(operation, "levelId")) as Level;
            UV location = ToInternalUv(GetDictionary(operation, "location"), "location");
            Room room = document.Create.NewRoom(level, location);
            if (room == null)
            {
                throw new InvalidOperationException("Revit did not create a room at the requested location.");
            }

            SetRoomStringParameter(room, BuiltInParameter.ROOM_NAME, NormalizeOptionalText(GetString(operation, "name")));
            SetRoomStringParameter(room, BuiltInParameter.ROOM_NUMBER, NormalizeOptionalText(GetString(operation, "number")));
            SetRoomStringParameter(room, BuiltInParameter.ROOM_DEPARTMENT, NormalizeOptionalText(GetString(operation, "department")));

            return Change(operation, index, "applied",
                target: ElementTarget(room, null),
                before: null,
                after: RoomSnapshot(room));
        }

        private static bool RoomNumberExists(Document document, string number)
        {
            string normalized = NormalizeOptionalText(number);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            return new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Any(room => string.Equals(GetRoomNumber(room), normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static FloorType ResolveFloorType(Document document, string floorTypeId)
        {
            if (!string.IsNullOrWhiteSpace(floorTypeId))
            {
                return ResolveElement(document, floorTypeId) as FloorType;
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault();
        }

        private static string ValidateFloorOutline(Document document, Level level, List<XYZ> rawPoints)
        {
            List<XYZ> points = NormalizeClosedLoop(rawPoints);
            if (points.Count < 3)
            {
                return "create_floor requires at least three unique outline points.";
            }

            double tolerance = Math.Max(document.Application.ShortCurveTolerance, 0.000001);
            for (int index = 0; index < points.Count; index++)
            {
                XYZ current = points[index];
                if (Math.Abs(current.Z - level.Elevation) > 0.000001)
                {
                    return "create_floor outline points must be on the target level elevation.";
                }

                XYZ next = points[(index + 1) % points.Count];
                if (current.DistanceTo(next) <= tolerance)
                {
                    return "create_floor outline has a segment shorter than Revit's minimum curve length.";
                }
            }

            double area = PolygonAreaInternal(points);
            if (area <= tolerance * tolerance)
            {
                return "create_floor outline area is too small.";
            }

            return null;
        }

        private static void SetRoomStringParameter(Room room, BuiltInParameter builtInParameter, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            Parameter parameter = room.get_Parameter(builtInParameter);
            if (parameter == null) throw new InvalidOperationException("Room parameter " + builtInParameter + " was not found.");
            if (parameter.IsReadOnly) throw new InvalidOperationException("Room parameter " + builtInParameter + " is read-only.");
            parameter.Set(value);
        }
    }
}
