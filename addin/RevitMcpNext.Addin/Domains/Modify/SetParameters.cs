using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// set_parameters (SPEC Appendix A, §5.9): values={param: value} on every target (ids incl. "project_info", from,
    /// filter) or rows=[{id, values}] per element; on:"type" writes the targets' types. Names resolve English first,
    /// then localized, then bip:/guid: tokens (ParamValues). Bare numbers on lengths are mm (never feet), areas m²,
    /// volumes m³, angles degrees. Text may use '{Other Param}' (copied from the same element) and '{n}' (1, 2, 3 ...
    /// in target order); a value that is exactly '{Param}' copies the raw value when the storage types match.
    /// A parameter that resolves on no target fails the whole call (nothing is written); targets lacking a parameter
    /// that others have are skipped with warn PARTIAL_RESULT. Type edits count their instances for the bulk rule.
    /// </summary>
    internal static class SetParametersOps
    {
        private const int MaxEchoRows = 20;
        private static readonly Regex Token = new Regex(@"\{\{|\}\}|\{(?<name>[^{}]+)\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly string[] ProjectInfoTokens = { "project_info", "projectinfo", "project info", "project information", "project_information" };

        private sealed class Assignment
        {
            public Element Target;
            public PayloadReader Values;
            public string ValuesField;
            public int N;
        }

        /// <summary>Per parameter key: where it was missing / read-only, and whether it was written anywhere.</summary>
        private sealed class KeyStats
        {
            public int Written;
            public int Unchanged;
            public readonly List<Element> Missing = new List<Element>();
            public readonly List<Element> ReadOnly = new List<Element>();
            public string Field;
        }

        [Op("set_parameters")]
        public static void Set(ChangeContext c)
        {
            PayloadReader a = ModifySupport.Args(c);
            bool onType = a.Enum("on", "instance", "instance", "type") == "type";
            List<Assignment> plan = Plan(c, a, onType, out int typedFrom);

            var stats = new Dictionary<string, KeyStats>(StringComparer.OrdinalIgnoreCase);
            var keyOrder = new List<string>();
            var changed = new List<Element>();
            var echo = new List<object>();
            int unchangedTargets = 0;

            foreach (Assignment assignment in plan)
            {
                Element element = assignment.Target;
                bool any = false;
                var written = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string key in assignment.Values.Keys)
                {
                    if (!stats.TryGetValue(key, out KeyStats stat))
                    {
                        stat = new KeyStats { Field = assignment.ValuesField + "." + key };
                        stats[key] = stat;
                        keyOrder.Add(key);
                    }
                    string field = assignment.ValuesField + "." + key;
                    Parameter parameter = ParamValues.Resolve(element, key);
                    if (parameter == null)
                    {
                        stat.Missing.Add(element);
                        continue;
                    }
                    if (parameter.IsReadOnly)
                    {
                        stat.ReadOnly.Add(element);
                        continue;
                    }

                    RefuseTemplateControlled(c.Doc, element, parameter, key);
                    object before = ParamValues.Get(parameter, c.Doc);
                    object raw = assignment.Values.Get(key);
                    Write(c, a, element, parameter, raw, assignment.N, field);
                    object after = ParamValues.Get(parameter, c.Doc);
                    if (SameValue(before, after))
                    {
                        stat.Unchanged++;
                        continue;
                    }
                    stat.Written++;
                    any = true;
                    c.Modified(element, "param:" + ParamValues.KeyOf(parameter), before);
                    written[ParamValues.KeyOf(parameter)] = after;
                }
                if (any)
                {
                    changed.Add(element);
                    if (echo.Count < MaxEchoRows) echo.Add(new List<object> { element.Id.Value, written });
                }
                else
                {
                    unchangedTargets++;
                }
            }

            FailWhenNowhere(c, plan, stats, keyOrder);

            // Type edits affect every instance of the changed types (bulk rule, SPEC §6.3).
            List<ElementType> changedTypes = changed.OfType<ElementType>().ToList();
            int affected = changedTypes.Count > 0 ? CountInstances(c.Doc, changedTypes) : 0;
            c.Affected(affected);

            var data = new Dictionary<string, object>
            {
                ["set"] = changed.Count,
                ["unchanged"] = unchangedTargets,
                ["params"] = keyOrder.Cast<object>().ToList()
            };
            if (onType) data["on"] = "type";
            if (affected > 0) data["instances_affected"] = affected;
            if (echo.Count > 0) data["values"] = echo;
            var skipped = new List<List<object>>();
            foreach (string key in keyOrder)
            {
                KeyStats stat = stats[key];
                foreach (Element element in stat.Missing) skipped.Add(ModifySupport.Skip(element.Id, "no parameter '" + key + "'"));
                foreach (Element element in stat.ReadOnly) skipped.Add(ModifySupport.Skip(element.Id, "'" + key + "' is read-only"));
            }
            ModifySupport.ReportSkipped(c, data, skipped, "set");
            c.Data(data);

            string what = changed.Count == 0 ? "nothing" : (onType || typedFrom > 0) && changed.All(e => e is ElementType)
                ? ModifySupport.Count(changed.Count, "type")
                : ModifySupport.Describe(changed);
            c.Summary((changed.Count == 0 ? "no values changed (" + string.Join(", ", keyOrder) + " already set)" : "set " + string.Join(", ", keyOrder) + " on " + what) +
                      (affected > 0 ? " (affects " + ModifySupport.Count(affected, "instance") + ")" : string.Empty) +
                      (changed.Count > 0 && unchangedTargets > 0 ? "; " + unchangedTargets.ToString(CultureInfo.InvariantCulture) + " already had these values" : string.Empty));
        }

        // -------------------------------------------------------------------------------------------------------------
        // Planning: targets and their values
        // -------------------------------------------------------------------------------------------------------------

        private static List<Assignment> Plan(ChangeContext c, PayloadReader a, bool onType, out int typedFrom)
        {
            typedFrom = 0;
            var plan = new List<Assignment>();
            bool hasRows = a.Has("rows");
            bool hasSelector = a.Has("ids") || a.Has("from") || a.Has("filter");
            if (hasRows && hasSelector) throw OpException.InvalidArgs(a.Field("rows"), "give either rows=[{id, values}] or ids/from/filter with values, not both");

            if (hasRows)
            {
                if (a.Has("values")) c.Warn(WarningCodes.ParamIgnored, a.Field("values") + " is ignored with rows (each row has its own values)");
                List<PayloadReader> rows = a.SubList("rows");
                if (rows.Count == 0) throw OpException.InvalidArgs(a.Field("rows"), "is empty; give [{id, values:{...}}]");
                for (int index = 0; index < rows.Count; index++)
                {
                    PayloadReader row = rows[index];
                    string idText = row.Str("id") ?? throw OpException.InvalidArgs(row.Field("id"), "is required (an element id, UniqueId or 'project_info')");
                    PayloadReader values = row.Sub("values") ?? throw OpException.InvalidArgs(row.Field("values"), "is required ({param: value})");
                    if (!values.Keys.Any()) throw OpException.InvalidArgs(row.Field("values"), "is empty");
                    Element element = IsProjectInfo(idText) ? ProjectInfo(c, row.Field("id")) : Resolve.Elements(c.Doc, row, "id", c.Ctx).First();
                    plan.Add(new Assignment { Target = element, Values = values, ValuesField = row.Field("values"), N = index + 1 });
                }
            }
            else
            {
                PayloadReader values = a.Sub("values") ?? throw OpException.InvalidArgs(a.Field("values"), "is required: values={param: value} (or rows=[{id, values}])",
                    new Dictionary<string, object> { ["ids"] = new List<object> { "<id>" }, ["values"] = new Dictionary<string, object> { ["Mark"] = "D1" } });
                if (!values.Keys.Any()) throw OpException.InvalidArgs(a.Field("values"), "is empty; give {param: value}");
                List<Element> targets = Targets(c, a);
                for (int index = 0; index < targets.Count; index++)
                {
                    plan.Add(new Assignment { Target = targets[index], Values = values, ValuesField = a.Field("values"), N = index + 1 });
                }
            }

            if (onType)
            {
                var typed = new List<Assignment>();
                var seen = new HashSet<long>();
                var skipped = new List<List<object>>();
                foreach (Assignment assignment in plan)
                {
                    ElementType type = assignment.Target as ElementType ?? TypeOf(c.Doc, assignment.Target);
                    if (type == null)
                    {
                        skipped.Add(ModifySupport.Skip(assignment.Target.Id, "has no type"));
                        continue;
                    }
                    if (!seen.Add(type.Id.Value)) continue;
                    typed.Add(new Assignment { Target = type, Values = assignment.Values, ValuesField = assignment.ValuesField, N = typed.Count + 1 });
                }
                if (typed.Count == 0) throw OpException.InvalidArgs(a.Field("on"), "none of the targets has a type");
                if (skipped.Count > 0) c.Warn(WarningCodes.PartialResult, skipped.Count.ToString(CultureInfo.InvariantCulture) + " target(s) have no type and were skipped", skipped.Select(s => new ElementId((long)s[0])));
                typedFrom = plan.Count;
                plan = typed;
            }

            if (!c.IsPreview && c.Doc.IsWorkshared) WorksharingGuard.EnsureEditable(c.Doc, plan.Select(p => p.Target.Id));
            return plan;
        }

        /// <summary>ids (with "project_info" tokens), from or filter.</summary>
        private static List<Element> Targets(ChangeContext c, PayloadReader a)
        {
            if (!a.Has("ids")) return ModifySupport.Targets(c, a);
            List<object> raw = a.List("ids");
            bool projectInfo = raw.Any(item => item is string text && IsProjectInfo(text));
            if (!projectInfo) return ModifySupport.Targets(c, a);

            var others = raw.Where(item => !(item is string text && IsProjectInfo(text))).ToList();
            var result = new List<Element> { ProjectInfo(c, a.Field("ids")) };
            if (others.Count > 0)
            {
                var copy = new Dictionary<string, object>(a.Raw, StringComparer.Ordinal) { ["ids"] = others };
                var reader = new PayloadReader(copy, a.Path, a.BareLengthToMm);
                result.AddRange(ModifySupport.Targets(c, reader).Where(e => e.Id != result[0].Id));
            }
            else if (a.Has("from") || a.Has("filter"))
            {
                throw OpException.InvalidArgs(a.Field("ids"), "give only one of ids, from, filter");
            }
            return result;
        }

        private static bool IsProjectInfo(string text)
        {
            string value = (text ?? string.Empty).Trim();
            return ProjectInfoTokens.Any(token => string.Equals(token, value, StringComparison.OrdinalIgnoreCase));
        }

        private static Element ProjectInfo(ChangeContext c, string field)
        {
            Element info = null;
            try { info = c.Doc.ProjectInformation; } catch { }
            if (info == null)
            {
                throw new OpException(ErrorCodes.NotFound, "'" + c.Doc.Title + "' has no project information (family documents have none).",
                    new Dictionary<string, object> { ["param"] = field, ["kind"] = "element", ["value"] = "project_info", ["candidates"] = new List<object>() });
            }
            return info;
        }

        private static ElementType TypeOf(Document doc, Element element)
        {
            try
            {
                ElementId id = element.GetTypeId();
                return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id) as ElementType;
            }
            catch
            {
                return null;
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Writing one value
        // -------------------------------------------------------------------------------------------------------------

        private static void Write(ChangeContext c, PayloadReader a, Element element, Parameter parameter, object raw, int n, string field)
        {
            if (raw is string text && text.IndexOf('{') >= 0)
            {
                Match whole = Token.Match(text);
                if (whole.Success && whole.Index == 0 && whole.Length == text.Length && whole.Groups["name"].Success)
                {
                    string name = whole.Groups["name"].Value.Trim();
                    if (name.Equals("n", StringComparison.OrdinalIgnoreCase))
                    {
                        ParamValues.Set(parameter, parameter.StorageType == StorageType.String ? (object)n.ToString(CultureInfo.InvariantCulture) : n, a.BareLengthToMm, field);
                        return;
                    }
                    Parameter source = Source(element, name, field);
                    if (CopyRaw(source, parameter)) return;
                }
                raw = Expand(element, text, n, field);
            }
            ParamValues.Set(parameter, raw, a.BareLengthToMm, field);
        }

        /// <summary>'{Param}' alone: copies the stored value when both parameters store the same kind of value.</summary>
        private static bool CopyRaw(Parameter source, Parameter target)
        {
            if (source.StorageType != target.StorageType) return false;
            try
            {
                switch (source.StorageType)
                {
                    case StorageType.String:
                        return target.Set(source.AsString() ?? string.Empty);
                    case StorageType.Integer:
                        return target.Set(source.AsInteger());
                    case StorageType.ElementId:
                        return target.Set(source.AsElementId());
                    case StorageType.Double:
                        ForgeTypeId a = ParamValues.SpecOf(source), b = ParamValues.SpecOf(target);
                        if (a == null || b == null || a != b) return false;
                        return target.Set(source.AsDouble());
                    default:
                        return false;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        /// <summary>Replaces {n} and {Param} tokens ({{ and }} are literal braces).</summary>
        private static string Expand(Element element, string text, int n, string field)
        {
            var builder = new StringBuilder(text.Length + 16);
            int last = 0;
            foreach (Match match in Token.Matches(text))
            {
                builder.Append(text, last, match.Index - last);
                last = match.Index + match.Length;
                if (match.Value == "{{") { builder.Append('{'); continue; }
                if (match.Value == "}}") { builder.Append('}'); continue; }
                string name = match.Groups["name"].Value.Trim();
                if (name.Equals("n", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append(n.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
                builder.Append(DisplayOf(Source(element, name, field)));
            }
            builder.Append(text, last, text.Length - last);
            return builder.ToString();
        }

        private static Parameter Source(Element element, string name, string field)
        {
            Parameter source = ParamValues.Resolve(element, name);
            if (source != null) return source;
            IEnumerable<string> names = element.Parameters.Cast<Parameter>().Where(p => p?.Definition != null).Select(ParamValues.KeyOf);
            throw OpException.InvalidArgs(field, "'{" + name + "}' names no parameter of element " + element.Id.Value.ToString(CultureInfo.InvariantCulture) +
                " (closest: " + string.Join(", ", Resolve.Closest(name, names, 5)) + "; use {n} for 1,2,3...)");
        }

        private static string DisplayOf(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue) return string.Empty;
            if (parameter.StorageType == StorageType.String) return parameter.AsString() ?? string.Empty;
            string display = ParamValues.Display(parameter);
            if (!string.IsNullOrEmpty(display)) return display;
            return Convert.ToString(ParamValues.Get(parameter), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Outcome checks
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>A parameter written nowhere because no target has it (or it is read-only everywhere) fails the call.</summary>
        private static void FailWhenNowhere(ChangeContext c, List<Assignment> plan, Dictionary<string, KeyStats> stats, List<string> keys)
        {
            foreach (string key in keys)
            {
                KeyStats stat = stats[key];
                if (stat.Written > 0 || stat.Unchanged > 0) continue;
                if (stat.Missing.Count > 0 && stat.ReadOnly.Count == 0)
                {
                    Element sample = stat.Missing[0];
                    IEnumerable<string> names = sample.Parameters.Cast<Parameter>().Where(p => p?.Definition != null && !p.IsReadOnly).Select(ParamValues.KeyOf);
                    List<string> closest = Resolve.Closest(key, names, 6);
                    // No `candidates` on purpose: the generic NOT_FOUND fix would rewrite a nested values key blindly.
                    throw new OpException(ErrorCodes.NotFound,
                        "No target has a parameter '" + key + "' (checked " + ModifySupport.Count(stat.Missing.Count, "element") + ", e.g. " +
                        (Rows.CategoryLabel(sample) ?? "element") + " " + sample.Id.Value.ToString(CultureInfo.InvariantCulture) + "); nothing was written." +
                        (closest.Count > 0 ? " Closest: " + string.Join(", ", closest) + "." : string.Empty) +
                        (plan.Any(p => !(p.Target is ElementType)) ? " Type parameters need on:\"type\"." : string.Empty),
                        new Dictionary<string, object> { ["param"] = stat.Field, ["kind"] = "parameter", ["value"] = key, ["closest"] = closest.Cast<object>().ToList() });
                }
                if (stat.ReadOnly.Count > 0)
                {
                    throw OpException.InvalidArgs(stat.Field, "'" + key + "' is read-only on " + ModifySupport.Count(stat.ReadOnly.Count, "target") +
                        " (computed or driven by the type/host); nothing was written");
                }
            }
        }

        /// <summary>A view parameter controlled by the view's template cannot be set on the view (TEMPLATE_CONTROLLED).</summary>
        private static void RefuseTemplateControlled(Document doc, Element element, Parameter parameter, string key)
        {
            if (!(element is View view)) return;
            try
            {
                ElementId templateId = view.ViewTemplateId;
                if (templateId == null || templateId == ElementId.InvalidElementId || view.IsTemplate) return;
                if (!(doc.GetElement(templateId) is View template)) return;
                var controlled = new HashSet<long>(template.GetTemplateParameterIds().Select(id => id.Value));
                foreach (ElementId id in template.GetNonControlledTemplateParameterIds()) controlled.Remove(id.Value);
                if (!controlled.Contains(parameter.Id.Value)) return;
                throw new OpException(ErrorCodes.TemplateControlled,
                    "'" + key + "' of view '" + view.Name + "' is controlled by its view template '" + template.Name + "'; nothing was written.",
                    new Dictionary<string, object> { ["template"] = template.Name, ["template_id"] = template.Id.Value, ["view"] = view.Name, ["param"] = key });
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Not a template-capable view; let Revit decide.
            }
        }

        private static bool SameValue(object before, object after)
        {
            if (before == null || after == null) return before == null && after == null;
            if (PayloadReader.TryNumber(before, out double x) && PayloadReader.TryNumber(after, out double y) && !(before is string) && !(after is string))
            {
                return Math.Abs(x - y) < 1e-9;
            }
            return string.Equals(Convert.ToString(before, CultureInfo.InvariantCulture), Convert.ToString(after, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        /// <summary>Instances of the given types (one pass over the model).</summary>
        private static int CountInstances(Document doc, List<ElementType> types)
        {
            var ids = new HashSet<long>(types.Select(t => t.Id.Value));
            int count = 0;
            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                ElementId typeId;
                try { typeId = element.GetTypeId(); } catch { continue; }
                if (typeId != null && ids.Contains(typeId.Value)) count++;
            }
            return count;
        }
    }
}
