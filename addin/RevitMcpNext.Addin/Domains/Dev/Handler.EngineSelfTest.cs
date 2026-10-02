using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// dev.engine_selftest — the reference pattern for lane handlers and a live check of the write engine.
    ///
    /// Write ops are <c>static void X(ChangeContext c)</c> bound with [Op("tool.op")]; catalog keys get their metadata
    /// (kind, scope, tx, blast) from the catalog, internal dev.* keys declare it on the attribute as below. Handlers
    /// read args through c (mm in → feet, names → elements), change the model inside the transaction the engine opened,
    /// record what they did (c.Created / c.Modified / c.Delete), publish outputs for $refs (c.Output) and a summary.
    /// The engine evaluates the blast rules, rolls back and returns needsConfirm when one triggers, runs previews in a
    /// TempScope, and checks per-element stamps on confirmed applies.
    ///
    /// The self-test runs, on a disposable document only (unsaved, or saved under &lt;home&gt;\runs or %TEMP%):
    /// preview of 25 reference planes (nothing persists) → apply → delete without confirm (NOT APPLIED, rule delete) →
    /// touch one plane, then a confirmed apply of the old plan (CONFIRM_STALE) → new plan + confirmed apply (deleted).
    /// </summary>
    internal static class EngineSelfTestOps
    {
        private const int PlaneCount = 25;

        [Op("dev.selftest_create_planes", Kind = "write", Scope = "project", Tx = "in", Blast = "create", Cs = true)]
        public static void CreatePlanes(ChangeContext c)
        {
            int count = c.Int("count", PlaneCount);
            string prefix = c.Str("prefix", "MCP selftest");
            double spacing = c.Mm("spacing", 1000);
            View view = PlanView(c.Doc) ?? throw c.Error(ErrorCodes.NotFound, "The document has no plan view to host reference planes.");
            for (int index = 0; index < count; index++)
            {
                double x = index * spacing;
                ReferencePlane plane = c.Doc.Create.NewReferencePlane(new XYZ(x, 0, 0), new XYZ(x, Units.MmToFt(5000), 0), XYZ.BasisZ, view);
                plane.Name = prefix + " " + (index + 1).ToString(CultureInfo.InvariantCulture);
                c.Created(plane);
                if (index == 0) c.Output("first", plane.Id);
            }
            c.Summary("created " + count.ToString(CultureInfo.InvariantCulture) + " reference planes '" + prefix + " n'");
        }

        [Op("dev.selftest_delete_planes", Kind = "write", Scope = "project", Tx = "in", Blast = "delete", Cs = true)]
        public static void DeletePlanes(ChangeContext c)
        {
            IList<ElementId> ids = c.Ids("ids");
            ICollection<ElementId> deleted = c.Delete(ids);
            c.Summary("deleted " + deleted.Count.ToString(CultureInfo.InvariantCulture) + " element(s)");
        }

        [Op("dev.selftest_touch", Kind = "write", Scope = "project", Tx = "in", Cs = true)]
        public static void Touch(ChangeContext c)
        {
            foreach (Element element in c.Elements("ids"))
            {
                string before = element.Name;
                element.Name = before + " (touched)";
                c.Modified(element, "name", before);
            }
            c.Summary("renamed " + c.ModifiedIds.Count.ToString(CultureInfo.InvariantCulture) + " element(s)");
        }

        [Op("dev.engine_selftest", Kind = "write", Scope = "project", Tx = "lifecycle")]
        public static OpResult SelfTest(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            if (!IsDisposable(ctx, doc))
            {
                throw OpException.InvalidArgs("doc", "dev.engine_selftest runs only on disposable documents (unsaved, or saved under " +
                    ctx.Home.RunsDir + " or %TEMP%); '" + doc.Title + "' is " + doc.PathName);
            }

            string prefix = "MCP selftest " + Guid.NewGuid().ToString("N").Substring(0, 6);
            var steps = new List<object>();
            bool passed = true;
            void Check(string name, bool ok, string detail)
            {
                steps.Add(new List<object> { name, ok, detail });
                passed &= ok;
            }

            var createArgs = new Dictionary<string, object> { ["count"] = PlaneCount, ["prefix"] = prefix };

            // 1. preview: a dry run reports the plan and changes nothing.
            BridgeResponse preview = Run(ctx, "dev.selftest_create_planes", createArgs, RequestModes.Preview, null);
            Check("preview", preview.Ok && preview.NeedsConfirm != null && preview.NeedsConfirm.Rule == null && preview.NeedsConfirm.Blast.CreateTotal == PlaneCount && CountPlanes(doc, prefix) == 0,
                Describe(preview) + "; planes after preview: " + CountPlanes(doc, prefix).ToString(CultureInfo.InvariantCulture));

            // 2. apply: 25 creations stay below confirm.createOver, so the call applies at once.
            BridgeResponse created = Run(ctx, "dev.selftest_create_planes", createArgs, RequestModes.Apply, null);
            List<long> ids = created.Changes?.Created ?? new List<long>();
            Check("apply", created.Ok && created.NeedsConfirm == null && created.Changes?.CreatedTotal == PlaneCount && CountPlanes(doc, prefix) == PlaneCount,
                Describe(created));
            if (!created.Ok || ids.Count == 0) return Finish(ctx, passed, steps, prefix);

            var deleteArgs = new Dictionary<string, object> { ["ids"] = ids.Cast<object>().ToList() };
            int deleteOver = ctx.Settings.Confirm.DeleteOver;
            bool ruleExpected = PlaneCount > deleteOver;

            // 3. delete without confirm: rule delete triggers (25 > deleteOver), the group is rolled back.
            BridgeResponse plan = Run(ctx, "dev.selftest_delete_planes", deleteArgs, RequestModes.Apply, null);
            if (ruleExpected)
            {
                Check("needs_confirm", plan.Ok && plan.NeedsConfirm?.Rule == "delete" && plan.NeedsConfirm.DeleteSet.Count == PlaneCount && CountPlanes(doc, prefix) == PlaneCount,
                    Describe(plan) + "; deleteSet " + (plan.NeedsConfirm?.DeleteSet.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                Check("needs_confirm", true, "skipped: settings confirm.deleteOver=" + deleteOver.ToString(CultureInfo.InvariantCulture) + " allows deleting 25 without confirmation");
                return Finish(ctx, passed && plan.Ok, steps, prefix);
            }
            if (plan.NeedsConfirm == null) return Finish(ctx, false, steps, prefix);

            // 4. stale plan: touching one planned element after the plan makes the confirmed apply fail.
            BridgeResponse touched = Run(ctx, "dev.selftest_touch", new Dictionary<string, object> { ["ids"] = new List<object> { ids[0] } }, RequestModes.Apply, null);
            BridgeResponse stale = Run(ctx, "dev.selftest_delete_planes", deleteArgs, RequestModes.Apply,
                new ConfirmedPlan { Stamp = plan.NeedsConfirm.Stamp, DeleteSet = plan.NeedsConfirm.DeleteSet });
            Check("confirm_stale", touched.Ok && !stale.Ok && stale.Code == ErrorCodes.ConfirmStale && CountPlanes(doc, prefix) == PlaneCount,
                "touch " + Describe(touched) + "; confirmed apply " + Describe(stale));

            // 5. a fresh plan applies with confirmation; stamps advanced.
            BridgeResponse replan = Run(ctx, "dev.selftest_delete_planes", deleteArgs, RequestModes.Apply, null);
            BridgeResponse applied = replan.NeedsConfirm == null
                ? replan
                : Run(ctx, "dev.selftest_delete_planes", deleteArgs, RequestModes.Apply,
                    new ConfirmedPlan { Stamp = replan.NeedsConfirm.Stamp, DeleteSet = replan.NeedsConfirm.DeleteSet });
            Check("confirmed_apply",
                replan.NeedsConfirm != null && replan.NeedsConfirm.Stamp > plan.NeedsConfirm.Stamp &&
                applied.Ok && applied.NeedsConfirm == null && (applied.Changes?.DeletedTotal ?? 0) >= PlaneCount && CountPlanes(doc, prefix) == 0,
                "stamps " + plan.NeedsConfirm.Stamp.ToString(CultureInfo.InvariantCulture) + " -> " +
                (replan.NeedsConfirm?.Stamp ?? 0).ToString(CultureInfo.InvariantCulture) + "; " + Describe(applied));

            return Finish(ctx, passed, steps, prefix);
        }

        /// <summary>Runs one synthetic request through the full dispatch pipeline (registry → engine).</summary>
        private static BridgeResponse Run(RequestContext outer, string key, Dictionary<string, object> args, string mode, ConfirmedPlan confirmed)
        {
            var request = new BridgeRequest
            {
                V = BridgeProtocol.Version,
                RequestId = outer.RequestId + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                ClientKey = outer.ClientKey,
                Op = key,
                Kind = RequestKinds.Write,
                Mode = mode,
                TimeoutMs = 30000,
                Doc = new DocRef { Rid = outer.Rid, Key = outer.Registry?.GetKey(outer.Doc) ?? string.Empty },
                WriteTag = outer.Request.WriteTag,
                Confirmed = confirmed,
                Args = args.ToDictionary(pair => pair.Key, pair => pair.Value)
            };
            var ctx = new RequestContext(request, outer.App, null, outer.Via);
            try
            {
                return Dispatcher.BuildResponse(ctx, Dispatcher.Run(ctx));
            }
            catch (Exception ex)
            {
                return Dispatcher.BuildResponse(ctx, Dispatcher.MapException(ctx, ex));
            }
        }

        private static OpResult Finish(RequestContext ctx, bool passed, List<object> steps, string prefix)
        {
            int failed = steps.Cast<List<object>>().Count(step => !(bool)step[1]);
            var data = new Dictionary<string, object>
            {
                ["passed"] = passed && failed == 0,
                ["steps"] = new Dictionary<string, object> { ["cols"] = new List<object> { "step", "passed", "detail" }, ["rows"] = steps },
                ["prefix"] = prefix,
                ["year"] = ctx.Year
            };
            if (passed && failed == 0)
            {
                return OpResult.Success(data, "engine self-test passed (" + steps.Count.ToString(CultureInfo.InvariantCulture) + " steps)");
            }
            return OpResult.Fail(ErrorCodes.InternalError,
                "engine self-test failed: " + failed.ToString(CultureInfo.InvariantCulture) + " of " + steps.Count.ToString(CultureInfo.InvariantCulture) + " steps",
                data);
        }

        private static string Describe(BridgeResponse response)
        {
            if (response == null) return "no response";
            if (!response.Ok) return response.Code + ": " + response.Message;
            if (response.NeedsConfirm != null) return "needsConfirm " + response.NeedsConfirm.Rule + " (stamp " + response.NeedsConfirm.Stamp.ToString(CultureInfo.InvariantCulture) + ")";
            return "ok: " + response.Summary;
        }

        private static int CountPlanes(Document doc, string prefix)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Count(plane => (plane.Name ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal));
        }

        private static View PlanView(Document doc)
        {
            View active = null;
            try { active = doc.ActiveView; } catch { }
            if (active is ViewPlan plan && !plan.IsTemplate) return plan;
            return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(view => !view.IsTemplate);
        }

        private static bool IsDisposable(RequestContext ctx, Document doc)
        {
            string path = doc.PathName;
            if (string.IsNullOrWhiteSpace(path)) return true;
            return IsUnder(path, ctx.Home?.RunsDir) || IsUnder(path, Path.GetTempPath());
        }

        private static bool IsUnder(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
