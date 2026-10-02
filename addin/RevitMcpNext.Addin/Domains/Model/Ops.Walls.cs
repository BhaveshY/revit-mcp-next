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
        private static Dictionary<string, object> PreviewCreateWall(Document document, Dictionary<string, object> operation, int index)
        {
            string levelId = GetString(operation, "levelId");
            Dictionary<string, object> startValue = GetDictionary(operation, "start");
            Dictionary<string, object> endValue = GetDictionary(operation, "end");
            if (string.IsNullOrWhiteSpace(levelId)) return BlockedChange(operation, index, "create_wall requires levelId.");
            if (startValue == null) return BlockedChange(operation, index, "create_wall requires start.");
            if (endValue == null) return BlockedChange(operation, index, "create_wall requires end.");

            Level level = ResolveElement(document, levelId) as Level;
            if (level == null) return BlockedChange(operation, index, "Level " + levelId + " was not found.");

            string wallTypeId = GetString(operation, "wallTypeId");
            WallType wallType = null;
            if (!string.IsNullOrWhiteSpace(wallTypeId))
            {
                wallType = ResolveElement(document, wallTypeId) as WallType;
                if (wallType == null) return BlockedChange(operation, index, "Wall type " + wallTypeId + " was not found.");
            }

            XYZ start;
            XYZ end;
            double? height = null;
            try
            {
                start = ToInternalPoint(startValue, "start");
                end = ToInternalPoint(endValue, "end");
                Dictionary<string, object> heightValue = GetDictionary(operation, "height");
                if (heightValue != null)
                {
                    height = ToInternalLength(heightValue, "height");
                    if (height.Value <= 0) return BlockedChange(operation, index, "create_wall height must be greater than zero.");
                }
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string geometryError = ValidateWallBaseline(document, start, end);
            if (!string.IsNullOrWhiteSpace(geometryError)) return BlockedChange(operation, index, geometryError);

            bool structural = GetBool(operation, "structural", false);
            bool flip = GetBool(operation, "flip", false);
            double baseOffset = start.Z - level.Elevation;
            var target = new Dictionary<string, object>
            {
                ["document"] = document.Title,
                ["levelId"] = ToElementIdString(level.Id),
                ["levelName"] = level.Name
            };
            if (wallType != null)
            {
                target["wallTypeId"] = ToElementIdString(wallType.Id);
                target["wallTypeName"] = SafeElementName(wallType);
            }

            var after = new Dictionary<string, object>
            {
                ["levelId"] = ToElementIdString(level.Id),
                ["start"] = PointValue(start),
                ["end"] = PointValue(end),
                ["length"] = LengthValue(start.DistanceTo(end)),
                ["baseOffset"] = LengthValue(baseOffset),
                ["structural"] = structural,
                ["flip"] = flip
            };
            if (wallType != null) after["wallTypeId"] = ToElementIdString(wallType.Id);
            if (height.HasValue) after["height"] = LengthValue(height.Value);

            return Change(operation, index, "ready", target, before: null, after: after);
        }

        private static Dictionary<string, object> ApplyCreateWall(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateWall(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_wall preview failed.");
            }

            Level level = ResolveElement(document, GetString(operation, "levelId")) as Level;
            XYZ start = ToInternalPoint(GetDictionary(operation, "start"), "start");
            XYZ end = ToInternalPoint(GetDictionary(operation, "end"), "end");
            bool structural = GetBool(operation, "structural", false);
            bool flip = GetBool(operation, "flip", false);
            Wall wall = Wall.Create(document, Line.CreateBound(start, end), level.Id, structural);

            string wallTypeId = GetString(operation, "wallTypeId");
            if (!string.IsNullOrWhiteSpace(wallTypeId))
            {
                WallType wallType = ResolveElement(document, wallTypeId) as WallType;
                ElementId changedId = wall.ChangeTypeId(wallType.Id);
                if (IsValidElementId(changedId) &&
                    !string.Equals(ToElementIdString(changedId), ToElementIdString(wall.Id), StringComparison.Ordinal))
                {
                    Wall changedWall = document.GetElement(changedId) as Wall;
                    if (changedWall != null) wall = changedWall;
                }
            }

            SetWallDoubleParameter(wall, BuiltInParameter.WALL_BASE_OFFSET, start.Z - level.Elevation, "base offset");

            Dictionary<string, object> heightValue = GetDictionary(operation, "height");
            if (heightValue != null)
            {
                SetWallDoubleParameter(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM, ToInternalLength(heightValue, "height"), "height");
            }

            if (flip)
            {
                wall.Flip();
            }

            return Change(operation, index, "applied",
                target: ElementTarget(wall, null),
                before: null,
                after: WallSnapshot(wall));
        }

        private static string ValidateWallBaseline(Document document, XYZ start, XYZ end)
        {
            if (Math.Abs(start.Z - end.Z) > 0.000001)
            {
                return "create_wall start and end must have the same z elevation.";
            }

            double length = start.DistanceTo(end);
            double minimumLength = Math.Max(document.Application.ShortCurveTolerance, 0.000001);
            if (length <= minimumLength)
            {
                return "create_wall baseline is shorter than Revit's minimum curve length.";
            }

            return null;
        }

        private static string ValidateWallFamilyInstanceHost(Document document, Wall wall)
        {
            if (wall == null) return "hostElementId must reference a Wall.";

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve?.Curve == null)
            {
                return "Host wall " + ToElementIdString(wall.Id) + " does not expose a valid location curve.";
            }

            WallType wallType = document.GetElement(wall.GetTypeId()) as WallType;
            if (wallType != null && wallType.Kind == WallKind.Curtain)
            {
                return "Curtain wall hosts are not supported by place_family_instance for door/window family placement.";
            }

            return null;
        }

        private static void SetWallDoubleParameter(Wall wall, BuiltInParameter builtInParameter, double value, string parameterName)
        {
            Parameter parameter = wall.get_Parameter(builtInParameter);
            if (parameter == null) throw new InvalidOperationException("Wall " + parameterName + " parameter was not found.");
            if (parameter.IsReadOnly) throw new InvalidOperationException("Wall " + parameterName + " parameter is read-only.");
            parameter.Set(value);
        }
    }
}
