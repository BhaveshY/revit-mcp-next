using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>modify_elements groups: group (optionally named), ungroup, place_group (model groups by type name).</summary>
    internal static partial class ModifyElementsOps
    {
        [Op("modify_elements.group")]
        public static void Group(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a);
            List<Element> grouped = elements.Where(e => e.GroupId != null && e.GroupId != ElementId.InvalidElementId).ToList();
            if (grouped.Count > 0)
            {
                throw OpException.InvalidArgs(a.Field("ids"), ModifySupport.Count(grouped.Count, "element") + " already belong to a group (" +
                    ModifySupport.IdList(grouped.Select(e => e.Id)) + "); ungroup them first or leave them out");
            }
            List<Element> invalid = elements.Where(e => e is ElementType || e is View).ToList();
            if (invalid.Count > 0) throw OpException.InvalidArgs(a.Field("ids"), "types and views cannot be grouped (" + ModifySupport.IdList(invalid.Select(e => e.Id)) + ")");

            string name = a.Str("name");
            if (name != null) EnsureGroupNameFree(c.Doc, a, name.Trim());

            Group group = c.Doc.Create.NewGroup(elements.Select(e => e.Id).ToList());
            if (group == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the group.", new Dictionary<string, object> { ["apiMessage"] = "NewGroup returned null" });
            if (name != null) group.GroupType.Name = name.Trim();
            c.Created(group);
            foreach (Element element in elements) c.Modified(element);
            c.Output("group", group.Id);
            c.Output("type", group.GroupType.Id);
            c.Data(new Dictionary<string, object> { ["group"] = group.Id.Value, ["name"] = group.GroupType.Name, ["members"] = elements.Count });
            c.Summary("grouped " + ModifySupport.Describe(elements) + " as '" + group.GroupType.Name + "' (group " + group.Id.Value.ToString(CultureInfo.InvariantCulture) + ")");
        }

        [Op("modify_elements.ungroup")]
        public static void Ungroup(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a, allowFilter: false);
            var groups = new List<Group>();
            var skipped = new List<List<object>>();
            int viaMember = 0;
            foreach (Element element in elements)
            {
                Group group = element as Group;
                if (group == null && element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
                {
                    // A member was given: ungroup the group it belongs to.
                    group = c.Doc.GetElement(element.GroupId) as Group;
                    if (group != null) viaMember++;
                }
                if (group == null)
                {
                    skipped.Add(ModifySupport.Skip(element.Id, "not a group"));
                    continue;
                }
                if (!groups.Any(g => g.Id == group.Id)) groups.Add(group);
            }
            if (groups.Count == 0) throw OpException.InvalidArgs(a.Field("ids"), "contains no groups (find them with find_elements {category:[\"Model Groups\"]})");

            var members = new List<ElementId>();
            var names = new List<string>();
            foreach (Group group in groups)
            {
                names.Add(group.GroupType?.Name ?? group.Name);
                ElementId groupId = group.Id;
                ICollection<ElementId> released = group.UngroupMembers();
                c.Deleted(new List<ElementId> { groupId });
                members.AddRange(released);
            }
            c.Modified(members);
            var data = new Dictionary<string, object> { ["ungrouped"] = groups.Count, ["members"] = members.Count, ["names"] = names.Distinct().Cast<object>().ToList() };
            if (viaMember > 0) data["via_members"] = viaMember;
            ModifySupport.ReportSkipped(c, data, skipped, "ungrouped");
            c.Data(data);
            c.Summary("ungrouped " + ModifySupport.Count(groups.Count, "group") + " (" + string.Join(", ", names.Distinct().Take(5)) + "); " + ModifySupport.Count(members.Count, "member") + " released");
        }

        [Op("modify_elements.place_group")]
        public static void PlaceGroup(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            string name = a.ReqStr("name");
            List<GroupType> types = new FilteredElementCollector(c.Doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
            List<GroupType> model = types.Where(t => Resolve.SafeBuiltIn(t.Category) == BuiltInCategory.OST_IOSModelGroups).ToList();
            GroupType type = Resolve.ById<GroupType>(c.Doc, name);
            if (type == null)
            {
                List<GroupType> matches = model.Where(t => Resolve.Eq(t.Name, name)).ToList();
                if (matches.Count == 0 && types.Any(t => Resolve.Eq(t.Name, name)))
                {
                    throw new OpException(ErrorCodes.UnsupportedOp, "'" + name + "' is a detail group; the API places model groups only.",
                        new Dictionary<string, object> { ["hint"] = "place detail groups in Revit, or copy an existing instance with modify_elements copy" });
                }
                type = Resolve.Single(model, t => t.Name, name, a.Field("name"), "model group");
            }
            if (Resolve.SafeBuiltIn(type.Category) != BuiltInCategory.OST_IOSModelGroups)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + type.Name + "' is not a model group; the API places model groups only.", new Dictionary<string, object> { ["hint"] = "give a model group name" });
            }

            Level level = c.Level("level", required: false);
            XYZ at = a.PointOpt("at", 0, out bool hasZ) ?? throw OpException.InvalidArgs(a.Field("at"), "is required (insertion point [x,y(,z)] mm)");
            if (level != null) at = new XYZ(at.X, at.Y, level.ProjectElevation + (hasZ ? at.Z : 0));

            Group group = c.Doc.Create.PlaceGroup(at, type);
            if (group == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not place group '" + type.Name + "'.", new Dictionary<string, object> { ["apiMessage"] = "PlaceGroup returned null" });
            if (level != null) TrySetGroupLevel(c, group, level);
            c.Created(group);
            try { c.Created(group.GetMemberIds()); } catch { }
            c.Output("group", group.Id);
            c.Data(new Dictionary<string, object> { ["group"] = group.Id.Value, ["name"] = type.Name, ["at"] = ModifySupport.PointMm(at), ["level"] = level?.Name });
            c.Summary("placed group '" + type.Name + "' at " + VectorText(ModifySupport.PointMm(at)) + (level != null ? " on " + level.Name : string.Empty) +
                      " (group " + group.Id.Value.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>Associates a placed group with the level (Reference Level) when Revit allows it; the position is kept.</summary>
        private static void TrySetGroupLevel(ChangeContext c, Group group, Level level)
        {
            try
            {
                Parameter reference = group.get_Parameter(BuiltInParameter.GROUP_LEVEL);
                if (reference == null || reference.IsReadOnly || reference.AsElementId() == level.Id) return;
                double z = (group.Location as LocationPoint)?.Point.Z ?? level.ProjectElevation;
                reference.Set(level.Id);
                Parameter offset = group.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL);
                if (offset != null && !offset.IsReadOnly) offset.Set(z - level.ProjectElevation);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                c.Warn(WarningCodes.PartialResult, "group placed at the right height but its reference level stays unchanged: " + ex.Message, new[] { group.Id });
            }
        }

        private static void EnsureGroupNameFree(Document doc, PayloadReader a, string name)
        {
            List<string> names = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Select(t => t.Name).ToList();
            if (!names.Any(n => Resolve.Eq(n, name))) return;
            throw new OpException(ErrorCodes.NameTaken, "A group named '" + name + "' already exists.",
                new Dictionary<string, object> { ["param"] = a.Field("name"), ["next"] = NextFreeName(name, names) });
        }

        /// <summary>"Name 2", "Name 3", ... the first not in <paramref name="taken"/>.</summary>
        internal static string NextFreeName(string name, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            for (int n = 2; n < 10000; n++)
            {
                string candidate = name + " " + n.ToString(CultureInfo.InvariantCulture);
                if (!set.Contains(candidate)) return candidate;
            }
            return name + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
        }
    }
}
