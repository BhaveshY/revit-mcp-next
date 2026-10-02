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
        private static Dictionary<string, object> PreviewLoadFamily(Document document, Dictionary<string, object> operation, int index)
        {
            if (!TryBuildFamilyLoadRequest(operation, out FamilyLoadRequest request, out string error))
            {
                return BlockedChange(operation, index, error);
            }

            Family existingFamily = FindFamilyByName(document, request.FamilyName);
            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["document"] = document.Title,
                    ["familyPath"] = request.FullPath,
                    ["fileName"] = request.FileName,
                    ["familyName"] = request.FamilyName,
                    ["expectedSha256"] = request.ExpectedSha256,
                    ["existingFamilyId"] = existingFamily == null ? null : ToElementIdString(existingFamily.Id)
                },
                before: existingFamily == null ? null : FamilyLoadSnapshot(document, existingFamily, existing: true),
                after: new Dictionary<string, object>
                {
                    ["familyPath"] = request.FullPath,
                    ["fileName"] = request.FileName,
                    ["fileSizeBytes"] = request.FileSizeBytes,
                    ["fileSha256"] = request.FileSha256,
                    ["familyName"] = request.FamilyName,
                    ["allowedCategories"] = request.AllowedCategories.ToArray(),
                    ["overwriteParameterValues"] = request.OverwriteParameterValues,
                    ["allowNetworkPath"] = request.AllowNetworkPath
                });
        }

        private static Dictionary<string, object> ApplyLoadFamily(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewLoadFamily(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "load_family preview failed.");
            }

            if (!TryBuildFamilyLoadRequest(operation, out FamilyLoadRequest request, out string error))
            {
                throw new InvalidOperationException(error);
            }

            Family before = FindFamilyByName(document, request.FamilyName);
            Family loadedFamily = null;
            bool loadReturned = document.LoadFamily(
                request.FullPath,
                new ControlledFamilyLoadOptions(request.OverwriteParameterValues),
                out loadedFamily);

            Family family = loadedFamily ?? FindFamilyByName(document, request.FamilyName) ?? before;
            if (family == null)
            {
                throw new InvalidOperationException("Revit did not report a loaded family for " + request.FileName + ".");
            }

            FamilySymbol[] symbols = GetFamilySymbols(document, family).ToArray();
            if (request.AllowedCategories.Count > 0 && !MatchesAllowedFamilyCategories(family, symbols, request.AllowedCategories))
            {
                throw new InvalidOperationException(
                    "Loaded family '" + SafeElementName(family) + "' did not match allowedCategories: " +
                    string.Join(", ", request.AllowedCategories));
            }

            Dictionary<string, object> after = FamilyLoadSnapshot(document, family, existing: false);
            after["familyPath"] = request.FullPath;
            after["fileName"] = request.FileName;
            after["fileSizeBytes"] = request.FileSizeBytes;
            after["fileSha256"] = request.FileSha256;
            after["loadReturned"] = loadReturned;
            after["allowedCategories"] = request.AllowedCategories.ToArray();
            after["overwriteParameterValues"] = request.OverwriteParameterValues;

            return Change(operation, index, "applied",
                target: ElementTarget(family, null),
                before: before == null ? null : FamilyLoadSnapshot(document, before, existing: true),
                after: after);
        }

        private static bool TryBuildFamilyLoadRequest(Dictionary<string, object> operation, out FamilyLoadRequest request, out string error)
        {
            request = null;
            error = null;

            string familyPath = GetString(operation, "familyPath");
            if (string.IsNullOrWhiteSpace(familyPath))
            {
                error = "load_family requires familyPath.";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(familyPath.Trim()));
            }
            catch (Exception ex)
            {
                error = "familyPath is not a valid local path: " + ex.Message;
                return false;
            }

            if (!Path.IsPathRooted(fullPath))
            {
                error = "load_family requires an absolute familyPath.";
                return false;
            }

            bool allowNetworkPath = GetBool(operation, "allowNetworkPath", false);
            if (!allowNetworkPath && IsNetworkPath(fullPath))
            {
                error = "load_family does not allow UNC/network paths unless allowNetworkPath is true.";
                return false;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".rfa", StringComparison.OrdinalIgnoreCase))
            {
                error = "load_family only accepts Revit .rfa family files.";
                return false;
            }

            FileInfo file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                error = "Family file was not found: " + fullPath;
                return false;
            }

            if (file.Length <= 0)
            {
                error = "Family file is empty: " + fullPath;
                return false;
            }

            if (file.Length > MaxFamilyLoadBytes)
            {
                error = "Family file exceeds the 100 MB load_family limit: " + fullPath;
                return false;
            }

            string fileSha256;
            try
            {
                fileSha256 = ComputeFileSha256(file.FullName);
            }
            catch (Exception ex)
            {
                error = "Could not hash family file: " + ex.Message;
                return false;
            }

            string expectedSha256;
            try
            {
                expectedSha256 = NormalizeSha256(GetString(operation, "expectedSha256"));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !string.Equals(expectedSha256, fileSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "Family file SHA-256 did not match expectedSha256.";
                return false;
            }

            List<string> allowedCategories = GetStringList(operation, "allowedCategories")
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToList();

            request = new FamilyLoadRequest(
                file.FullName,
                file.Name,
                Path.GetFileNameWithoutExtension(file.Name),
                file.Length,
                "sha256:" + fileSha256,
                string.IsNullOrWhiteSpace(expectedSha256) ? null : "sha256:" + expectedSha256,
                allowedCategories,
                GetBool(operation, "overwriteParameterValues", false),
                allowNetworkPath);
            return true;
        }

        private static bool IsNetworkPath(string fullPath)
        {
            return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                   fullPath.StartsWith("//", StringComparison.Ordinal);
        }

        private static string NormalizeSha256(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("sha256:".Length);
            }

            if (string.IsNullOrWhiteSpace(normalized)) return null;
            normalized = normalized.ToLowerInvariant();
            if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
            {
                throw new ArgumentException("expectedSha256 must be a 64-character hex SHA-256 digest.");
            }

            return normalized;
        }

        private static string ComputeFileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static Family FindFamilyByName(Document document, string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return null;
            return new FilteredElementCollector(document)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(family => string.Equals(SafeElementName(family), familyName, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<FamilySymbol> GetFamilySymbols(Document document, Family family)
        {
            if (family == null) return Enumerable.Empty<FamilySymbol>();
            return family.GetFamilySymbolIds()
                .Select(id => document.GetElement(id) as FamilySymbol)
                .Where(symbol => symbol != null);
        }

        private static Dictionary<string, object> FamilyLoadSnapshot(Document document, Family family, bool existing)
        {
            FamilySymbol[] symbols = GetFamilySymbols(document, family).ToArray();
            var snapshot = new Dictionary<string, object>
            {
                ["familyId"] = ToElementIdString(family.Id),
                ["familyName"] = SafeElementName(family),
                ["existing"] = existing,
                ["symbolCount"] = symbols.Length,
                ["symbols"] = symbols
                    .Take(50)
                    .Select(symbol => FamilySymbolLoadSnapshot(symbol))
                    .ToArray()
            };

            if (family.Category != null) snapshot["category"] = family.Category.Name;
            if (family.FamilyCategory != null) snapshot["familyCategory"] = family.FamilyCategory.Name;
            return snapshot;
        }

        private static Dictionary<string, object> FamilySymbolLoadSnapshot(FamilySymbol symbol)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(symbol.Id),
                ["uniqueId"] = symbol.UniqueId,
                ["class"] = symbol.GetType().Name,
                ["name"] = SafeElementName(symbol),
                ["familyName"] = GetFamilyName(symbol),
                ["isActive"] = symbol.IsActive
            };

            string builtInCategory = GetBuiltInCategoryName(symbol);
            if (!string.IsNullOrWhiteSpace(builtInCategory)) snapshot["builtInCategory"] = builtInCategory;
            if (symbol.Category != null) snapshot["category"] = symbol.Category.Name;
            string placementType = GetPlacementType(symbol);
            if (!string.IsNullOrWhiteSpace(placementType)) snapshot["placementType"] = placementType;
            return snapshot;
        }

        private static bool MatchesAllowedFamilyCategories(Family family, IEnumerable<FamilySymbol> symbols, IReadOnlyList<string> allowedCategories)
        {
            if (allowedCategories == null || allowedCategories.Count == 0) return true;
            HashSet<string> allowed = new HashSet<string>(allowedCategories, StringComparer.OrdinalIgnoreCase);
            foreach (string token in FamilyCategoryTokens(family, symbols))
            {
                if (allowed.Contains(token)) return true;
            }

            return false;
        }

        private static IEnumerable<string> FamilyCategoryTokens(Family family, IEnumerable<FamilySymbol> symbols)
        {
            if (family?.Category != null) yield return family.Category.Name;
            if (family?.FamilyCategory != null)
            {
                yield return family.FamilyCategory.Name;
                string builtInFamilyCategory = string.Empty;
                try
                {
                    builtInFamilyCategory = ((BuiltInCategory)GetElementIdValue(family.FamilyCategory.Id)).ToString();
                }
                catch
                {
                    // Some custom family categories cannot be mapped to BuiltInCategory.
                }
                if (!string.IsNullOrWhiteSpace(builtInFamilyCategory)) yield return builtInFamilyCategory;
            }

            foreach (FamilySymbol symbol in symbols ?? Enumerable.Empty<FamilySymbol>())
            {
                string builtInCategory = GetBuiltInCategoryName(symbol);
                if (!string.IsNullOrWhiteSpace(builtInCategory)) yield return builtInCategory;
                if (symbol.Category != null) yield return symbol.Category.Name;
            }
        }

        private sealed class FamilyLoadRequest
        {
            public FamilyLoadRequest(
                string fullPath,
                string fileName,
                string familyName,
                long fileSizeBytes,
                string fileSha256,
                string expectedSha256,
                IReadOnlyList<string> allowedCategories,
                bool overwriteParameterValues,
                bool allowNetworkPath)
            {
                FullPath = fullPath;
                FileName = fileName;
                FamilyName = familyName;
                FileSizeBytes = fileSizeBytes;
                FileSha256 = fileSha256;
                ExpectedSha256 = expectedSha256;
                AllowedCategories = allowedCategories ?? Array.Empty<string>();
                OverwriteParameterValues = overwriteParameterValues;
                AllowNetworkPath = allowNetworkPath;
            }

            public string FullPath { get; }
            public string FileName { get; }
            public string FamilyName { get; }
            public long FileSizeBytes { get; }
            public string FileSha256 { get; }
            public string ExpectedSha256 { get; }
            public IReadOnlyList<string> AllowedCategories { get; }
            public bool OverwriteParameterValues { get; }
            public bool AllowNetworkPath { get; }
        }

        private sealed class ControlledFamilyLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwriteParameterValues;

            public ControlledFamilyLoadOptions(bool overwriteParameterValues)
            {
                _overwriteParameterValues = overwriteParameterValues;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwriteParameterValues;
                return true;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily,
                bool familyInUse,
                out FamilySource source,
                out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwriteParameterValues;
                return true;
            }
        }
    }
}
