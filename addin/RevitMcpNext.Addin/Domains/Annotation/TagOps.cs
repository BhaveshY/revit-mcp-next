using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// annotate tag | tag_all (D3 §3.5). Element tags are IndependentTags of the given type or of the category's default
    /// tag; rooms, areas and spaces get their spatial tags. A missing tag family is NO_TAG_FAMILY (fix: load one).
    /// </summary>
    internal static class TagOps
    {
        [Op("annotate.tag")]
        public static void Tag(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = AnnotationSupport.AnnotationView(c, "tagging", allowSheet: false, allow3D: true);
            List<Element> elements = AnnotationSupport.Selected(c);
            FamilySymbol named = c.Has("type") ? AnnotationSupport.NamedTagType(doc, c.Str("type")) : null;
            string orientation = c.Args.Enum("orientation", null, "horizontal", "vertical", "model");
            bool leader = c.Bool("leader", false);
            Plane plane = AnnotationSupport.ViewPlane(view);
            XYZ at = c.Args.PointOpt("at");
            if (at != null && elements.Count > 1)
            {
                c.Warn(WarningCodes.ParamIgnored, "at: used only when tagging one element; " + elements.Count.ToString(CultureInfo.InvariantCulture) + " elements are tagged at their centres");
                at = null;
            }

            HashSet<long> visible = AnnotationSupport.VisibleIn(doc, view);
            var cache = new Dictionary<BuiltInCategory, FamilySymbol>();
            var table = new TableBuilder(new[] { "element", "tag", "type" });
            var notVisible = new List<ElementId>();
            var notTaggable = new List<ElementId>();
            for (int index = 0; index < elements.Count; index++)
            {
                Element element = elements[index];
                if (element is ElementType || element is View || element.Category == null || element.ViewSpecific && !(element is SpatialElement))
                {
                    notTaggable.Add(element.Id);
                    continue;
                }
                if (!visible.Contains(element.Id.Value))
                {
                    notVisible.Add(element.Id);
                    continue;
                }
                FamilySymbol type = AnnotationSupport.TagTypeFor(doc, element, named, cache);
                XYZ head = AnnotationSupport.OnPlane(plane, at ?? AnnotationSupport.CenterOf(element, view));
                Element tag = AnnotationSupport.CreateTag(doc, view, element, type, head, leader, orientation);
                c.Created(tag);
                table.Add(element.Id.Value, tag.Id.Value, Resolve.FullTypeName(doc.GetElement(tag.GetTypeId()) as ElementType));
            }
            if (notVisible.Count > 0)
            {
                c.Warn(WarningCodes.PartialResult, notVisible.Count.ToString(CultureInfo.InvariantCulture) + " element(s) are not visible in '" + view.Name + "' and were not tagged", notVisible);
            }
            if (notTaggable.Count > 0)
            {
                c.Warn(WarningCodes.PartialResult, notTaggable.Count.ToString(CultureInfo.InvariantCulture) + " element(s) cannot be tagged (types, views or annotation)", notTaggable);
            }
            if (table.Rows.Count == 0)
            {
                throw new OpException(ErrorCodes.InvalidArgs,
                    "None of the " + AnnotationSupport.Count(elements.Count, "element") + " can be tagged in '" + view.Name + "' (" +
                    (notVisible.Count > 0 ? notVisible.Count.ToString(CultureInfo.InvariantCulture) + " not visible" : notTaggable.Count.ToString(CultureInfo.InvariantCulture) + " not taggable") + ").",
                    new Dictionary<string, object> { ["param"] = "view", ["reason"] = "elements not visible or not taggable in this view" });
            }
            c.Output("view", view.Id);
            c.Data(new Dictionary<string, object> { ["view"] = view.Name, ["tags"] = table.ToWire() });
            c.Summary("tagged " + AnnotationSupport.Count(table.Rows.Count, "element") + " in '" + view.Name + "'");
        }

        [Op("annotate.tag_all")]
        public static void TagAll(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = AnnotationSupport.AnnotationView(c, "tagging", allowSheet: false, allow3D: true);
            List<string> categoryNames = c.Args.Strs("category");
            if (categoryNames.Count == 0) throw OpException.InvalidArgs("category", "is required (categories to tag, e.g. [\"Doors\",\"Windows\"])");
            List<BuiltInCategory> categories = Resolve.Categories(doc, categoryNames, c.Args.Field("category"));
            FamilySymbol named = c.Has("type") ? AnnotationSupport.NamedTagType(doc, c.Str("type")) : null;
            if (named != null && categories.Count > 1)
            {
                throw OpException.InvalidArgs("type", "one tag type cannot tag " + categories.Count.ToString(CultureInfo.InvariantCulture) + " categories; call tag_all once per category or omit type");
            }
            bool untaggedOnly = c.Bool("untagged_only", true);
            bool leader = c.Bool("leader", false);
            XYZ offset = c.Has("offset") ? c.Args.Vector("offset") : null;
            Plane plane = AnnotationSupport.ViewPlane(view);
            HashSet<long> tagged = untaggedOnly ? AnnotationSupport.TaggedIn(doc, view) : new HashSet<long>();
            var cache = new Dictionary<BuiltInCategory, FamilySymbol>();
            var perCategory = new TableBuilder(new[] { "category", "tagged", "skipped_tagged", "skipped_other" });

            foreach (BuiltInCategory category in categories)
            {
                string label = EnglishAliases.LabelOf(category) ?? category.ToString();
                List<Element> elements = new FilteredElementCollector(doc, view.Id).OfCategory(category).WhereElementIsNotElementType().ToElements()
                    .Where(e => e.Category != null).ToList();
                int done = 0, already = 0, other = 0;
                FamilySymbol type = null;
                foreach (Element element in elements)
                {
                    if (tagged.Contains(element.Id.Value))
                    {
                        already++;
                        continue;
                    }
                    if (element is SpatialElement spatial && (spatial.Location == null || spatial.Area <= 0))
                    {
                        other++;
                        continue;
                    }
                    if (type == null) type = AnnotationSupport.TagTypeFor(doc, element, named, cache);
                    XYZ center = AnnotationSupport.CenterOf(element, view);
                    if (center == null)
                    {
                        other++;
                        continue;
                    }
                    XYZ head = AnnotationSupport.OnPlane(plane, offset == null ? center : center + offset);
                    Element tag = AnnotationSupport.CreateTag(doc, view, element, type, head, leader, null);
                    c.Created(tag);
                    tagged.Add(element.Id.Value);
                    done++;
                }
                perCategory.Add(label, done, already, other);
            }

            int total = c.CreatedIds.Count;
            c.Output("view", view.Id);
            c.Data(new Dictionary<string, object> { ["view"] = view.Name, ["by_category"] = perCategory.ToWire() });
            int skipped = perCategory.Rows.Sum(r => Convert.ToInt32(r[2], CultureInfo.InvariantCulture));
            c.Summary("tagged " + AnnotationSupport.Count(total, "element") + " in '" + view.Name + "'" +
                      (skipped > 0 ? " (" + skipped.ToString(CultureInfo.InvariantCulture) + " already tagged)" : string.Empty));
        }
    }
}
