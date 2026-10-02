using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Addin.Ipc;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>What a job step sees: the originating request context (doc, args, warnings) plus job controls.</summary>
    internal sealed class JobStep
    {
        private readonly JobRecord _job;

        internal JobStep(JobRecord job, UIApplication app, int index)
        {
            _job = job;
            App = app;
            Index = index;
        }

        public string JobId => _job.JobId;
        /// <summary>0-based number of steps already executed for this job.</summary>
        public int Index { get; }
        public UIApplication App { get; }
        /// <summary>The request context the job was started from (re-armed for each step; Doc is re-validated).</summary>
        public RequestContext Context => _job.Context;
        public Document Doc => _job.Context?.Doc;
        /// <summary>Set by job_cancel; steps should stop and return a terminal result (partial outputs removed).</summary>
        public bool CancelRequested => _job.CancelRequested;
        /// <summary>A bag shared by all steps of the job.</summary>
        public IDictionary<string, object> State => _job.Bag;

        /// <summary>Reports progress (job_status stage/done/total; the broker turns it into progress notifications).</summary>
        public void Progress(string stage, int? done = null, int? total = null)
        {
            _job.SetProgress(stage, done, total);
        }

        /// <summary>Cooperative yield check for long loops inside a step (every 256 items; deadline or cancel).</summary>
        public bool ShouldYield(int index)
        {
            return _job.CancelRequested || (Context?.ShouldYield(index) ?? false);
        }
    }

    /// <summary>The outcome of one job step.</summary>
    internal sealed class StepResult
    {
        internal enum StepKind
        {
            Next,
            Repeat,
            Done
        }

        private StepResult(StepKind kind)
        {
            Kind = kind;
        }

        internal StepKind Kind { get; }
        internal OpResult Result { get; private set; }
        internal int DelayMs { get; private set; }
        internal string Stage { get; private set; }
        internal int? Done { get; private set; }
        internal int? Total { get; private set; }

        /// <summary>Advance to the next step in a later Execute pass (the job succeeds after the last step).</summary>
        public static StepResult Next(string stage = null, int? done = null, int? total = null)
        {
            return new StepResult(StepKind.Next) { Stage = stage, Done = done, Total = total };
        }

        /// <summary>Run the same step again in a later pass (polling), not earlier than <paramref name="delayMs"/>.</summary>
        public static StepResult Repeat(int delayMs = 0, string stage = null)
        {
            return new StepResult(StepKind.Repeat) { DelayMs = Math.Max(0, delayMs), Stage = stage };
        }

        /// <summary>Terminal: the job's final result (Ok or an error).</summary>
        public static StepResult Finish(OpResult result)
        {
            return new StepResult(StepKind.Done) { Result = result ?? OpResult.Success() };
        }

        public static StepResult Fail(string code, string message, object details = null)
        {
            return Finish(OpResult.Fail(code, message, details));
        }
    }

    /// <summary>A job (SPEC §9.4, D2 §12.5): steps run one per Execute pass so other requests interleave.</summary>
    internal sealed class JobRecord
    {
        private readonly object _gate = new object();
        internal readonly Queue<Func<JobStep, StepResult>> Steps = new Queue<Func<JobStep, StepResult>>();

        public string JobId { get; internal set; }
        public string Key { get; internal set; }
        public string RequestId { get; internal set; }
        public string ClientKey { get; internal set; }
        public string WriteTag { get; internal set; }
        public DateTime CreatedAtUtc { get; internal set; }
        public DateTime? StartedAtUtc { get; internal set; }
        public DateTime? CompletedAtUtc { get; internal set; }
        public string State { get; internal set; } = JobStates.Queued;
        public string Stage { get; private set; }
        public int? DoneCount { get; private set; }
        public int? TotalCount { get; private set; }
        public BridgeResponse Result { get; internal set; }
        internal RequestContext Context { get; set; }
        internal Func<JobStep, StepResult> CurrentStep { get; set; }
        internal int StepsRun { get; set; }
        internal DateTime NotBeforeUtc { get; set; }
        internal volatile bool CancelRequested;
        internal readonly Dictionary<string, object> Bag = new Dictionary<string, object>(StringComparer.Ordinal);
        internal readonly TaskCompletionSource<BridgeResponse> Completion =
            new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsTerminal => JobStates.IsTerminal(State);

        internal void SetProgress(string stage, int? done, int? total)
        {
            lock (_gate)
            {
                if (stage != null) Stage = stage;
                if (done.HasValue) DoneCount = done;
                if (total.HasValue) TotalCount = total;
            }
        }

        public JobInfo ToJobInfo()
        {
            lock (_gate)
            {
                return new JobInfo { JobId = JobId, State = State, Stage = Stage, Done = DoneCount, Total = TotalCount };
            }
        }

        public JobStatusData ToStatus()
        {
            lock (_gate)
            {
                DateTime end = CompletedAtUtc ?? DateTime.UtcNow;
                DateTime start = StartedAtUtc ?? CreatedAtUtc;
                return new JobStatusData
                {
                    JobId = JobId,
                    Key = Key,
                    State = State,
                    Stage = Stage,
                    Done = DoneCount,
                    Total = TotalCount,
                    ElapsedMs = Math.Max(0, (long)(end - start).TotalMilliseconds),
                    ClientKey = ClientKey,
                    RequestId = RequestId,
                    CreatedAtUtc = DocumentRegistry.Utc(CreatedAtUtc),
                    Result = IsTerminal ? Result : null
                };
            }
        }
    }

    /// <summary>
    /// Runs multi-pass work (SPEC §9.4): JobRunner.Start(ctx, steps) from a handler, or a whole request as a job
    /// (asJob). One step per Execute pass; job_status/job_cancel on the control pipe; terminal records persisted to
    /// &lt;home&gt;\jobs\&lt;instanceId&gt;\&lt;jobId&gt;.json. In memory: 64 jobs / 1 h. Public signatures are frozen for wave 2.
    /// </summary>
    internal sealed class JobRunner
    {
        private const int MaxJobsInMemory = 64;
        private static readonly TimeSpan MemoryRetention = TimeSpan.FromHours(1);
        private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(10);

        private readonly object _gate = new object();
        private readonly McpHome _home;
        private readonly InstanceIdentity _identity;
        private readonly List<JobRecord> _jobs = new List<JobRecord>();

        public JobRunner(McpHome home, InstanceIdentity identity)
        {
            _home = home;
            _identity = identity;
        }

        /// <summary>Builds the wire response of a terminal step result (set by the dispatcher at startup).</summary>
        internal Func<RequestContext, OpResult, BridgeResponse> ResponseBuilder { get; set; }

        /// <summary>Raised (UI thread) when a job reaches a terminal state.</summary>
        public event Action<JobRecord> Completed;

        /// <summary>
        /// Runs <paramref name="steps"/> as a job, one per Execute pass, and returns the OpResult the handler must return.
        /// The request then completes with the job's final result, or with {job:{jobId,state:"running",...}} when its
        /// time budget ends first (the broker polls job_status). Inside a job (asJob or an earlier Start) the steps are
        /// appended to the current job instead.
        /// </summary>
        public OpResult Start(RequestContext ctx, IEnumerable<Func<JobStep, StepResult>> steps, string stage = null, int? total = null)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (steps == null) throw new ArgumentNullException(nameof(steps));
            JobRecord job = ctx.Job;
            lock (_gate)
            {
                if (job == null)
                {
                    job = NewRecordUnsafe(ctx.Request);
                    job.State = JobStates.Running;
                    job.StartedAtUtc = DateTime.UtcNow;
                    job.Context = ctx;
                    ctx.Job = job;
                }
                foreach (Func<JobStep, StepResult> step in steps)
                {
                    if (step != null) job.Steps.Enqueue(step);
                }
            }
            job.SetProgress(stage, total.HasValue ? 0 : (int?)null, total);
            McpRuntime.Pump?.OnEnqueue();
            return OpResult.JobHandoff(job.JobId);
        }

        /// <summary>
        /// Creates a job for an asJob request whose first step is <paramref name="firstStep"/> (the dispatcher). Returns
        /// the record at once; the pipe answers {job:{jobId,state:"queued"}}.
        /// </summary>
        public JobRecord Submit(BridgeRequest request, Func<JobStep, StepResult> firstStep)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            JobRecord job;
            lock (_gate)
            {
                job = NewRecordUnsafe(request);
                job.Steps.Enqueue(firstStep);
            }
            McpRuntime.Pump?.OnEnqueue();
            return job;
        }

        /// <summary>True when a job has a step that may run now.</summary>
        public bool HasRunnableStep
        {
            get
            {
                DateTime now = DateTime.UtcNow;
                lock (_gate)
                {
                    return _jobs.Any(job => !job.IsTerminal && (job.CurrentStep != null || job.Steps.Count > 0 || job.CancelRequested) && job.NotBeforeUtc <= now);
                }
            }
        }

        /// <summary>True while any job is not terminal (the pump keeps waking).</summary>
        public bool HasActiveJobs
        {
            get { lock (_gate) return _jobs.Any(job => !job.IsTerminal); }
        }

        /// <summary>
        /// UI thread: runs one step of the oldest runnable job. <paramref name="filter"/> (e.g. idle-safe keys for the
        /// Idling fallback) can skip jobs. Returns true when a step ran.
        /// </summary>
        public bool RunNext(UIApplication app, Func<JobRecord, bool> filter = null)
        {
            JobRecord job;
            DateTime now = DateTime.UtcNow;
            lock (_gate)
            {
                job = _jobs.FirstOrDefault(candidate => !candidate.IsTerminal &&
                    (candidate.CurrentStep != null || candidate.Steps.Count > 0 || candidate.CancelRequested) &&
                    candidate.NotBeforeUtc <= now && (filter == null || filter(candidate)));
                if (job == null) return false;
                if (job.State == JobStates.Queued)
                {
                    job.State = JobStates.Running;
                    job.StartedAtUtc = now;
                }
            }

            RevitRequestQueue queue = McpRuntime.Queue;
            queue?.BeginExecution(null, new ExecutingInfo
            {
                RequestId = job.RequestId,
                Op = job.Key,
                SinceUtc = DocumentRegistry.Utc(now),
                ClientKey = job.ClientKey,
                WriteTag = job.WriteTag,
                JobId = job.JobId
            });
            RequestContext previous = RequestContext.Current;
            try
            {
                if (job.CancelRequested)
                {
                    Finish(job, OpResult.Fail(ErrorCodes.RequestCancelled, "The job was cancelled before it finished."), JobStates.Cancelled);
                    return true;
                }

                Func<JobStep, StepResult> step;
                lock (_gate)
                {
                    step = job.CurrentStep ?? (job.Steps.Count > 0 ? job.Steps.Dequeue() : null);
                    job.CurrentStep = step;
                }
                if (step == null)
                {
                    Finish(job, OpResult.Success(null, job.Stage), JobStates.Succeeded);
                    return true;
                }

                if (job.Context != null)
                {
                    job.Context.PrepareForJobStep(app, StepBudget);
                    RequestContext.Current = job.Context;
                }

                StepResult result;
                try
                {
                    result = step(new JobStep(job, app, job.StepsRun)) ?? StepResult.Next();
                }
                catch (OpException ex)
                {
                    result = StepResult.Finish(OpResult.From(ex));
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Error("jobs", "Job step failed. jobId=" + job.JobId + " key=" + job.Key, ex);
                    result = StepResult.Finish(Dispatcher.MapException(job.Context, ex));
                }
                job.StepsRun++;

                switch (result.Kind)
                {
                    case StepResult.StepKind.Next:
                        job.SetProgress(result.Stage, result.Done, result.Total);
                        lock (_gate) job.CurrentStep = null;
                        bool more;
                        lock (_gate) more = job.Steps.Count > 0;
                        if (!more) Finish(job, OpResult.Success(null, result.Stage ?? job.Stage), JobStates.Succeeded);
                        break;
                    case StepResult.StepKind.Repeat:
                        job.SetProgress(result.Stage, null, null);
                        lock (_gate) job.NotBeforeUtc = DateTime.UtcNow.AddMilliseconds(result.DelayMs);
                        break;
                    default:
                        Finish(job, result.Result, result.Result.Ok ? JobStates.Succeeded : JobStates.Failed);
                        break;
                }
                return true;
            }
            finally
            {
                RequestContext.Current = previous;
                queue?.EndExecution();
            }
        }

        public JobRecord Find(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return null;
            lock (_gate) return _jobs.FirstOrDefault(job => string.Equals(job.JobId, jobId, StringComparison.Ordinal));
        }

        /// <summary>The job started by a request id (asJob replays), or null.</summary>
        public JobRecord FindByRequest(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return null;
            lock (_gate) return _jobs.FirstOrDefault(job => string.Equals(job.RequestId, requestId, StringComparison.Ordinal));
        }

        /// <summary>job_status: memory first, then the persisted terminal record; null when unknown.</summary>
        public JobStatusData Status(string jobId)
        {
            JobRecord job = Find(jobId);
            if (job != null) return job.ToStatus();
            return ReadPersisted(jobId);
        }

        /// <summary>job_cancel: sets the cooperative flag (checked between steps). False when unknown or finished.</summary>
        public bool Cancel(string jobId)
        {
            JobRecord job = Find(jobId);
            if (job == null || job.IsTerminal) return false;
            job.CancelRequested = true;
            McpRuntime.Pump?.OnEnqueue();
            return true;
        }

        /// <summary>The job's final response (completes when terminal).</summary>
        public Task<BridgeResponse> WhenDone(string jobId)
        {
            JobRecord job = Find(jobId);
            return job?.Completion.Task ?? Task.FromResult<BridgeResponse>(null);
        }

        /// <summary>Non-terminal jobs plus recent terminal ones (health).</summary>
        public List<JobStatusData> Snapshot(int max = 20)
        {
            lock (_gate)
            {
                return _jobs.OrderByDescending(job => job.CreatedAtUtc).Take(max).Select(job => job.ToStatus()).ToList();
            }
        }

        /// <summary>Fails every non-terminal job (shutdown).</summary>
        public void CancelAll(string code, string message)
        {
            List<JobRecord> active;
            lock (_gate) active = _jobs.Where(job => !job.IsTerminal).ToList();
            foreach (JobRecord job in active)
            {
                Finish(job, OpResult.Fail(code, message), JobStates.Interrupted);
            }
        }

        private JobRecord NewRecordUnsafe(BridgeRequest request)
        {
            Prune();
            var job = new JobRecord
            {
                JobId = Guid.NewGuid().ToString("N").Substring(0, 16),
                Key = request?.Op ?? string.Empty,
                RequestId = request?.RequestId,
                ClientKey = request?.ClientKey,
                WriteTag = request?.WriteTag,
                CreatedAtUtc = DateTime.UtcNow
            };
            _jobs.Add(job);
            return job;
        }

        private void Finish(JobRecord job, OpResult result, string state)
        {
            BridgeResponse response;
            try
            {
                response = ResponseBuilder != null
                    ? ResponseBuilder(job.Context, result)
                    : result.ToResponse(job.RequestId, new ResponseMetrics { Via = "job" });
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("jobs", "Building the job result failed. jobId=" + job.JobId, ex);
                response = BridgeResponse.Failure(job.RequestId, ErrorCodes.InternalError, "The job finished but its result could not be built: " + ex.Message);
            }
            response.Job = new JobInfo { JobId = job.JobId, State = state, Stage = job.Stage, Done = job.DoneCount, Total = job.TotalCount };

            lock (_gate)
            {
                job.Result = response;
                job.State = state;
                job.CompletedAtUtc = DateTime.UtcNow;
                job.CurrentStep = null;
                job.Steps.Clear();
            }
            Persist(job);
            job.Completion.TrySetResult(response);
            try
            {
                Completed?.Invoke(job);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("jobs", "A job completion subscriber failed.", ex);
            }
        }

        private void Persist(JobRecord job)
        {
            if (_home == null || _identity == null) return;
            try
            {
                string path = _home.JobFile(_identity.InstanceId, job.JobId);
                string json = JsonWireCodec.Serialize(job.ToStatus().ToWire());
                if (!AtomicFile.WriteAllText(path, json, out string error))
                {
                    DiagnosticsLogger.Warn("jobs", "Could not persist job " + job.JobId + ": " + error);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("jobs", "Could not persist job " + job.JobId + ": " + ex.Message);
            }
        }

        private JobStatusData ReadPersisted(string jobId)
        {
            if (_home == null || _identity == null || string.IsNullOrWhiteSpace(jobId)) return null;
            if (jobId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            try
            {
                string path = _home.JobFile(_identity.InstanceId, jobId);
                if (!File.Exists(path)) return null;
                var map = JsonWireCodec.DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
                if (map == null) return null;
                return new JobStatusData
                {
                    JobId = Wire.GetString(map, "jobId") ?? jobId,
                    Key = Wire.GetString(map, "key") ?? string.Empty,
                    State = Wire.GetString(map, "state") ?? JobStates.Interrupted,
                    Stage = Wire.GetString(map, "stage"),
                    Done = (int?)Wire.GetLong(map, "done"),
                    Total = (int?)Wire.GetLong(map, "total"),
                    ElapsedMs = Wire.GetLong(map, "elapsedMs") ?? 0,
                    ClientKey = Wire.GetString(map, "clientKey"),
                    RequestId = Wire.GetString(map, "requestId"),
                    CreatedAtUtc = Wire.GetString(map, "createdAtUtc"),
                    PersistedResult = Wire.Get(map, "result")
                };
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("jobs", "Could not read job record " + jobId + ": " + ex.Message);
                return null;
            }
        }

        private void Prune()
        {
            DateTime cutoff = DateTime.UtcNow - MemoryRetention;
            _jobs.RemoveAll(job => job.IsTerminal && job.CompletedAtUtc.HasValue && job.CompletedAtUtc.Value < cutoff);
            while (_jobs.Count >= MaxJobsInMemory)
            {
                JobRecord oldest = _jobs.Where(job => job.IsTerminal).OrderBy(job => job.CompletedAtUtc).FirstOrDefault();
                if (oldest == null) break;
                _jobs.Remove(oldest);
            }
        }
    }
}
