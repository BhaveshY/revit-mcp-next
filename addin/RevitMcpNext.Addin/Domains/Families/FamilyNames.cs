using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Words ↔ Revit ids for family parameters (D3 §3.7): data types (SpecTypeId / family_type:&lt;Category&gt;) and
    /// properties groups (GroupTypeId; "Other" is the empty ForgeTypeId). English words resolve on every UI language;
    /// localized labels (LabelUtils) and TypeId tokens are accepted as fallbacks. Outputs use the English word when known.
    /// </summary>
    internal static class FamilyNames
    {
        /// <summary>The "Other" properties group (PG_INVALID): the empty ForgeTypeId.</summary>
        public static ForgeTypeId OtherGroup => new ForgeTypeId(string.Empty);

        private static readonly Lazy<List<KeyValuePair<string, ForgeTypeId>>> SpecWords = new Lazy<List<KeyValuePair<string, ForgeTypeId>>>(() =>
            new List<KeyValuePair<string, ForgeTypeId>>
            {
                Pair("text", SpecTypeId.String.Text),
                Pair("string", SpecTypeId.String.Text),
                Pair("multiline", SpecTypeId.String.MultilineText),
                Pair("multiline_text", SpecTypeId.String.MultilineText),
                Pair("length", SpecTypeId.Length),
                Pair("area", SpecTypeId.Area),
                Pair("volume", SpecTypeId.Volume),
                Pair("angle", SpecTypeId.Angle),
                Pair("slope", SpecTypeId.Slope),
                Pair("number", SpecTypeId.Number),
                Pair("double", SpecTypeId.Number),
                Pair("integer", SpecTypeId.Int.Integer),
                Pair("int", SpecTypeId.Int.Integer),
                Pair("yesno", SpecTypeId.Boolean.YesNo),
                Pair("yes_no", SpecTypeId.Boolean.YesNo),
                Pair("boolean", SpecTypeId.Boolean.YesNo),
                Pair("bool", SpecTypeId.Boolean.YesNo),
                Pair("material", SpecTypeId.Reference.Material),
                Pair("url", SpecTypeId.String.Url),
                Pair("image", SpecTypeId.Reference.Image),
                Pair("currency", SpecTypeId.Currency),
                Pair("mass", SpecTypeId.Mass)
            });

        private static readonly Lazy<List<KeyValuePair<string, ForgeTypeId>>> GroupWords = new Lazy<List<KeyValuePair<string, ForgeTypeId>>>(() =>
            new List<KeyValuePair<string, ForgeTypeId>>
            {
                Pair("Dimensions", GroupTypeId.Geometry),
                Pair("Constraints", GroupTypeId.Constraints),
                Pair("Identity Data", GroupTypeId.IdentityData),
                Pair("Materials and Finishes", GroupTypeId.Materials),
                Pair("Graphics", GroupTypeId.Graphics),
                Pair("Text", GroupTypeId.Text),
                Pair("Data", GroupTypeId.Data),
                Pair("Construction", GroupTypeId.Construction),
                Pair("General", GroupTypeId.General),
                Pair("Visibility", GroupTypeId.Visibility),
                Pair("Phasing", GroupTypeId.Phasing),
                Pair("Structural", GroupTypeId.Structural),
                Pair("Structural Analysis", GroupTypeId.StructuralAnalysis),
                Pair("Analysis Results", GroupTypeId.AnalysisResults),
                Pair("Electrical", GroupTypeId.Electrical),
                Pair("Electrical - Lighting", GroupTypeId.ElectricalLighting),
                Pair("Electrical - Loads", GroupTypeId.ElectricalLoads),
                Pair("Mechanical", GroupTypeId.Mechanical),
                Pair("Mechanical - Airflow", GroupTypeId.MechanicalAirflow),
                Pair("Mechanical - Loads", GroupTypeId.MechanicalLoads),
                Pair("Plumbing", GroupTypeId.Plumbing),
                Pair("Fire Protection", GroupTypeId.FireProtection),
                Pair("Life Safety", GroupTypeId.LifeSafety),
                Pair("Green Building Properties", GroupTypeId.GreenBuilding),
                Pair("Energy Analysis", GroupTypeId.EnergyAnalysis),
                Pair("IFC Parameters", GroupTypeId.Ifc),
                Pair("Model Properties", GroupTypeId.AdskModelProperties),
                Pair("Title Text", GroupTypeId.Title),
                Pair("Other", OtherGroup)
            });

        private static readonly Dictionary<string, string> GroupSynonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dimension"] = "Dimensions",
            ["geometry"] = "Dimensions",
            ["constraint"] = "Constraints",
            ["identity"] = "Identity Data",
            ["identitydata"] = "Identity Data",
            ["materials"] = "Materials and Finishes",
            ["material"] = "Materials and Finishes",
            ["materialsandfinishes"] = "Materials and Finishes",
            ["finishes"] = "Materials and Finishes",
            ["graphic"] = "Graphics",
            ["textdata"] = "Text",
            ["ifc"] = "IFC Parameters",
            ["green building"] = "Green Building Properties",
            ["model"] = "Model Properties",
            ["invalid"] = "Other",
            ["none"] = "Other",
            ["pg_invalid"] = "Other"
        };

        private static KeyValuePair<string, ForgeTypeId> Pair(string word, ForgeTypeId id) => new KeyValuePair<string, ForgeTypeId>(word, id);

        private static string Squash(string text) => (text ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

        // -------------------------------------------------------------------------------------------------------
        // Data types
        // -------------------------------------------------------------------------------------------------------

        /// <summary>A data word resolved: a spec, or a family-type category (family_type:&lt;Category&gt;).</summary>
        internal sealed class DataType
        {
            public ForgeTypeId Spec { get; set; }
            public Category FamilyTypeCategory { get; set; }
            public string Word { get; set; }
        }

        /// <summary>text, length, ..., family_type:&lt;Category&gt;; localized spec labels and spec TypeIds as fallbacks.</summary>
        public static DataType Data(Document famDoc, string value, string param)
        {
            string text = (value ?? "text").Trim();
            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                string head = Squash(text.Substring(0, colon));
                if (head == "familytype" || head == "family")
                {
                    string categoryName = text.Substring(colon + 1).Trim();
                    Category category = Resolve.Category(famDoc, categoryName, param);
                    return new DataType { FamilyTypeCategory = category, Word = "family_type:" + CategoryLabel(category) };
                }
            }

            string squashed = Squash(text);
            foreach (KeyValuePair<string, ForgeTypeId> pair in SpecWords.Value)
            {
                if (Squash(pair.Key) == squashed) return new DataType { Spec = pair.Value, Word = pair.Key };
            }

            // Localized label or TypeId of any spec ("Currency", "Electrical Potential", "autodesk.spec.aec:length-2.0.0").
            var labels = new List<string>();
            foreach (ForgeTypeId spec in SafeAllSpecs())
            {
                string label = SafeSpecLabel(spec);
                if (label != null) labels.Add(label);
                if (label != null && Squash(label) == squashed) return new DataType { Spec = spec, Word = label };
                if (string.Equals(spec.TypeId, text, StringComparison.OrdinalIgnoreCase) || Squash(TypeIdToken(spec.TypeId)) == squashed)
                {
                    return new DataType { Spec = spec, Word = label ?? spec.TypeId };
                }
            }
            var words = SpecWords.Value.Select(p => p.Key).Concat(new[] { "family_type:<Category>" });
            throw new OpException(ErrorCodes.InvalidArgs, param + ": unknown data type '" + value + "'; use one of " + string.Join(", ", SpecWords.Value.Select(p => p.Key).Distinct().Take(14)) + " or family_type:<Category>.",
                new Dictionary<string, object> { ["param"] = param, ["reason"] = "unknown data type", ["candidates"] = Resolve.Closest(text, words.Concat(labels)) });
        }

        /// <summary>English data word for a parameter definition (family_type:&lt;Category&gt; for family-type parameters).</summary>
        public static string DataLabel(Document famDoc, ForgeTypeId spec)
        {
            if (spec == null) return null;
            try
            {
                if (spec.Empty()) return "none";
                if (Category.IsBuiltInCategory(spec))
                {
                    BuiltInCategory bic = Category.GetBuiltInCategory(spec);
                    string english = EnglishAliases.LabelOf(bic);
                    return "family_type:" + (english ?? SafeCategoryName(famDoc, bic) ?? bic.ToString());
                }
            }
            catch
            {
                // Not a category spec.
            }
            foreach (KeyValuePair<string, ForgeTypeId> pair in SpecWords.Value)
            {
                if (pair.Value == spec) return pair.Key;
            }
            return SafeSpecLabel(spec) ?? spec.TypeId;
        }

        // -------------------------------------------------------------------------------------------------------
        // Groups
        // -------------------------------------------------------------------------------------------------------

        /// <summary>English group names (Dimensions, Identity Data, Other, ...), localized labels and TypeId tokens.</summary>
        public static ForgeTypeId Group(string value, string param)
        {
            if (string.IsNullOrWhiteSpace(value)) return OtherGroup;
            string text = value.Trim();
            if (GroupSynonyms.TryGetValue(text, out string canonical) || GroupSynonyms.TryGetValue(Squash(text), out canonical)) text = canonical;
            string squashed = Squash(text);
            foreach (KeyValuePair<string, ForgeTypeId> pair in GroupWords.Value)
            {
                if (Squash(pair.Key) == squashed) return pair.Value;
            }

            var labels = new List<string>(GroupWords.Value.Select(p => p.Key));
            foreach (ForgeTypeId group in SafeAllGroups())
            {
                string label = SafeGroupLabel(group);
                if (label != null)
                {
                    labels.Add(label);
                    if (Squash(label) == squashed) return group;
                }
                if (string.Equals(group.TypeId, text, StringComparison.OrdinalIgnoreCase) || Squash(TypeIdToken(group.TypeId)) == squashed) return group;
            }
            throw OpException.NotFound(param, "properties group", value, Resolve.Closest(value, labels));
        }

        /// <summary>English group name for output ("Other" for the empty group).</summary>
        public static string GroupLabel(ForgeTypeId group)
        {
            try
            {
                if (group == null || group.Empty()) return "Other";
            }
            catch
            {
                return "Other";
            }
            foreach (KeyValuePair<string, ForgeTypeId> pair in GroupWords.Value)
            {
                if (pair.Value == group) return pair.Key;
            }
            return SafeGroupLabel(group) ?? group.TypeId;
        }

        // -------------------------------------------------------------------------------------------------------
        // Categories
        // -------------------------------------------------------------------------------------------------------

        /// <summary>The stable English category label (localized name when no mapping exists).</summary>
        public static string CategoryLabel(Category category)
        {
            if (category == null) return null;
            return Rows.CategoryLabel(category) ?? category.Name;
        }

        // -------------------------------------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------------------------------------

        /// <summary>"autodesk.spec.aec:length-2.0.0" → "length".</summary>
        private static string TypeIdToken(string typeId)
        {
            if (string.IsNullOrEmpty(typeId)) return string.Empty;
            int colon = typeId.LastIndexOf(':');
            string token = colon >= 0 ? typeId.Substring(colon + 1) : typeId;
            int dash = token.LastIndexOf('-');
            return dash > 0 ? token.Substring(0, dash) : token;
        }

        private static IEnumerable<ForgeTypeId> SafeAllSpecs()
        {
            try { return SpecUtils.GetAllSpecs() ?? (IList<ForgeTypeId>)new List<ForgeTypeId>(); } catch { return new List<ForgeTypeId>(); }
        }

        private static IEnumerable<ForgeTypeId> SafeAllGroups()
        {
            try { return ParameterUtils.GetAllBuiltInGroups() ?? (IList<ForgeTypeId>)new List<ForgeTypeId>(); } catch { return new List<ForgeTypeId>(); }
        }

        private static string SafeSpecLabel(ForgeTypeId spec)
        {
            try { return LabelUtils.GetLabelForSpec(spec); } catch { return null; }
        }

        private static string SafeGroupLabel(ForgeTypeId group)
        {
            try { return LabelUtils.GetLabelForGroup(group); } catch { return null; }
        }

        private static string SafeCategoryName(Document doc, BuiltInCategory bic)
        {
            try { return doc == null ? null : Category.GetCategory(doc, bic)?.Name; } catch { return null; }
        }
    }
}
