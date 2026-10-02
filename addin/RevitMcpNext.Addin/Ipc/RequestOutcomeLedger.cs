using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    /// <summary>One write outcome (D2 §13.1, SPEC §8.10).</summary>
    internal sealed class LedgerEntry
    {
        public string RequestId { get; set; }
        public string WriteTag { get; set; }
        public string ClientKey { get; set; }
        public string Key { get; set; }
        public string Kind { get; set; }
        public string DocKey { get; set; }
        public long? Rid { get; set; }
        public string Fingerprint { get; set; }
        public string State { get; set; } = OutcomeStates.Accepted;
        public DateTime AcceptedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public int Created { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        public int RevitWarnings { get; set; }
        public BridgeResponse Response { get; set; }
        /// <summary>Response JSON tree read back from the ledger file (when Response is null).</summary>
        public object PersistedResponse { get; set; }
        internal TaskCompletionSource<LedgerEntry> Completion { get; } =
            new TaskCompletionSource<LedgerEntry>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsTerminal => OutcomeStates.IsTerminal(State);

        /// <summary>get_request_result data: the entry including the recorded response.</summary>
        public Dictionary<string, object> ToWire(bool includeResponse = true)
        {
            var body = new Dictionary<string, object>
            {
                ["found"] = true,
                ["requestId"] = RequestId,
                ["writeTag"] = WriteTag,
                ["clientKey"] = ClientKey,
                ["key"] = Key,
                ["kind"] = Kind,
                ["docKey"] = DocKey,
                ["rid"] = Rid,
                ["state"] = State,
                ["acceptedAtUtc"] = AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["completedAtUtc"] = CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                ["counts"] = new Dictionary<string, object> { ["created"] = Created, ["modified"] = Modified, ["deleted"] = Deleted },
                ["revitWarnings"] = RevitWarnings
            };
            if (includeResponse) body["response"] = Response != null ? (object)Response.ToWire() : PersistedResponse;
            return body;
        }
    }

    internal sealed class LedgerLease
    {
        private LedgerLease(bool isOwner, LedgerEntry entry, string errorCode, string errorMessage)
        {
            IsOwner = isOwner;
            Entry = entry;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public bool IsOwner { get; }
        public LedgerEntry Entry { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }
        public bool IsError => ErrorCode != null;

        public static LedgerLease Owner(LedgerEntry entry) => new LedgerLease(true, entry, null, null);
        public static LedgerLease Replay(LedgerEntry entry) => new LedgerLease(false, entry, null, null);
        public static LedgerLease Conflict(string message) => new LedgerLease(false, null, ErrorCodes.RequestIdConflict, message);
    }

    /// <summary>
    /// Write outcome ledger keyed by requestId (SPEC §8.10): stores writeTag and clientKey, keeps 1,024 entries / 24 h in
    /// memory and appends terminal entries to &lt;home&gt;\ledger\&lt;instanceId&gt;.jsonl (rotated at 5 MB, 3 files kept).
    /// Thread-safe.
    /// </summary>
    internal sealed class RequestOutcomeLedger
    {
        public const int Capacity = 1024;
        public static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);
        private const long RotateBytes = 5L * 1024 * 1024;
        private const int KeepFiles = 3;
        private const int MaxResponseBytes = 64 * 1024;

        private readonly object _gate = new object();
        private readonly object _fileGate = new object();
        private readonly Dictionary<string, LedgerEntry> _entries = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
        private readonly LinkedList<string> _order = new LinkedList<string>();
        private readonly string _filePath;

        public RequestOutcomeLedger(string filePath)
        {
            _filePath = filePath;
        }

        public string FilePath => _filePath;

        /// <summary>True for request kinds whose outcome is recorded (write, lifecycle, code).</summary>
        public static bool IsRecorded(BridgeRequest request)
        {
            string kind = request?.Kind;
            return kind == RequestKinds.Write || kind == RequestKinds.Lifecycle || kind == RequestKinds.Code;
        }

        public LedgerLease Acquire(BridgeRequest request, string docKey)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string fingerprint = Fingerprint(request);
            lock (_gate)
            {
                PruneUnsafe(DateTime.UtcNow);
                if (_entries.TryGetValue(request.RequestId, out LedgerEntry existing))
                {
                    if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        return LedgerLease.Conflict("Request id " + request.RequestId + " was already used for " + existing.Key + " with different arguments.");
                    }
                    return LedgerLease.Replay(existing);
                }

                var entry = new LedgerEntry
                {
                    RequestId = request.RequestId,
                    WriteTag = request.WriteTag,
                    ClientKey = request.ClientKey,
                    Key = request.Op,
                    Kind = request.Kind,
                    DocKey = docKey ?? request.Doc?.Key,
                    Rid = request.Doc?.Rid,
                    Fingerprint = fingerprint,
                    AcceptedAtUtc = DateTime.UtcNow
                };
                _entries[entry.RequestId] = entry;
                _order.AddLast(entry.RequestId);
                return LedgerLease.Owner(entry);
            }
        }

        public void MarkRunning(string requestId)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(requestId ?? string.Empty, out LedgerEntry entry) && entry.State == OutcomeStates.Accepted)
                {
                    entry.State = OutcomeStates.Running;
                }
            }
        }

        /// <summary>Records the terminal outcome and appends it to the ledger file.</summary>
        public void Complete(string requestId, BridgeResponse response)
        {
            if (response == null) return;
            LedgerEntry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(requestId ?? string.Empty, out entry) || entry.IsTerminal) return;
                entry.State = StateOf(response);
                entry.Response = response;
                entry.CompletedAtUtc = DateTime.UtcNow;
                entry.Created = response.Changes?.CreatedTotal ?? 0;
                entry.Modified = response.Changes?.ModifiedTotal ?? 0;
                entry.Deleted = response.Changes?.DeletedTotal ?? 0;
                entry.RevitWarnings = response.Warnings?.Where(w => w.Code == WarningCodes.RevitWarning).Sum(w => Math.Max(1, w.N)) ?? 0;
                if (!string.IsNullOrEmpty(response.Doc?.Key)) entry.DocKey = response.Doc.Key;
            }
            Append(entry);
            entry.Completion.TrySetResult(entry);
        }

        /// <summary>get_request_result: memory first, then the ledger files of this instance.</summary>
        public LedgerEntry Find(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return null;
            lock (_gate)
            {
                if (_entries.TryGetValue(requestId, out LedgerEntry entry)) return entry;
            }
            return FindInFiles(requestId);
        }

        /// <summary>recent_writes: the newest terminal or running writes for a document key (all clients).</summary>
        public List<RecentWrite> Recent(string docKey, int limit, Func<string, DateTime?> lastSavedUtc = null)
        {
            limit = Math.Max(1, Math.Min(20, limit));
            lock (_gate)
            {
                return _order.Reverse()
                    .Select(id => _entries.TryGetValue(id, out LedgerEntry entry) ? entry : null)
                    .Where(entry => entry != null && (string.IsNullOrWhiteSpace(docKey) || string.Equals(entry.DocKey, docKey, StringComparison.OrdinalIgnoreCase)))
                    .Take(limit)
                    .Select(entry =>
                    {
                        DateTime? saved = lastSavedUtc?.Invoke(entry.DocKey);
                        return new RecentWrite
                        {
                            RequestId = entry.RequestId,
                            WriteTag = entry.WriteTag,
                            ClientKey = entry.ClientKey,
                            Key = entry.Key,
                            DocKey = entry.DocKey,
                            State = entry.State,
                            AtUtc = (entry.CompletedAtUtc ?? entry.AcceptedAtUtc).ToString("O", CultureInfo.InvariantCulture),
                            Created = entry.Created,
                            Modified = entry.Modified,
                            Deleted = entry.Deleted,
                            Saved = saved.HasValue && entry.CompletedAtUtc.HasValue && saved.Value > entry.CompletedAtUtc.Value
                        };
                    })
                    .ToList();
            }
        }

        public static string StateOf(BridgeResponse response)
        {
            if (response.Ok)
            {
                if (response.NeedsConfirm != null) return OutcomeStates.NotApplied;
                if (response.Job != null && !JobStates.IsTerminal(response.Job.State)) return OutcomeStates.Running;
                return OutcomeStates.Committed;
            }
            return response.Code == ErrorCodes.RevitTransactionRolledBack ? OutcomeStates.RolledBack : OutcomeStates.Failed;
        }

        /// <summary>Canonical fingerprint of the parts of a request that define its effect.</summary>
        public static string Fingerprint(BridgeRequest request)
        {
            var canonical = new StringBuilder();
            AppendCanonical(canonical, new Dictionary<string, object>
            {
                ["op"] = request.Op,
                ["kind"] = request.Kind,
                ["mode"] = request.Mode,
                ["doc"] = request.Doc?.ToWire(),
                ["confirmed"] = request.Confirmed?.ToWire(),
                ["args"] = request.Args
            });
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void AppendCanonical(StringBuilder builder, object value)
        {
            switch (value)
            {
                case null: builder.Append("null"); return;
                case string text: builder.Append('"').Append(text.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); return;
                case bool flag: builder.Append(flag ? "true" : "false"); return;
                case IDictionary<string, object> map:
                    builder.Append('{');
                    bool first = true;
                    foreach (string key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    {
                        if (!first) builder.Append(',');
                        first = false;
                        AppendCanonical(builder, key);
                        builder.Append(':');
                        AppendCanonical(builder, map[key]);
                    }
                    builder.Append('}');
                    return;
                case System.Collections.IEnumerable list:
                    builder.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem) builder.Append(',');
                        firstItem = false;
                        AppendCanonical(builder, item);
                    }
                    builder.Append(']');
                    return;
                case IFormattable formattable:
                    builder.Append(formattable.ToString(value is double || value is float ? "R" : null, CultureInfo.InvariantCulture));
                    return;
                default:
                    builder.Append(value);
                    return;
            }
        }

        private void PruneUnsafe(DateTime now)
        {
            while (_order.Count > 0)
            {
                string oldestId = _order.First.Value;
                if (!_entries.TryGetValue(oldestId, out LedgerEntry oldest))
                {
                    _order.RemoveFirst();
                    continue;
                }
                bool expired = oldest.IsTerminal && now - (oldest.CompletedAtUtc ?? oldest.AcceptedAtUtc) > TimeToLive;
                bool overCapacity = _entries.Count >= Capacity && oldest.IsTerminal;
                if (!expired && !overCapacity) break;
                _order.RemoveFirst();
                _entries.Remove(oldestId);
            }
        }

        private void Append(LedgerEntry entry)
        {
            if (string.IsNullOrWhiteSpace(_filePath)) return;
            try
            {
                Dictionary<string, object> line = entry.ToWire(includeResponse: false);
                line.Remove("found");
                string responseJson = entry.Response == null ? null : JsonWireCodec.Serialize(entry.Response.ToWire());
                if (responseJson != null && Encoding.UTF8.GetByteCount(responseJson) <= MaxResponseBytes)
                {
                    line["response"] = entry.Response.ToWire();
                }
                else if (responseJson != null)
                {
                    line["responseTrimmed"] = true;
                    line["response"] = new Dictionary<string, object>
                    {
                        ["ok"] = entry.Response.Ok,
                        ["code"] = entry.Response.Code,
                        ["summary"] = entry.Response.Summary,
                        ["changes"] = entry.Response.Changes?.ToWire()
                    };
                }
                string json = JsonWireCodec.Serialize(line);
                lock (_fileGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
                    RotateIfNeeded();
                    File.AppendAllText(_filePath, json + "\n", new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("ledger", "Could not append to the write ledger: " + ex.Message);
            }
        }

        private void RotateIfNeeded()
        {
            // Keeps the current file plus KeepFiles - 1 rotated ones (.1 newest).
            var info = new FileInfo(_filePath);
            if (!info.Exists || info.Length < RotateBytes) return;
            string oldest = RotatedPath(KeepFiles - 1);
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int index = KeepFiles - 2; index >= 1; index--)
            {
                string from = RotatedPath(index);
                if (File.Exists(from)) File.Move(from, RotatedPath(index + 1));
            }
            File.Move(_filePath, RotatedPath(1));
        }

        private string RotatedPath(int index)
        {
            string directory = Path.GetDirectoryName(_filePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(_filePath);
            return Path.Combine(directory, name + "." + index.ToString(CultureInfo.InvariantCulture) + ".jsonl");
        }

        private LedgerEntry FindInFiles(string requestId)
        {
            if (string.IsNullOrWhiteSpace(_filePath)) return null;
            var files = new List<string> { _filePath };
            for (int index = 1; index < KeepFiles; index++) files.Add(RotatedPath(index));
            lock (_fileGate)
            {
                foreach (string file in files)
                {
                    try
                    {
                        if (!File.Exists(file)) continue;
                        string[] lines;
                        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                        {
                            lines = reader.ReadToEnd().Split('\n');
                        }
                        for (int i = lines.Length - 1; i >= 0; i--)
                        {
                            string line = lines[i].Trim();
                            if (line.Length == 0 || line.IndexOf(requestId, StringComparison.Ordinal) < 0) continue;
                            if (!(JsonWireCodec.DeserializeObject(line) is IDictionary<string, object> map)) continue;
                            if (!string.Equals(Wire.GetString(map, "requestId"), requestId, StringComparison.Ordinal)) continue;
                            var counts = Wire.AsMap(Wire.Get(map, "counts"));
                            return new LedgerEntry
                            {
                                RequestId = requestId,
                                WriteTag = Wire.GetString(map, "writeTag"),
                                ClientKey = Wire.GetString(map, "clientKey"),
                                Key = Wire.GetString(map, "key"),
                                Kind = Wire.GetString(map, "kind"),
                                DocKey = Wire.GetString(map, "docKey"),
                                Rid = Wire.GetLong(map, "rid"),
                                State = Wire.GetString(map, "state") ?? OutcomeStates.Failed,
                                AcceptedAtUtc = ParseUtc(Wire.GetString(map, "acceptedAtUtc")) ?? DateTime.MinValue,
                                CompletedAtUtc = ParseUtc(Wire.GetString(map, "completedAtUtc")),
                                Created = (int)(counts == null ? 0 : Wire.GetLong(counts, "created") ?? 0),
                                Modified = (int)(counts == null ? 0 : Wire.GetLong(counts, "modified") ?? 0),
                                Deleted = (int)(counts == null ? 0 : Wire.GetLong(counts, "deleted") ?? 0),
                                PersistedResponse = Wire.Get(map, "response")
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLogger.Warn("ledger", "Could not search " + file + ": " + ex.Message);
                    }
                }
            }
            return null;
        }

        private static DateTime? ParseUtc(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime value)
                ? value
                : (DateTime?)null;
        }
    }
}
