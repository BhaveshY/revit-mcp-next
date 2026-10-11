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
    /// manage_document.save | save_as | close (SPEC §5.9, §6.3 file_overwrite/unsaved_close, D3 §3.10, V26).
    /// close never closes the UI-active document directly: when another saved document is open, job step 1 activates
    /// it and job step 2 (the next Execute pass) closes the target; when the target is the only one,
    /// CANNOT_CLOSE_ACTIVE. There is never a placeholder document.
    /// </summary>
    internal static class DocumentSaveCloseOps
    {
        [Op("manage_document.save")]
        public static OpResult Save(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            bool compact = ctx.Args.Bool("compact", false);
            string path = DocFiles.SafePath(doc);
            bool cloud = SafeCloud(doc);
            if (string.IsNullOrWhiteSpace(path) && !cloud)
            {
                throw new OpException(ErrorCodes.SaveAsRequired, "'" + DocFiles.SafeTitle(doc) + "' has never been saved, so it has no file to save to.",
                    new Dictionary<string, object> { ["path"] = SuggestedPath(doc) });
            }
            if (doc.IsReadOnly)
            {
                throw new OpException(ErrorCodes.DocReadOnly, "'" + DocFiles.SafeTitle(doc) + "' is open read-only.",
                    new Dictionary<string, object> { ["doc"] = DocFiles.SafeTitle(doc), ["editable"] = false });
            }
            bool workshared = doc.IsWorkshared;
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("save '" + DocFiles.SafeTitle(doc) + "'" + (workshared ? " (local file only; worksharing sync sends it to central)" : string.Empty) + (compact ? ", compacted" : string.Empty),
                    new Dictionary<string, object> { ["path"] = path, ["cloud"] = cloud, ["workshared"] = workshared, ["compact"] = compact });
            }

            try
            {
                DocFiles.Native(ctx, "saving", doc, () =>
                {
                    if (cloud) doc.SaveCloudModel();
                    else doc.Save(new SaveOptions { Compact = compact });
                });
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, path, "Saving " + DocFiles.SafeTitle(doc));
            }
            if (workshared) ctx.Notice("LOCAL_SAVED", "saved the local file only; worksharing {\"op\":\"sync\"} sends the changes to central");
            var data = new Dictionary<string, object>
            {
                ["path"] = path,
                ["bytes"] = cloud ? (object)null : DocFiles.Bytes(path),
                ["cloud"] = cloud,
                ["workshared"] = workshared,
                ["compact"] = compact
            };
            return OpResult.Success(data, "saved '" + DocFiles.SafeTitle(doc) + "'" + (cloud ? " to the cloud" : string.Empty)).WithDoc(DocFiles.Describe(ctx, doc, rebuild: false));
        }

        [Op("manage_document.save_as")]
        public static OpResult SaveAs(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            PayloadReader args = ctx.Args;
            string path = DocFiles.FullPath(args.ReqStr("path"), "path");
            bool asCentral = args.Bool("as_central", false);
            bool compact = args.Bool("compact", false);
            bool family = doc.IsFamilyDocument;
            string[] extensions = family ? new[] { ".rfa" } : new[] { ".rvt", ".rte" };
            DocFiles.RequireExtension(path, "path", new Dictionary<string, object> { ["op"] = "save_as", ["path"] = Path.ChangeExtension(path, extensions[0]) }, extensions);
            string current = DocFiles.SafePath(doc);
            if (DocFiles.PathsEqual(current, path))
            {
                throw OpException.InvalidArgs("path", "is the document's own file; use save to save it in place",
                    new Dictionary<string, object> { ["op"] = "save" });
            }
            Document other = DocFiles.FindOpen(ctx.RevitApp, path);
            if (other != null)
            {
                throw OpException.InvalidArgs("path", path + " is open in Revit as '" + DocFiles.SafeTitle(other) + "'; close it first or choose another path",
                    new Dictionary<string, object> { ["op"] = "close", ["doc"] = DocFiles.SafeTitle(other) });
            }
            bool workshared = doc.IsWorkshared;
            if (asCentral && !workshared)
            {
                throw OpException.InvalidArgs("as_central", "'" + DocFiles.SafeTitle(doc) + "' is not workshared; enable worksharing first (worksharing {\"op\":\"enable\"}), then save_as with as_central:true",
                    new Dictionary<string, object> { ["op"] = "save_as", ["path"] = path });
            }
            if (asCentral && family) throw OpException.InvalidArgs("as_central", "family documents cannot be central models");
            if (workshared && !asCentral)
            {
                throw OpException.InvalidArgs("as_central", "'" + DocFiles.SafeTitle(doc) + "' is workshared: Revit saves it under a new name only as a new central model (as_central:true), or detach it on open",
                    new Dictionary<string, object> { ["op"] = "save_as", ["path"] = path, ["as_central"] = true });
            }

            bool exists = File.Exists(path);
            var plan = new Dictionary<string, object>
            {
                ["path"] = path,
                ["from"] = current,
                ["overwrite"] = exists,
                ["as_central"] = asCentral,
                ["compact"] = compact
            };
            if (exists)
            {
                OpResult confirm = ctx.ConfirmOrPlan("file_overwrite", plan, "save_as would overwrite the existing file " + path);
                if (confirm != null) return confirm;
            }
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("save '" + DocFiles.SafeTitle(doc) + "' as " + path + (asCentral ? " (new central model)" : string.Empty) + (exists ? ", replacing the existing file" : string.Empty), plan);
            }

            string folder = Path.GetDirectoryName(path);
            var options = new SaveAsOptions { OverwriteExistingFile = exists, Compact = compact };
            if (workshared)
            {
                options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = asCentral });
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
                DocFiles.Native(ctx, "saving", doc, () => doc.SaveAs(path, options));
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, path, "Saving " + DocFiles.SafeTitle(doc) + " as " + Path.GetFileName(path));
            }

            ResponseDoc described = DocFiles.Describe(ctx, doc);
            var data = new Dictionary<string, object>
            {
                ["doc"] = DocFiles.DocData(described, path),
                ["path"] = path,
                ["from"] = current,
                ["bytes"] = DocFiles.Bytes(path),
                ["overwritten"] = exists,
                ["central"] = asCentral ? DocFiles.CentralPath(doc) : null,
                ["workshared"] = doc.IsWorkshared
            };
            string summary = "saved '" + DocFiles.SafeTitle(doc) + "' as " + path + (asCentral ? " (central model; open it to get a local copy)" : string.Empty);
            return OpResult.Success(data, summary).WithDoc(described).WithOutput("doc", described?.Rid);
        }

        [Op("manage_document.close")]
        public static OpResult Close(RequestContext ctx)
        {
            Document target = ctx.Doc;
            bool save = ctx.Args.Bool("save", false);
            string title = DocFiles.SafeTitle(target);
            string path = DocFiles.SafePath(target);
            bool modified = SafeModified(target);
            bool cloud = SafeCloud(target);
            if (save && string.IsNullOrWhiteSpace(path) && !cloud)
            {
                throw new OpException(ErrorCodes.SaveAsRequired, "'" + title + "' has never been saved; save_as it first or close it with save:false.",
                    new Dictionary<string, object> { ["path"] = SuggestedPath(target) });
            }
            if (save && target.IsReadOnly)
            {
                throw new OpException(ErrorCodes.DocReadOnly, "'" + title + "' is open read-only and cannot be saved; close it with save:false.",
                    new Dictionary<string, object> { ["doc"] = title, ["editable"] = false });
            }

            bool active = DocFiles.IsActive(ctx.App, target);
            Document switchTo = active ? PickOtherDocument(ctx, target) : null;
            if (active && switchTo == null)
            {
                throw new OpException(ErrorCodes.CannotCloseActive,
                    "'" + title + "' is the active document and no other saved document is open to switch to; the Revit API cannot close the active document.",
                    new Dictionary<string, object> { ["doc"] = title });
            }

            var plan = new Dictionary<string, object>
            {
                ["doc"] = title,
                ["save"] = save,
                ["unsaved_changes"] = modified,
                ["switch_to"] = switchTo == null ? null : DocFiles.SafeTitle(switchTo)
            };
            if (modified && !save)
            {
                OpResult confirm = ctx.ConfirmOrPlan("unsaved_close", plan, "close '" + title + "' without saving its unsaved changes (they are lost)");
                if (confirm != null) return confirm;
            }
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("close '" + title + "'" + (save ? " after saving it" : modified ? " discarding unsaved changes" : string.Empty) +
                                           (switchTo != null ? " (first switching Revit to '" + DocFiles.SafeTitle(switchTo) + "')" : string.Empty), plan);
            }

            ResponseDoc before = DocFiles.Describe(ctx, target, rebuild: false);
            if (!active) return CloseNow(ctx, target, save, before, null);

            // V26: activate another document in this pass, close the target in the next one.
            string switchPath = DocFiles.SafePath(switchTo);
            string switchTitle = DocFiles.SafeTitle(switchTo);
            var steps = new List<Func<JobStep, StepResult>>
            {
                step =>
                {
                    if (step.CancelRequested) return StepResult.Fail(ErrorCodes.RequestCancelled, "close was cancelled before anything changed.");
                    step.Progress("activating " + switchTitle, 0, 2);
                    try
                    {
                        DocFiles.Native(step.Context, "opening", switchTo, () => step.App.OpenAndActivateDocument(switchPath));
                    }
                    catch (Exception ex)
                    {
                        throw DocFiles.MapFileException(ex, switchPath, "Switching Revit to " + switchTitle);
                    }
                    return StepResult.Next("closing " + title, 1, 2);
                },
                step =>
                {
                    if (!target.IsValidObject) return StepResult.Finish(OpResult.Success(new Dictionary<string, object> { ["closed"] = title }, "'" + title + "' was already closed").WithDoc(before));
                    if (DocFiles.IsActive(step.App, target))
                    {
                        return StepResult.Fail(ErrorCodes.CannotCloseActive, "Revit did not switch away from '" + title + "', so it is still the active document.",
                            new Dictionary<string, object> { ["doc"] = title, ["switch_to"] = switchTitle });
                    }
                    return StepResult.Finish(CloseNow(step.Context, target, save, before, switchTitle));
                }
            };
            return ctx.Jobs.Start(ctx, steps, "activating " + switchTitle, 2);
        }

        private static OpResult CloseNow(RequestContext ctx, Document target, bool save, ResponseDoc before, string switchedTo)
        {
            string title = DocFiles.SafeTitle(target);
            string path = DocFiles.SafePath(target);
            bool family = target.IsFamilyDocument;
            bool closed;
            try
            {
                closed = DocFiles.Native(ctx, save ? "saving" : "closing", target, () => target.Close(save));
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Revit refused to close '" + title + "': " + ex.Message,
                    new Dictionary<string, object> { ["apiMessage"] = ex.Message, ["hint"] = "close '" + title + "' in Revit yourself" + (family ? " (family editors with open views may need the UI)" : string.Empty) });
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, path, "Closing " + title);
            }
            if (!closed)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not close '" + title + "'.",
                    new Dictionary<string, object> { ["apiMessage"] = "Document.Close returned false", ["doc"] = title });
            }
            try { ctx.Registry?.Rebuild(ctx.RevitApp, ctx.App); } catch { }
            var data = new Dictionary<string, object> { ["closed"] = title, ["saved"] = save, ["path"] = string.IsNullOrWhiteSpace(path) ? null : path };
            if (switchedTo != null) data["active_now"] = switchedTo;
            string summary = "closed '" + title + "'" + (save ? " after saving" : string.Empty) + (switchedTo != null ? "; Revit now shows '" + switchedTo + "'" : string.Empty);
            return OpResult.Success(data, summary).WithDoc(before);
        }

        /// <summary>Another open, saved document to activate before closing the active one (projects first).</summary>
        private static Document PickOtherDocument(RequestContext ctx, Document target)
        {
            return DocFiles.OpenDocuments(ctx.RevitApp, target)
                .Where(document => !string.IsNullOrWhiteSpace(DocFiles.SafePath(document)))
                .OrderBy(document => document.IsFamilyDocument ? 1 : 0)
                .FirstOrDefault();
        }

        private static string SuggestedPath(Document doc)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string extension = doc.IsFamilyDocument ? ".rfa" : ".rvt";
            return Path.Combine(documents, DocFiles.SafeFilePart(Path.GetFileNameWithoutExtension(DocFiles.SafeTitle(doc))) + extension);
        }

        internal static bool SafeCloud(Document doc)
        {
            try { return doc.IsModelInCloud; } catch { return false; }
        }

        private static bool SafeModified(Document doc)
        {
            try { return doc.IsModified; } catch { return false; }
        }
    }
}
