using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpNext.Addin
{
    /// <summary>One parsed where condition: 'Param op value'.</summary>
    internal sealed class WhereRule
    {
        public string Param { get; set; }
        /// <summary>= != &gt; &gt;= &lt; &lt;= contains startswith empty notempty.</summary>
        public string Op { get; set; }
        public string Value { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Compiles the find_elements filter keys (SPEC §9.5: category, class, level, type, family, view, hidden, workset,
    /// phase, design_option, box, where, ids, link, types) into native quick/slow filters (multicategory, multiclass,
    /// level, workset, design option, bounding box, ElementParameterFilter for where rules when the parameter can be
    /// typed) plus managed post-filters for the rest (wildcards, family names, untyped where rules). Count() uses
    /// GetElementCount when no post-filter remains. Elements() honours the request deadline (Truncated).
    /// </summary>
    internal sealed class ElementQuery
    {
        private static readonly Regex WherePattern = new Regex(
            @"^\s*(?<param>.+?)\s*(?<op>!=|>=|<=|=|>|<|\s(?:contains|startswith|notempty|empty)\b)\s*(?<value>.*?)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly List<ElementFilter> _native = new List<ElementFilter>();
        private readonly List<Func<Element, bool>> _post = new List<Func<Element, bool>>();
        private readonly List<string> _description = new List<string>();
        private List<ElementId> _ids;
        private View _view;
        private bool _hidden;

        private ElementQuery(Document doc)
        {
            Doc = doc;
        }

        /// <summary>The document searched (the link document for link queries).</summary>
        public Document Doc { get; private set; }
        public bool IsLink => LinkInstance != null;
        public RevitLinkInstance LinkInstance { get; private set; }
        /// <summary>Link → host transform (identity for host queries).</summary>
        public Transform LinkTransform { get; private set; } = Transform.Identity;
        public string LinkName { get; private set; }
        public bool ElementTypes { get; private set; }
        public bool HasPostFilter => _post.Count > 0 || (_hidden && _view != null);
        /// <summary>True when Elements()/Count() stopped at the request deadline.</summary>
        public bool Truncated { get; private set; }
        /// <summary>The single category when exactly one was given (for default columns).</summary>
        public BuiltInCategory? SingleCategory { get; private set; }
        public IReadOnlyList<string> Description => _description;

        /// <summary>Compiles a filter object (find_elements args or a bulk-selector filter{}).</summary>
        public static ElementQuery Compile(Document hostDoc, PayloadReader args, RequestContext ctx = null, UIDocument uidoc = null)
        {
            if (hostDoc == null) throw new ArgumentNullException(nameof(hostDoc));
            args = args ?? new PayloadReader(new Dictionary<string, object>());
            var query = new ElementQuery(hostDoc);

            string link = args.Str("link");
            if (!string.IsNullOrWhiteSpace(link))
            {
                RevitLinkInstance instance = Resolve.Link(hostDoc, link, args.Field("link"));
                Document linkDoc = instance.GetLinkDocument();
                if (linkDoc == null)
                {
                    throw new OpException(ErrorCodes.NotFound, "The link '" + instance.Name + "' is not loaded.",
                        new Dictionary<string, object> { ["param"] = args.Field("link"), ["kind"] = "link", ["value"] = link, ["candidates"] = new List<object>() });
                }
                query.Doc = linkDoc;
                query.LinkInstance = instance;
                query.LinkTransform = instance.GetTotalTransform();
                query.LinkName = instance.Name;
                query._description.Add("in link " + instance.Name);
            }
            Document doc = query.Doc;

            query.ElementTypes = args.Bool("types", false);

            if (args.Has("ids"))
            {
                var tokens = new List<string>();
                List<long> numeric = args.IdValues("ids", tokens);
                var ids = numeric.Select(id => new ElementId(id)).ToList();
                foreach (string token in tokens)
                {
                    Element byUnique = null;
                    try { byUnique = doc.GetElement(token); } catch { }
                    if (byUnique != null) ids.Add(byUnique.Id);
                }
                query._ids = ids.Where(id => Resolve.SafeGet(doc, id) != null).Distinct().ToList();
                query._description.Add(query._ids.Count.ToString(CultureInfo.InvariantCulture) + " ids");
            }

            List<string> categories = args.Strs("category");
            if (categories.Count > 0)
            {
                List<BuiltInCategory> ids = Resolve.Categories(doc, categories, args.Field("category"));
                query._native.Add(ids.Count == 1 ? (ElementFilter)new ElementCategoryFilter(ids[0]) : new ElementMulticategoryFilter(ids));
                if (ids.Count == 1) query.SingleCategory = ids[0];
                query._description.Add(string.Join(", ", ids.Select(id => EnglishAliases.LabelOf(id) ?? id.ToString())));
            }

            List<string> classes = args.Strs("class");
            if (classes.Count > 0)
            {
                var types = classes.Select(name => ResolveClass(name, args.Field("class"))).Distinct().ToList();
                query._native.Add(types.Count == 1 ? (ElementFilter)new ElementClassFilter(types[0]) : new ElementMulticlassFilter(types));
                query._description.Add("class " + string.Join("/", types.Select(t => t.Name)));
            }

            List<string> levels = args.Strs("level");
            if (levels.Count > 0)
            {
                var filters = levels.Select(value => (ElementFilter)new ElementLevelFilter(Resolve.Level(doc, value, args.Field("level"), uidoc).Id)).ToList();
                query._native.Add(filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters));
                query._description.Add("on " + string.Join(", ", levels));
            }

            List<string> worksets = args.Strs("workset");
            if (worksets.Count > 0)
            {
                var filters = worksets.Select(value => (ElementFilter)new ElementWorksetFilter(Resolve.Workset(doc, value, args.Field("workset")).Id)).ToList();
                query._native.Add(filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters));
                query._description.Add("workset " + string.Join(", ", worksets));
            }

            List<string> options = args.Strs("design_option");
            if (options.Count > 0)
            {
                var filters = options.Select(value => (ElementFilter)new ElementDesignOptionFilter(
                    value.Equals("main", StringComparison.OrdinalIgnoreCase) ? ElementId.InvalidElementId : Resolve.DesignOption(doc, value, args.Field("design_option")).Id)).ToList();
                query._native.Add(filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters));
                query._description.Add("design option " + string.Join(", ", options));
            }

            string phase = args.Str("phase");
            if (!string.IsNullOrWhiteSpace(phase))
            {
                Phase resolved = Resolve.Phase(doc, phase, args.Field("phase"));
                query._native.Add(new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(new ElementId(BuiltInParameter.PHASE_CREATED), resolved.Id)));
                query._description.Add("phase " + resolved.Name);
            }

            if (args.Has("box"))
            {
                double[] box = args.BoxFt("box");
                XYZ min, max;
                if (box.Length == 4)
                {
                    min = new XYZ(Math.Min(box[0], box[2]), Math.Min(box[1], box[3]), -1e5);
                    max = new XYZ(Math.Max(box[0], box[2]), Math.Max(box[1], box[3]), 1e5);
                }
                else
                {
                    min = new XYZ(Math.Min(box[0], box[3]), Math.Min(box[1], box[4]), Math.Min(box[2], box[5]));
                    max = new XYZ(Math.Max(box[0], box[3]), Math.Max(box[1], box[4]), Math.Max(box[2], box[5]));
                }
                if (query.IsLink)
                {
                    Transform inverse = query.LinkTransform.Inverse;
                    var corners = new[]
                    {
                        new XYZ(min.X, min.Y, min.Z), new XYZ(max.X, min.Y, min.Z), new XYZ(min.X, max.Y, min.Z), new XYZ(max.X, max.Y, min.Z),
                        new XYZ(min.X, min.Y, max.Z), new XYZ(max.X, min.Y, max.Z), new XYZ(min.X, max.Y, max.Z), new XYZ(max.X, max.Y, max.Z)
                    }.Select(inverse.OfPoint).ToList();
                    min = corners.Aggregate(Geometry.Min);
                    max = corners.Aggregate(Geometry.Max);
                }
                query._native.Add(new BoundingBoxIntersectsFilter(new Outline(min, max)));
                query._description.Add("in box");
            }

            string viewValue = args.Str("view");
            if (!string.IsNullOrWhiteSpace(viewValue))
            {
                if (query.IsLink)
                {
                    ctx?.Warn(WarningCodes.ParamIgnored, "view is ignored for link searches");
                }
                else
                {
                    query._view = Resolve.View(doc, viewValue, args.Field("view"), uidoc);
                    query._hidden = args.Bool("hidden", false);
                    query._description.Add("in view " + query._view.Name + (query._hidden ? " (incl. hidden)" : string.Empty));
                }
            }

            string type = args.Str("type");
            if (!string.IsNullOrWhiteSpace(type)) query.AddTypeFilter(type, args.Field("type"));

            string family = args.Str("family");
            if (!string.IsNullOrWhiteSpace(family))
            {
                Regex pattern = Wildcard(family);
                query._post.Add(element => pattern.IsMatch(Rows.FamilyName(element) ?? string.Empty));
                query._description.Add("family " + family);
            }

            foreach (string condition in args.Strs("where"))
            {
                query.AddWhere(ParseWhere(condition, args.Field("where")));
            }
            return query;
        }

        /// <summary>A fresh collector with all native filters applied (instances or types per the types flag).</summary>
        public FilteredElementCollector NewCollector()
        {
            FilteredElementCollector collector;
            if (_ids != null && _ids.Count == 0)
            {
                // An empty id list matches nothing (collectors cannot be built from an empty set).
                var none = new LogicalAndFilter(new ElementCategoryFilter(BuiltInCategory.OST_Walls), new ElementCategoryFilter(BuiltInCategory.OST_Walls, true));
                return new FilteredElementCollector(Doc).WherePasses(none);
            }
            if (_ids != null)
            {
                collector = new FilteredElementCollector(Doc, _ids);
            }
            else if (_view != null && !_hidden)
            {
                collector = new FilteredElementCollector(Doc, _view.Id);
            }
            else
            {
                collector = new FilteredElementCollector(Doc);
            }

            collector = ElementTypes ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
            foreach (ElementFilter filter in _native) collector = collector.WherePasses(filter);
            return collector;
        }

        /// <summary>Matching elements (native filters, then post-filters); stops at the deadline (Truncated).</summary>
        public IEnumerable<Element> Elements(RequestContext ctx = null)
        {
            Truncated = false;
            int index = 0;
            foreach (Element element in NewCollector())
            {
                if (ctx != null && ctx.ShouldYield(index++))
                {
                    Truncated = true;
                    yield break;
                }
                if (Matches(element)) yield return element;
            }
        }

        /// <summary>Exact count: GetElementCount when no post-filter remains, else a scan (Truncated at the deadline).</summary>
        public int Count(RequestContext ctx = null)
        {
            if (!HasPostFilter)
            {
                Truncated = false;
                return NewCollector().GetElementCount();
            }
            return Elements(ctx).Count();
        }

        public List<ElementId> Ids(int limit = int.MaxValue, RequestContext ctx = null)
        {
            if (!HasPostFilter && limit == int.MaxValue) return NewCollector().ToElementIds().ToList();
            return Elements(ctx).Take(Math.Max(0, limit)).Select(element => element.Id).ToList();
        }

        /// <summary>Post-filters only (native filters are assumed to have passed).</summary>
        public bool Matches(Element element)
        {
            if (element == null) return false;
            if (_hidden && _view != null)
            {
                bool inView;
                try { inView = element.IsHidden(_view) || IsVisibleInView(element); } catch { inView = false; }
                if (!inView) return false;
            }
            foreach (Func<Element, bool> predicate in _post)
            {
                bool ok;
                try { ok = predicate(element); } catch { ok = false; }
                if (!ok) return false;
            }
            return true;
        }

        private bool IsVisibleInView(Element element)
        {
            return new FilteredElementCollector(Doc, _view.Id).WherePasses(new ElementIdSetFilter(new List<ElementId> { element.Id })).GetElementCount() > 0;
        }

        private void AddTypeFilter(string type, string field)
        {
            _description.Add("type " + type);
            if (type.IndexOf('*') >= 0)
            {
                Regex pattern = Wildcard(type);
                _post.Add(element =>
                {
                    ElementType elementType = element as ElementType ?? Rows.TypeOf(element);
                    return elementType != null && (pattern.IsMatch(elementType.Name ?? string.Empty) || pattern.IsMatch(Resolve.FullTypeName(elementType)));
                });
                return;
            }

            List<ElementType> matches = new FilteredElementCollector(Doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => Resolve.Eq(t.Name, type) || Resolve.Eq(Resolve.FullTypeName(t), type) || t.Id.Value.ToString(CultureInfo.InvariantCulture) == type.Trim())
                .ToList();
            if (matches.Count == 0)
            {
                IEnumerable<string> names = new FilteredElementCollector(Doc).WhereElementIsElementType().Cast<ElementType>().Select(Resolve.FullTypeName);
                throw OpException.NotFound(field, "type", type, Resolve.Closest(type, names));
            }
            if (ElementTypes)
            {
                _native.Add(new ElementIdSetFilter(matches.Select(t => t.Id).ToList()));
                return;
            }
            var rules = matches.Select(t => (ElementFilter)new ElementParameterFilter(
                ParameterFilterRuleFactory.CreateEqualsRule(new ElementId(BuiltInParameter.ELEM_TYPE_PARAM), t.Id))).ToList();
            _native.Add(rules.Count == 1 ? rules[0] : new LogicalOrFilter(rules));
        }

        // ---------------------------------------------------------------------------------------------------------
        // where rules
        // ---------------------------------------------------------------------------------------------------------

        public static WhereRule ParseWhere(string text, string field = "where")
        {
            Match match = WherePattern.Match(text ?? string.Empty);
            if (!match.Success)
            {
                throw OpException.InvalidArgs(field, "'" + text + "' is not 'Param op value' (op: = != > >= < <= contains startswith empty notempty)");
            }
            string op = match.Groups["op"].Value.Trim().ToLowerInvariant();
            string value = match.Groups["value"].Value.Trim().Trim('"', '\'');
            if ((op == "empty" || op == "notempty") && value.Length > 0)
            {
                throw OpException.InvalidArgs(field, "'" + text + "': " + op + " takes no value");
            }
            return new WhereRule { Param = match.Groups["param"].Value.Trim(), Op = op, Value = value, Text = text };
        }

        private void AddWhere(WhereRule rule)
        {
            _description.Add(rule.Text);
            ElementFilter native = TryNative(rule);
            if (native != null)
            {
                _native.Add(native);
                return;
            }
            _post.Add(element => Evaluate(element, rule));
        }

        /// <summary>
        /// Builds an ElementParameterFilter when the parameter is a known built-in (English alias, bip: token or enum
        /// name) and a sample element types it; otherwise null (post-filter).
        /// </summary>
        private ElementFilter TryNative(WhereRule rule)
        {
            List<BuiltInParameter> candidates = BuiltInsFor(rule.Param);
            if (candidates.Count == 0) return null;
            var filters = new List<ElementFilter>();
            foreach (BuiltInParameter bip in candidates)
            {
                Parameter sample = SampleParameter(bip);
                if (sample == null) continue;
                FilterRule filterRule = BuildRule(bip, sample, rule);
                if (filterRule == null) return null;
                filters.Add(new ElementParameterFilter(filterRule));
            }
            if (filters.Count == 0) return null;
            return filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters);
        }

        private static List<BuiltInParameter> BuiltInsFor(string param)
        {
            string text = (param ?? string.Empty).Trim();
            if (text.StartsWith("bip:", StringComparison.OrdinalIgnoreCase))
            {
                return Enum.TryParse(text.Substring(4), true, out BuiltInParameter bip) ? new List<BuiltInParameter> { bip } : new List<BuiltInParameter>();
            }
            List<BuiltInParameter> aliases = EnglishAliases.Parameters(text).ToList();
            if (aliases.Count > 0) return aliases;
            if (Enum.TryParse(text, true, out BuiltInParameter token) && Enum.IsDefined(typeof(BuiltInParameter), token) && text.Contains("_"))
            {
                return new List<BuiltInParameter> { token };
            }
            return new List<BuiltInParameter>();
        }

        private Parameter SampleParameter(BuiltInParameter bip)
        {
            try
            {
                FilteredElementCollector collector = NewCollector();
                int scanned = 0;
                foreach (Element element in collector)
                {
                    Parameter parameter = element.get_Parameter(bip);
                    if (parameter != null) return parameter;
                    if (++scanned > 200) break;
                }
            }
            catch
            {
                // Fall back to a post-filter.
            }
            return null;
        }

        private static FilterRule BuildRule(BuiltInParameter bip, Parameter sample, WhereRule rule)
        {
            var id = new ElementId(bip);
            try
            {
                if (rule.Op == "empty") return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(id);
                if (rule.Op == "notempty") return ParameterFilterRuleFactory.CreateHasValueParameterRule(id);

                switch (sample.StorageType)
                {
                    case StorageType.String:
                        switch (rule.Op)
                        {
                            case "=": return ParameterFilterRuleFactory.CreateEqualsRule(id, rule.Value);
                            case "!=": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, rule.Value);
                            case "contains": return ParameterFilterRuleFactory.CreateContainsRule(id, rule.Value);
                            case "startswith": return ParameterFilterRuleFactory.CreateBeginsWithRule(id, rule.Value);
                            case ">": return ParameterFilterRuleFactory.CreateGreaterRule(id, rule.Value);
                            case ">=": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, rule.Value);
                            case "<": return ParameterFilterRuleFactory.CreateLessRule(id, rule.Value);
                            case "<=": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, rule.Value);
                        }
                        return null;
                    case StorageType.Integer:
                    {
                        int value;
                        if (ParamValues.IsYesNo(sample))
                        {
                            bool? flag = new PayloadReader(new Dictionary<string, object> { ["v"] = rule.Value }, null, 1.0).BoolOpt("v");
                            if (!flag.HasValue) return null;
                            value = flag.Value ? 1 : 0;
                        }
                        else if (!int.TryParse(rule.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                        {
                            return null;
                        }
                        switch (rule.Op)
                        {
                            case "=": return ParameterFilterRuleFactory.CreateEqualsRule(id, value);
                            case "!=": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, value);
                            case ">": return ParameterFilterRuleFactory.CreateGreaterRule(id, value);
                            case ">=": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, value);
                            case "<": return ParameterFilterRuleFactory.CreateLessRule(id, value);
                            case "<=": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, value);
                        }
                        return null;
                    }
                    case StorageType.Double:
                    {
                        if (!TryInternal(sample, rule.Value, out double value)) return null;
                        const double epsilon = 1e-4;
                        switch (rule.Op)
                        {
                            case "=": return ParameterFilterRuleFactory.CreateEqualsRule(id, value, epsilon);
                            case "!=": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, value, epsilon);
                            case ">": return ParameterFilterRuleFactory.CreateGreaterRule(id, value, epsilon);
                            case ">=": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, value, epsilon);
                            case "<": return ParameterFilterRuleFactory.CreateLessRule(id, value, epsilon);
                            case "<=": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, value, epsilon);
                        }
                        return null;
                    }
                    case StorageType.ElementId:
                    {
                        long? raw = RevitMcpNext.Contracts.Wire.ToLong(rule.Value);
                        if (!raw.HasValue) return null;
                        var value = new ElementId(raw.Value);
                        switch (rule.Op)
                        {
                            case "=": return ParameterFilterRuleFactory.CreateEqualsRule(id, value);
                            case "!=": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, value);
                        }
                        return null;
                    }
                }
            }
            catch
            {
                // Unsupported rule shape for this parameter: post-filter instead.
            }
            return null;
        }

        private static bool TryInternal(Parameter parameter, string text, out double value)
        {
            value = 0;
            ForgeTypeId spec = ParamValues.SpecOf(parameter);
            switch (Units.KindOf(spec))
            {
                case ValueKind.Length:
                    if (!Units.TryParseLength(text, 1.0, out double mm)) return false;
                    value = Units.MmToFt(mm);
                    return true;
                case ValueKind.Area:
                    if (!Units.TryParseArea(text, out double m2)) return false;
                    value = Units.M2ToFt2(m2);
                    return true;
                case ValueKind.Volume:
                    if (!Units.TryParseVolume(text, out double m3)) return false;
                    value = Units.M3ToFt3(m3);
                    return true;
                case ValueKind.Angle:
                    if (!Units.TryParseAngle(text, out double degrees)) return false;
                    value = Units.DegToRad(degrees);
                    return true;
                case ValueKind.Number:
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                default:
                    return false;
            }
        }

        /// <summary>Managed evaluation of a where rule (instance parameter first, then type parameter).</summary>
        public static bool Evaluate(Element element, WhereRule rule)
        {
            Parameter parameter = ParamValues.Resolve(element, rule.Param) ?? ParamValues.Resolve(Rows.TypeOf(element), rule.Param);
            if (parameter == null)
            {
                object special = SpecialValue(element, rule.Param);
                if (special == null) return rule.Op == "empty";
                return CompareText(Convert.ToString(special, CultureInfo.InvariantCulture), rule);
            }

            bool hasValue = parameter.HasValue && !(parameter.StorageType == StorageType.String && string.IsNullOrEmpty(parameter.AsString())) &&
                            !(parameter.StorageType == StorageType.ElementId && parameter.AsElementId() == ElementId.InvalidElementId);
            if (rule.Op == "empty") return !hasValue;
            if (rule.Op == "notempty") return hasValue;
            if (!hasValue) return rule.Op == "!=";

            switch (parameter.StorageType)
            {
                case StorageType.Double:
                {
                    if (!TryInternal(parameter, rule.Value, out double target)) return CompareText(ParamValues.Display(parameter), rule);
                    return CompareNumbers(parameter.AsDouble(), target, rule.Op, 1e-4);
                }
                case StorageType.Integer:
                {
                    if (ParamValues.IsYesNo(parameter))
                    {
                        bool? flag = new PayloadReader(new Dictionary<string, object> { ["v"] = rule.Value }, null, 1.0).BoolOpt("v");
                        if (!flag.HasValue) return false;
                        bool actual = parameter.AsInteger() != 0;
                        return rule.Op == "!=" ? actual != flag.Value : rule.Op == "=" && actual == flag.Value;
                    }
                    if (double.TryParse(rule.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    {
                        return CompareNumbers(parameter.AsInteger(), number, rule.Op, 1e-9);
                    }
                    return CompareText(ParamValues.Display(parameter), rule);
                }
                case StorageType.ElementId:
                {
                    ElementId id = parameter.AsElementId();
                    long? raw = RevitMcpNext.Contracts.Wire.ToLong(rule.Value);
                    if (raw.HasValue && (rule.Op == "=" || rule.Op == "!=")) return (id.Value == raw.Value) == (rule.Op == "=");
                    string name = Resolve.SafeGet(element.Document, id)?.Name ?? ParamValues.Display(parameter);
                    return CompareText(name, rule);
                }
                default:
                    return CompareText(parameter.AsString(), rule);
            }
        }

        private static object SpecialValue(Element element, string param)
        {
            switch ((param ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "name": return element.Name;
                case "category": return Rows.CategoryLabel(element);
                case "family": return Rows.FamilyName(element);
                case "type": return Rows.TypeName(element);
                case "level": return Rows.LevelName(element);
                case "id": return element.Id.Value;
                default: return null;
            }
        }

        private static bool CompareNumbers(double actual, double target, string op, double epsilon)
        {
            switch (op)
            {
                case "=": return Math.Abs(actual - target) <= epsilon;
                case "!=": return Math.Abs(actual - target) > epsilon;
                case ">": return actual > target + epsilon;
                case ">=": return actual >= target - epsilon;
                case "<": return actual < target - epsilon;
                case "<=": return actual <= target + epsilon;
                default: return false;
            }
        }

        private static bool CompareText(string actual, WhereRule rule)
        {
            string value = actual ?? string.Empty;
            switch (rule.Op)
            {
                case "=": return string.Equals(value, rule.Value, StringComparison.OrdinalIgnoreCase);
                case "!=": return !string.Equals(value, rule.Value, StringComparison.OrdinalIgnoreCase);
                case "contains": return value.IndexOf(rule.Value, StringComparison.OrdinalIgnoreCase) >= 0;
                case "startswith": return value.StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase);
                case "empty": return value.Length == 0;
                case "notempty": return value.Length > 0;
                case ">": return string.Compare(value, rule.Value, StringComparison.OrdinalIgnoreCase) > 0;
                case ">=": return string.Compare(value, rule.Value, StringComparison.OrdinalIgnoreCase) >= 0;
                case "<": return string.Compare(value, rule.Value, StringComparison.OrdinalIgnoreCase) < 0;
                case "<=": return string.Compare(value, rule.Value, StringComparison.OrdinalIgnoreCase) <= 0;
                default: return false;
            }
        }

        private static Type ResolveClass(string name, string field)
        {
            string text = (name ?? string.Empty).Trim();
            foreach (string ns in new[] { "Autodesk.Revit.DB.", "Autodesk.Revit.DB.Architecture.", "Autodesk.Revit.DB.Mechanical.",
                                          "Autodesk.Revit.DB.Plumbing.", "Autodesk.Revit.DB.Electrical.", "Autodesk.Revit.DB.Structure." })
            {
                Type type = typeof(Element).Assembly.GetType(ns + text, false, true);
                if (type != null && typeof(Element).IsAssignableFrom(type)) return type;
            }
            throw OpException.NotFound(field, "class", name, new[] { "Wall", "FamilyInstance", "Floor", "Ceiling", "RoofBase", "Room", "Level", "Grid", "View", "ViewSheet", "Pipe", "Duct" });
        }

        private static Regex Wildcard(string pattern)
        {
            string escaped = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(escaped, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
