using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Family parameter values through FamilyManager (D3 §3.7): wire conventions in and out (lengths mm, areas m²,
    /// volumes m³, angles degrees, yes/no true|false, text as is, materials/family types/images by name). Bare length
    /// numbers use the call's units (mm unless units: says otherwise), independent of the family's display units.
    /// </summary>
    internal static class FamilyValues
    {
        // -------------------------------------------------------------------------------------------------------
        // Lookup
        // -------------------------------------------------------------------------------------------------------

        /// <summary>The family parameter named <paramref name="name"/> (exact, then case-insensitive), else NOT_FOUND.</summary>
        public static FamilyParameter Param(FamilyManager fm, string name, string field)
        {
            if (string.IsNullOrWhiteSpace(name)) throw OpException.InvalidArgs(field, "is required (a family parameter name)");
            string text = name.Trim();
            FamilyParameter exact = null;
            try { exact = fm.get_Parameter(text); } catch { exact = null; }
            if (exact != null) return exact;
            List<FamilyParameter> all = All(fm);
            List<FamilyParameter> matches = all.Where(p => Resolve.Eq(NameOf(p), text)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) throw OpException.Ambiguous(field, "family parameter", name, matches.Select(NameOf));
            throw OpException.NotFound(field, "family parameter", name, Resolve.Closest(text, all.Select(NameOf)));
        }

        /// <summary>The parameter when present (exact or case-insensitive), else null.</summary>
        public static FamilyParameter Find(FamilyManager fm, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                FamilyParameter exact = fm.get_Parameter(name.Trim());
                if (exact != null) return exact;
            }
            catch
            {
                // Fall through to the scan.
            }
            return All(fm).FirstOrDefault(p => Resolve.Eq(NameOf(p), name));
        }

        /// <summary>Parameters in display order (GetParameters), else the unordered set.</summary>
        public static List<FamilyParameter> All(FamilyManager fm)
        {
            try
            {
                IList<FamilyParameter> ordered = fm.GetParameters();
                if (ordered != null && ordered.Count > 0) return ordered.ToList();
            }
            catch
            {
                // Older families may not support ordering; use the set.
            }
            var list = new List<FamilyParameter>();
            foreach (FamilyParameter parameter in fm.Parameters) list.Add(parameter);
            return list;
        }

        public static string NameOf(FamilyParameter parameter)
        {
            try { return parameter?.Definition?.Name ?? string.Empty; } catch { return string.Empty; }
        }

        /// <summary>Built-in family parameters (negative ids) cannot be removed or renamed.</summary>
        public static bool IsBuiltIn(FamilyParameter parameter)
        {
            try
            {
                if (parameter.Id != null && parameter.Id.Value < 0) return true;
                return parameter.Definition is InternalDefinition definition && definition.BuiltInParameter != BuiltInParameter.INVALID;
            }
            catch
            {
                return false;
            }
        }

        public static List<FamilyType> Types(FamilyManager fm)
        {
            var list = new List<FamilyType>();
            try
            {
                foreach (FamilyType type in fm.Types) list.Add(type);
            }
            catch
            {
                // No types.
            }
            return list;
        }

        public static string TypeName(FamilyType type)
        {
            try { return type?.Name ?? string.Empty; } catch { return string.Empty; }
        }

        /// <summary>The family type named <paramref name="name"/> (exact, then case-insensitive), else NOT_FOUND.</summary>
        public static FamilyType Type(FamilyManager fm, string name, string field)
        {
            List<FamilyType> types = Types(fm);
            string text = (name ?? string.Empty).Trim();
            if (text.Length == 0) throw OpException.InvalidArgs(field, "is required (a family type name)");
            FamilyType exact = types.FirstOrDefault(t => string.Equals(TypeName(t), text, StringComparison.Ordinal));
            if (exact != null) return exact;
            List<FamilyType> matches = types.Where(t => Resolve.Eq(TypeName(t), text)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) throw OpException.Ambiguous(field, "family type", name, matches.Select(TypeName));
            throw OpException.NotFound(field, "family type", name, Resolve.Closest(text, types.Select(TypeName)));
        }

        public static bool TypeExists(FamilyManager fm, string name)
        {
            return Types(fm).Any(t => Resolve.Eq(TypeName(t), name));
        }

        public static ForgeTypeId SpecOf(FamilyParameter parameter)
        {
            try { return parameter?.Definition?.GetDataType(); } catch { return null; }
        }

        public static bool IsYesNo(FamilyParameter parameter)
        {
            try
            {
                ForgeTypeId spec = SpecOf(parameter);
                return spec != null && !spec.Empty() && spec == SpecTypeId.Boolean.YesNo;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The category of a family-type parameter (null for other parameters).</summary>
        public static BuiltInCategory? FamilyTypeCategory(FamilyParameter parameter)
        {
            try
            {
                ForgeTypeId spec = SpecOf(parameter);
                if (spec != null && !spec.Empty() && Category.IsBuiltInCategory(spec)) return Category.GetBuiltInCategory(spec);
            }
            catch
            {
                // Not a category spec.
            }
            return null;
        }

        // -------------------------------------------------------------------------------------------------------
        // Output
        // -------------------------------------------------------------------------------------------------------

        /// <summary>The value of <paramref name="parameter"/> in <paramref name="type"/> in wire conventions (null when unset).</summary>
        public static object Get(Document famDoc, FamilyType type, FamilyParameter parameter)
        {
            try
            {
                if (type == null || parameter == null || !type.HasValue(parameter)) return null;
                switch (parameter.StorageType)
                {
                    case StorageType.Double:
                        double? number = type.AsDouble(parameter);
                        return number.HasValue ? Units.ToOutput(SpecOf(parameter), number.Value, famDoc) : null;
                    case StorageType.Integer:
                        int? integer = type.AsInteger(parameter);
                        if (!integer.HasValue) return null;
                        return IsYesNo(parameter) ? (object)(integer.Value != 0) : integer.Value;
                    case StorageType.String:
                        return type.AsString(parameter);
                    case StorageType.ElementId:
                        ElementId id = type.AsElementId(parameter);
                        if (id == null || id == ElementId.InvalidElementId) return null;
                        Element element = Resolve.SafeGet(famDoc, id);
                        if (element is ElementType elementType) return Resolve.FullTypeName(elementType);
                        return element?.Name ?? (object)id.Value;
                    default:
                        return type.AsValueString(parameter);
                }
            }
            catch
            {
                return null;
            }
        }

        // -------------------------------------------------------------------------------------------------------
        // Input
        // -------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Sets <paramref name="value"/> on the CURRENT family type. Formula-driven → PARAM_HAS_FORMULA; reporting →
        /// INVALID_ARGS; unparsable → INVALID_ARGS naming the expected unit. Runs inside a family-document transaction.
        /// </summary>
        public static void Set(Document famDoc, FamilyManager fm, FamilyParameter parameter, object value, double bareLengthToMm, string field)
        {
            string name = NameOf(parameter);
            if (parameter.IsDeterminedByFormula)
            {
                throw new OpException(ErrorCodes.ParamHasFormula, "'" + name + "' is driven by the formula '" + parameter.Formula + "'; clear the formula first (set_formula with formula '').",
                    new Dictionary<string, object> { ["param"] = name, ["formula"] = parameter.Formula });
            }
            if (parameter.IsReporting)
            {
                throw OpException.InvalidArgs(field, "'" + name + "' is a reporting parameter; its value comes from the geometry it labels");
            }
            if (parameter.IsReadOnly)
            {
                throw new OpException(ErrorCodes.NotEditable, "'" + name + "' is read-only in this family.",
                    new Dictionary<string, object> { ["param"] = name, ["reason"] = "read-only" });
            }

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    fm.Set(parameter, value == null ? string.Empty : Text(value));
                    return;
                case StorageType.Integer:
                    SetInteger(fm, parameter, value, name, field);
                    return;
                case StorageType.Double:
                    SetDouble(famDoc, fm, parameter, value, bareLengthToMm, name, field);
                    return;
                case StorageType.ElementId:
                    fm.Set(parameter, ToElementId(famDoc, parameter, value, name, field));
                    return;
                default:
                    throw OpException.InvalidArgs(field, "'" + name + "' has no settable value");
            }
        }

        private static void SetInteger(FamilyManager fm, FamilyParameter parameter, object value, string name, string field)
        {
            if (IsYesNo(parameter))
            {
                var reader = new PayloadReader(new Dictionary<string, object> { ["v"] = value }, null, 1.0);
                bool? flag;
                try { flag = reader.BoolOpt("v"); } catch (OpException) { flag = null; }
                if (!flag.HasValue) throw OpException.InvalidArgs(field, "'" + name + "' is Yes/No; use true or false, got " + PayloadReader.Describe(value));
                fm.Set(parameter, flag.Value ? 1 : 0);
                return;
            }
            if (PayloadReader.TryNumber(value, out double number) && Math.Abs(number - Math.Round(number)) < 1e-9)
            {
                fm.Set(parameter, (int)Math.Round(number));
                return;
            }
            if (value is string text && !string.IsNullOrWhiteSpace(text))
            {
                // Enumerated integers ("Yes", localized choices) go through Revit's own parser.
                try
                {
                    fm.SetValueString(parameter, text);
                    return;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Report below.
                }
                catch (ArgumentException)
                {
                    // Report below.
                }
            }
            throw OpException.InvalidArgs(field, "'" + name + "' needs a whole number, got " + PayloadReader.Describe(value));
        }

        private static void SetDouble(Document famDoc, FamilyManager fm, FamilyParameter parameter, object value, double bareLengthToMm, string name, string field)
        {
            ForgeTypeId spec = SpecOf(parameter);
            ValueKind kind = Units.KindOf(spec);
            if (PayloadReader.TryNumber(value, out double number))
            {
                fm.Set(parameter, Units.ToInternal(spec, kind == ValueKind.Length ? number * bareLengthToMm : number, famDoc));
                return;
            }

            string text = value as string;
            if (Wire.AsMap(value) is IDictionary<string, object> legacy && legacy.TryGetValue("value", out object raw))
            {
                string unit = legacy.TryGetValue("unit", out object unitValue) ? Convert.ToString(unitValue, CultureInfo.InvariantCulture) : null;
                text = Convert.ToString(raw, CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(unit) ? string.Empty : " " + unit);
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                switch (kind)
                {
                    case ValueKind.Length:
                        if (Units.TryParseLength(text, bareLengthToMm, out double mm)) { fm.Set(parameter, Units.MmToFt(mm)); return; }
                        break;
                    case ValueKind.Area:
                        if (Units.TryParseArea(text, out double m2)) { fm.Set(parameter, Units.M2ToFt2(m2)); return; }
                        break;
                    case ValueKind.Volume:
                        if (Units.TryParseVolume(text, out double m3)) { fm.Set(parameter, Units.M3ToFt3(m3)); return; }
                        break;
                    case ValueKind.Angle:
                        if (Units.TryParseAngle(text, out double degrees)) { fm.Set(parameter, Units.DegToRad(degrees)); return; }
                        break;
                }
                // Revit's display-unit parser ("3' 6\"", "12.5 L/s") in the family document's units.
                try
                {
                    fm.SetValueString(parameter, text);
                    return;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Report below.
                }
                catch (ArgumentException)
                {
                    // Report below.
                }
            }
            string expected = kind == ValueKind.Length ? "a length in mm (or \"3.5 m\")" :
                kind == ValueKind.Area ? "an area in m²" :
                kind == ValueKind.Volume ? "a volume in m³" :
                kind == ValueKind.Angle ? "an angle in degrees" : "a number";
            throw OpException.InvalidArgs(field, "'" + name + "' needs " + expected + ", got " + PayloadReader.Describe(value));
        }

        private static ElementId ToElementId(Document famDoc, FamilyParameter parameter, object value, string name, string field)
        {
            if (value == null) return ElementId.InvalidElementId;
            long? id = Wire.ToLong(value is string s ? s.Trim() : value);
            if (id.HasValue) return new ElementId(id.Value);
            string text = Text(value).Trim();
            if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("<none>", StringComparison.OrdinalIgnoreCase))
            {
                return ElementId.InvalidElementId;
            }

            ForgeTypeId spec = SpecOf(parameter);
            try
            {
                if (spec != null && spec == SpecTypeId.Reference.Material) return Resolve.Material(famDoc, text, field).Id;
                if (spec != null && spec == SpecTypeId.Reference.Image)
                {
                    List<ImageType> images = new FilteredElementCollector(famDoc).OfClass(typeof(ImageType)).Cast<ImageType>().ToList();
                    return Resolve.Single(images, i => i.Name, text, field, "image").Id;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Fall through to the generic lookups.
            }

            BuiltInCategory? category = FamilyTypeCategory(parameter);
            if (category.HasValue)
            {
                List<FamilySymbol> symbols = new FilteredElementCollector(famDoc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .Where(symbol => symbol.Category != null && Resolve.SafeBuiltIn(symbol.Category) == category.Value).ToList();
                FamilySymbol full = symbols.FirstOrDefault(symbol => Resolve.Eq(Resolve.FullTypeName(symbol), text));
                if (full != null) return full.Id;
                return Resolve.Single(symbols, symbol => symbol.Name, text, field, "nested family type", Resolve.FullTypeName).Id;
            }
            throw OpException.InvalidArgs(field, "'" + name + "' needs an element id or name, got '" + text + "'");
        }

        private static string Text(object value)
        {
            if (value is bool flag) return flag ? "true" : "false";
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
