using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Parameter access with the SPEC §9.7 name rules and §5.4 units. Keys: English label (any UI language),
    /// localized display name, "bip:ALL_MODEL_MARK", "guid:&lt;guid&gt;", "id:&lt;parameter id&gt;", or a disambiguated
    /// "Name [bip:X]" / "Name [guid:...]". Values: lengths mm, areas m², volumes m³, angles degrees, Yes/No booleans,
    /// ElementId params as ids (names accepted when setting levels, phases, materials and same-class elements);
    /// other measurable specs use the document's display units ("12.5 L/s" on output).
    /// </summary>
    internal static class ParamValues
    {
        /// <summary>The parameter for <paramref name="key"/>, or null.</summary>
        public static Parameter Resolve(Element element, string key)
        {
            if (element == null || string.IsNullOrWhiteSpace(key)) return null;
            string text = key.Trim();

            int bracket = text.LastIndexOf(" [", StringComparison.Ordinal);
            if (bracket > 0 && text.EndsWith("]", StringComparison.Ordinal))
            {
                Parameter qualified = Resolve(element, text.Substring(bracket + 2, text.Length - bracket - 3));
                if (qualified != null) return qualified;
                text = text.Substring(0, bracket).Trim();
            }

            if (text.StartsWith("bip:", StringComparison.OrdinalIgnoreCase))
            {
                return Enum.TryParse(text.Substring(4).Trim(), true, out BuiltInParameter bip) ? SafeGet(element, bip) : null;
            }
            if (text.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                return Guid.TryParse(text.Substring(5).Trim(), out Guid guid) ? SafeGet(element, guid) : null;
            }
            if (text.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                long? id = Wire.ToLong(text.Substring(3).Trim());
                if (!id.HasValue) return null;
                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter?.Id != null && parameter.Id.Value == id.Value) return parameter;
                }
                return null;
            }

            // English label first: with several built-ins per label, the one present on the element wins.
            foreach (BuiltInParameter bip in EnglishAliases.Parameters(text))
            {
                Parameter parameter = SafeGet(element, bip);
                if (parameter != null) return parameter;
            }

            Parameter localized = null;
            try { localized = element.LookupParameter(text); } catch { }
            if (localized != null) return localized;

            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter?.Definition != null && string.Equals(parameter.Definition.Name, text, StringComparison.OrdinalIgnoreCase)) return parameter;
            }

            if (Enum.TryParse(text, true, out BuiltInParameter token) && Enum.IsDefined(typeof(BuiltInParameter), token))
            {
                return SafeGet(element, token);
            }
            return null;
        }

        /// <summary>Like <see cref="Resolve"/> but throws NOT_FOUND with the element's parameter names as candidates.</summary>
        public static Parameter Require(Element element, string key, string param = "param")
        {
            Parameter parameter = Resolve(element, key);
            if (parameter != null) return parameter;
            IEnumerable<string> names = element.Parameters.Cast<Parameter>().Where(p => p?.Definition != null).Select(KeyOf);
            throw OpException.NotFound(param, "parameter", key, RevitMcpNext.Addin.Resolve.Closest(key, names));
        }

        /// <summary>English label for built-ins when known, else the display name.</summary>
        public static string KeyOf(Parameter parameter)
        {
            if (parameter?.Definition == null) return string.Empty;
            BuiltInParameter? bip = BuiltInOf(parameter);
            if (bip.HasValue)
            {
                string english = EnglishAliases.LabelOf(bip.Value);
                if (!string.IsNullOrEmpty(english)) return english;
            }
            return parameter.Definition.Name;
        }

        public static BuiltInParameter? BuiltInOf(Parameter parameter)
        {
            try
            {
                if (parameter?.Definition is InternalDefinition definition && definition.BuiltInParameter != BuiltInParameter.INVALID)
                {
                    return definition.BuiltInParameter;
                }
            }
            catch
            {
                // Not a built-in parameter.
            }
            return null;
        }

        public static ForgeTypeId SpecOf(Parameter parameter)
        {
            try { return parameter?.Definition?.GetDataType(); } catch { return null; }
        }

        public static bool IsYesNo(Parameter parameter)
        {
            ForgeTypeId spec = SpecOf(parameter);
            try
            {
                return spec != null && !spec.Empty() && spec == SpecTypeId.Boolean.YesNo;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The value in wire conventions (null when the parameter has no value).</summary>
        public static object Get(Parameter parameter, Document doc = null)
        {
            if (parameter == null) return null;
            try
            {
                if (!parameter.HasValue) return null;
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString();
                    case StorageType.Integer:
                        return IsYesNo(parameter) ? (object)(parameter.AsInteger() != 0) : parameter.AsInteger();
                    case StorageType.Double:
                        return Units.ToOutput(SpecOf(parameter), parameter.AsDouble(), doc ?? parameter.Element?.Document);
                    case StorageType.ElementId:
                        ElementId id = parameter.AsElementId();
                        return id == null || id == ElementId.InvalidElementId ? null : (object)id.Value;
                    default:
                        return parameter.AsValueString();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Revit's display text of the value (AsValueString, else AsString).</summary>
        public static string Display(Parameter parameter)
        {
            try
            {
                return parameter?.AsValueString() ?? parameter?.AsString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets a value with spec conversion. Bare numbers on lengths are mm (scaled by <paramref name="bareLengthToMm"/>,
        /// never feet). Throws INVALID_ARGS for read-only parameters or unparsable values, REVIT_REFUSED when Revit rejects it.
        /// Must run inside a transaction.
        /// </summary>
        public static void Set(Parameter parameter, object value, double bareLengthToMm = 1.0, string param = "value")
        {
            if (parameter == null) throw OpException.InvalidArgs(param, "parameter not found");
            string name = KeyOf(parameter);
            if (parameter.IsReadOnly)
            {
                throw OpException.InvalidArgs(param, "'" + name + "' is read-only");
            }

            bool ok;
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    ok = parameter.Set(value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case StorageType.Integer:
                    ok = parameter.Set(ToInteger(parameter, value, name, param));
                    break;
                case StorageType.Double:
                    ok = parameter.Set(ToInternalDouble(parameter, value, bareLengthToMm, name, param));
                    break;
                case StorageType.ElementId:
                    ok = parameter.Set(ToElementId(parameter, value, name, param));
                    break;
                default:
                    throw OpException.InvalidArgs(param, "'" + name + "' cannot be set");
            }
            if (!ok)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit refused the value " + PayloadReader.Describe(value) + " for '" + name + "'.",
                    new Dictionary<string, object> { ["apiMessage"] = "Parameter.Set returned false", ["param"] = name });
            }
        }

        private static int ToInteger(Parameter parameter, object value, string name, string param)
        {
            if (IsYesNo(parameter))
            {
                var reader = new PayloadReader(new Dictionary<string, object> { ["v"] = value }, null, 1.0);
                bool? flag;
                try { flag = reader.BoolOpt("v"); } catch (OpException) { flag = null; }
                if (!flag.HasValue) throw OpException.InvalidArgs(param, "'" + name + "' is Yes/No; use true or false");
                return flag.Value ? 1 : 0;
            }
            if (PayloadReader.TryNumber(value, out double number) && Math.Abs(number - Math.Round(number)) < 1e-9) return (int)Math.Round(number);
            throw OpException.InvalidArgs(param, "'" + name + "' needs a whole number, got " + PayloadReader.Describe(value));
        }

        private static double ToInternalDouble(Parameter parameter, object value, double bareLengthToMm, string name, string param)
        {
            ForgeTypeId spec = SpecOf(parameter);
            ValueKind kind = Units.KindOf(spec);
            Document doc = parameter.Element?.Document;

            if (PayloadReader.TryNumber(value, out double number))
            {
                return Units.ToInternal(spec, kind == ValueKind.Length ? number * bareLengthToMm : number, doc);
            }

            string text = value as string;
            if (Wire.AsMap(value) is IDictionary<string, object> legacy && legacy.TryGetValue("value", out object raw))
            {
                string unit = legacy.TryGetValue("unit", out object unitValue) ? Convert.ToString(unitValue, CultureInfo.InvariantCulture) : null;
                text = Convert.ToString(raw, CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(unit) ? string.Empty : " " + unit);
            }
            if (text != null)
            {
                switch (kind)
                {
                    case ValueKind.Length:
                        if (Units.TryParseLength(text, bareLengthToMm, out double mm)) return Units.MmToFt(mm);
                        break;
                    case ValueKind.Area:
                        if (Units.TryParseArea(text, out double m2)) return Units.M2ToFt2(m2);
                        break;
                    case ValueKind.Volume:
                        if (Units.TryParseVolume(text, out double m3)) return Units.M3ToFt3(m3);
                        break;
                    case ValueKind.Angle:
                        if (Units.TryParseAngle(text, out double degrees)) return Units.DegToRad(degrees);
                        break;
                }
                try
                {
                    if (doc != null && spec != null && UnitFormatUtils.TryParse(doc.GetUnits(), spec, text, out double parsed)) return parsed;
                }
                catch
                {
                    // Fall through to the error.
                }
            }
            string expected = kind == ValueKind.Length ? "a length in mm (or \"3.5 m\")" :
                kind == ValueKind.Area ? "an area in m²" :
                kind == ValueKind.Volume ? "a volume in m³" :
                kind == ValueKind.Angle ? "an angle in degrees" : "a number";
            throw OpException.InvalidArgs(param, "'" + name + "' needs " + expected + ", got " + PayloadReader.Describe(value));
        }

        private static ElementId ToElementId(Parameter parameter, object value, string name, string param)
        {
            if (value == null) return ElementId.InvalidElementId;
            long? id = Wire.ToLong(value is string s ? s.Trim() : value);
            if (id.HasValue) return new ElementId(id.Value);
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("<none>", StringComparison.OrdinalIgnoreCase))
            {
                return ElementId.InvalidElementId;
            }

            Document doc = parameter.Element?.Document;
            if (doc == null) throw OpException.InvalidArgs(param, "'" + name + "' needs an element id");
            BuiltInParameter? bip = BuiltInOf(parameter);
            if (bip.HasValue)
            {
                switch (bip.Value)
                {
                    case BuiltInParameter.PHASE_CREATED:
                    case BuiltInParameter.PHASE_DEMOLISHED:
                        return RevitMcpNext.Addin.Resolve.Phase(doc, text, param).Id;
                }
            }

            ElementId current = null;
            try { current = parameter.AsElementId(); } catch { }
            Element currentElement = current == null ? null : RevitMcpNext.Addin.Resolve.SafeGet(doc, current);
            if (currentElement is Level || IsLevelParameter(bip, parameter)) return RevitMcpNext.Addin.Resolve.Level(doc, text, param).Id;
            if (currentElement is Material || IsMaterialParameter(parameter)) return RevitMcpNext.Addin.Resolve.Material(doc, text, param).Id;
            if (currentElement is Phase) return RevitMcpNext.Addin.Resolve.Phase(doc, text, param).Id;
            if (currentElement != null)
            {
                Type type = currentElement.GetType();
                List<Element> sameClass = new FilteredElementCollector(doc).OfClass(type).ToElements().ToList();
                return RevitMcpNext.Addin.Resolve.Single(sameClass, e => e.Name, text, param, type.Name).Id;
            }
            throw OpException.InvalidArgs(param, "'" + name + "' needs an element id, got '" + text + "'");
        }

        private static bool IsLevelParameter(BuiltInParameter? bip, Parameter parameter)
        {
            if (bip.HasValue)
            {
                string token = bip.Value.ToString();
                if (token.Contains("LEVEL") || token == "WALL_BASE_CONSTRAINT" || token == "WALL_HEIGHT_TYPE" ||
                    token == "FAMILY_BASE_LEVEL_PARAM" || token == "FAMILY_TOP_LEVEL_PARAM")
                {
                    return true;
                }
            }
            string name = parameter.Definition?.Name ?? string.Empty;
            return name.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Constraint", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMaterialParameter(Parameter parameter)
        {
            try
            {
                ForgeTypeId spec = SpecOf(parameter);
                return spec != null && spec == SpecTypeId.Reference.Material;
            }
            catch
            {
                return false;
            }
        }

        private static Parameter SafeGet(Element element, BuiltInParameter bip)
        {
            try { return element.get_Parameter(bip); } catch { return null; }
        }

        private static Parameter SafeGet(Element element, Guid guid)
        {
            try { return element.get_Parameter(guid); } catch { return null; }
        }
    }
}
