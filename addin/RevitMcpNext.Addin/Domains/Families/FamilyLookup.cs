using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Finding loaded families (family= : name, id, "Family: Type") and the facts edit_family/read_family report about
    /// them: editability, project-side types and how many instances use each type.
    /// </summary>
    internal static class FamilyLookup
    {
        /// <summary>A resolved family= value: the family and, for "Family: Type", the type part.</summary>
        internal sealed class Found
        {
            public Family Family { get; set; }
            /// <summary>The type named after "Family: " (null when the value named only the family).</summary>
            public string TypeName { get; set; }
        }

        /// <summary>
        /// The loaded family named <paramref name="value"/> in <paramref name="doc"/>: element id (of the family or of one
        /// of its types), exact name, case-insensitive name, then "Family: Type". NOT_FOUND/AMBIGUOUS_NAME otherwise.
        /// </summary>
        public static Found Find(Document doc, string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) throw OpException.InvalidArgs(field, "is required (a loaded family name or id)");
            string text = value.Trim();

            long? id = Wire.ToLong(text);
            if (id.HasValue)
            {
                Element element = Resolve.SafeGet(doc, new ElementId(id.Value));
                if (element is Family byId) return new Found { Family = byId };
                if (element is FamilySymbol symbol && symbol.Family != null) return new Found { Family = symbol.Family, TypeName = symbol.Name };
                if (element is FamilyInstance instance && instance.Symbol?.Family != null) return new Found { Family = instance.Symbol.Family, TypeName = instance.Symbol.Name };
            }

            List<Family> families = All(doc);
            Family exact = families.FirstOrDefault(f => string.Equals(SafeName(f), text, StringComparison.Ordinal));
            if (exact != null) return new Found { Family = exact };
            List<Family> loose = families.Where(f => Resolve.Eq(SafeName(f), text)).ToList();
            if (loose.Count == 1) return new Found { Family = loose[0] };
            if (loose.Count > 1) throw OpException.Ambiguous(field, "family", value, loose.Select(f => SafeName(f) + " (id " + f.Id.Value + ")"));

            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                string familyPart = text.Substring(0, colon).Trim();
                string typePart = text.Substring(colon + 1).Trim();
                Family byPrefix = families.FirstOrDefault(f => Resolve.Eq(SafeName(f), familyPart));
                if (byPrefix != null) return new Found { Family = byPrefix, TypeName = typePart.Length == 0 ? null : typePart };
            }

            IEnumerable<string> names = families
                .OrderBy(f => SafeEditable(f) ? 0 : 1)
                .Select(SafeName);
            throw OpException.NotFound(field, "family", value, Resolve.Closest(text, names));
        }

        /// <summary>Loadable families of the document (no system families exist as Family elements).</summary>
        public static List<Family> All(Document doc)
        {
            try
            {
                return new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            }
            catch
            {
                return new List<Family>();
            }
        }

        /// <summary>NOT_EDITABLE_FAMILY for in-place, protected or otherwise non-editable families.</summary>
        public static void EnsureEditable(Family family)
        {
            string reason = null;
            try
            {
                if (family.IsInPlace) reason = "in-place family";
                else if (!family.IsEditable) reason = "Revit does not allow editing this family (protected or not saveable)";
            }
            catch
            {
                reason = "Revit cannot report whether this family is editable";
            }
            if (reason == null) return;
            throw new OpException(ErrorCodes.NotEditableFamily, "Family '" + SafeName(family) + "' cannot be edited: " + reason + ".",
                new Dictionary<string, object> { ["family"] = SafeName(family), ["reason"] = reason });
        }

        public static bool SafeEditable(Family family)
        {
            try { return family != null && family.IsEditable && !family.IsInPlace; } catch { return false; }
        }

        public static string SafeName(Family family)
        {
            try { return family?.Name ?? string.Empty; } catch { return string.Empty; }
        }

        /// <summary>The family a family document defines (title without .rfa when the owner family has no name).</summary>
        public static string FamilyNameOf(Document famDoc)
        {
            if (famDoc == null) return string.Empty;
            try
            {
                string name = famDoc.OwnerFamily?.Name;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch
            {
                // Fall back to the title.
            }
            return StripRfa(SafeTitle(famDoc));
        }

        public static string StripRfa(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            return title.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? title.Substring(0, title.Length - 4) : title;
        }

        public static string SafeTitle(Document doc)
        {
            try { return doc?.Title ?? string.Empty; } catch { return string.Empty; }
        }

        /// <summary>The family's types in the document: [id, name] in name order.</summary>
        public static List<FamilySymbol> Symbols(Document doc, Family family)
        {
            var list = new List<FamilySymbol>();
            if (family == null) return list;
            try
            {
                foreach (ElementId id in family.GetFamilySymbolIds())
                {
                    if (Resolve.SafeGet(doc, id) is FamilySymbol symbol) list.Add(symbol);
                }
            }
            catch
            {
                // No types readable.
            }
            return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Placed instances per type id of <paramref name="family"/> (one collector pass over its category).</summary>
        public static Dictionary<long, int> InstanceCounts(Document doc, Family family)
        {
            var counts = new Dictionary<long, int>();
            if (doc == null || family == null) return counts;
            try
            {
                var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance));
                ElementId categoryId = family.FamilyCategoryId;
                if (categoryId != null && categoryId != ElementId.InvalidElementId) collector = collector.OfCategoryId(categoryId);
                long familyId = family.Id.Value;
                foreach (FamilyInstance instance in collector.Cast<FamilyInstance>())
                {
                    FamilySymbol symbol = instance.Symbol;
                    if (symbol == null || symbol.Family == null || symbol.Family.Id.Value != familyId) continue;
                    long key = symbol.Id.Value;
                    counts.TryGetValue(key, out int n);
                    counts[key] = n + 1;
                }
            }
            catch
            {
                // Counts are informative only.
            }
            return counts;
        }

        /// <summary>An open family document already editing <paramref name="family"/> of <paramref name="host"/>, else null.</summary>
        public static Document OpenEditOf(RequestContext ctx, Document host, Family family)
        {
            if (ctx?.RevitApp == null || family == null) return null;
            string name = SafeName(family);
            long hostRid = ctx.Registry?.GetRid(host) ?? 0;
            foreach (Document document in ctx.RevitApp.Documents)
            {
                try
                {
                    if (document == null || !document.IsFamilyDocument || document.IsLinked) continue;
                    if (!string.IsNullOrEmpty(document.PathName)) continue;
                    if (!Resolve.Eq(FamilyNameOf(document), name)) continue;
                    if (hostRid > 0 && ctx.Registry != null && ctx.Registry.TryGetRid(document, out long rid))
                    {
                        long? source = ctx.Registry.GetDoc(rid)?.FamilySourceRid;
                        if (source.HasValue && source.Value != hostRid) continue;
                    }
                    return document;
                }
                catch
                {
                    // Skip documents that cannot be inspected.
                }
            }
            return null;
        }

        /// <summary>A full, normalized .rfa path (".rfa" appended when the path has no extension).</summary>
        public static string RfaPath(string raw, string field)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')));
            }
            catch (Exception ex)
            {
                throw OpException.InvalidArgs(field, "is not a valid path (" + ex.Message + ")");
            }
            string extension = Path.GetExtension(full);
            if (string.IsNullOrEmpty(extension)) return full + ".rfa";
            if (!string.Equals(extension, ".rfa", StringComparison.OrdinalIgnoreCase))
            {
                throw OpException.InvalidArgs(field, "must be a Revit family file (.rfa), got " + full,
                    new Dictionary<string, object> { ["path"] = Path.ChangeExtension(full, ".rfa") });
            }
            return full;
        }
    }

    /// <summary>Reload options: always load, overwrite project type parameter values only when asked.</summary>
    internal sealed class FamilyReloadOptions : IFamilyLoadOptions
    {
        private readonly bool _overwrite;

        public FamilyReloadOptions(bool overwrite)
        {
            _overwrite = overwrite;
        }

        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = _overwrite;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = _overwrite;
            return true;
        }
    }
}
