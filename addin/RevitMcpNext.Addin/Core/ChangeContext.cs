using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpNext.Addin
{
    /// <summary>An element removed by an op, with its English category label when known (blast by_category).</summary>
    internal sealed class DeletedRecord
    {
        public long Id { get; set; }
        /// <summary>English category label; null when unknown.</summary>
        public string Category { get; set; }
        /// <summary>True for model/annotation elements (counted by the delete rule); false for internal elements.</summary>
        public bool Counted { get; set; } = true;
        /// <summary>True when the handler asked to delete this element (false for cascaded dependents).</summary>
        public bool Target { get; set; } = true;
    }

    /// <summary>
    /// What a write handler (<c>static void X(ChangeContext c)</c>) works with, one per op invocation inside the
    /// ChangeEngine (D3 §2.7). Readers resolve names and units (mm in → feet), recorders feed the blast rules and the
    /// response, and Output() publishes named values for later $refs. Throw <c>c.Error(...)</c> to fail the op.
    /// </summary>
    internal sealed class ChangeContext
    {
        private readonly List<ElementId> _created = new List<ElementId>();
        private readonly List<ElementId> _modified = new List<ElementId>();
        private readonly List<DeletedRecord> _deleted = new List<DeletedRecord>();
        private readonly HashSet<long> _createdSet = new HashSet<long>();
        private readonly HashSet<long> _modifiedSet = new HashSet<long>();
        private readonly HashSet<long> _deletedSet = new HashSet<long>();
        private bool _needsRegeneration;

        internal ChangeContext(RequestContext ctx, PayloadReader args, OpMeta meta, int index)
        {
            Ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            Args = args ?? throw new ArgumentNullException(nameof(args));
            Meta = meta;
            Index = index;
        }

        public RequestContext Ctx { get; }
        public Document Doc => Ctx.Doc;
        public UIApplication App => Ctx.App;
        public PayloadReader Args { get; }
        public OpMeta Meta { get; }
        /// <summary>Op index inside a change_set (0 for single calls).</summary>
        public int Index { get; }
        public string Key => Meta?.Key;
        public string Tool => Meta?.Tool;
        public string Op => Meta?.Op;
        /// <summary>True in preview mode: everything is rolled back afterwards; do not touch files or other documents.</summary>
        public bool IsPreview => Ctx.IsPreview;

        /// <summary>Opt in to resolving error failures that have a default resolution (instead of rolling back).</summary>
        public bool ResolveErrors { get; set; }

        /// <summary>Restricts <see cref="ResolveErrors"/> to these failure definition ids (null = any resolvable error).</summary>
        public Func<Guid, bool> ResolvableFailures { get; set; }

        /// <summary>Failure capture for ops with tx "own" (pass it to the edit scope's Commit).</summary>
        public FailureCapture Failures { get; } = new FailureCapture();

        // Readers -----------------------------------------------------------------------------------------------

        public string Str(string key, string defaultValue = null) => Args.Str(key, defaultValue);
        public int Int(string key, int? defaultValue = null) => Args.Int(key, defaultValue);
        public bool Bool(string key, bool defaultValue = false) => Args.Bool(key, defaultValue);
        public double Num(string key, double? defaultValue = null) => Args.Num(key, defaultValue);
        public bool Has(string key) => Args.Has(key);

        /// <summary>A length in mm (or with a unit) → internal feet.</summary>
        public double Mm(string key, double? defaultMm = null) => Args.Mm(key, defaultMm);

        /// <summary>An angle in degrees → radians.</summary>
        public double Deg(string key, double? defaultDegrees = null) => Args.Deg(key, defaultDegrees);

        /// <summary>A point in mm → XYZ feet; z defaults to <paramref name="defaultZft"/> (feet) or 0.</summary>
        public XYZ Point(string key, double? defaultZft = null) => Args.Point(key, defaultZft);

        public List<XYZ> Points(string key, double? defaultZft = null) => Args.Points(key, defaultZft);

        /// <summary>"active" | id | exact name | sheet number (for sheets: the sheet itself).</summary>
        public View View(string key = "view", bool required = true)
        {
            string value = Args.Str(key);
            if (value == null)
            {
                if (required) throw Error(ErrorCodes.InvalidArgs, Args.Field(key) + " is required (a view name, id or \"active\").",
                    new Dictionary<string, object> { ["param"] = Args.Field(key), ["reason"] = "required" });
                return null;
            }
            return Resolve.View(Doc, value, Args.Field(key), Ctx.UiDoc);
        }

        /// <summary>id | name | "active" (the active plan's level) — optional values return null when absent.</summary>
        public Level Level(string key = "level", bool required = true)
        {
            string value = Args.Str(key);
            if (value == null)
            {
                if (required) throw Error(ErrorCodes.InvalidArgs, Args.Field(key) + " is required (a level name or id).",
                    new Dictionary<string, object> { ["param"] = Args.Field(key), ["reason"] = "required" });
                return null;
            }
            return Resolve.Level(Doc, value, Args.Field(key), Ctx.UiDoc);
        }

        /// <summary>id | "Family: Type" | "Type" ; null value → the document's default type of that class when <paramref name="category"/> is given.</summary>
        public T Type<T>(string key, BuiltInCategory? category = null, bool required = true) where T : ElementType
        {
            string value = Args.Str(key);
            if (value == null)
            {
                T fallback = category.HasValue ? Resolve.DefaultType<T>(Doc, category.Value) : null;
                if (fallback != null || !required) return fallback;
                throw Error(ErrorCodes.InvalidArgs, Args.Field(key) + " is required (a type name \"Family: Type\" or id).",
                    new Dictionary<string, object> { ["param"] = Args.Field(key), ["reason"] = "required" });
            }
            return Resolve.Type<T>(Doc, value, Args.Field(key), category);
        }

        /// <summary>Element ids: numbers, UniqueIds, "selection" (last UI selection of the doc). Missing ids raise NOT_FOUND.</summary>
        public IList<ElementId> Ids(string key = "ids", bool required = true)
        {
            if (!Args.Has(key))
            {
                if (required) throw Error(ErrorCodes.InvalidArgs, Args.Field(key) + " is required (element ids).",
                    new Dictionary<string, object> { ["param"] = Args.Field(key), ["reason"] = "required" });
                return new List<ElementId>();
            }
            return Resolve.Elements(Doc, Args, key, Ctx).Select(element => element.Id).ToList();
        }

        /// <summary>The elements of <see cref="Ids"/> (existing elements only; gone ids give warn IDS_GONE).</summary>
        public IList<Element> Elements(string key = "ids", bool required = true)
        {
            if (!Args.Has(key))
            {
                if (required) throw Error(ErrorCodes.InvalidArgs, Args.Field(key) + " is required (element ids).",
                    new Dictionary<string, object> { ["param"] = Args.Field(key), ["reason"] = "required" });
                return new List<Element>();
            }
            return Resolve.Elements(Doc, Args, key, Ctx);
        }

        // Recorders ---------------------------------------------------------------------------------------------

        public void Created(Element element)
        {
            if (element != null) Created(element.Id);
        }

        public void Created(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            if (_createdSet.Add(id.Value)) _created.Add(id);
            _needsRegeneration = true;
        }

        public void Created(IEnumerable<ElementId> ids)
        {
            foreach (ElementId id in ids ?? Enumerable.Empty<ElementId>()) Created(id);
        }

        /// <summary>An existing element changed (counted by the bulk rule). <paramref name="what"/>/<paramref name="before"/> feed undo compensation.</summary>
        public void Modified(Element element, string what = null, object before = null)
        {
            if (element == null) return;
            Modified(element.Id);
            if (what != null) ModifiedDetails.Add(new ModifiedDetail { Id = element.Id.Value, What = what, Before = before });
        }

        public void Modified(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId || _createdSet.Contains(id.Value)) return;
            if (_modifiedSet.Add(id.Value)) _modified.Add(id);
            _needsRegeneration = true;
        }

        public void Modified(IEnumerable<ElementId> ids)
        {
            foreach (ElementId id in ids ?? Enumerable.Empty<ElementId>()) Modified(id);
        }

        /// <summary>Instances affected by a type edit without listing them (counted by the bulk rule).</summary>
        public void Affected(int count)
        {
            if (count > 0) AffectedCount += count;
        }

        /// <summary>Records ids the handler already deleted itself (categories unknown; prefer <see cref="Delete"/>).</summary>
        public void Deleted(ICollection<ElementId> ids)
        {
            foreach (ElementId id in ids ?? new List<ElementId>())
            {
                if (id == null || !_deletedSet.Add(id.Value)) continue;
                _deleted.Add(new DeletedRecord { Id = id.Value, Category = null, Counted = true });
            }
            _needsRegeneration = true;
        }

        /// <summary>
        /// Deletes elements and records the full cascade with English categories (a rolled-back SubTransaction probe
        /// reads the dependents first). Must run inside the op's transaction (tx "in"). Returns every deleted id.
        /// </summary>
        public ICollection<ElementId> Delete(ICollection<ElementId> ids)
        {
            if (ids == null || ids.Count == 0) return new List<ElementId>();
            var categories = new Dictionary<long, DeletedRecord>();
            var targets = new HashSet<long>(ids.Where(id => id != null).Select(id => id.Value));
            try
            {
                using (var probe = new SubTransaction(Doc))
                {
                    probe.Start();
                    ICollection<ElementId> cascade;
                    try
                    {
                        cascade = Doc.Delete(ids);
                    }
                    finally
                    {
                        probe.RollBack();
                    }
                    foreach (ElementId id in cascade)
                    {
                        Element element = Doc.GetElement(id);
                        categories[id.Value] = new DeletedRecord
                        {
                            Id = id.Value,
                            Category = Rows.CategoryLabel(element?.Category),
                            Counted = IsCountedElement(element),
                            Target = targets.Contains(id.Value)
                        };
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // The probe failed (e.g. the delete itself is refused); the real delete below reports the error.
                categories.Clear();
            }

            ICollection<ElementId> deleted = Doc.Delete(ids);
            foreach (ElementId id in deleted)
            {
                if (!_deletedSet.Add(id.Value)) continue;
                _deleted.Add(categories.TryGetValue(id.Value, out DeletedRecord record)
                    ? record
                    : new DeletedRecord { Id = id.Value, Category = null, Counted = true, Target = targets.Contains(id.Value) });
            }
            _needsRegeneration = true;
            return deleted;
        }

        /// <summary>Publishes a named output for $refs ($N.name) and the response (type, view, sheet, level, ...).</summary>
        public void Output(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Outputs[name] = value is ElementId id ? (object)id.Value : value is Element element ? element.Id.Value : value;
        }

        public void Warn(string code, string text, IEnumerable<ElementId> ids = null)
        {
            OpResult.AddWarning(Warnings, code, text, ids?.Where(id => id != null).Select(id => id.Value), 1);
        }

        public void Notice(string code, string text)
        {
            Ctx.Notice(code, text);
        }

        /// <summary>One-line summary of what the op did ("created 4 walls on Level 1"); the engine builds one otherwise.</summary>
        public void Summary(string text)
        {
            SummaryText = text;
        }

        /// <summary>Op-specific result data (merged into the response data of a single call).</summary>
        public void Data(object data)
        {
            DataValue = data;
        }

        /// <summary>
        /// For handler-evaluated rules (file_overwrite, always, ...): when the request is not a confirmed apply, the
        /// engine rolls back and returns NOT APPLIED with this rule and plan.
        /// </summary>
        public void RequireConfirm(string rule, object plan)
        {
            if (Ctx.Confirmed != null || string.IsNullOrWhiteSpace(rule)) return;
            PendingRule = rule;
            PendingPlan = plan;
        }

        /// <summary>Regenerates only when this op changed the document since the last regeneration (D3 §4 #12).</summary>
        public void EnsureRegenerated()
        {
            if (!_needsRegeneration) return;
            Doc.Regenerate();
            _needsRegeneration = false;
        }

        /// <summary>Marks the document as needing regeneration (for changes not recorded through the recorders).</summary>
        public void MarkDirty()
        {
            _needsRegeneration = true;
        }

        /// <summary>Use as <c>throw c.Error(...)</c>.</summary>
        public OpException Error(string code, string message, object details = null)
        {
            return new OpException(code, message, details);
        }

        public bool ShouldYield(int index) => Ctx.ShouldYield(index);

        // Recorded state (read by the engine) -------------------------------------------------------------------

        public IReadOnlyList<ElementId> CreatedIds => _created;
        public IReadOnlyList<ElementId> ModifiedIds => _modified;
        public IReadOnlyList<DeletedRecord> DeletedRecords => _deleted;
        public int AffectedCount { get; private set; }
        public List<ModifiedDetail> ModifiedDetails { get; } = new List<ModifiedDetail>();
        public Dictionary<string, object> Outputs { get; } = new Dictionary<string, object>(StringComparer.Ordinal);
        public List<RevitMcpNext.Contracts.BridgeWarning> Warnings { get; } = new List<RevitMcpNext.Contracts.BridgeWarning>();
        public string SummaryText { get; private set; }
        public object DataValue { get; private set; }
        internal string PendingRule { get; private set; }
        internal object PendingPlan { get; private set; }

        /// <summary>$N: the first created element, else the first modified one.</summary>
        public ElementId PrimaryId => _created.Count > 0 ? _created[0] : (_modified.Count > 0 ? _modified[0] : null);

        /// <summary>
        /// Whether a deleted element counts for the delete rule and by_category: everything except sketch helpers
        /// (Sketch, SketchPlane, sketch lines) and uncategorized non-type elements.
        /// </summary>
        internal static bool IsCountedElement(Element element)
        {
            if (element == null) return true;
            try
            {
                if (element is Sketch || element is SketchPlane) return false;
                Category category = element.Category;
                if (category == null) return element is ElementType;
                BuiltInCategory builtIn = Resolve.SafeBuiltIn(category);
                return builtIn != BuiltInCategory.OST_SketchLines && builtIn != BuiltInCategory.OST_IOSSketchGrid;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>A recorded parameter/property change (for undo compensation).</summary>
    internal sealed class ModifiedDetail
    {
        public long Id { get; set; }
        public string What { get; set; }
        public object Before { get; set; }
    }
}
