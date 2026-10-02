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
        private static double ToInternalElevation(Dictionary<string, object> elevation)
        {
            return ToInternalLength(elevation, "Elevation");
        }

        private static double ToInternalLength(Dictionary<string, object> unitValue, string fieldName)
        {
            if (unitValue == null) throw new ArgumentException(fieldName + " is required.");
            double value = GetDouble(unitValue, "value") ?? throw new ArgumentException(fieldName + " value is required.");
            string unit = (GetString(unitValue, "unit") ?? "mm").Trim().ToLowerInvariant();

            switch (unit)
            {
                case "mm":
                case "millimeters":
                    return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
                case "m":
                case "meters":
                    return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters);
                case "ft":
                case "feet":
                case "revit-internal":
                    return value;
                default:
                    throw new ArgumentException("Unsupported " + fieldName.ToLowerInvariant() + " unit: " + unit);
            }
        }

        private static double ToInternalAngle(Dictionary<string, object> angleValue)
        {
            if (angleValue == null) throw new ArgumentException("Angle is required.");
            double value = GetDouble(angleValue, "value") ?? throw new ArgumentException("Angle value is required.");
            string unit = (GetString(angleValue, "unit") ?? "degrees").Trim().ToLowerInvariant();

            switch (unit)
            {
                case "degrees":
                    return value * Math.PI / 180.0;
                case "radians":
                    return value;
                default:
                    throw new ArgumentException("Unsupported angle unit: " + unit);
            }
        }

        private static XYZ ToInternalPoint(Dictionary<string, object> point, string fieldName)
        {
            if (point == null) throw new ArgumentException(fieldName + " is required.");
            return new XYZ(
                ToInternalLength(GetDictionary(point, "x"), fieldName + ".x"),
                ToInternalLength(GetDictionary(point, "y"), fieldName + ".y"),
                ToInternalLength(GetDictionary(point, "z"), fieldName + ".z"));
        }

        private static XYZ ToInternalPlacementPoint(Dictionary<string, object> point, string fieldName, double? defaultZ)
        {
            if (point == null) throw new ArgumentException(fieldName + " is required.");

            double z;
            Dictionary<string, object> zValue = GetDictionary(point, "z");
            if (zValue != null)
            {
                z = ToInternalLength(zValue, fieldName + ".z");
            }
            else if (defaultZ.HasValue)
            {
                z = defaultZ.Value;
            }
            else
            {
                throw new ArgumentException(fieldName + ".z is required when no level elevation can be inferred.");
            }

            return new XYZ(
                ToInternalLength(GetDictionary(point, "x"), fieldName + ".x"),
                ToInternalLength(GetDictionary(point, "y"), fieldName + ".y"),
                z);
        }

        private static UV ToInternalUv(Dictionary<string, object> point, string fieldName)
        {
            if (point == null) throw new ArgumentException(fieldName + " is required.");
            return new UV(
                ToInternalLength(GetDictionary(point, "x"), fieldName + ".x"),
                ToInternalLength(GetDictionary(point, "y"), fieldName + ".y"));
        }

        private static XYZ ToInternalSheetPoint(Dictionary<string, object> point, string fieldName)
        {
            UV uv = ToInternalUv(point, fieldName);
            return new XYZ(uv.U, uv.V, 0);
        }

        private static List<XYZ> ToInternalPointList(IReadOnlyList<Dictionary<string, object>> points, string fieldName)
        {
            var result = new List<XYZ>();
            for (int index = 0; index < points.Count; index++)
            {
                result.Add(ToInternalPoint(points[index], fieldName + "[" + index.ToString(CultureInfo.InvariantCulture) + "]"));
            }

            return result;
        }

        private static Dictionary<string, object> LengthValue(double internalLength)
        {
            return UnitValue(UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Millimeters), "mm", "metric");
        }

        private static Dictionary<string, object> AreaValue(double internalArea)
        {
            double squareMeters = UnitUtils.ConvertFromInternalUnits(internalArea, UnitTypeId.SquareMeters);
            return UnitValue(squareMeters, "m2", "metric");
        }

        private static Dictionary<string, object> VolumeValue(double internalVolume)
        {
            double cubicMeters = UnitUtils.ConvertFromInternalUnits(internalVolume, UnitTypeId.CubicMeters);
            return UnitValue(cubicMeters, "m3", "metric");
        }

        private static Dictionary<string, object> AngleValue(double radians)
        {
            return new Dictionary<string, object>
            {
                ["value"] = Math.Round(radians * 180.0 / Math.PI, 6),
                ["unit"] = "degrees",
                ["radians"] = Math.Round(radians, 9)
            };
        }

        private static Dictionary<string, object> PointValue(XYZ point)
        {
            return new Dictionary<string, object>
            {
                ["x"] = LengthValue(point.X),
                ["y"] = LengthValue(point.Y),
                ["z"] = LengthValue(point.Z)
            };
        }

        private static Dictionary<string, object> Point2Value(UV point)
        {
            return new Dictionary<string, object>
            {
                ["x"] = LengthValue(point.U),
                ["y"] = LengthValue(point.V)
            };
        }

        private static object[] PointArrayValue(IEnumerable<XYZ> points)
        {
            return points.Select(PointValue).ToArray();
        }

        private static Dictionary<string, object> UnitValue(double value, string unit, string system)
        {
            return new Dictionary<string, object>
            {
                ["value"] = Math.Round(value, 6),
                ["unit"] = unit,
                ["system"] = system
            };
        }
    }
}
