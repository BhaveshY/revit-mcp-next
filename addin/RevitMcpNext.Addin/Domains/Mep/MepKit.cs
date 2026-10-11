using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpNext.Addin
{
    /// <summary>Connector, fitting, system-type and size helpers of the mep tool (D3 §3.8 MEP).</summary>
    internal static class MepKit
    {
        /// <summary>Two connectors closer than this are treated as coincident (mm → feet).</summary>
        public static readonly double Coincident = Units.MmToFt(1);

        public static ConnectorManager ManagerOf(Element element)
        {
            if (element is MEPCurve curve) return curve.ConnectorManager;
            if (element is FamilyInstance instance) return instance.MEPModel?.ConnectorManager;
            return null;
        }

        /// <summary>Physical connectors (End type) of an element.</summary>
        public static List<Connector> Connectors(Element element)
        {
            var result = new List<Connector>();
            ConnectorManager manager = ManagerOf(element);
            if (manager == null) return result;
            foreach (Connector connector in manager.Connectors)
            {
                if (connector.ConnectorType == ConnectorType.End || connector.ConnectorType == ConnectorType.Curve && element is FamilyInstance) result.Add(connector);
            }
            return result;
        }

        /// <summary>The connector of <paramref name="element"/> nearest to <paramref name="point"/> (optionally only unconnected ones).</summary>
        public static Connector Nearest(Element element, XYZ point, bool unconnectedOnly = false)
        {
            return Connectors(element)
                .Where(c => !unconnectedOnly || !c.IsConnected)
                .Where(c => c.ConnectorType == ConnectorType.End)
                .OrderBy(c => c.Origin.DistanceTo(point))
                .FirstOrDefault();
        }

        /// <summary>The closest pair of unconnected end connectors of two elements (null when one has none).</summary>
        public static Tuple<Connector, Connector> ClosestPair(Element a, Element b)
        {
            Tuple<Connector, Connector> best = null;
            double bestDistance = double.MaxValue;
            foreach (Connector ca in Connectors(a).Where(x => x.ConnectorType == ConnectorType.End && !x.IsConnected))
            {
                foreach (Connector cb in Connectors(b).Where(x => x.ConnectorType == ConnectorType.End && !x.IsConnected))
                {
                    if (ca.Domain != cb.Domain) continue;
                    double distance = ca.Origin.DistanceTo(cb.Origin);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = Tuple.Create(ca, cb);
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Elbow (or, for parallel/aligned connectors, a transition/union) between two connectors in a SubTransaction,
        /// so a refused fitting leaves nothing behind. Returns null and <paramref name="error"/> on failure.
        /// </summary>
        public static FamilyInstance TryFitting(Document doc, Connector a, Connector b, out string error)
        {
            error = null;
            XYZ da = a.CoordinateSystem?.BasisZ, db = b.CoordinateSystem?.BasisZ;
            bool aligned = da != null && db != null && Math.Abs(da.DotProduct(db)) > 0.9999;
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    FamilyInstance fitting;
                    if (aligned)
                    {
                        fitting = SameSize(a, b) ? doc.Create.NewUnionFitting(a, b) : doc.Create.NewTransitionFitting(a, b);
                    }
                    else
                    {
                        fitting = doc.Create.NewElbowFitting(a, b);
                    }
                    doc.Regenerate();
                    sub.Commit();
                    return fitting;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    try { if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack(); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                    error = ex.Message;
                    return null;
                }
            }
        }

        private static bool SameSize(Connector a, Connector b)
        {
            try
            {
                if (a.Shape != b.Shape) return false;
                if (a.Shape == ConnectorProfileType.Round) return Math.Abs(a.Radius - b.Radius) < 1e-6;
                return Math.Abs(a.Width - b.Width) < 1e-6 && Math.Abs(a.Height - b.Height) < 1e-6;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Removes consecutive duplicates and collinear interior vertices (a straight run needs no fitting).</summary>
        public static List<XYZ> Simplify(IList<XYZ> points)
        {
            var clean = new List<XYZ>();
            foreach (XYZ p in points)
            {
                if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > Geometry.MinSegmentFeet) clean.Add(p);
            }
            for (int i = clean.Count - 2; i >= 1; i--)
            {
                XYZ a = (clean[i] - clean[i - 1]).Normalize(), b = (clean[i + 1] - clean[i]).Normalize();
                if (a.DotProduct(b) > 0.99999) clean.RemoveAt(i);
            }
            return clean;
        }

        /// <summary>Sets a size parameter; warns VALUE_CLAMPED when Revit rounds it to another size.</summary>
        public static void SetSize(ChangeContext c, Element element, BuiltInParameter bip, double feet, string what)
        {
            Parameter p = element.get_Parameter(bip);
            if (p == null || p.IsReadOnly)
            {
                c.Warn(WarningCodes.ParamIgnored, what + " cannot be set on " + ModelKit.Describe(element) + " (read-only or not available)", new[] { element.Id });
                return;
            }
            p.Set(feet);
            double actual = p.AsDouble();
            if (Math.Abs(actual - feet) > Units.MmToFt(0.5))
            {
                c.Warn(WarningCodes.ValueClamped, what + " " + ModelKit.Mm(feet) + " mm became " + ModelKit.Mm(actual) + " mm on " + ModelKit.Describe(element), new[] { element.Id });
            }
        }

        /// <summary>
        /// A system type by name (type name, or the name of an existing system whose type is used); default: the first
        /// type with <paramref name="preferred"/> classification, else the first of the class.
        /// </summary>
        public static T SystemType<T>(ChangeContext c, string key, Func<T, bool> preferred, string kind) where T : MEPSystemType
        {
            Document doc = c.Doc;
            string value = c.Str(key);
            List<T> all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrWhiteSpace(value))
            {
                T fallback = all.FirstOrDefault(preferred) ?? all.FirstOrDefault();
                if (fallback != null) return fallback;
                throw new OpException(ErrorCodes.NotFound, "The document has no " + kind + " system type; use a template with MEP system types.",
                    new Dictionary<string, object> { ["param"] = c.Args.Field(key), ["kind"] = kind + " system type", ["value"] = null, ["candidates"] = new List<string>() });
            }
            T byId = Resolve.ById<T>(doc, value);
            if (byId != null) return byId;
            List<T> exact = all.Where(t => Resolve.Eq(t.Name, value.Trim())).ToList();
            if (exact.Count >= 1) return exact[0];
            MEPSystem system = new FilteredElementCollector(doc).OfClass(typeof(MEPSystem)).Cast<MEPSystem>().FirstOrDefault(s => Resolve.Eq(s.Name, value.Trim()));
            if (system != null && doc.GetElement(system.GetTypeId()) is T fromSystem) return fromSystem;
            throw OpException.NotFound(c.Args.Field(key), kind + " system type", value, Resolve.Closest(value, all.Select(t => t.Name)));
        }

        /// <summary>A system element by id or name (any MEP system class).</summary>
        public static MEPSystem System(ChangeContext c, string key)
        {
            Document doc = c.Doc;
            string value = c.Str(key) ?? throw OpException.InvalidArgs(c.Args.Field(key), "is required (a system name or id)");
            MEPSystem byId = Resolve.ById<MEPSystem>(doc, value);
            if (byId != null) return byId;
            List<MEPSystem> systems = new FilteredElementCollector(doc).OfClass(typeof(MEPSystem)).Cast<MEPSystem>().ToList();
            return Resolve.Single(systems, s => s.Name, value, c.Args.Field(key), "system");
        }

        public static bool IsPipingClassification(PipingSystemType type, MEPSystemClassification wanted) => type.SystemClassification == wanted;

        public static string Size(double feet) => ModelKit.Mm(feet);

        public static string Joined(IEnumerable<string> items, int max = 6)
        {
            List<string> list = items.ToList();
            return string.Join(", ", list.Take(max)) + (list.Count > max ? " +" + (list.Count - max).ToString(CultureInfo.InvariantCulture) : string.Empty);
        }
    }
}
