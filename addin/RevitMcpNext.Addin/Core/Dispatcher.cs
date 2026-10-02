using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The dispatch pipeline of SPEC §9.3 (protocol/auth already done by the pipe host): key lookup → kind check →
    /// min year → document resolution by {rid,key} per scope → UI-active check → in-process flag → invoke (inside the
    /// dialog-responder scope; exceptions mapped to error codes). Runs on the Revit UI thread.
    /// </summary>
    internal static class Dispatcher
    {
        /// <summary>Executes a dequeued item. Returns null when completion was deferred or handed to a job.</summary>
        public static BridgeResponse Execute(QueuedRevitWorkItem item, UIApplication app, string via)
        {
            RequestContext ctx = null;
            RequestContext previous = RequestContext.Current;
            try
            {
                OpMeta meta = McpRuntime.Ops?.GetMeta(item.Request.Op);
                ctx = new RequestContext(item.Request, app, meta, via, item);
                item.Context = ctx;
                RequestContext.Current = ctx;
                OpResult result = Run(ctx);

                if (ctx.HasDeferredCompletion)
                {
                    item.DeferredTask = ctx.DeferredTask;
                    item.DeferredDeadlineUtc = ctx.DeferredDeadlineUtc;
                    item.DeferredOnTimeout = ctx.DeferredOnTimeout;
                    return null;
                }

                if (result != null && result.HandoffJobId != null)
                {
                    AttachJobToItem(item, ctx, result.HandoffJobId);
                    return null;
                }
                return BuildResponse(ctx, result);
            }
            catch (Exception ex)
            {
                return BuildResponse(ctx, MapException(ctx, ex), item.Request);
            }
            finally
            {
                RequestContext.Current = previous;
            }
        }

        /// <summary>The first step of an asJob request: dispatches it and continues with any steps the handler adds.</summary>
        public static StepResult RunRequestAsJobStep(JobStep step, BridgeRequest request)
        {
            RequestContext ctx = null;
            try
            {
                OpMeta meta = McpRuntime.Ops?.GetMeta(request.Op);
                ctx = new RequestContext(request, step.App, meta, "job");
                JobRecord job = McpRuntime.Jobs?.Find(step.JobId);
                ctx.Job = job;
                if (job != null) job.Context = ctx;
                RequestContext.Current = ctx;
                OpResult result = Run(ctx);
                if (ctx.HasDeferredCompletion)
                {
                    return StepResult.Fail(ErrorCodes.InternalError, "Deferred completion is not supported inside jobs (" + request.Op + ").");
                }
                if (result != null && result.HandoffJobId != null) return StepResult.Next();
                return StepResult.Finish(result);
            }
            catch (Exception ex)
            {
                return StepResult.Finish(MapException(ctx, ex));
            }
        }

        /// <summary>
        /// In-process bridge (pyRevit/Dynamo): same pipeline, UI thread only, keys with inproc:true only.
        /// </summary>
        public static BridgeResponse InvokeInProcess(UIApplication app, BridgeRequest request)
        {
            RequestContext ctx = null;
            RequestContext previous = RequestContext.Current;
            try
            {
                OpMeta meta = McpRuntime.Ops?.GetMeta(request.Op);
                ctx = new RequestContext(request, app, meta, "inProcess");
                RequestContext.Current = ctx;
                OpResult result = Run(ctx);
                if (ctx.HasDeferredCompletion || (result != null && result.HandoffJobId != null))
                {
                    return BuildResponse(ctx, OpResult.Fail(ErrorCodes.UnsupportedOp, request.Op + " cannot complete synchronously from the in-process bridge."));
                }
                return BuildResponse(ctx, result);
            }
            catch (Exception ex)
            {
                return BuildResponse(ctx, MapException(ctx, ex), request);
            }
            finally
            {
                RequestContext.Current = previous;
            }
        }

        /// <summary>Steps 2-8 of SPEC §9.3. Throws OpException for pipeline errors.</summary>
        internal static OpResult Run(RequestContext ctx)
        {
            OperationRegistry ops = McpRuntime.Ops ?? throw new OpException(ErrorCodes.RevitStarting, "The add-in is still starting.");
            string key = ctx.Request.Op;
            int year = ctx.Year;

            // 2. key lookup
            OpMeta meta = ctx.Meta ?? ops.GetMeta(key);
            OpBinding binding = meta == null ? null : ops.GetBinding(key);
            if (meta == null)
            {
                if (ops.CatalogMissing && !OpMeta.IsInternalKey(key))
                {
                    throw new OpException(ErrorCodes.AddinOutdated,
                        "This add-in build has no tool catalog (CATALOG_MISSING), so it cannot serve '" + key + "'.",
                        new Dictionary<string, object> { ["reason"] = ErrorCodes.CatalogMissing, ["addinVersion"] = ctx.Instance?.AddinVersion });
                }
                List<string> closest = ops.Closest(key);
                throw new OpException(ErrorCodes.UnknownOp, "Unknown op '" + key + "'.",
                    new Dictionary<string, object> { ["ops"] = closest, ["closest"] = closest.FirstOrDefault() });
            }
            ctx.Meta = meta;
            if (!meta.IsAddinBound)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + key + "' is served by the broker" + (meta.Impl == "control" ? " through the control pipe" : string.Empty) + ".",
                    new Dictionary<string, object> { ["impl"] = meta.Impl });
            }
            if (binding == null)
            {
                if (meta.Min > year) throw UnsupportedVersion(meta, year);
                throw new OpException(ErrorCodes.AddinOutdated, "This add-in build has no handler for '" + key + "' yet.",
                    new Dictionary<string, object> { ["key"] = key, ["addinVersion"] = ctx.Instance?.AddinVersion });
            }

            // 3. kind check (catalog skew between broker and add-in)
            if (!string.IsNullOrWhiteSpace(ctx.Request.Kind) && !string.Equals(ctx.Request.Kind, meta.Kind, StringComparison.Ordinal))
            {
                throw new OpException(ErrorCodes.AddinOutdated,
                    "The broker sent '" + key + "' as kind " + ctx.Request.Kind + " but this add-in's catalog says " + meta.Kind + ".",
                    new Dictionary<string, object> { ["expectedKind"] = meta.Kind, ["gotKind"] = ctx.Request.Kind, ["catalogHash"] = ops.CatalogHash });
            }

            // 4. minimum Revit year
            if (meta.Min > year) throw UnsupportedVersion(meta, year);

            // 5. document resolution per scope
            ResolveDocument(ctx, meta);

            // 6. UI-active document
            if (meta.Ui && ctx.Doc != null && !ctx.DocIsActive)
            {
                throw new OpException(ErrorCodes.NeedsActiveDoc,
                    "'" + key + "' needs '" + SafeTitle(ctx.Doc) + "' to be the active document in Revit.",
                    new Dictionary<string, object> { ["doc"] = SafeTitle(ctx.Doc), ["rid"] = ctx.Rid });
            }

            // 7. in-process callers reach inproc keys only
            if (ctx.IsInProcess && !meta.Inproc)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + key + "' is not available through the in-process bridge.",
                    new Dictionary<string, object> { ["reason"] = "inproc" });
            }

            // 8. invoke
            if (binding.UsesChangeContext) return ChangeEngine.RunSingle(ctx, binding);
            return binding.Invoke(ctx) ?? OpResult.Success();
        }

        private static OpException UnsupportedVersion(OpMeta meta, int year)
        {
            return new OpException(ErrorCodes.UnsupportedVersion,
                "'" + meta.Key + "' needs Revit " + meta.Min.ToString(CultureInfo.InvariantCulture) + " or newer (this is Revit " + year.ToString(CultureInfo.InvariantCulture) + ").",
                new Dictionary<string, object> { ["min"] = meta.Min, ["year"] = year });
        }

        /// <summary>Resolves ctx.Doc from request.doc {rid,key} (falling back to the active document) and applies the scope rules.</summary>
        internal static void ResolveDocument(RequestContext ctx, OpMeta meta)
        {
            string scope = meta.Scope ?? "none";
            DocRef docRef = ctx.Request.Doc;
            Document doc = null;
            if (scope == "none")
            {
                if (docRef != null) doc = TryResolveRef(ctx, docRef, out _);
                Attach(ctx, doc);
                return;
            }

            if (docRef != null && (docRef.Rid > 0 || !string.IsNullOrWhiteSpace(docRef.Key)))
            {
                doc = TryResolveRef(ctx, docRef, out OpException problem);
                if (doc == null) throw problem;
            }
            else
            {
                doc = ActiveOrOnlyDocument(ctx);
            }

            CheckScope(doc, meta, null);
            if (meta.Kind == RequestKinds.Write && doc.IsReadOnly)
            {
                throw new OpException(ErrorCodes.DocReadOnly, "'" + SafeTitle(doc) + "' is open read-only.",
                    new Dictionary<string, object> { ["doc"] = SafeTitle(doc), ["editable"] = false });
            }
            Attach(ctx, doc);
        }

        /// <summary>Scope rules: project ops refuse family docs and vice versa.</summary>
        internal static void CheckScope(Document doc, OpMeta meta, string label)
        {
            if (doc == null) return;
            string prefix = string.IsNullOrEmpty(label) ? string.Empty : label + ": ";
            bool family = doc.IsFamilyDocument;
            switch (meta.Scope)
            {
                case "project":
                    if (family)
                    {
                        throw new OpException(ErrorCodes.ProjectDocRequired,
                            prefix + "'" + meta.Key + "' works on project documents; '" + SafeTitle(doc) + "' is a family.",
                            new Dictionary<string, object> { ["doc"] = SafeTitle(doc), ["options"] = OpenDocs(false) });
                    }
                    break;
                case "family":
                    if (!family)
                    {
                        throw new OpException(ErrorCodes.FamilyDocRequired,
                            prefix + "'" + meta.Key + "' works on family documents; '" + SafeTitle(doc) + "' is a project.",
                            new Dictionary<string, object> { ["doc"] = SafeTitle(doc), ["options"] = OpenDocs(true) });
                    }
                    break;
            }
        }

        private static Document TryResolveRef(RequestContext ctx, DocRef docRef, out OpException problem)
        {
            problem = null;
            DocumentRegistry registry = ctx.Registry;
            if (registry == null || ctx.RevitApp == null)
            {
                problem = new OpException(ErrorCodes.RevitStarting, "The add-in is still starting.");
                return null;
            }

            if (docRef.Rid > 0)
            {
                Document byRid = registry.FindDocument(ctx.RevitApp, docRef.Rid);
                if (byRid != null)
                {
                    if (string.IsNullOrWhiteSpace(docRef.Key) || registry.KeyMatches(docRef.Rid, docRef.Key)) return byRid;
                    registry.GetKey(byRid);
                    if (registry.KeyMatches(docRef.Rid, docRef.Key)) return byRid;
                    problem = Stale(ctx, docRef, registry.GetKey(byRid));
                    return null;
                }
            }

            if (!string.IsNullOrWhiteSpace(docRef.Key))
            {
                List<Document> matches = registry.FindByKey(ctx.RevitApp, docRef.Key);
                if (matches.Count == 1) return matches[0];
                if (matches.Count > 1)
                {
                    problem = new OpException(ErrorCodes.TargetAmbiguous, "Several open documents match '" + docRef.Key + "'.",
                        new Dictionary<string, object> { ["options"] = matches.Select(d => (object)Describe(registry, d)).ToList() });
                    return null;
                }
            }

            problem = docRef.Rid > 0 ? Stale(ctx, docRef, null) : new OpException(ErrorCodes.DocNotOpen,
                "The document '" + docRef.Key + "' is not open in this Revit.",
                new Dictionary<string, object> { ["key"] = docRef.Key });
            return null;
        }

        private static OpException Stale(RequestContext ctx, DocRef docRef, string currentKey)
        {
            var details = new Dictionary<string, object>
            {
                ["rid"] = docRef.Rid,
                ["key"] = docRef.Key,
                ["currentKey"] = currentKey,
                ["docs"] = (ctx.Registry?.Current.Docs ?? new List<SnapshotDoc>())
                    .Select(d => (object)new Dictionary<string, object> { ["rid"] = d.Rid, ["key"] = d.Key, ["title"] = d.Title }).ToList()
            };
            return new OpException(ErrorCodes.TargetStale,
                currentKey == null
                    ? "Document #" + docRef.Rid.ToString(CultureInfo.InvariantCulture) + " is no longer open in this Revit."
                    : "Document #" + docRef.Rid.ToString(CultureInfo.InvariantCulture) + " is now '" + currentKey + "', not '" + docRef.Key + "'.",
                details);
        }

        private static Document ActiveOrOnlyDocument(RequestContext ctx)
        {
            Document active = null;
            try
            {
                active = ctx.App?.ActiveUIDocument?.Document;
            }
            catch
            {
                active = null;
            }
            if (active != null && !active.IsLinked) return active;

            var open = new List<Document>();
            if (ctx.RevitApp != null)
            {
                foreach (Document document in ctx.RevitApp.Documents)
                {
                    if (document != null && !document.IsLinked) open.Add(document);
                }
            }
            if (open.Count == 1) return open[0];
            if (open.Count == 0)
            {
                throw new OpException(ErrorCodes.NoOpenDocument, "Revit " + ctx.Year.ToString(CultureInfo.InvariantCulture) + " has no open document.",
                    new Dictionary<string, object> { ["year"] = ctx.Year });
            }
            throw new OpException(ErrorCodes.TargetAmbiguous, "Several documents are open and the request named none.",
                new Dictionary<string, object> { ["options"] = open.Select(d => (object)Describe(ctx.Registry, d)).ToList() });
        }

        private static void Attach(RequestContext ctx, Document doc)
        {
            ctx.Doc = doc;
            if (doc == null) return;
            DocumentRegistry registry = ctx.Registry;
            ctx.Rid = registry?.GetRid(doc) ?? 0;
            Document active = null;
            try { active = ctx.App?.ActiveUIDocument?.Document; } catch { }
            ctx.DocIsActive = active != null && registry != null && registry.TryGetRid(active, out long activeRid) && activeRid == ctx.Rid;
            try
            {
                ctx.UiDoc = ctx.DocIsActive ? ctx.App.ActiveUIDocument : new UIDocument(doc);
            }
            catch
            {
                ctx.UiDoc = null;
            }
        }

        /// <summary>Maps exceptions to cataloged errors (D3 B13).</summary>
        public static OpResult MapException(RequestContext ctx, Exception ex)
        {
            switch (ex)
            {
                case OpException op:
                    return OpResult.From(op);
                case OperationCanceledException _:
                    return OpResult.Fail(ErrorCodes.RequestCancelled, "The request was cancelled.");
                case Autodesk.Revit.Exceptions.OperationCanceledException _:
                    return OpResult.Fail(ErrorCodes.RequestCancelled, "Revit cancelled the operation.");
                case Autodesk.Revit.Exceptions.ApplicationException revit:
                    return OpResult.Fail(ErrorCodes.RevitRefused, revit.Message,
                        new Dictionary<string, object> { ["apiMessage"] = revit.Message, ["exception"] = revit.GetType().Name });
                case ArgumentException argument:
                    return OpResult.Fail(ErrorCodes.InvalidArgs, argument.Message,
                        new Dictionary<string, object> { ["param"] = argument.ParamName, ["reason"] = argument.Message });
                case InvalidOperationException invalid:
                    return OpResult.Fail(ErrorCodes.RevitRefused, invalid.Message,
                        new Dictionary<string, object> { ["apiMessage"] = invalid.Message, ["exception"] = invalid.GetType().Name });
                default:
                    DiagnosticsLogger.Error("dispatch", "Unexpected failure in " + (ctx?.Key ?? "(unknown op)") + ". requestId=" + (ctx?.RequestId ?? "?"), ex);
                    return OpResult.Fail(ErrorCodes.InternalError, ex.GetType().Name + ": " + ex.Message,
                        new Dictionary<string, object> { ["requestId"] = ctx?.RequestId, ["logPath"] = DiagnosticsLogger.CurrentLogFile });
            }
        }

        /// <summary>Wire response: result + context warnings/notices + the doc block + metrics.</summary>
        public static BridgeResponse BuildResponse(RequestContext ctx, OpResult result, BridgeRequest fallbackRequest = null)
        {
            result = result ?? OpResult.Success();
            BridgeRequest request = ctx?.Request ?? fallbackRequest;
            QueuedRevitWorkItem item = ctx?.WorkItem;
            var metrics = new ResponseMetrics
            {
                QueueWaitMs = item?.QueueWaitMs ?? 0,
                RaiseToExecMs = item?.RaiseToExecMs ?? 0,
                ExecMs = ctx?.Stopwatch.ElapsedMilliseconds ?? 0,
                Via = ctx?.Via ?? "externalEvent"
            };

            if (ctx != null)
            {
                foreach (BridgeWarning warning in ctx.Warnings) OpResult.AddWarning(result.Warnings, warning.Code, warning.Text, warning.Ids, warning.N);
                foreach (CapturedFailure failure in ctx.RevitWarnings)
                {
                    OpResult.AddWarning(result.Warnings, WarningCodes.RevitWarning, failure.Text, failure.FailingIds.Concat(failure.AdditionalIds), 1);
                }
                foreach (BridgeNotice notice in ctx.Notices) OpResult.AddNotice(result.Notices, notice.Code, notice.Text);
                if (result.Doc == null && ctx.Doc != null)
                {
                    try
                    {
                        if (ctx.Doc.IsValidObject) result.WithDoc(ctx.Registry?.Describe(ctx.Doc));
                    }
                    catch
                    {
                        // The document closed during the op.
                    }
                }
            }

            BridgeResponse response = result.ToResponse(request?.RequestId, metrics);
            if (response.Page?.Ids != null && request != null && !request.WantIds && !(ctx?.WantIds ?? false)) response.Page.Ids = null;
            return response;
        }

        /// <summary>Completes the item when the job finishes; at the request deadline answers with the running job instead.</summary>
        private static void AttachJobToItem(QueuedRevitWorkItem item, RequestContext ctx, string jobId)
        {
            JobRunner jobs = McpRuntime.Jobs;
            JobRecord job = jobs?.Find(jobId);
            if (job == null)
            {
                item.TrySetResult(BuildResponse(ctx, OpResult.Fail(ErrorCodes.InternalError, "The job " + jobId + " was not created.")));
                return;
            }
            job.Completion.Task.ContinueWith(task =>
            {
                if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && task.Result != null) item.TrySetResult(task.Result);
            }, System.Threading.Tasks.TaskScheduler.Default);

            int waitMs = (int)Math.Max(250, ctx.Request.TimeoutMs - item.AgeMs - RequestContext.ReadDeadlineMarginMs);
            System.Threading.Tasks.Task.Delay(waitMs).ContinueWith(_ =>
            {
                if (item.IsCompleted) return;
                BridgeResponse running = BridgeResponse.Success(item.RequestId, null, "running as a job");
                running.Job = job.ToJobInfo();
                running.Metrics = new ResponseMetrics { QueueWaitMs = item.QueueWaitMs, RaiseToExecMs = item.RaiseToExecMs, Via = "job" };
                running.Doc = job.Context?.Doc == null ? null : SafeDescribe(job.Context);
                item.TrySetResult(running);
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private static ResponseDoc SafeDescribe(RequestContext ctx)
        {
            SnapshotDoc doc = ctx.Registry?.GetDoc(ctx.Rid);
            if (doc == null) return null;
            return new ResponseDoc { Rid = doc.Rid, Key = doc.Key, Title = doc.Title, Year = ctx.Year, Kind = doc.Kind, Generation = doc.Generation, Modified = doc.Modified };
        }

        /// <summary>Open documents of one kind as {rid, title, kind} options.</summary>
        private static List<object> OpenDocs(bool family)
        {
            DocumentRegistry registry = McpRuntime.Registry;
            if (registry == null) return new List<object>();
            return registry.Current.Docs
                .Where(doc => (doc.Kind == "family") == family)
                .Select(doc => (object)new Dictionary<string, object> { ["rid"] = doc.Rid, ["title"] = doc.Title, ["kind"] = doc.Kind })
                .ToList();
        }

        private static Dictionary<string, object> Describe(DocumentRegistry registry, Document doc)
        {
            return new Dictionary<string, object>
            {
                ["rid"] = registry?.GetRid(doc) ?? 0,
                ["title"] = SafeTitle(doc),
                ["kind"] = doc.IsFamilyDocument ? "family" : "project"
            };
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc?.Title ?? string.Empty; } catch { return string.Empty; }
        }
    }
}
