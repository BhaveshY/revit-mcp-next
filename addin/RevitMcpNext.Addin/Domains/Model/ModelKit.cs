using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Shared helpers of the modeling lanes (create_elements, place_family, mep, structure): the SPEC §5.4 z rule,
    /// defaulted type lookups, name increments, sketch planes, family symbol activation, StructuralType by category,
    /// short result rows. Everything is in Revit internal units (feet) unless a name says mm.
    /// </summary>
    internal static class ModelKit
    {
        private static readonly Regex TrailingNumber = new Regex(@"^(?<head>.*?)(?<num>\d+)$", RegexOptions.CultureInvariant);

        // ---------------------------------------------------------------------------------------------------------
        // Points and the z rule (SPEC §5.4: z of a 3D point is height above level when a level is given, else absolute)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Internal elevation of a level (project origin, independent of the Elevation Base setting).</summary>
        public static double Z(Level level) => level == null ? 0 : level.ProjectElevation;

        /// <summary>
        /// Points of <paramref name="key"/> with the z rule applied: a given z is above <paramref name="level"/> (or absolute
        /// without level); a missing z becomes the level elevation (or 0) plus <paramref name="offsetFt"/>.
        /// </summary>
        public static List<XYZ> Points3(ChangeContext c, string key, Level level, double offsetFt = 0, bool required = true)
        {
            double baseZ = Z(level);
            return c.Args.Points(key, PayloadReader.NoZ, required)
                .Select(p => new XYZ(p.X, p.Y, PayloadReader.IsNoZ(p.Z) ? baseZ + offsetFt : (level != null ? baseZ + p.Z : p.Z)))
                .ToList();
        }

        /// <summary>One point with the z rule (null when absent and not required).</summary>
        public static XYZ Point3(ChangeContext c, string key, Level level, double offsetFt = 0, bool required = true)
        {
            XYZ p = c.Args.PointOpt(key, PayloadReader.NoZ);
            if (p == null)
            {
                if (required) throw OpException.InvalidArgs(c.Args.Field(key), "is required (a point [x,y] or [x,y,z] in mm)");
                return null;
            }
            double baseZ = Z(level);
            return new XYZ(p.X, p.Y, PayloadReader.IsNoZ(p.Z) ? baseZ + offsetFt : (level != null ? baseZ + p.Z : p.Z));
        }

        /// <summary>Points of a level-based sketch (wall, floor, room...): z is ignored and set to <paramref name="z"/>.</summary>
        public static List<XYZ> FlatPoints(ChangeContext c, string key, double z, bool required = true)
        {
            return c.Args.Points(key, 0, required).Select(p => new XYZ(p.X, p.Y, z)).ToList();
        }

        public static XYZ Flat(XYZ p, double z) => p == null ? null : new XYZ(p.X, p.Y, z);

        /// <summary>Loops of a level-based sketch with z set to <paramref name="z"/>.</summary>
        public static List<List<XYZ>> FlatLoops(ChangeContext c, string key, double z)
        {
            return c.Args.Loops(key, 0, required: false).Select(loop => loop.Select(p => new XYZ(p.X, p.Y, z)).ToList()).ToList();
        }

        /// <summary>A list of lengths in mm (bare numbers or unit strings) converted to feet.</summary>
        public static List<double> LengthsFt(ChangeContext c, string key)
        {
            var result = new List<double>();
            List<object> items = c.Args.List(key) ?? (c.Args.Has(key) ? new List<object> { c.Args.Get(key) } : new List<object>());
            for (int index = 0; index < items.Count; index++)
            {
                var one = new PayloadReader(new Dictionary<string, object>(StringComparer.Ordinal) { ["v"] = items[index] },
                    c.Args.Field(key) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", c.Args.BareLengthToMm);
                double? mm = one.MmRawOpt("v");
                if (mm.HasValue) result.Add(Units.MmToFt(mm.Value));
            }
            return result;
        }

        /// <summary>A list of integers (edge indexes, counts).</summary>
        public static List<int> Ints(ChangeContext c, string key)
        {
            var result = new List<int>();
            foreach (string token in c.Args.Strs(key))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && Math.Abs(number - Math.Round(number)) < 1e-9)
                    {
                        value = (int)Math.Round(number);
                    }
                    else
                    {
                        throw OpException.InvalidArgs(c.Args.Field(key), "must be a list of integers, got '" + token + "'");
                    }
                }
                result.Add(value);
            }
            return result;
        }

        /// <summary>Unit horizontal direction a→b (INVALID_ARGS when the points coincide in plan).</summary>
        public static XYZ DirXY(XYZ a, XYZ b, string param)
        {
            var d = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (d.GetLength() < Geometry.MinSegmentFeet) throw OpException.InvalidArgs(param, "start and end must differ in plan");
            return d.Normalize();
        }

        /// <summary>Left normal of a horizontal direction (counterclockwise 90°).</summary>
        public static XYZ LeftOf(XYZ direction) => new XYZ(-direction.Y, direction.X, 0);

        /// <summary>A bound line, INVALID_ARGS when shorter than Revit's short curve tolerance.</summary>
        public static Line LineOf(Document doc, XYZ a, XYZ b, string param)
        {
            double tolerance = Math.Max(doc.Application.ShortCurveTolerance, 1e-6);
            if (a.DistanceTo(b) <= tolerance)
            {
                throw OpException.InvalidArgs(param, "segment " + Pt(a) + " -> " + Pt(b) + " is shorter than Revit's minimum (" +
                    Units.FtToMm(tolerance).ToString("0.#", CultureInfo.InvariantCulture) + " mm)");
            }
            return Line.CreateBound(a, b);
        }

        /// <summary>An arc start→end through a third point (INVALID_ARGS when the points are collinear).</summary>
        public static Arc ArcOf(XYZ start, XYZ end, XYZ through, string param)
        {
            XYZ u = end - start, v = through - start;
            if (u.CrossProduct(v).GetLength() < 1e-9) throw OpException.InvalidArgs(param, "through must not lie on the line start -> end");
            return Arc.Create(start, end, through);
        }

        /// <summary>Lines through the points (open, or closed back to the first point).</summary>
        public static List<Curve> Polyline(Document doc, IList<XYZ> points, bool closed, string param)
        {
            var clean = new List<XYZ>();
            foreach (XYZ p in points)
            {
                if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > Geometry.MinSegmentFeet) clean.Add(p);
            }
            if (closed && clean.Count > 2 && clean[0].DistanceTo(clean[clean.Count - 1]) <= Geometry.MinSegmentFeet) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 2) throw OpException.InvalidArgs(param, "needs at least 2 distinct points");
            var curves = new List<Curve>();
            for (int i = 0; i + 1 < clean.Count; i++) curves.Add(LineOf(doc, clean[i], clean[i + 1], param));
            if (closed && clean.Count > 2) curves.Add(LineOf(doc, clean[clean.Count - 1], clean[0], param));
            return curves;
        }

        /// <summary>A closed CurveLoop through the points (at least 3 distinct points).</summary>
        public static CurveLoop Loop(Document doc, IList<XYZ> points, string param)
        {
            List<XYZ> loop = Geometry.NormalizeClosedLoop(points);
            if (loop.Count < 3) throw OpException.InvalidArgs(param, "needs at least 3 distinct points for a closed outline");
            if (Geometry.PolygonAreaXY(loop) < 1e-6) throw OpException.InvalidArgs(param, "outline has no area (points are collinear)");
            var curveLoop = new CurveLoop();
            for (int i = 0; i < loop.Count; i++) curveLoop.Append(LineOf(doc, loop[i], loop[(i + 1) % loop.Count], param));
            return curveLoop;
        }

        public static CurveArray ToArray(IEnumerable<Curve> curves)
        {
            var array = new CurveArray();
            foreach (Curve curve in curves) array.Append(curve);
            return array;
        }

        public static string Pt(XYZ p)
        {
            if (p == null) return "[]";
            return "[" + Units.FtToMm(p.X).ToString("0.#", CultureInfo.InvariantCulture) + "," + Units.FtToMm(p.Y).ToString("0.#", CultureInfo.InvariantCulture) + "]";
        }

        // ---------------------------------------------------------------------------------------------------------
        // Levels, views, sketch planes
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>A horizontal sketch plane at the level (reuses the level's own plane) or at z.</summary>
        public static SketchPlane HorizontalPlane(Document doc, Level level, double z)
        {
            if (level != null && Math.Abs(Z(level) - z) < 1e-9)
            {
                try { return SketchPlane.Create(doc, level.Id); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            }
            return SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
        }

        /// <summary>A sketch plane that contains the curve (horizontal when the curve is flat, else vertical through it).</summary>
        public static SketchPlane PlaneFor(Document doc, Curve curve)
        {
            XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
            if (Math.Abs(a.Z - b.Z) < 1e-9 && !(curve is Arc arc && Math.Abs(Math.Abs(arc.Normal.Z) - 1) > 1e-6))
            {
                return SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, a));
            }
            if (curve is Arc spatial) return SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(spatial.Normal, spatial.Center));
            XYZ d = (b - a).Normalize();
            XYZ helper = Math.Abs(d.Z) > 0.99 ? XYZ.BasisX : XYZ.BasisZ;
            XYZ normal = d.CrossProduct(helper).Normalize();
            return SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, a));
        }

        /// <summary>A non-template floor plan of the level (or any non-template plan when <paramref name="level"/> is null).</summary>
        public static ViewPlan PlanOf(Document doc, Level level, ViewType type = ViewType.FloorPlan)
        {
            List<ViewPlan> plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == type).ToList();
            if (level != null)
            {
                ViewPlan match = plans.FirstOrDefault(v => v.GenLevel != null && v.GenLevel.Id == level.Id);
                if (match != null) return match;
            }
            return plans.FirstOrDefault();
        }

        /// <summary>The level a view is associated with (plans), else null.</summary>
        public static Level LevelOf(View view) => (view as ViewPlan)?.GenLevel;

        /// <summary>The level of an element (LevelId, base constraint, reference level), else the level at or below its anchor.</summary>
        public static Level LevelOf(Document doc, Element element)
        {
            if (element == null) return null;
            try
            {
                if (element.LevelId != null && element.LevelId != ElementId.InvalidElementId && doc.GetElement(element.LevelId) is Level own) return own;
            }
            catch
            {
                // Some elements throw on LevelId.
            }
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM, BuiltInParameter.ROOF_BASE_LEVEL_PARAM, BuiltInParameter.LEVEL_PARAM, BuiltInParameter.RBS_START_LEVEL_PARAM })
            {
                try
                {
                    ElementId id = element.get_Parameter(bip)?.AsElementId();
                    if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is Level level) return level;
                }
                catch
                {
                    // Parameter not applicable.
                }
            }
            XYZ anchor = Geometry.AnchorOf(element);
            return anchor == null ? null : Resolve.NearestLevel(doc, anchor.Z);
        }

        /// <summary>The phase of a view (VIEW_PHASE), else the last phase of the document.</summary>
        public static Phase PhaseOf(Document doc, View view = null)
        {
            try
            {
                ElementId id = view?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is Phase phase) return phase;
            }
            catch
            {
                // No phase parameter.
            }
            return doc.Phases.Size > 0 ? doc.Phases.get_Item(doc.Phases.Size - 1) : null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Types
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The named type (key), else the document default for the category, else NOT_FOUND naming the category.</summary>
        public static T TypeOrDefault<T>(ChangeContext c, string key, BuiltInCategory category, string kind) where T : ElementType
        {
            T type = c.Type<T>(key, category, required: false);
            if (type != null) return type;
            throw new OpException(ErrorCodes.NotFound, "The document has no " + kind + " type; load or create one first.",
                new Dictionary<string, object> { ["param"] = c.Args.Field(key), ["kind"] = kind + " type", ["value"] = null, ["candidates"] = new List<string>() });
        }

        /// <summary>The first element type of a class (optionally of a category), else null.</summary>
        public static T FirstType<T>(Document doc, BuiltInCategory? category = null, Func<T, bool> where = null) where T : ElementType
        {
            var collector = new FilteredElementCollector(doc).OfClass(typeof(T));
            if (category.HasValue) collector = collector.OfCategory(category.Value);
            return collector.Cast<T>().Where(t => where == null || where(t)).OrderBy(t => Resolve.FullTypeName(t), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        /// <summary>Activates an inactive family symbol (and regenerates once).</summary>
        public static void Activate(Document doc, FamilySymbol symbol)
        {
            if (symbol != null && !symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }
        }

        /// <summary>StructuralType taken from the family category (columns, framing, footings), else NonStructural.</summary>
        public static StructuralType StructuralTypeOf(FamilySymbol symbol)
        {
            switch (Resolve.SafeBuiltIn(symbol?.Category))
            {
                case BuiltInCategory.OST_StructuralColumns: return StructuralType.Column;
                case BuiltInCategory.OST_StructuralFraming: return StructuralType.Beam;
                case BuiltInCategory.OST_StructuralFoundation: return StructuralType.Footing;
                default: return StructuralType.NonStructural;
            }
        }

        public static BuiltInCategory CategoryOf(Element element) => Resolve.SafeBuiltIn(element?.Category);

        // ---------------------------------------------------------------------------------------------------------
        // Names
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>"Level 3" → "Level 4", "A" → "B", "Z" → "AA", "1a" → "1a 2"; skips names in <paramref name="taken"/>.</summary>
        public static string NextName(string name, ISet<string> taken)
        {
            string candidate = Increment(name);
            int guard = 0;
            while (taken.Contains(candidate) && guard++ < 10000) candidate = Increment(candidate);
            return candidate;
        }

        public static string Increment(string name)
        {
            string text = (name ?? string.Empty).Trim();
            if (text.Length == 0) return "1";
            Match match = TrailingNumber.Match(text);
            if (match.Success)
            {
                string digits = match.Groups["num"].Value;
                long value = long.Parse(digits, CultureInfo.InvariantCulture) + 1;
                string next = value.ToString(CultureInfo.InvariantCulture);
                if (digits.Length > next.Length && digits.StartsWith("0", StringComparison.Ordinal)) next = next.PadLeft(digits.Length, '0');
                return match.Groups["head"].Value + next;
            }
            if (text.All(ch => ch >= 'A' && ch <= 'Z') && text.Length <= 3) return NextLetters(text);
            if (text.All(ch => ch >= 'a' && ch <= 'z') && text.Length <= 3) return NextLetters(text.ToUpperInvariant()).ToLowerInvariant();
            return text + " 2";
        }

        private static string NextLetters(string letters)
        {
            char[] chars = letters.ToCharArray();
            for (int i = chars.Length - 1; i >= 0; i--)
            {
                if (chars[i] < 'Z')
                {
                    chars[i]++;
                    return new string(chars);
                }
                chars[i] = 'A';
            }
            return "A" + new string(chars);
        }

        public static HashSet<string> NamesOf<T>(Document doc, Func<T, string> name) where T : Element
        {
            return new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().Select(name).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Sets a name or throws NAME_TAKEN when another element of the class already uses it.</summary>
        public static void Rename(Element element, string name, ISet<string> taken, string kind, string param = "name")
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string text = name.Trim();
            if (taken.Contains(text) && !string.Equals(element.Name, text, StringComparison.OrdinalIgnoreCase))
            {
                throw new OpException(ErrorCodes.NameTaken, "A " + kind + " named '" + text + "' already exists.",
                    new Dictionary<string, object> { ["param"] = param, ["kind"] = kind, ["value"] = text });
            }
            element.Name = text;
            taken.Add(text);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Parameters
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Sets a built-in double parameter; false when the element does not have it or it is read-only.</summary>
        public static bool TrySet(Element element, BuiltInParameter bip, double value)
        {
            Parameter p = element?.get_Parameter(bip);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return false;
            return p.Set(value);
        }

        public static bool TrySet(Element element, BuiltInParameter bip, ElementId value)
        {
            Parameter p = element?.get_Parameter(bip);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.ElementId) return false;
            return p.Set(value);
        }

        public static bool TrySet(Element element, BuiltInParameter bip, string value)
        {
            if (value == null) return false;
            Parameter p = element?.get_Parameter(bip);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return false;
            return p.Set(value);
        }

        public static bool TrySet(Element element, BuiltInParameter bip, int value)
        {
            Parameter p = element?.get_Parameter(bip);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Integer) return false;
            return p.Set(value);
        }

        /// <summary>Sets a built-in double parameter and warns PARAM_IGNORED when it cannot be set.</summary>
        public static void SetOrWarn(ChangeContext c, Element element, BuiltInParameter bip, double value, string what)
        {
            if (!TrySet(element, bip, value))
            {
                c.Warn(WarningCodes.ParamIgnored, what + " could not be set on " + Describe(element), new[] { element.Id });
            }
        }

        public static double? GetDouble(Element element, BuiltInParameter bip)
        {
            Parameter p = element?.get_Parameter(bip);
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? p.AsDouble() : (double?)null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Output helpers
        // ---------------------------------------------------------------------------------------------------------

        public static string Describe(Element element)
        {
            if (element == null) return "(none)";
            string label = Rows.CategoryLabel(element.Category) ?? element.GetType().Name;
            return Singular(label) + " " + element.Id.Value.ToString(CultureInfo.InvariantCulture);
        }

        public static string Singular(string label)
        {
            if (string.IsNullOrEmpty(label)) return "element";
            if (label.EndsWith("ies", StringComparison.Ordinal)) return label.Substring(0, label.Length - 3) + "y";
            if (label.EndsWith("ses", StringComparison.Ordinal)) return label.Substring(0, label.Length - 2);
            if (label.EndsWith("s", StringComparison.Ordinal) && !label.EndsWith("ss", StringComparison.Ordinal)) return label.Substring(0, label.Length - 1);
            return label;
        }

        public static string Count(int n, string singular, string plural = null)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");
        }

        public static string Mm(double feet) => Units.FtToMm(feet).ToString("0.#", CultureInfo.InvariantCulture);

        public static string TypeName(Element type) => type is ElementType et ? Resolve.FullTypeName(et) : type?.Name ?? "(default)";

        /// <summary>Publishes the standard outputs: type and level ids for $refs.</summary>
        public static void Outputs(ChangeContext c, ElementType type, Level level)
        {
            if (type != null) c.Output("type", type.Id);
            if (level != null) c.Output("level", level.Id);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Transactions, slopes, boundaries
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Name for transactions/edit scopes opened by tx "own" handlers (SPEC §4.4): "MCP temp preview" in preview,
        /// else "MCP &lt;wtag&gt; &lt;tool.op&gt;" (inner names reuse the write tag; only the group name is in the undo list).
        /// </summary>
        public static string TxName(ChangeContext c) => c.IsPreview ? Naming.Temp(Naming.Purposes.Preview) : Naming.Write(c.Ctx.WriteTag, c.Key);

        /// <summary>A slope in degrees as Revit's internal slope value (rise/run), unit-system safe.</summary>
        public static double SlopeFromDegrees(double degrees, string param = "slope")
        {
            if (degrees <= -89.9 || degrees >= 89.9) throw OpException.InvalidArgs(param, "must be between -89.9 and 89.9 degrees");
            return UnitUtils.ConvertToInternalUnits(degrees, UnitTypeId.SlopeDegrees);
        }

        /// <summary>Boundary loops of a room/space/area flattened to <paramref name="z"/> (outer loop first).</summary>
        public static List<CurveLoop> BoundaryLoops(SpatialElement spatial, double z, SpatialElementBoundaryLocation location = SpatialElementBoundaryLocation.Finish)
        {
            var result = new List<CurveLoop>();
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = location };
            IList<IList<BoundarySegment>> segments = spatial.GetBoundarySegments(options);
            if (segments == null) return result;
            foreach (IList<BoundarySegment> loop in segments)
            {
                var curveLoop = new CurveLoop();
                foreach (BoundarySegment segment in loop)
                {
                    Curve curve = segment.GetCurve();
                    if (curve == null) continue;
                    double dz = z - curve.GetEndPoint(0).Z;
                    curveLoop.Append(Math.Abs(dz) < 1e-9 ? curve : curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, dz))));
                }
                if (curveLoop.NumberOfCurves() > 2) result.Add(curveLoop);
            }
            return result;
        }

        /// <summary>The elements of a selector param (r#/selection/ids) filtered to <typeparamref name="T"/>; INVALID_ARGS when none.</summary>
        public static List<T> ElementsOf<T>(ChangeContext c, string key, string kind) where T : Element
        {
            IList<Element> elements = c.Elements(key);
            List<T> typed = elements.OfType<T>().ToList();
            if (typed.Count == 0) throw OpException.InvalidArgs(c.Args.Field(key), "contains no " + kind + " (got " + elements.Count.ToString(CultureInfo.InvariantCulture) + " other element(s))");
            if (typed.Count < elements.Count)
            {
                c.Warn(WarningCodes.ParamIgnored, (elements.Count - typed.Count).ToString(CultureInfo.InvariantCulture) + " element(s) in " + c.Args.Field(key) + " are not " + kind + " and were skipped");
            }
            return typed;
        }

        /// <summary>A fail-safe Regenerate (returns false when Revit refuses).</summary>
        public static bool TryRegenerate(Document doc)
        {
            try
            {
                doc.Regenerate();
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }
    }
}
