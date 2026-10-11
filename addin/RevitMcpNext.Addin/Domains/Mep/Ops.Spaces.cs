using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;

namespace RevitMcpNext.Addin
{
    /// <summary>mep space and zone (D1 §4B, D3 §3.8).</summary>
    internal static class MepSpaceOps
    {
        [Op("mep.space")]
        public static void CreateSpace(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            string name = c.Str("name");
            string number = c.Str("number");
            ViewPlan plan = ModelKit.PlanOf(doc, level);
            Phase phase = ModelKit.PhaseOf(doc, plan);
            var spaces = new List<Space>();

            if (c.Bool("all"))
            {
                if (c.Args.Has("at")) c.Warn(WarningCodes.ParamIgnored, "at is ignored with all:true");
                if (!string.IsNullOrWhiteSpace(number)) c.Warn(WarningCodes.ParamIgnored, "number is ignored with all:true (Revit numbers the spaces)");
                if (plan == null) throw new OpException(ErrorCodes.NotFound, "Level '" + level.Name + "' has no floor plan; create one (create_elements level with plans) first.",
                    new Dictionary<string, object> { ["param"] = c.Args.Field("level"), ["kind"] = "floor plan", ["value"] = level.Name, ["candidates"] = new List<string>() });
                ICollection<ElementId> ids = doc.Create.NewSpaces2(level, phase, plan);
                foreach (ElementId id in ids ?? new List<ElementId>())
                {
                    if (doc.GetElement(id) is Space space) spaces.Add(space);
                }
                if (spaces.Count == 0) c.Warn(WarningCodes.PartialResult, "no enclosed area without a space on " + level.Name + "; nothing was created");
            }
            else
            {
                XYZ at = c.Args.PointOpt("at") ?? throw OpException.InvalidArgs("at", "give at [x,y] (a point inside an enclosed area) or all:true");
                var probe = new XYZ(at.X, at.Y, ModelKit.Z(level) + Units.MmToFt(500));
                Space existing = phase != null ? doc.GetSpaceAtPoint(probe, phase) : doc.GetSpaceAtPoint(probe);
                if (existing != null)
                {
                    throw OpException.InvalidArgs(c.Args.Field("at"), "space " + existing.Number + " (" + existing.Id.Value + ") already occupies " + ModelKit.Pt(at));
                }
                Space space = phase != null ? doc.Create.NewSpace(level, phase, new UV(at.X, at.Y)) : doc.Create.NewSpace(level, new UV(at.X, at.Y));
                if (space == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create a space at " + ModelKit.Pt(at) + ".");
                if (!string.IsNullOrWhiteSpace(number)) space.Number = number.Trim();
                spaces.Add(space);
            }

            foreach (Space space in spaces)
            {
                c.Created(space);
                if (!string.IsNullOrWhiteSpace(name)) space.Name = name.Trim();
            }
            c.EnsureRegenerated();
            var table = new TableBuilder(new[] { "id", "number", "name", "area_m2" });
            foreach (Space space in spaces.Take(100)) table.Add(space.Id.Value, space.Number, space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? space.Name, Units.Area(space.Area));
            table.Total = spaces.Count;
            List<Space> unbounded = spaces.Where(s => s.Area <= 1e-9).ToList();
            if (unbounded.Count > 0) c.Warn(WarningCodes.RevitWarning, unbounded.Count.ToString(CultureInfo.InvariantCulture) + " space(s) are not in a properly enclosed region (area 0)", unbounded.Select(s => s.Id));
            c.Output("level", level.Id);
            c.Data(new Dictionary<string, object> { ["spaces"] = table.ToWire() });
            c.Summary("created " + ModelKit.Count(spaces.Count, "space") + " on " + level.Name);
        }

        [Op("mep.zone")]
        public static void CreateZone(ChangeContext c)
        {
            Document doc = c.Doc;
            string name = c.Str("name") ?? throw OpException.InvalidArgs(c.Args.Field("name"), "is required (the zone name)");
            List<Space> spaces = ModelKit.ElementsOf<Space>(c, "ids", "spaces");
            Level level = c.Level("level", required: false) ?? spaces.Select(s => s.Level).FirstOrDefault(l => l != null) ?? FamilyPlacementOps.DefaultLevel(c, null);
            Phase phase = null;
            try { phase = spaces[0].Document.GetElement(spaces[0].get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase; } catch { }
            phase = phase ?? ModelKit.PhaseOf(doc, ModelKit.PlanOf(doc, level));

#if REVIT2027
            // Revit 2026+ replaced HVAC zones with space-based system-zones (GenericZone + SystemZoneData).
            HashSet<string> taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Analysis.GenericZone)).Select(z => z.Name).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
#else
            HashSet<string> taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Zone)).Cast<Zone>().Select(z => z.Name).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
#endif
            if (taken.Contains(name.Trim()))
            {
                string next = ModelKit.NextName(name.Trim(), taken);
                throw new OpException(ErrorCodes.NameTaken, "A zone named '" + name.Trim() + "' already exists; the next free name is '" + next + "'.",
                    new Dictionary<string, object> { ["param"] = c.Args.Field("name"), ["kind"] = "zone", ["value"] = name.Trim(), ["next"] = next });
            }
            foreach (Space space in spaces) WorksharingGuard.EnsureEditable(doc, space);

#if REVIT2027
            List<Space> offLevel = spaces.Where(s => s.LevelId != level.Id).ToList();
            if (offLevel.Count > 0)
            {
                throw OpException.InvalidArgs(c.Args.Field("ids"), ModelKit.Count(offLevel.Count, "space") + " are not on " + level.Name + " (Revit " + c.Ctx.Year + " zones hold spaces of one level)");
            }
            SystemZoneElementType zoneType = new FilteredElementCollector(doc).OfClass(typeof(SystemZoneElementType)).Cast<SystemZoneElementType>()
                .FirstOrDefault(t => t.GeometricDefinition == Autodesk.Revit.DB.Analysis.ZoneGeometricDefinition.Spaces);
            if (zoneType == null)
            {
                HashSet<string> typeNames = ModelKit.NamesOf<SystemZoneElementType>(doc, t => t.Name);
                string typeName = typeNames.Contains("Space Zone") ? ModelKit.NextName("Space Zone", typeNames) : "Space Zone";
                zoneType = SystemZoneElementType.CreateSpaceBased(doc, typeName);
                c.Created(zoneType);
            }
            Autodesk.Revit.DB.Analysis.GenericZone zone = Autodesk.Revit.DB.Analysis.GenericZone.CreateSpaceBased(doc, zoneType.Id, name.Trim(), SystemZoneData.Create(), level.Id,
                new HashSet<ElementId>(spaces.Select(s => s.Id)));
            if (zone == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the zone.");
            c.Created(zone);
#else
            Zone zone = doc.Create.NewZone(level, phase);
            if (zone == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the zone.");
            c.Created(zone);
            zone.Name = name.Trim();
            var set = new SpaceSet();
            foreach (Space space in spaces) set.Insert(space);
            zone.AddSpaces(set);
#endif
            c.Modified(spaces.Select(s => s.Id));
            c.EnsureRegenerated();
            c.Output("zone", zone.Id);
            c.Summary("created zone '" + zone.Name + "' on " + level.Name + " with " + ModelKit.Count(spaces.Count, "space"));
        }
    }
}
