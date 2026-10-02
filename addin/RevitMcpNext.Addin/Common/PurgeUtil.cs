using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Purge candidates (SPEC §9.5): Document.GetUnusedElements filtered by DocumentValidation.CanDeleteElement.
    /// One pass needs no transaction; several passes delete in between, so the caller must hold an open transaction
    /// (a TempScope for a read-only report, the WriteScope for manage_document.purge).
    /// </summary>
    internal static class PurgeUtil
    {
        /// <summary>One pass: elements Revit reports as unused and that can be deleted.</summary>
        public static List<ElementId> Candidates(Document doc)
        {
            ICollection<ElementId> unused;
            try
            {
                unused = doc.GetUnusedElements(new HashSet<ElementId>());
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit could not list unused elements: " + ex.Message,
                    new Dictionary<string, object> { ["apiMessage"] = ex.Message });
            }
            var result = new List<ElementId>(unused.Count);
            foreach (ElementId id in unused)
            {
                bool deletable;
                try
                {
                    deletable = DocumentValidation.CanDeleteElement(doc, id);
                }
                catch
                {
                    deletable = false;
                }
                if (deletable) result.Add(id);
            }
            return result;
        }

        /// <summary>
        /// Up to <paramref name="passes"/> (1-3) passes. When <paramref name="delete"/> is given, each pass's candidates are
        /// deleted through it before the next pass (requires an open transaction); otherwise only the first pass runs.
        /// Returns the candidates per pass.
        /// </summary>
        public static List<List<ElementId>> Passes(Document doc, int passes, Func<ICollection<ElementId>, ICollection<ElementId>> delete = null)
        {
            passes = Math.Max(1, Math.Min(3, passes));
            var result = new List<List<ElementId>>();
            for (int pass = 0; pass < passes; pass++)
            {
                List<ElementId> candidates = Candidates(doc);
                if (candidates.Count == 0) break;
                result.Add(candidates);
                if (delete == null) break;
                delete(candidates);
            }
            return result;
        }

        /// <summary>Counts by kind: families, types, materials, views, patterns, styles, other.</summary>
        public static Dictionary<string, int> ByKind(Document doc, IEnumerable<ElementId> ids)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ElementId id in ids ?? Enumerable.Empty<ElementId>())
            {
                string kind = KindOf(Resolve.SafeGet(doc, id));
                counts.TryGetValue(kind, out int count);
                counts[kind] = count + 1;
            }
            return counts;
        }

        private static string KindOf(Element element)
        {
            switch (element)
            {
                case null: return "other";
                case Family _: return "families";
                case Material _: return "materials";
                case View _: return "views";
                case FillPatternElement _: return "fill_patterns";
                case LinePatternElement _: return "line_patterns";
                case GraphicsStyle _: return "styles";
                case ElementType _: return "types";
                default: return "other";
            }
        }
    }
}
