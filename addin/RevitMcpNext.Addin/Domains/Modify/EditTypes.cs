using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// edit_types (SPEC Appendix A/B): duplicate, rename and delete types; set compound layers of wall/floor/roof/ceiling
    /// types (exterior/top first, core = the structure layers); default type per category; create and edit materials
    /// (shading color, transparency, surface/cut foreground patterns); rename a loadable family. Name clashes give
    /// NAME_TAKEN with the next free name; type edits count the type's instances for the response.
    /// </summary>
    internal static class EditTypesOps
    {
        // -------------------------------------------------------------------------------------------------------------
        // duplicate / rename / delete
        // -------------------------------------------------------------------------------------------------------------

        [Op("edit_types.duplicate_type")]
        public static void DuplicateType(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            ElementType type = Resolve.Type<ElementType>(c.Doc, a.ReqStr("type"), a.Field("type"));
            string name = a.ReqStr("name").Trim();
            EnsureTypeNameFree(c.Doc, type, name, a.Field("name"), null);
            ElementType copy = type.Duplicate(name);
            c.Created(copy);
            c.Output("type", copy.Id);
            c.Data(new Dictionary<string, object> { ["type"] = copy.Id.Value, ["name"] = Resolve.FullTypeName(copy), ["from"] = Resolve.FullTypeName(type) });
            c.Summary("duplicated '" + Resolve.FullTypeName(type) + "' as '" + Resolve.FullTypeName(copy) + "' (id " + copy.Id.Value.ToString(CultureInfo.InvariantCulture) + ")");
        }

        [Op("edit_types.rename_type")]
        public static void RenameType(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            ElementType type = Resolve.Type<ElementType>(c.Doc, a.ReqStr("type"), a.Field("type"));
            string name = a.ReqStr("name").Trim();
            string before = Resolve.FullTypeName(type);
            if (string.Equals(type.Name, name, StringComparison.Ordinal))
            {
                c.Data(new Dictionary<string, object> { ["type"] = type.Id.Value, ["name"] = before, ["unchanged"] = true });
                c.Summary("'" + before + "' already has that name");
                return;
            }
            EnsureTypeNameFree(c.Doc, type, name, a.Field("name"), type.Id);
            string oldName = type.Name;
            type.Name = name;
            c.Modified(type, "name", oldName);
            c.Output("type", type.Id);
            c.Data(new Dictionary<string, object> { ["type"] = type.Id.Value, ["name"] = Resolve.FullTypeName(type), ["was"] = before });
            c.Summary("renamed type '" + before + "' to '" + Resolve.FullTypeName(type) + "'");
        }

        [Op("edit_types.delete_type")]
        public static void DeleteType(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            ElementType type = Resolve.Type<ElementType>(c.Doc, a.ReqStr("type"), a.Field("type"));
            string name = Resolve.FullTypeName(type);
            ElementId typeId = type.Id;
            c.Delete(new List<ElementId> { typeId });
            int instances = c.DeletedRecords.Count(d => d.Counted && d.Id != typeId.Value);
            c.Data(new Dictionary<string, object> { ["deleted"] = c.DeletedRecords.Count(d => d.Counted), ["type"] = name, ["with_instances"] = instances });
            c.Summary("deleted type '" + name + "'" + (instances > 0 ? " and " + ModifySupport.Count(instances, "dependent element") : string.Empty));
        }

        private static void EnsureTypeNameFree(Document doc, ElementType type, string name, string field, ElementId self)
        {
            if (string.IsNullOrWhiteSpace(name)) throw OpException.InvalidArgs(field, "must not be empty");
            if (name.IndexOfAny(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\' }) >= 0)
            {
                throw OpException.InvalidArgs(field, "contains a character Revit does not allow in names ({}[]|;<>?`~:\\)");
            }
            List<string> siblings = new FilteredElementCollector(doc).OfClass(type.GetType()).Cast<ElementType>()
                .Where(t => t.Id != self && string.Equals(t.FamilyName, type.FamilyName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name).ToList();
            if (!siblings.Any(n => Resolve.Eq(n, name))) return;
            throw new OpException(ErrorCodes.NameTaken, "'" + type.FamilyName + "' already has a type named '" + name + "'.",
                new Dictionary<string, object> { ["param"] = field, ["next"] = ModifyElementsOps.NextFreeName(name, siblings) });
        }

        // -------------------------------------------------------------------------------------------------------------
        // set_layers
        // -------------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, MaterialFunctionAssignment> Functions = new Dictionary<string, MaterialFunctionAssignment>(StringComparer.OrdinalIgnoreCase)
        {
            ["structure"] = MaterialFunctionAssignment.Structure,
            ["core"] = MaterialFunctionAssignment.Structure,
            ["substrate"] = MaterialFunctionAssignment.Substrate,
            ["thermal"] = MaterialFunctionAssignment.Insulation,
            ["insulation"] = MaterialFunctionAssignment.Insulation,
            ["thermal/air"] = MaterialFunctionAssignment.Insulation,
            ["finish1"] = MaterialFunctionAssignment.Finish1,
            ["finish"] = MaterialFunctionAssignment.Finish1,
            ["finish 1"] = MaterialFunctionAssignment.Finish1,
            ["finish2"] = MaterialFunctionAssignment.Finish2,
            ["finish 2"] = MaterialFunctionAssignment.Finish2,
            ["membrane"] = MaterialFunctionAssignment.Membrane,
            ["membrane layer"] = MaterialFunctionAssignment.Membrane
        };

        [Op("edit_types.set_layers")]
        public static void SetLayers(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            ElementType type = Resolve.Type<ElementType>(c.Doc, a.ReqStr("type"), a.Field("type"));
            if (!(type is HostObjAttributes host))
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + Resolve.FullTypeName(type) + "' has no layers; set_layers works on wall, floor, roof and ceiling types.",
                    new Dictionary<string, object> { ["hint"] = "pick a basic wall, floor, roof or ceiling type" });
            }
            CompoundStructure old = host.GetCompoundStructure();
            if (old == null)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + Resolve.FullTypeName(type) + "' has no compound structure (curtain, stacked or in-place types cannot take layers).",
                    new Dictionary<string, object> { ["hint"] = "duplicate a basic type and set its layers" });
            }

            List<PayloadReader> rows = a.SubList("layers");
            if (rows.Count == 0) throw OpException.InvalidArgs(a.Field("layers"), "needs at least one layer {function, material, thickness}");
            var layers = new List<CompoundStructureLayer>();
            var echo = new List<object>();
            foreach (PayloadReader row in rows)
            {
                string functionText = row.Str("function") ?? "structure";
                if (!Functions.TryGetValue(functionText.Trim(), out MaterialFunctionAssignment function))
                {
                    throw OpException.InvalidArgs(row.Field("function"), "must be one of structure, substrate, thermal, finish1, finish2, membrane (got '" + functionText + "')");
                }
                double thickness = function == MaterialFunctionAssignment.Membrane ? row.MmOpt("thickness") ?? 0 : row.Mm("thickness");
                if (function == MaterialFunctionAssignment.Membrane && thickness > 1e-9) throw OpException.InvalidArgs(row.Field("thickness"), "a membrane layer has thickness 0");
                if (function != MaterialFunctionAssignment.Membrane && thickness <= 1e-9) throw OpException.InvalidArgs(row.Field("thickness"), "must be greater than 0 mm");
                ElementId material = ElementId.InvalidElementId;
                string materialText = row.Str("material");
                if (!string.IsNullOrWhiteSpace(materialText) && !IsNone(materialText)) material = Resolve.Material(c.Doc, materialText, row.Field("material")).Id;
                layers.Add(new CompoundStructureLayer(thickness, function, material));
                echo.Add(new List<object> { functionText.ToLowerInvariant(), material == ElementId.InvalidElementId ? null : c.Doc.GetElement(material)?.Name, Units.Mm(thickness) });
            }

            CompoundStructure structure = CompoundStructure.CreateSimpleCompoundStructure(layers);
            int first = layers.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
            int last = layers.FindLastIndex(l => l.Function == MaterialFunctionAssignment.Structure);
            if (first >= 0)
            {
                structure.SetNumberOfShellLayers(ShellLayerType.Exterior, first);
                structure.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - last);
                structure.StructuralMaterialIndex = first;
            }
            try { structure.EndCap = old.EndCap; } catch { }
            try { structure.OpeningWrapping = old.OpeningWrapping; } catch { }

            if (!structure.IsValid(c.Doc, out IDictionary<int, CompoundStructureError> errors, out IDictionary<int, int> _))
            {
                string reasons = errors == null || errors.Count == 0 ? "Revit reports the layer set as invalid" :
                    string.Join("; ", errors.Take(4).Select(e => "layer " + (e.Key + 1).ToString(CultureInfo.InvariantCulture) + ": " + e.Value));
                throw OpException.InvalidArgs(a.Field("layers"), reasons + " (membranes need neighbours, the core needs at least one structure layer between shells)");
            }

            List<object> before = Describe(c.Doc, old);
            host.SetCompoundStructure(structure);
            c.Modified(type, "layers", before);
            int instances = CountInstances(c.Doc, type.Id);
            c.Affected(instances);
            c.Output("type", type.Id);
            c.Data(new Dictionary<string, object>
            {
                ["type"] = Resolve.FullTypeName(type),
                ["layers"] = echo,
                ["thickness"] = Units.Mm(structure.GetWidth()),
                ["instances_affected"] = instances
            });
            c.Summary("set " + ModifySupport.Count(layers.Count, "layer") + " on '" + Resolve.FullTypeName(type) + "' (total " +
                      Convert.ToString(Units.Mm(structure.GetWidth()), CultureInfo.InvariantCulture) + " mm" +
                      (instances > 0 ? ", " + ModifySupport.Count(instances, "instance") + " follow" : string.Empty) + ")");
        }

        private static List<object> Describe(Document doc, CompoundStructure structure)
        {
            return structure.GetLayers().Select(l => (object)new List<object>
            {
                l.Function.ToString().ToLowerInvariant(),
                l.MaterialId == ElementId.InvalidElementId ? null : doc.GetElement(l.MaterialId)?.Name,
                Units.Mm(l.Width)
            }).ToList();
        }

        // -------------------------------------------------------------------------------------------------------------
        // set_default
        // -------------------------------------------------------------------------------------------------------------

        [Op("edit_types.set_default")]
        public static void SetDefault(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            BuiltInCategory category = Resolve.CategoryId(c.Doc, a.ReqStr("category"), a.Field("category"));
            ElementType type = Resolve.Type<ElementType>(c.Doc, a.ReqStr("type"), a.Field("type"), category);
            string label = EnglishAliases.LabelOf(category) ?? category.ToString();
            ElementTypeGroup? group = GroupOf(category);
            ElementId previous;
            if (group.HasValue)
            {
                previous = c.Doc.GetDefaultElementTypeId(group.Value);
                if (previous == type.Id)
                {
                    Unchanged(c, label, type);
                    return;
                }
                if (!c.Doc.IsDefaultElementTypeIdValid(group.Value, type.Id))
                {
                    throw OpException.InvalidArgs(a.Field("type"), "'" + Resolve.FullTypeName(type) + "' cannot be the default " + label + " type");
                }
                c.Doc.SetDefaultElementTypeId(group.Value, type.Id);
            }
            else
            {
                var categoryId = new ElementId(category);
                previous = c.Doc.GetDefaultFamilyTypeId(categoryId);
                if (previous == type.Id)
                {
                    Unchanged(c, label, type);
                    return;
                }
                if (!c.Doc.IsDefaultFamilyTypeIdValid(categoryId, type.Id))
                {
                    throw OpException.InvalidArgs(a.Field("type"), "'" + Resolve.FullTypeName(type) + "' cannot be the default type of " + label + " (it must be a loadable family type of that category)");
                }
                c.Doc.SetDefaultFamilyTypeId(categoryId, type.Id);
            }
            c.MarkDirty();
            c.Output("type", type.Id);
            string before = previous != null && previous != ElementId.InvalidElementId && c.Doc.GetElement(previous) is ElementType old ? Resolve.FullTypeName(old) : null;
            c.Data(new Dictionary<string, object> { ["category"] = label, ["type"] = Resolve.FullTypeName(type), ["was"] = before });
            c.Summary("default " + label + " type is now '" + Resolve.FullTypeName(type) + "'" + (before != null ? " (was '" + before + "')" : string.Empty));
        }

        private static void Unchanged(ChangeContext c, string label, ElementType type)
        {
            c.Output("type", type.Id);
            c.Data(new Dictionary<string, object> { ["category"] = label, ["type"] = Resolve.FullTypeName(type), ["unchanged"] = true });
            c.Summary("'" + Resolve.FullTypeName(type) + "' already is the default " + label + " type");
        }

        private static ElementTypeGroup? GroupOf(BuiltInCategory category)
        {
            switch (category)
            {
                case BuiltInCategory.OST_Walls: return ElementTypeGroup.WallType;
                case BuiltInCategory.OST_Floors: return ElementTypeGroup.FloorType;
                case BuiltInCategory.OST_Roofs: return ElementTypeGroup.RoofType;
                case BuiltInCategory.OST_Ceilings: return ElementTypeGroup.CeilingType;
                case BuiltInCategory.OST_Grids: return ElementTypeGroup.GridType;
                case BuiltInCategory.OST_Levels: return ElementTypeGroup.LevelType;
                case BuiltInCategory.OST_Stairs: return ElementTypeGroup.StairsType;
                case BuiltInCategory.OST_StairsRailing: return ElementTypeGroup.StairsRailingType;
                case BuiltInCategory.OST_TextNotes: return ElementTypeGroup.TextNoteType;
                case BuiltInCategory.OST_Dimensions: return ElementTypeGroup.LinearDimensionType;
                case BuiltInCategory.OST_PipeCurves: return ElementTypeGroup.PipeType;
                case BuiltInCategory.OST_DuctCurves: return ElementTypeGroup.DuctType;
                case BuiltInCategory.OST_Conduit: return ElementTypeGroup.ConduitType;
                case BuiltInCategory.OST_CableTray: return ElementTypeGroup.CableTrayType;
                case BuiltInCategory.OST_FlexPipeCurves: return ElementTypeGroup.FlexPipeType;
                case BuiltInCategory.OST_FlexDuctCurves: return ElementTypeGroup.FlexDuctType;
                case BuiltInCategory.OST_Viewports: return ElementTypeGroup.ViewportType;
                default: return null;
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // materials
        // -------------------------------------------------------------------------------------------------------------

        [Op("edit_types.material_create")]
        public static void MaterialCreate(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            string name = a.ReqStr("name").Trim();
            EnsureMaterialNameFree(c.Doc, name, a.Field("name"), null);
            Material material;
            string from = null;
            if (a.Has("material"))
            {
                Material source = Resolve.Material(c.Doc, a.Str("material"), a.Field("material"));
                material = source.Duplicate(name);
                from = source.Name;
            }
            else
            {
                material = c.Doc.GetElement(Material.Create(c.Doc, name)) as Material
                           ?? throw new OpException(ErrorCodes.RevitRefused, "Revit did not create material '" + name + "'.", new Dictionary<string, object> { ["apiMessage"] = "Material.Create returned no material" });
            }
            List<string> applied = Apply(c, a, material);
            c.Created(material);
            c.Output("material", material.Id);
            c.Data(MaterialData(c.Doc, material, from));
            c.Summary("created material '" + material.Name + "'" + (from != null ? " from '" + from + "'" : string.Empty) +
                      (applied.Count > 0 ? " (" + string.Join(", ", applied) + ")" : string.Empty));
        }

        [Op("edit_types.material_edit")]
        public static void MaterialEdit(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            Material material = Resolve.Material(c.Doc, a.ReqStr("material"), a.Field("material"));
            if (!new[] { "name", "color", "transparency", "surface_pattern", "cut_pattern" }.Any(a.Has))
            {
                throw OpException.InvalidArgs(a.Field("material"), "nothing to change: give name, color, transparency, surface_pattern or cut_pattern",
                    new Dictionary<string, object> { ["op"] = "material_edit", ["material"] = material.Name, ["color"] = "#B5523B" });
            }
            var applied = new List<string>();
            string oldName = material.Name;
            if (a.Has("name"))
            {
                string name = a.Str("name").Trim();
                if (!string.Equals(name, material.Name, StringComparison.Ordinal))
                {
                    EnsureMaterialNameFree(c.Doc, name, a.Field("name"), material.Id);
                    material.Name = name;
                    applied.Add("renamed from '" + oldName + "'");
                }
            }
            applied.AddRange(Apply(c, a, material));
            c.Modified(material, "material", oldName);
            c.Output("material", material.Id);
            c.Data(MaterialData(c.Doc, material, null));
            c.Summary("edited material '" + material.Name + "'" + (applied.Count > 0 ? " (" + string.Join(", ", applied) + ")" : " (no changes)"));
        }

        private static List<string> Apply(ChangeContext c, PayloadReader a, Material material)
        {
            var applied = new List<string>();
            if (a.Has("color"))
            {
                Color color = ModifySupport.ParseColor(a.Str("color"), a.Field("color"));
                material.Color = color;
                applied.Add("color " + ModifySupport.ColorText(color));
            }
            if (a.Has("transparency"))
            {
                int transparency = a.IntClamped("transparency", 0, 0, 100, (code, text) => c.Warn(code, text));
                material.Transparency = transparency;
                applied.Add("transparency " + transparency.ToString(CultureInfo.InvariantCulture) + "%");
            }
            if (a.Has("surface_pattern"))
            {
                string value = a.Str("surface_pattern");
                material.SurfaceForegroundPatternId = IsNone(value) ? ElementId.InvalidElementId : Resolve.FillPattern(c.Doc, value, a.Field("surface_pattern")).Id;
                applied.Add("surface pattern " + (IsNone(value) ? "none" : value));
            }
            if (a.Has("cut_pattern"))
            {
                string value = a.Str("cut_pattern");
                material.CutForegroundPatternId = IsNone(value) ? ElementId.InvalidElementId : Resolve.FillPattern(c.Doc, value, a.Field("cut_pattern"), FillPatternTarget.Drafting).Id;
                applied.Add("cut pattern " + (IsNone(value) ? "none" : value));
            }
            return applied;
        }

        private static Dictionary<string, object> MaterialData(Document doc, Material material, string from)
        {
            var data = new Dictionary<string, object>
            {
                ["material"] = material.Id.Value,
                ["name"] = material.Name,
                ["color"] = ModifySupport.ColorText(material.Color),
                ["transparency"] = material.Transparency,
                ["surface_pattern"] = PatternName(doc, material.SurfaceForegroundPatternId),
                ["cut_pattern"] = PatternName(doc, material.CutForegroundPatternId)
            };
            if (from != null) data["from"] = from;
            return data;
        }

        private static string PatternName(Document doc, ElementId id)
        {
            return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
        }

        private static void EnsureMaterialNameFree(Document doc, string name, string field, ElementId self)
        {
            if (string.IsNullOrWhiteSpace(name)) throw OpException.InvalidArgs(field, "must not be empty");
            List<string> names = new FilteredElementCollector(doc).OfClass(typeof(Material)).Where(m => m.Id != self).Select(m => m.Name).ToList();
            if (!names.Any(n => Resolve.Eq(n, name))) return;
            throw new OpException(ErrorCodes.NameTaken, "A material named '" + name + "' already exists.",
                new Dictionary<string, object> { ["param"] = field, ["next"] = ModifyElementsOps.NextFreeName(name, names) });
        }

        // -------------------------------------------------------------------------------------------------------------
        // rename_family
        // -------------------------------------------------------------------------------------------------------------

        [Op("edit_types.rename_family")]
        public static void RenameFamily(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            string value = a.ReqStr("family");
            List<Family> families = new FilteredElementCollector(c.Doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            Family family = Resolve.ById<Family>(c.Doc, value) ?? Resolve.Single(families, f => f.Name, value, a.Field("family"), "family");
            string name = a.ReqStr("name").Trim();
            if (string.Equals(family.Name, name, StringComparison.Ordinal))
            {
                c.Data(new Dictionary<string, object> { ["family"] = family.Id.Value, ["name"] = name, ["unchanged"] = true });
                c.Summary("family '" + name + "' already has that name");
                return;
            }
            List<string> taken = families.Where(f => f.Id != family.Id).Select(f => f.Name).ToList();
            if (taken.Any(n => Resolve.Eq(n, name)))
            {
                throw new OpException(ErrorCodes.NameTaken, "A family named '" + name + "' already exists.",
                    new Dictionary<string, object> { ["param"] = a.Field("name"), ["next"] = ModifyElementsOps.NextFreeName(name, taken) });
            }
            string oldName = family.Name;
            family.Name = name;
            c.Modified(family, "name", oldName);
            c.Output("family", family.Id);
            c.Data(new Dictionary<string, object> { ["family"] = family.Id.Value, ["name"] = name, ["was"] = oldName });
            c.Summary("renamed family '" + oldName + "' to '" + name + "'");
        }

        // -------------------------------------------------------------------------------------------------------------
        // Shared
        // -------------------------------------------------------------------------------------------------------------

        private static bool IsNone(string value)
        {
            string text = (value ?? string.Empty).Trim();
            return text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("<none>", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("<by category>", StringComparison.OrdinalIgnoreCase) || text.Equals("by category", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountInstances(Document doc, ElementId typeId)
        {
            int count = 0;
            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                try
                {
                    if (element.GetTypeId() == typeId) count++;
                }
                catch
                {
                    // Elements without a type.
                }
            }
            return count;
        }
    }
}
