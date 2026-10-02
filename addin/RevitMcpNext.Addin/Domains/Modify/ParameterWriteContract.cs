using System;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin.Revit
{
    internal static class ParameterWriteContract
    {
        internal static bool IsYesNoDataType(ForgeTypeId dataType)
        {
#if REVIT2021
            // Revit 2021 does not expose SpecTypeId.Boolean.YesNo. Callers must
            // use Definition.ParameterType for Yes/No detection in that host.
            return false;
#else
            return dataType != null && !dataType.Empty() && dataType == SpecTypeId.Boolean.YesNo;
#endif
        }

        internal static string GetForgeTypeIdString(ForgeTypeId typeId)
        {
            return typeId == null || typeId.Empty() ? null : typeId.TypeId;
        }

        internal static bool IsInternalUnitToken(string unit)
        {
            return string.Equals(unit, "revit-internal", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(unit, "internal", StringComparison.OrdinalIgnoreCase);
        }

        internal static ForgeTypeId ResolveUnitTypeId(string unit)
        {
            string normalized = (unit ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "mm":
                case "millimeter":
                case "millimeters":
                    return UnitTypeId.Millimeters;
                case "m":
                case "meter":
                case "meters":
                    return UnitTypeId.Meters;
                case "ft":
                case "foot":
                case "feet":
                    return UnitTypeId.Feet;
                case "in":
                case "inch":
                case "inches":
                    return UnitTypeId.Inches;
                default:
                    throw new InvalidOperationException("Unsupported unit token '" + (unit ?? string.Empty) + "'.");
            }
        }

        internal static void EnsureFinite(double value, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException(label + " must be finite.");
            }
        }
    }
}
