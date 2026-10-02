using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>How a parameter/spec value is exchanged on the wire.</summary>
    internal enum ValueKind
    {
        /// <summary>mm in and out.</summary>
        Length,
        /// <summary>m² in and out.</summary>
        Area,
        /// <summary>m³ in and out.</summary>
        Volume,
        /// <summary>Degrees in and out.</summary>
        Angle,
        /// <summary>Plain numbers (no unit, e.g. counts, factors).</summary>
        Number,
        /// <summary>Other measurable specs: document display units (flow, power, ...); output as display string.</summary>
        Other
    }

    /// <summary>
    /// Unit conventions (D1 §6, SPEC §5.4): lengths mm, areas m², volumes m³, angles degrees, slope percent; Revit
    /// internals are feet/radians. Parsing accepts "3.5 m", "12'6\"", "900mm", "45°", "2%". Output rounding per D1 §6.9:
    /// lengths 0.1 mm (integers when whole), areas 2 decimals, volumes 3, angles 0.1°.
    /// </summary>
    internal static class Units
    {
        public const double MmPerFoot = 304.8;
        public const double SquareMetersPerSquareFoot = 0.09290304;
        public const double CubicMetersPerCubicFoot = 0.028316846592;

        private static readonly Regex FeetInches = new Regex(
            "^\\s*(?<sign>-)?\\s*(?:(?<ft>\\d+(?:\\.\\d+)?)\\s*(?:'|ft|feet|foot))?\\s*-?\\s*(?:(?<in>\\d+(?:\\.\\d+)?)\\s*(?:\"|''|in|inch|inches))?\\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex NumberWithUnit = new Regex(
            "^\\s*(?<num>[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][-+]?\\d+)?)\\s*(?<unit>[a-zA-Z°%²³'\"]*)\\s*$",
            RegexOptions.CultureInvariant);

        public static double MmToFt(double mm) => mm / MmPerFoot;
        public static double FtToMm(double feet) => feet * MmPerFoot;
        public static double DegToRad(double degrees) => degrees * Math.PI / 180.0;
        public static double RadToDeg(double radians) => radians * 180.0 / Math.PI;
        /// <summary>ft² → m².</summary>
        public static double M2(double squareFeet) => squareFeet * SquareMetersPerSquareFoot;
        /// <summary>ft³ → m³.</summary>
        public static double M3(double cubicFeet) => cubicFeet * CubicMetersPerCubicFoot;
        public static double M2ToFt2(double squareMeters) => squareMeters / SquareMetersPerSquareFoot;
        public static double M3ToFt3(double cubicMeters) => cubicMeters / CubicMetersPerCubicFoot;

        /// <summary>mm per one input unit (mm, cm, m, in, ft and their spellings); null when unknown.</summary>
        public static double? LengthUnitToMm(string unit)
        {
            switch ((unit ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "mm":
                case "millimeter":
                case "millimeters":
                case "millimetre":
                case "millimetres":
                    return 1.0;
                case "cm":
                case "centimeter":
                case "centimeters":
                    return 10.0;
                case "dm":
                    return 100.0;
                case "m":
                case "meter":
                case "meters":
                case "metre":
                case "metres":
                    return 1000.0;
                case "in":
                case "inch":
                case "inches":
                case "\"":
                    return 25.4;
                case "ft":
                case "feet":
                case "foot":
                case "'":
                    return MmPerFoot;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Parses a length to mm. Bare numbers use <paramref name="bareUnitToMm"/> (1 = mm; the per-call units param).
        /// Accepts "3.5 m", "900mm", "12'6\"", "12' 6\"", "6\"", "1,5 m" (decimal comma).
        /// </summary>
        public static bool TryParseLength(string text, double bareUnitToMm, out double mm)
        {
            mm = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string normalized = NormalizeNumberText(text);
            Match match = NumberWithUnit.Match(normalized);
            if (match.Success)
            {
                double value = double.Parse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                string unit = match.Groups["unit"].Value;
                double? factor = unit.Length == 0 ? bareUnitToMm : LengthUnitToMm(unit);
                if (!factor.HasValue) return false;
                mm = value * factor.Value;
                return IsFinite(mm);
            }

            Match imperial = FeetInches.Match(normalized);
            if (imperial.Success && (imperial.Groups["ft"].Success || imperial.Groups["in"].Success))
            {
                double feet = imperial.Groups["ft"].Success ? double.Parse(imperial.Groups["ft"].Value, CultureInfo.InvariantCulture) : 0;
                double inches = imperial.Groups["in"].Success ? double.Parse(imperial.Groups["in"].Value, CultureInfo.InvariantCulture) : 0;
                mm = (feet * 12.0 + inches) * 25.4;
                if (imperial.Groups["sign"].Success) mm = -mm;
                return true;
            }
            return false;
        }

        /// <summary>Parses an angle to degrees: bare numbers are degrees; "45°", "45 deg", "0.785 rad", "50 grad".</summary>
        public static bool TryParseAngle(string text, out double degrees)
        {
            degrees = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            Match match = NumberWithUnit.Match(NormalizeNumberText(text));
            if (!match.Success) return false;
            double value = double.Parse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            switch (match.Groups["unit"].Value.ToLowerInvariant())
            {
                case "":
                case "°":
                case "deg":
                case "degree":
                case "degrees":
                    degrees = value;
                    return true;
                case "rad":
                case "radian":
                case "radians":
                    degrees = RadToDeg(value);
                    return true;
                case "grad":
                case "gon":
                    degrees = value * 0.9;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Parses a percentage ("2%", "2 %", or a bare number meaning percent).</summary>
        public static bool TryParsePercent(string text, out double percent)
        {
            percent = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            Match match = NumberWithUnit.Match(NormalizeNumberText(text));
            if (!match.Success) return false;
            string unit = match.Groups["unit"].Value;
            if (unit.Length > 0 && unit != "%") return false;
            percent = double.Parse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Parses an area to m² ("12", "12 m2", "12 m²", "120000 mm2", "100 ft2").</summary>
        public static bool TryParseArea(string text, out double squareMeters)
        {
            squareMeters = 0;
            Match match = string.IsNullOrWhiteSpace(text) ? Match.Empty : NumberWithUnit.Match(NormalizeNumberText(text));
            if (!match.Success) return false;
            double value = double.Parse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            switch (match.Groups["unit"].Value.ToLowerInvariant())
            {
                case "": case "m2": case "m²": case "sqm": squareMeters = value; return true;
                case "mm2": case "mm²": squareMeters = value / 1e6; return true;
                case "cm2": case "cm²": squareMeters = value / 1e4; return true;
                case "ft2": case "ft²": case "sqft": squareMeters = M2(value); return true;
                default: return false;
            }
        }

        /// <summary>Parses a volume to m³ ("3", "3 m3", "3 m³", "100 ft3", "1000 l").</summary>
        public static bool TryParseVolume(string text, out double cubicMeters)
        {
            cubicMeters = 0;
            Match match = string.IsNullOrWhiteSpace(text) ? Match.Empty : NumberWithUnit.Match(NormalizeNumberText(text));
            if (!match.Success) return false;
            double value = double.Parse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            switch (match.Groups["unit"].Value.ToLowerInvariant())
            {
                case "": case "m3": case "m³": cubicMeters = value; return true;
                case "l": case "liter": case "liters": case "litre": cubicMeters = value / 1000.0; return true;
                case "ft3": case "ft³": cubicMeters = M3(value); return true;
                default: return false;
            }
        }

        /// <summary>The wire convention for a spec (length/area/volume/angle use mm/m²/m³/deg; others use display units).</summary>
        public static ValueKind KindOf(ForgeTypeId spec)
        {
            if (spec == null || spec.Empty()) return ValueKind.Number;
            try
            {
                if (spec == SpecTypeId.Length) return ValueKind.Length;
                if (spec == SpecTypeId.Area) return ValueKind.Area;
                if (spec == SpecTypeId.Volume) return ValueKind.Volume;
                if (spec == SpecTypeId.Angle) return ValueKind.Angle;
                if (!UnitUtils.IsMeasurableSpec(spec)) return ValueKind.Number;
                if (UnitUtils.IsValidUnit(spec, UnitTypeId.Millimeters)) return ValueKind.Length;
                if (UnitUtils.IsValidUnit(spec, UnitTypeId.SquareMeters)) return ValueKind.Area;
                if (UnitUtils.IsValidUnit(spec, UnitTypeId.CubicMeters)) return ValueKind.Volume;
                if (UnitUtils.IsValidUnit(spec, UnitTypeId.Degrees)) return ValueKind.Angle;
                return ValueKind.Other;
            }
            catch
            {
                return ValueKind.Other;
            }
        }

        /// <summary>
        /// Converts a wire value (mm, m², m³, degrees; for Other specs the document display unit) to Revit internal units.
        /// </summary>
        public static double ToInternal(ForgeTypeId spec, double value, Document document = null)
        {
            switch (KindOf(spec))
            {
                case ValueKind.Length: return MmToFt(value);
                case ValueKind.Area: return M2ToFt2(value);
                case ValueKind.Volume: return M3ToFt3(value);
                case ValueKind.Angle: return DegToRad(value);
                case ValueKind.Other:
                    ForgeTypeId unit = DisplayUnit(spec, document);
                    return unit == null ? value : UnitUtils.ConvertToInternalUnits(value, unit);
                default: return value;
            }
        }

        /// <summary>
        /// Converts an internal value to the wire convention: rounded numbers for length/area/volume/angle/number, the
        /// document display string ("12.5 L/s") for other specs.
        /// </summary>
        public static object ToOutput(ForgeTypeId spec, double internalValue, Document document = null)
        {
            if (!IsFinite(internalValue)) return null;
            switch (KindOf(spec))
            {
                case ValueKind.Length: return Number(FtToMm(internalValue), 1);
                case ValueKind.Area: return Number(M2(internalValue), 2);
                case ValueKind.Volume: return Number(M3(internalValue), 3);
                case ValueKind.Angle: return Number(RadToDeg(internalValue), 1);
                case ValueKind.Number: return Number(internalValue, 6);
                default:
                    try
                    {
                        if (document != null) return UnitFormatUtils.Format(document.GetUnits(), spec, internalValue, false);
                    }
                    catch
                    {
                        // Fall back to the number.
                    }
                    return Number(internalValue, 6);
            }
        }

        /// <summary>The document's display unit for a spec (null when unavailable).</summary>
        public static ForgeTypeId DisplayUnit(ForgeTypeId spec, Document document)
        {
            try
            {
                return document?.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Rounds and returns a long when whole (no trailing zeros, no exponent on the wire).</summary>
        public static object Number(double value, int decimals)
        {
            if (!IsFinite(value)) return null;
            double rounded = Math.Round(value, Math.Max(0, Math.Min(10, decimals)), MidpointRounding.AwayFromZero);
            if (rounded == 0) return 0L;
            if (Math.Abs(rounded) < 9e15 && rounded == Math.Floor(rounded)) return (long)rounded;
            return rounded;
        }

        /// <summary>Internal feet → output mm (0.1 mm).</summary>
        public static object Mm(double feet) => Number(FtToMm(feet), 1);

        /// <summary>Internal ft² → output m² (2 decimals).</summary>
        public static object Area(double squareFeet) => Number(M2(squareFeet), 2);

        /// <summary>Internal ft³ → output m³ (3 decimals).</summary>
        public static object Volume(double cubicFeet) => Number(M3(cubicFeet), 3);

        /// <summary>Internal radians → output degrees (0.1°).</summary>
        public static object Angle(double radians) => Number(RadToDeg(radians), 1);

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Accepts a decimal comma ("1,5") when the text has no dot; strips thousands spaces.</summary>
        private static string NormalizeNumberText(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.IndexOf('.') < 0 && trimmed.Count(c => c == ',') == 1) trimmed = trimmed.Replace(',', '.');
            return trimmed.Replace(" ", " ");
        }

        private static int Count(this string text, Func<char, bool> predicate)
        {
            int count = 0;
            foreach (char character in text) if (predicate(character)) count++;
            return count;
        }
    }
}
