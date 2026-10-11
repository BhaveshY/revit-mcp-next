using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpNext.Addin
{
    /// <summary>mep connect, system_create, system_add, system_remove, circuit, insulate (D1 §4B, D3 §3.8).</summary>
    internal static class MepSystemOps
    {
        // -------------------------------------------------------------------------------------------------------------
        // connect
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>Connects consecutive elements of ids at their closest free connectors (coincident: joined; else a fitting).</summary>
        [Op("mep.connect")]
        public static void Connect(ChangeContext c)
        {
            Document doc = c.Doc;
            IList<Element> elements = c.Elements("ids");
            if (elements.Count < 2) throw OpException.InvalidArgs(c.Args.Field("ids"), "needs at least 2 elements with connectors (connected in order)");
            var joined = new List<string>();
            var fittings = new List<FamilyInstance>();
            for (int i = 0; i + 1 < elements.Count; i++)
            {
                Element a = elements[i], b = elements[i + 1];
                WorksharingGuard.EnsureEditable(doc, a);
                WorksharingGuard.EnsureEditable(doc, b);
                Tuple<Connector, Connector> pair = MepKit.ClosestPair(a, b);
                if (pair == null)
                {
                    throw OpException.InvalidArgs(c.Args.Field("ids"), ModelKit.Describe(a) + " and " + ModelKit.Describe(b) + " have no free connectors of the same domain");
                }
                double gap = pair.Item1.Origin.DistanceTo(pair.Item2.Origin);
                bool runs = a is MEPCurve && b is MEPCurve;
                if (gap <= MepKit.Coincident && (!runs || (MepKit.Aligned(pair.Item1, pair.Item2) && MepKit.SameSize(pair.Item1, pair.Item2))))
                {
                    pair.Item1.ConnectTo(pair.Item2);
                    joined.Add(a.Id.Value + "-" + b.Id.Value);
                }
                else if (runs)
                {
                    FamilyInstance fitting = MepKit.TryFitting(doc, pair.Item1, pair.Item2, out string error);
                    if (fitting == null)
                    {
                        throw new OpException(ErrorCodes.RevitRefused, "Revit could not add a fitting between " + ModelKit.Describe(a) + " and " + ModelKit.Describe(b) + ": " + error,
                            new Dictionary<string, object> { ["apiMessage"] = error, ["gap"] = Units.Mm(gap) });
                    }
                    c.Created(fitting);
                    fittings.Add(fitting);
                    joined.Add(a.Id.Value + "-" + b.Id.Value + " (fitting " + fitting.Id.Value + ")");
                }
                else
                {
                    throw OpException.InvalidArgs(c.Args.Field("ids"), "the closest free connectors of " + ModelKit.Describe(a) + " and " + ModelKit.Describe(b) + " are " + ModelKit.Mm(gap) +
                        " mm apart; move the run end onto the connector (modify_elements) and connect again");
                }
                c.Modified(a);
                c.Modified(b);
            }
            c.EnsureRegenerated();
            c.Data(new Dictionary<string, object> { ["joined"] = joined.Cast<object>().ToList(), ["fittings"] = fittings.Select(f => (object)f.Id.Value).ToList() });
            c.Summary("connected " + ModelKit.Count(joined.Count, "pair") + (fittings.Count > 0 ? " with " + ModelKit.Count(fittings.Count, "fitting") : string.Empty) + ": " + MepKit.Joined(joined));
        }

        // -------------------------------------------------------------------------------------------------------------
        // Systems
        // -------------------------------------------------------------------------------------------------------------

        [Op("mep.system_create")]
        public static void SystemCreate(ChangeContext c)
        {
            Document doc = c.Doc;
            string kind = c.Args.Enum("kind", null, "piping", "duct", "electrical") ?? throw OpException.InvalidArgs(c.Args.Field("kind"), "is required (piping, duct or electrical)");
            IList<Element> elements = c.Elements("ids");
            string name = c.Str("name");

            if (kind == "electrical")
            {
                ElectricalSystem circuit = CreateCircuit(c, elements, null);
                if (!string.IsNullOrWhiteSpace(name)) ModelKit.TrySet(circuit, BuiltInParameter.RBS_ELEC_CIRCUIT_NAME, name.Trim());
                c.Summary("created electrical circuit " + circuit.Name + " with " + ModelKit.Count(elements.Count, "element"));
                return;
            }

            Domain domain = kind == "piping" ? Domain.DomainPiping : Domain.DomainHvac;
            ConnectorSet connectors = FreeSystemConnectors(c, elements, domain, out List<Element> members);
            MEPSystem system;
            if (kind == "piping")
            {
                PipingSystemType type = TypeArg<PipingSystemType>(c, "type", "piping", t => t.SystemClassification == MEPSystemClassification.DomesticColdWater);
                PipingSystem piping = PipingSystem.Create(doc, type.Id, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
                piping.Add(connectors);
                system = piping;
            }
            else
            {
                MechanicalSystemType type = TypeArg<MechanicalSystemType>(c, "type", "duct", t => t.SystemClassification == MEPSystemClassification.SupplyAir);
                MechanicalSystem mechanical = MechanicalSystem.Create(doc, type.Id, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
                mechanical.Add(connectors);
                system = mechanical;
            }
            if (system == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the " + kind + " system.");
            c.Created(system);
            c.Modified(members.Select(m => m.Id));
            c.EnsureRegenerated();
            c.Output("system", system.Id);
            c.Data(new Dictionary<string, object> { ["system"] = system.Name, ["id"] = system.Id.Value, ["members"] = members.Select(m => (object)m.Id.Value).ToList() });
            c.Summary("created " + kind + " system '" + system.Name + "' (" + (doc.GetElement(system.GetTypeId())?.Name ?? "?") + ") with " + ModelKit.Count(members.Count, "element"));
        }

        [Op("mep.system_add")]
        public static void SystemAdd(ChangeContext c)
        {
            Document doc = c.Doc;
            MEPSystem system = MepKit.System(c, "system");
            IList<Element> elements = c.Elements("ids");
            WorksharingGuard.EnsureEditable(doc, system);
            if (system is ElectricalSystem circuit)
            {
                var set = new ElementSet();
                foreach (Element element in elements) set.Insert(element);
                circuit.AddToCircuit(set);
                c.Modified(system);
                c.Modified(elements.Select(e => e.Id));
                c.Summary("added " + ModelKit.Count(elements.Count, "element") + " to circuit " + circuit.Name);
                return;
            }
            Domain domain = system is PipingSystem ? Domain.DomainPiping : Domain.DomainHvac;
            ConnectorSet connectors = FreeSystemConnectors(c, elements, domain, out List<Element> members);
            if (system is PipingSystem piping) piping.Add(connectors);
            else if (system is MechanicalSystem mechanical) mechanical.Add(connectors);
            else throw new OpException(ErrorCodes.UnsupportedOp, "System '" + system.Name + "' (" + system.GetType().Name + ") cannot take elements through the API.");
            c.Modified(system);
            c.Modified(members.Select(m => m.Id));
            c.EnsureRegenerated();
            c.Summary("added " + ModelKit.Count(members.Count, "element") + " to system '" + system.Name + "'");
        }

        [Op("mep.system_remove")]
        public static void SystemRemove(ChangeContext c)
        {
            Document doc = c.Doc;
            MEPSystem system = MepKit.System(c, "system");
            IList<Element> elements = c.Elements("ids");
            WorksharingGuard.EnsureEditable(doc, system);
            if (system is ElectricalSystem circuit)
            {
                var set = new ElementSet();
                foreach (Element element in elements) set.Insert(element);
                circuit.RemoveFromCircuit(set);
                c.Modified(system);
                c.Modified(elements.Select(e => e.Id));
                c.Summary("removed " + ModelKit.Count(elements.Count, "element") + " from circuit " + circuit.Name);
                return;
            }
            var connectors = new ConnectorSet();
            var members = new List<Element>();
            foreach (Element element in elements)
            {
                bool any = false;
                foreach (Connector connector in MepKit.Connectors(element))
                {
                    MEPSystem owner = null;
                    try { owner = connector.MEPSystem; } catch { }
                    if (owner != null && owner.Id == system.Id)
                    {
                        connectors.Insert(connector);
                        any = true;
                    }
                }
                if (any) members.Add(element);
                else c.Warn(WarningCodes.ParamIgnored, ModelKit.Describe(element) + " is not in system '" + system.Name + "'", new[] { element.Id });
            }
            if (members.Count == 0) throw OpException.InvalidArgs(c.Args.Field("ids"), "none of the elements is in system '" + system.Name + "'");
            if (system is PipingSystem piping) piping.Remove(connectors);
            else if (system is MechanicalSystem mechanical) mechanical.Remove(connectors);
            else throw new OpException(ErrorCodes.UnsupportedOp, "System '" + system.Name + "' (" + system.GetType().Name + ") cannot release elements through the API.");
            c.Modified(system);
            c.Modified(members.Select(m => m.Id));
            c.EnsureRegenerated();
            c.Summary("removed " + ModelKit.Count(members.Count, "element") + " from system '" + system.Name + "'");
        }

        [Op("mep.circuit")]
        public static void Circuit(ChangeContext c)
        {
            IList<Element> elements = c.Elements("ids");
            FamilyInstance panel = c.Args.Has("panel") ? Panel(c) : null;
            ElectricalSystem circuit = CreateCircuit(c, elements, panel);
            c.Summary("created circuit " + circuit.Name + " with " + ModelKit.Count(elements.Count, "element") + (panel != null ? " on panel " + PanelName(panel) : " (no panel)"));
        }

        private static ElectricalSystem CreateCircuit(ChangeContext c, IList<Element> elements, FamilyInstance panel)
        {
            Document doc = c.Doc;
            var ids = new List<ElementId>();
            foreach (Element element in elements)
            {
                bool electrical = MepKit.Connectors(element).Any(x => x.Domain == Domain.DomainElectrical);
                if (!electrical) throw OpException.InvalidArgs(c.Args.Field("ids"), ModelKit.Describe(element) + " has no electrical connector");
                WorksharingGuard.EnsureEditable(doc, element);
                ids.Add(element.Id);
            }
            ElectricalSystem circuit = ElectricalSystem.Create(doc, ids, ElectricalSystemType.PowerCircuit);
            if (circuit == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not create the circuit.");
            c.Created(circuit);
            c.Modified(ids);
            if (panel != null)
            {
                WorksharingGuard.EnsureEditable(doc, panel);
                circuit.SelectPanel(panel);
                c.Modified(panel);
            }
            c.EnsureRegenerated();
            c.Output("system", circuit.Id);
            c.Data(new Dictionary<string, object>
            {
                ["circuit"] = circuit.Name,
                ["id"] = circuit.Id.Value,
                ["panel"] = panel != null ? PanelName(panel) : null,
                ["members"] = ids.Select(id => (object)id.Value).ToList()
            });
            return circuit;
        }

        private static FamilyInstance Panel(ChangeContext c)
        {
            Document doc = c.Doc;
            string value = c.Str("panel");
            FamilyInstance byId = Resolve.ById<FamilyInstance>(doc, value);
            if (byId != null) return byId;
            List<FamilyInstance> panels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment).WhereElementIsNotElementType()
                .OfType<FamilyInstance>().ToList();
            return Resolve.Single(panels, PanelName, value, c.Args.Field("panel"), "panel");
        }

        private static string PanelName(FamilyInstance panel)
        {
            string name = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
            return string.IsNullOrWhiteSpace(name) ? panel.Name : name;
        }

        /// <summary>Unassigned connectors of the domain on family instances (pipes/ducts join systems through connections).</summary>
        private static ConnectorSet FreeSystemConnectors(ChangeContext c, IList<Element> elements, Domain domain, out List<Element> members)
        {
            var set = new ConnectorSet();
            members = new List<Element>();
            foreach (Element element in elements)
            {
                if (!(element is FamilyInstance))
                {
                    c.Warn(WarningCodes.ParamIgnored, ModelKit.Describe(element) + " is skipped: runs join a system through their connections, add the equipment/terminals instead", new[] { element.Id });
                    continue;
                }
                WorksharingGuard.EnsureEditable(c.Doc, element);
                bool any = false;
                foreach (Connector connector in MepKit.Connectors(element))
                {
                    if (connector.Domain != domain) continue;
                    MEPSystem owner = null;
                    try { owner = connector.MEPSystem; } catch { }
                    if (owner != null) continue;
                    set.Insert(connector);
                    any = true;
                }
                if (any) members.Add(element);
                else c.Warn(WarningCodes.ParamIgnored, ModelKit.Describe(element) + " has no free " + (domain == Domain.DomainPiping ? "piping" : "duct") + " connector", new[] { element.Id });
            }
            if (set.IsEmpty) throw OpException.InvalidArgs(c.Args.Field("ids"), "no element has a free " + (domain == Domain.DomainPiping ? "piping" : "duct") + " connector (equipment, fixtures or air terminals)");
            return set;
        }

        private static T TypeArg<T>(ChangeContext c, string key, string kind, Func<T, bool> preferred) where T : MEPSystemType
        {
            return MepKit.SystemType(c, key, preferred, kind);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Insulation
        // -------------------------------------------------------------------------------------------------------------

        [Op("mep.insulate")]
        public static void Insulate(ChangeContext c)
        {
            Document doc = c.Doc;
            string key = c.Args.Has("ids") ? "ids" : "from";
            IList<Element> elements = c.Elements(key);
            double thickness = c.Mm("thickness");
            if (thickness <= Units.MmToFt(0.1)) throw OpException.InvalidArgs(c.Args.Field("thickness"), "must be greater than 0 mm");
            string typeName = c.Str("type") ?? throw OpException.InvalidArgs(c.Args.Field("type"), "is required (an insulation type name)");

            PipeInsulationType pipeType = null;
            DuctInsulationType ductType = null;
            int added = 0, updated = 0, skipped = 0;
            foreach (Element element in elements)
            {
                bool pipe = IsPipeLike(element), duct = IsDuctLike(element);
                if (!pipe && !duct)
                {
                    skipped++;
                    continue;
                }
                WorksharingGuard.EnsureEditable(doc, element);
                ICollection<ElementId> existing = InsulationLiningBase.GetInsulationIds(doc, element.Id);
                if (existing != null && existing.Count > 0)
                {
                    foreach (ElementId id in existing)
                    {
                        if (doc.GetElement(id) is InsulationLiningBase insulation)
                        {
                            insulation.Thickness = thickness;
                            c.Modified(insulation, "thickness");
                            updated++;
                        }
                    }
                    continue;
                }
                Element created;
                if (pipe)
                {
                    pipeType = pipeType ?? Resolve.Type<PipeInsulationType>(doc, typeName, c.Args.Field("type"));
                    created = PipeInsulation.Create(doc, element.Id, pipeType.Id, thickness);
                }
                else
                {
                    ductType = ductType ?? Resolve.Type<DuctInsulationType>(doc, typeName, c.Args.Field("type"));
                    created = DuctInsulation.Create(doc, element.Id, ductType.Id, thickness);
                }
                if (created == null) throw new OpException(ErrorCodes.RevitRefused, "Revit did not insulate " + ModelKit.Describe(element) + ".");
                c.Created(created);
                added++;
            }
            if (added + updated == 0) throw OpException.InvalidArgs(c.Args.Field(key), "contains no pipes, ducts or their fittings/accessories");
            if (skipped > 0) c.Warn(WarningCodes.ParamIgnored, skipped.ToString(CultureInfo.InvariantCulture) + " element(s) are not pipes/ducts or their fittings and were skipped");
            c.Summary("insulated " + ModelKit.Count(added, "element") + (updated > 0 ? ", updated " + updated.ToString(CultureInfo.InvariantCulture) + " existing insulation(s)" : string.Empty) +
                      " with " + typeName + " " + ModelKit.Mm(thickness) + " mm");
        }

        private static bool IsPipeLike(Element element)
        {
            BuiltInCategory category = Resolve.SafeBuiltIn(element.Category);
            return element is Pipe || element is FlexPipe || category == BuiltInCategory.OST_PipeFitting || category == BuiltInCategory.OST_PipeAccessory;
        }

        private static bool IsDuctLike(Element element)
        {
            BuiltInCategory category = Resolve.SafeBuiltIn(element.Category);
            return element is Duct || element is FlexDuct || category == BuiltInCategory.OST_DuctFitting || category == BuiltInCategory.OST_DuctAccessory;
        }
    }
}
