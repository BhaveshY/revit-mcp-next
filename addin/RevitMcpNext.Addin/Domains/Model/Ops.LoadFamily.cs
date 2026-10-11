using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// place_family.load (D1 §4B, D3 §3.8): loads an .rfa (all types or only <c>symbols</c>) with guards — absolute local
    /// path (UNC/network only with allow_network), .rfa only, ≤ 100 MB, optional SHA-256 and category allow-list. The
    /// category check runs after the load inside the op transaction, so a refused family leaves nothing behind.
    /// </summary>
    internal static class LoadFamilyOps
    {
        private const long MaxFamilyBytes = 100L * 1024 * 1024;

        [Op("place_family.load")]
        public static void Load(ChangeContext c)
        {
            Document doc = c.Doc;
            string pathField = c.Args.Field("path");
            string text = c.Str("path") ?? throw OpException.InvalidArgs(pathField, "is required (an .rfa path visible to Revit)");
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(text.Trim().Trim('"')));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                throw OpException.InvalidArgs(pathField, "is not a valid path: " + ex.Message);
            }
            if (!Path.IsPathRooted(text.Trim().Trim('"')) && !Path.IsPathRooted(Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'))))
            {
                throw OpException.InvalidArgs(pathField, "must be an absolute path (e.g. C:\\Families\\Door.rfa)");
            }
            if (!string.Equals(Path.GetExtension(fullPath), ".rfa", StringComparison.OrdinalIgnoreCase))
            {
                throw OpException.InvalidArgs(pathField, "must be a Revit family file (.rfa); got '" + Path.GetFileName(fullPath) + "'");
            }
            bool allowNetwork = c.Bool("allow_network");
            if (!allowNetwork && IsNetworkPath(fullPath))
            {
                var example = new Dictionary<string, object>(StringComparer.Ordinal) { ["op"] = "load", ["path"] = text, ["allow_network"] = true };
                throw OpException.InvalidArgs(c.Args.Field("allow_network"), "'" + fullPath + "' is on a network share; pass allow_network:true to load from it", example);
            }
            var file = new FileInfo(fullPath);
            if (!file.Exists) throw OpException.NotFound(pathField, "file", fullPath, SiblingFamilies(file));
            if (file.Length <= 0) throw OpException.InvalidArgs(pathField, "'" + file.Name + "' is empty");
            if (file.Length > MaxFamilyBytes) throw OpException.InvalidArgs(pathField, "'" + file.Name + "' is larger than 100 MB");

            string expected = NormalizeSha256(c.Str("sha256"), c.Args.Field("sha256"));
            string actual = null;
            if (expected != null)
            {
                actual = Sha256(file.FullName);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new OpException(ErrorCodes.InvalidArgs, "The SHA-256 of '" + file.Name + "' is " + actual + ", not the expected " + expected + "; nothing was loaded.",
                        new Dictionary<string, object> { ["param"] = c.Args.Field("sha256"), ["reason"] = "hash mismatch", ["actual"] = actual });
                }
            }

            bool overwrite = c.Bool("overwrite");
            List<string> wantedSymbols = c.Args.Strs("symbols").Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string familyName = Path.GetFileNameWithoutExtension(file.Name);
            Family before = FindFamily(doc, familyName);
            var beforeSymbols = new HashSet<long>(before == null ? Enumerable.Empty<long>() : before.GetFamilySymbolIds().Select(id => id.Value));
            var options = new LoadOptions(overwrite);

            Family family = null;
            bool loaded;
            var missingSymbols = new List<string>();
            if (wantedSymbols.Count == 0)
            {
                loaded = doc.LoadFamily(file.FullName, options, out family);
            }
            else
            {
                loaded = false;
                foreach (string name in wantedSymbols)
                {
                    bool one = doc.LoadFamilySymbol(file.FullName, name, options, out FamilySymbol symbol);
                    if (symbol != null)
                    {
                        family = family ?? symbol.Family;
                        loaded |= one;
                    }
                    else if (!one && !(before != null && SymbolNames(doc, before).Contains(name)))
                    {
                        missingSymbols.Add(name);
                    }
                }
                if (missingSymbols.Count == wantedSymbols.Count && family == null && before == null)
                {
                    throw OpException.InvalidArgs(c.Args.Field("symbols"), "none of the types " + string.Join(", ", missingSymbols) + " exist in '" + file.Name + "' (type names are case-sensitive)");
                }
            }
            family = family ?? FindFamily(doc, familyName) ?? before;
            if (family == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not load a family from '" + file.Name + "'.", new Dictionary<string, object> { ["apiMessage"] = "LoadFamily returned no family" });

            CheckCategories(c, family);

            List<FamilySymbol> symbols = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>()
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            List<FamilySymbol> added = symbols.Where(s => !beforeSymbols.Contains(s.Id.Value)).ToList();
            if (before == null) c.Created(family);
            else if (loaded) c.Modified(family, "family reloaded");
            foreach (FamilySymbol symbol in added) c.Created(symbol);
            if (missingSymbols.Count > 0) c.Warn(WarningCodes.PartialResult, "types not found in '" + file.Name + "': " + string.Join(", ", missingSymbols));

            FamilySymbol first = added.FirstOrDefault() ?? symbols.FirstOrDefault();
            c.Output("family", family.Id);
            if (first != null) c.Output("type", first.Id);
            string category = Rows.CategoryLabel(family.FamilyCategory);
            var table = new TableBuilder(new[] { "id", "type", "new" });
            foreach (FamilySymbol symbol in symbols.Take(100)) table.Add(symbol.Id.Value, Resolve.FullTypeName(symbol), !beforeSymbols.Contains(symbol.Id.Value));
            table.Total = symbols.Count;
            c.Data(new Dictionary<string, object>
            {
                ["family"] = family.Name,
                ["family_id"] = family.Id.Value,
                ["category"] = category,
                ["placement"] = family.FamilyPlacementType.ToString(),
                ["types"] = table.ToWire(),
                ["sha256"] = actual
            });
            string state = before == null ? "loaded" : (loaded ? "reloaded" : "already loaded (unchanged)");
            c.Summary(state + " family '" + family.Name + "' (" + (category ?? "no category") + ") with " + ModelKit.Count(symbols.Count, "type") +
                      (added.Count > 0 && before != null ? ", " + added.Count.ToString(CultureInfo.InvariantCulture) + " new" : string.Empty));
        }

        private static void CheckCategories(ChangeContext c, Family family)
        {
            List<string> allowed = c.Args.Strs("categories").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (allowed.Count == 0) return;
            Category category = family.FamilyCategory;
            BuiltInCategory actual = Resolve.SafeBuiltIn(category);
            foreach (string token in allowed)
            {
                if (Resolve.TryCategory(c.Doc, token, out BuiltInCategory wanted) && wanted == actual && actual != BuiltInCategory.INVALID) return;
                if (category != null && (Resolve.Eq(category.Name, token) || Resolve.Eq(Rows.CategoryLabel(category), token))) return;
            }
            throw new OpException(ErrorCodes.InvalidArgs,
                "Family '" + family.Name + "' is in category " + (Rows.CategoryLabel(category) ?? "(none)") + ", not one of " + string.Join(", ", allowed) + "; nothing was loaded.",
                new Dictionary<string, object> { ["param"] = c.Args.Field("categories"), ["reason"] = "category not allowed", ["category"] = Rows.CategoryLabel(category) });
        }

        private static bool IsNetworkPath(string fullPath)
        {
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal)) return true;
            try
            {
                string root = Path.GetPathRoot(fullPath);
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeSha256(string value, string field)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7);
            if (text.Length == 0) return null;
            text = text.ToLowerInvariant();
            if (text.Length != 64 || text.Any(ch => !Uri.IsHexDigit(ch))) throw OpException.InvalidArgs(field, "must be a 64-character hex SHA-256");
            return text;
        }

        private static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static Family FindFamily(Document doc, string name)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<string> SymbolNames(Document doc, Family family)
        {
            return new HashSet<string>(family.GetFamilySymbolIds().Select(id => doc.GetElement(id)?.Name).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Other .rfa files in the same folder (closest names first) for the NOT_FOUND candidates.</summary>
        private static IEnumerable<string> SiblingFamilies(FileInfo file)
        {
            try
            {
                DirectoryInfo folder = file.Directory;
                if (folder == null || !folder.Exists) return new string[0];
                List<string> names = folder.EnumerateFiles("*.rfa").Take(500).Select(f => f.FullName).ToList();
                return Resolve.Closest(file.FullName, names, 5);
            }
            catch
            {
                return new string[0];
            }
        }

        /// <summary>Reloads an existing family; <c>overwrite</c> decides whether project parameter values are replaced.</summary>
        private sealed class LoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;

            public LoadOptions(bool overwrite)
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
}
