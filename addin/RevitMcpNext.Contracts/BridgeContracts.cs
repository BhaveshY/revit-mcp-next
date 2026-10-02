using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

// Wire protocol v3 between the broker and the Revit add-in (SPEC §4.6; mirrors contracts/src/protocol.ts).
// Transport: one request per connection; frame = 4-byte big-endian length + UTF-8 JSON (camelCase), at most 4 MiB.
// Every type converts itself to a plain JSON tree (Dictionary<string, object?> / List<object?> / string / number /
// bool / null) through ToWire(), so each side can use its own JSON serializer.
namespace RevitMcpNext.Contracts
{
    /// <summary>Implemented by every wire DTO: returns a JSON tree with camelCase keys.</summary>
    public interface IWireObject
    {
        Dictionary<string, object?> ToWire();
    }

    public static class BridgeProtocol
    {
        /// <summary>The protocol version this build speaks (the "v" field).</summary>
        public const string Version = "2026-10-01";
        /// <summary>Oldest protocol version the add-in accepts (advertised in hello).</summary>
        public const string MinVersion = "2026-10-01";
        /// <summary>Newest protocol version the add-in accepts (advertised in hello).</summary>
        public const string MaxVersion = "2026-10-01";
        public const int MaxFrameBytes = 4 * 1024 * 1024;
        public const string PipePrefix = "revit-mcp-next-";
        public const string ControlPipeSuffix = "-control";
        public const int PrimaryPipeInstances = 16;
        public const int ControlPipeInstances = 4;
        /// <summary>Time a client has to send its request frame after connecting.</summary>
        public const int HandshakeTimeoutMs = 5000;
        public const int ResponseWriteTimeoutMs = 5000;

        public static string PrimaryPipeName(string instanceId) => PipePrefix + instanceId;

        public static string ControlPipeName(string instanceId) => PipePrefix + instanceId + ControlPipeSuffix;

        /// <summary>Orders protocol versions (ISO dates compare ordinally). Null/empty sorts first.</summary>
        public static int Compare(string? left, string? right)
        {
            return string.CompareOrdinal(left ?? string.Empty, right ?? string.Empty);
        }

        public static Dictionary<string, object?> RangeWire()
        {
            return new Dictionary<string, object?> { ["min"] = MinVersion, ["max"] = MaxVersion };
        }
    }

    /// <summary>Registry kinds (catalog OpMeta.kind); "control" is used on the control pipe.</summary>
    public static class RequestKinds
    {
        public const string Read = "read";
        public const string Write = "write";
        public const string Ui = "ui";
        public const string Lifecycle = "lifecycle";
        public const string Control = "control";
        public const string Code = "code";
    }

    public static class RequestModes
    {
        public const string Apply = "apply";
        public const string Preview = "preview";
    }

    /// <summary>Control-pipe operations (SPEC §4.6.4). Served on pipe threads; all need auth except hello.</summary>
    public static class ControlOps
    {
        public const string Hello = "hello";
        public const string Snapshot = "snapshot";
        public const string Health = "health";
        public const string CancelRequest = "cancel_request";
        public const string GetRequestResult = "get_request_result";
        public const string RecentWrites = "recent_writes";
        public const string JobStatus = "job_status";
        public const string JobCancel = "job_cancel";
        public const string Dialogs = "dialogs";
        public const string Press = "press";

        public static readonly string[] All =
        {
            Hello, Snapshot, Health, CancelRequest, GetRequestResult, RecentWrites, JobStatus, JobCancel, Dialogs, Press
        };

        public static bool IsControlOp(string? op)
        {
            if (string.IsNullOrEmpty(op)) return false;
            foreach (string name in All)
            {
                if (string.Equals(name, op, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>cancel_request outcomes.</summary>
    public static class CancelOutcomes
    {
        public const string Queued = "queued";
        public const string Cooperative = "cooperative";
        public const string NotFound = "not_found";
        public const string RunningWrite = "running_write";
    }

    /// <summary>Job states reported by job_status.</summary>
    public static class JobStates
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Interrupted = "interrupted";

        public static bool IsTerminal(string? state)
        {
            return state == Succeeded || state == Failed || state == Cancelled || state == Interrupted;
        }
    }

    /// <summary>Ledger states for write outcomes (get_request_result / recent_writes).</summary>
    public static class OutcomeStates
    {
        public const string Accepted = "accepted";
        public const string Running = "running";
        public const string Committed = "committed";
        /// <summary>Also used when nothing was applied (needsConfirm); the recorded response carries the plan.</summary>
        public const string RolledBack = "rolledBack";
        public const string Failed = "failed";

        public static bool IsTerminal(string? state)
        {
            return state == Committed || state == RolledBack || state == Failed;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Request (SPEC §4.6.2)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The target document of a request: runtime id plus durable documentKey (SPEC §4.6.5).</summary>
    public sealed class DocRef : IWireObject
    {
        public long Rid { get; set; }
        public string Key { get; set; } = string.Empty;

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["rid"] = Rid, ["key"] = Key };
        }

        public static DocRef? FromWire(object? value)
        {
            var map = Wire.AsMap(value);
            if (map == null) return null;
            long? rid = Wire.GetLong(map, "rid");
            string? key = Wire.GetString(map, "key");
            if (!rid.HasValue && string.IsNullOrEmpty(key)) return null;
            return new DocRef { Rid = rid ?? 0, Key = key ?? string.Empty };
        }
    }

    /// <summary>Auth block. The fp (first 8 hex of sha256(token)) and file are for diagnosis only.</summary>
    public sealed class AuthInfo : IWireObject
    {
        public string? Token { get; set; }
        public string? Fp { get; set; }
        public string? File { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["token"] = Token, ["fp"] = Fp, ["file"] = File };
        }

        public static AuthInfo? FromWire(object? value)
        {
            var map = Wire.AsMap(value);
            if (map == null) return null;
            return new AuthInfo
            {
                Token = Wire.GetString(map, "token"),
                Fp = Wire.GetString(map, "fp"),
                File = Wire.GetString(map, "file")
            };
        }
    }

    /// <summary>Sent only when applying a confirmed plan: the plan stamp and the delete set it was computed with.</summary>
    public sealed class ConfirmedPlan : IWireObject
    {
        public long Stamp { get; set; }
        public List<long> DeleteSet { get; set; } = new List<long>();

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["stamp"] = Stamp, ["deleteSet"] = Wire.Longs(DeleteSet) };
        }

        public static ConfirmedPlan? FromWire(object? value)
        {
            var map = Wire.AsMap(value);
            if (map == null) return null;
            return new ConfirmedPlan
            {
                Stamp = Wire.GetLong(map, "stamp") ?? 0,
                DeleteSet = Wire.GetLongList(map, "deleteSet")
            };
        }
    }

    public sealed class BridgeRequest : IWireObject
    {
        public string V { get; set; } = BridgeProtocol.Version;
        /// <summary>ULID from the broker; globally unique; the ledger key.</summary>
        public string RequestId { get; set; } = string.Empty;
        /// <summary>clientInfo.name + '#' + broker pid.</summary>
        public string ClientKey { get; set; } = string.Empty;
        /// <summary>Omitted for hello.</summary>
        public AuthInfo? Auth { get; set; }
        /// <summary>Registry key (primary pipe) or control op name (control pipe).</summary>
        public string Op { get; set; } = string.Empty;
        /// <summary>Registry kind; "control" on the control pipe.</summary>
        public string Kind { get; set; } = RequestKinds.Read;
        /// <summary>write/code only: "apply" | "preview".</summary>
        public string? Mode { get; set; }
        /// <summary>Time left for this request, measured from receipt by the add-in.</summary>
        public int TimeoutMs { get; set; } = 30000;
        /// <summary>Null for scope none.</summary>
        public DocRef? Doc { get; set; }
        /// <summary>Broker short id (w17) for write, lifecycle and code-commit requests.</summary>
        public string? WriteTag { get; set; }
        public ConfirmedPlan? Confirmed { get; set; }
        /// <summary>Normalized args (r#/last already expanded to ids). Control ops carry their payload here.</summary>
        public Dictionary<string, object?> Args { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        /// <summary>job_start: run as a job and return {job:{jobId,state:"queued"}} at once.</summary>
        public bool AsJob { get; set; }
        /// <summary>Return page ids (for broker handles r#).</summary>
        public bool WantIds { get; set; }

        public bool IsPreview => string.Equals(Mode, RequestModes.Preview, StringComparison.OrdinalIgnoreCase);

        public Dictionary<string, object?> ToWire()
        {
            var body = new Dictionary<string, object?>
            {
                ["v"] = V,
                ["requestId"] = RequestId,
                ["clientKey"] = ClientKey,
                ["op"] = Op,
                ["kind"] = Kind,
                ["timeoutMs"] = TimeoutMs,
                ["doc"] = Doc?.ToWire(),
                ["args"] = Args
            };
            if (Auth != null) body["auth"] = Auth.ToWire();
            if (Mode != null) body["mode"] = Mode;
            if (WriteTag != null) body["writeTag"] = WriteTag;
            if (Confirmed != null) body["confirmed"] = Confirmed.ToWire();
            if (AsJob) body["asJob"] = true;
            if (WantIds) body["wantIds"] = true;
            return body;
        }

        /// <summary>Parses a request object; tolerates missing optional fields (never throws for shape problems).</summary>
        public static BridgeRequest FromWire(IDictionary<string, object?> map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            var request = new BridgeRequest
            {
                V = Wire.GetString(map, "v") ?? string.Empty,
                RequestId = Wire.GetString(map, "requestId") ?? string.Empty,
                ClientKey = Wire.GetString(map, "clientKey") ?? string.Empty,
                Auth = AuthInfo.FromWire(Wire.Get(map, "auth")),
                Op = Wire.GetString(map, "op") ?? string.Empty,
                Kind = Wire.GetString(map, "kind") ?? string.Empty,
                Mode = Wire.GetString(map, "mode"),
                TimeoutMs = (int)Math.Max(1, Math.Min(int.MaxValue, Wire.GetLong(map, "timeoutMs") ?? 30000)),
                Doc = DocRef.FromWire(Wire.Get(map, "doc")),
                WriteTag = Wire.GetString(map, "writeTag"),
                Confirmed = ConfirmedPlan.FromWire(Wire.Get(map, "confirmed")),
                AsJob = Wire.GetBool(map, "asJob") ?? false,
                WantIds = Wire.GetBool(map, "wantIds") ?? false
            };
            var args = Wire.AsMap(Wire.Get(map, "args"));
            if (args != null)
            {
                request.Args = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> pair in args) request.Args[pair.Key] = pair.Value;
            }
            return request;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Response (SPEC §4.6.3)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Created/modified/deleted ids of a write (arrays may be capped; totals are exact).</summary>
    public sealed class ChangeSummary : IWireObject
    {
        public List<long> Created { get; set; } = new List<long>();
        public List<long> Modified { get; set; } = new List<long>();
        public List<long> Deleted { get; set; } = new List<long>();
        public int CreatedTotal { get; set; }
        public int ModifiedTotal { get; set; }
        public int DeletedTotal { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["created"] = Wire.Longs(Created),
                ["modified"] = Wire.Longs(Modified),
                ["deleted"] = Wire.Longs(Deleted),
                ["createdTotal"] = CreatedTotal,
                ["modifiedTotal"] = ModifiedTotal,
                ["deletedTotal"] = DeletedTotal
            };
        }
    }

    /// <summary>Blast-radius numbers of a plan (SPEC §6.3).</summary>
    public sealed class BlastInfo : IWireObject
    {
        public int DeleteTotal { get; set; }
        /// <summary>Delete counts by English category label.</summary>
        public Dictionary<string, int> ByCategory { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Sample of delete-set ids (at most 50).</summary>
        public List<long> Sample { get; set; } = new List<long>();
        public int ModifyTotal { get; set; }
        public int CreateTotal { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            var byCategory = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in ByCategory) byCategory[pair.Key] = pair.Value;
            return new Dictionary<string, object?>
            {
                ["deleteTotal"] = DeleteTotal,
                ["byCategory"] = byCategory,
                ["sample"] = Wire.Longs(Sample),
                ["modifyTotal"] = ModifyTotal,
                ["createTotal"] = CreateTotal
            };
        }
    }

    /// <summary>
    /// Returned instead of committing when a blast rule triggers (SPEC §6.1) and, in preview mode, always (rule null
    /// when no rule triggered) so the broker can mint a confirm token for the dry run. Structured plan details go in
    /// the response data; <see cref="Plan"/> is the one-line text the broker shows as the summary.
    /// </summary>
    public sealed class NeedsConfirm : IWireObject
    {
        /// <summary>delete | bulk | create | file_overwrite | unsaved_close | always | multi_doc | central_open | code_commit; null for a plain preview.</summary>
        public string? Rule { get; set; }
        /// <summary>One-line human-readable plan (the broker uses it as the NOT APPLIED / preview summary).</summary>
        public string Plan { get; set; } = string.Empty;
        public BlastInfo Blast { get; set; } = new BlastInfo();
        /// <summary>Document generation the plan was computed at; per-element stamps above it make the plan stale.</summary>
        public long Stamp { get; set; }
        /// <summary>The exact delete set (ids) the plan would remove.</summary>
        public List<long> DeleteSet { get; set; } = new List<long>();

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["rule"] = Rule,
                ["plan"] = Plan,
                ["blast"] = Blast.ToWire(),
                ["stamp"] = Stamp,
                ["deleteSet"] = Wire.Longs(DeleteSet)
            };
        }
    }

    /// <summary>warn: line source. Codes: REVIT_WARNING, VALUE_CLAMPED, IDS_GONE, NON_FINITE_NUMBER, ... (SPEC §4.3).</summary>
    public sealed class BridgeWarning : IWireObject
    {
        public string Code { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public List<long> Ids { get; set; } = new List<long>();
        /// <summary>How many times this (code, text) occurred.</summary>
        public int N { get; set; } = 1;

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["code"] = Code, ["text"] = Text, ["ids"] = Wire.Longs(Ids), ["n"] = N };
        }
    }

    /// <summary>notice: line source. Codes: TARGET_NOW, SAVED_AS, SELECTION_STALE, ... (SPEC §4.3).</summary>
    public sealed class BridgeNotice : IWireObject
    {
        public string Code { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["code"] = Code, ["text"] = Text };
        }
    }

    /// <summary>The document a response refers to.</summary>
    public sealed class ResponseDoc : IWireObject
    {
        public long Rid { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Year { get; set; }
        /// <summary>project | family.</summary>
        public string Kind { get; set; } = "project";
        public long Generation { get; set; }
        public bool Modified { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["rid"] = Rid,
                ["key"] = Key,
                ["title"] = Title,
                ["year"] = Year,
                ["kind"] = Kind,
                ["generation"] = Generation,
                ["modified"] = Modified
            };
        }
    }

    public sealed class PageInfo : IWireObject
    {
        public int Total { get; set; }
        public int Offset { get; set; }
        public int Count { get; set; }
        /// <summary>All matching ids (only when the request had wantIds).</summary>
        public List<long>? Ids { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            var body = new Dictionary<string, object?> { ["total"] = Total, ["offset"] = Offset, ["count"] = Count };
            if (Ids != null) body["ids"] = Wire.Longs(Ids);
            return body;
        }
    }

    public sealed class PartialInfo : IWireObject
    {
        /// <summary>deadline | limit.</summary>
        public string Reason { get; set; } = "deadline";
        /// <summary>Opaque resume state; the broker turns it into a page token.</summary>
        public object? Resume { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["reason"] = Reason, ["resume"] = Resume };
        }
    }

    /// <summary>A file produced by the add-in (captures, exports). Images never cross the pipe.</summary>
    public sealed class ResultFile : IWireObject
    {
        public string Path { get; set; } = string.Empty;
        public string Mime { get; set; } = string.Empty;
        public int? W { get; set; }
        public int? H { get; set; }
        public long Bytes { get; set; }
        public object? Meta { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["path"] = Path,
                ["mime"] = Mime,
                ["w"] = W,
                ["h"] = H,
                ["bytes"] = Bytes,
                ["meta"] = Meta
            };
        }
    }

    public sealed class JobInfo : IWireObject
    {
        public string JobId { get; set; } = string.Empty;
        public string State { get; set; } = JobStates.Queued;
        public string? Stage { get; set; }
        public int? Done { get; set; }
        public int? Total { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["jobId"] = JobId,
                ["state"] = State,
                ["stage"] = Stage,
                ["done"] = Done,
                ["total"] = Total
            };
        }
    }

    public sealed class ResponseMetrics : IWireObject
    {
        public long QueueWaitMs { get; set; }
        public long RaiseToExecMs { get; set; }
        public long ExecMs { get; set; }
        /// <summary>externalEvent | idling | control | inProcess | job.</summary>
        public string Via { get; set; } = "control";
        public bool CacheHit { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["queueWaitMs"] = QueueWaitMs,
                ["raiseToExecMs"] = RaiseToExecMs,
                ["execMs"] = ExecMs,
                ["via"] = Via,
                ["cacheHit"] = CacheHit
            };
        }
    }

    public sealed class BridgeResponse : IWireObject
    {
        public string V { get; set; } = BridgeProtocol.Version;
        public string RequestId { get; set; } = string.Empty;
        public bool Ok { get; set; }
        /// <summary>Error code (SPEC §4.3) when Ok is false.</summary>
        public string? Code { get; set; }
        /// <summary>Concrete names and values; never tool names or fix text (the broker renders fix:).</summary>
        public string? Message { get; set; }
        public object? Details { get; set; }
        public string? Summary { get; set; }
        public object? Data { get; set; }
        public ChangeSummary? Changes { get; set; }
        public NeedsConfirm? NeedsConfirm { get; set; }
        /// <summary>Named outputs for $refs (type, view, sheet, level, schedule, family, room, ...).</summary>
        public Dictionary<string, object?>? Outputs { get; set; }
        public List<BridgeWarning> Warnings { get; set; } = new List<BridgeWarning>();
        public List<BridgeNotice> Notices { get; set; } = new List<BridgeNotice>();
        public ResponseDoc? Doc { get; set; }
        public PageInfo? Page { get; set; }
        public PartialInfo? Partial { get; set; }
        public ResultFile? File { get; set; }
        public JobInfo? Job { get; set; }
        public ResponseMetrics Metrics { get; set; } = new ResponseMetrics();

        public static BridgeResponse Success(string? requestId, object? data = null, string? summary = null)
        {
            return new BridgeResponse { RequestId = requestId ?? string.Empty, Ok = true, Data = data, Summary = summary };
        }

        public static BridgeResponse Failure(string? requestId, string code, string message, object? details = null)
        {
            return new BridgeResponse
            {
                RequestId = requestId ?? string.Empty,
                Ok = false,
                Code = code,
                Message = message,
                Details = details
            };
        }

        public Dictionary<string, object?> ToWire()
        {
            var warnings = new List<object?>(Warnings?.Count ?? 0);
            if (Warnings != null) foreach (BridgeWarning warning in Warnings) warnings.Add(warning.ToWire());
            var notices = new List<object?>(Notices?.Count ?? 0);
            if (Notices != null) foreach (BridgeNotice notice in Notices) notices.Add(notice.ToWire());
            Dictionary<string, object?>? outputs = null;
            if (Outputs != null)
            {
                outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> pair in Outputs) outputs[pair.Key] = pair.Value;
            }

            return new Dictionary<string, object?>
            {
                ["v"] = V,
                ["requestId"] = RequestId,
                ["ok"] = Ok,
                ["code"] = Ok ? null : Code,
                ["message"] = Ok ? null : Message,
                ["details"] = Ok ? null : Details,
                ["summary"] = Summary,
                ["data"] = Data,
                ["changes"] = Changes?.ToWire(),
                ["needsConfirm"] = NeedsConfirm?.ToWire(),
                ["outputs"] = outputs,
                ["warnings"] = warnings,
                ["notices"] = notices,
                ["doc"] = Doc?.ToWire(),
                ["page"] = Page?.ToWire(),
                ["partial"] = Partial?.ToWire(),
                ["file"] = File?.ToWire(),
                ["job"] = Job?.ToWire(),
                ["metrics"] = (Metrics ?? new ResponseMetrics()).ToWire()
            };
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // DocSnapshot (SPEC §4.6.5): control "snapshot" data and the registration file body.
    // ------------------------------------------------------------------------------------------------------------

    public sealed class ProtocolRange : IWireObject
    {
        public string Min { get; set; } = BridgeProtocol.MinVersion;
        public string Max { get; set; } = BridgeProtocol.MaxVersion;

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["min"] = Min, ["max"] = Max };
        }
    }

    /// <summary>A modal popup owned by the Revit main window.</summary>
    public sealed class PopupInfo : IWireObject
    {
        /// <summary>Window handle (health only; 0 when unknown).</summary>
        public long Hwnd { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Class { get; set; } = string.Empty;
        /// <summary>True for progress windows (msctls_progress32 child or a progress-like title).</summary>
        public bool IsProgress { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["title"] = Title, ["class"] = Class, ["isProgress"] = IsProgress };
        }

        public Dictionary<string, object?> ToHealthWire()
        {
            var body = ToWire();
            body["hwnd"] = Hwnd;
            return body;
        }
    }

    public sealed class SnapshotUi : IWireObject
    {
        public bool Foreground { get; set; }
        public string? LastForegroundAtUtc { get; set; }
        public string? LastViewActivatedAtUtc { get; set; }
        public bool Minimized { get; set; }
        public bool MainWindowEnabled { get; set; } = true;
        public bool Hung { get; set; }
        public PopupInfo? Popup { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["foreground"] = Foreground,
                ["lastForegroundAtUtc"] = LastForegroundAtUtc,
                ["lastViewActivatedAtUtc"] = LastViewActivatedAtUtc,
                ["minimized"] = Minimized,
                ["mainWindowEnabled"] = MainWindowEnabled,
                ["hung"] = Hung,
                ["popup"] = Popup?.ToWire()
            };
        }

        public SnapshotUi Clone()
        {
            return (SnapshotUi)MemberwiseClone();
        }
    }

    public sealed class NativeProgress : IWireObject
    {
        public string? Caption { get; set; }
        public int Pos { get; set; }
        public int Max { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["caption"] = Caption, ["pos"] = Pos, ["max"] = Max };
        }
    }

    /// <summary>A native (non-MCP) long operation that blocks the Revit UI thread.</summary>
    public sealed class NativeActivity : IWireObject
    {
        /// <summary>sync | opening | saving | exporting | printing | reloading.</summary>
        public string Kind { get; set; } = string.Empty;
        public long? Rid { get; set; }
        public string? SinceUtc { get; set; }
        public NativeProgress? Progress { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = Kind,
                ["rid"] = Rid,
                ["sinceUtc"] = SinceUtc,
                ["progress"] = Progress?.ToWire()
            };
        }
    }

    /// <summary>The MCP item currently executing on the UI thread.</summary>
    public sealed class ExecutingInfo : IWireObject
    {
        public string? RequestId { get; set; }
        public string Op { get; set; } = string.Empty;
        public string? SinceUtc { get; set; }
        public string? ClientKey { get; set; }
        public string? WriteTag { get; set; }
        public string? JobId { get; set; }
        /// <summary>Health only.</summary>
        public long ElapsedMs { get; set; }

        /// <summary>Snapshot shape: {op, sinceUtc, clientKey, writeTag, jobId}.</summary>
        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["op"] = Op,
                ["sinceUtc"] = SinceUtc,
                ["clientKey"] = ClientKey,
                ["writeTag"] = WriteTag,
                ["jobId"] = JobId
            };
        }

        /// <summary>Health shape: {requestId, op, clientKey, writeTag?, jobId?, startedAtUtc, elapsedMs}.</summary>
        public Dictionary<string, object?> ToHealthWire()
        {
            return new Dictionary<string, object?>
            {
                ["requestId"] = RequestId,
                ["op"] = Op,
                ["clientKey"] = ClientKey,
                ["writeTag"] = WriteTag,
                ["jobId"] = JobId,
                ["startedAtUtc"] = SinceUtc,
                ["elapsedMs"] = ElapsedMs
            };
        }
    }

    public sealed class CodeExecutionState : IWireObject
    {
        public bool Enabled { get; set; }
        /// <summary>The user allowed code execution for this Revit session.</summary>
        public bool Consented { get; set; }
        public string? EnabledSinceUtc { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["enabled"] = Enabled, ["consented"] = Consented, ["enabledSinceUtc"] = EnabledSinceUtc };
        }
    }

    public sealed class ViewInfo : IWireObject
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        /// <summary>ViewType name, e.g. FloorPlan, ThreeD, DrawingSheet.</summary>
        public string Type { get; set; } = string.Empty;
        public int? Scale { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["id"] = Id, ["name"] = Name, ["type"] = Type, ["scale"] = Scale };
        }
    }

    public sealed class SelectionInfo : IWireObject
    {
        public int Count { get; set; }
        public string? AtUtc { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["count"] = Count, ["atUtc"] = AtUtc };
        }
    }

    /// <summary>One level row: id, name, elevation in mm.</summary>
    public sealed class LevelRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double ElevationMm { get; set; }

        public List<object?> ToWire()
        {
            return new List<object?> { Id, Name, ElevationMm };
        }
    }

    public sealed class SnapshotDoc : IWireObject
    {
        public long Rid { get; set; }
        /// <summary>Durable document key (D2 §9.2).</summary>
        public string Key { get; set; } = string.Empty;
        /// <summary>Earlier keys of the same document (Save As), at most 5.</summary>
        public List<string> Aliases { get; set; } = new List<string>();
        public string Title { get; set; } = string.Empty;
        /// <summary>project | family.</summary>
        public string Kind { get; set; } = "project";
        public string? Path { get; set; }
        public string? Central { get; set; }
        public bool Workshared { get; set; }
        public bool Cloud { get; set; }
        public bool ReadOnly { get; set; }
        public bool Modified { get; set; }
        public long Generation { get; set; }
        public bool Closing { get; set; }
        public ViewInfo? ActiveView { get; set; }
        public string? LastActivatedAtUtc { get; set; }
        /// <summary>At most 30 levels sorted by elevation; LevelsMore counts the rest.</summary>
        public List<LevelRow> Levels { get; set; } = new List<LevelRow>();
        public int LevelsMore { get; set; }
        public SelectionInfo Selection { get; set; } = new SelectionInfo();
        /// <summary>For family docs opened by EditFamily from a project: that project's rid.</summary>
        public long? FamilySourceRid { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            var levels = new List<object?>(Levels.Count);
            foreach (LevelRow level in Levels) levels.Add(level.ToWire());
            return new Dictionary<string, object?>
            {
                ["rid"] = Rid,
                ["key"] = Key,
                ["aliases"] = new List<object?>(Aliases),
                ["title"] = Title,
                ["kind"] = Kind,
                ["path"] = Path,
                ["central"] = Central,
                ["workshared"] = Workshared,
                ["cloud"] = Cloud,
                ["readOnly"] = ReadOnly,
                ["modified"] = Modified,
                ["generation"] = Generation,
                ["closing"] = Closing,
                ["activeView"] = ActiveView?.ToWire(),
                ["lastActivatedAtUtc"] = LastActivatedAtUtc,
                ["levels"] = levels,
                ["levelsMore"] = LevelsMore,
                ["selection"] = Selection.ToWire(),
                ["familySourceRid"] = FamilySourceRid
            };
        }

        public SnapshotDoc Clone()
        {
            var copy = (SnapshotDoc)MemberwiseClone();
            copy.Aliases = new List<string>(Aliases);
            copy.Levels = new List<LevelRow>(Levels);
            copy.Selection = new SelectionInfo { Count = Selection.Count, AtUtc = Selection.AtUtc };
            return copy;
        }
    }

    /// <summary>
    /// Event-driven, immutable-by-convention view of one Revit instance. Built on the UI thread and swapped
    /// atomically; pipe threads only read it. The registration file is this object plus writtenAtUtc.
    /// </summary>
    public sealed class DocSnapshot : IWireObject
    {
        public int SchemaVersion { get; set; } = 3;
        public long Seq { get; set; }
        public string? AtUtc { get; set; }
        public string? WrittenAtUtc { get; set; }
        public string InstanceId { get; set; } = string.Empty;
        public int Pid { get; set; }
        public int Year { get; set; }
        public string Build { get; set; } = string.Empty;
        /// <summary>Revit UI language (e.g. ENU, DEU).</summary>
        public string Language { get; set; } = string.Empty;
        public string AddinVersion { get; set; } = string.Empty;
        public string GitSha { get; set; } = string.Empty;
        public string PayloadId { get; set; } = string.Empty;
        public string CatalogHash { get; set; } = string.Empty;
        public ProtocolRange Protocol { get; set; } = new ProtocolRange();
        public string Pipe { get; set; } = string.Empty;
        public string ControlPipe { get; set; } = string.Empty;
        public string Home { get; set; } = string.Empty;
        /// <summary>starting | ready | stopping.</summary>
        public string State { get; set; } = "starting";
        public SnapshotUi Ui { get; set; } = new SnapshotUi();
        public string? LastIdlingAtUtc { get; set; }
        public NativeActivity? Native { get; set; }
        public ExecutingInfo? Executing { get; set; }
        public long? ActiveRid { get; set; }
        public long? LastActiveProjectRid { get; set; }
        public CodeExecutionState CodeExecution { get; set; } = new CodeExecutionState();
        public List<SnapshotDoc> Docs { get; set; } = new List<SnapshotDoc>();

        public Dictionary<string, object?> ToWire()
        {
            var docs = new List<object?>(Docs.Count);
            foreach (SnapshotDoc doc in Docs) docs.Add(doc.ToWire());
            return new Dictionary<string, object?>
            {
                ["schemaVersion"] = SchemaVersion,
                ["seq"] = Seq,
                ["atUtc"] = AtUtc,
                ["writtenAtUtc"] = WrittenAtUtc,
                ["instanceId"] = InstanceId,
                ["pid"] = Pid,
                ["year"] = Year,
                ["build"] = Build,
                ["language"] = Language,
                ["addinVersion"] = AddinVersion,
                ["gitSha"] = GitSha,
                ["payloadId"] = PayloadId,
                ["catalogHash"] = CatalogHash,
                ["protocol"] = Protocol.ToWire(),
                ["pipe"] = Pipe,
                ["controlPipe"] = ControlPipe,
                ["home"] = Home,
                ["state"] = State,
                ["ui"] = Ui.ToWire(),
                ["lastIdlingAtUtc"] = LastIdlingAtUtc,
                ["native"] = Native?.ToWire(),
                ["executing"] = Executing?.ToWire(),
                ["activeRid"] = ActiveRid,
                ["lastActiveProjectRid"] = LastActiveProjectRid,
                ["codeExecution"] = CodeExecution.ToWire(),
                ["docs"] = docs
            };
        }

        /// <summary>Shallow copy with a copied doc list (docs themselves are replaced, never mutated).</summary>
        public DocSnapshot Clone()
        {
            var copy = (DocSnapshot)MemberwiseClone();
            copy.Docs = new List<SnapshotDoc>(Docs);
            return copy;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Control op data (SPEC §4.6.4)
    // ------------------------------------------------------------------------------------------------------------

    public sealed class Capabilities : IWireObject
    {
        /// <summary>Registry keys bound to handlers (catalog keys plus dev./test. keys).</summary>
        public List<string> Keys { get; set; } = new List<string>();
        /// <summary>Registry startup check findings (unbound catalog keys, unknown bindings, CATALOG_MISSING, ...).</summary>
        public List<string> Mismatches { get; set; } = new List<string>();

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["keys"] = new List<object?>(Keys),
                ["mismatches"] = new List<object?>(Mismatches)
            };
        }
    }

    public sealed class HelloData : IWireObject
    {
        public string InstanceId { get; set; } = string.Empty;
        public int Pid { get; set; }
        public int Year { get; set; }
        public string Build { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string AddinVersion { get; set; } = string.Empty;
        public string GitSha { get; set; } = string.Empty;
        public string PayloadId { get; set; } = string.Empty;
        public string CatalogHash { get; set; } = string.Empty;
        public ProtocolRange Protocol { get; set; } = new ProtocolRange();
        public string State { get; set; } = "starting";
        public string Home { get; set; } = string.Empty;
        public string AuthFile { get; set; } = string.Empty;
        public string AuthFp { get; set; } = string.Empty;
        /// <summary>ok | env | unwritable | missing.</summary>
        public string AuthState { get; set; } = string.Empty;
        public Capabilities Capabilities { get; set; } = new Capabilities();
        /// <summary>True when the e2e-only test.* ops are registered (SPEC §13.4).</summary>
        public bool TestOps { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["instanceId"] = InstanceId,
                ["pid"] = Pid,
                ["year"] = Year,
                ["build"] = Build,
                ["language"] = Language,
                ["addinVersion"] = AddinVersion,
                ["gitSha"] = GitSha,
                ["payloadId"] = PayloadId,
                ["catalogHash"] = CatalogHash,
                ["protocol"] = Protocol.ToWire(),
                ["state"] = State,
                ["home"] = Home,
                ["authFile"] = AuthFile,
                ["authFp"] = AuthFp,
                ["authState"] = AuthState,
                ["capabilities"] = Capabilities.ToWire(),
                ["testOps"] = TestOps
            };
        }
    }

    public sealed class QueueHealth : IWireObject
    {
        public int Pending { get; set; }
        public ExecutingInfo? Executing { get; set; }
        /// <summary>Pending count per clientKey.</summary>
        public Dictionary<string, int> ByClient { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        public Dictionary<string, object?> ToWire()
        {
            var byClient = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in ByClient) byClient[pair.Key] = pair.Value;
            return new Dictionary<string, object?>
            {
                ["pending"] = Pending,
                ["executing"] = Executing?.ToHealthWire(),
                ["byClient"] = byClient
            };
        }
    }

    public sealed class PumpHealth : IWireObject
    {
        public long P50 { get; set; }
        public long P90 { get; set; }
        public long P99 { get; set; }
        public long Max { get; set; }
        public long N { get; set; }
        public long Over1s { get; set; }
        /// <summary>ExternalEvent.Raise results: accepted, pending, denied, timedOut.</summary>
        public Dictionary<string, long> RaiseResults { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
        public long WmNullPosts { get; set; }
        public long ExecViaIdling { get; set; }
        public long Recreates { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            var raise = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> pair in RaiseResults) raise[pair.Key] = pair.Value;
            return new Dictionary<string, object?>
            {
                ["p50"] = P50,
                ["p90"] = P90,
                ["p99"] = P99,
                ["max"] = Max,
                ["n"] = N,
                ["over1s"] = Over1s,
                ["raiseResults"] = raise,
                ["wmNullPosts"] = WmNullPosts,
                ["execViaIdling"] = ExecViaIdling,
                ["recreates"] = Recreates
            };
        }
    }

    public sealed class UiHealth : IWireObject
    {
        public bool MainWindowEnabled { get; set; } = true;
        public bool Hung { get; set; }
        public bool Minimized { get; set; }
        public bool Foreground { get; set; }
        public PopupInfo? Popup { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["mainWindowEnabled"] = MainWindowEnabled,
                ["hung"] = Hung,
                ["minimized"] = Minimized,
                ["foreground"] = Foreground,
                ["popup"] = Popup?.ToHealthWire()
            };
        }
    }

    /// <summary>The last dialog Revit showed (health.lastDialog; protocol.ts LastDialog plus diagnostics fields).</summary>
    public sealed class DialogRecord : IWireObject
    {
        public string DialogId { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Text { get; set; }
        /// <summary>Button answered automatically (null when nothing was answered).</summary>
        public string? Answer { get; set; }
        public string? AtUtc { get; set; }
        /// <summary>True when the dialog appeared while one of our requests was executing.</summary>
        public bool Ours { get; set; }
        public bool Auto { get; set; }
        public bool OverrideRejected { get; set; }
        public string? RequestId { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["dialogId"] = DialogId,
                ["title"] = Title,
                ["text"] = Text,
                ["answer"] = Answer,
                ["atUtc"] = AtUtc,
                ["ours"] = Ours,
                ["auto"] = Auto,
                ["overrideRejected"] = OverrideRejected,
                ["requestId"] = RequestId
            };
        }
    }

    public sealed class ListenerHealth : IWireObject
    {
        public int PrimaryWaiting { get; set; }
        public int PrimaryActive { get; set; }
        public int ControlWaiting { get; set; }
        public int ControlActive { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["primaryWaiting"] = PrimaryWaiting,
                ["primaryActive"] = PrimaryActive,
                ["controlWaiting"] = ControlWaiting,
                ["controlActive"] = ControlActive
            };
        }
    }

    public sealed class JobStatusData : IWireObject
    {
        public string JobId { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string State { get; set; } = JobStates.Queued;
        public string? Stage { get; set; }
        public int? Done { get; set; }
        public int? Total { get; set; }
        public long ElapsedMs { get; set; }
        public string? ClientKey { get; set; }
        public string? RequestId { get; set; }
        public string? CreatedAtUtc { get; set; }
        /// <summary>The final response (terminal states only).</summary>
        public BridgeResponse? Result { get; set; }
        /// <summary>The final response as read back from a persisted job record (JSON tree), when Result is null.</summary>
        public object? PersistedResult { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["jobId"] = JobId,
                ["key"] = Key,
                ["state"] = State,
                ["stage"] = Stage,
                ["done"] = Done,
                ["total"] = Total,
                ["elapsedMs"] = ElapsedMs,
                ["clientKey"] = ClientKey,
                ["requestId"] = RequestId,
                ["createdAtUtc"] = CreatedAtUtc,
                ["result"] = Result != null ? Result.ToWire() : PersistedResult
            };
        }
    }

    public sealed class HealthData : IWireObject
    {
        public QueueHealth Queue { get; set; } = new QueueHealth();
        public PumpHealth Pump { get; set; } = new PumpHealth();
        public UiHealth Ui { get; set; } = new UiHealth();
        public NativeActivity? Native { get; set; }
        public string? LastIdlingAtUtc { get; set; }
        public DialogRecord? LastDialog { get; set; }
        public ListenerHealth Listeners { get; set; } = new ListenerHealth();
        public long AdmissionRejections { get; set; }
        public List<JobStatusData> Jobs { get; set; } = new List<JobStatusData>();
        /// <summary>Settings problems (SETTINGS_INVALID) and other add-in findings for status detail:full.</summary>
        public List<string> Problems { get; set; } = new List<string>();

        public Dictionary<string, object?> ToWire()
        {
            var jobs = new List<object?>(Jobs.Count);
            foreach (JobStatusData job in Jobs) jobs.Add(job.ToWire());
            return new Dictionary<string, object?>
            {
                ["queue"] = Queue.ToWire(),
                ["pump"] = Pump.ToWire(),
                ["ui"] = Ui.ToWire(),
                ["native"] = Native?.ToWire(),
                ["lastIdlingAtUtc"] = LastIdlingAtUtc,
                ["lastDialog"] = LastDialog?.ToWire(),
                ["listeners"] = Listeners.ToWire(),
                ["admissionRejections"] = AdmissionRejections,
                ["jobs"] = jobs,
                ["problems"] = new List<object?>(Problems)
            };
        }
    }

    public sealed class RecentWrite : IWireObject
    {
        public string RequestId { get; set; } = string.Empty;
        public string? WriteTag { get; set; }
        public string? ClientKey { get; set; }
        /// <summary>Registry key of the write.</summary>
        public string Key { get; set; } = string.Empty;
        public string? DocKey { get; set; }
        public string State { get; set; } = OutcomeStates.Accepted;
        public string? AtUtc { get; set; }
        public int Created { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        /// <summary>True when the document was saved after this write committed.</summary>
        public bool Saved { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?>
            {
                ["requestId"] = RequestId,
                ["writeTag"] = WriteTag,
                ["clientKey"] = ClientKey,
                ["key"] = Key,
                ["docKey"] = DocKey,
                ["state"] = State,
                ["atUtc"] = AtUtc,
                ["counts"] = new Dictionary<string, object?> { ["created"] = Created, ["modified"] = Modified, ["deleted"] = Deleted },
                ["saved"] = Saved
            };
        }
    }

    public sealed class DialogButton : IWireObject
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>Win32/TaskDialog button id or UIA AutomationId.</summary>
        public string Id { get; set; } = string.Empty;

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["name"] = Name, ["id"] = Id };
        }
    }

    /// <summary>An open dialog listed by the control op "dialogs" (P-REL-ADDIN).</summary>
    public sealed class DialogInfo : IWireObject
    {
        /// <summary>Short handle for press, e.g. "d3".</summary>
        public string Dialog { get; set; } = string.Empty;
        public long Hwnd { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Text { get; set; }
        public List<DialogButton> Buttons { get; set; } = new List<DialogButton>();
        public string? DialogId { get; set; }
        public string? Since { get; set; }
        /// <summary>True for our own dialogs (RevitMcpNext_ ids), which can never be pressed.</summary>
        public bool Ours { get; set; }

        public Dictionary<string, object?> ToWire()
        {
            var buttons = new List<object?>(Buttons.Count);
            foreach (DialogButton button in Buttons) buttons.Add(button.ToWire());
            return new Dictionary<string, object?>
            {
                ["dialog"] = Dialog,
                ["hwnd"] = Hwnd,
                ["title"] = Title,
                ["text"] = Text,
                ["buttons"] = buttons,
                ["dialogId"] = DialogId,
                ["since"] = Since,
                ["ours"] = Ours
            };
        }
    }

    public sealed class PressResult : IWireObject
    {
        public bool Pressed { get; set; }
        /// <summary>uia | tdm | wm_command | bm_click.</summary>
        public string Via { get; set; } = string.Empty;

        public Dictionary<string, object?> ToWire()
        {
            return new Dictionary<string, object?> { ["pressed"] = Pressed, ["via"] = Via };
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Opt-in C# execution: compiler seam implemented by RevitMcpNext.Scripting (D3 §3.13, SPEC §11).
    // The interface uses BCL types only so the scripting assembly never needs Revit or add-in types.
    // ------------------------------------------------------------------------------------------------------------

    public sealed class ScriptDiagnostic
    {
        /// <summary>Compiler id, e.g. CS1002.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>error | warning | info.</summary>
        public string Severity { get; set; } = "error";
        public string Message { get; set; } = string.Empty;
        /// <summary>1-based line in the compiled source (the host maps it back to the user code).</summary>
        public int Line { get; set; }
        /// <summary>1-based column.</summary>
        public int Column { get; set; }
    }

    public sealed class ScriptCompileResult
    {
        public bool Success { get; set; }
        /// <summary>The emitted assembly (IL) when Success is true.</summary>
        public byte[]? Il { get; set; }
        /// <summary>Optional portable PDB.</summary>
        public byte[]? Pdb { get; set; }
        public List<ScriptDiagnostic> Diagnostics { get; set; } = new List<ScriptDiagnostic>();
        public long ElapsedMs { get; set; }
    }

    /// <summary>A use of a denied API found by semantic analysis.</summary>
    public sealed class ScriptDeniedSymbol
    {
        /// <summary>Fully qualified symbol, e.g. System.Diagnostics.Process.Start.</summary>
        public string Symbol { get; set; } = string.Empty;
        /// <summary>always | lifecycle (SPEC §11.6).</summary>
        public string DenyList { get; set; } = "always";
        public int Line { get; set; }
        public int Column { get; set; }
    }

    public sealed class ScriptAnalysisResult
    {
        public List<ScriptDeniedSymbol> DeniedSymbols { get; set; } = new List<ScriptDeniedSymbol>();
        /// <summary>True when the code constructs Transaction, SubTransaction, TransactionGroup or any *EditScope.</summary>
        public bool UsesTransactions { get; set; }
        /// <summary>Parse/bind errors found during analysis.</summary>
        public List<ScriptDiagnostic> Diagnostics { get; set; } = new List<ScriptDiagnostic>();
    }

    /// <summary>
    /// Roslyn-backed compiler loaded lazily from &lt;payload&gt;\scripting\ when code execution is enabled.
    /// referencePaths are absolute assembly paths (framework assemblies, RevitAPI, RevitAPIUI); langVersion 0 = latest.
    /// </summary>
    public interface IScriptCompiler
    {
        /// <summary>Compiler identification for diagnostics, e.g. "Microsoft.CodeAnalysis.CSharp 4.8.0".</summary>
        string Version { get; }

        /// <summary>Semantic analysis only (deny lists, transaction construction); emits nothing.</summary>
        ScriptAnalysisResult Analyze(string source, IReadOnlyList<string> referencePaths, int langVersion);

        /// <summary>Compiles the source (with loop guards already inserted by the caller or the implementation).</summary>
        ScriptCompileResult Compile(string source, IReadOnlyList<string> referencePaths, int langVersion);
    }

    // ------------------------------------------------------------------------------------------------------------
    // JSON tree helpers shared by both serializers (JavaScriptSerializer on net48, System.Text.Json on net10).
    // ------------------------------------------------------------------------------------------------------------

    public static class Wire
    {
        public static object? Get(IDictionary<string, object?> map, string key)
        {
            if (map == null || key == null) return null;
            return map.TryGetValue(key, out object? value) ? value : null;
        }

        /// <summary>Returns the value as a string map (Dictionary or any IDictionary with string keys), else null.</summary>
        public static IDictionary<string, object?>? AsMap(object? value)
        {
            if (value is IDictionary<string, object?> typed) return typed;
            if (value is IDictionary dictionary)
            {
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string key) copy[key] = entry.Value;
                }
                return copy;
            }
            return null;
        }

        /// <summary>Returns the value as a list when it is a JSON array (object[], List, ArrayList), else null.</summary>
        public static IList<object?>? AsList(object? value)
        {
            if (value == null || value is string) return null;
            if (value is IList<object?> typed) return typed;
            if (value is IEnumerable enumerable && !(value is IDictionary))
            {
                var list = new List<object?>();
                foreach (object? item in enumerable) list.Add(item);
                return list;
            }
            return null;
        }

        public static string? GetString(IDictionary<string, object?> map, string key)
        {
            object? value = Get(map, key);
            if (value == null) return null;
            if (value is string text) return text;
            if (value is bool flag) return flag ? "true" : "false";
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        public static long? GetLong(IDictionary<string, object?> map, string key)
        {
            return ToLong(Get(map, key));
        }

        public static bool? GetBool(IDictionary<string, object?> map, string key)
        {
            object? value = Get(map, key);
            if (value == null) return null;
            if (value is bool flag) return flag;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" || text.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        public static List<long> GetLongList(IDictionary<string, object?> map, string key)
        {
            var result = new List<long>();
            IList<object?>? list = AsList(Get(map, key));
            if (list == null) return result;
            foreach (object? item in list)
            {
                long? value = ToLong(item);
                if (value.HasValue) result.Add(value.Value);
            }
            return result;
        }

        public static long? ToLong(object? value)
        {
            switch (value)
            {
                case null: return null;
                case long l: return l;
                case int i: return i;
                case short s: return s;
                case byte b: return b;
                case uint ui: return ui;
                case ulong ul: return ul <= long.MaxValue ? (long)ul : (long?)null;
                case double d: return IsWholeInRange(d) ? (long)d : (long?)null;
                case float f: return IsWholeInRange(f) ? (long)f : (long?)null;
                case decimal m: return m == decimal.Truncate(m) && m >= long.MinValue && m <= long.MaxValue ? (long)m : (long?)null;
                case string text:
                    return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : (long?)null;
                default: return null;
            }
        }

        public static List<object?> Longs(IEnumerable<long>? values)
        {
            var list = new List<object?>();
            if (values == null) return list;
            foreach (long value in values) list.Add(value);
            return list;
        }

        private static bool IsWholeInRange(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Floor(value) == value &&
                   value >= long.MinValue && value <= long.MaxValue;
        }
    }
}
