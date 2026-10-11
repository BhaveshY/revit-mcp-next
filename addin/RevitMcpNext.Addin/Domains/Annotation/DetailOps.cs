using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// annotate detail_line | filled_region | detail_item | revision_cloud | copy_to_views (D3 §3.5). Points are mm and
    /// are projected onto the view plane (plans: the level elevation). copy_to_views copies view-specific elements of one
    /// view into others at the same model position (strict: warnings roll back).
    /// </summary>
    internal static class DetailOps
    {
        [Op("annotate.detail_line")]
        public static void DetailLine(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = AnnotationSupport.AnnotationView(c, "a detail line", allowSheet: true, allow3D: false);
            Plane plane = AnnotationSupport.ViewPlane(view);
            List<XYZ> points = AnnotationSupport.OnPlane(plane, c.Points("points"));
            bool closed = c.Bool("closed", false);
            GraphicsStyle style = AnnotationSupport.LineStyle(c);

            var curves = new List<Curve>();
            if (c.Has("through"))
            {
                if (points.Count != 2 || closed)
                {
                    throw OpException.InvalidArgs("through", "an arc takes exactly 2 points (its ends) plus through, and cannot be closed");
                }
                XYZ through = AnnotationSupport.OnPlane(plane, c.Point("through"));
                try
                {
                    curves.Add(Arc.Create(points[0], points[1], through));
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    throw OpException.InvalidArgs("through", "the three points are collinear or coincide; no arc passes through them");
                }
            }
            else
            {
                curves.AddRange(Geometry.BuildPolyline(points, closed, "points"));
            }

            var ids = new List<object>();
            foreach (Curve curve in curves)
            {
                DetailCurve detail = doc.Create.NewDetailCurve(view, curve) ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not create the detail line in '" + view.Name + "'.");
                if (style != null) SetStyle(c, detail, style);
                c.Created(detail);
                ids.Add(detail.Id.Value);
            }
            c.Output("view", view.Id);
            double length = curves.Sum(curve => curve.Length);
            c.Data(new Dictionary<string, object>
            {
                ["view"] = view.Name,
                ["lines"] = ids,
                ["line_style"] = style == null ? null : StyleName(style),
                ["length"] = Units.Mm(length)
            });
            c.Summary((c.Has("through") ? "detail arc" : AnnotationSupport.Count(curves.Count, "detail line")) + " in " + AnnotationSupport.ViewLabel(view) +
                      ", " + Units.FtToMm(length).ToString("0", CultureInfo.InvariantCulture) + " mm" + (style == null ? string.Empty : " (" + StyleName(style) + ")"));
        }

        [Op("annotate.filled_region")]
        public static void FilledRegion(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = AnnotationSupport.AnnotationView(c, "a filled region", allowSheet: false, allow3D: false);
            Plane plane = AnnotationSupport.ViewPlane(view);
            var loops = new List<CurveLoop> { Geometry.BuildCurveLoop(AnnotationSupport.OnPlane(plane, c.Points("points")), "points") };
            List<List<XYZ>> holes = c.Args.Loops("holes", null, required: false);
            for (int index = 0; index < holes.Count; index++)
            {
                loops.Add(Geometry.BuildCurveLoop(AnnotationSupport.OnPlane(plane, holes[index]), c.Args.Field("holes") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]"));
            }
            FilledRegionType type = AnnotationSupport.TypeOrDefault<FilledRegionType>(c, "type", ElementTypeGroup.FilledRegionType);
            GraphicsStyle style = AnnotationSupport.LineStyle(c);
            if (style != null && !Autodesk.Revit.DB.FilledRegion.IsValidLineStyleIdForFilledRegion(doc, style.Id))
            {
                throw OpException.InvalidArgs("line_style", "'" + StyleName(style) + "' cannot outline a filled region");
            }

            Autodesk.Revit.DB.FilledRegion region = Autodesk.Revit.DB.FilledRegion.Create(doc, type.Id, view.Id, loops)
                ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not create the filled region in '" + view.Name + "'.");
            if (style != null) region.SetLineStyleId(style.Id);
            c.Created(region);
            c.Output("region", region.Id);
            double area = LoopArea(loops);
            c.Data(new Dictionary<string, object>
            {
                ["id"] = region.Id.Value,
                ["view"] = view.Name,
                ["type"] = type.Name,
                ["holes"] = holes.Count,
                ["area"] = area > 0 ? Units.Area(area) : null
            });
            c.Summary("filled region '" + type.Name + "' in '" + view.Name + "'" + (holes.Count > 0 ? " with " + AnnotationSupport.Count(holes.Count, "hole") : string.Empty) +
                      (area > 0 ? ", " + Units.M2(area).ToString("0.##", CultureInfo.InvariantCulture) + " m²" : string.Empty));
        }

        [Op("annotate.detail_item")]
        public static void DetailItem(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = AnnotationSupport.AnnotationView(c, "a detail item", allowSheet: true, allow3D: false);
            Plane plane = AnnotationSupport.ViewPlane(view);
            string typeText = c.Str("type");
            if (string.IsNullOrWhiteSpace(typeText)) throw OpException.InvalidArgs("type", "is required (a detail component or annotation symbol type \"Family: Type\")");
            FamilySymbol symbol = Resolve.Type<FamilySymbol>(doc, typeText, c.Args.Field("type"));
            FamilyPlacementType placement = symbol.Family?.FamilyPlacementType ?? FamilyPlacementType.Invalid;
            if (placement != FamilyPlacementType.ViewBased && placement != FamilyPlacementType.CurveBasedDetail)
            {
                throw OpException.InvalidArgs("type", "'" + Resolve.FullTypeName(symbol) + "' is a model family (" + placement + "); give a detail component or annotation symbol, or use place_family");
            }
            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            FamilyInstance instance;
            bool lineBased = placement == FamilyPlacementType.CurveBasedDetail;
            if (lineBased)
            {
                if (!c.Has("start") || !c.Has("end"))
                {
                    throw OpException.InvalidArgs("start", "'" + Resolve.FullTypeName(symbol) + "' is line-based; give start and end",
                        new Dictionary<string, object> { ["op"] = "detail_item", ["view"] = view.Name, ["type"] = Resolve.FullTypeName(symbol), ["start"] = new List<object> { 0, 0 }, ["end"] = new List<object> { 2000, 0 } });
                }
                XYZ start = AnnotationSupport.OnPlane(plane, c.Point("start"));
                XYZ end = AnnotationSupport.OnPlane(plane, c.Point("end"));
                if (start.DistanceTo(end) < Geometry.MinSegmentFeet) throw OpException.InvalidArgs("end", "start and end coincide");
                instance = doc.Create.NewFamilyInstance(Line.CreateBound(start, end), symbol, view);
                if (c.Has("rotation")) c.Warn(WarningCodes.ParamIgnored, "rotation: line-based items follow start→end");
            }
            else
            {
                if (!c.Has("at"))
                {
                    throw OpException.InvalidArgs("at", "'" + Resolve.FullTypeName(symbol) + "' is point-based; give at",
                        new Dictionary<string, object> { ["op"] = "detail_item", ["view"] = view.Name, ["type"] = Resolve.FullTypeName(symbol), ["at"] = new List<object> { 0, 0 } });
                }
                XYZ at = AnnotationSupport.OnPlane(plane, c.Point("at"));
                instance = doc.Create.NewFamilyInstance(at, symbol, view);
                if (instance != null && c.Has("rotation"))
                {
                    double angle = c.Deg("rotation");
                    if (Math.Abs(angle) > 1e-9) ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateUnbound(at, plane.Normal), angle);
                }
            }
            if (instance == null) throw c.Error(ErrorCodes.RevitRefused, "Revit did not place '" + Resolve.FullTypeName(symbol) + "' in '" + view.Name + "'.");
            c.Created(instance);
            c.Output("item", instance.Id);
            c.Data(new Dictionary<string, object>
            {
                ["id"] = instance.Id.Value,
                ["view"] = view.Name,
                ["type"] = Resolve.FullTypeName(symbol),
                ["category"] = Rows.CategoryLabel(symbol),
                ["line_based"] = lineBased
            });
            c.Summary("placed " + (lineBased ? "line-based " : string.Empty) + "'" + Resolve.FullTypeName(symbol) + "' in " + AnnotationSupport.ViewLabel(view));
        }

        [Op("annotate.revision_cloud")]
        public static void RevisionCloud(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = AnnotationSupport.AnnotationView(c, "a revision cloud", allowSheet: true, allow3D: false);
            Revision revision = SheetSupport.FindRevision(doc, c.Str("revision"));
            if (revision.Issued)
            {
                throw new OpException(ErrorCodes.InvalidArgs,
                    "Revision '" + SheetSupport.Describe(revision).Trim() + "' is issued; Revit adds no clouds to issued revisions.",
                    new Dictionary<string, object>
                    {
                        ["param"] = "revision",
                        ["reason"] = "revision is issued",
                        ["example"] = new Dictionary<string, object> { ["tool"] = "edit_sheets", ["op"] = "revision_create", ["description"] = revision.Description + " (rev)" }
                    });
            }
            Plane plane = AnnotationSupport.ViewPlane(view);
            List<Curve> curves = Geometry.BuildPolyline(AnnotationSupport.OnPlane(plane, c.Points("points")), closed: true, param: "points").Cast<Curve>().ToList();
            if (curves.Count < 3) throw OpException.InvalidArgs("points", "a revision cloud needs at least 3 distinct points");
            Autodesk.Revit.DB.RevisionCloud cloud = Autodesk.Revit.DB.RevisionCloud.Create(doc, view, revision.Id, curves)
                ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not create the revision cloud in '" + view.Name + "'.");
            c.Created(cloud);
            c.Output("cloud", cloud.Id);
            c.Data(new Dictionary<string, object>
            {
                ["id"] = cloud.Id.Value,
                ["view"] = view.Name,
                ["revision"] = SheetSupport.RevisionData(revision)
            });
            c.Summary("revision cloud for '" + revision.Description + "' in " + AnnotationSupport.ViewLabel(view));
        }

        [Op("annotate.copy_to_views")]
        public static void CopyToViews(ChangeContext c)
        {
            Document doc = c.Doc;
            View source = AnnotationSupport.AnnotationView(c, "copying annotation", allowSheet: true, allow3D: true);
            IList<Element> elements = c.Elements("ids");
            List<Element> notOwned = elements.Where(e => e.OwnerViewId != source.Id).ToList();
            if (notOwned.Count > 0)
            {
                throw OpException.InvalidArgs("ids", AnnotationSupport.Count(notOwned.Count, "element") + " (" + string.Join(", ", notOwned.Take(5).Select(e => e.Id.Value.ToString(CultureInfo.InvariantCulture))) +
                                                     ") are not annotation of '" + source.Name + "'; copy_to_views copies view-specific elements (tags, text, dimensions, detail items) of the source view");
            }
            List<string> names = c.Args.Strs("views");
            if (names.Count == 0) throw OpException.InvalidArgs("views", "is required (target view names or ids)");
            var targets = new List<View>();
            for (int index = 0; index < names.Count; index++)
            {
                View target = ListArgs.Item(c.Args, "views", index, (value, field) => Resolve.View(doc, value, field, c.Ctx.UiDoc));
                if (target.Id == source.Id)
                {
                    c.Warn(WarningCodes.ParamIgnored, "views: '" + target.Name + "' is the source view and was skipped");
                    continue;
                }
                if (target.IsTemplate) throw OpException.InvalidArgs(c.Args.Field("views") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", "'" + target.Name + "' is a view template");
                if (targets.All(t => t.Id != target.Id)) targets.Add(target);
            }
            if (targets.Count == 0) throw OpException.InvalidArgs("views", "no target view other than the source view");

            ICollection<ElementId> ids = elements.Select(e => e.Id).ToList();
            var table = new TableBuilder(new[] { "view", "copied" });
            var options = new CopyPasteOptions();
            foreach (View target in targets)
            {
                ICollection<ElementId> copies = ElementTransformUtils.CopyElements(source, ids, target, Transform.Identity, options);
                c.Created(copies);
                table.Add(target.Name, copies.Count);
            }
            c.Output("view", targets[0].Id);
            c.Data(new Dictionary<string, object> { ["source"] = source.Name, ["views"] = table.ToWire() });
            c.Summary("copied " + AnnotationSupport.Count(ids.Count, "element") + " from '" + source.Name + "' to " + AnnotationSupport.Count(targets.Count, "view"));
        }

        // ---------------------------------------------------------------------------------------------------------

        private static void SetStyle(ChangeContext c, DetailCurve detail, GraphicsStyle style)
        {
            ICollection<ElementId> valid = detail.GetLineStyleIds();
            if (!valid.Contains(style.Id))
            {
                List<string> names = valid.Select(id => c.Doc.GetElement(id)).OfType<GraphicsStyle>().Select(StyleName).ToList();
                throw OpException.NotFound("line_style", "line style for detail lines", StyleName(style), Resolve.Closest(StyleName(style), names));
            }
            detail.LineStyle = style;
        }

        private static string StyleName(GraphicsStyle style)
        {
            return style.GraphicsStyleCategory?.Name ?? style.Name;
        }

        /// <summary>Area of the outer loop minus holes (loops on one plane), ft².</summary>
        private static double LoopArea(IList<CurveLoop> loops)
        {
            double area = 0;
            for (int index = 0; index < loops.Count; index++)
            {
                var points = new List<XYZ>();
                foreach (Curve curve in loops[index]) points.Add(curve.GetEndPoint(0));
                double loopArea = PlanarArea(points);
                area += index == 0 ? loopArea : -loopArea;
            }
            return Math.Max(0, area);
        }

        /// <summary>Polygon area in its own plane (Newell's method), ft².</summary>
        private static double PlanarArea(IList<XYZ> points)
        {
            if (points.Count < 3) return 0;
            XYZ sum = XYZ.Zero;
            for (int index = 0; index < points.Count; index++)
            {
                XYZ a = points[index], b = points[(index + 1) % points.Count];
                sum += a.CrossProduct(b);
            }
            return sum.GetLength() / 2.0;
        }
    }
}
