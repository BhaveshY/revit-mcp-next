using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// edit_family document ops: open (keep a loaded family open as its own background document and re-pin to it),
    /// load_into (load an open family document into projects), save and save_as.
    /// </summary>
    internal static class FamilyDocOps
    {
        // ---------------------------------------------------------------------------------------------------------
        // open
        // ---------------------------------------------------------------------------------------------------------

        [Op("edit_family.open")]
        public static OpResult Open(RequestContext ctx)
        {
            Document host = ctx.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document to open a family from.");
            FamilyLookup.Found found = FamilyLookup.Find(host, ctx.Args.ReqStr("family"), "family");
            Family family = found.Family;
            FamilyLookup.EnsureEditable(family);
            if (host.IsModifiable)
            {
                throw new OpException(ErrorCodes.RevitEditModeOrCommand,
                    "'" + FamilyLookup.SafeTitle(host) + "' has an open transaction (Revit is probably in a command or edit mode).");
            }

            DocumentRegistry registry = ctx.Registry;
            Document famDoc = FamilyLookup.OpenEditOf(ctx, host, family);
            bool reused = famDoc != null;
            if (!reused)
            {
                try
                {
                    famDoc = host.EditFamily(family);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit cannot open family '" + FamilyLookup.SafeName(family) + "' for editing: " + ex.Message,
                        new Dictionary<string, object> { ["apiMessage"] = ex.Message, ["family"] = FamilyLookup.SafeName(family) });
                }
                if (famDoc == null)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit did not open family '" + FamilyLookup.SafeName(family) + "' for editing.",
                        new Dictionary<string, object> { ["apiMessage"] = "EditFamily returned null" });
                }
            }

            ResponseDoc doc = null;
            try
            {
                registry?.SetFamilySource(famDoc, host);
                registry?.Rebuild(ctx.RevitApp, ctx.App);
                doc = registry?.Describe(famDoc);
            }
            catch
            {
                doc = null;
            }

            FamilyManager fm = famDoc.FamilyManager;
            var data = new Dictionary<string, object>
            {
                ["family"] = FamilyLookup.FamilyNameOf(famDoc),
                ["doc"] = doc == null ? null : new Dictionary<string, object> { ["rid"] = doc.Rid, ["title"] = doc.Title, ["kind"] = doc.Kind },
                ["from"] = FamilyLookup.SafeTitle(host),
                ["category"] = FamilyNames.CategoryLabel(EditFamilyOps.SafeCategory(famDoc.OwnerFamily)),
                ["params"] = FamilyValues.All(fm).Count,
                ["types"] = FamilyValues.Types(fm).Select(t => (object)FamilyValues.TypeName(t)).Take(30).ToList(),
                ["reused"] = reused
            };
            string summary = (reused ? "family '" : "opened family '") + FamilyLookup.FamilyNameOf(famDoc) + "' " +
                             (reused ? "is already open" : "from " + FamilyLookup.SafeTitle(host)) +
                             " as a background family document (not shown in Revit's window); edits go to it; edit_family load_into reloads it into " +
                             FamilyLookup.SafeTitle(host);
            OpResult result = OpResult.Success(data, summary).WithDoc(doc).WithOutput("family", family.Id.Value);
            if (doc != null) result.WithOutput("doc", doc.Rid);
            return result;
        }

        // ---------------------------------------------------------------------------------------------------------
        // load_into
        // ---------------------------------------------------------------------------------------------------------

        [Op("edit_family.load_into")]
        public static void LoadInto(ChangeContext c)
        {
            Document famDoc = c.Doc;
            if (famDoc == null || !famDoc.IsFamilyDocument)
            {
                throw new OpException(ErrorCodes.FamilyDocRequired, "load_into works on an open family document.",
                    new Dictionary<string, object> { ["options"] = FamilySession.FamilyDocOptions(c.Ctx) });
            }
            List<Document> targets = Targets(c, famDoc);
            bool overwrite = c.Bool("overwrite", false);
            string familyName = FamilyLookup.FamilyNameOf(famDoc);

            if (c.IsPreview)
            {
                c.Data(new Dictionary<string, object>
                {
                    ["family"] = familyName,
                    ["into"] = targets.Select(t => (object)FamilyLookup.SafeTitle(t)).ToList(),
                    ["overwrite"] = overwrite
                });
                c.Summary("would load " + familyName + " into " + string.Join(", ", targets.Select(FamilyLookup.SafeTitle)) +
                          (overwrite ? " (overwriting project type values)" : " (keeping project type values)"));
                return;
            }

            var loaded = new List<object>();
            foreach (Document target in targets)
            {
                if (target.IsModifiable)
                {
                    throw new OpException(ErrorCodes.RevitEditModeOrCommand,
                        "'" + FamilyLookup.SafeTitle(target) + "' has an open transaction (Revit is probably in a command or edit mode); nothing was loaded there.");
                }
                if (target.IsReadOnly)
                {
                    throw new OpException(ErrorCodes.DocReadOnly, "'" + FamilyLookup.SafeTitle(target) + "' is open read-only.",
                        new Dictionary<string, object> { ["doc"] = FamilyLookup.SafeTitle(target), ["editable"] = false });
                }
                Family existing = FamilyLookup.All(target).FirstOrDefault(f => Resolve.Eq(FamilyLookup.SafeName(f), familyName));
                if (existing != null && target.IsWorkshared) WorksharingGuard.EnsureEditable(target, existing);
                var before = new HashSet<long>(FamilyLookup.Symbols(target, existing).Select(s => s.Id.Value));

                Family family = famDoc.LoadFamily(target, new FamilyReloadOptions(overwrite));
                if (family == null)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit did not load " + familyName + " into '" + FamilyLookup.SafeTitle(target) + "'.",
                        new Dictionary<string, object> { ["apiMessage"] = "LoadFamily returned null" });
                }
                List<FamilySymbol> symbols = FamilyLookup.Symbols(target, family);
                loaded.Add(new Dictionary<string, object>
                {
                    ["doc"] = FamilyLookup.SafeTitle(target),
                    ["family_id"] = family.Id.Value,
                    ["new"] = existing == null,
                    ["types"] = symbols.Count,
                    ["types_created"] = symbols.Where(s => !before.Contains(s.Id.Value)).Select(s => (object)new List<object> { s.Id.Value, s.Name }).Take(50).ToList()
                });
            }

            c.Data(new Dictionary<string, object> { ["family"] = familyName, ["loaded"] = loaded, ["overwrite"] = overwrite });
            c.Summary("loaded " + familyName + " into " + string.Join(", ", targets.Select(FamilyLookup.SafeTitle)) +
                      (overwrite ? " (project type values overwritten)" : " (project type values kept)"));
        }

        private static readonly Regex CanonicalTarget = new Regex(@"^rid:(\d+)\|(.+)$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Target documents: into= items ("rid:&lt;rid&gt;|&lt;docKey&gt;" from the broker, or titles/paths from in-process
        /// callers), else the project the family was opened from, else the only open project.
        /// </summary>
        private static List<Document> Targets(ChangeContext c, Document famDoc)
        {
            DocumentRegistry registry = c.Ctx.Registry;
            var app = c.Ctx.RevitApp;
            var targets = new List<Document>();
            var open = new List<Document>();
            foreach (Document document in app.Documents)
            {
                if (document != null && !document.IsLinked && !document.Equals(famDoc)) open.Add(document);
            }

            foreach (string item in c.Has("into") ? c.Args.Strs("into") : new List<string>())
            {
                string field = c.Args.Field("into");
                Match canonical = CanonicalTarget.Match(item);
                if (canonical.Success)
                {
                    // "rid:<rid>|<docKey>" from the broker (it resolved #n, titles and the session's project pin).
                    long rid = long.Parse(canonical.Groups[1].Value, CultureInfo.InvariantCulture);
                    string key = canonical.Groups[2].Value;
                    Document byRid = registry?.FindDocument(app, rid);
                    if (byRid != null && registry.KeyMatches(rid, key)) { Add(targets, byRid); continue; }
                    List<Document> byKey = registry == null ? new List<Document>() : registry.FindByKey(app, key);
                    if (byKey.Count == 1) { Add(targets, byKey[0]); continue; }
                    throw new OpException(ErrorCodes.DocNotOpen, "The document " + key + " is not open in this Revit any more.",
                        new Dictionary<string, object> { ["key"] = key });
                }

                List<Document> matches = open.Where(d => Resolve.Eq(FamilyLookup.SafeTitle(d), item) ||
                                                         Resolve.Eq(Path.GetFileNameWithoutExtension(FamilyLookup.SafeTitle(d)), item) ||
                                                         (!string.IsNullOrEmpty(d.PathName) && McpHome.PathsEqual(d.PathName, item))).ToList();
                if (matches.Count == 1) { Add(targets, matches[0]); continue; }
                if (matches.Count > 1) throw OpException.Ambiguous(field, "document", item, matches.Select(FamilyLookup.SafeTitle));
                throw OpException.NotFound(field, "document", item, Resolve.Closest(item, open.Select(FamilyLookup.SafeTitle)));
            }

            if (targets.Count == 0)
            {
                Document source = null;
                if (registry != null && registry.TryGetRid(famDoc, out long famRid))
                {
                    long? sourceRid = registry.GetDoc(famRid)?.FamilySourceRid;
                    if (sourceRid.HasValue) source = registry.FindDocument(app, sourceRid.Value);
                }
                if (source == null)
                {
                    List<Document> projects = open.Where(d => !d.IsFamilyDocument).ToList();
                    if (projects.Count == 1) source = projects[0];
                    else
                    {
                        throw OpException.InvalidArgs("into", projects.Count == 0
                                ? "no project is open to load the family into"
                                : "say which project to load into (open projects: " + string.Join(", ", projects.Select(FamilyLookup.SafeTitle)) + ")",
                            new Dictionary<string, object> { ["op"] = "load_into", ["into"] = projects.Select(p => (object)FamilyLookup.SafeTitle(p)).Take(1).ToList() });
                    }
                }
                targets.Add(source);
            }

            if (targets.Any(t => t.Equals(famDoc))) throw OpException.InvalidArgs("into", "a family cannot be loaded into itself");
            return targets;
        }

        private static void Add(List<Document> targets, Document document)
        {
            if (!targets.Any(t => t.Equals(document))) targets.Add(document);
        }

        // ---------------------------------------------------------------------------------------------------------
        // save / save_as
        // ---------------------------------------------------------------------------------------------------------

        [Op("edit_family.save")]
        public static OpResult Save(RequestContext ctx)
        {
            Document famDoc = RequireFamilyDoc(ctx);
            if (string.IsNullOrEmpty(famDoc.PathName))
            {
                throw new OpException(ErrorCodes.SaveAsRequired, "'" + FamilyLookup.SafeTitle(famDoc) + "' was never saved to a file (opened from a project); use save_as with an .rfa path.",
                    new Dictionary<string, object> { ["doc"] = FamilyLookup.SafeTitle(famDoc) });
            }
            if (famDoc.IsReadOnly)
            {
                throw new OpException(ErrorCodes.DocReadOnly, "'" + FamilyLookup.SafeTitle(famDoc) + "' is open read-only.",
                    new Dictionary<string, object> { ["doc"] = FamilyLookup.SafeTitle(famDoc), ["editable"] = false });
            }
            EnsureNotModifiable(famDoc);
            bool wasModified = SafeModified(famDoc);
            famDoc.Save();
            var data = new Dictionary<string, object> { ["path"] = famDoc.PathName, ["had_changes"] = wasModified };
            return OpResult.Success(data, "saved " + FamilyLookup.SafeTitle(famDoc) + (wasModified ? string.Empty : " (no unsaved changes)"));
        }

        [Op("edit_family.save_as")]
        public static OpResult SaveAs(RequestContext ctx)
        {
            Document famDoc = RequireFamilyDoc(ctx);
            string path = FamilyLookup.RfaPath(ctx.Args.ReqStr("path"), "path");
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) throw OpException.InvalidArgs("path", "must include a folder");
            EnsureNotModifiable(famDoc);

            bool exists = File.Exists(path);
            if (exists)
            {
                if (!string.IsNullOrEmpty(famDoc.PathName) && McpHome.PathsEqual(famDoc.PathName, path))
                {
                    throw OpException.InvalidArgs("path", "is the family's own file; use save",
                        new Dictionary<string, object> { ["op"] = "save" });
                }
                OpResult plan = ctx.ConfirmOrPlan("file_overwrite",
                    new Dictionary<string, object> { ["op"] = "save_as", ["overwrite"] = path, ["family"] = FamilyLookup.FamilyNameOf(famDoc) },
                    "save_as would overwrite the existing file " + Path.GetFileName(path));
                if (plan != null) return plan;
            }

            string before = FamilyLookup.SafeTitle(famDoc);
            Directory.CreateDirectory(directory);
            famDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = exists, MaximumBackups = 1 });

            ResponseDoc doc = null;
            try
            {
                ctx.Registry?.Rebuild(ctx.RevitApp, ctx.App);
                doc = ctx.Registry?.Describe(famDoc);
            }
            catch
            {
                doc = null;
            }
            var data = new Dictionary<string, object>
            {
                ["path"] = path,
                ["was"] = before,
                ["overwritten"] = exists,
                ["family"] = FamilyLookup.FamilyNameOf(famDoc)
            };
            OpResult result = OpResult.Success(data, "saved " + before + " as " + path + (exists ? " (replaced the existing file)" : string.Empty));
            if (doc != null) result.WithDoc(doc);
            return result;
        }

        private static Document RequireFamilyDoc(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            if (doc == null || !doc.IsFamilyDocument)
            {
                throw new OpException(ErrorCodes.FamilyDocRequired,
                    "edit_family." + ctx.Op + " works on a family document" + (doc == null ? "." : "; '" + FamilyLookup.SafeTitle(doc) + "' is a project."),
                    new Dictionary<string, object> { ["doc"] = FamilyLookup.SafeTitle(doc), ["options"] = FamilySession.FamilyDocOptions(ctx) });
            }
            return doc;
        }

        private static void EnsureNotModifiable(Document doc)
        {
            if (!doc.IsModifiable) return;
            throw new OpException(ErrorCodes.RevitEditModeOrCommand,
                "'" + FamilyLookup.SafeTitle(doc) + "' has an open transaction (Revit is probably in a command or edit mode).");
        }

        private static bool SafeModified(Document doc)
        {
            try { return doc.IsModified; } catch { return true; }
        }
    }
}
