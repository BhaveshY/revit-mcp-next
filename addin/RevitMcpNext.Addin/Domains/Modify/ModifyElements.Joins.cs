using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// modify_elements relations between elements: join/unjoin/switch_join (JoinGeometryUtils), cut/uncut (void
    /// instance cuts and solid-solid cuts; `with` is the cutting element), attach/detach (walls: Wall.AddAttachment,
    /// Revit 2026+ API, so min 2027 in the registry; structural columns: ColumnAttachment).
    /// </summary>
    internal static partial class ModifyElementsOps
    {
        // -------------------------------------------------------------------------------------------------------------
        // join / unjoin / switch_join
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.join")]
        public static void Join(ChangeContext c)
        {
            RunPairs(c, "joined", (doc, a, b) =>
            {
                if (JoinGeometryUtils.AreElementsJoined(doc, a, b)) return PairOutcome.Unchanged;
                JoinGeometryUtils.JoinGeometry(doc, a, b);
                return PairOutcome.Changed;
            });
        }

        [Op("modify_elements.unjoin")]
        public static void Unjoin(ChangeContext c)
        {
            RunPairs(c, "unjoined", (doc, a, b) =>
            {
                if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) return PairOutcome.Unchanged;
                JoinGeometryUtils.UnjoinGeometry(doc, a, b);
                return PairOutcome.Changed;
            });
        }

        [Op("modify_elements.switch_join")]
        public static void SwitchJoin(ChangeContext c)
        {
            RunPairs(c, "switched", (doc, a, b) =>
            {
                if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) throw new PairSkip("not joined; join them first");
                JoinGeometryUtils.SwitchJoinOrder(doc, a, b);
                return PairOutcome.Changed;
            });
        }

        // -------------------------------------------------------------------------------------------------------------
        // cut / uncut (`with` cuts `ids`)
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.cut")]
        public static void Cut(ChangeContext c)
        {
            RunPairs(c, "cut", (doc, target, cutter) =>
            {
                // A void family instance always is the cutter, whichever side it was given on.
                if (IsVoidCutter(cutter) && InstanceVoidCutUtils.CanBeCutWithVoid(target))
                {
                    if (InstanceVoidCutUtils.InstanceVoidCutExists(target, cutter)) return PairOutcome.Unchanged;
                    InstanceVoidCutUtils.AddInstanceVoidCut(doc, target, cutter);
                    return PairOutcome.Changed;
                }
                if (IsVoidCutter(target) && InstanceVoidCutUtils.CanBeCutWithVoid(cutter))
                {
                    if (InstanceVoidCutUtils.InstanceVoidCutExists(cutter, target)) return PairOutcome.Unchanged;
                    InstanceVoidCutUtils.AddInstanceVoidCut(doc, cutter, target);
                    return PairOutcome.Changed;
                }
                if (SolidSolidCutUtils.CutExistsBetweenElements(target, cutter, out bool firstCutsSecond) && !firstCutsSecond) return PairOutcome.Unchanged;
                if (!SolidSolidCutUtils.CanElementCutElement(cutter, target, out CutFailureReason reason))
                {
                    throw new PairSkip("Revit cannot cut it with " + cutter.Id.Value.ToString(CultureInfo.InvariantCulture) + " (" + reason + ")");
                }
                SolidSolidCutUtils.AddCutBetweenSolids(doc, target, cutter);
                return PairOutcome.Changed;
            });
        }

        [Op("modify_elements.uncut")]
        public static void Uncut(ChangeContext c)
        {
            RunPairs(c, "uncut", (doc, target, cutter) =>
            {
                bool removed = false;
                if (InstanceVoidCutUtils.InstanceVoidCutExists(target, cutter))
                {
                    InstanceVoidCutUtils.RemoveInstanceVoidCut(doc, target, cutter);
                    removed = true;
                }
                if (InstanceVoidCutUtils.InstanceVoidCutExists(cutter, target))
                {
                    InstanceVoidCutUtils.RemoveInstanceVoidCut(doc, cutter, target);
                    removed = true;
                }
                if (SolidSolidCutUtils.CutExistsBetweenElements(target, cutter, out _))
                {
                    SolidSolidCutUtils.RemoveCutBetweenSolids(doc, target, cutter);
                    removed = true;
                }
                return removed ? PairOutcome.Changed : PairOutcome.Unchanged;
            });
        }

        private static bool IsVoidCutter(Element element)
        {
            try
            {
                return element is FamilyInstance instance && InstanceVoidCutUtils.IsVoidInstanceCuttingElement(instance);
            }
            catch
            {
                return false;
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // attach / detach (2027)
        // -------------------------------------------------------------------------------------------------------------

        [Op("modify_elements.attach")]
        public static void Attach(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            string side = Side(a, "top");
            RunPairs(c, "attached", (doc, element, target) =>
            {
                if (element is Wall wall) return AttachWall(doc, wall, target, side);
                if (IsColumn(element)) return AttachColumn(doc, (FamilyInstance)element, target, side);
                throw new PairSkip("only walls and columns attach");
            });
        }

        [Op("modify_elements.detach")]
        public static void Detach(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            string side = Side(a, null);
            if (a.Has("with"))
            {
                RunPairs(c, "detached", (doc, element, target) =>
                {
                    if (element is Wall wall) return DetachWall(wall, target.Id, side);
                    if (IsColumn(element)) return DetachColumn((FamilyInstance)element, target.Id, side);
                    throw new PairSkip("only walls and columns attach");
                });
                return;
            }

            // Without `with`: remove every attachment on the side(s).
            List<Element> elements = ModifySupport.Targets(c, a, allowFrom: false, allowFilter: false);
            var skipped = new List<List<object>>();
            var changed = new List<Element>();
            int unchanged = 0;
            foreach (Element element in elements)
            {
                try
                {
                    PairOutcome outcome =
                        element is Wall wall ? DetachWall(wall, null, side) :
                        IsColumn(element) ? DetachColumn((FamilyInstance)element, null, side) :
                        throw new PairSkip("only walls and columns attach");
                    if (outcome == PairOutcome.Changed)
                    {
                        c.Modified(element, "detach", side ?? "both");
                        changed.Add(element);
                    }
                    else
                    {
                        unchanged++;
                    }
                }
                catch (PairSkip skip)
                {
                    skipped.Add(ModifySupport.Skip(element.Id, skip.Message));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    skipped.Add(ModifySupport.Skip(element.Id, ex.Message));
                }
            }
            FinishPairs(c, "detached", changed, unchanged, skipped);
        }

        private static string Side(PayloadReader a, string defaultValue)
        {
            string side = a.Enum("side", defaultValue, "top", "base", "hand", "facing", "wall");
            if (side != null && side != "top" && side != "base") throw OpException.InvalidArgs(a.Field("side"), "attach/detach take side top or base (hand/facing/wall are for flip)");
            return side;
        }

        private static bool IsColumn(Element element)
        {
            if (!(element is FamilyInstance)) return false;
            BuiltInCategory category = Resolve.SafeBuiltIn(element.Category);
            return category == BuiltInCategory.OST_StructuralColumns || category == BuiltInCategory.OST_Columns;
        }

        private static PairOutcome AttachWall(Document doc, Wall wall, Element target, string side)
        {
#if REVIT2026_OR_GREATER
            AttachmentLocation location = side == "base" ? AttachmentLocation.Base : AttachmentLocation.Top;
            if (wall.GetAttachmentIds(location).Contains(target.Id)) return PairOutcome.Unchanged;
            if (!Wall.IsValidTargetAttachment(doc, target.Id)) throw new PairSkip("walls attach to roofs, floors, ceilings, toposolids or other walls, not to " + (Rows.CategoryLabel(target) ?? "this element"));
            wall.AddAttachment(target.Id, location);
            return PairOutcome.Changed;
#else
            throw new OpException(ErrorCodes.UnsupportedVersion, "Attaching walls through the API needs Revit 2026 or newer.",
                new Dictionary<string, object> { ["min"] = 2027, ["year"] = RevitYear() });
#endif
        }

        private static PairOutcome DetachWall(Wall wall, ElementId target, string side)
        {
#if REVIT2026_OR_GREATER
            var locations = side == null ? new[] { AttachmentLocation.Top, AttachmentLocation.Base } : new[] { side == "base" ? AttachmentLocation.Base : AttachmentLocation.Top };
            bool removed = false;
            foreach (AttachmentLocation location in locations)
            {
                foreach (ElementId id in wall.GetAttachmentIds(location).ToList())
                {
                    if (target != null && id != target) continue;
                    wall.RemoveAttachment(id, location);
                    removed = true;
                }
            }
            return removed ? PairOutcome.Changed : PairOutcome.Unchanged;
#else
            throw new OpException(ErrorCodes.UnsupportedVersion, "Detaching walls through the API needs Revit 2026 or newer.",
                new Dictionary<string, object> { ["min"] = 2027, ["year"] = RevitYear() });
#endif
        }

        private static PairOutcome AttachColumn(Document doc, FamilyInstance column, Element target, string side)
        {
            int top = side == "base" ? 0 : 1;
            ColumnAttachment existing = ColumnAttachment.GetColumnAttachment(column, top);
            if (existing != null && existing.TargetId == target.Id) return PairOutcome.Unchanged;
            if (existing != null) ColumnAttachment.RemoveColumnAttachment(column, top);
            ColumnAttachment.AddColumnAttachment(doc, column, target, top, ColumnAttachmentCutStyle.None, ColumnAttachmentJustification.Minimum, 0);
            return PairOutcome.Changed;
        }

        private static PairOutcome DetachColumn(FamilyInstance column, ElementId target, string side)
        {
            bool removed = false;
            foreach (int top in side == null ? new[] { 1, 0 } : new[] { side == "base" ? 0 : 1 })
            {
                ColumnAttachment existing = ColumnAttachment.GetColumnAttachment(column, top);
                if (existing == null || (target != null && existing.TargetId != target)) continue;
                ColumnAttachment.RemoveColumnAttachment(column, top);
                removed = true;
            }
            return removed ? PairOutcome.Changed : PairOutcome.Unchanged;
        }

        private static int RevitYear()
        {
            return McpRuntime.Instance?.Year ?? 0;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Pair runner
        // -------------------------------------------------------------------------------------------------------------

        private enum PairOutcome
        {
            Changed,
            Unchanged
        }

        /// <summary>Thrown by a pair action to skip that pair with a reason (the op continues).</summary>
        private sealed class PairSkip : Exception
        {
            public PairSkip(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// Applies <paramref name="action"/> to every (ids[i], with[j]) pair. Pairs Revit refuses are skipped with the
        /// reason; when nothing could be done the op fails (REVIT_REFUSED / UNSUPPORTED_OP) and rolls back.
        /// </summary>
        private static void RunPairs(ChangeContext c, string verb, Func<Document, Element, Element, PairOutcome> action)
        {
            PayloadReader a = ModifySupport.Args(c);
            List<Element> elements = ModifySupport.Targets(c, a, allowFrom: false, allowFilter: false);
            List<Element> others = ModifySupport.Others(c, a, "with");
            if (others.Count == 0) throw OpException.InvalidArgs(a.Field("with"), "is required (the other element id or r# handle)");

            var skipped = new List<List<object>>();
            var changed = new List<Element>();
            var changedOthers = new List<Element>();
            int unchanged = 0;
            foreach (Element element in elements)
            {
                bool any = false;
                foreach (Element other in others)
                {
                    if (other.Id == element.Id) continue;
                    try
                    {
                        if (action(c.Doc, element, other) == PairOutcome.Changed)
                        {
                            any = true;
                            if (!changedOthers.Contains(other)) changedOthers.Add(other);
                        }
                        else
                        {
                            unchanged++;
                        }
                    }
                    catch (PairSkip skip)
                    {
                        skipped.Add(ModifySupport.Skip(element.Id, skip.Message));
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        skipped.Add(ModifySupport.Skip(element.Id, ex.Message));
                    }
                }
                if (any)
                {
                    c.Modified(element, verb, others.Select(o => o.Id.Value).ToList());
                    changed.Add(element);
                }
            }
            foreach (Element other in changedOthers) c.Modified(other);
            FinishPairs(c, verb, changed, unchanged, skipped, others);
        }

        private static void FinishPairs(ChangeContext c, string verb, List<Element> changed, int unchanged, List<List<object>> skipped, List<Element> others = null)
        {
            if (changed.Count == 0 && unchanged == 0)
            {
                string reason = skipped.Count > 0 ? Convert.ToString(skipped[0][1], CultureInfo.InvariantCulture) : "no pairs";
                throw new OpException(ErrorCodes.UnsupportedOp, "Nothing could be " + verb + ": " + reason + ".",
                    new Dictionary<string, object> { ["hint"] = reason, ["skipped"] = skipped.Take(20).Cast<object>().ToList() });
            }
            var data = new Dictionary<string, object> { [verb] = changed.Count, ["unchanged"] = unchanged };
            if (others != null) data["with"] = others.Select(o => (object)o.Id.Value).ToList();
            ModifySupport.ReportSkipped(c, data, skipped, verb);
            c.Data(data);
            string with = others == null || others.Count == 0 ? string.Empty : " with " + ModifySupport.IdList(others.Select(o => o.Id), 3);
            c.Summary(verb + " " + (changed.Count > 0 ? ModifySupport.Describe(changed) : "nothing new") + with +
                      (unchanged > 0 ? " (" + unchanged.ToString(CultureInfo.InvariantCulture) + " pair(s) already " + verb + ")" : string.Empty));
        }
    }
}
