using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Everything a handler needs for one request (D2 §12.3 + D3 §2.7): the resolved document, lenient args, the
    /// cooperative deadline, collected warnings/notices, services, result builders and the deferral/job seams.
    /// Created by the Dispatcher on the UI thread; <see cref="Current"/> is set while the handler runs.
    /// </summary>
    internal sealed class RequestContext
    {
        /// <summary>Margin between the request's time budget and the cooperative read deadline (D2 §12.2).</summary>
        public const int ReadDeadlineMarginMs = 1500;

        [ThreadStatic] private static RequestContext _current;

        private readonly QueuedRevitWorkItem _item;
        private DateTime _deadlineUtc;

        public RequestContext(BridgeRequest request, UIApplication app, OpMeta meta, string via, QueuedRevitWorkItem item = null)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            App = app;
            Meta = meta;
            Via = via ?? "externalEvent";
            _item = item;
            Stopwatch = Stopwatch.StartNew();
            // The broker already applied the per-call "units" arg (SPEC §5.3); only in-process callers get it applied here.
            Args = new PayloadReader(request.Args, null, Via == "inProcess" ? (double?)null : 1.0);
            long elapsedSinceReceipt = item?.AgeMs ?? 0;
            _deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(250, request.TimeoutMs - elapsedSinceReceipt - ReadDeadlineMarginMs));
        }

        /// <summary>The context of the handler running on this thread (null outside handlers).</summary>
        public static RequestContext Current
        {
            get => _current;
            internal set => _current = value;
        }

        // Request -----------------------------------------------------------------------------------------------

        public BridgeRequest Request { get; }
        public UIApplication App { get; private set; }
        public Application RevitApp => App?.Application;
        public OpMeta Meta { get; internal set; }
        /// <summary>externalEvent | idling | inProcess | job.</summary>
        public string Via { get; private set; }
        public string RequestId => Request.RequestId;
        public string Key => Request.Op;
        public string Tool => Meta?.Tool ?? Key.Split('.')[0];
        public string Op => Meta?.Op;
        public string Kind => Meta?.Kind ?? Request.Kind;
        public string ClientKey => Request.ClientKey;
        /// <summary>The broker write tag, normalized (w0 when missing).</summary>
        public string WriteTag => Naming.NormalizeTag(Request.WriteTag);
        public bool IsPreview => Request.IsPreview;
        /// <summary>Set when applying a confirmed plan.</summary>
        public ConfirmedPlan Confirmed => Request.Confirmed;
        /// <summary>The broker wants all page ids (for an r# handle).</summary>
        public bool WantIds => Request.WantIds || Args.Bool("want_ids", false);
        public bool IsInProcess => Via == "inProcess";
        public PayloadReader Args { get; internal set; }

        // Document ----------------------------------------------------------------------------------------------

        /// <summary>The target document (null for scope none unless the request named one).</summary>
        public Document Doc { get; internal set; }
        /// <summary>A UIDocument for <see cref="Doc"/> (works for non-active documents too).</summary>
        public UIDocument UiDoc { get; internal set; }
        public long Rid { get; internal set; }
        /// <summary>True when <see cref="Doc"/> is the UI-active document.</summary>
        public bool DocIsActive { get; internal set; }

        /// <summary>The target document's generation now.</summary>
        public long Generation => Doc == null ? 0 : Registry?.GetGeneration(Doc) ?? 0;

        /// <summary>The year of this Revit (2024/2027).</summary>
        public int Year => Instance?.Year ?? 0;

        // Services ----------------------------------------------------------------------------------------------

        public McpHome Home => McpRuntime.Home;
        public McpSettings Settings => McpRuntime.CurrentSettings;
        public SettingsStore SettingsStore => McpRuntime.Settings;
        public DocumentRegistry Registry => McpRuntime.Registry;
        public JobRunner Jobs => McpRuntime.Jobs;
        public OperationRegistry Ops => McpRuntime.Ops;
        public InstanceIdentity Instance => McpRuntime.Instance;

        // Deadline and cancellation -----------------------------------------------------------------------------

        public Stopwatch Stopwatch { get; }

        /// <summary>When long work should stop and return partial results (request budget minus 1.5 s).</summary>
        public DateTime DeadlineUtc => _deadlineUtc;
        public TimeSpan Remaining => _deadlineUtc - DateTime.UtcNow;
        public bool OverDeadline => DateTime.UtcNow > _deadlineUtc;

        /// <summary>Set by the control op cancel_request for a running read (or job_cancel for a job).</summary>
        public bool CancelRequested => (_item?.CancelRequested ?? false) || (Job?.CancelRequested ?? false);

        /// <summary>Call every item of a long loop: true (every 256 items) when the deadline passed or cancel was requested.</summary>
        public bool ShouldYield(int index)
        {
            if ((index & 255) != 0) return false;
            return OverDeadline || CancelRequested;
        }

        // Collected output --------------------------------------------------------------------------------------

        public List<BridgeWarning> Warnings { get; } = new List<BridgeWarning>();
        public List<BridgeNotice> Notices { get; } = new List<BridgeNotice>();
        /// <summary>Warnings captured outside our transactions (FailuresProcessing safety net).</summary>
        public List<CapturedFailure> RevitWarnings { get; } = new List<CapturedFailure>();
        public List<CapturedFailure> RevitErrors { get; } = new List<CapturedFailure>();
        /// <summary>A scratch bag for handlers and helpers of this request.</summary>
        public Dictionary<string, object> Items { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

        public void Warn(string code, string text, IEnumerable<long> ids = null)
        {
            OpResult.AddWarning(Warnings, code, text, ids, 1);
        }

        public void Warn(string code, string text, IEnumerable<ElementId> ids)
        {
            OpResult.AddWarning(Warnings, code, text, ids?.Where(id => id != null).Select(id => id.Value), 1);
        }

        public void Notice(string code, string text)
        {
            OpResult.AddNotice(Notices, code, text);
        }

        /// <summary>Reports progress (job stage when running as a job; ignored otherwise).</summary>
        public void Progress(string stage, int? done = null, int? total = null)
        {
            Job?.SetProgress(stage, done, total);
        }

        // Results -----------------------------------------------------------------------------------------------

        public OpResult Ok(object data = null, string summary = null)
        {
            return OpResult.Success(data, summary);
        }

        public OpResult Fail(string code, string message, object details = null)
        {
            return OpResult.Fail(code, message, details);
        }

        /// <summary>Use as <c>throw ctx.Error(...)</c>.</summary>
        public OpException Error(string code, string message, object details = null)
        {
            return new OpException(code, message, details);
        }

        /// <summary>
        /// For rules the handler evaluates itself (file_overwrite, unsaved_close, always, ...): returns null when the
        /// request carries a confirmation, else a NOT APPLIED result with needsConfirm {rule, plan, stamp}.
        /// <paramref name="summary"/> becomes needsConfirm.plan (the broker's one-line summary); a structured
        /// <paramref name="plan"/> goes in the response data.
        /// </summary>
        public OpResult ConfirmOrPlan(string rule, object plan, string summary, IEnumerable<long> deleteSet = null)
        {
            if (Confirmed != null) return null;
            var needs = new NeedsConfirm { Rule = rule, Plan = summary ?? plan as string ?? rule, Stamp = Generation };
            if (deleteSet != null)
            {
                needs.DeleteSet = deleteSet.ToList();
                needs.Blast.DeleteTotal = needs.DeleteSet.Count;
                needs.Blast.Sample = needs.DeleteSet.Take(50).ToList();
            }
            object data = plan == null || plan is string ? null : plan;
            return OpResult.Success(data, summary).WithNeedsConfirm(needs);
        }

        // Deferral and jobs -------------------------------------------------------------------------------------

        /// <summary>The job this request runs in (asJob, or after JobRunner.Start); null otherwise.</summary>
        public JobRecord Job { get; internal set; }
        public string JobId => Job?.JobId;

        internal QueuedRevitWorkItem WorkItem => _item;

        internal Task<OpResult> DeferredTask { get; private set; }
        internal DateTime DeferredDeadlineUtc { get; private set; }
        internal Func<OpResult> DeferredOnTimeout { get; private set; }
        public bool HasDeferredCompletion => DeferredTask != null;

        /// <summary>
        /// Keeps the request executing after the handler returns (SPEC §9.4): no other queued item starts until
        /// <paramref name="task"/> completes or <paramref name="timeout"/> passes, and the pump keeps waking Revit.
        /// The handler's own return value is ignored; the task's result becomes the response. On timeout,
        /// <paramref name="onTimeout"/> (or an INTERNAL_ERROR) is returned. Used by undo (PostCommand) and close sequencing.
        /// Not available inside jobs or the in-process bridge (use job steps there).
        /// </summary>
        public void DeferCompletion(Task<OpResult> task, TimeSpan timeout, Func<OpResult> onTimeout = null)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (_item == null || Job != null)
            {
                throw new InvalidOperationException("DeferCompletion is only available for queued requests (not jobs or in-process calls).");
            }
            DeferredTask = task;
            DeferredDeadlineUtc = DateTime.UtcNow + (timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : timeout);
            DeferredOnTimeout = onTimeout;
        }

        /// <summary>Re-arms the context for one job step (fresh step deadline; the document is re-validated).</summary>
        internal void PrepareForJobStep(UIApplication app, TimeSpan budget)
        {
            if (app != null) App = app;
            Via = "job";
            _deadlineUtc = DateTime.UtcNow + budget;
            if (Doc != null)
            {
                bool valid;
                try { valid = Doc.IsValidObject; } catch { valid = false; }
                if (!valid)
                {
                    throw new OpException(ErrorCodes.TargetClosed, "The document of this job was closed.",
                        new Dictionary<string, object> { ["rid"] = Rid });
                }
            }
        }
    }
}
