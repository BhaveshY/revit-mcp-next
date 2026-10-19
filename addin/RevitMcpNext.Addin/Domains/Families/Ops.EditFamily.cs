using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// edit_family parameter and type ops (SPEC Appendix A/B, §5.9, D3 §3.7). Each handler runs inside a
    /// <see cref="FamilySession"/> opened by the engine's family-segment runner: the family document is either the target
    /// (doc= an open .rfa) or a background EditFamily copy of a loaded family (family=) that the runner reloads after the
    /// last op. Every op is one family-document Transaction; errors leave nothing changed.
    /// </summary>
    internal static class EditFamilyOps
    {
        // ---------------------------------------------------------------------------------------------------------
        // Parameters
        // ---------------------------------------------------------------------------------------------------------

        [Op("edit_family.add_param")]
        public static void AddParam(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            string name = c.Args.ReqStr("name").Trim();
            bool instance = IsInstance(c);
            string sharedRef = c.Str("shared");
            bool hasFormula = !string.IsNullOrWhiteSpace(c.Str("formula"));
            bool hasValue = c.Has("value");
            if (hasFormula && hasValue)
            {
                throw OpException.InvalidArgs(c.Args.Field("value"), "a parameter driven by a formula takes no value; send formula or value, not both");
            }

            FamilyParameter created = null;
            string dataWord = null;
            List<string> valueTypes = null;
            s.Edit(c, fm =>
            {
                ForgeTypeId group = FamilyNames.Group(c.Str("group"), c.Args.Field("group"));
                CheckAssignableGroup(fm, group, c.Str("group"), c.Args.Field("group"));
                if (sharedRef != null)
                {
                    ExternalDefinition definition = SharedParams.Find(c, sharedRef, c.Args.Field("shared"));
                    if (c.Has("data")) c.Warn(WarningCodes.ParamIgnored, "data is ignored: the shared parameter '" + definition.Name + "' defines its own data type");
                    if (!string.Equals(definition.Name, name, StringComparison.Ordinal))
                    {
                        c.Warn(WarningCodes.ParamRenamed, "the shared parameter keeps its own name '" + definition.Name + "' (name was '" + name + "')");
                        name = definition.Name;
                    }
                    EnsureFreeName(fm, name, c.Args.Field("name"));
                    FamilyParameter sameGuid = SafeParameter(fm, definition.GUID);
                    if (sameGuid != null)
                    {
                        throw new OpException(ErrorCodes.NameTaken, "The family already has the shared parameter '" + FamilyValues.NameOf(sameGuid) + "' (" + definition.GUID + ").",
                            new Dictionary<string, object> { ["param"] = c.Args.Field("shared"), ["value"] = sharedRef, ["existing"] = FamilyValues.NameOf(sameGuid) });
                    }
                    created = fm.AddParameter(definition, group, instance);
                    dataWord = FamilyNames.DataLabel(s.FamilyDoc, FamilyValues.SpecOf(created));
                }
                else
                {
                    EnsureFreeName(fm, name, c.Args.Field("name"));
                    FamilyNames.DataType data = FamilyNames.Data(s.FamilyDoc, c.Str("data") ?? "text", c.Args.Field("data"));
                    created = data.FamilyTypeCategory != null
                        ? fm.AddParameter(name, group, data.FamilyTypeCategory, instance)
                        : fm.AddParameter(name, group, data.Spec, instance);
                    dataWord = data.Word;
                }

                if (c.Bool("reporting")) MakeReporting(fm, created, true, c.Args.Field("reporting"));
                if (hasFormula) SetFormula(fm, created, c.Str("formula"), c.Args.Field("formula"));
                if (hasValue)
                {
                    List<FamilyType> types = FamilyValues.Types(fm);
                    if (types.Count == 0)
                    {
                        throw OpException.InvalidArgs(c.Args.Field("value"), "family '" + s.FamilyName + "' has no types to hold a value; add_type first, then set_values");
                    }
                    valueTypes = SetOnTypes(c, s, fm, created, c.Args.Get("value"), types, c.Args.Field("value"));
                }
            });

            if (s.Direct) c.Created(created.Id);
            c.Output("param", FamilyValues.NameOf(created));
            var result = new Dictionary<string, object>
            {
                ["param"] = ParamRow(s.FamilyDoc, created),
                ["family"] = s.FamilyName
            };
            if (valueTypes != null) result["value_types"] = valueTypes.Count;
            c.Data(result);
            c.Summary("added " + (instance ? "instance" : "type") + " parameter '" + FamilyValues.NameOf(created) + "' (" + dataWord + ", " +
                      FamilyNames.GroupLabel(SafeGroup(created)) + ") to " + s.FamilyName +
                      (valueTypes != null ? ", value set on " + Plural(valueTypes.Count, "type") : string.Empty));
        }

        [Op("edit_family.remove_param")]
        public static void RemoveParam(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            FamilyParameter parameter = FamilyValues.Param(s.Manager, c.Args.ReqStr("name"), c.Args.Field("name"));
            string name = FamilyValues.NameOf(parameter);
            EnsureNotBuiltIn(parameter, "removed");
            List<string> usedBy = Usage(s.FamilyDoc, s.Manager, parameter);
            if (usedBy.Count > 0)
            {
                throw new OpException(ErrorCodes.ParamInUse,
                    "'" + name + "' is used by " + string.Join("; ", usedBy.Take(5)) + (usedBy.Count > 5 ? " and " + (usedBy.Count - 5).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty) + "; nothing was removed.",
                    new Dictionary<string, object> { ["param"] = name, ["usedBy"] = usedBy.Take(20).Cast<object>().ToList() });
            }

            ElementId id = parameter.Id;
            s.Edit(c, fm => fm.RemoveParameter(parameter));
            if (s.Direct && id != null && id.Value > 0) c.Deleted(new List<ElementId> { id });
            c.Data(new Dictionary<string, object> { ["removed"] = name, ["family"] = s.FamilyName });
            c.Summary("removed parameter '" + name + "' from " + s.FamilyName);
        }

        [Op("edit_family.rename_param")]
        public static void RenameParam(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            FamilyParameter parameter = FamilyValues.Param(s.Manager, c.Args.ReqStr("name"), c.Args.Field("name"));
            string oldName = FamilyValues.NameOf(parameter);
            string newName = c.Args.ReqStr("new_name").Trim();
            if (SafeIsShared(parameter))
            {
                throw new OpException(ErrorCodes.SharedParamRename, "'" + oldName + "' is a shared parameter; shared parameters cannot be renamed inside a family.",
                    new Dictionary<string, object> { ["param"] = oldName });
            }
            EnsureNotBuiltIn(parameter, "renamed");
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                c.Data(new Dictionary<string, object> { ["param"] = oldName, ["unchanged"] = true });
                c.Summary("'" + oldName + "' already has that name; nothing to do");
                return;
            }
            FamilyParameter clash = FamilyValues.Find(s.Manager, newName);
            if (clash != null && !ReferenceEquals(clash, parameter) && clash.Id != parameter.Id) throw NameTaken(s.Manager, c.Args.Field("new_name"), newName);

            s.Edit(c, fm => fm.RenameParameter(parameter, newName));
            if (s.Direct && parameter.Id != null && parameter.Id.Value > 0) c.Modified(parameter.Id);
            c.Output("param", newName);
            c.Data(new Dictionary<string, object> { ["param"] = newName, ["was"] = oldName, ["family"] = s.FamilyName });
            c.Summary("renamed parameter '" + oldName + "' to '" + newName + "' in " + s.FamilyName + " (formulas follow)");
        }

        [Op("edit_family.set_param")]
        public static void SetParam(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            FamilyParameter parameter = FamilyValues.Param(s.Manager, c.Args.ReqStr("name"), c.Args.Field("name"));
            string name = FamilyValues.NameOf(parameter);
            bool hasKind = c.Has("kind"), hasGroup = c.Has("group"), hasReporting = c.Has("reporting");
            if (!hasKind && !hasGroup && !hasReporting)
            {
                throw OpException.InvalidArgs("kind", "set_param needs kind, group or reporting",
                    new Dictionary<string, object> { ["op"] = "set_param", ["name"] = name, ["kind"] = "instance" });
            }
            if ((hasKind || hasGroup) && FamilyValues.IsBuiltIn(parameter))
            {
                throw new OpException(ErrorCodes.NotEditable, "'" + name + "' is a built-in parameter; its kind and group are fixed.",
                    new Dictionary<string, object> { ["param"] = name, ["reason"] = "built-in" });
            }

            var changes = new List<string>();
            s.Edit(c, fm =>
            {
                if (hasKind)
                {
                    bool instance = IsInstance(c);
                    if (parameter.IsInstance != instance)
                    {
                        try
                        {
                            if (instance) fm.MakeInstance(parameter);
                            else fm.MakeType(parameter);
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                        {
                            throw new OpException(ErrorCodes.RevitRefused, "Revit cannot make '" + name + "' a" + (instance ? "n instance" : " type") + " parameter: " + ex.Message,
                                new Dictionary<string, object> { ["apiMessage"] = ex.Message, ["param"] = name });
                        }
                        changes.Add(instance ? "instance" : "type");
                    }
                }
                if (hasGroup)
                {
                    ForgeTypeId group = FamilyNames.Group(c.Str("group"), c.Args.Field("group"));
                    CheckAssignableGroup(fm, group, c.Str("group"), c.Args.Field("group"));
                    if (!(parameter.Definition is InternalDefinition definition))
                    {
                        throw new OpException(ErrorCodes.UnsupportedOp, "The group of '" + name + "' cannot be changed through the API.",
                            new Dictionary<string, object> { ["param"] = name });
                    }
                    if (SafeGroup(parameter) != group)
                    {
                        definition.SetGroupTypeId(group);
                        changes.Add("group " + FamilyNames.GroupLabel(group));
                    }
                }
                if (hasReporting)
                {
                    bool reporting = c.Bool("reporting");
                    if (parameter.IsReporting != reporting)
                    {
                        MakeReporting(fm, parameter, reporting, c.Args.Field("reporting"));
                        changes.Add(reporting ? "reporting" : "not reporting");
                    }
                }
            });

            if (s.Direct && changes.Count > 0 && parameter.Id != null && parameter.Id.Value > 0) c.Modified(parameter.Id);
            c.Data(new Dictionary<string, object> { ["param"] = ParamRow(s.FamilyDoc, parameter), ["changed"] = changes.Cast<object>().ToList() });
            c.Summary(changes.Count == 0
                ? "'" + name + "' already had those settings; nothing changed"
                : "set '" + name + "' in " + s.FamilyName + ": " + string.Join(", ", changes));
        }

        [Op("edit_family.set_formula")]
        public static void SetFormulaOp(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            FamilyParameter parameter = FamilyValues.Param(s.Manager, c.Args.ReqStr("name"), c.Args.Field("name"));
            string name = FamilyValues.NameOf(parameter);
            // '' is a value here (clear the formula): Has() treats an empty string as absent, so test for the key itself.
            if (!c.Args.TryGet("formula", out object rawFormula) || rawFormula == null) throw OpException.InvalidArgs(c.Args.Field("formula"), "is required (a formula, or '' to clear it)");
            string formula = rawFormula as string ?? c.Str("formula");
            bool clear = string.IsNullOrWhiteSpace(formula);
            string before = SafeFormula(parameter);
            if (clear && string.IsNullOrEmpty(before))
            {
                c.Data(new Dictionary<string, object> { ["param"] = name, ["formula"] = null, ["unchanged"] = true });
                c.Summary("'" + name + "' has no formula; nothing to clear");
                return;
            }

            s.Edit(c, fm => SetFormula(fm, parameter, clear ? null : formula, c.Args.Field("formula")), ErrorCodes.FormulaInvalid);
            if (s.Direct && parameter.Id != null && parameter.Id.Value > 0) c.Modified(parameter.Id);
            c.Data(new Dictionary<string, object> { ["param"] = name, ["formula"] = clear ? null : SafeFormula(parameter), ["was"] = before });
            c.Summary(clear ? "cleared the formula of '" + name + "' in " + s.FamilyName : "set '" + name + "' = " + formula + " in " + s.FamilyName);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Values and types
        // ---------------------------------------------------------------------------------------------------------

        [Op("edit_family.set_values")]
        public static void SetValues(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            FamilyManager fm = s.Manager;
            List<FamilyType> all = FamilyValues.Types(fm);
            if (all.Count == 0) throw OpException.InvalidArgs("types", "family '" + s.FamilyName + "' has no types; add_type first");

            // (type, parameter name, value, field) assignments, validated before anything changes.
            var plan = new List<Tuple<FamilyType, FamilyParameter, object, string>>();
            if (c.Has("rows"))
            {
                List<PayloadReader> rows = c.Args.SubList("rows");
                if (rows.Count == 0) throw OpException.InvalidArgs(c.Args.Field("rows"), "needs at least one {type, values:{param: value}}");
                foreach (PayloadReader row in rows)
                {
                    FamilyType type = FamilyValues.Type(fm, row.ReqStr("type"), row.Field("type"));
                    IDictionary<string, object> values = row.Dict("values");
                    if (values == null || values.Count == 0) throw OpException.InvalidArgs(row.Field("values"), "needs {param: value}");
                    foreach (KeyValuePair<string, object> pair in values)
                    {
                        string field = row.Field("values") + "." + pair.Key;
                        plan.Add(Tuple.Create(type, FamilyValues.Param(fm, pair.Key, field), pair.Value, field));
                    }
                }
            }
            else
            {
                FamilyParameter parameter = FamilyValues.Param(fm, c.Args.ReqStr("name"), c.Args.Field("name"));
                if (!c.Has("value")) throw OpException.InvalidArgs(c.Args.Field("value"), "is required with name (or send rows)");
                foreach (FamilyType type in SelectTypes(c, s, all))
                {
                    plan.Add(Tuple.Create(type, parameter, c.Args.Get("value"), c.Args.Field("value")));
                }
            }

            var changedTypes = new List<string>();
            var changedParams = new HashSet<string>(StringComparer.Ordinal);
            s.Edit(c, manager =>
            {
                FamilyType original = manager.CurrentType;
                try
                {
                    foreach (Tuple<FamilyType, FamilyParameter, object, string> step in plan)
                    {
                        if (!SameType(manager.CurrentType, step.Item1)) manager.CurrentType = step.Item1;
                        FamilyValues.Set(s.FamilyDoc, manager, step.Item2, step.Item3, c.Args.BareLengthToMm, step.Item4);
                        string typeName = FamilyValues.TypeName(step.Item1);
                        if (!changedTypes.Contains(typeName)) changedTypes.Add(typeName);
                        changedParams.Add(FamilyValues.NameOf(step.Item2));
                    }
                }
                finally
                {
                    RestoreCurrent(manager, original);
                }
            });

            s.NoteTypeValues(changedTypes);
            c.Data(new Dictionary<string, object>
            {
                ["family"] = s.FamilyName,
                ["types"] = changedTypes.Cast<object>().ToList(),
                ["params"] = changedParams.Cast<object>().ToList(),
                ["values"] = plan.Count
            });
            c.Summary("set " + string.Join(", ", changedParams.Take(5)) + " on " + Plural(changedTypes.Count, "type") + " of " + s.FamilyName);
        }

        [Op("edit_family.add_type")]
        public static void AddType(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            FamilyManager fm = s.Manager;
            string name = c.Args.ReqStr("name").Trim();
            if (FamilyValues.TypeExists(fm, name)) throw TypeNameTaken(fm, c.Args.Field("name"), name);
            FamilyType source = c.Has("copy_from") ? FamilyValues.Type(fm, c.Str("copy_from"), c.Args.Field("copy_from")) : null;
            IDictionary<string, object> values = c.Args.Dict("values");
            var assignments = new List<Tuple<FamilyParameter, object, string>>();
            if (values != null)
            {
                foreach (KeyValuePair<string, object> pair in values)
                {
                    string field = c.Args.Field("values") + "." + pair.Key;
                    assignments.Add(Tuple.Create(FamilyValues.Param(fm, pair.Key, field), pair.Value, field));
                }
            }

            string copied = null;
            s.Edit(c, manager =>
            {
                if (source != null) manager.CurrentType = source;
                copied = FamilyValues.TypeName(manager.CurrentType);
                manager.NewType(name);
                foreach (Tuple<FamilyParameter, object, string> step in assignments)
                {
                    FamilyValues.Set(s.FamilyDoc, manager, step.Item1, step.Item2, c.Args.BareLengthToMm, step.Item3);
                }
            });

            s.TypesAdded.Add(name);
            if (assignments.Count > 0) s.NoteTypeValues(new[] { name });
            c.Output("type", name);
            c.Data(new Dictionary<string, object>
            {
                ["family"] = s.FamilyName,
                ["type"] = name,
                ["copied_from"] = copied,
                ["values"] = assignments.Count,
                ["types_total"] = FamilyValues.Types(fm).Count
            });
            c.Summary("added type '" + name + "' to " + s.FamilyName + (string.IsNullOrEmpty(copied) ? string.Empty : " (copy of '" + copied + "')") +
                      (assignments.Count > 0 ? " with " + Plural(assignments.Count, "value") : string.Empty));
        }

        [Op("edit_family.rename_type")]
        public static void RenameType(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            FamilyManager fm = s.Manager;
            FamilyType type = FamilyValues.Type(fm, c.Args.ReqStr("name"), c.Args.Field("name"));
            string oldName = FamilyValues.TypeName(type);
            string newName = c.Args.ReqStr("new_name").Trim();
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                c.Data(new Dictionary<string, object> { ["type"] = oldName, ["unchanged"] = true });
                c.Summary("type '" + oldName + "' already has that name; nothing to do");
                return;
            }
            if (!Resolve.Eq(oldName, newName) && FamilyValues.TypeExists(fm, newName))
            {
                throw TypeNameTaken(fm, c.Args.Field("new_name"), newName);
            }

            s.Edit(c, manager =>
            {
                FamilyType original = manager.CurrentType;
                bool wasCurrent = SameType(original, type);
                manager.CurrentType = type;
                manager.RenameCurrentType(newName);
                if (!wasCurrent) RestoreCurrent(manager, original);
            });

            s.TypeRenames.Add(new KeyValuePair<string, string>(oldName, newName));
            c.Output("type", newName);
            c.Data(new Dictionary<string, object> { ["family"] = s.FamilyName, ["type"] = newName, ["was"] = oldName });
            c.Summary("renamed type '" + oldName + "' to '" + newName + "' in " + s.FamilyName);
        }

        [Op("edit_family.delete_type")]
        public static void DeleteType(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            FamilyManager fm = s.Manager;
            List<FamilyType> types = FamilyValues.Types(fm);
            FamilyType type = FamilyValues.Type(fm, c.Args.ReqStr("name"), c.Args.Field("name"));
            string name = FamilyValues.TypeName(type);
            if (types.Count <= 1)
            {
                throw OpException.InvalidArgs(c.Args.Field("name"), "'" + name + "' is the only type of " + s.FamilyName + "; a family keeps at least one type");
            }

            s.Edit(c, manager =>
            {
                FamilyType original = manager.CurrentType;
                bool wasCurrent = SameType(original, type);
                manager.CurrentType = type;
                manager.DeleteCurrentType();
                if (!wasCurrent) RestoreCurrent(manager, original);
            });

            s.TypesDeleted.Add(name);
            c.Data(new Dictionary<string, object> { ["family"] = s.FamilyName, ["deleted"] = name, ["types_total"] = FamilyValues.Types(fm).Count });
            c.Summary("deleted type '" + name + "' from " + s.FamilyName + (s.Direct ? string.Empty : " (an unused project copy of it is removed on reload)"));
        }

        [Op("edit_family.set_category")]
        public static void SetCategory(ChangeContext c)
        {
            FamilySession s = FamilySession.Of(c);
            Family owner = s.FamilyDoc.OwnerFamily ?? throw new OpException(ErrorCodes.UnsupportedOp, "The family document has no owner family.");
            Category category = Resolve.Category(s.FamilyDoc, c.Args.ReqStr("category"), c.Args.Field("category"));
            string before = FamilyNames.CategoryLabel(SafeCategory(owner));
            string after = FamilyNames.CategoryLabel(category);
            if (SafeCategory(owner)?.Id == category.Id)
            {
                c.Data(new Dictionary<string, object> { ["family"] = s.FamilyName, ["category"] = after, ["unchanged"] = true });
                c.Summary(s.FamilyName + " already is in category " + after);
                return;
            }

            s.Edit(c, fm =>
            {
                try
                {
                    owner.FamilyCategory = category;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw OpException.InvalidArgs(c.Args.Field("category"), "Revit cannot put " + s.FamilyName + " in category " + after + " (" + ex.Message + ")");
                }
            });
            if (s.Direct) c.Modified(owner.Id);
            c.Data(new Dictionary<string, object> { ["family"] = s.FamilyName, ["category"] = after, ["was"] = before });
            c.Summary("moved " + s.FamilyName + " from " + before + " to " + after);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------------------------------

        private static bool IsInstance(ChangeContext c)
        {
            string kind = c.Str("kind");
            if (kind == null) return false;
            string squashed = kind.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            switch (squashed)
            {
                case "instance":
                case "instanceparameter":
                case "inst":
                    return true;
                case "type":
                case "typeparameter":
                    return false;
            }
            throw OpException.InvalidArgs(c.Args.Field("kind"), "must be type or instance (got '" + kind + "')");
        }

        /// <summary>The types a name+value set_values call changes: types=[...] / ['*'], or the only type; else NEEDS_TYPES.</summary>
        private static List<FamilyType> SelectTypes(ChangeContext c, FamilySession s, List<FamilyType> all)
        {
            List<string> requested = c.Has("types") ? c.Args.Strs("types") : new List<string>();
            if (requested.Count == 0)
            {
                if (all.Count == 1) return all;
                throw new OpException(ErrorCodes.NeedsTypes,
                    s.FamilyName + " has " + all.Count.ToString(CultureInfo.InvariantCulture) + " types (" +
                    string.Join(", ", all.Take(6).Select(FamilyValues.TypeName)) + (all.Count > 6 ? ", ..." : string.Empty) +
                    "); say which with types, or ['*'] for all.",
                    new Dictionary<string, object> { ["param"] = "types", ["options"] = all.Select(t => (object)FamilyValues.TypeName(t)).Take(50).ToList() });
            }
            if (requested.Any(t => t == "*")) return all;
            var selected = new List<FamilyType>();
            foreach (string name in requested)
            {
                FamilyType type = FamilyValues.Type(s.Manager, name, c.Args.Field("types"));
                if (!selected.Any(t => SameType(t, type))) selected.Add(type);
            }
            return selected;
        }

        /// <summary>Sets one parameter on several types (restores the current type). Returns the changed type names.</summary>
        private static List<string> SetOnTypes(ChangeContext c, FamilySession s, FamilyManager fm, FamilyParameter parameter, object value, List<FamilyType> types, string field)
        {
            var changed = new List<string>();
            FamilyType original = fm.CurrentType;
            try
            {
                foreach (FamilyType type in types)
                {
                    if (!SameType(fm.CurrentType, type)) fm.CurrentType = type;
                    FamilyValues.Set(s.FamilyDoc, fm, parameter, value, c.Args.BareLengthToMm, field);
                    changed.Add(FamilyValues.TypeName(type));
                }
            }
            finally
            {
                RestoreCurrent(fm, original);
            }
            s.NoteTypeValues(changed);
            return changed;
        }

        /// <summary>FamilyType wrappers are not reference-stable; compare by name.</summary>
        internal static bool SameType(FamilyType a, FamilyType b)
        {
            return a != null && b != null && string.Equals(FamilyValues.TypeName(a), FamilyValues.TypeName(b), StringComparison.Ordinal);
        }

        private static void RestoreCurrent(FamilyManager fm, FamilyType original)
        {
            if (original == null) return;
            try
            {
                if (!SameType(fm.CurrentType, original)) fm.CurrentType = original;
            }
            catch
            {
                // The original type may have been deleted or renamed; the current type stays as is.
            }
        }

        private static void SetFormula(FamilyManager fm, FamilyParameter parameter, string formula, string field)
        {
            if (formula != null && parameter.IsReporting)
            {
                throw new OpException(ErrorCodes.FormulaInvalid, "'" + FamilyValues.NameOf(parameter) + "' is a reporting parameter and cannot have a formula.",
                    new Dictionary<string, object> { ["param"] = FamilyValues.NameOf(parameter), ["apiMessage"] = "reporting parameter" });
            }
            try
            {
                fm.SetFormula(parameter, formula);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new OpException(ErrorCodes.FormulaInvalid, "Revit rejected the formula '" + formula + "' for '" + FamilyValues.NameOf(parameter) + "': " + ex.Message,
                    new Dictionary<string, object> { ["param"] = field, ["formula"] = formula, ["apiMessage"] = ex.Message });
            }
        }

        private static void MakeReporting(FamilyManager fm, FamilyParameter parameter, bool reporting, string field)
        {
            try
            {
                if (reporting) fm.MakeReporting(parameter);
                else fm.MakeNonReporting(parameter);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw OpException.InvalidArgs(field, "Revit cannot make '" + FamilyValues.NameOf(parameter) + "' " + (reporting ? "a reporting" : "a non-reporting") +
                    " parameter (" + ex.Message + "); reporting parameters are lengths or angles labelled on a dimension, without a formula");
            }
        }

        private static void CheckAssignableGroup(FamilyManager fm, ForgeTypeId group, string requested, string field)
        {
            bool ok;
            try
            {
                ok = group == null || group.Empty() || fm.IsUserAssignableParameterGroup(group);
            }
            catch
            {
                ok = true;
            }
            if (!ok) throw OpException.InvalidArgs(field, "the properties group '" + requested + "' cannot hold user parameters; use e.g. Dimensions, Identity Data, Constraints or Other");
        }

        private static void EnsureFreeName(FamilyManager fm, string name, string field)
        {
            if (string.IsNullOrWhiteSpace(name)) throw OpException.InvalidArgs(field, "is required (the parameter name)");
            if (FamilyValues.Find(fm, name) != null) throw NameTaken(fm, field, name);
        }

        private static OpException NameTaken(FamilyManager fm, string field, string name)
        {
            var taken = new HashSet<string>(FamilyValues.All(fm).Select(FamilyValues.NameOf), StringComparer.OrdinalIgnoreCase);
            string next = NextFree(name, taken);
            return new OpException(ErrorCodes.NameTaken, "The family already has a parameter named '" + name + "'.",
                new Dictionary<string, object> { ["param"] = field, ["value"] = name, ["next"] = next });
        }

        private static OpException TypeNameTaken(FamilyManager fm, string field, string name)
        {
            var taken = new HashSet<string>(FamilyValues.Types(fm).Select(FamilyValues.TypeName), StringComparer.OrdinalIgnoreCase);
            return new OpException(ErrorCodes.NameTaken, "The family already has a type named '" + name + "'.",
                new Dictionary<string, object> { ["param"] = field, ["value"] = name, ["next"] = NextFree(name, taken) });
        }

        internal static string NextFree(string name, ISet<string> taken)
        {
            for (int n = 2; n < 1000; n++)
            {
                string candidate = name + " " + n.ToString(CultureInfo.InvariantCulture);
                if (!taken.Contains(candidate)) return candidate;
            }
            return name + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        private static void EnsureNotBuiltIn(FamilyParameter parameter, string verb)
        {
            if (!FamilyValues.IsBuiltIn(parameter)) return;
            string name = FamilyValues.NameOf(parameter);
            throw new OpException(ErrorCodes.NotEditable, "'" + name + "' is a built-in parameter and cannot be " + verb + ".",
                new Dictionary<string, object> { ["param"] = name, ["reason"] = "built-in" });
        }

        /// <summary>What uses a parameter: formulas of other parameters, dimension labels and associated element parameters.</summary>
        internal static List<string> Usage(Document famDoc, FamilyManager fm, FamilyParameter parameter)
        {
            var usedBy = new List<string>();
            string name = FamilyValues.NameOf(parameter);
            var token = new Regex("(?<![\\p{L}\\p{N}_])" + Regex.Escape(name) + "(?![\\p{L}\\p{N}_])", RegexOptions.CultureInvariant);
            foreach (FamilyParameter other in FamilyValues.All(fm))
            {
                if (other.Id == parameter.Id) continue;
                string formula = SafeFormula(other);
                if (!string.IsNullOrEmpty(formula) && token.IsMatch(formula)) usedBy.Add("the formula of '" + FamilyValues.NameOf(other) + "' (" + formula + ")");
            }

            try
            {
                foreach (Dimension dimension in new FilteredElementCollector(famDoc).OfClass(typeof(Dimension)).Cast<Dimension>())
                {
                    FamilyParameter label = null;
                    try { label = dimension.FamilyLabel; } catch { label = null; }
                    if (label != null && label.Id == parameter.Id) usedBy.Add("the label of dimension " + dimension.Id.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                // Dimensions could not be inspected.
            }

            try
            {
                foreach (Parameter associated in parameter.AssociatedParameters)
                {
                    Element owner = associated?.Element;
                    string what = associated?.Definition?.Name ?? "a parameter";
                    usedBy.Add("'" + what + "' of " + (owner == null ? "an element" : (Rows.CategoryLabel(owner.Category) ?? owner.GetType().Name) + " " + owner.Id.Value.ToString(CultureInfo.InvariantCulture)));
                }
            }
            catch
            {
                // Associations could not be inspected.
            }
            return usedBy;
        }

        internal static Dictionary<string, object> ParamRow(Document famDoc, FamilyParameter parameter)
        {
            return new Dictionary<string, object>
            {
                ["name"] = FamilyValues.NameOf(parameter),
                ["kind"] = SafeIsInstance(parameter) ? "instance" : "type",
                ["data"] = FamilyNames.DataLabel(famDoc, FamilyValues.SpecOf(parameter)),
                ["group"] = FamilyNames.GroupLabel(SafeGroup(parameter)),
                ["formula"] = SafeFormula(parameter),
                ["shared"] = SafeIsShared(parameter) ? (object)SafeGuid(parameter) : null,
                ["reporting"] = SafeIsReporting(parameter)
            };
        }

        internal static ForgeTypeId SafeGroup(FamilyParameter parameter)
        {
            try { return parameter?.Definition?.GetGroupTypeId(); } catch { return null; }
        }

        internal static string SafeFormula(FamilyParameter parameter)
        {
            try { return string.IsNullOrEmpty(parameter?.Formula) ? null : parameter.Formula; } catch { return null; }
        }

        internal static bool SafeIsShared(FamilyParameter parameter)
        {
            try { return parameter != null && parameter.IsShared; } catch { return false; }
        }

        internal static bool SafeIsInstance(FamilyParameter parameter)
        {
            try { return parameter != null && parameter.IsInstance; } catch { return false; }
        }

        internal static bool SafeIsReporting(FamilyParameter parameter)
        {
            try { return parameter != null && parameter.IsReporting; } catch { return false; }
        }

        internal static string SafeGuid(FamilyParameter parameter)
        {
            try { return parameter.GUID.ToString(); } catch { return null; }
        }

        internal static Category SafeCategory(Family family)
        {
            try { return family?.FamilyCategory; } catch { return null; }
        }

        private static FamilyParameter SafeParameter(FamilyManager fm, Guid guid)
        {
            try { return fm.get_Parameter(guid); } catch { return null; }
        }

        internal static string Plural(int count, string noun)
        {
            return count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");
        }
    }

    /// <summary>Shared parameter definitions from the application's shared parameter file.</summary>
    internal static class SharedParams
    {
        /// <summary>The definition named <paramref name="value"/> (name, "Group/Name" or GUID); SHARED_PARAM_FILE_MISSING without a file.</summary>
        public static ExternalDefinition Find(ChangeContext c, string value, string field)
        {
            DefinitionFile file = null;
            string configured = null;
            try
            {
                configured = c.Ctx.RevitApp?.SharedParametersFilename;
                file = c.Ctx.RevitApp?.OpenSharedParameterFile();
            }
            catch (Exception)
            {
                file = null;
            }
            if (file == null)
            {
                throw new OpException(ErrorCodes.SharedParamFileMissing,
                    string.IsNullOrWhiteSpace(configured)
                        ? "Revit has no shared parameter file set."
                        : "Revit cannot read the shared parameter file " + configured + ".",
                    new Dictionary<string, object> { ["file"] = configured });
            }

            var all = new List<KeyValuePair<string, ExternalDefinition>>();
            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is ExternalDefinition external) all.Add(new KeyValuePair<string, ExternalDefinition>(group.Name, external));
                }
            }

            string text = value.Trim();
            if (Guid.TryParse(text, out Guid guid))
            {
                ExternalDefinition byGuid = all.Select(p => p.Value).FirstOrDefault(d => d.GUID == guid);
                if (byGuid != null) return byGuid;
                throw OpException.NotFound(field, "shared parameter", value, all.Select(p => p.Value.Name + " (" + p.Value.GUID + ")").Take(8));
            }

            string groupName = null;
            string name = text;
            int slash = text.IndexOf('/');
            if (slash > 0 && all.All(p => !Resolve.Eq(p.Value.Name, text)))
            {
                groupName = text.Substring(0, slash).Trim();
                name = text.Substring(slash + 1).Trim();
            }
            var candidates = all.Where(p => groupName == null || Resolve.Eq(p.Key, groupName)).ToList();
            List<ExternalDefinition> exact = candidates.Where(p => string.Equals(p.Value.Name, name, StringComparison.Ordinal)).Select(p => p.Value).ToList();
            if (exact.Count == 1) return exact[0];
            List<ExternalDefinition> loose = exact.Count > 1 ? exact : candidates.Where(p => Resolve.Eq(p.Value.Name, name)).Select(p => p.Value).ToList();
            if (loose.Count == 1) return loose[0];
            if (loose.Count > 1)
            {
                throw OpException.Ambiguous(field, "shared parameter", value, candidates.Where(p => loose.Contains(p.Value)).Select(p => p.Key + "/" + p.Value.Name + " (" + p.Value.GUID + ")"));
            }
            throw OpException.NotFound(field, "shared parameter", value, Resolve.Closest(name, all.Select(p => p.Value.Name)));
        }
    }
}
