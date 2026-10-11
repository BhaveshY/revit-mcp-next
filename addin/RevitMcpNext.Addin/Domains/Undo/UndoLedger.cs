using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Undo
{
    /// <summary>What one MCP write did, as far as undo compensation needs it.</summary>
    internal sealed class LedgerRecord
    {
        public long Rid { get; set; }
        public string DocKey { get; set; }
        public string RequestId { get; set; }
        public string WriteTag { get; set; }
        public string ClientKey { get; set; }
        /// <summary>The request's registry key (create_elements.wall, change_set, run_csharp, ...).</summary>
        public string Key { get; set; }
        /// <summary>The registry keys the write ran (the ops of a change_set; else just Key).</summary>
        public List<string> OpKeys { get; set; } = new List<string>();
        public string Name { get; set; }
        public DateTime AtUtc { get; set; }
        /// <summary>Elements the write created and did not delete again (in creation order).</summary>
        public List<long> Created { get; } = new List<long>();
        /// <summary>Before-values the write's ops recorded (ChangeEngine.Committed); input for future compensation of edits.</summary>
        public List<ModifiedDetail> Details { get; } = new List<ModifiedDetail>();
        public int DeletedCount { get; set; }
        public int ModifiedCount { get; set; }
        /// <summary>The request id of the compensation write that deleted this write's elements (null when none).</summary>
        public string CompensatedBy { get; set; }

        public string Label => (WriteTag ?? "w?") + " " + (Key ?? "write");
    }

    /// <summary>
    /// The UndoLedger of SPEC §6.9 (mode compensate): one record per MCP write and document, built from
    /// DocumentRegistry.DocumentChangeRecorded (ids per committed transaction attributed to the executing request) plus
    /// the request's op keys. Compensation is exact only where the inverse is known from these facts: writes whose ops
    /// all create new elements are undone by deleting what they created. Writes that changed or deleted existing
    /// elements (moves, parameters, types, deletes, purge, family loads, code) are irreversible by compensation, since
    /// their before-values (ChangeEngine.Committed fills LedgerRecord.Details) are recorded but not yet replayed.
    /// In memory per Revit session (Revit's undo stack is too).
    /// </summary>
    internal static class UndoLedger
    {
        public const int MaxRecordsPerDoc = 200;

        /// <summary>Ops whose only effect is creating new elements (their compensation deletes them).</summary>
        private static readonly HashSet<string> CreationKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "place_family.place",
            "modify_elements.copy", "modify_elements.array", "modify_elements.place_group",
            "structure.column", "structure.beam", "structure.brace", "structure.beam_system", "structure.foundation",
            "mep.pipe", "mep.duct", "mep.conduit", "mep.cable_tray", "mep.flex_pipe", "mep.flex_duct", "mep.space", "mep.zone",
            "annotate.tag", "annotate.tag_all", "annotate.text", "annotate.dimension", "annotate.spot", "annotate.detail_line",
            "annotate.filled_region", "annotate.detail_item", "annotate.revision_cloud", "annotate.copy_to_views",
            "edit_views.create", "edit_views.duplicate", "edit_views.create_template",
            "edit_sheets.create", "edit_sheets.duplicate", "edit_sheets.revision_create",
            "edit_schedules.create", "edit_schedules.duplicate",
            "edit_types.duplicate_type", "edit_types.material_create"
        };

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, List<LedgerRecord>> Records = new Dictionary<long, List<LedgerRecord>>();
        private static DocumentRegistry _attachedTo;

        /// <summary>
        /// Subscribes to the registry once (idempotent). RevitMcpApplication calls it right after the DocumentRegistry
        /// attaches, so writes made before the first undo call are recorded; undo calls it again as a safety net.
        /// </summary>
        public static bool Attach(DocumentRegistry registry)
        {
            if (registry == null) return false;
            lock (Gate)
            {
                if (ReferenceEquals(_attachedTo, registry)) return true;
                if (_attachedTo != null) _attachedTo.DocumentChangeRecorded -= OnRecorded;
                registry.DocumentChangeRecorded += OnRecorded;
                if (_attachedTo == null) ChangeEngine.Committed += OnCommitted;
                _attachedTo = registry;
                Records.Clear();
            }
            return true;
        }

        public static bool IsCreationKey(string key) => key != null && (CreationKeys.Contains(key) || key.StartsWith("create_elements.", StringComparison.Ordinal));

        /// <summary>True while the compensation of this record is applied (an undone compensation revives the record).</summary>
        public static bool IsCompensated(LedgerRecord record, ICollection<string> undoneRequests)
        {
            return record.CompensatedBy != null && (undoneRequests == null || !undoneRequests.Contains(record.CompensatedBy));
        }

        /// <summary>Why a record cannot be compensated (null when it can).</summary>
        public static string IrreversibleReason(LedgerRecord record)
        {
            if (record.Key == "undo") return record.Label + " is itself a compensation";
            if (record.Key == "run_csharp") return record.Label + " ran C# code, which is never compensated automatically";
            List<string> keys = record.OpKeys.Count > 0 ? record.OpKeys : new List<string> { record.Key };
            string other = keys.FirstOrDefault(k => !IsCreationKey(k));
            if (other != null)
            {
                return record.Label + (keys.Count > 1 ? " ran " + other + ", which" : "") +
                       " changed or deleted existing elements; their previous state is not recorded";
            }
            if (record.Created.Count == 0) return record.Label + " created no elements";
            return null;
        }

        /// <summary>The records of a document, newest first.</summary>
        public static List<LedgerRecord> For(long rid)
        {
            lock (Gate)
            {
                return Records.TryGetValue(rid, out List<LedgerRecord> list) ? Enumerable.Reverse(list).ToList() : new List<LedgerRecord>();
            }
        }

        public static void MarkCompensated(IEnumerable<LedgerRecord> records, string compensationRequestId)
        {
            lock (Gate)
            {
                foreach (LedgerRecord record in records) record.CompensatedBy = compensationRequestId ?? "compensated";
            }
        }

        /// <summary>Adds the ops' before-values to the record of the committed write (recorded by OnRecorded at commit).</summary>
        private static void OnCommitted(RequestContext ctx, IReadOnlyList<OpOutcome> outcomes)
        {
            if (ctx == null || outcomes == null || string.IsNullOrEmpty(ctx.RequestId)) return;
            lock (Gate)
            {
                if (!Records.TryGetValue(ctx.Rid, out List<LedgerRecord> list)) return;
                LedgerRecord record = list.LastOrDefault(r => r.RequestId == ctx.RequestId);
                if (record == null) return;
                foreach (OpOutcome outcome in outcomes)
                {
                    if (outcome?.Details != null) record.Details.AddRange(outcome.Details);
                }
            }
        }

        private static void OnRecorded(DocumentChangeRecord change)
        {
            try
            {
                JournalEntry entry = change?.Entry;
                if (entry != null && entry.Operation == "history_cleared")
                {
                    // Revit cleared the undo history: nothing recorded before can be compensated against it any more.
                    lock (Gate) Records.Remove(change.Rid);
                    return;
                }
                OursInfo ours = entry?.Ours;
                if (entry == null || ours == null || string.IsNullOrEmpty(ours.RequestId)) return;
                lock (Gate)
                {
                    if (!Records.TryGetValue(change.Rid, out List<LedgerRecord> list))
                    {
                        list = new List<LedgerRecord>();
                        Records[change.Rid] = list;
                    }

                    if (entry.Operation == "group_rolled_back")
                    {
                        // NOT APPLIED or failed writes: the group was rolled back, so nothing of it exists.
                        list.RemoveAll(r => r.RequestId == ours.RequestId);
                        return;
                    }
                    if (entry.Operation != "committed" || entry.Class != NameClass.Write) return;

                    LedgerRecord record = list.FirstOrDefault(r => r.RequestId == ours.RequestId);
                    if (record == null)
                    {
                        record = new LedgerRecord
                        {
                            Rid = change.Rid,
                            DocKey = change.DocKey,
                            RequestId = ours.RequestId,
                            WriteTag = ours.WriteTag,
                            ClientKey = ours.ClientKey,
                            Key = ours.Key,
                            Name = entry.Names?.FirstOrDefault(),
                            AtUtc = entry.AtUtc
                        };
                        record.OpKeys.AddRange(OpKeysOf(ours));
                        list.Add(record);
                        if (list.Count > MaxRecordsPerDoc) list.RemoveRange(0, list.Count - MaxRecordsPerDoc);
                    }

                    var created = new HashSet<long>(record.Created);
                    foreach (ElementId id in change.AddedIds ?? new List<ElementId>())
                    {
                        if (id != null && created.Add(id.Value)) record.Created.Add(id.Value);
                    }
                    foreach (ElementId id in change.DeletedIds ?? new List<ElementId>())
                    {
                        if (id == null) continue;
                        if (created.Remove(id.Value)) record.Created.Remove(id.Value);
                        else record.DeletedCount++;
                    }
                    record.ModifiedCount += change.ModifiedIds?.Count ?? 0;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("undo", "Recording a write for compensation failed: " + ex.Message);
            }
        }

        /// <summary>The op keys of the executing request (a change_set's ops; else its own key).</summary>
        private static IEnumerable<string> OpKeysOf(OursInfo ours)
        {
            RequestContext ctx = RequestContext.Current;
            if (ctx == null || !string.Equals(ctx.RequestId, ours.RequestId, StringComparison.Ordinal) || ours.Key != "change_set")
            {
                return new[] { ours.Key };
            }
            var keys = new List<string>();
            try
            {
                foreach (PayloadReader op in ctx.Args.SubList("ops"))
                {
                    string key = op.Str("key");
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        string tool = op.Str("tool");
                        string discriminator = ctx.Ops?.Catalog.DiscriminatorOf(tool) ?? "op";
                        string value = op.Str(discriminator) ?? op.Str("op");
                        key = string.IsNullOrWhiteSpace(value) ? tool : tool + "." + value;
                    }
                    keys.Add(key);
                }
            }
            catch
            {
                keys.Clear();
            }
            return keys.Count > 0 ? (IEnumerable<string>)keys : new[] { ours.Key };
        }
    }
}
