using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// structure foundation (D1 §4B, D3 §3.8): kind isolated (footings under columns from ids/from, or at points on a
    /// level), wall (WallFoundation under walls) or slab (foundation slab from a points outline).
    /// </summary>
    internal static class FoundationOps
    {
        [Op("structure.foundation")]
        public static void Foundation(ChangeContext c)
        {
            string kind = c.Args.Enum("kind", null, "isolated", "wall", "slab") ?? throw OpException.InvalidArgs(c.Args.Field("kind"), "is required (isolated, wall or slab)");
            switch (kind)
            {
                case "isolated":
                    Isolated(c);
                    break;
                case "wall":
                    WallFoundations(c);
                    break;
                default:
                    Slab(c);
                    break;
            }
        }

        private static IList<Element> Targets(ChangeContext c, bool required)
        {
            if (c.Args.Has("ids")) return c.Elements("ids");
            if (c.Args.Has("from")) return c.Elements("from");
            if (required) throw OpException.InvalidArgs("ids", "give ids or from (the elements to put foundations under)");
            return new List<Element>();
        }

        private static void Isolated(ChangeContext c)
        {
            Document doc = c.Doc;
            FamilySymbol symbol = Resolve.Type<FamilySymbol>(doc, c.Str("type"), c.Args.Field("type"), BuiltInCategory.OST_StructuralFoundation);
            ModelKit.Activate(doc, symbol);
            var placements = new List<Tuple<XYZ, Level, Element>>();
            if (c.Args.Has("ids") || c.Args.Has("from"))
            {
                foreach (Element element in Targets(c, true))
                {
                    BuiltInCategory category = ModelKit.CategoryOf(element);
                    if (category != BuiltInCategory.OST_StructuralColumns && category != BuiltInCategory.OST_Columns)
                    {
                        c.Warn(WarningCodes.ParamIgnored, ModelKit.Describe(element) + " is not a column and was skipped", new[] { element.Id });
                        continue;
                    }
                    Level baseLevel = doc.GetElement(element.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() ?? element.LevelId) as Level ?? ModelKit.LevelOf(doc, element);
                    double baseOffset = ModelKit.GetDouble(element, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM) ?? 0;
                    XYZ p = element.Location is LocationPoint point ? point.Point : (element.Location as LocationCurve)?.Curve.GetEndPoint(0) ?? Geometry.AnchorOf(element);
                    if (p == null || baseLevel == null) continue;
                    placements.Add(Tuple.Create(new XYZ(p.X, p.Y, ModelKit.Z(baseLevel) + baseOffset), baseLevel, element));
                }
                if (placements.Count == 0) throw OpException.InvalidArgs(c.Args.Field(c.Args.Has("ids") ? "ids" : "from"), "contains no columns");
            }
            else
            {
                Level level = c.Level("level");
                List<XYZ> points = ModelKit.Points3(c, "points", level);
                placements.AddRange(points.Select(p => Tuple.Create(p, level, (Element)null)));
            }

            var footings = new List<FamilyInstance>();
            foreach (Tuple<XYZ, Level, Element> placement in placements)
            {
                FamilyInstance footing = doc.Create.NewFamilyInstance(placement.Item1, symbol, placement.Item2, StructuralType.Footing);
                if (footing == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create a footing at " + ModelKit.Pt(placement.Item1) + ".");
                c.Created(footing);
                footings.Add(footing);
                double height = placement.Item1.Z - ModelKit.Z(placement.Item2);
                if (Math.Abs(height) > 1e-9 && !ModelKit.TrySet(footing, BuiltInParameter.INSTANCE_ELEVATION_PARAM, height))
                {
                    ModelKit.TrySet(footing, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, height);
                }
            }
            c.EnsureRegenerated();
            c.Output("type", symbol.Id);
            c.Summary("created " + ModelKit.Count(footings.Count, "isolated footing") + " (" + Resolve.FullTypeName(symbol) + ")" +
                      (placements.Any(p => p.Item3 != null) ? " under " + ModelKit.Count(placements.Count(p => p.Item3 != null), "column") : string.Empty));
        }

        private static void WallFoundations(ChangeContext c)
        {
            Document doc = c.Doc;
            WallFoundationType type = Resolve.Type<WallFoundationType>(doc, c.Str("type"), c.Args.Field("type"));
            HashSet<long> founded = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(WallFoundation)).Cast<WallFoundation>().Select(f => f.WallId.Value));
            var created = new List<WallFoundation>();
            int skipped = 0;
            foreach (Element element in Targets(c, true))
            {
                if (!(element is Wall wall))
                {
                    c.Warn(WarningCodes.ParamIgnored, ModelKit.Describe(element) + " is not a wall and was skipped", new[] { element.Id });
                    continue;
                }
                if (founded.Contains(wall.Id.Value))
                {
                    skipped++;
                    continue;
                }
                WorksharingGuard.EnsureEditable(doc, wall);
                WallFoundation foundation = WallFoundation.Create(doc, type.Id, wall.Id);
                if (foundation == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create a wall foundation under wall " + wall.Id.Value + ".");
                c.Created(foundation);
                c.Modified(wall);
                created.Add(foundation);
            }
            if (skipped > 0) c.Warn(WarningCodes.PartialResult, skipped.ToString(CultureInfo.InvariantCulture) + " wall(s) already have a foundation and were skipped");
            if (created.Count == 0) throw OpException.InvalidArgs(c.Args.Field(c.Args.Has("ids") ? "ids" : "from"), "no wall without a foundation in the selection");
            c.EnsureRegenerated();
            c.Output("type", type.Id);
            c.Summary("created " + ModelKit.Count(created.Count, "wall foundation") + " (" + Resolve.FullTypeName(type) + ")");
        }

        private static void Slab(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            FloorType type = Resolve.Type<FloorType>(doc, c.Str("type"), c.Args.Field("type"), BuiltInCategory.OST_StructuralFoundation);
            double z = ModelKit.Z(level);
            if (!c.Args.Has("points")) throw OpException.InvalidArgs("points", "a foundation slab needs points (its outline [[x,y],...])");
            CurveLoop outline = FloorRoomOps.OrientedLoop(doc, ModelKit.FlatPoints(c, "points", z), c.Args.Field("points"), counterClockwise: true);
            Floor slab = Floor.Create(doc, new List<CurveLoop> { outline }, type.Id, level.Id, true, null, 0);
            if (slab == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the foundation slab.");
            c.Created(slab);
            c.EnsureRegenerated();
            c.Output("type", type.Id);
            c.Output("level", level.Id);
            double area = ModelKit.GetDouble(slab, BuiltInParameter.HOST_AREA_COMPUTED) ?? 0;
            c.Summary("created foundation slab (" + Resolve.FullTypeName(type) + ") on " + level.Name + ", " + Units.Number(Units.M2(area), 2).ToString() + " m2");
        }
    }
}
