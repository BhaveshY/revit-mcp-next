using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    /// <summary>
    /// Control-pipe operations (SPEC §4.6.4), served on pipe threads; they never touch the Revit UI thread and never
    /// call the Revit API. dialogs/press return UNSUPPORTED_OP until P-REL-ADDIN implements them.
    /// </summary>
    internal sealed class ControlOperations
    {
        private readonly Func<ListenerHealth> _listeners;
        private readonly Func<long> _admissionRejections;

        public ControlOperations(Func<ListenerHealth> listeners, Func<long> admissionRejections = null)
        {
            _listeners = listeners ?? (() => new ListenerHealth());
            _admissionRejections = admissionRejections ?? (() => 0);
        }

        public BridgeResponse Handle(BridgeRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            BridgeResponse response;
            try
            {
                switch (request.Op)
                {
                    case ControlOps.Hello: response = Ok(request, Hello()); break;
                    case ControlOps.Snapshot: response = Snapshot(request); break;
                    case ControlOps.Health: response = Ok(request, Health()); break;
                    case ControlOps.CancelRequest: response = CancelRequest(request); break;
                    case ControlOps.GetRequestResult: response = GetRequestResult(request); break;
                    case ControlOps.RecentWrites: response = RecentWrites(request); break;
                    case ControlOps.JobStatus: response = JobStatus(request); break;
                    case ControlOps.JobCancel: response = JobCancel(request); break;
                    case ControlOps.Dialogs:
                    case ControlOps.Press:
                        response = BridgeResponse.Failure(request.RequestId, ErrorCodes.UnsupportedOp,
                            "The control op '" + request.Op + "' is not available in this add-in build.",
                            new Dictionary<string, object> { ["op"] = request.Op });
                        break;
                    default:
                        response = BridgeResponse.Failure(request.RequestId, ErrorCodes.UnknownOp,
                            "Unknown control op '" + request.Op + "'.",
                            new Dictionary<string, object> { ["ops"] = ControlOps.All.ToList(), ["closest"] = Closest(request.Op) });
                        break;
                }
            }
            catch (OpException ex)
            {
                response = BridgeResponse.Failure(request.RequestId, ex.Code, ex.Message, ex.Details);
            }
            catch (Exception ex)
            {
                Diagnostics.DiagnosticsLogger.Error("control", "Control op " + request.Op + " failed.", ex);
                response = BridgeResponse.Failure(request.RequestId, ErrorCodes.InternalError, ex.Message,
                    new Dictionary<string, object> { ["requestId"] = request.RequestId, ["logPath"] = Diagnostics.DiagnosticsLogger.CurrentLogFile });
            }
            response.Metrics = new ResponseMetrics { ExecMs = stopwatch.ElapsedMilliseconds, Via = "control" };
            return response;
        }

        public HelloData Hello()
        {
            InstanceIdentity identity = McpRuntime.Instance ?? new InstanceIdentity();
            AuthTokenStore auth = McpRuntime.Auth;
            OperationRegistry ops = McpRuntime.Ops;
            return new HelloData
            {
                InstanceId = identity.InstanceId,
                Pid = identity.Pid,
                Year = identity.Year,
                Build = identity.Build,
                Language = identity.Language,
                AddinVersion = identity.AddinVersion,
                GitSha = identity.GitSha,
                PayloadId = identity.PayloadId,
                CatalogHash = identity.CatalogHash,
                State = McpRuntime.Registry?.State ?? "starting",
                Home = identity.HomeRoot,
                AuthFile = auth?.AuthFile ?? string.Empty,
                AuthFp = auth?.Fingerprint ?? string.Empty,
                AuthState = auth?.State ?? "missing",
                Capabilities = ops?.Capabilities() ?? new Capabilities { Mismatches = new List<string> { "REGISTRY_NOT_BUILT" } },
                TestOps = McpRuntime.TestOpsEnabled
            };
        }

        private static BridgeResponse Snapshot(BridgeRequest request)
        {
            DocumentRegistry registry = McpRuntime.Registry;
            if (registry == null)
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.RevitStarting, "The add-in is still starting.");
            }
            DocSnapshot snapshot = registry.Current;
            long? sinceSeq = Wire.GetLong(request.Args, "sinceSeq");
            if (sinceSeq.HasValue && sinceSeq.Value == snapshot.Seq)
            {
                return Ok(request, new Dictionary<string, object> { ["unchanged"] = true, ["seq"] = snapshot.Seq });
            }
            return Ok(request, snapshot);
        }

        public HealthData Health()
        {
            var health = new HealthData
            {
                Queue = McpRuntime.Queue?.GetHealth() ?? new QueueHealth(),
                Pump = McpRuntime.Pump?.GetHealth() ?? new PumpHealth(),
                Ui = McpRuntime.UiMonitor?.GetHealth() ?? new UiHealth(),
                Native = McpRuntime.Registry?.Native,
                LastDialog = McpRuntime.Dialogs?.LastDialog,
                Listeners = _listeners(),
                AdmissionRejections = _admissionRejections(),
                Jobs = McpRuntime.Jobs?.Snapshot() ?? new List<JobStatusData>()
            };
            DateTime lastIdling = McpRuntime.Registry?.LastIdlingUtc ?? DateTime.MinValue;
            if (lastIdling != DateTime.MinValue) health.LastIdlingAtUtc = DocumentRegistry.Utc(lastIdling);

            SettingsStore settings = McpRuntime.Settings;
            if (!string.IsNullOrEmpty(settings?.Problem)) health.Problems.Add(settings.Problem);
            if (McpRuntime.Ops != null && McpRuntime.Ops.CatalogMissing) health.Problems.Add(ErrorCodes.CatalogMissing);
            string authProblem = McpRuntime.Auth?.Problem;
            if (!string.IsNullOrEmpty(authProblem)) health.Problems.Add(ErrorCodes.AuthNotConfigured + ": " + authProblem);
            string homeWarning = McpRuntime.Home?.LocationWarning;
            if (!string.IsNullOrEmpty(homeWarning)) health.Problems.Add(homeWarning);
            return health;
        }

        private static BridgeResponse CancelRequest(BridgeRequest request)
        {
            string requestId = Wire.GetString(request.Args, "requestId");
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw OpException.InvalidArgs("requestId", "cancel_request needs the requestId of the queued request.");
            }
            string outcome = McpRuntime.Queue?.TryCancel(requestId, Wire.GetString(request.Args, "reason")) ?? CancelOutcomes.NotFound;
            return Ok(request, new Dictionary<string, object> { ["cancelled"] = outcome, ["requestId"] = requestId });
        }

        private static BridgeResponse GetRequestResult(BridgeRequest request)
        {
            string requestId = Wire.GetString(request.Args, "requestId");
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw OpException.InvalidArgs("requestId", "get_request_result needs a requestId.");
            }
            LedgerEntry entry = McpRuntime.Ledger?.Find(requestId);
            if (entry == null)
            {
                return Ok(request, new Dictionary<string, object> { ["found"] = false, ["requestId"] = requestId });
            }
            return Ok(request, entry.ToWire());
        }

        private static BridgeResponse RecentWrites(BridgeRequest request)
        {
            string docKey = Wire.GetString(request.Args, "docKey");
            int limit = (int)Math.Max(1, Math.Min(20, Wire.GetLong(request.Args, "limit") ?? 20));
            DocumentRegistry registry = McpRuntime.Registry;
            List<RecentWrite> writes = McpRuntime.Ledger?.Recent(docKey, limit, key =>
            {
                if (registry == null || string.IsNullOrWhiteSpace(key)) return null;
                SnapshotDoc doc = registry.Current.Docs.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
                return doc == null ? null : registry.LastSavedUtc(doc.Rid);
            }) ?? new List<RecentWrite>();
            return Ok(request, writes);
        }

        private static BridgeResponse JobStatus(BridgeRequest request)
        {
            string jobId = Wire.GetString(request.Args, "jobId");
            if (string.IsNullOrWhiteSpace(jobId)) throw OpException.InvalidArgs("jobId", "job_status needs a jobId.");
            JobStatusData status = McpRuntime.Jobs?.Status(jobId);
            if (status == null)
            {
                return BridgeResponse.Failure(request.RequestId, ErrorCodes.JobUnknown, "No job " + jobId + " is known to this Revit instance.",
                    new Dictionary<string, object> { ["jobId"] = jobId });
            }
            return Ok(request, status);
        }

        private static BridgeResponse JobCancel(BridgeRequest request)
        {
            string jobId = Wire.GetString(request.Args, "jobId");
            if (string.IsNullOrWhiteSpace(jobId)) throw OpException.InvalidArgs("jobId", "job_cancel needs a jobId.");
            bool cancelled = McpRuntime.Jobs?.Cancel(jobId) ?? false;
            return Ok(request, new Dictionary<string, object> { ["cancelled"] = cancelled, ["jobId"] = jobId });
        }

        private static BridgeResponse Ok(BridgeRequest request, object data)
        {
            return BridgeResponse.Success(request.RequestId, data);
        }

        private static string Closest(string op)
        {
            if (string.IsNullOrEmpty(op)) return ControlOps.Hello;
            return ControlOps.All.OrderBy(candidate => Levenshtein(candidate, op)).First();
        }

        internal static int Levenshtein(string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return previous[b.Length];
        }
    }
}
