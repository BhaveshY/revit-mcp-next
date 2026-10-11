using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// read_family: parameters (name, kind, data type, group, formula, shared), types with their values, category and
    /// nested families of a family. family= reads a loaded family through a background EditFamily copy (closed
    /// afterwards, nothing in the project changes), path= opens an .rfa hidden and closes it, and a family document
    /// target (doc=) is read in place.
    /// </summary>
    internal static class ReadFamilyOps
    {
        private const int MaxTypes = 30;
        private const int MaxParams = 300;
        private const int MaxNested = 100;

        [Op("read_family")]
        public static OpResult Read(RequestContext ctx)
        {
            PayloadReader args = ctx.Args;
            Document doc = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document to read a family from.");
            bool full = args.Enum("detail", "compact", "compact", "full") == "full";
            List<string> typeFilter = args.Strs("types");
            string familyArg = args.Str("family");
            string pathArg = args.Str("path");
            if (!string.IsNullOrWhiteSpace(familyArg) && !string.IsNullOrWhiteSpace(pathArg))
            {
                throw OpException.InvalidArgs("path", "send family= (a loaded family) or path= (an .rfa file), not both");
            }

            Document famDoc = null;
            bool close = false;
            string source;
            Family loaded = null;
            if (!string.IsNullOrWhiteSpace(pathArg))
            {
                string path = FamilyLookup.RfaPath(pathArg, "path");
                famDoc = OpenFile(ctx, path, out close);
                source = "file";
            }
            else if (!string.IsNullOrWhiteSpace(familyArg) && !(doc.IsFamilyDocument && Resolve.Eq(FamilyLookup.FamilyNameOf(doc), familyArg)))
            {
                FamilyLookup.Found found = FamilyLookup.Find(doc, familyArg, "family");
                loaded = found.Family;
                if (found.TypeName != null && typeFilter.Count == 0) typeFilter.Add(found.TypeName);
                source = doc.IsFamilyDocument ? "nested" : "loaded";
                if (!FamilyLookup.SafeEditable(loaded))
                {
                    return ProjectOnly(ctx, doc, loaded, typeFilter);
                }
                if (doc.IsModifiable)
                {
                    throw new OpException(ErrorCodes.RevitEditModeOrCommand,
                        "'" + FamilyLookup.SafeTitle(doc) + "' has an open transaction (Revit is probably in a command or edit mode).");
                }
                try
                {
                    famDoc = doc.EditFamily(loaded);
                    close = famDoc != null;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    Document open = FamilyLookup.OpenEditOf(ctx, doc, loaded);
                    if (open == null)
                    {
                        throw new OpException(ErrorCodes.RevitRefused, "Revit cannot open family '" + FamilyLookup.SafeName(loaded) + "' for reading: " + ex.Message,
                            new Dictionary<string, object> { ["apiMessage"] = ex.Message, ["family"] = FamilyLookup.SafeName(loaded) });
                    }
                    famDoc = open;
                    ctx.Notice(NoticeCodes.FamilyEditing, "read from the open family document " + FamilyLookup.SafeTitle(open) + " (it may have unsaved edits)");
                }
                if (famDoc == null)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit did not open family '" + FamilyLookup.SafeName(loaded) + "' for reading.",
                        new Dictionary<string, object> { ["apiMessage"] = "EditFamily returned null" });
                }
            }
            else
            {
                if (!doc.IsFamilyDocument)
                {
                    throw OpException.InvalidArgs("family", "say which family: family=<loaded family>, path=<.rfa file> or doc=<an open family document>",
                        new Dictionary<string, object> { ["family"] = "<family name>" });
                }
                famDoc = doc;
                source = "doc";
            }

            try
            {
                Dictionary<string, object> data = Build(ctx, famDoc, typeFilter, full);
                data["source"] = source;
                if (source == "file") data["path"] = famDoc.PathName;
                if (loaded != null) AddProjectTypes(data, doc, loaded, full);
                string summary = Summary(data);
                return OpResult.Success(data, summary).WithOutput("family", loaded?.Id.Value);
            }
            finally
            {
                if (close) CloseQuietly(famDoc);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Report
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The family report of an open family document (params, types with values, nested families).</summary>
        internal static Dictionary<string, object> Build(RequestContext ctx, Document famDoc, List<string> typeFilter, bool full)
        {
            FamilyManager fm = famDoc.FamilyManager;
            List<FamilyParameter> parameters = FamilyValues.All(fm);
            List<FamilyType> allTypes = FamilyValues.Types(fm);

            List<FamilyType> types;
            if (typeFilter != null && typeFilter.Count > 0 && !typeFilter.Contains("*"))
            {
                types = new List<FamilyType>();
                foreach (string name in typeFilter)
                {
                    FamilyType type = FamilyValues.Type(fm, name, "types");
                    if (!types.Any(t => EditFamilyOps.SameType(t, type))) types.Add(type);
                }
            }
            else
            {
                types = allTypes.OrderBy(FamilyValues.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (types.Count > MaxTypes)
            {
                ctx.Warn(WarningCodes.PartialResult, "showing the values of " + MaxTypes.ToString(CultureInfo.InvariantCulture) + " of " +
                    types.Count.ToString(CultureInfo.InvariantCulture) + " types; pass types=[...] for others");
                types = types.Take(MaxTypes).ToList();
            }

            // Values per (param, type), read once.
            var values = new Dictionary<FamilyParameter, List<object>>();
            foreach (FamilyParameter parameter in parameters)
            {
                var row = new List<object>(types.Count);
                foreach (FamilyType type in types) row.Add(Clip(FamilyValues.Get(famDoc, type, parameter), full));
                values[parameter] = row;
            }

            // Parameters table.
            var paramCols = new List<string> { "name", "kind", "data", "group", "formula", "shared" };
            if (full) paramCols.AddRange(new[] { "reporting", "builtin", "id", "used_by" });
            var paramTable = new TableBuilder(paramCols);
            int builtInsHidden = 0;
            foreach (FamilyParameter parameter in parameters)
            {
                bool builtIn = FamilyValues.IsBuiltIn(parameter);
                if (!full && builtIn && values[parameter].All(IsEmpty))
                {
                    builtInsHidden++;
                    continue;
                }
                if (paramTable.Rows.Count >= MaxParams) continue;
                var row = new List<object>
                {
                    FamilyValues.NameOf(parameter),
                    EditFamilyOps.SafeIsInstance(parameter) ? "instance" : "type",
                    FamilyNames.DataLabel(famDoc, FamilyValues.SpecOf(parameter)),
                    FamilyNames.GroupLabel(EditFamilyOps.SafeGroup(parameter)),
                    EditFamilyOps.SafeFormula(parameter),
                    EditFamilyOps.SafeIsShared(parameter) ? (object)EditFamilyOps.SafeGuid(parameter) : null
                };
                if (full)
                {
                    row.Add(EditFamilyOps.SafeIsReporting(parameter));
                    row.Add(builtIn);
                    row.Add(parameter.Id?.Value);
                    row.Add(builtIn ? 0 : EditFamilyOps.Usage(famDoc, fm, parameter).Count);
                }
                paramTable.AddRow(row);
            }
            paramTable.Total = parameters.Count - builtInsHidden;
            if (paramTable.Total > MaxParams) ctx.Warn(WarningCodes.PartialResult, "showing " + MaxParams.ToString(CultureInfo.InvariantCulture) + " of " + paramTable.Total.ToString(CultureInfo.InvariantCulture) + " parameters");

            // Types table: one column per parameter that varies between the shown types; equal values go to "same".
            var varying = new List<FamilyParameter>();
            var same = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (FamilyParameter parameter in parameters)
            {
                if (!full && EditFamilyOps.SafeIsInstance(parameter)) continue;
                List<object> row = values[parameter];
                if (row.All(IsEmpty)) continue;
                if (types.Count > 1 && row.Any(v => !SameValue(v, row[0]))) varying.Add(parameter);
                else same[FamilyValues.NameOf(parameter)] = row.Count == 0 ? null : row[0];
            }
            var typeCols = new List<string> { "type" };
            typeCols.AddRange(varying.Select(FamilyValues.NameOf));
            var typeTable = new TableBuilder(typeCols) { Total = allTypes.Count };
            for (int t = 0; t < types.Count; t++)
            {
                var row = new List<object> { FamilyValues.TypeName(types[t]) };
                foreach (FamilyParameter parameter in varying) row.Add(values[parameter][t]);
                typeTable.AddRow(row);
            }
            Dictionary<string, object> typesWire = typeTable.ToWire();
            if (same.Count > 0) typesWire["same"] = same;
            if (!full) typesWire["note"] = "type parameters only; detail:full adds instance parameter defaults";

            Family owner = SafeOwner(famDoc);
            var data = new Dictionary<string, object>
            {
                ["family"] = FamilyLookup.FamilyNameOf(famDoc),
                ["category"] = FamilyNames.CategoryLabel(EditFamilyOps.SafeCategory(owner)),
                ["params"] = paramTable.ToWire(),
                ["types"] = typesWire
            };
            if (builtInsHidden > 0) data["builtins_hidden"] = builtInsHidden;
            Dictionary<string, object> nested = Nested(famDoc, owner, full);
            if (nested != null) data["nested"] = nested;
            return data;
        }

        private static Dictionary<string, object> Nested(Document famDoc, Family owner, bool full)
        {
            List<Family> families = FamilyLookup.All(famDoc).Where(f => owner == null || f.Id != owner.Id).OrderBy(FamilyLookup.SafeName, StringComparer.OrdinalIgnoreCase).ToList();
            if (families.Count == 0) return null;
            var cols = new List<string> { "name", "category", "types" };
            if (full) cols.Add("type_names");
            var table = new TableBuilder(cols) { Total = families.Count };
            foreach (Family family in families.Take(MaxNested))
            {
                List<FamilySymbol> symbols = FamilyLookup.Symbols(famDoc, family);
                var row = new List<object> { FamilyLookup.SafeName(family), FamilyNames.CategoryLabel(EditFamilyOps.SafeCategory(family)), symbols.Count };
                if (full) row.Add(symbols.Select(s => (object)s.Name).Take(30).ToList());
                table.AddRow(row);
            }
            return table.ToWire();
        }

        /// <summary>The loaded family's types in the project: [id, type, placed instances].</summary>
        private static void AddProjectTypes(Dictionary<string, object> data, Document host, Family family, bool full)
        {
            Dictionary<long, int> counts = FamilyLookup.InstanceCounts(host, family);
            var table = new TableBuilder(new[] { "id", "type", "placed" });
            List<FamilySymbol> symbols = FamilyLookup.Symbols(host, family);
            table.Total = symbols.Count;
            foreach (FamilySymbol symbol in symbols.Take(full ? 200 : 50))
            {
                counts.TryGetValue(symbol.Id.Value, out int placed);
                table.Add(symbol.Id.Value, symbol.Name, placed);
            }
            data["family_id"] = family.Id.Value;
            data["project_types"] = table.ToWire();
        }

        /// <summary>Non-editable families (in-place, protected): project-side types and their type parameters only.</summary>
        private static OpResult ProjectOnly(RequestContext ctx, Document doc, Family family, List<string> typeFilter)
        {
            var data = new Dictionary<string, object>
            {
                ["family"] = FamilyLookup.SafeName(family),
                ["category"] = FamilyNames.CategoryLabel(EditFamilyOps.SafeCategory(family)),
                ["source"] = "loaded",
                ["editable"] = false
            };
            AddProjectTypes(data, doc, family, false);
            string reason;
            try { reason = family.IsInPlace ? "an in-place family" : "not editable"; } catch { reason = "not editable"; }
            ctx.Warn(WarningCodes.PartialResult, "family '" + FamilyLookup.SafeName(family) + "' is " + reason + "; only its types in the project are listed");
            return OpResult.Success(data, FamilyLookup.SafeName(family) + " (" + reason + "): " + EditFamilyOps.Plural(FamilyLookup.Symbols(doc, family).Count, "type") + " in the project")
                .WithOutput("family", family.Id.Value);
        }

        private static string Summary(Dictionary<string, object> data)
        {
            int paramTotal = (data["params"] as Dictionary<string, object>)?["total"] is int p ? p : 0;
            int typeTotal = (data["types"] as Dictionary<string, object>)?["total"] is int t ? t : 0;
            string category = data["category"] as string;
            return Convert.ToString(data["family"], CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(category) ? string.Empty : " (" + category + ")") + ": " +
                   EditFamilyOps.Plural(paramTotal, "parameter") + ", " + EditFamilyOps.Plural(typeTotal, "type");
        }

        // ---------------------------------------------------------------------------------------------------------
        // Files
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The .rfa at <paramref name="path"/>: the already open document, else opened hidden (closed by the caller).</summary>
        private static Document OpenFile(RequestContext ctx, string path, out bool close)
        {
            close = false;
            if (!File.Exists(path))
            {
                string folder = Path.GetDirectoryName(path);
                IEnumerable<string> nearby = Enumerable.Empty<string>();
                try
                {
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) nearby = Directory.GetFiles(folder, "*.rfa").Select(Path.GetFileName);
                }
                catch
                {
                    nearby = Enumerable.Empty<string>();
                }
                throw OpException.NotFound("path", "family file", path, Resolve.Closest(Path.GetFileName(path), nearby));
            }

            foreach (Document open in ctx.RevitApp.Documents)
            {
                try
                {
                    if (open != null && !open.IsLinked && !string.IsNullOrEmpty(open.PathName) && McpHome.PathsEqual(open.PathName, path)) return open;
                }
                catch
                {
                    // Skip documents that cannot be inspected.
                }
            }

            Document document;
            try
            {
                document = ctx.RevitApp.OpenDocumentFile(path);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit cannot open " + path + ": " + ex.Message,
                    new Dictionary<string, object> { ["apiMessage"] = ex.Message, ["path"] = path });
            }
            if (document == null)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not open " + path + ".", new Dictionary<string, object> { ["path"] = path });
            }
            close = true;
            if (!document.IsFamilyDocument)
            {
                CloseQuietly(document);
                throw OpException.InvalidArgs("path", path + " is not a family file");
            }
            return document;
        }

        private static void CloseQuietly(Document document)
        {
            try
            {
                if (document != null && document.IsValidObject) document.Close(false);
            }
            catch (Exception ex)
            {
                Diagnostics.DiagnosticsLogger.Error("family", "Closing a family document opened for reading failed.", ex);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Values
        // ---------------------------------------------------------------------------------------------------------

        private static object Clip(object value, bool full)
        {
            return value is string text ? OutputJson.Cut(text, full) : value;
        }

        private static bool IsEmpty(object value)
        {
            return value == null || value is string text && text.Length == 0;
        }

        private static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (PayloadReader.TryNumber(a, out double x) && PayloadReader.TryNumber(b, out double y) && !(a is string) && !(b is string)) return Math.Abs(x - y) < 1e-9;
            return Equals(a, b) || string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private static Family SafeOwner(Document famDoc)
        {
            try { return famDoc.OwnerFamily; } catch { return null; }
        }
    }
}
