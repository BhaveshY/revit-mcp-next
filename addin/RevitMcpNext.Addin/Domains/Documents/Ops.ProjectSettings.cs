using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// manage_document.purge | set_units | coordinates: write ops run by the ChangeEngine (one transaction each,
    /// allowed in change_set). purge deletes through ChangeContext.Delete so the delete blast rule (with categories)
    /// asks for the user's OK above confirm.deleteOver; coordinates is always confirmed first by the broker.
    /// </summary>
    internal static class DocumentSettingsOps
    {
        [Op("manage_document.purge")]
        public static void Purge(ChangeContext c)
        {
            Document doc = c.Doc;
            int passes = c.Args.IntClamped("passes", 3, 1, 3, (code, text) => c.Warn(code, text));
            var perPass = new List<int>();
            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            int skipped = 0;
            for (int pass = 0; pass < passes; pass++)
            {
                List<ElementId> candidates = PurgeUtil.Candidates(doc)
                    .Where(id => Resolve.SafeGet(doc, id) != null)
                    .ToList();
                if (candidates.Count == 0) break;
                foreach (KeyValuePair<string, int> kind in PurgeUtil.ByKind(doc, candidates))
                {
                    byKind.TryGetValue(kind.Key, out int count);
                    byKind[kind.Key] = count + kind.Value;
                }
                int deleted = DeleteRobust(c, candidates, ref skipped);
                perPass.Add(deleted);
                if (deleted == 0) break;
                c.EnsureRegenerated();
            }

            int total = perPass.Sum();
            if (skipped > 0) c.Warn(WarningCodes.PartialResult, skipped.ToString(CultureInfo.InvariantCulture) + " unused element(s) could not be deleted and were kept");
            c.Data(new Dictionary<string, object>
            {
                ["purged"] = total,
                ["passes"] = perPass.Cast<object>().ToList(),
                ["by_kind"] = byKind.ToDictionary(pair => pair.Key, pair => (object)pair.Value)
            });
            c.Summary(total == 0
                ? "nothing to purge: no unused families, types or materials"
                : "purged " + total.ToString(CultureInfo.InvariantCulture) + " unused element(s) in " + perPass.Count.ToString(CultureInfo.InvariantCulture) + " pass(es)");
        }

        /// <summary>Deletes the candidates in one call, else one by one (an element can vanish with its family).</summary>
        private static int DeleteRobust(ChangeContext c, List<ElementId> candidates, ref int skipped)
        {
            Document doc = c.Doc;
            int before = c.DeletedRecords.Count;
            try
            {
                c.Delete(candidates);
                return c.DeletedRecords.Count - before;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            {
                // Fall back to single deletes below.
            }
            foreach (ElementId id in candidates)
            {
                if (Resolve.SafeGet(doc, id) == null) continue;
                try
                {
                    c.Delete(new List<ElementId> { id });
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                {
                    skipped++;
                }
            }
            return c.DeletedRecords.Count - before;
        }

        [Op("manage_document.set_units")]
        public static void SetUnits(ChangeContext c)
        {
            Document doc = c.Doc;
            string unit = c.Args.Enum("unit", null, "mm", "cm", "m", "in", "ft");
            if (unit == null)
            {
                throw OpException.InvalidArgs("unit", "is required (mm, cm, m, in or ft)", new Dictionary<string, object> { ["op"] = "set_units", ["unit"] = "mm", ["accuracy"] = 1 });
            }
            ForgeTypeId unitId = UnitIdFor(unit);
            Autodesk.Revit.DB.Units units = doc.GetUnits();
            FormatOptions current = units.GetFormatOptions(SpecTypeId.Length);
            string before = DescribeLength(current);

            var options = new FormatOptions(unitId);
            double accuracy = c.Args.NumOpt("accuracy") ?? DefaultAccuracy(unit, current);
            if (!options.IsValidAccuracy(accuracy))
            {
                throw OpException.InvalidArgs("accuracy", accuracy.ToString(CultureInfo.InvariantCulture) + " is not a valid rounding for " + unit + " (use e.g. " + DefaultAccuracy(unit, null).ToString(CultureInfo.InvariantCulture) + ")",
                    new Dictionary<string, object> { ["op"] = "set_units", ["unit"] = unit, ["accuracy"] = DefaultAccuracy(unit, null) });
            }
            options.Accuracy = accuracy;
            try
            {
                ForgeTypeId symbol = SymbolFor(options, unitId);
                if (symbol != null) options.SetSymbolTypeId(symbol);
            }
            catch
            {
                // Keep Revit's default symbol.
            }
            units.SetFormatOptions(SpecTypeId.Length, options);
            doc.SetUnits(units);
            c.MarkDirty();
            try
            {
                ProjectInfo info = doc.ProjectInformation;
                if (info != null) c.Modified(info.Id);
            }
            catch
            {
                // Units have no element of their own; ProjectInformation stands in for the change record.
            }
            string after = DescribeLength(options);
            c.Data(new Dictionary<string, object> { ["length"] = after, ["before"] = before, ["unit"] = unit, ["accuracy"] = accuracy });
            c.Summary("project length unit is now " + after + " (was " + before + ")");
        }

        private static ForgeTypeId UnitIdFor(string unit)
        {
            switch (unit)
            {
                case "cm": return UnitTypeId.Centimeters;
                case "m": return UnitTypeId.Meters;
                case "in": return UnitTypeId.Inches;
                case "ft": return UnitTypeId.Feet;
                default: return UnitTypeId.Millimeters;
            }
        }

        private static double DefaultAccuracy(string unit, FormatOptions current)
        {
            switch (unit)
            {
                case "mm": return 1;
                case "cm": return 0.1;
                case "m": return 0.001;
                case "in": return 0.01;
                default: return 0.01;
            }
        }

        private static ForgeTypeId SymbolFor(FormatOptions options, ForgeTypeId unitId)
        {
            IList<ForgeTypeId> symbols = FormatOptions.GetValidSymbols(unitId);
            return symbols?.FirstOrDefault(s => s != null && !s.Empty());
        }

        private static string DescribeLength(FormatOptions options)
        {
            try
            {
                if (options.UseDefault) return "default";
                string label = LabelUtils.GetLabelForUnit(options.GetUnitTypeId());
                return label + " (" + options.Accuracy.ToString(CultureInfo.InvariantCulture) + ")";
            }
            catch
            {
                return "unknown";
            }
        }

        [Op("manage_document.coordinates")]
        public static void Coordinates(ChangeContext c)
        {
            Document doc = c.Doc;
            bool hasBase = c.Has("base_point");
            bool hasSurvey = c.Has("survey_point");
            bool hasNorth = c.Has("true_north");
            if (!hasBase && !hasSurvey && !hasNorth)
            {
                throw OpException.InvalidArgs("base_point", "give base_point, survey_point or true_north",
                    new Dictionary<string, object> { ["op"] = "coordinates", ["true_north"] = 12.5 });
            }

            BasePoint project = BasePoint.GetProjectBasePoint(doc);
            BasePoint survey = BasePoint.GetSurveyPoint(doc);
            if (project == null || survey == null) throw new OpException(ErrorCodes.UnsupportedOp, "'" + doc.Title + "' has no project base point or survey point.");
            var before = new Dictionary<string, object> { ["base_point"] = Shared(project), ["survey_point"] = Shared(survey), ["true_north"] = North(project) };

            // The values are the shared coordinates shown in Revit's properties (E/W = x, N/S = y, Elev = z), in mm.
            if (hasSurvey)
            {
                WorksharingGuard.EnsureEditable(doc, survey);
                SetShared(c, survey, c.Point("survey_point", 0), "survey_point");
            }
            if (hasBase)
            {
                WorksharingGuard.EnsureEditable(doc, project);
                SetShared(c, project, c.Point("base_point", 0), "base_point");
            }
            if (hasNorth)
            {
                WorksharingGuard.EnsureEditable(doc, project);
                double radians = c.Deg("true_north");
                SetParam(project, BuiltInParameter.BASEPOINT_ANGLETON_PARAM, radians, "true_north");
                c.Modified(project, "true_north", before["true_north"]);
            }
            c.EnsureRegenerated();
            var after = new Dictionary<string, object> { ["base_point"] = Shared(project), ["survey_point"] = Shared(survey), ["true_north"] = North(project) };
            c.Data(new Dictionary<string, object> { ["before"] = before, ["after"] = after });
            var parts = new List<string>();
            if (hasBase) parts.Add("project base point " + Text(after["base_point"]));
            if (hasSurvey) parts.Add("survey point " + Text(after["survey_point"]));
            if (hasNorth) parts.Add("true north " + Convert.ToString(after["true_north"], CultureInfo.InvariantCulture) + "°");
            c.Summary("set " + string.Join(", ", parts));
        }

        private static void SetShared(ChangeContext c, BasePoint point, XYZ value, string field)
        {
            object before = Shared(point);
            SetParam(point, BuiltInParameter.BASEPOINT_EASTWEST_PARAM, value.X, field);
            SetParam(point, BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM, value.Y, field);
            SetParam(point, BuiltInParameter.BASEPOINT_ELEVATION_PARAM, value.Z, field);
            c.Modified(point, field, before);
        }

        private static void SetParam(Element element, BuiltInParameter bip, double value, string field)
        {
            Parameter parameter = element.get_Parameter(bip);
            if (parameter == null || parameter.IsReadOnly)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, field + ": Revit does not allow changing " + bip + " on this " + (element.Category?.Name ?? "point") + " (it may be clipped or pinned).",
                    new Dictionary<string, object> { ["param"] = field, ["parameter"] = bip.ToString() });
            }
            if (!parameter.Set(value))
            {
                throw new OpException(ErrorCodes.RevitRefused, field + ": Revit refused the value for " + bip + ".",
                    new Dictionary<string, object> { ["param"] = field, ["apiMessage"] = "Parameter.Set returned false" });
            }
        }

        private static List<object> Shared(BasePoint point)
        {
            double Read(BuiltInParameter bip)
            {
                try { return point.get_Parameter(bip)?.AsDouble() ?? 0; } catch { return 0; }
            }
            return new List<object>
            {
                Units.Mm(Read(BuiltInParameter.BASEPOINT_EASTWEST_PARAM)),
                Units.Mm(Read(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM)),
                Units.Mm(Read(BuiltInParameter.BASEPOINT_ELEVATION_PARAM))
            };
        }

        private static object North(BasePoint project)
        {
            try { return Units.Angle(project.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM)?.AsDouble() ?? 0); } catch { return null; }
        }

        private static string Text(object point)
        {
            return point is List<object> list
                ? "[" + string.Join(",", list.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))) + "] mm"
                : string.Empty;
        }
    }
}
