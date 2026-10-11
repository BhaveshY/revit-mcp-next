using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// worksharing.* (SPEC Appendix A/B, D3 §3.10). Lifecycle ops (sync, reload_latest, relinquish, borrow, enable) run
    /// outside transactions as native calls (status shows "sync"/"reloading"); sync and enable are always confirmed by
    /// the broker first (rule always), sync and reload_latest run as jobs. Workset ops are ChangeEngine writes.
    /// </summary>
    internal static class WorksharingOps
    {
        // ---------------------------------------------------------------------------------------------- lifecycle

        [Op("worksharing.sync")]
        public static OpResult Sync(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            RequireWorkshared(doc, "sync");
            string comment = ctx.Args.Str("comment") ?? string.Empty;
            bool relinquish = ctx.Args.Bool("relinquish", true);
            bool compact = ctx.Args.Bool("compact", false);
            string central = DocFiles.CentralPath(doc);
            bool modified = SafeModified(doc);
            var plan = new Dictionary<string, object>
            {
                ["central"] = central,
                ["comment"] = comment,
                ["relinquish"] = relinquish,
                ["compact"] = compact,
                ["unsaved_changes"] = modified
            };
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("synchronize '" + DocFiles.SafeTitle(doc) + "' with central " + Path.GetFileName(central) +
                                           (relinquish ? " and relinquish everything" : " keeping borrowed elements"), plan);
            }

            string centralMtimeBefore = DocFiles.Mtime(central);
            var transact = new TransactWithCentralOptions();
            var options = new SynchronizeWithCentralOptions
            {
                Comment = comment,
                Compact = compact,
                SaveLocalBefore = true,
                SaveLocalAfter = true
            };
            options.SetRelinquishOptions(new RelinquishOptions(relinquish));
            ctx.Progress("synchronizing with central");
            try
            {
                DocFiles.Native(ctx, "sync", doc, () => doc.SynchronizeWithCentral(transact, options));
            }
            catch (Exception ex)
            {
                throw MapCentralException(ex, central, "Synchronizing '" + DocFiles.SafeTitle(doc) + "' with central");
            }

            // V11: Revit clears the undo history on sync; earlier MCP writes can no longer be undone with undo.
            ctx.Notice("UNDO_CLEARED", "synchronizing cleared Revit's undo history for '" + DocFiles.SafeTitle(doc) + "'");
            var data = new Dictionary<string, object>
            {
                ["central"] = central,
                ["comment"] = comment,
                ["relinquished"] = relinquish,
                ["compacted"] = compact,
                ["central_saved_utc"] = DocFiles.Mtime(central),
                ["central_saved_before_utc"] = centralMtimeBefore
            };
            return OpResult.Success(data, "synchronized '" + DocFiles.SafeTitle(doc) + "' with central" + (relinquish ? " and relinquished everything" : string.Empty))
                .WithDoc(DocFiles.Describe(ctx, doc));
        }

        [Op("worksharing.reload_latest")]
        public static OpResult ReloadLatest(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            RequireWorkshared(doc, "reload_latest");
            string central = DocFiles.CentralPath(doc);
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("load the latest changes from central " + Path.GetFileName(central) + " into '" + DocFiles.SafeTitle(doc) + "'",
                    new Dictionary<string, object> { ["central"] = central });
            }
            long before = ctx.Generation;
            ctx.Progress("reloading latest from central");
            try
            {
                DocFiles.Native(ctx, "reloading", doc, () => doc.ReloadLatest(new ReloadLatestOptions()));
            }
            catch (Exception ex)
            {
                throw MapCentralException(ex, central, "Reloading the latest changes of '" + DocFiles.SafeTitle(doc) + "'");
            }
            ctx.Notice("UNDO_CLEARED", "reloading cleared Revit's undo history for '" + DocFiles.SafeTitle(doc) + "'");
            long after = ctx.Generation;
            var data = new Dictionary<string, object> { ["central"] = central, ["changed"] = after != before };
            return OpResult.Success(data, "reloaded the latest changes from central into '" + DocFiles.SafeTitle(doc) + "'").WithDoc(DocFiles.Describe(ctx, doc, rebuild: false));
        }

        [Op("worksharing.relinquish")]
        public static OpResult Relinquish(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            RequireWorkshared(doc, "relinquish");
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("relinquish every element and workset '" + DocFiles.SafeTitle(doc) + "' borrows (changes not yet synchronized stay borrowed)",
                    new Dictionary<string, object> { ["central"] = DocFiles.CentralPath(doc) });
            }
            RelinquishedItems items;
            try
            {
                items = DocFiles.Native(ctx, "sync", doc, () => WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(true), new TransactWithCentralOptions()));
            }
            catch (Exception ex)
            {
                throw MapCentralException(ex, DocFiles.CentralPath(doc), "Relinquishing in '" + DocFiles.SafeTitle(doc) + "'");
            }
            int elements = items?.GetRelinquishedElements()?.Count ?? 0;
            int worksets = items?.GetRelinquishedWorksets()?.Count ?? 0;
            if (SafeModified(doc))
            {
                ctx.Notice("UNSYNCED_KEPT", "elements changed since the last sync stay borrowed until worksharing {\"op\":\"sync\"}");
            }
            var data = new Dictionary<string, object> { ["elements"] = elements, ["worksets"] = worksets };
            return OpResult.Success(data, "relinquished " + elements.ToString(CultureInfo.InvariantCulture) + " element(s) and " + worksets.ToString(CultureInfo.InvariantCulture) + " workset(s)");
        }

        [Op("worksharing.borrow")]
        public static OpResult Borrow(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            RequireWorkshared(doc, "borrow");
            List<Element> elements = Selection(ctx, doc, ctx.Args);
            List<ElementId> ids = elements.Select(e => e.Id).ToList();
            if (ctx.IsPreview)
            {
                WorksharingGuard.Report report = WorksharingGuard.Inspect(doc, ids);
                return DocFiles.StaticPlan("borrow " + ids.Count.ToString(CultureInfo.InvariantCulture) + " element(s)" +
                                           (report.OwnedByOthers.Count > 0 ? "; " + report.OwnedByOthers.Count.ToString(CultureInfo.InvariantCulture) + " are borrowed by " + string.Join(", ", report.Owners) : string.Empty),
                    new Dictionary<string, object> { ["total"] = ids.Count, ["owned_by_others"] = report.OwnedByOthers.Take(50).Cast<object>().ToList() });
            }

            ISet<ElementId> borrowed;
            try
            {
                borrowed = DocFiles.Native(ctx, "sync", doc, () => WorksharingUtils.CheckoutElements(doc, new HashSet<ElementId>(ids), new TransactWithCentralOptions()));
            }
            catch (Exception ex)
            {
                throw MapCentralException(ex, DocFiles.CentralPath(doc), "Borrowing elements in '" + DocFiles.SafeTitle(doc) + "'");
            }
            var got = new HashSet<long>((borrowed ?? new HashSet<ElementId>()).Select(id => id.Value));
            var refused = new List<object>();
            foreach (ElementId id in ids.Where(id => !got.Contains(id.Value)))
            {
                string owner = null;
                CheckoutStatus status;
                try { status = WorksharingUtils.GetCheckoutStatus(doc, id, out owner); } catch { status = CheckoutStatus.NotOwned; }
                if (status == CheckoutStatus.OwnedByCurrentUser) { got.Add(id.Value); continue; }
                refused.Add(new Dictionary<string, object> { ["id"] = id.Value, ["owner"] = owner });
            }
            if (refused.Count > 0)
            {
                ctx.Warn(WarningCodes.PartialResult, refused.Count.ToString(CultureInfo.InvariantCulture) + " element(s) could not be borrowed (owned by another user or changed in central)",
                    ids.Where(id => !got.Contains(id.Value)).Select(id => id.Value).Take(20));
            }
            var data = new Dictionary<string, object>
            {
                ["borrowed"] = got.Count,
                ["refused"] = refused.Take(50).ToList(),
                ["total"] = ids.Count
            };
            return OpResult.Success(data, "borrowed " + got.Count.ToString(CultureInfo.InvariantCulture) + " of " + ids.Count.ToString(CultureInfo.InvariantCulture) + " element(s)");
        }

        [Op("worksharing.enable")]
        public static OpResult Enable(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            if (doc.IsWorkshared)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + DocFiles.SafeTitle(doc) + "' is already workshared.",
                    new Dictionary<string, object> { ["central"] = DocFiles.CentralPath(doc) });
            }
            if (!doc.CanEnableWorksharing())
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Revit cannot enable worksharing for '" + DocFiles.SafeTitle(doc) + "' (cloud, read-only or a template).",
                    new Dictionary<string, object> { ["doc"] = DocFiles.SafeTitle(doc) });
            }
            string defaultWorkset = ctx.Args.Str("name") ?? "Workset1";
            const string levelsWorkset = "Shared Levels and Grids";
            if (string.Equals(defaultWorkset, levelsWorkset, StringComparison.OrdinalIgnoreCase))
            {
                throw OpException.InvalidArgs("name", "must differ from '" + levelsWorkset + "' (Revit creates that workset for levels and grids)",
                    new Dictionary<string, object> { ["op"] = "enable", ["name"] = "Workset1" });
            }
            string path = DocFiles.SafePath(doc);
            bool saved = !string.IsNullOrWhiteSpace(path);
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("enable worksharing in '" + DocFiles.SafeTitle(doc) + "' (worksets '" + levelsWorkset + "' and '" + defaultWorkset + "')" +
                                           (saved ? " and save it as the central model " + path : string.Empty),
                    new Dictionary<string, object> { ["default_workset"] = defaultWorkset, ["levels_workset"] = levelsWorkset, ["central"] = saved ? path : null });
            }

            try
            {
                DocFiles.Native(ctx, "saving", doc, () => doc.EnableWorksharing(levelsWorkset, defaultWorkset));
            }
            catch (Exception ex)
            {
                throw DocFiles.MapFileException(ex, path, "Enabling worksharing in '" + DocFiles.SafeTitle(doc) + "'");
            }

            bool savedAsCentral = false;
            if (saved)
            {
                try
                {
                    var options = new SaveAsOptions { OverwriteExistingFile = true };
                    options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                    DocFiles.Native(ctx, "saving", doc, () => doc.SaveAs(path, options));
                    savedAsCentral = true;
                }
                catch (Exception ex)
                {
                    ctx.Warn(WarningCodes.PartialResult, "worksharing is enabled but saving it as the central model failed (" + ex.Message + "); save it with manage_document {\"op\":\"save_as\",\"as_central\":true}");
                }
            }
            else
            {
                ctx.Notice("SAVE_AS_CENTRAL", "save it once with manage_document {\"op\":\"save_as\",\"path\":\"...\",\"as_central\":true} to create the central model");
            }
            var data = new Dictionary<string, object>
            {
                ["worksets"] = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => (object)w.Name).ToList(),
                ["central"] = savedAsCentral ? path : null
            };
            return OpResult.Success(data, "enabled worksharing in '" + DocFiles.SafeTitle(doc) + "'" + (savedAsCentral ? " and saved it as the central model" : string.Empty))
                .WithDoc(DocFiles.Describe(ctx, doc));
        }

        // ---------------------------------------------------------------------------------------------- writes

        [Op("worksharing.workset_create")]
        public static void WorksetCreate(ChangeContext c)
        {
            Document doc = c.Doc;
            RequireWorkshared(doc, "workset_create");
            string name = c.Args.ReqStr("name").Trim();
            EnsureUniqueName(doc, name);
            Workset workset = Workset.Create(doc, name);
            c.MarkDirty();
            c.Output("workset", workset.Id.IntegerValue);
            c.Data(new Dictionary<string, object> { ["workset"] = new Dictionary<string, object> { ["id"] = workset.Id.IntegerValue, ["name"] = workset.Name } });
            c.Summary("created workset '" + workset.Name + "'");
        }

        [Op("worksharing.workset_rename")]
        public static void WorksetRename(ChangeContext c)
        {
            Document doc = c.Doc;
            RequireWorkshared(doc, "workset_rename");
            Workset workset = Resolve.Workset(doc, c.Args.ReqStr("workset"));
            string name = c.Args.ReqStr("name").Trim();
            if (string.Equals(workset.Name, name, StringComparison.Ordinal))
            {
                c.Summary("workset '" + name + "' already has that name");
                return;
            }
            EnsureUniqueName(doc, name);
            string old = workset.Name;
            WorksetTable.RenameWorkset(doc, workset.Id, name);
            c.MarkDirty();
            c.Output("workset", workset.Id.IntegerValue);
            c.Data(new Dictionary<string, object> { ["workset"] = new Dictionary<string, object> { ["id"] = workset.Id.IntegerValue, ["name"] = name, ["was"] = old } });
            c.Summary("renamed workset '" + old + "' to '" + name + "'");
        }

        [Op("worksharing.set_active")]
        public static void SetActive(ChangeContext c)
        {
            Document doc = c.Doc;
            RequireWorkshared(doc, "set_active");
            Workset workset = Resolve.Workset(doc, c.Args.ReqStr("workset"));
            if (!workset.IsOpen)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Workset '" + workset.Name + "' is closed in '" + doc.Title + "'; open it in Revit (Manage Worksets) first.",
                    new Dictionary<string, object> { ["workset"] = workset.Name });
            }
            WorksetTable table = doc.GetWorksetTable();
            WorksetId previous = table.GetActiveWorksetId();
            table.SetActiveWorksetId(workset.Id);
            c.MarkDirty();
            string was = null;
            try { was = table.GetWorkset(previous)?.Name; } catch { was = null; }
            c.Output("workset", workset.Id.IntegerValue);
            c.Data(new Dictionary<string, object> { ["active"] = workset.Name, ["was"] = was });
            c.Summary("active workset is now '" + workset.Name + "'" + (was != null ? " (was '" + was + "')" : string.Empty));
        }

        [Op("worksharing.move_to")]
        public static void MoveTo(ChangeContext c)
        {
            Document doc = c.Doc;
            RequireWorkshared(doc, "move_to");
            Workset workset = Resolve.Workset(doc, c.Args.ReqStr("workset"));
            List<Element> elements = Selection(c.Ctx, doc, c.Args);
            WorksharingGuard.EnsureEditable(doc, elements.Select(e => e.Id));
            int moved = 0, already = 0;
            var refused = new List<ElementId>();
            for (int index = 0; index < elements.Count; index++)
            {
                Element element = elements[index];
                if (element.WorksetId == workset.Id)
                {
                    already++;
                    continue;
                }
                Parameter parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter == null || parameter.IsReadOnly)
                {
                    refused.Add(element.Id);
                    continue;
                }
                int before = element.WorksetId.IntegerValue;
                if (parameter.Set(workset.Id.IntegerValue))
                {
                    c.Modified(element, "workset", before);
                    moved++;
                }
                else
                {
                    refused.Add(element.Id);
                }
            }
            if (refused.Count > 0)
            {
                c.Warn(WarningCodes.PartialResult, refused.Count.ToString(CultureInfo.InvariantCulture) +
                    " element(s) cannot change workset (hosted elements follow their host; types, views and system elements have fixed worksets)", refused.Take(20));
            }
            if (moved == 0 && already == 0)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "None of the " + elements.Count.ToString(CultureInfo.InvariantCulture) + " element(s) can be moved to a workset.",
                    new Dictionary<string, object> { ["workset"] = workset.Name, ["refused"] = refused.Take(20).Select(id => (object)id.Value).ToList() });
            }
            c.Output("workset", workset.Id.IntegerValue);
            c.Data(new Dictionary<string, object> { ["workset"] = workset.Name, ["moved"] = moved, ["already"] = already, ["refused"] = refused.Count });
            c.Summary("moved " + moved.ToString(CultureInfo.InvariantCulture) + " element(s) to workset '" + workset.Name + "'" +
                      (already > 0 ? " (" + already.ToString(CultureInfo.InvariantCulture) + " already there)" : string.Empty));
        }

        // ---------------------------------------------------------------------------------------------- helpers

        /// <summary>The elements of the sel group: ids, from (r#/last expanded by the broker, or "selection") or filter{}.</summary>
        internal static List<Element> Selection(RequestContext ctx, Document doc, PayloadReader args)
        {
            if (args.Has("ids")) return Resolve.Elements(doc, args, "ids", ctx);
            if (args.Has("from")) return Resolve.Elements(doc, args, "from", ctx);
            PayloadReader filter = args.Sub("filter");
            if (filter != null)
            {
                ElementQuery query = ElementQuery.Compile(doc, filter, ctx, ctx.UiDoc);
                if (query.LinkInstance != null) throw new OpException(ErrorCodes.LinkReadOnly, "filter.link: elements of linked models cannot be changed.", new Dictionary<string, object> { ["link"] = query.LinkName });
                List<Element> found = query.Elements(ctx).ToList();
                if (query.Truncated) throw new OpException(ErrorCodes.InvalidArgs, "filter matched too many elements to collect in time; narrow it (category, level, view).", new Dictionary<string, object> { ["param"] = "filter", ["reason"] = "too broad" });
                if (found.Count == 0)
                {
                    throw new OpException(ErrorCodes.NotFound, "filter matched no elements in '" + doc.Title + "' (" + string.Join(", ", query.Description) + ").",
                        new Dictionary<string, object> { ["param"] = "filter", ["kind"] = "element", ["value"] = string.Join(", ", query.Description), ["candidates"] = new List<object>() });
                }
                return found;
            }
            throw OpException.InvalidArgs("ids", "give the elements: ids, from (r# handle, 'selection' or 'last') or filter {category, level, ...}",
                new Dictionary<string, object> { ["from"] = "selection" });
        }

        private static void RequireWorkshared(Document doc, string op)
        {
            bool workshared;
            try { workshared = doc.IsWorkshared; } catch { workshared = false; }
            if (!workshared)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + DocFiles.SafeTitle(doc) + "' is not workshared, so " + op + " does not apply.",
                    new Dictionary<string, object> { ["doc"] = DocFiles.SafeTitle(doc), ["alternative"] = "worksharing {\"op\":\"enable\"}" });
            }
            bool detached;
            try { detached = doc.IsDetached; } catch { detached = false; }
            if (detached && (op == "sync" || op == "reload_latest" || op == "relinquish" || op == "borrow"))
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + DocFiles.SafeTitle(doc) + "' was opened detached from central, so " + op + " does not apply; save it as a new central first.",
                    new Dictionary<string, object> { ["doc"] = DocFiles.SafeTitle(doc), ["alternative"] = "manage_document {\"op\":\"save_as\",\"as_central\":true}" });
            }
        }

        private static void EnsureUniqueName(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw OpException.InvalidArgs("name", "is required (the workset name)");
            if (WorksetTable.IsWorksetNameUnique(doc, name)) return;
            string next = name;
            for (int n = 2; n < 100; n++)
            {
                next = name + " " + n.ToString(CultureInfo.InvariantCulture);
                if (WorksetTable.IsWorksetNameUnique(doc, next)) break;
            }
            throw new OpException(ErrorCodes.NameTaken, "A workset named '" + name + "' already exists in '" + doc.Title + "'.",
                new Dictionary<string, object> { ["param"] = "name", ["value"] = name, ["next"] = next });
        }

        private static bool SafeModified(Document doc)
        {
            try { return doc.IsModified; } catch { return false; }
        }

        /// <summary>Central-model failures: busy/locked central, missing central, permissions.</summary>
        private static OpException MapCentralException(Exception ex, string central, string what)
        {
            switch (ex)
            {
                case OpException op:
                    return op;
                case Autodesk.Revit.Exceptions.CentralModelContentionException busy:
                    return new OpException(ErrorCodes.RevitRefused, what + ": the central model is busy (another user is synchronizing); try again in a minute.",
                        new Dictionary<string, object> { ["apiMessage"] = busy.Message, ["central"] = central, ["reason"] = "central_busy" });
                default:
                    return DocFiles.MapFileException(ex, central, what);
            }
        }
    }
}
