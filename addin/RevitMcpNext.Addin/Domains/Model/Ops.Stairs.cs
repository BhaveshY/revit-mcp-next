using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// create_elements stairs (tx own: StairsEditScope, V8/V8b) and railing (D1 §4B, D3 §3.8).
    /// Stairs: one straight run per segment of <c>points</c> with automatic landings at the corners; risers are spread
    /// over the runs by length, run lengths follow from the tread depth. Railings are added on the treads when Revit did
    /// not add any (V8 fallback).
    /// </summary>
    internal static class StairOps
    {
        [Op("create_elements.stairs")]
        public static void CreateStairs(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            Level top = c.Level("top");
            double height = ModelKit.Z(top) - ModelKit.Z(level);
            if (height <= Units.MmToFt(100)) throw OpException.InvalidArgs(c.Args.Field("top"), "top '" + top.Name + "' must be above '" + level.Name + "'");
            StairsType type = c.Args.Has("type") ? Resolve.Type<StairsType>(doc, c.Str("type"), c.Args.Field("type")) : null;
            double? width = c.Args.MmOpt("width");
            if (width.HasValue && width.Value <= Units.MmToFt(100)) throw OpException.InvalidArgs(c.Args.Field("width"), "must be at least 100 mm");
            int? risers = c.Args.IntOpt("risers");
            if (risers.HasValue && (risers.Value < 2 || risers.Value > 500)) throw OpException.InvalidArgs(c.Args.Field("risers"), "must be between 2 and 500");

            List<XYZ> path = Geometry.NormalizeClosedLoop(ModelKit.FlatPoints(c, "points", 0));
            if (path.Count < 2) throw OpException.InvalidArgs(c.Args.Field("points"), "needs at least 2 distinct points (the walk line)");
            int segments = path.Count - 1;
            if (segments > 8) throw OpException.InvalidArgs(c.Args.Field("points"), "supports at most 8 straight runs (9 points)");

            string name = ModelKit.TxName(c);
            ElementId stairsId;
            var runIds = new List<ElementId>();
            var landingIds = new List<ElementId>();
            int actualRisers;
            double riserHeight, treadDepth;

            using (var scope = new StairsEditScope(doc, name))
            {
                stairsId = scope.Start(level.Id, top.Id);
                using (var transaction = new Transaction(doc, name))
                {
                    FailureCapture.Configure(transaction, c.Failures);
                    if (transaction.Start() != TransactionStatus.Started) throw new OpException(ErrorCodes.RevitRefused, "Revit refused to start the stairs transaction.");
                    Stairs stairs = doc.GetElement(stairsId) as Stairs ?? throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the stairs element.");
                    if (type != null && stairs.GetTypeId() != type.Id) stairs.ChangeTypeId(type.Id);
                    if (risers.HasValue) stairs.DesiredRisersNumber = risers.Value;
                    actualRisers = Math.Max(2, stairs.DesiredRisersNumber);
                    riserHeight = height / actualRisers;
                    treadDepth = stairs.ActualTreadDepth > 1e-6 ? stairs.ActualTreadDepth : Units.MmToFt(280);
                    double runWidth = width ?? DefaultRunWidth(doc, stairs);

                    List<int> counts = SpreadRisers(path, actualRisers, runWidth);
                    StairsRun previous = null;
                    double z = stairs.BaseElevation;
                    for (int i = 0; i < segments; i++)
                    {
                        XYZ a = path[i], b = path[i + 1];
                        XYZ d = ModelKit.DirXY(a, b, c.Args.Field("points"));
                        XYZ runStart = i == 0 ? a : a + d * (runWidth / 2);
                        double length = Math.Max(1, counts[i] - 1) * treadDepth;
                        double available = a.DistanceTo(b) - (i == 0 ? 0 : runWidth / 2) - (i == segments - 1 ? 0 : runWidth / 2);
                        if (length > available + Units.MmToFt(1))
                        {
                            c.Warn(WarningCodes.ValueClamped, "segment " + i + " is " + ModelKit.Mm(available) + " mm long but its " + counts[i] + " risers need " + ModelKit.Mm(length) + " mm; the run extends past the path");
                        }
                        double runZ = previous == null ? z : previous.TopElevation;
                        Line location = Line.CreateBound(new XYZ(runStart.X, runStart.Y, runZ), new XYZ(runStart.X + d.X * length, runStart.Y + d.Y * length, runZ));
                        StairsRun run = StairsRun.CreateStraightRun(doc, stairsId, location, StairsRunJustification.Center);
                        if (run == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create stairs run " + i + ".");
                        if (width.HasValue) run.ActualRunWidth = width.Value;
                        runIds.Add(run.Id);
                        if (previous != null)
                        {
                            try
                            {
                                landingIds.AddRange(StairsLanding.CreateAutomaticLanding(doc, previous.Id, run.Id) ?? new List<ElementId>());
                            }
                            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                            {
                                c.Warn(WarningCodes.PartialResult, "no landing between runs " + (i - 1) + " and " + i + ": " + ex.Message);
                            }
                        }
                        previous = run;
                    }
                    TransactionStatus status = transaction.Commit();
                    if (status != TransactionStatus.Committed) throw RolledBack(c, "the stairs runs");
                }
                scope.Commit(c.Failures);
                if (c.Failures.RolledBack || !(doc.GetElement(stairsId) is Stairs)) throw RolledBack(c, "the stairs");
            }

            c.Created(stairsId);
            c.Created(runIds);
            c.Created(landingIds);

            // V8: API-created stairs usually have no railings; add them on the treads like the Stairs tool does.
            List<ElementId> railings = AddRailings(c, stairsId);
            c.Created(railings);

            c.Output("stairs", stairsId);
            ModelKit.Outputs(c, type, level);
            c.Data(new Dictionary<string, object>
            {
                ["stairs"] = stairsId.Value,
                ["runs"] = runIds.Select(id => (object)id.Value).ToList(),
                ["landings"] = landingIds.Select(id => (object)id.Value).ToList(),
                ["railings"] = railings.Select(id => (object)id.Value).ToList(),
                ["risers"] = actualRisers,
                ["riser_height"] = Units.Mm(riserHeight),
                ["tread_depth"] = Units.Mm(treadDepth)
            });
            c.Summary("created stairs " + level.Name + " -> " + top.Name + ": " + ModelKit.Count(runIds.Count, "run") +
                      (landingIds.Count > 0 ? ", " + ModelKit.Count(landingIds.Count, "landing") : string.Empty) + ", " + actualRisers.ToString(CultureInfo.InvariantCulture) +
                      " risers of " + ModelKit.Mm(riserHeight) + " mm" + (railings.Count > 0 ? ", " + ModelKit.Count(railings.Count, "railing") : string.Empty));
        }

        private static OpException RolledBack(ChangeContext c, string what)
        {
            string first = c.Failures.Errors.Count > 0 ? c.Failures.Errors[0].Text : "transaction not committed";
            return new OpException(ErrorCodes.RevitTransactionRolledBack, "Revit rolled back " + what + ": " + first,
                new Dictionary<string, object> { ["failures"] = c.Failures.Errors.Concat(c.Failures.Warnings).Select(f => (object)f.ToWire()).ToList() });
        }

        private static double DefaultRunWidth(Document doc, Stairs stairs)
        {
            try
            {
                if (doc.GetElement(stairs.GetTypeId()) is StairsType type && type.MinRunWidth > 1e-6) return type.MinRunWidth;
            }
            catch
            {
                // Fall back below.
            }
            return Units.MmToFt(1000);
        }

        /// <summary>Risers per segment, proportional to the usable length of each segment (≥ 2 each, sum = total).</summary>
        private static List<int> SpreadRisers(List<XYZ> path, int total, double width)
        {
            int segments = path.Count - 1;
            var usable = new List<double>();
            for (int i = 0; i < segments; i++)
            {
                double length = path[i].DistanceTo(path[i + 1]) - (i == 0 ? 0 : width / 2) - (i == segments - 1 ? 0 : width / 2);
                usable.Add(Math.Max(length, 1e-3));
            }
            double sum = usable.Sum();
            var counts = usable.Select(u => Math.Max(2, (int)Math.Round(total * u / sum))).ToList();
            int diff = total - counts.Sum();
            for (int guard = 0; diff != 0 && guard < 1000; guard++)
            {
                int index = diff > 0 ? IndexOfMax(usable, counts) : IndexOfReducible(counts);
                if (index < 0) break;
                counts[index] += diff > 0 ? 1 : -1;
                diff += diff > 0 ? -1 : 1;
            }
            return counts;
        }

        private static int IndexOfMax(List<double> usable, List<int> counts)
        {
            int best = 0;
            for (int i = 1; i < usable.Count; i++) if (usable[i] / counts[i] > usable[best] / counts[best]) best = i;
            return best;
        }

        private static int IndexOfReducible(List<int> counts)
        {
            int best = -1;
            for (int i = 0; i < counts.Count; i++) if (counts[i] > 2 && (best < 0 || counts[i] > counts[best])) best = i;
            return best;
        }

        /// <summary>Railings on the treads of new stairs (own transaction; nothing when Revit already added some).</summary>
        private static List<ElementId> AddRailings(ChangeContext c, ElementId stairsId)
        {
            Document doc = c.Doc;
            var result = new List<ElementId>();
            if (!(doc.GetElement(stairsId) is Stairs stairs)) return result;
            ICollection<ElementId> existing = stairs.GetAssociatedRailings();
            if (existing != null && existing.Count > 0)
            {
                result.AddRange(existing);
                return result;
            }
            RailingType railingType = Resolve.DefaultType<RailingType>(doc, BuiltInCategory.OST_StairsRailing);
            if (railingType == null)
            {
                c.Warn(WarningCodes.PartialResult, "no railing type in this document; the stairs have no railings", new[] { stairsId });
                return result;
            }
            // A separate capture: a refused railing is a warning, not a rollback of the stairs (the engine reads c.Failures).
            var capture = new FailureCapture();
            using (var transaction = new Transaction(doc, ModelKit.TxName(c)))
            {
                FailureCapture.Configure(transaction, capture);
                if (transaction.Start() != TransactionStatus.Started) return result;
                try
                {
                    ICollection<ElementId> created = Railing.Create(doc, stairsId, railingType.Id, RailingPlacementPosition.Treads);
                    if (transaction.Commit() == TransactionStatus.Committed && created != null) result.AddRange(created);
                    else c.Warn(WarningCodes.PartialResult, "Revit rolled back the stairs railings; the stairs have none", new[] { stairsId });
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    TempScope.RollBackQuietly(transaction);
                    c.Warn(WarningCodes.PartialResult, "railings were not added to the stairs: " + ex.Message, new[] { stairsId });
                }
            }
            capture.CopyWarningsTo(c.Warnings);
            return result;
        }

        [Op("create_elements.railing")]
        public static void RailingOp(ChangeContext c)
        {
            Document doc = c.Doc;
            RailingType type = c.Args.Has("type")
                ? Resolve.Type<RailingType>(doc, c.Str("type"), c.Args.Field("type"))
                : Resolve.DefaultType<RailingType>(doc, BuiltInCategory.OST_StairsRailing)
                  ?? throw new OpException(ErrorCodes.NotFound, "The document has no railing type; load or create one first.",
                      new Dictionary<string, object> { ["param"] = c.Args.Field("type"), ["kind"] = "railing type", ["value"] = null, ["candidates"] = new List<string>() });

            if (c.Args.Has("host"))
            {
                Element host = WallOps.SingleHost(c, "host");
                if (!(host is Stairs) && Resolve.SafeBuiltIn(host.Category) != BuiltInCategory.OST_Ramps)
                {
                    throw OpException.InvalidArgs(c.Args.Field("host"), ModelKit.Describe(host) + " is not stairs or a ramp (railings by host need stairs/ramps; use points for a free railing)");
                }
                if (host is Stairs stairs && stairs.GetAssociatedRailings().Count > 0)
                {
                    throw OpException.InvalidArgs(c.Args.Field("host"), "stairs " + host.Id.Value + " already have railings (" + string.Join(", ", stairs.GetAssociatedRailings().Select(id => id.Value).Take(4)) +
                        "); change their type with edit_types/modify_elements instead");
                }
                ICollection<ElementId> created = Railing.Create(doc, host.Id, type.Id, RailingPlacementPosition.Treads);
                if (created == null || created.Count == 0) throw new OpException(ErrorCodes.RevitRefused, "Revit created no railing on " + ModelKit.Describe(host) + ".");
                c.Created(created);
                c.Output("type", type.Id);
                c.Summary("created " + ModelKit.Count(created.Count, "railing") + " (" + Resolve.FullTypeName(type) + ") on " + ModelKit.Describe(host));
                return;
            }

            Level level = c.Level("level", required: false) ?? FamilyPlacementOps.DefaultLevel(c, null);
            double z = ModelKit.Z(level);
            List<Curve> curves = ModelKit.Polyline(doc, ModelKit.FlatPoints(c, "points", z), false, c.Args.Field("points"));
            var path = new CurveLoop();
            foreach (Curve curve in curves) path.Append(curve);
            Railing railing = Railing.Create(doc, path, type.Id, level.Id);
            if (railing == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the railing.");
            c.Created(railing);
            ModelKit.Outputs(c, type, level);
            double length = curves.Sum(curve => curve.Length);
            c.Summary("created railing (" + Resolve.FullTypeName(type) + ") on " + level.Name + ", " + ModelKit.Mm(length) + " mm along " + ModelKit.Count(curves.Count, "segment"));
        }
    }
}
