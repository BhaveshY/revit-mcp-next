using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// manage_document.activate (show an open document in the Revit UI; the broker re-pins with TARGET_NOW) and
    /// manage_document.new_family (a family document from a .rft template, saved to path and shown; re-pins).
    /// </summary>
    internal static class DocumentActivateNewFamilyOps
    {
        [Op("manage_document.activate")]
        public static OpResult Activate(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            if (doc == null)
            {
                throw OpException.InvalidArgs("doc", "is required: the document to show (\"#2\", a title or a path from status)",
                    new Dictionary<string, object> { ["op"] = "activate", ["doc"] = "#2" });
            }
            string title = DocFiles.SafeTitle(doc);
            bool wasActive = DocFiles.IsActive(ctx.App, doc);
            Document shown = doc;
            if (!wasActive)
            {
                if (DocumentSaveCloseOps.SafeCloud(doc))
                {
                    ModelPath cloudPath;
                    try { cloudPath = doc.GetCloudModelPath(); }
                    catch (Exception ex) { throw DocFiles.MapFileException(ex, title, "Reading the cloud path of " + title); }
                    try
                    {
                        UIDocument ui = DocFiles.Native(ctx, "opening", doc, () => ctx.App.OpenAndActivateDocument(cloudPath, new OpenOptions(), false));
                        shown = ui?.Document ?? doc;
                    }
                    catch (Exception ex)
                    {
                        throw DocFiles.MapFileException(ex, title, "Activating " + title);
                    }
                }
                else
                {
                    shown = DocumentOpenOps.ActivateOpen(ctx, doc);
                }
            }

            ResponseDoc described = DocFiles.Describe(ctx, shown);
            bool active = DocFiles.IsActive(ctx.App, shown);
            if (!active)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not switch to '" + title + "'.",
                    new Dictionary<string, object> { ["apiMessage"] = "the document is not active after OpenAndActivateDocument", ["doc"] = title });
            }
            View view = null;
            try { view = ctx.App.ActiveUIDocument?.ActiveView; } catch { view = null; }
            var data = new Dictionary<string, object>
            {
                ["doc"] = DocFiles.DocData(described, DocFiles.SafePath(shown)),
                ["was_active"] = wasActive,
                ["active_view"] = view == null ? null : view.Name
            };
            string summary = wasActive ? "'" + title + "' is already the active document" : "Revit now shows '" + title + "'";
            return OpResult.Success(data, summary).WithDoc(described).WithOutput("doc", described?.Rid);
        }

        [Op("manage_document.new_family")]
        public static OpResult NewFamily(RequestContext ctx)
        {
            PayloadReader args = ctx.Args;
            string path = DocFiles.FullPath(args.ReqStr("path"), "path");
            DocFiles.RequireExtension(path, "path",
                new Dictionary<string, object> { ["op"] = "new_family", ["template"] = args.Str("template") ?? "Metric Generic Model.rft", ["path"] = Path.ChangeExtension(path, ".rfa") }, ".rfa");
            string template = ResolveFamilyTemplate(ctx, args.ReqStr("template"));
            Document openAtPath = DocFiles.FindOpen(ctx.RevitApp, path);
            if (openAtPath != null)
            {
                throw OpException.InvalidArgs("path", path + " is open in Revit as '" + DocFiles.SafeTitle(openAtPath) + "'; close it first or choose another path",
                    new Dictionary<string, object> { ["op"] = "close", ["doc"] = DocFiles.SafeTitle(openAtPath) });
            }
            bool exists = File.Exists(path);
            var plan = new Dictionary<string, object> { ["path"] = path, ["template"] = template, ["overwrite"] = exists };
            if (exists)
            {
                OpResult confirm = ctx.ConfirmOrPlan("file_overwrite", plan, "new_family would overwrite the existing file " + path);
                if (confirm != null) return confirm;
            }
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("create family " + path + " from " + Path.GetFileName(template) + (exists ? ", replacing the existing file" : string.Empty) + " and show it", plan);
            }

            Document created;
            try
            {
                created = DocFiles.Native(ctx, "opening", null, () => ctx.RevitApp.NewFamilyDocument(template));
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, template, "Creating a family from " + Path.GetFileName(template));
            }
            if (created == null)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not create a family from " + template + ".",
                    new Dictionary<string, object> { ["apiMessage"] = "NewFamilyDocument returned null", ["template"] = template });
            }

            Document shown;
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
                DocFiles.Native(ctx, "saving", created, () => created.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = exists }));
                // Show the saved file: the background document is closed and the file opened in the UI (like new_project).
                created.Close(false);
                UIDocument ui = DocFiles.Native(ctx, "opening", null, () => ctx.App.OpenAndActivateDocument(path));
                shown = ui?.Document;
                if (shown == null)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit saved the family but did not show it: " + path,
                        new Dictionary<string, object> { ["apiMessage"] = "OpenAndActivateDocument returned null", ["path"] = path });
                }
            }
            catch (Exception ex)
            {
                try { if (created.IsValidObject) created.Close(false); } catch { }
                throw DocFiles.MapFileException(ex, path, "Saving the new family as " + Path.GetFileName(path));
            }

            ResponseDoc described = DocFiles.Describe(ctx, shown);
            string category = null;
            try { category = Rows.CategoryLabel(shown.OwnerFamily?.FamilyCategory); } catch { category = null; }
            var data = new Dictionary<string, object>
            {
                ["doc"] = DocFiles.DocData(described, path),
                ["path"] = path,
                ["template"] = template,
                ["category"] = category,
                ["overwritten"] = exists,
                ["activated"] = DocFiles.IsActive(ctx.App, shown)
            };
            string summary = "created family '" + Path.GetFileNameWithoutExtension(path) + "' from " + Path.GetFileName(template) +
                             (category != null ? " (" + category + ")" : string.Empty);
            return OpResult.Success(data, summary).WithDoc(described).WithOutput("doc", described?.Rid);
        }

        /// <summary>A .rft path, or a template name (with or without .rft) under Revit's family template folder.</summary>
        internal static string ResolveFamilyTemplate(RequestContext ctx, string requested)
        {
            string text = Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"'));
            if (Path.IsPathRooted(text))
            {
                string full = DocFiles.FullPath(text, "template");
                DocFiles.RequireExtension(full, "template", new Dictionary<string, object> { ["op"] = "new_family", ["template"] = "Metric Generic Model.rft" }, ".rft");
                if (File.Exists(full)) return full;
                throw OpException.NotFound("template", "family template", requested, DocFiles.Siblings(full, "*.rft"));
            }

            List<string> candidates = FamilyTemplates(ctx);
            string wanted = Path.GetFileNameWithoutExtension(text);
            List<string> exact = candidates.Where(p => string.Equals(Path.GetFileNameWithoutExtension(p), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count >= 1) return exact.OrderBy(p => p.IndexOf("\\English", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).First();
            List<string> contains = candidates.Where(p => Path.GetFileNameWithoutExtension(p).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (contains.Count == 1) return contains[0];
            IEnumerable<string> names = (contains.Count > 1 ? contains : candidates).Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase);
            if (contains.Count > 1) throw OpException.Ambiguous("template", "family template", requested, names.Take(10));
            throw OpException.NotFound("template", "family template", requested, Resolve.Closest(wanted, names));
        }

        private static List<string> FamilyTemplates(RequestContext ctx)
        {
            var roots = new List<string>();
            try
            {
                string configured = ctx.RevitApp.FamilyTemplatePath;
                if (!string.IsNullOrWhiteSpace(configured)) roots.Add(configured);
            }
            catch
            {
                // Fall back to the standard folders.
            }
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            roots.Add(Path.Combine(programData, "Autodesk", "RVT " + ctx.Year.ToString(CultureInfo.InvariantCulture), "Family Templates"));
            var files = new List<string>();
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.Exists(root)) files.AddRange(Directory.GetFiles(root, "*.rft", SearchOption.AllDirectories));
                }
                catch
                {
                    // Unreadable folder: skip.
                }
            }
            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
