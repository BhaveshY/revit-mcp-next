using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMcpNext.Addin
{
    /// <summary>create_elements floor, ceiling, room, room_separator, area, area_boundary (D1 §4B, D3 §3.8).</summary>
    internal static class FloorRoomOps
    {
        // -------------------------------------------------------------------------------------------------------------
        // Floors and ceilings
        // -------------------------------------------------------------------------------------------------------------

        [Op("create_elements.floor")]
        public static void Floor(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            FloorType type = ModelKit.TypeOrDefault<FloorType>(c, "type", BuiltInCategory.OST_Floors, "floor");
            bool structural = c.Bool("structural");
            double? offset = c.Args.MmOpt("offset");
            double? slopeDegrees = c.Args.DegRawOpt("slope");
            double z = ModelKit.Z(level);

            var floors = new List<Floor>();
            foreach (Outline outline in Outlines(c, z))
            {
                Line arrow = null;
                double slope = 0;
                if (slopeDegrees.HasValue && Math.Abs(slopeDegrees.Value) > 1e-9)
                {
                    arrow = SlopeArrow(outline.Loops[0]);
                    slope = ModelKit.SlopeFromDegrees(slopeDegrees.Value, c.Args.Field("slope"));
                }
                Floor floor = Autodesk.Revit.DB.Floor.Create(doc, outline.Loops, type.Id, level.Id, structural, arrow, slope);
                if (floor == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the floor" + outline.Source + ".");
                c.Created(floor);
                if (offset.HasValue) ModelKit.SetOrWarn(c, floor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, offset.Value, "height offset");
                floors.Add(floor);
            }
            c.EnsureRegenerated();
            ModelKit.Outputs(c, type, level);
            double area = floors.Sum(f => ModelKit.GetDouble(f, BuiltInParameter.HOST_AREA_COMPUTED) ?? 0);
            c.Data(new Dictionary<string, object> { ["type"] = Resolve.FullTypeName(type), ["level"] = level.Name, ["area_m2"] = Units.Area(area) });
            c.Summary("created " + ModelKit.Count(floors.Count, "floor") + " (" + Resolve.FullTypeName(type) + ") on " + level.Name + ", " +
                      Units.Number(Units.M2(area), 2).ToString() + " m2" +
                      (slopeDegrees.HasValue && Math.Abs(slopeDegrees.Value) > 1e-9 ? ", slope " + slopeDegrees.Value.ToString("0.##", CultureInfo.InvariantCulture) + " deg along the first edge" : string.Empty));
        }

        [Op("create_elements.ceiling")]
        public static void Ceiling(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            CeilingType type = ModelKit.TypeOrDefault<CeilingType>(c, "type", BuiltInCategory.OST_Ceilings, "ceiling");
            double? offset = c.Args.MmOpt("offset");
            double z = ModelKit.Z(level);

            var ceilings = new List<Ceiling>();
            foreach (Outline outline in Outlines(c, z))
            {
                Ceiling ceiling = Autodesk.Revit.DB.Ceiling.Create(doc, outline.Loops, type.Id, level.Id, null, 0);
                if (ceiling == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the ceiling" + outline.Source + ".");
                c.Created(ceiling);
                if (offset.HasValue) ModelKit.SetOrWarn(c, ceiling, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM, offset.Value, "height offset");
                ceilings.Add(ceiling);
            }
            c.EnsureRegenerated();
            ModelKit.Outputs(c, type, level);
            double? height = ceilings.Count > 0 ? ModelKit.GetDouble(ceilings[0], BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM) : null;
            c.Data(new Dictionary<string, object> { ["type"] = Resolve.FullTypeName(type), ["level"] = level.Name, ["height_offset"] = height.HasValue ? Units.Mm(height.Value) : null });
            c.Summary("created " + ModelKit.Count(ceilings.Count, "ceiling") + " (" + Resolve.FullTypeName(type) + ") on " + level.Name +
                      (height.HasValue ? " at " + ModelKit.Mm(height.Value) + " mm above the level" : string.Empty));
        }

        /// <summary>One outline (outer loop + holes) per call from points, or one per room from <c>from</c>.</summary>
        private sealed class Outline
        {
            public IList<CurveLoop> Loops;
            public string Source;
        }

        private static List<Outline> Outlines(ChangeContext c, double z)
        {
            Document doc = c.Doc;
            var result = new List<Outline>();
            if (c.Args.Has("points"))
            {
                List<XYZ> points = ModelKit.FlatPoints(c, "points", z);
                var loops = new List<CurveLoop> { OrientedLoop(doc, points, c.Args.Field("points"), counterClockwise: true) };
                List<List<XYZ>> holes = ModelKit.FlatLoops(c, "holes", z);
                for (int i = 0; i < holes.Count; i++)
                {
                    loops.Add(OrientedLoop(doc, holes[i], c.Args.Field("holes") + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", counterClockwise: false));
                }
                result.Add(new Outline { Loops = loops, Source = string.Empty });
                return result;
            }
            if (!c.Args.Has("from")) throw OpException.InvalidArgs("points", "give points [[x,y],...] (outline) or from (rooms: r# handle or 'selection')");
            if (c.Args.Has("holes")) c.Warn(WarningCodes.ParamIgnored, "holes are ignored with from (room boundaries include their own holes)");
            foreach (SpatialElement room in ModelKit.ElementsOf<SpatialElement>(c, "from", "rooms"))
            {
                List<CurveLoop> loops = ModelKit.BoundaryLoops(room, z);
                if (loops.Count == 0)
                {
                    c.Warn(WarningCodes.PartialResult, ModelKit.Describe(room) + " is not enclosed (no boundary) and was skipped", new[] { room.Id });
                    continue;
                }
                result.Add(new Outline { Loops = loops, Source = " for " + ModelKit.Describe(room) });
            }
            if (result.Count == 0) throw OpException.InvalidArgs(c.Args.Field("from"), "none of the rooms has a closed boundary");
            return result;
        }

        /// <summary>A closed loop with the requested orientation (outer loops counterclockwise, holes clockwise).</summary>
        internal static CurveLoop OrientedLoop(Document doc, IList<XYZ> points, string param, bool counterClockwise)
        {
            List<XYZ> loop = Geometry.NormalizeClosedLoop(points);
            if (loop.Count < 3) throw OpException.InvalidArgs(param, "needs at least 3 distinct points for a closed outline");
            if (Geometry.IsCounterClockwise(loop) != counterClockwise) loop.Reverse();
            return ModelKit.Loop(doc, loop, param);
        }

        /// <summary>Slope arrow along the first edge of the outline (rising from its start).</summary>
        private static Line SlopeArrow(CurveLoop loop)
        {
            Curve first = loop.First();
            return Line.CreateBound(first.GetEndPoint(0), first.GetEndPoint(1));
        }

        // -------------------------------------------------------------------------------------------------------------
        // Rooms and room separators
        // -------------------------------------------------------------------------------------------------------------

        [Op("create_elements.room")]
        public static void Room(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            string name = c.Str("name");
            string number = c.Str("number");
            string department = c.Str("department");
            bool allowDuplicate = c.Bool("allow_duplicate");
            var rooms = new List<Room>();

            if (c.Bool("all"))
            {
                if (c.Args.Has("at")) c.Warn(WarningCodes.ParamIgnored, "at is ignored with all:true");
                if (!string.IsNullOrWhiteSpace(number)) c.Warn(WarningCodes.ParamIgnored, "number is ignored with all:true (Revit numbers the rooms)");
                Phase phase = ModelKit.PhaseOf(doc, ModelKit.PlanOf(doc, level));
                ICollection<ElementId> ids = phase != null ? doc.Create.NewRooms2(level, phase) : doc.Create.NewRooms2(level);
                foreach (ElementId id in ids ?? new List<ElementId>())
                {
                    if (doc.GetElement(id) is Room room) rooms.Add(room);
                }
                if (rooms.Count == 0)
                {
                    c.Warn(WarningCodes.PartialResult, "no enclosed area without a room on " + level.Name + "; nothing was created");
                }
            }
            else
            {
                XYZ at = c.Args.PointOpt("at") ?? throw OpException.InvalidArgs("at", "give at [x,y] (a point inside an enclosed area) or all:true");
                var probe = new XYZ(at.X, at.Y, ModelKit.Z(level) + Units.MmToFt(500));
                Phase phase = ModelKit.PhaseOf(doc, ModelKit.PlanOf(doc, level));
                Room existing = phase != null ? doc.GetRoomAtPoint(probe, phase) : doc.GetRoomAtPoint(probe);
                if (existing != null)
                {
                    throw OpException.InvalidArgs(c.Args.Field("at"), "room " + existing.Number + " '" + RoomName(existing) + "' (" + existing.Id.Value + ") already occupies " + ModelKit.Pt(at) +
                        "; use set_parameters to rename it or pick a point in another enclosed area");
                }
                if (!string.IsNullOrWhiteSpace(number) && !allowDuplicate) CheckNumber(c, doc, number.Trim());
                Room room = doc.Create.NewRoom(level, new UV(at.X, at.Y));
                if (room == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create a room at " + ModelKit.Pt(at) + ".");
                if (!string.IsNullOrWhiteSpace(number)) room.Number = number.Trim();
                rooms.Add(room);
            }

            foreach (Room room in rooms)
            {
                c.Created(room);
                if (!string.IsNullOrWhiteSpace(name)) room.Name = name.Trim();
                if (!string.IsNullOrWhiteSpace(department)) ModelKit.TrySet(room, BuiltInParameter.ROOM_DEPARTMENT, department.Trim());
            }
            c.EnsureRegenerated();

            var table = new TableBuilder(new[] { "id", "number", "name", "area_m2" });
            int unbounded = 0;
            foreach (Room room in rooms)
            {
                if (room.Area <= 1e-9) unbounded++;
                if (table.Rows.Count < 100) table.Add(room.Id.Value, room.Number, RoomName(room), Units.Area(room.Area));
            }
            table.Total = rooms.Count;
            if (unbounded > 0) c.Warn(WarningCodes.RevitWarning, unbounded.ToString(CultureInfo.InvariantCulture) + " room(s) are not in a properly enclosed region (area 0)", rooms.Where(r => r.Area <= 1e-9).Select(r => r.Id));
            ModelKit.Outputs(c, null, level);
            c.Data(new Dictionary<string, object> { ["rooms"] = table.ToWire() });
            c.Summary(rooms.Count == 1
                ? "created room " + rooms[0].Number + " '" + RoomName(rooms[0]) + "' on " + level.Name + ", " + Units.Number(Units.M2(rooms[0].Area), 2).ToString() + " m2"
                : "created " + ModelKit.Count(rooms.Count, "room") + " on " + level.Name);
        }

        private static string RoomName(SpatialElement room)
        {
            return room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name;
        }

        /// <summary>NAME_TAKEN with the next free number when another room already uses <paramref name="number"/>.</summary>
        private static void CheckNumber(ChangeContext c, Document doc, string number)
        {
            HashSet<string> numbers = new HashSet<string>(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Select(r => r.Number).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            if (!numbers.Contains(number)) return;
            string next = ModelKit.NextName(number, numbers);
            throw new OpException(ErrorCodes.NameTaken, "Room number '" + number + "' is already used; the next free number is '" + next + "' (or pass allow_duplicate:true).",
                new Dictionary<string, object> { ["param"] = c.Args.Field("number"), ["kind"] = "room number", ["value"] = number, ["next"] = next });
        }

        [Op("create_elements.room_separator")]
        public static void RoomSeparator(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewPlan plan = PlanView(c, ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan);
            Level level = plan.GenLevel;
            double z = ModelKit.Z(level);
            List<Curve> curves = ModelKit.Polyline(doc, ModelKit.FlatPoints(c, "points", z), c.Bool("closed"), c.Args.Field("points"));
            SketchPlane plane = ModelKit.HorizontalPlane(doc, level, z);
            ModelCurveArray lines = doc.Create.NewRoomBoundaryLines(plane, ModelKit.ToArray(curves), plan);
            int count = 0;
            foreach (ModelCurve line in lines)
            {
                c.Created(line);
                count++;
            }
            ModelKit.Outputs(c, null, level);
            c.Summary("created " + ModelKit.Count(count, "room separation line") + " on " + level.Name + " (view '" + plan.Name + "')");
        }

        // -------------------------------------------------------------------------------------------------------------
        // Areas
        // -------------------------------------------------------------------------------------------------------------

        [Op("create_elements.area")]
        public static void Area(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewPlan plan = PlanView(c, ViewType.AreaPlan);
            XYZ at = c.Point("at");
            Area area = doc.Create.NewArea(plan, new UV(at.X, at.Y));
            if (area == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create an area at " + ModelKit.Pt(at) + ".");
            c.Created(area);
            string name = c.Str("name");
            string number = c.Str("number");
            if (!string.IsNullOrWhiteSpace(name)) area.Name = name.Trim();
            if (!string.IsNullOrWhiteSpace(number)) area.Number = number.Trim();
            c.EnsureRegenerated();
            if (area.Area <= 1e-9) c.Warn(WarningCodes.RevitWarning, "the area is not enclosed by area boundary lines (area 0); draw them with op area_boundary", new[] { area.Id });
            c.Output("view", plan.Id);
            c.Data(new Dictionary<string, object> { ["id"] = area.Id.Value, ["number"] = area.Number, ["name"] = RoomName(area), ["area_m2"] = Units.Area(area.Area) });
            c.Summary("created area " + area.Number + " '" + RoomName(area) + "' in '" + plan.Name + "', " + Units.Number(Units.M2(area.Area), 2).ToString() + " m2");
        }

        [Op("create_elements.area_boundary")]
        public static void AreaBoundary(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewPlan plan = PlanView(c, ViewType.AreaPlan);
            Level level = plan.GenLevel;
            double z = ModelKit.Z(level);
            List<Curve> curves = ModelKit.Polyline(doc, ModelKit.FlatPoints(c, "points", z), c.Bool("closed"), c.Args.Field("points"));
            SketchPlane plane = ModelKit.HorizontalPlane(doc, level, z);
            foreach (Curve curve in curves)
            {
                ModelCurve line = doc.Create.NewAreaBoundaryLine(plane, curve, plan);
                if (line == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create an area boundary line.");
                c.Created(line);
            }
            c.Output("view", plan.Id);
            c.Summary("created " + ModelKit.Count(curves.Count, "area boundary line") + " in '" + plan.Name + "'");
        }

        /// <summary>The view param as a non-template plan of one of the given types.</summary>
        private static ViewPlan PlanView(ChangeContext c, params ViewType[] types)
        {
            View view = c.View("view");
            if (view is ViewPlan plan && !plan.IsTemplate && types.Contains(plan.ViewType) && plan.GenLevel != null) return plan;
            string wanted = types.Length == 1 && types[0] == ViewType.AreaPlan ? "an area plan" : "a floor or ceiling plan";
            throw OpException.InvalidArgs(c.Args.Field("view"), "must be " + wanted + " ('" + view.Name + "' is " + view.ViewType + ")");
        }
    }
}
