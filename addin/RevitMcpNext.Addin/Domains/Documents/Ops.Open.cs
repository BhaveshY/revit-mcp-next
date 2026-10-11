using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// manage_document.open (SPEC §5.9 "open (central)", D1 §4B, D3 §3.10).
    /// <list type="bullet">
    /// <item>Workshared central (BasicFileInfo.IsCentral, else CentralPath == path; V19): a new local copy is created with
    /// WorksharingUtils.CreateNewLocal at <c>local</c> (default %USERPROFILE%\Documents\&lt;name&gt;_&lt;Revit user&gt;.rvt) and
    /// opened; an existing local of the same central is reused (notice LOCAL_COPY). central:true opens the central
    /// itself (the broker asks to confirm first, rule central_open).</item>
    /// <item>detach preserve|discard opens a detached copy (never edits the central); audit; worksets all|none|last|editable.</item>
    /// <item>activate (default true): OpenAndActivateDocument (shown in the UI, re-pins); false: OpenDocumentFile (background).</item>
    /// <item>An already open file is returned (and activated when asked) instead of opened twice.</item>
    /// </list>
    /// The broker runs it as a job (asJob) so a slow open (upgrade, large central) returns j# at the call budget.
    /// </summary>
    internal static class DocumentOpenOps
    {
        [Op("manage_document.open")]
        public static OpResult Open(RequestContext ctx)
        {
            PayloadReader args = ctx.Args;
            string path = DocFiles.FullPath(args.ReqStr("path"), "path");
            DocFiles.RequireExtension(path, "path", new Dictionary<string, object> { ["op"] = "open", ["path"] = Path.ChangeExtension(path, ".rvt") },
                ".rvt", ".rfa", ".rte", ".rft");
            if (!File.Exists(path)) throw OpException.NotFound("path", "file", path, DocFiles.Siblings(path, "*.r??"));

            bool activate = args.Bool("activate", true);
            string detach = args.Enum("detach", "no", "no", "preserve", "discard");
            bool audit = args.Bool("audit", false);
            string worksets = args.Enum("worksets", "last", "all", "none", "last", "editable");
            bool openCentral = args.Bool("central", false);
            string localArg = args.Str("local");

            BasicFileInfo info = ReadInfo(path);
            bool workshared = SafeBool(() => info.IsWorkshared);
            bool isCentral = workshared && IsCentralFile(info, path);
            bool isLocal = workshared && !isCentral && SafeBool(() => info.IsLocal);
            if (SafeBool(() => info.IsSavedInLaterVersion))
            {
                throw new OpException(ErrorCodes.UnsupportedVersion,
                    Path.GetFileName(path) + " was saved in a newer Revit than " + ctx.Year + " and cannot be opened here.",
                    new Dictionary<string, object> { ["year"] = ctx.Year, ["path"] = path, ["format"] = SafeString(() => info.Format) });
            }
            if (!workshared && detach != "no")
            {
                ctx.Warn(WarningCodes.ParamIgnored, "detach ignored: " + Path.GetFileName(path) + " is not workshared");
                detach = "no";
            }
            if (!workshared && args.Has("worksets")) ctx.Warn(WarningCodes.ParamIgnored, "worksets ignored: " + Path.GetFileName(path) + " is not workshared");
            if (!isCentral && openCentral) ctx.Warn(WarningCodes.ParamIgnored, "central ignored: " + Path.GetFileName(path) + " is not a central model");
            if ((!isCentral || openCentral || detach != "no") && localArg != null) ctx.Warn(WarningCodes.ParamIgnored, "local ignored: no local copy is created for this open");

            bool makeLocal = isCentral && !openCentral && detach == "no";
            string localPath = makeLocal ? LocalPathFor(ctx, path, localArg) : null;
            string openPath = makeLocal ? localPath : path;
            bool reuseLocal = makeLocal && File.Exists(localPath);
            if (reuseLocal) VerifyLocalOf(localPath, path);

            // Already open: return it (activate when asked) instead of opening twice.
            Document existing = DocFiles.FindOpen(ctx.RevitApp, openPath);
            if (existing == null && makeLocal) existing = DocFiles.FindOpenLocalOf(ctx.RevitApp, path);
            if (existing == null && isCentral && openCentral) existing = DocFiles.FindOpen(ctx.RevitApp, path);

            var plan = new Dictionary<string, object>
            {
                ["path"] = path,
                ["open"] = existing != null ? DocFiles.SafePath(existing) : openPath,
                ["workshared"] = workshared,
                ["central"] = isCentral,
                ["local"] = localPath,
                ["create_local"] = makeLocal && !reuseLocal && existing == null,
                ["detach"] = detach,
                ["worksets"] = workshared ? worksets : null,
                ["audit"] = audit,
                ["activate"] = activate,
                ["already_open"] = existing != null
            };
            if (ctx.IsPreview)
            {
                string what = existing != null ? "show the already open " + DocFiles.SafeTitle(existing)
                    : makeLocal ? (reuseLocal ? "open the existing local " + localPath : "create the local copy " + localPath + " of central " + Path.GetFileName(path) + " and open it")
                    : isCentral && openCentral ? "open the central model " + path + " itself"
                    : "open " + path + (detach != "no" ? " detached (" + detach + " worksets)" : string.Empty);
                return DocFiles.StaticPlan(what + (activate ? " in the UI" : " in the background"), plan);
            }

            Document document;
            bool createdLocal = false;
            if (existing != null)
            {
                document = existing;
                if (activate && !DocFiles.IsActive(ctx.App, document)) document = ActivateOpen(ctx, document);
                ctx.Notice("ALREADY_OPEN", "'" + DocFiles.SafeTitle(document) + "' was already open; no second copy was opened");
            }
            else
            {
                if (makeLocal)
                {
                    if (reuseLocal)
                    {
                        ctx.Notice("LOCAL_COPY", "opened existing local " + localPath);
                    }
                    else
                    {
                        CreateLocal(ctx, path, localPath);
                        createdLocal = true;
                        ctx.Notice("LOCAL_COPY", "created local copy " + localPath + " of central " + path);
                    }
                }
                OpenOptions options = BuildOptions(ctx, openPath, workshared && detach != "discard", detach, audit, worksets);
                document = OpenFile(ctx, openPath, options, activate);
            }

            ResponseDoc doc = DocFiles.Describe(ctx, document);
            bool activated = DocFiles.IsActive(ctx.App, document);
            string docPath = DocFiles.SafePath(document);
            var data = new Dictionary<string, object>
            {
                ["doc"] = DocFiles.DocData(doc, docPath),
                ["path"] = docPath,
                ["workshared"] = SafeBool(() => document.IsWorkshared),
                ["central"] = DocFiles.CentralPath(document),
                ["is_local"] = makeLocal || isLocal,
                ["created_local"] = createdLocal,
                ["detached"] = detach != "no",
                ["activated"] = activated,
                ["already_open"] = existing != null
            };
            string summary = (existing != null ? "showing already open '" : "opened '") + (doc?.Title ?? Path.GetFileName(docPath)) + "'" +
                             (makeLocal ? (createdLocal ? " as a new local copy" : " (existing local copy)") : string.Empty) +
                             (detach != "no" ? " detached" : string.Empty) +
                             (activated ? " (active in Revit)" : " (in the background)");
            return OpResult.Success(data, summary).WithDoc(doc).WithOutput("doc", doc?.Rid);
        }

        // ---------------------------------------------------------------------------------------------------------

        internal static BasicFileInfo ReadInfo(string path)
        {
            try
            {
                BasicFileInfo info = BasicFileInfo.Extract(path);
                if (info == null) throw new OpException(ErrorCodes.RevitRefused, Path.GetFileName(path) + " is not a Revit file Revit can read.", new Dictionary<string, object> { ["path"] = path });
                return info;
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, path, "Reading the file header of " + Path.GetFileName(path));
            }
        }

        /// <summary>V19: BasicFileInfo.IsCentral (2024/2027), else a CentralPath that is the file itself.</summary>
        internal static bool IsCentralFile(BasicFileInfo info, string path)
        {
            if (SafeBool(() => info.IsCentral)) return true;
            if (SafeBool(() => info.IsLocal)) return false;
            string central = SafeString(() => info.CentralPath);
            return DocFiles.PathsEqual(central, path);
        }

        /// <summary>local arg, else %USERPROFILE%\Documents\&lt;name&gt;_&lt;Revit user&gt;.rvt (Revit's own default).</summary>
        private static string LocalPathFor(RequestContext ctx, string centralPath, string localArg)
        {
            string local;
            if (!string.IsNullOrWhiteSpace(localArg))
            {
                local = DocFiles.FullPath(localArg, "local");
                DocFiles.RequireExtension(local, "local", new Dictionary<string, object> { ["op"] = "open", ["path"] = centralPath, ["local"] = Path.ChangeExtension(local, ".rvt") }, ".rvt");
            }
            else
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(documents)) documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
                local = Path.Combine(documents, Path.GetFileNameWithoutExtension(centralPath) + "_" + DocFiles.RevitUser(ctx.RevitApp) + ".rvt");
            }
            if (DocFiles.PathsEqual(local, centralPath))
            {
                throw OpException.InvalidArgs("local", "must differ from the central model path " + centralPath,
                    new Dictionary<string, object> { ["op"] = "open", ["path"] = centralPath });
            }
            return local;
        }

        /// <summary>An existing file at the local path must be a local of this central (never overwrite other files).</summary>
        private static void VerifyLocalOf(string localPath, string centralPath)
        {
            BasicFileInfo info = ReadInfo(localPath);
            string central = SafeString(() => info.CentralPath);
            bool ok = SafeBool(() => info.IsWorkshared) && !IsCentralFile(info, localPath) && DocFiles.PathsEqual(central, centralPath);
            if (ok) return;
            string alternative = Path.Combine(Path.GetDirectoryName(localPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(localPath) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".rvt");
            throw OpException.InvalidArgs("local", localPath + " exists and is not a local copy of " + centralPath +
                (string.IsNullOrWhiteSpace(central) ? string.Empty : " (it belongs to " + central + ")") + "; it is never overwritten",
                new Dictionary<string, object> { ["op"] = "open", ["path"] = centralPath, ["local"] = alternative });
        }

        private static void CreateLocal(RequestContext ctx, string centralPath, string localPath)
        {
            try
            {
                string folder = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
                ModelPath central = ModelPathUtils.ConvertUserVisiblePathToModelPath(centralPath);
                ModelPath local = ModelPathUtils.ConvertUserVisiblePathToModelPath(localPath);
                DocFiles.Native(ctx, "opening", null, () => WorksharingUtils.CreateNewLocal(central, local));
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, centralPath, "Creating the local copy " + localPath);
            }
            if (!File.Exists(localPath))
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the local copy " + localPath + ".",
                    new Dictionary<string, object> { ["path"] = centralPath, ["local"] = localPath });
            }
        }

        internal static OpenOptions BuildOptions(RequestContext ctx, string openPath, bool workshared, string detach, bool audit, string worksets)
        {
            var options = new OpenOptions { Audit = audit };
            switch (detach)
            {
                case "preserve":
                    options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    break;
                case "discard":
                    options.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;
                    break;
                default:
                    options.DetachFromCentralOption = DetachFromCentralOption.DoNotDetach;
                    break;
            }
            if (!workshared) return options;

            WorksetConfiguration configuration;
            switch (worksets)
            {
                case "all":
                    configuration = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
                    break;
                case "none":
                    configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
                    break;
                case "editable":
                    configuration = EditableWorksets(ctx, openPath);
                    break;
                default:
                    configuration = new WorksetConfiguration(WorksetConfigurationOption.OpenLastViewed);
                    break;
            }
            options.SetOpenWorksetsConfiguration(configuration);
            return options;
        }

        /// <summary>Open the user worksets that nobody else owns (WorksharingUtils.GetUserWorksetInfo), close the rest.</summary>
        private static WorksetConfiguration EditableWorksets(RequestContext ctx, string openPath)
        {
            var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
            try
            {
                string user = null;
                try { user = ctx.RevitApp.Username; } catch { user = null; }
                IList<WorksetPreview> previews = WorksharingUtils.GetUserWorksetInfo(ModelPathUtils.ConvertUserVisiblePathToModelPath(openPath));
                List<WorksetId> open = previews
                    .Where(p => string.IsNullOrWhiteSpace(p.Owner) || string.Equals(p.Owner, user, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Id)
                    .ToList();
                if (open.Count > 0) configuration.Open(open);
            }
            catch (Exception ex)
            {
                ctx.Warn(WarningCodes.PartialResult, "worksets editable: the workset list could not be read (" + ex.Message + "); opening the last viewed worksets");
                configuration = new WorksetConfiguration(WorksetConfigurationOption.OpenLastViewed);
            }
            return configuration;
        }

        internal static Document OpenFile(RequestContext ctx, string openPath, OpenOptions options, bool activate)
        {
            ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(openPath);
            try
            {
                return DocFiles.Native(ctx, "opening", null, () =>
                {
                    if (activate)
                    {
                        UIDocument shown = ctx.App.OpenAndActivateDocument(modelPath, options, false);
                        if (shown?.Document == null)
                        {
                            throw new OpException(ErrorCodes.RevitRefused, "Revit did not show " + openPath + ".",
                                new Dictionary<string, object> { ["apiMessage"] = "OpenAndActivateDocument returned null", ["path"] = openPath });
                        }
                        return shown.Document;
                    }
                    Document opened = ctx.RevitApp.OpenDocumentFile(modelPath, options);
                    if (opened == null)
                    {
                        throw new OpException(ErrorCodes.RevitRefused, "Revit did not open " + openPath + ".",
                            new Dictionary<string, object> { ["apiMessage"] = "OpenDocumentFile returned null", ["path"] = openPath });
                    }
                    return opened;
                });
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, openPath, "Opening " + Path.GetFileName(openPath));
            }
        }

        /// <summary>Shows an already open document in the UI (V4: OpenAndActivateDocument re-activates an open file).</summary>
        internal static Document ActivateOpen(RequestContext ctx, Document document)
        {
            string path = DocFiles.SafePath(document);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + DocFiles.SafeTitle(document) + "' has never been saved, so Revit cannot switch to it through the API.",
                    new Dictionary<string, object> { ["hint"] = "switch to the '" + DocFiles.SafeTitle(document) + "' window in Revit yourself" });
            }
            try
            {
                UIDocument shown = DocFiles.Native(ctx, "opening", document, () => ctx.App.OpenAndActivateDocument(path));
                return shown?.Document ?? document;
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, path, "Activating " + DocFiles.SafeTitle(document));
            }
        }

        internal static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return false; }
        }

        internal static string SafeString(Func<string> read)
        {
            try { return read() ?? string.Empty; } catch { return string.Empty; }
        }
    }
}
