using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// manage_document.new_project (W1 adapter of the old create_project_from_template; P-DOCS owns this file in wave 2).
    /// Args: path (.rvt, required); template (path or name; default settings.defaultTemplate[year], else
    /// %ProgramData%\Autodesk\RVT &lt;year&gt;\Templates\English\Default-Multi-Discipline_Metric.rte, else Revit's default
    /// project template); overwrite; activate (default true: OpenAndActivateDocument after SaveAs).
    /// An existing file at path needs a confirmed plan (rule file_overwrite).
    /// </summary>
    internal static class ProjectFromTemplateOps
    {
        [Op("manage_document.new_project")]
        public static OpResult NewProject(RequestContext ctx)
        {
            PayloadReader args = ctx.Args;
            string outputPath = FullPath(args.ReqStr("path"), "path");
            if (!string.Equals(Path.GetExtension(outputPath), ".rvt", StringComparison.OrdinalIgnoreCase))
            {
                throw OpException.InvalidArgs("path", "must be a Revit project file (.rvt), got " + outputPath,
                    new Dictionary<string, object> { ["op"] = "new_project", ["path"] = Path.ChangeExtension(outputPath, ".rvt") });
            }
            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw OpException.InvalidArgs("path", "must include a folder");

            string templatePath = ResolveTemplate(ctx, args.Str("template"));
            bool activate = args.Bool("activate", true);
            bool exists = File.Exists(outputPath);
            if (exists)
            {
                OpResult plan = ctx.ConfirmOrPlan("file_overwrite",
                    new Dictionary<string, object> { ["op"] = "new_project", ["overwrite"] = outputPath, ["template"] = templatePath },
                    "new_project would overwrite the existing file " + Path.GetFileName(outputPath));
                if (plan != null) return plan;
            }

            Directory.CreateDirectory(outputDirectory);
            Document created = ctx.RevitApp.NewProjectDocument(templatePath);
            if (created == null)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not create a project from " + templatePath + ".",
                    new Dictionary<string, object> { ["apiMessage"] = "NewProjectDocument returned null" });
            }

            Document result;
            try
            {
                created.SaveAs(outputPath, new SaveAsOptions { OverwriteExistingFile = exists });
                result = activate ? Activate(ctx.App, created, outputPath) : created;
            }
            catch
            {
                CloseQuietly(created);
                throw;
            }

            DocumentRegistry registry = ctx.Registry;
            registry?.Rebuild(ctx.RevitApp, ctx.App);
            ResponseDocOrNull(registry, result, out RevitMcpNext.Contracts.ResponseDoc doc);
            bool activated = false;
            try
            {
                Document active = ctx.App.ActiveUIDocument?.Document;
                activated = active != null && registry != null && doc != null && registry.TryGetRid(active, out long activeRid) && activeRid == doc.Rid;
            }
            catch
            {
                activated = false;
            }

            var data = new Dictionary<string, object>
            {
                ["doc"] = doc == null ? null : new Dictionary<string, object> { ["rid"] = doc.Rid, ["key"] = doc.Key, ["title"] = doc.Title },
                ["path"] = outputPath,
                ["template"] = templatePath,
                ["activated"] = activated,
                ["overwritten"] = exists
            };
            string summary = "created project '" + (doc?.Title ?? Path.GetFileNameWithoutExtension(outputPath)) + "' from " +
                             Path.GetFileName(templatePath) + (activated ? " (active in Revit)" : " (not activated)");
            return OpResult.Success(data, summary).WithDoc(doc).WithOutput("doc", doc?.Rid);
        }

        /// <summary>Settings template, the English multi-discipline template of this year, then Revit's default template.</summary>
        internal static string ResolveTemplate(RequestContext ctx, string requested)
        {
            int year = ctx.Year;
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string candidate = Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"'));
                if (candidate.IndexOfAny(new[] { '\\', '/' }) >= 0 || candidate.EndsWith(".rte", StringComparison.OrdinalIgnoreCase))
                {
                    string full = FullPath(candidate, "template");
                    if (File.Exists(full)) return full;
                    throw OpException.NotFound("template", "template", requested, TemplateCandidates(year).Take(5).Select(Path.GetFileName));
                }

                string match = TemplateCandidates(year).FirstOrDefault(path =>
                    string.Equals(Path.GetFileNameWithoutExtension(path), candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
                throw OpException.NotFound("template", "template", requested, TemplateCandidates(year).Take(8).Select(Path.GetFileNameWithoutExtension));
            }

            string configured = ctx.Settings.DefaultTemplateFor(year);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string full = FullPath(configured, "settings.defaultTemplate");
                if (File.Exists(full)) return full;
                ctx.Warn(WarningCodes.SettingsInvalid, "settings.defaultTemplate." + year.ToString(CultureInfo.InvariantCulture) + " (" + full + ") does not exist; using the standard template.");
            }

            string english = Path.Combine(TemplatesRoot(year), "English", "Default-Multi-Discipline_Metric.rte");
            if (File.Exists(english)) return english;

            string revitDefault = null;
            try { revitDefault = ctx.RevitApp.DefaultProjectTemplate; } catch { }
            if (!string.IsNullOrWhiteSpace(revitDefault) && File.Exists(revitDefault)) return revitDefault;

            string any = TemplateCandidates(year).FirstOrDefault();
            if (any != null) return any;
            throw OpException.NotFound("template", "template", "Default-Multi-Discipline_Metric", new string[0]);
        }

        private static void ResponseDocOrNull(DocumentRegistry registry, Document document, out RevitMcpNext.Contracts.ResponseDoc doc)
        {
            doc = null;
            try
            {
                doc = registry?.Describe(document);
            }
            catch
            {
                doc = null;
            }
        }

        private static IEnumerable<string> TemplateCandidates(int year)
        {
            string root = TemplatesRoot(year);
            if (!Directory.Exists(root)) return new string[0];
            try
            {
                return Directory.GetFiles(root, "*.rte", SearchOption.AllDirectories)
                    .OrderBy(path => path.IndexOf("\\English\\", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new string[0];
            }
        }

        private static string TemplatesRoot(int year)
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Autodesk", "RVT " + year.ToString(CultureInfo.InvariantCulture), "Templates");
        }

        /// <summary>Shows the saved project in the UI: closes the background copy and opens/activates the file.</summary>
        private static Document Activate(UIApplication app, Document created, string outputPath)
        {
            Document active = app.ActiveUIDocument?.Document;
            if (active != null && McpHome.PathsEqual(active.PathName, outputPath)) return active;
            created.Close(false);
            UIDocument opened = app.OpenAndActivateDocument(outputPath);
            if (opened?.Document == null)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit saved the project but did not show it: " + outputPath,
                    new Dictionary<string, object> { ["apiMessage"] = "OpenAndActivateDocument returned null" });
            }
            return opened.Document;
        }

        private static string FullPath(string raw, string param)
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')));
            }
            catch (Exception ex)
            {
                throw OpException.InvalidArgs(param, "is not a valid path (" + ex.Message + ")");
            }
        }

        private static void CloseQuietly(Document document)
        {
            try
            {
                if (document != null && document.IsValidObject) document.Close(false);
            }
            catch
            {
                // Keep the original failure.
            }
        }
    }
}
