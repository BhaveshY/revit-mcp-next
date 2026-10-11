using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Builds view-filter rules ("Param op value", ops = != &gt; &gt;= &lt; &lt;= contains startswith empty notempty) for
    /// ParameterFilterElement: the parameter must be filterable for all the filter's categories; values are typed from a
    /// sample element (lengths in mm, areas m², angles degrees) and fall back to the parameter definition.
    /// </summary>
    internal static class ViewFilterRules
    {
        private const double Epsilon = 1e-4;

        public static List<ElementId> Categories(Document doc, IEnumerable<string> names, string param)
        {
            ICollection<ElementId> filterable = ParameterFilterUtilities.GetAllFilterableCategories();
            var filterableSet = new HashSet<long>(filterable.Select(id => id.Value));
            var result = new List<ElementId>();
            foreach (string name in names)
            {
                Category category = Resolve.Category(doc, name, param);
                if (!filterableSet.Contains(category.Id.Value))
                {
                    throw OpException.InvalidArgs(param, "the category '" + Rows.CategoryLabel(category) + "' cannot be used in view filters");
                }
                if (result.All(id => id != category.Id)) result.Add(category.Id);
            }
            if (result.Count == 0) throw OpException.InvalidArgs(param, "is required (categories the filter applies to, e.g. [\"Doors\"])");
            return result;
        }

        /// <summary>The rules ANDed into one ElementParameterFilter.</summary>
        public static ElementFilter Build(Document doc, IList<ElementId> categories, IEnumerable<string> ruleTexts, string param, double bareLengthToMm = 1.0)
        {
            List<string> texts = ruleTexts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (texts.Count == 0) throw OpException.InvalidArgs(param, "is required (rules like 'Fire Rating = EI30')");
            ICollection<ElementId> common = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, categories);
            var rules = new List<FilterRule>();
            for (int index = 0; index < texts.Count; index++)
            {
                string field = param + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                WhereRule rule = ElementQuery.ParseWhere(texts[index], field);
                ElementId parameterId = FindParameter(doc, common, rule.Param, field);
                rules.Add(BuildRule(doc, categories, parameterId, rule, field, bareLengthToMm));
            }
            return new ElementParameterFilter(rules);
        }

        private static ElementId FindParameter(Document doc, ICollection<ElementId> filterable, string name, string field)
        {
            string text = (name ?? string.Empty).Trim();
            var exact = new List<ElementId>();
            var loose = new List<ElementId>();
            var labels = new List<string>();
            foreach (ElementId id in filterable)
            {
                foreach (string label in Labels(doc, id))
                {
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    if (string.Equals(label, text, StringComparison.Ordinal)) { if (!exact.Contains(id)) exact.Add(id); }
                    else if (string.Equals(label, text, StringComparison.OrdinalIgnoreCase)) { if (!loose.Contains(id)) loose.Add(id); }
                }
                string first = Labels(doc, id).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (first != null) labels.Add(first);
            }
            List<ElementId> matches = exact.Count > 0 ? exact : loose;
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                throw OpException.Ambiguous(field, "parameter", text, matches.Select(id => Labels(doc, id).FirstOrDefault() + " (" + id.Value.ToString(CultureInfo.InvariantCulture) + ")"));
            }
            throw OpException.NotFound(field, "filterable parameter", text, Resolve.Closest(text, labels));
        }

        /// <summary>English alias, localized label, bip: token and enum name of a built-in; the name of a project parameter.</summary>
        private static IEnumerable<string> Labels(Document doc, ElementId id)
        {
            if (id.Value < 0)
            {
                var bip = (BuiltInParameter)id.Value;
                string english = EnglishAliases.LabelOf(bip);
                if (english != null) yield return english;
                string localized = null;
                try { localized = LabelUtils.GetLabelFor(bip); } catch { localized = null; }
                if (localized != null) yield return localized;
                yield return "bip:" + bip;
                yield return bip.ToString();
                yield break;
            }
            Element element = doc.GetElement(id);
            if (element != null) yield return element.Name;
        }

        private static FilterRule BuildRule(Document doc, IList<ElementId> categories, ElementId id, WhereRule rule, string field, double bareLengthToMm)
        {
            if (rule.Op == "empty") return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(id);
            if (rule.Op == "notempty") return ParameterFilterRuleFactory.CreateHasValueParameterRule(id);

            Parameter sample = Sample(doc, categories, id);
            StorageType storage = sample?.StorageType ?? StorageOf(doc, id, rule.Value);
            ForgeTypeId spec = sample != null ? ParamValues.SpecOf(sample) : SpecOf(doc, id);
            switch (storage)
            {
                case StorageType.String:
                    switch (rule.Op)
                    {
                        case "=": return ParameterFilterRuleFactory.CreateEqualsRule(id, rule.Value);
                        case "!=": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, rule.Value);
                        case "contains": return ParameterFilterRuleFactory.CreateContainsRule(id, rule.Value);
                        case "startswith": return ParameterFilterRuleFactory.CreateBeginsWithRule(id, rule.Value);
                        case ">": return ParameterFilterRuleFactory.CreateGreaterRule(id, rule.Value);
                        case ">=": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, rule.Value);
                        case "<": return ParameterFilterRuleFactory.CreateLessRule(id, rule.Value);
                        case "<=": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, rule.Value);
                    }
                    break;
                case StorageType.Integer:
                {
                    int value = ParseInteger(sample, rule.Value, field);
                    switch (rule.Op)
                    {
                        case "=": return ParameterFilterRuleFactory.CreateEqualsRule(id, value);
                        case "!=": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, value);
                        case ">": return ParameterFilterRuleFactory.CreateGreaterRule(id, value);
                        case ">=": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, value);
                        case "<": return ParameterFilterRuleFactory.CreateLessRule(id, value);
                        case "<=": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, value);
                    }
                    break;
                }
                case StorageType.Double:
                {
                    double value = ParseDouble(doc, spec, rule.Value, field, bareLengthToMm);
                    switch (rule.Op)
                    {
                        case "=": return ParameterFilterRuleFactory.CreateEqualsRule(id, value, Epsilon);
                        case "!=": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, value, Epsilon);
                        case ">": return ParameterFilterRuleFactory.CreateGreaterRule(id, value, Epsilon);
                        case ">=": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, value, Epsilon);
                        case "<": return ParameterFilterRuleFactory.CreateLessRule(id, value, Epsilon);
                        case "<=": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, value, Epsilon);
                    }
                    break;
                }
                case StorageType.ElementId:
                {
                    ElementId value = ParseElementId(doc, rule.Value, field);
                    switch (rule.Op)
                    {
                        case "=": return ParameterFilterRuleFactory.CreateEqualsRule(id, value);
                        case "!=": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, value);
                    }
                    break;
                }
            }
            throw OpException.InvalidArgs(field, "the operator '" + rule.Op + "' does not work with the " + storage.ToString().ToLowerInvariant() + " parameter '" + rule.Param + "'");
        }

        private static Parameter Sample(Document doc, IList<ElementId> categories, ElementId id)
        {
            try
            {
                Definition definition = id.Value > 0 ? (doc.GetElement(id) as ParameterElement)?.GetDefinition() : null;
                var filter = new ElementMulticategoryFilter(categories);
                foreach (bool types in new[] { false, true })
                {
                    FilteredElementCollector collector = new FilteredElementCollector(doc).WherePasses(filter);
                    collector = types ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
                    int scanned = 0;
                    foreach (Element element in collector)
                    {
                        Parameter parameter = id.Value < 0 ? element.get_Parameter((BuiltInParameter)id.Value) : definition == null ? null : element.get_Parameter(definition);
                        if (parameter != null) return parameter;
                        if (++scanned > 300) break;
                    }
                }
            }
            catch
            {
                // No sample: type the value from the definition.
            }
            return null;
        }

        private static StorageType StorageOf(Document doc, ElementId id, string value)
        {
            try
            {
                if (id.Value < 0) return doc.get_TypeOfStorage((BuiltInParameter)id.Value);
            }
            catch
            {
                // Fall through.
            }
            ForgeTypeId spec = SpecOf(doc, id);
            if (spec != null && !spec.Empty())
            {
                if (spec == SpecTypeId.String.Text || spec == SpecTypeId.String.MultilineText || spec == SpecTypeId.String.Url) return StorageType.String;
                if (spec == SpecTypeId.Boolean.YesNo || spec == SpecTypeId.Int.Integer) return StorageType.Integer;
                if (UnitUtils.IsMeasurableSpec(spec)) return StorageType.Double;
            }
            return PayloadReader.TryNumber(value, out _) ? StorageType.Double : StorageType.String;
        }

        private static ForgeTypeId SpecOf(Document doc, ElementId id)
        {
            try
            {
                if (id.Value > 0 && doc.GetElement(id) is ParameterElement element) return element.GetDefinition()?.GetDataType();
            }
            catch
            {
                // Unknown spec.
            }
            return null;
        }

        private static int ParseInteger(Parameter sample, string value, string field)
        {
            if (sample != null && ParamValues.IsYesNo(sample))
            {
                bool? flag = new PayloadReader(new Dictionary<string, object> { ["v"] = value }, null, 1.0).BoolOpt("v");
                if (!flag.HasValue) throw OpException.InvalidArgs(field, "is a Yes/No parameter; use true or false");
                return flag.Value ? 1 : 0;
            }
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return number;
            string lower = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (lower == "yes" || lower == "true") return 1;
            if (lower == "no" || lower == "false") return 0;
            throw OpException.InvalidArgs(field, "needs a whole number, got '" + value + "'");
        }

        /// <summary>A rule value in internal units; bare lengths are in the call's units (<paramref name="bareLengthToMm"/>).</summary>
        private static double ParseDouble(Document doc, ForgeTypeId spec, string value, string field, double bareLengthToMm)
        {
            string text = (value ?? string.Empty).Trim();
            switch (Units.KindOf(spec))
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
                default:
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return Units.ToInternal(spec, number, doc);
                    break;
            }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw)) return Units.ToInternal(spec, raw, doc);
            throw OpException.InvalidArgs(field, "needs a number (lengths in mm, areas in m²), got '" + value + "'");
        }

        private static ElementId ParseElementId(Document doc, string value, string field)
        {
            long? raw = RevitMcpNext.Contracts.Wire.ToLong(value);
            if (raw.HasValue) return new ElementId(raw.Value);
            foreach (Type type in new[] { typeof(Level), typeof(Phase), typeof(Material) })
            {
                Element match = new FilteredElementCollector(doc).OfClass(type).FirstOrDefault(e => Resolve.Eq(e.Name, value));
                if (match != null) return match.Id;
            }
            throw OpException.InvalidArgs(field, "needs an element id, or the name of a level, phase or material, got '" + value + "'");
        }
    }
}
