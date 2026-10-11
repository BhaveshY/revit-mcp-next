using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// modify_elements edits of existing elements: delete (with expect_count / expect_ids guards and the full cascade
    /// recorded for the delete rule), pin/unpin, change_type, flip.
    /// </summary>
    internal static partial class ModifyElementsOps
    {
        // -------------------------------------------------------------------------------------------------------------
        // delete
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.delete")]
        public static void Delete(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a);
            ModifySupport.RefusePinned(c, a, elements, "delete");

            List<ElementId> ids = elements.Select(e => e.Id).ToList();
            foreach (Element element in elements.Where(ModifySupport.IsPinned)) element.Pinned = false;
            c.Delete(ids);

            List<long> deleteSet = c.DeletedRecords.Where(d => d.Counted).Select(d => d.Id).Distinct().OrderBy(id => id).ToList();
            CheckExpectations(c, a, deleteSet);

            int targets = c.DeletedRecords.Count(d => d.Counted && d.Target);
            int dependents = deleteSet.Count - targets;
            var byCategory = c.DeletedRecords.Where(d => d.Counted)
                .GroupBy(d => string.IsNullOrWhiteSpace(d.Category) ? "Other" : d.Category)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => (object)g.Count());
            c.Data(new Dictionary<string, object>
            {
                ["deleted"] = deleteSet.Count,
                ["targets"] = targets,
                ["dependents"] = Math.Max(0, dependents),
                ["by_category"] = byCategory
            });
            c.Summary("deleted " + ModifySupport.Count(deleteSet.Count, "element") +
                      (dependents > 0 ? " (" + targets.ToString(CultureInfo.InvariantCulture) + " targets + " + dependents.ToString(CultureInfo.InvariantCulture) + " dependents)" : string.Empty));
        }

        /// <summary>expect_count / expect_ids: abort (roll back) unless the counted delete set is exactly as expected.</summary>
        private static void CheckExpectations(ChangeContext c, PayloadReader a, List<long> deleteSet)
        {
            if (a.Has("expect_count"))
            {
                int expected = a.Int("expect_count");
                if (expected != deleteSet.Count)
                {
                    throw new OpException(ModifySupport.ExpectMismatch,
                        "The delete would remove " + ModifySupport.Count(deleteSet.Count, "element") + " (incl. dependents), not " +
                        expected.ToString(CultureInfo.InvariantCulture) + "; nothing was deleted.",
                        new Dictionary<string, object>
                        {
                            ["param"] = "expect_count",
                            ["expected"] = expected,
                            ["actual"] = deleteSet.Count,
                            ["sample"] = deleteSet.Take(20).Cast<object>().ToList()
                        });
                }
            }

            if (a.Has("expect_ids"))
            {
                var expected = new HashSet<long>(a.IdValues("expect_ids"));
                var actual = new HashSet<long>(deleteSet);
                if (!expected.SetEquals(actual))
                {
                    List<long> extra = actual.Where(id => !expected.Contains(id)).OrderBy(id => id).ToList();
                    List<long> missing = expected.Where(id => !actual.Contains(id)).OrderBy(id => id).ToList();
                    throw new OpException(ModifySupport.ExpectMismatch,
                        "The delete set differs from expect_ids (" + extra.Count.ToString(CultureInfo.InvariantCulture) + " extra, " +
                        missing.Count.ToString(CultureInfo.InvariantCulture) + " missing); nothing was deleted.",
                        new Dictionary<string, object>
                        {
                            ["param"] = "expect_ids",
                            ["expected"] = expected.Count,
                            ["actual"] = actual.Count,
                            ["extra"] = extra.Take(50).Cast<object>().ToList(),
                            ["missing"] = missing.Take(50).Cast<object>().ToList()
                        });
                }
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // pin
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.pin")]
        public static void Pin(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a);
            bool pinned = a.Bool("pinned", true);
            var skipped = new List<List<object>>();
            int changed = 0, unchanged = 0;
            foreach (Element element in elements)
            {
                bool current;
                try { current = element.Pinned; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { skipped.Add(ModifySupport.Skip(element.Id, "cannot be pinned")); continue; }
                if (current == pinned)
                {
                    unchanged++;
                    continue;
                }
                try
                {
                    element.Pinned = pinned;
                    c.Modified(element, "pinned", current);
                    changed++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    skipped.Add(ModifySupport.Skip(element.Id, ex.Message));
                }
            }
            if (changed == 0 && unchanged == 0)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit cannot " + (pinned ? "pin" : "unpin") + " these elements: " + Convert.ToString(skipped[0][1], CultureInfo.InvariantCulture),
                    new Dictionary<string, object> { ["apiMessage"] = Convert.ToString(skipped[0][1], CultureInfo.InvariantCulture) });
            }
            var data = new Dictionary<string, object> { [pinned ? "pinned" : "unpinned"] = changed, ["unchanged"] = unchanged };
            ModifySupport.ReportSkipped(c, data, skipped, pinned ? "pinned" : "unpinned");
            c.Data(data);
            c.Summary((pinned ? "pinned " : "unpinned ") + ModifySupport.Count(changed, "element") +
                      (unchanged > 0 ? " (" + unchanged.ToString(CultureInfo.InvariantCulture) + " already " + (pinned ? "pinned" : "unpinned") + ")" : string.Empty));
        }

        // -------------------------------------------------------------------------------------------------------------
        // change_type
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.change_type")]
        public static void ChangeType(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a).Where(e => !(e is ElementType)).ToList();
            if (elements.Count == 0) throw OpException.InvalidArgs(a.Field("ids"), "contains only types; change_type changes the type of placed elements");

            string value = a.ReqStr("type");
            List<BuiltInCategory> categories = elements.Select(e => Resolve.SafeBuiltIn(e.Category)).Distinct().ToList();
            BuiltInCategory? category = categories.Count == 1 && categories[0] != BuiltInCategory.INVALID ? categories[0] : (BuiltInCategory?)null;
            ElementType type;
            try
            {
                type = Resolve.Type<ElementType>(c.Doc, value, a.Field("type"), category);
            }
            catch (OpException ex) when (ex.Code == ErrorCodes.NotFound && category.HasValue)
            {
                // Name the types that actually fit the first element.
                throw OpException.NotFound(a.Field("type"), "type", value, Resolve.Closest(value, ValidTypeNames(c.Doc, elements[0])));
            }

            var skipped = new List<List<object>>();
            int changed = 0, unchanged = 0;
            foreach (Element element in elements)
            {
                ElementId current = element.GetTypeId();
                if (current == type.Id)
                {
                    unchanged++;
                    continue;
                }
                bool valid;
                try { valid = element.IsValidType(type.Id); } catch { valid = false; }
                if (!valid)
                {
                    skipped.Add(ModifySupport.Skip(element.Id, "'" + Resolve.FullTypeName(type) + "' does not fit this " + (Rows.CategoryLabel(element) ?? "element")));
                    continue;
                }
                element.ChangeTypeId(type.Id);
                c.Modified(element, "type", current.Value);
                changed++;
            }
            if (changed == 0 && unchanged == 0)
            {
                throw OpException.NotFound(a.Field("type"), "type", value, Resolve.Closest(value, ValidTypeNames(c.Doc, elements[0])));
            }
            c.Output("type", type.Id);
            var data = new Dictionary<string, object> { ["changed"] = changed, ["unchanged"] = unchanged, ["type"] = Resolve.FullTypeName(type) };
            ModifySupport.ReportSkipped(c, data, skipped, "changed");
            c.Data(data);
            c.Summary("changed " + ModifySupport.Count(changed, "element") + " to '" + Resolve.FullTypeName(type) + "'" +
                      (unchanged > 0 ? " (" + unchanged.ToString(CultureInfo.InvariantCulture) + " already of that type)" : string.Empty));
        }

        private static IEnumerable<string> ValidTypeNames(Document doc, Element element)
        {
            try
            {
                return element.GetValidTypes().Select(id => doc.GetElement(id) as ElementType).Where(t => t != null).Select(Resolve.FullTypeName).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // flip
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.flip")]
        public static void Flip(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a);
            string side = a.Enum("side", null, "hand", "facing", "wall", "top", "base");
            if (side == null) throw OpException.InvalidArgs(a.Field("side"), "is required: hand or facing (doors, windows, families) or wall (wall orientation)");
            if (side == "top" || side == "base") throw OpException.InvalidArgs(a.Field("side"), "flip takes hand, facing or wall (top/base are for attach/detach)");

            var skipped = new List<List<object>>();
            var flipped = new List<Element>();
            foreach (Element element in elements)
            {
                string reason = FlipOne(element, side);
                if (reason != null)
                {
                    skipped.Add(ModifySupport.Skip(element.Id, reason));
                    continue;
                }
                c.Modified(element, "flip", side);
                flipped.Add(element);
            }
            if (flipped.Count == 0)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "None of the elements can flip " + side + ": " + Convert.ToString(skipped[0][1], CultureInfo.InvariantCulture) + ".",
                    new Dictionary<string, object> { ["hint"] = "use side hand or facing for doors/windows/families, side wall for walls" });
            }
            var data = new Dictionary<string, object> { ["flipped"] = flipped.Count, ["side"] = side };
            ModifySupport.ReportSkipped(c, data, skipped, "flipped");
            c.Data(data);
            c.Summary("flipped " + ModifySupport.Describe(flipped) + " (" + side + ")");
        }

        /// <summary>Flips one element; returns null on success or why it cannot.</summary>
        private static string FlipOne(Element element, string side)
        {
            try
            {
                if (element is Wall wall)
                {
                    if (side == "hand") return "walls flip with side wall (or facing)";
                    wall.Flip();
                    return null;
                }
                if (element is FamilyInstance instance)
                {
                    if (side == "hand")
                    {
                        if (!instance.CanFlipHand) return "this family cannot flip hand";
                        instance.flipHand();
                        return null;
                    }
                    if (side == "facing" || side == "wall")
                    {
                        if (!instance.CanFlipFacing) return "this family cannot flip facing";
                        instance.flipFacing();
                        return null;
                    }
                }
                return "only walls and family instances can be flipped";
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                return ex.Message;
            }
        }
    }
}
