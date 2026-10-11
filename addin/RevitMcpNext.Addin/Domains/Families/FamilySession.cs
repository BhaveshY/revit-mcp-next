using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The family document that a run of edit_family ops works on (SPEC §6.7 family segments, §5.9 overwrite rules).
    /// <list type="bullet">
    /// <item>Direct: the target document is a family document (doc= an open .rfa). Ops run in that document; the engine's
    /// WriteScope group on it makes the call one undo step.</item>
    /// <item>Background: family= names a loaded family. Document.EditFamily makes an independent copy; every op is one
    /// Transaction in the copy ("MCP &lt;wtag&gt; edit_family.&lt;op&gt;"); after the last op the copy is loaded back
    /// (Document.LoadFamily with no host Transaction open; type values overwritten only when this call changed type
    /// values or the caller asked), then closed without saving. reload:false keeps the copy open instead.</item>
    /// </list>
    /// Created by <see cref="ChangeEngine.RunFamilySegment"/> and published in <see cref="RequestContext.Items"/>.
    /// </summary>
    internal sealed class FamilySession : IDisposable
    {
        public const string ItemKey = "edit_family.session";

        private readonly HashSet<string> _typesChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<long> _symbolsBefore = new HashSet<long>();
        private bool _disposed;

        private FamilySession()
        {
        }

        /// <summary>The family document every op edits.</summary>
        public Document FamilyDoc { get; private set; }

        /// <summary>Background mode: the document the family is loaded in (project, or a host family for nested families).</summary>
        public Document Host { get; private set; }

        /// <summary>Background mode: the family element in <see cref="Host"/>.</summary>
        public Family Family { get; private set; }

        public bool Direct => Host == null;

        /// <summary>Background mode: load the edited copy back after the last op.</summary>
        public bool Reload { get; private set; } = true;

        /// <summary>The caller's overwrite arg (null = default: only when this call changed type values).</summary>
        public bool? OverwriteRequested { get; private set; }

        /// <summary>True once an op changed type parameter values (set_values, add_type values, add_param value).</summary>
        public bool TypeValuesChanged { get; private set; }

        /// <summary>Family types whose values this call changed (types_changed in the result).</summary>
        public IEnumerable<string> TypesChanged => _typesChanged.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        /// <summary>Type renames in order (applied to the host's types before the reload so the project keeps its instances).</summary>
        public List<KeyValuePair<string, string>> TypeRenames { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>Types deleted in the family (unused copies are removed from the host after the reload).</summary>
        public List<string> TypesDeleted { get; } = new List<string>();

        /// <summary>Types added in the family.</summary>
        public List<string> TypesAdded { get; } = new List<string>();

        /// <summary>Background mode with reload:false: the edited copy stays open as its own document.</summary>
        public bool KeptOpen { get; private set; }

        public FamilyManager Manager => FamilyDoc.FamilyManager;

        public string FamilyName => FamilyLookup.FamilyNameOf(FamilyDoc);

        // -------------------------------------------------------------------------------------------------------
        // Lifetime
        // -------------------------------------------------------------------------------------------------------

        /// <summary>The session of the running segment (set by the engine's family-segment runner).</summary>
        public static FamilySession Of(ChangeContext c)
        {
            if (c.Ctx.Items.TryGetValue(ItemKey, out object value) && value is FamilySession session) return session;
            throw new OpException(ErrorCodes.InternalError, c.Key + " ran outside a family session (engine wiring).",
                new Dictionary<string, object> { ["key"] = c.Key });
        }

        /// <summary>
        /// Opens the session for the first op of a segment: family= → background copy of the loaded family (a family
        /// document naming itself is direct); no family= → the target must be a family document.
        /// </summary>
        public static FamilySession Open(ChangeContext c)
        {
            Document doc = c.Doc ?? throw new OpException(ErrorCodes.NoOpenDocument, "No document for " + c.Key + ".");
            string familyArg = c.Str("family");
            var session = new FamilySession { OverwriteRequested = c.Args.BoolOpt("overwrite") };

            if (string.IsNullOrWhiteSpace(familyArg) || (doc.IsFamilyDocument && Resolve.Eq(FamilyLookup.FamilyNameOf(doc), familyArg)))
            {
                if (!doc.IsFamilyDocument)
                {
                    throw new OpException(ErrorCodes.FamilyDocRequired,
                        "edit_family." + c.Op + " needs family=<loaded family> or a family document; '" + FamilyLookup.SafeTitle(doc) + "' is a project.",
                        new Dictionary<string, object> { ["doc"] = FamilyLookup.SafeTitle(doc), ["family"] = null, ["options"] = FamilyDocOptions(c.Ctx) });
                }
                if (c.Has("reload")) c.Warn(WarningCodes.ParamIgnored, "reload applies only with family= (a loaded family); this edits the open family document " + FamilyLookup.SafeTitle(doc));
                session.FamilyDoc = doc;
                return session;
            }

            FamilyLookup.Found found = FamilyLookup.Find(doc, familyArg, c.Args.Field("family"));
            Family family = found.Family;
            FamilyLookup.EnsureEditable(family);
            if (!c.IsPreview && doc.IsWorkshared) WorksharingGuard.EnsureEditable(doc, family);

            Document copy;
            try
            {
                copy = doc.EditFamily(family);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                Document open = FamilyLookup.OpenEditOf(c.Ctx, doc, family);
                string where = open == null ? string.Empty : " as '" + FamilyLookup.SafeTitle(open) + "'";
                throw new OpException(ErrorCodes.RevitRefused,
                    "Revit cannot open family '" + FamilyLookup.SafeName(family) + "' for editing" + where + ": " + ex.Message +
                    (open == null ? string.Empty : " Edit that document with doc= and load_into, or close it first."),
                    new Dictionary<string, object> { ["apiMessage"] = ex.Message, ["family"] = FamilyLookup.SafeName(family), ["openDoc"] = open == null ? null : FamilyLookup.SafeTitle(open) });
            }
            if (copy == null)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not open family '" + FamilyLookup.SafeName(family) + "' for editing.",
                    new Dictionary<string, object> { ["apiMessage"] = "EditFamily returned null", ["family"] = FamilyLookup.SafeName(family) });
            }

            session.FamilyDoc = copy;
            session.Host = doc;
            session.Family = family;
            session.Reload = c.Bool("reload", true);
            foreach (FamilySymbol symbol in FamilyLookup.Symbols(doc, family)) session._symbolsBefore.Add(symbol.Id.Value);
            return session;
        }

        /// <summary>Closes the background copy (unless it was kept open). Never throws.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Direct || KeptOpen || FamilyDoc == null) return;
            try
            {
                if (FamilyDoc.IsValidObject) FamilyDoc.Close(false);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("family", "Closing the edit copy of family '" + FamilyLookup.SafeName(Family) + "' failed.", ex);
            }
        }

        // -------------------------------------------------------------------------------------------------------
        // Editing
        // -------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="body"/> in one Transaction of the family document named "MCP &lt;wtag&gt; edit_family.&lt;op&gt;"
        /// ("MCP temp preview" in preview mode) with failure capture. A Revit-side rollback throws
        /// <paramref name="rollbackCode"/> (default REVIT_TRANSACTION_ROLLED_BACK) with the failures.
        /// </summary>
        public void Edit(ChangeContext c, Action<FamilyManager> body, string rollbackCode = null)
        {
            string name = c.IsPreview ? Naming.Temp(Naming.Purposes.Preview) : Naming.FamilyWrite(c.Ctx.WriteTag, c.Op);
            TransactionStatus status;
            using (var transaction = new Transaction(FamilyDoc, name))
            {
                FailureCapture.Configure(transaction, c.Failures);
                TransactionStatus start = transaction.Start();
                if (start != TransactionStatus.Started)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start a transaction in family '" + FamilyName + "' (" + start + ").");
                }
                try
                {
                    body(Manager);
                    status = transaction.Commit();
                }
                catch
                {
                    TempScope.RollBackQuietly(transaction);
                    throw;
                }
            }
            if (status == TransactionStatus.Committed) return;
            var tx = new TxResult { Committed = false, Status = status };
            tx.Errors.AddRange(c.Failures.Errors);
            tx.Warnings.AddRange(c.Failures.Warnings);
            OpException failure = tx.ToException(c.Key);
            if (rollbackCode == null) throw failure;
            throw new OpException(rollbackCode, failure.Message, failure.Details);
        }

        /// <summary>Remembers that these types' values changed (drives the overwrite default and types_changed).</summary>
        public void NoteTypeValues(IEnumerable<string> typeNames)
        {
            bool any = false;
            foreach (string name in typeNames ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(name)) continue;
                _typesChanged.Add(name);
                any = true;
            }
            if (any) TypeValuesChanged = true;
        }

        // -------------------------------------------------------------------------------------------------------
        // Finish (after the last op of the segment)
        // -------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Background mode: loads the edited copy back into the host (records created/modified host ids on
        /// <paramref name="c"/>), or keeps it open (reload:false). Direct mode and previews only add result data.
        /// </summary>
        public void Finish(ChangeContext c)
        {
            Dictionary<string, object> data = DataOf(c);
            if (Direct)
            {
                data["types_changed"] = TypesChanged.Cast<object>().ToList();
                return;
            }

            data["family"] = FamilyLookup.SafeName(Family);
            data["types_changed"] = TypesChanged.Cast<object>().ToList();
            if (c.IsPreview)
            {
                data["reload"] = new Dictionary<string, object> { ["skipped"] = "preview", ["overwrite"] = OverwriteRequested ?? TypeValuesChanged };
                return;
            }

            if (!Reload)
            {
                KeptOpen = true;
                DocumentRegistry registry = c.Ctx.Registry;
                try
                {
                    registry?.SetFamilySource(FamilyDoc, Host);
                    registry?.Rebuild(c.Ctx.RevitApp, c.App);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Error("family", "Registering the kept edit copy failed.", ex);
                }
                data["reload"] = new Dictionary<string, object> { ["skipped"] = "reload false", ["open_doc"] = FamilyLookup.SafeTitle(FamilyDoc) };
                c.Notice(NoticeCodes.FamilyEditing, "the edited family " + FamilyLookup.SafeTitle(FamilyDoc) + " stays open (not loaded into " +
                    FamilyLookup.SafeTitle(Host) + "); edit it with doc=, then edit_family load_into");
                AppendSummary(c, "kept open, not reloaded");
                return;
            }

            bool overwrite = OverwriteRequested ?? TypeValuesChanged;
            ApplyTypeRenamesToHost(c);
            Family loaded = FamilyDoc.LoadFamily(Host, new FamilyReloadOptions(overwrite));
            if (loaded == null)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit did not reload family '" + FamilyName + "' into '" + FamilyLookup.SafeTitle(Host) + "'.",
                    new Dictionary<string, object> { ["apiMessage"] = "LoadFamily returned null" });
            }
            Family = loaded;

            var createdTypes = new List<object>();
            var modifiedTypes = new List<object>();
            foreach (FamilySymbol symbol in FamilyLookup.Symbols(Host, loaded))
            {
                if (!_symbolsBefore.Contains(symbol.Id.Value))
                {
                    c.Created(symbol.Id);
                    createdTypes.Add(new List<object> { symbol.Id.Value, symbol.Name });
                }
                else if (_typesChanged.Contains(symbol.Name) && overwrite)
                {
                    c.Modified(symbol.Id);
                    modifiedTypes.Add(new List<object> { symbol.Id.Value, symbol.Name });
                }
            }
            c.Modified(loaded.Id);
            List<object> removed = RemoveDeletedTypesFromHost(c, loaded);

            var reload = new Dictionary<string, object>
            {
                ["into"] = FamilyLookup.SafeTitle(Host),
                ["family_id"] = loaded.Id.Value,
                ["overwrite"] = overwrite
            };
            if (createdTypes.Count > 0) reload["types_created"] = createdTypes;
            if (modifiedTypes.Count > 0) reload["types_overwritten"] = modifiedTypes;
            if (removed.Count > 0) reload["types_removed"] = removed;
            data["reload"] = reload;
            data["project_types"] = FamilyLookup.Symbols(Host, loaded).Take(50).Select(s => (object)new List<object> { s.Id.Value, s.Name }).ToList();
            c.Output("family", loaded.Id);
            AppendSummary(c, "reloaded into " + FamilyLookup.SafeTitle(Host) + (overwrite ? " (project type values overwritten)" : " (project type values kept)"));
        }

        private static Dictionary<string, object> DataOf(ChangeContext c)
        {
            if (c.DataValue is Dictionary<string, object> existing) return existing;
            var data = new Dictionary<string, object>(StringComparer.Ordinal);
            if (c.DataValue is IDictionary<string, object> other) foreach (KeyValuePair<string, object> pair in other) data[pair.Key] = pair.Value;
            c.Data(data);
            return data;
        }

        private static void AppendSummary(ChangeContext c, string text)
        {
            c.Summary(string.IsNullOrEmpty(c.SummaryText) ? text : c.SummaryText + "; " + text);
        }

        /// <summary>Renames the host's types like the family's renames, so the reload maps them (instances keep their type).</summary>
        private void ApplyTypeRenamesToHost(ChangeContext c)
        {
            if (TypeRenames.Count == 0) return;
            RunHostTransaction(c, () =>
            {
                foreach (KeyValuePair<string, string> rename in TypeRenames)
                {
                    List<FamilySymbol> symbols = FamilyLookup.Symbols(Host, Family);
                    FamilySymbol from = symbols.FirstOrDefault(s => string.Equals(s.Name, rename.Key, StringComparison.Ordinal)) ??
                                        symbols.FirstOrDefault(s => Resolve.Eq(s.Name, rename.Key));
                    if (from == null) continue;
                    if (symbols.Any(s => s.Id != from.Id && Resolve.Eq(s.Name, rename.Value)))
                    {
                        c.Warn(WarningCodes.PartialResult, "type '" + rename.Key + "' was renamed in the family, but '" + FamilyLookup.SafeTitle(Host) +
                            "' already has a type '" + rename.Value + "'; the project keeps both", new[] { from.Id });
                        continue;
                    }
                    c.Modified(from, "name", from.Name);
                    from.Name = rename.Value;
                }
            });
        }

        /// <summary>Deletes host types whose family type was deleted, when nothing uses them; warns otherwise.</summary>
        private List<object> RemoveDeletedTypesFromHost(ChangeContext c, Family loaded)
        {
            var removed = new List<object>();
            if (TypesDeleted.Count == 0) return removed;
            Dictionary<long, int> counts = FamilyLookup.InstanceCounts(Host, loaded);
            var delete = new List<ElementId>();
            List<FamilySymbol> symbols = FamilyLookup.Symbols(Host, loaded);
            foreach (string name in TypesDeleted.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                FamilySymbol symbol = symbols.FirstOrDefault(s => Resolve.Eq(s.Name, name));
                if (symbol == null) continue;
                counts.TryGetValue(symbol.Id.Value, out int used);
                if (used > 0)
                {
                    c.Warn(WarningCodes.PartialResult, "type '" + name + "' was deleted in the family but " + used.ToString(CultureInfo.InvariantCulture) +
                        " instance(s) in '" + FamilyLookup.SafeTitle(Host) + "' still use it; the project keeps that type", new[] { symbol.Id });
                    continue;
                }
                if (symbols.Count - delete.Count <= 1) continue;
                delete.Add(symbol.Id);
                removed.Add(new List<object> { symbol.Id.Value, symbol.Name });
            }
            if (delete.Count > 0) RunHostTransaction(c, () => c.Delete(delete));
            return removed;
        }

        /// <summary>One Transaction in the host (inside the engine's WriteScope group), named like the write.</summary>
        private void RunHostTransaction(ChangeContext c, Action action)
        {
            TransactionStatus status;
            using (var transaction = new Transaction(Host, Naming.Write(c.Ctx.WriteTag, c.Key)))
            {
                FailureCapture.Configure(transaction, c.Failures);
                TransactionStatus start = transaction.Start();
                if (start != TransactionStatus.Started)
                {
                    throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start a transaction in '" + FamilyLookup.SafeTitle(Host) + "' (" + start + ").");
                }
                try
                {
                    action();
                    status = transaction.Commit();
                }
                catch
                {
                    TempScope.RollBackQuietly(transaction);
                    throw;
                }
            }
            if (status == TransactionStatus.Committed) return;
            var tx = new TxResult { Committed = false, Status = status };
            tx.Errors.AddRange(c.Failures.Errors);
            tx.Warnings.AddRange(c.Failures.Warnings);
            throw tx.ToException(c.Key);
        }

        /// <summary>Open family documents as {rid, title, kind} options (FAMILY_DOC_REQUIRED details).</summary>
        internal static List<object> FamilyDocOptions(RequestContext ctx)
        {
            DocumentRegistry registry = ctx?.Registry;
            if (registry == null) return new List<object>();
            return registry.Current.Docs
                .Where(doc => doc.Kind == "family")
                .Select(doc => (object)new Dictionary<string, object> { ["rid"] = doc.Rid, ["title"] = doc.Title, ["kind"] = doc.Kind })
                .ToList();
        }
    }
}
