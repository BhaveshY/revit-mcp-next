using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// get_changes (SPEC Appendix A, §6.6 m#): net added/modified/deleted elements and the transaction names since a
    /// point, from the DocumentRegistry change ring and journal (temp transactions never count). The broker turns the
    /// since grammar into numbers: since_gen (a mark m# = doc generation), since_write (the w# of the session's last write
    /// on this doc) or session_gen (the generation when the session first saw the doc). "session" also reaches back to
    /// the session's first own transaction in the journal. incomplete:true when the ring overflowed since then.
    /// </summary>
    internal static class ChangeOps
    {
        private const int MaxTransactions = 30;

        [Op("get_changes")]
        public static OpResult Changes(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            DocumentRegistry registry = ctx.Registry ?? throw new OpException(ErrorCodes.RevitStarting, "The add-in is still starting.");
            long now = registry.GetGeneration(doc);
            IReadOnlyList<JournalEntry> journal = registry.GetJournal(doc); // newest first
            string since = (ctx.Args.Str("since", "session") ?? "session").Trim();
            long? sinceGen = ctx.Args.LongOpt("since_gen");
            string sinceWrite = ctx.Args.Str("since_write");
            string from;
            long baseline;

            if (sinceGen.HasValue)
            {
                baseline = sinceGen.Value;
                from = since.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? "mark " + since : "generation " + baseline.ToString(CultureInfo.InvariantCulture);
            }
            else if (!string.IsNullOrWhiteSpace(sinceWrite) || since.Equals("last", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(sinceWrite))
                {
                    throw OpException.InvalidArgs("since", "'last' needs the broker (it knows your last write); pass since_write or a generation");
                }
                JournalEntry entry = journal.FirstOrDefault(e => IsWrite(e, sinceWrite, ctx.ClientKey));
                if (entry != null)
                {
                    baseline = entry.Generation - 1;
                    from = "your write " + sinceWrite;
                }
                else
                {
                    baseline = SessionBaseline(ctx, journal, out from);
                    ctx.Warn(WarningCodes.PartialResult, "write " + sinceWrite + " is no longer in the transaction journal (or was undone); showing changes since " + from);
                }
            }
            else if (since.Equals("session", StringComparison.OrdinalIgnoreCase))
            {
                baseline = SessionBaseline(ctx, journal, out from);
            }
            else if (long.TryParse(since, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                baseline = parsed;
                from = "generation " + parsed.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                throw OpException.InvalidArgs("since", "must be 'session', 'last' or a mark like m12 (got '" + since + "')");
            }
            baseline = Math.Max(0, Math.Min(baseline, now));

            ChangeQuery query = registry.GetChanges(doc, baseline);
            var items = new List<KeyValuePair<long, string>>();
            items.AddRange(query.Added.Select(id => new KeyValuePair<long, string>(id, "added")));
            items.AddRange(query.Modified.Select(id => new KeyValuePair<long, string>(id, "modified")));
            items.AddRange(query.Deleted.Select(id => new KeyValuePair<long, string>(id, "deleted")));

            var transactions = new List<object>();
            int yours = 0, mcp = 0, user = 0;
            foreach (JournalEntry entry in query.Transactions)
            {
                string by = By(entry, ctx.ClientKey);
                if (by == "you") yours++;
                else if (by == "mcp") mcp++;
                else user++;
            }
            foreach (JournalEntry entry in query.Transactions.Skip(Math.Max(0, query.Transactions.Count - MaxTransactions)))
            {
                transactions.Add(new List<object>
                {
                    entry.Generation, string.Join(" + ", entry.Names ?? new List<string>()), By(entry, ctx.ClientKey), entry.Operation,
                    entry.Added + entry.Modified + entry.Deleted
                });
            }

            var extra = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["since"] = baseline,
                ["now"] = now,
                ["from"] = from,
                ["added"] = query.Added.Count,
                ["modified"] = query.Modified.Count,
                ["deleted"] = query.Deleted.Count,
                ["transactions"] = new Dictionary<string, object>
                {
                    ["total"] = query.Transactions.Count,
                    ["cols"] = new List<object> { "gen", "name", "by", "op", "elements" },
                    ["rows"] = transactions
                }
            };
            if (query.Incomplete)
            {
                extra["incomplete"] = true;
                ctx.Warn(WarningCodes.PartialResult, "the change history overflowed since " + from + "; element lists are incomplete");
            }

            string summary = items.Count == 0 && query.Transactions.Count == 0
                ? "no changes since " + from
                : query.Added.Count.ToString("N0", CultureInfo.InvariantCulture) + " added, " + query.Modified.Count.ToString("N0", CultureInfo.InvariantCulture) + " modified, " +
                  query.Deleted.Count.ToString("N0", CultureInfo.InvariantCulture) + " deleted since " + from + " (" +
                  ReadKit.Count(query.Transactions.Count, "transaction") + ": " + yours.ToString(CultureInfo.InvariantCulture) + " yours, " +
                  user.ToString(CultureInfo.InvariantCulture) + " by the user" + (mcp > 0 ? ", " + mcp.ToString(CultureInfo.InvariantCulture) + " by other MCP sessions" : string.Empty) + ")";
            return ReadKit.Page(ctx, items, new[] { "id", "change", "category" }, pair =>
            {
                string category = null;
                if (pair.Value != "deleted") category = Rows.CategoryLabel(Resolve.SafeGet(doc, new ElementId(pair.Key)));
                return new List<object> { pair.Key, pair.Value, category };
            }, summary, extra);
        }

        /// <summary>
        /// The session's start on this doc: the earlier of the broker's first sight (session_gen) and the session's first
        /// own transaction still in the journal; otherwise generation 0 (when the add-in started tracking the doc).
        /// </summary>
        private static long SessionBaseline(RequestContext ctx, IReadOnlyList<JournalEntry> journal, out string from)
        {
            long? sessionGen = ctx.Args.LongOpt("session_gen");
            JournalEntry firstOwn = journal.LastOrDefault(e => e.Ours != null && string.Equals(e.Ours.ClientKey, ctx.ClientKey, StringComparison.Ordinal));
            long? ownGen = firstOwn == null ? (long?)null : firstOwn.Generation - 1;
            if (sessionGen.HasValue && ownGen.HasValue)
            {
                from = "this session started";
                return Math.Min(sessionGen.Value, ownGen.Value);
            }
            if (sessionGen.HasValue)
            {
                from = "this session first read the doc";
                return sessionGen.Value;
            }
            if (ownGen.HasValue)
            {
                from = "your first write in this session";
                return ownGen.Value;
            }
            from = "the doc was opened";
            return 0;
        }

        private static bool IsWrite(JournalEntry entry, string writeTag, string clientKey)
        {
            if (entry == null || !string.Equals(entry.Operation, "committed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (entry.Ours != null)
            {
                return string.Equals(entry.Ours.WriteTag, writeTag, StringComparison.OrdinalIgnoreCase) &&
                       (string.IsNullOrEmpty(clientKey) || string.IsNullOrEmpty(entry.Ours.ClientKey) || string.Equals(entry.Ours.ClientKey, clientKey, StringComparison.Ordinal));
            }
            return (entry.Names ?? new List<string>()).Any(name =>
            {
                ParsedName parsed = Naming.Parse(name);
                return parsed.Class == NameClass.Write && string.Equals(parsed.WriteTag, writeTag, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string By(JournalEntry entry, string clientKey)
        {
            if (entry.Ours != null)
            {
                return string.Equals(entry.Ours.ClientKey, clientKey, StringComparison.Ordinal) ? "you" : "mcp";
            }
            if (entry.Class == NameClass.Write || entry.Class == NameClass.Ui) return "mcp";
            return "user";
        }
    }
}
