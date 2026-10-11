using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Shared resolution and output helpers of the sheets lane (P-SHEETS-ANNO): sheets, titleblocks, placements on a
    /// sheet (viewports and schedule instances), revisions and free sheet numbers. Coordinates on sheets are internal
    /// feet in sheet space; callers convert mm with the ChangeContext readers.
    /// </summary>
    internal static class SheetSupport
    {
        private static readonly Regex TrailingNumber = new Regex(@"^(?<head>.*?)(?<digits>\d+)(?<tail>\D*)$", RegexOptions.CultureInvariant);

        // ---------------------------------------------------------------------------------------------------------
        // Sheets
        // ---------------------------------------------------------------------------------------------------------

        public static List<ViewSheet> AllSheets(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        }

        /// <summary>The sheet named by <paramref name="key"/> (number, name, "A101 - Name" or id).</summary>
        public static ViewSheet Sheet(ChangeContext c, string key = "sheet")
        {
            string value = c.Str(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw OpException.InvalidArgs(c.Args.Field(key), "is required (a sheet number, name or id)");
            }
            return Resolve.Sheet(c.Doc, value, c.Args.Field(key));
        }

        /// <summary>sheet= or sheets= (exactly one of them), in the given order, without duplicates.</summary>
        public static List<ViewSheet> Sheets(ChangeContext c)
        {
            var result = new List<ViewSheet>();
            var seen = new HashSet<long>();
            if (c.Has("sheet"))
            {
                ViewSheet single = Sheet(c, "sheet");
                if (seen.Add(single.Id.Value)) result.Add(single);
            }
            List<string> many = c.Args.Strs("sheets");
            for (int index = 0; index < many.Count; index++)
            {
                ViewSheet sheet = Resolve.Sheet(c.Doc, many[index], c.Args.Field("sheets") + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                if (seen.Add(sheet.Id.Value)) result.Add(sheet);
            }
            if (result.Count == 0)
            {
                throw OpException.InvalidArgs("sheet", "sheet or sheets is required (sheet numbers, names or ids)");
            }
            return result;
        }

        /// <summary>True when a sheet (or placeholder) already uses this number (case-insensitive).</summary>
        public static bool NumberTaken(Document doc, string number, ElementId except = null)
        {
            string wanted = (number ?? string.Empty).Trim();
            if (wanted.Length == 0) return false;
            return AllSheets(doc).Any(sheet => (except == null || sheet.Id != except) &&
                                               string.Equals((sheet.SheetNumber ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The next unused number after <paramref name="number"/> (A101 → A102, A101a → A102a, X → X-2).</summary>
        public static string NextFreeNumber(Document doc, string number, ICollection<string> alsoTaken = null)
        {
            var taken = new HashSet<string>(AllSheets(doc).Select(s => (s.SheetNumber ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
            if (alsoTaken != null) foreach (string extra in alsoTaken) taken.Add((extra ?? string.Empty).Trim());
            string text = (number ?? string.Empty).Trim();
            Match match = TrailingNumber.Match(text);
            for (int step = 1; step < 10000; step++)
            {
                string candidate;
                if (match.Success)
                {
                    string digits = match.Groups["digits"].Value;
                    long value = long.Parse(digits, CultureInfo.InvariantCulture) + step;
                    candidate = match.Groups["head"].Value + value.ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0') + match.Groups["tail"].Value;
                }
                else
                {
                    candidate = text + "-" + (step + 1).ToString(CultureInfo.InvariantCulture);
                }
                if (!taken.Contains(candidate)) return candidate;
            }
            return text + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>NAME_TAKEN for a used sheet number, with the next free number in details.next (the fix line).</summary>
        public static OpException NumberTakenError(Document doc, string param, string number, ICollection<string> alsoTaken = null)
        {
            string next = NextFreeNumber(doc, number, alsoTaken);
            return new OpException(ErrorCodes.NameTaken,
                "Sheet number '" + number + "' is already used in '" + doc.Title + "'; the next free number is " + next + ".",
                new Dictionary<string, object> { ["param"] = param, ["value"] = number, ["next"] = next });
        }

        // ---------------------------------------------------------------------------------------------------------
        // Titleblocks
        // ---------------------------------------------------------------------------------------------------------

        public static List<FamilySymbol> TitleBlockTypes(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>().ToList();
        }

        /// <summary>
        /// A titleblock type by id, "Family: Type" or type/family name; null text gives the document default (or the
        /// first loaded) titleblock, or null when none is loaded.
        /// </summary>
        public static FamilySymbol TitleBlock(Document doc, string value, string param = "titleblock")
        {
            List<FamilySymbol> types = TitleBlockTypes(doc);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (types.Count == 0) return null;
                try
                {
                    ElementId defaultId = doc.GetDefaultFamilyTypeId(new ElementId(BuiltInCategory.OST_TitleBlocks));
                    if (defaultId != null && defaultId != ElementId.InvalidElementId && doc.GetElement(defaultId) is FamilySymbol byDefault) return byDefault;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Fall back to the first loaded titleblock.
                }
                return types.OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).First();
            }

            string text = value.Trim();
            FamilySymbol byId = Resolve.ById<FamilySymbol>(doc, text);
            if (byId != null)
            {
                if (Resolve.SafeBuiltIn(byId.Category) != BuiltInCategory.OST_TitleBlocks)
                {
                    throw OpException.InvalidArgs(param, "element " + text + " is not a titleblock type (it is " + (Rows.CategoryLabel(byId) ?? "another category") + ")");
                }
                return byId;
            }
            if (types.Count == 0)
            {
                throw new OpException(ErrorCodes.NotFound, "No titleblock family is loaded in '" + doc.Title + "'.",
                    new Dictionary<string, object> { ["param"] = param, ["kind"] = "titleblock", ["value"] = value, ["candidates"] = new List<string>() });
            }
            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                string family = text.Substring(0, colon).Trim();
                string typeName = text.Substring(colon + 1).Trim();
                FamilySymbol exact = types.FirstOrDefault(t => Resolve.Eq(t.FamilyName, family) && Resolve.Eq(t.Name, typeName));
                if (exact != null) return exact;
            }
            List<FamilySymbol> byName = types.Where(t => Resolve.Eq(t.Name, text)).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1) throw OpException.Ambiguous(param, "titleblock", value, byName.Select(Resolve.FullTypeName));
            List<FamilySymbol> byFamily = types.Where(t => Resolve.Eq(t.FamilyName, text)).ToList();
            if (byFamily.Count == 1) return byFamily[0];
            if (byFamily.Count > 1) throw OpException.Ambiguous(param, "titleblock", value, byFamily.Select(Resolve.FullTypeName));
            throw OpException.NotFound(param, "titleblock", value, Resolve.Closest(text, types.Select(Resolve.FullTypeName)));
        }

        /// <summary>The titleblock instances placed on a sheet.</summary>
        public static List<FamilyInstance> TitleBlocksOn(Document doc, ViewSheet sheet)
        {
            return new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
        }

        /// <summary>
        /// The default placement point on a sheet: the centre of the (first) titleblock's box in the sheet, else the
        /// centre of the sheet outline, else the origin. Sheet space, feet.
        /// </summary>
        public static XYZ SheetCenter(Document doc, ViewSheet sheet)
        {
            foreach (FamilyInstance titleBlock in TitleBlocksOn(doc, sheet))
            {
                BoundingBoxXYZ box = null;
                try { box = titleBlock.get_BoundingBox(sheet); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                if (box != null && box.Max.X - box.Min.X > 1e-6) return new XYZ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, 0);
            }
            try
            {
                BoundingBoxUV outline = sheet.Outline;
                if (outline != null && outline.Max.U - outline.Min.U > 1e-6)
                {
                    return new XYZ((outline.Min.U + outline.Max.U) / 2, (outline.Min.V + outline.Max.V) / 2, 0);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // No outline (empty sheet).
            }
            return XYZ.Zero;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Placements (viewports, schedule instances)
        // ---------------------------------------------------------------------------------------------------------

        public static List<ScheduleSheetInstance> SchedulesOn(Document doc, ViewSheet sheet)
        {
            return new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .Where(s => s.OwnerViewId == sheet.Id).ToList();
        }

        public static List<Viewport> ViewportsOn(Document doc, ViewSheet sheet)
        {
            return sheet.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>().ToList();
        }

        /// <summary>
        /// The viewport or schedule instance on <paramref name="sheet"/> that shows the view named by
        /// <paramref name="key"/> (view/schedule/legend name or id; the viewport's own id works too).
        /// </summary>
        public static Element Placement(ChangeContext c, ViewSheet sheet, string key = "view")
        {
            Document doc = c.Doc;
            string value = c.Str(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw OpException.InvalidArgs(c.Args.Field(key), "is required (the view, schedule or legend placed on the sheet)");
            }
            Element direct = Resolve.ById<Element>(doc, value);
            if (direct is Viewport viewport && viewport.SheetId == sheet.Id) return viewport;
            if (direct is ScheduleSheetInstance instance && instance.OwnerViewId == sheet.Id) return instance;

            List<Viewport> viewports = ViewportsOn(doc, sheet);
            List<ScheduleSheetInstance> schedules = SchedulesOn(doc, sheet).Where(s => !s.IsTitleblockRevisionSchedule).ToList();
            View view = direct as View;
            if (view == null)
            {
                // Names: views on this sheet first (a name can repeat across view types).
                var onSheet = viewports.Select(v => doc.GetElement(v.ViewId) as View)
                    .Concat(schedules.Select(s => doc.GetElement(s.ScheduleId) as View))
                    .Where(v => v != null).ToList();
                List<View> named = onSheet.Where(v => Resolve.Eq(v.Name, value)).ToList();
                if (named.Count == 1) view = named[0];
                else if (named.Count > 1) throw OpException.Ambiguous(c.Args.Field(key), "view", value, named.Select(v => v.Name + " (" + v.ViewType + ", id " + v.Id.Value.ToString(CultureInfo.InvariantCulture) + ")"));
                else
                {
                    throw OpException.NotFound(c.Args.Field(key), "view on sheet " + sheet.SheetNumber, value,
                        Resolve.Closest(value, onSheet.Select(v => v.Name)));
                }
            }
            Viewport match = viewports.FirstOrDefault(v => v.ViewId == view.Id);
            if (match != null) return match;
            ScheduleSheetInstance scheduleMatch = schedules.FirstOrDefault(s => s.ScheduleId == view.Id);
            if (scheduleMatch != null) return scheduleMatch;
            throw new OpException(ErrorCodes.NotFound,
                "'" + view.Name + "' is not placed on sheet " + sheet.SheetNumber + " - " + sheet.Name + ".",
                new Dictionary<string, object>
                {
                    ["param"] = c.Args.Field(key),
                    ["kind"] = "view on sheet " + sheet.SheetNumber,
                    ["value"] = value,
                    ["candidates"] = viewports.Select(v => doc.GetElement(v.ViewId)?.Name).Concat(schedules.Select(s => doc.GetElement(s.ScheduleId)?.Name))
                        .Where(n => !string.IsNullOrWhiteSpace(n)).Cast<object>().ToList()
                });
        }

        /// <summary>The sheet a view is placed on (viewports only), or null.</summary>
        public static ViewSheet SheetShowing(Document doc, View view)
        {
            Viewport viewport = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(v => v.ViewId == view.Id);
            return viewport == null ? null : doc.GetElement(viewport.SheetId) as ViewSheet;
        }

        /// <summary>A viewport type valid for this viewport by name or id.</summary>
        public static ElementType ViewportType(Document doc, Viewport viewport, string value, string param = "type")
        {
            List<ElementType> valid = viewport.GetValidTypes().Select(id => doc.GetElement(id)).OfType<ElementType>().ToList();
            ElementType byId = Resolve.ById<ElementType>(doc, value);
            if (byId != null)
            {
                if (valid.Any(t => t.Id == byId.Id)) return byId;
                throw OpException.InvalidArgs(param, "element " + value + " is not a viewport type");
            }
            return Resolve.Single(valid, t => t.Name, value, param, "viewport type", Resolve.FullTypeName);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Revisions
        // ---------------------------------------------------------------------------------------------------------

        public static List<Revision> AllRevisions(Document doc)
        {
            return Revision.GetAllRevisionIds(doc).Select(id => doc.GetElement(id)).OfType<Revision>().ToList();
        }

        /// <summary>A revision by id, exact description, revision number ("2", "B") or sequence ("Seq. 3", "#3").</summary>
        public static Revision FindRevision(Document doc, string value, string param = "revision")
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) throw OpException.InvalidArgs(param, "is required (a revision description, number or id)");
            List<Revision> revisions = AllRevisions(doc);
            Revision byId = Resolve.ById<Revision>(doc, text);
            if (byId != null) return byId;

            List<Revision> byDescription = revisions.Where(r => Resolve.Eq(r.Description, text)).ToList();
            if (byDescription.Count == 1) return byDescription[0];
            List<Revision> byNumber = revisions.Where(r => Resolve.Eq(SafeNumber(r), text)).ToList();
            if (byDescription.Count == 0 && byNumber.Count == 1) return byNumber[0];

            Match sequence = Regex.Match(text, @"^(?:seq\.?|sequence|#)\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (sequence.Success)
            {
                int wanted = int.Parse(sequence.Groups[1].Value, CultureInfo.InvariantCulture);
                Revision bySequence = revisions.FirstOrDefault(r => r.SequenceNumber == wanted);
                if (bySequence != null) return bySequence;
            }

            List<Revision> ambiguous = byDescription.Count > 1 ? byDescription : byNumber;
            if (ambiguous.Count > 1) throw OpException.Ambiguous(param, "revision", value, ambiguous.Select(Describe));
            throw OpException.NotFound(param, "revision", value, Resolve.Closest(text, revisions.Select(r => r.Description).Concat(revisions.Select(SafeNumber))));
        }

        public static string SafeNumber(Revision revision)
        {
            try { return revision.RevisionNumber; } catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        public static string Describe(Revision revision)
        {
            return "Seq. " + revision.SequenceNumber.ToString(CultureInfo.InvariantCulture) + " " + (SafeNumber(revision) ?? string.Empty) + " " + (revision.Description ?? string.Empty);
        }

        public static Dictionary<string, object> RevisionData(Revision revision)
        {
            return new Dictionary<string, object>
            {
                ["id"] = revision.Id.Value,
                ["sequence"] = revision.SequenceNumber,
                ["number"] = SafeNumber(revision),
                ["description"] = revision.Description,
                ["date"] = revision.RevisionDate,
                ["issued_by"] = revision.IssuedBy,
                ["issued_to"] = revision.IssuedTo,
                ["issued"] = revision.Issued
            };
        }

        // ---------------------------------------------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------------------------------------------

        public static Dictionary<string, object> SheetData(ViewSheet sheet)
        {
            return new Dictionary<string, object>
            {
                ["id"] = sheet.Id.Value,
                ["number"] = sheet.SheetNumber,
                ["name"] = sheet.Name
            };
        }

        public static string Label(ViewSheet sheet)
        {
            return sheet.SheetNumber + " - " + sheet.Name;
        }

        /// <summary>A sheet-space point [x,y] in mm for output.</summary>
        public static List<object> Point2(XYZ point)
        {
            return point == null ? null : new List<object> { Units.Mm(point.X), Units.Mm(point.Y) };
        }
    }
}
