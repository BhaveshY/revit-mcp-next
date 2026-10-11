using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Addin.Revit;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// model_delivery.inspect | preview | execute | fixture (SPEC Appendix A/B; recipes are stored by the broker).
    /// Adapts the delivery engine (ModelDeliveryWorkflow: isolated source copies, link repath/unload/remove, cleanup,
    /// exports, QA, atomic publish) to the v3 registry. All four are jobs (job:always). preview returns previewId and
    /// planHash, which the broker keeps behind the confirm token of execute; execute advances the engine one phase
    /// step per Execute pass, so other requests interleave and job_status reports progress. The delivery binds to this
    /// Revit instance only (scope none): no project needs to be open, and production sources must be closed.
    /// </summary>
    internal static class ModelDeliveryOps
    {
        private static readonly object Gate = new object();
        private static ModelDeliveryWorkflow _workflow;

        private static ModelDeliveryWorkflow Workflow
        {
            get
            {
                lock (Gate)
                {
                    return _workflow ?? (_workflow = new ModelDeliveryWorkflow(InstanceId, document => McpRuntime.Registry?.GetGeneration(document) ?? 0));
                }
            }
        }

        private static string InstanceId => McpRuntime.Instance?.InstanceId ?? "in-process";

        /// <summary>The delivery is bound to this Revit instance only (an empty document fingerprint, see VerifyTargetAvailable).</summary>
        private static DeliveryTargetBinding Binding() => new DeliveryTargetBinding(InstanceId, string.Empty, 0, string.Empty, string.Empty);

        private static string Session(RequestContext ctx) => ctx.ClientKey ?? string.Empty;

        [Op("model_delivery.inspect")]
        public static OpResult Inspect(RequestContext ctx)
        {
            List<string> sources = ctx.Args.Strs("sources");
            if (sources.Count == 0)
            {
                throw OpException.InvalidArgs("sources", "is required: the source .rvt paths to inspect",
                    new Dictionary<string, object> { ["op"] = "inspect", ["sources"] = new List<object> { "C:\\Projects\\A.rvt" } });
            }
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sourcePaths"] = sources.Cast<object>().ToList()
            };
            IDictionary<string, object> previous = ctx.Args.Dict("recipe");
            if (previous != null) payload["previousRecipe"] = Plain(previous);
            ctx.Progress("inspecting " + sources.Count.ToString(CultureInfo.InvariantCulture) + " source model(s)");
            Dictionary<string, object> data = Guard(() => DocFiles.Native(ctx, "opening", null, () => Workflow.Inspect(ctx.App, Binding(), payload)));
            int missing = Count(data, "missingDecisions");
            int changes = Count(data, "detectedChanges");
            string mode = Convert.ToString(data.TryGetValue("mode", out object m) ? m : null, CultureInfo.InvariantCulture);
            string summary = "inspected " + sources.Count.ToString(CultureInfo.InvariantCulture) + " source model(s) (" + (mode == "repeat" ? "saved recipe found" : "first delivery") + ")" +
                             (missing > 0 ? ", " + missing.ToString(CultureInfo.InvariantCulture) + " decision(s) needed" : string.Empty) +
                             (changes > 0 ? ", " + changes.ToString(CultureInfo.InvariantCulture) + " change(s) since the saved recipe" : string.Empty);
            return OpResult.Success(data, summary);
        }

        [Op("model_delivery.preview")]
        public static OpResult Preview(RequestContext ctx)
        {
            IDictionary<string, object> recipe = ctx.Args.Dict("recipe");
            if (recipe == null) throw OpException.InvalidArgs("recipe", "is required (shape: help {\"topic\":\"recipe\"})");
            var payload = new Dictionary<string, object>(StringComparer.Ordinal) { ["recipe"] = Plain(recipe) };
            ctx.Progress("checking the delivery plan");
            Dictionary<string, object> data = Guard(() => DocFiles.Native(ctx, "opening", null, () => Workflow.Preview(ctx.App, Session(ctx), Binding(), payload)));
            bool ready = data.TryGetValue("ready", out object r) && r is bool b && b;
            int blockers = Count(data, "blockers");
            int models = Count(data, "models");
            string summary = ready
                ? "delivery plan ready: " + models.ToString(CultureInfo.InvariantCulture) + " model(s) to " + Convert.ToString(data.TryGetValue("packagePath", out object p) ? p : null, CultureInfo.InvariantCulture)
                : "delivery plan blocked by " + blockers.ToString(CultureInfo.InvariantCulture) + " problem(s); fix the recipe or sources and preview again";
            return OpResult.Success(data, summary);
        }

        [Op("model_delivery.execute")]
        public static OpResult Execute(RequestContext ctx)
        {
            if (ctx.Confirmed == null)
            {
                throw new OpException(ErrorCodes.ConfirmExpired, "execute runs only a confirmed delivery preview; run preview, then execute with its confirm token.",
                    new Dictionary<string, object> { ["reason"] = "not confirmed" });
            }
            IDictionary<string, object> recipe = ctx.Args.Dict("recipe");
            string previewId = ctx.Args.Str("preview_id") ?? ctx.Args.Str("previewId");
            string planHash = ctx.Args.Str("plan_hash") ?? ctx.Args.Str("planHash");
            if (recipe == null || string.IsNullOrWhiteSpace(previewId) || string.IsNullOrWhiteSpace(planHash))
            {
                throw new OpException(ErrorCodes.ConfirmExpired, "The confirm token does not carry a delivery preview; run preview again.",
                    new Dictionary<string, object> { ["reason"] = "no preview" });
            }
            if (ctx.IsPreview) return DocFiles.StaticPlan("run the approved delivery " + previewId, new Dictionary<string, object> { ["previewId"] = previewId });

            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["confirm"] = true,
                ["previewId"] = previewId,
                ["planHash"] = planHash,
                ["recipe"] = Plain(recipe)
            };
            string session = Session(ctx);
            Dictionary<string, object> started = Guard(() => Workflow.Execute(ctx.App, session, Binding(), payload));
            string jobId = Convert.ToString(started["jobId"], CultureInfo.InvariantCulture);
            var status = new Dictionary<string, object>(StringComparer.Ordinal) { ["jobId"] = jobId };
            bool cancelSent = false;

            Func<JobStep, StepResult> step = s =>
            {
                if (s.CancelRequested && !cancelSent)
                {
                    cancelSent = true;
                    Guard(() => Workflow.Cancel(session, new Dictionary<string, object>(status) { ["reason"] = "cancelled with cancel_job" }));
                }
                DocFiles.Native(s.Context, "exporting", null, () => Workflow.ProcessNext(s.App));
                Dictionary<string, object> current = Guard(() => Workflow.GetStatus(session, status));
                int done = Int(current, "completedUnits");
                int total = Int(current, "totalUnits");
                string message = Convert.ToString(current.TryGetValue("message", out object msg) ? msg : null, CultureInfo.InvariantCulture);
                if (!(current.TryGetValue("terminal", out object terminal) && terminal is bool t && t))
                {
                    s.Progress(message, done, total);
                    return StepResult.Repeat(0, message);
                }
                return StepResult.Finish(Terminal(current));
            };
            return ctx.Jobs.Start(ctx, new[] { step }, "delivery queued", Int(started, "totalUnits"));
        }

        private static OpResult Terminal(Dictionary<string, object> status)
        {
            string state = Convert.ToString(status.TryGetValue("state", out object s) ? s : null, CultureInfo.InvariantCulture);
            string package = Convert.ToString(status.TryGetValue("packagePath", out object p) ? p : null, CultureInfo.InvariantCulture);
            switch (state)
            {
                case "succeeded":
                    return OpResult.Success(status, "delivery published to " + package);
                case "cancelled":
                    return OpResult.Fail(ErrorCodes.RequestCancelled, "The delivery was cancelled; nothing was published and staging was cleaned up.", status);
                default:
                    string first = FirstError(status);
                    return OpResult.Fail(ErrorCodes.RevitRefused, "The delivery failed" + (first != null ? ": " + first : string.Empty) + "; nothing was published.", status);
            }
        }

        [Op("model_delivery.fixture")]
        public static OpResult Fixture(RequestContext ctx)
        {
            string template = DocFiles.FullPath(ctx.Args.ReqStr("template"), "template");
            DocFiles.RequireExtension(template, "template", new Dictionary<string, object> { ["op"] = "fixture", ["template"] = Path.ChangeExtension(template, ".rte") }, ".rte");
            if (!File.Exists(template)) throw OpException.NotFound("template", "template", template, DocFiles.Siblings(template, "*.rte"));
            string folder = DocFiles.FullPath(ctx.Args.ReqStr("folder"), "folder");
            string id = ctx.Args.ReqStr("id");
            if (Directory.Exists(folder))
            {
                throw new OpException(ErrorCodes.NameTaken, "The fixture folder " + folder + " already exists; the fixture needs a new, empty folder.",
                    new Dictionary<string, object> { ["param"] = "folder", ["value"] = folder, ["next"] = folder.TrimEnd('\\', '/') + "-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) });
            }
            if (ctx.IsPreview)
            {
                return DocFiles.StaticPlan("create two linked central test models and a recipe in " + folder,
                    new Dictionary<string, object> { ["template"] = template, ["folder"] = folder, ["id"] = id });
            }
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["confirm"] = true,
                ["templatePath"] = template,
                ["fixtureRoot"] = folder,
                ["fixtureId"] = id
            };
            ctx.Progress("creating fixture models");
            Dictionary<string, object> data = Guard(() => DocFiles.Native(ctx, "saving", null, () => ModelDeliveryFixture.Create(ctx.App, payload)));
            return OpResult.Success(data, "created delivery fixture '" + id + "' in " + folder + " (2 central models and a recipe)");
        }

        // ---------------------------------------------------------------------------------------------- helpers

        /// <summary>Runs engine code and maps its codes to catalog errors (details.reason keeps the engine code).</summary>
        private static T Guard<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (ModelDeliveryException ex)
            {
                throw Map(ex);
            }
        }

        private static OpException Map(ModelDeliveryException ex)
        {
            var details = new Dictionary<string, object> { ["reason"] = ex.Code };
            string code;
            switch (ex.Code)
            {
                case "DELIVERY_PREVIEW_NOT_FOUND":
                case "CONFIRMATION_REQUIRED":
                    code = ErrorCodes.ConfirmExpired;
                    break;
                case "DELIVERY_PREVIEW_MISMATCH":
                case "DELIVERY_PLAN_HASH_MISMATCH":
                case "DELIVERY_SOURCE_CHANGED":
                case "DELIVERY_TARGET_CHANGED":
                case "DELIVERY_TARGET_INSTANCE_CHANGED":
                    code = ErrorCodes.ConfirmStale;
                    break;
                case "DELIVERY_PREVIEW_NOT_READY":
                    code = ErrorCodes.InvalidArgs;
                    details["param"] = "recipe";
                    break;
                case "DELIVERY_JOB_ACTIVE":
                    code = ErrorCodes.RevitBusy;
                    break;
                case "SOURCE_NOT_FOUND":
                case "TEMPLATE_NOT_FOUND":
                case "DELIVERY_JOB_NOT_FOUND":
                    code = ErrorCodes.NotFound;
                    details["param"] = ex.Code == "TEMPLATE_NOT_FOUND" ? "template" : "sources";
                    details["kind"] = "file";
                    details["value"] = ex.Message;
                    details["candidates"] = new List<object>();
                    break;
                case "SOURCE_DOCUMENT_OPEN":
                    code = ErrorCodes.RevitRefused;
                    break;
                default:
                    if (ex.Code.EndsWith("_REQUIRED", StringComparison.Ordinal) || ex.Code.StartsWith("INVALID_", StringComparison.Ordinal) ||
                        ex.Code.StartsWith("DUPLICATE_", StringComparison.Ordinal) || ex.Code.EndsWith("_UNKNOWN", StringComparison.Ordinal) ||
                        ex.Code.EndsWith("_LIMIT", StringComparison.Ordinal))
                    {
                        code = ErrorCodes.InvalidArgs;
                        details["param"] = "recipe";
                    }
                    else
                    {
                        code = ErrorCodes.RevitRefused;
                        details["apiMessage"] = ex.Message;
                    }
                    break;
            }
            return new OpException(code, ex.Message, details);
        }

        /// <summary>A deep copy as Dictionary&lt;string, object&gt; / List&lt;object&gt; (what the engine's parsers expect).</summary>
        private static object Plain(object value)
        {
            if (value == null) return null;
            if (value is string) return value;
            IDictionary<string, object> map = Wire.AsMap(value);
            if (map != null)
            {
                var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in map) copy[pair.Key] = Plain(pair.Value);
                return copy;
            }
            if (value is IEnumerable list && !(value is IDictionary))
            {
                var items = new List<object>();
                foreach (object item in list) items.Add(Plain(item));
                return items;
            }
            return value;
        }

        private static Dictionary<string, object> Plain(IDictionary<string, object> map) => (Dictionary<string, object>)Plain((object)map);

        private static int Count(Dictionary<string, object> data, string key)
        {
            return data != null && data.TryGetValue(key, out object value) && value is ICollection collection ? collection.Count : 0;
        }

        private static int Int(Dictionary<string, object> data, string key)
        {
            try { return data != null && data.TryGetValue(key, out object value) && value != null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0; } catch { return 0; }
        }

        private static string FirstError(Dictionary<string, object> status)
        {
            if (!status.TryGetValue("errors", out object errors) || !(errors is IEnumerable list)) return null;
            foreach (object item in list)
            {
                if (item is IDictionary<string, object> error && error.TryGetValue("message", out object message)) return Convert.ToString(message, CultureInfo.InvariantCulture);
            }
            return null;
        }
    }
}
