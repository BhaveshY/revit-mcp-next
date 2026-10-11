using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpNext.Addin.Undo
{
    /// <summary>One entry of Revit's undo (or redo) stack as reconstructed from the transaction journal.</summary>
    internal sealed class UndoStep
    {
        public UndoStep(JournalEntry first)
        {
            Entries.Add(first);
        }

        /// <summary>The journal entries Revit shows as this one undo step (the inner transactions of an assimilated group).</summary>
        public List<JournalEntry> Entries { get; } = new List<JournalEntry>();

        /// <summary>undone/redone events already seen for this step (a group may report one per inner transaction).</summary>
        public int Events { get; set; }

        public JournalEntry First => Entries[0];
        public IReadOnlyList<string> Names => First.Names ?? new List<string>();
        public NameClass Class => First.Class;
        public OursInfo Ours => Entries.Select(e => e.Ours).FirstOrDefault(o => o != null);
        public bool IsUi => Class == NameClass.Ui;

        /// <summary>A write of the given broker session (clientKey).</summary>
        public bool IsWriteOf(string clientKey)
        {
            return Class == NameClass.Write && Ours != null && !string.IsNullOrEmpty(clientKey) &&
                   string.Equals(Ours.ClientKey, clientKey, StringComparison.Ordinal);
        }

        /// <summary>"w17 create_elements.wall" for our names, the transaction name otherwise.</summary>
        public string Label
        {
            get
            {
                string name = Names.FirstOrDefault() ?? "(unnamed change)";
                return Naming.IsMcpName(name) ? name.Substring(Naming.Prefix.Length) : name;
            }
        }

        public bool SameNames(IReadOnlyList<string> names)
        {
            IReadOnlyList<string> mine = Names;
            if (names == null || names.Count == 0 || mine.Count == 0) return false;
            return mine.Intersect(names, StringComparer.Ordinal).Any();
        }
    }

    /// <summary>
    /// Revit's undo and redo stacks of one document as far as the journal knows them (DocumentRegistry keeps the last
    /// 200 DocumentChanged records): committed entries push (consecutive commits of one MCP request collapse into one
    /// assimilated step) and clear the redo stack, undone/redone entries move steps between the stacks, and group
    /// rollbacks remove the request's inner commits again. Changes older than the journal are unknown (bottom of stack).
    /// </summary>
    internal sealed class UndoStack
    {
        /// <summary>Oldest first; the top is the last element.</summary>
        public List<UndoStep> Undo { get; } = new List<UndoStep>();

        /// <summary>Oldest first; the top is the last element.</summary>
        public List<UndoStep> Redo { get; } = new List<UndoStep>();

        /// <summary>False when the journal was full (older history was dropped).</summary>
        public bool Complete { get; private set; }

        public UndoStep UndoTop => Undo.Count == 0 ? null : Undo[Undo.Count - 1];

        /// <summary>Builds the stacks from the registry journal (newest first, as GetJournal returns it).</summary>
        public static UndoStack From(IReadOnlyList<JournalEntry> newestFirst, int capacity)
        {
            var stack = new UndoStack { Complete = newestFirst.Count < capacity };
            foreach (JournalEntry entry in newestFirst.Reverse())
            {
                switch (entry.Operation)
                {
                    case "committed":
                        stack.Commit(entry);
                        break;
                    case "undone":
                        Move(stack.Undo, stack.Redo, entry);
                        break;
                    case "redone":
                        Move(stack.Redo, stack.Undo, entry);
                        break;
                    case "group_rolled_back":
                        stack.RollBackGroup(entry);
                        break;
                    case "history_cleared":
                        // Revit dropped its undo/redo history (sync with central, reload latest).
                        stack.Undo.Clear();
                        stack.Redo.Clear();
                        break;
                }
            }
            return stack;
        }

        /// <summary>Request ids of steps currently undone (on the redo stack).</summary>
        public HashSet<string> UndoneRequests()
        {
            return new HashSet<string>(Redo.Select(s => s.Ours?.RequestId).Where(id => id != null), StringComparer.Ordinal);
        }

        /// <summary>Request ids of steps currently applied (on the undo stack).</summary>
        public HashSet<string> AppliedRequests()
        {
            return new HashSet<string>(Undo.Select(s => s.Ours?.RequestId).Where(id => id != null), StringComparer.Ordinal);
        }

        private void Commit(JournalEntry entry)
        {
            UndoStep top = UndoTop;
            if (top != null && SameRequest(top, entry) && top.SameNames(entry.Names))
            {
                top.Entries.Add(entry);
            }
            else
            {
                Undo.Add(new UndoStep(entry));
            }
            Redo.Clear();
        }

        private static void Move(List<UndoStep> from, List<UndoStep> to, JournalEntry entry)
        {
            // A group may report one event per inner transaction: extra events for the step just moved are continuations.
            UndoStep moved = to.Count == 0 ? null : to[to.Count - 1];
            UndoStep next = from.Count == 0 ? null : from[from.Count - 1];
            if (moved != null && moved.Entries.Count > 1 && moved.Events < moved.Entries.Count && moved.SameNames(entry.Names) &&
                (next == null || !next.SameNames(entry.Names)))
            {
                moved.Events++;
                return;
            }
            if (next == null) return;
            from.RemoveAt(from.Count - 1);
            next.Events = 1;
            to.Add(next);
        }

        private void RollBackGroup(JournalEntry entry)
        {
            string request = entry.Ours?.RequestId;
            while (Undo.Count > 0)
            {
                UndoStep top = UndoTop;
                bool sameRequest = request != null && string.Equals(top.Ours?.RequestId, request, StringComparison.Ordinal);
                if (!sameRequest && !(request == null && top.SameNames(entry.Names))) break;
                Undo.RemoveAt(Undo.Count - 1);
            }
        }

        private static bool SameRequest(UndoStep step, JournalEntry entry)
        {
            string a = step.Ours?.RequestId;
            string b = entry.Ours?.RequestId;
            return a != null && b != null && string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}
