using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// edit_sheets create | duplicate | rename | set_titleblock (SPEC Appendix A/B, D1 §4B, D3 §3.4). All run in the
    /// ChangeEngine transaction (tx in); duplicate is strict (Revit warnings roll it back).
    /// </summary>
    internal static class SheetOps
    {
        [Op("edit_sheets.create")]
        public static void Create(ChangeContext c)
        {
            Document doc = c.Doc;
            var planned = new List<Planned>();
            if (c.Has("items"))
            {
                List<PayloadReader> items = c.Args.SubList("items");
                if (items.Count == 0) throw OpException.InvalidArgs("items", "needs at least one {number, name}");
                foreach (PayloadReader item in items)
                {
                    string number = (item.Str("number") ?? string.Empty).Trim();
                    if (number.Length == 0) throw OpException.InvalidArgs(item.Field("number"), "is required (the sheet number)");
                    planned.Add(new Planned { Number = number, Name = item.Str("name"), Field = item.Field("number"), TitleBlock = item.Str("titleblock") });
                }
            }
            if (c.Has("number"))
            {
                if (planned.Count > 0) throw OpException.InvalidArgs("number", "give number (one sheet) or items (several), not both");
                planned.Add(new Planned { Number = c.Str("number").Trim(), Name = c.Str("name"), Field = "number" });
            }
            if (planned.Count == 0) throw OpException.InvalidArgs("number", "number or items is required", new Dictionary<string, object> { ["op"] = "create", ["number"] = "A101", ["name"] = "Ground floor" });

            FamilySymbol defaultTitleBlock = SheetSupport.TitleBlock(doc, c.Str("titleblock"));
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Planned plan in planned)
            {
                if (SheetSupport.NumberTaken(doc, plan.Number) || used.Contains(plan.Number))
                {
                    if (!c.Has("items")) throw SheetSupport.NumberTakenError(doc, "number", plan.Number, used);
                    throw ItemsTaken(c, doc, planned, plan);
                }
                used.Add(plan.Number);
            }

            var table = new TableBuilder(new[] { "id", "number", "name", "titleblock" });
            foreach (Planned plan in planned)
            {
                FamilySymbol titleBlock = plan.TitleBlock == null ? defaultTitleBlock : SheetSupport.TitleBlock(doc, plan.TitleBlock, plan.Field.Replace(".number", ".titleblock"));
                ViewSheet sheet = ViewSheet.Create(doc, titleBlock?.Id ?? ElementId.InvalidElementId)
                    ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not create sheet " + plan.Number + ".");
                sheet.SheetNumber = plan.Number;
                if (!string.IsNullOrWhiteSpace(plan.Name)) sheet.Name = plan.Name.Trim();
                c.Created(sheet);
                table.Add(sheet.Id.Value, sheet.SheetNumber, sheet.Name, titleBlock == null ? null : Resolve.FullTypeName(titleBlock));
                if (table.Rows.Count == 1) c.Output("sheet", sheet.Id);
            }

            c.Data(new Dictionary<string, object> { ["sheets"] = table.ToWire() });
            string numbers = string.Join(", ", planned.Take(5).Select(p => p.Number)) + (planned.Count > 5 ? ", ..." : string.Empty);
            c.Summary("created " + Count(planned.Count, "sheet") + " " + numbers +
                      (defaultTitleBlock == null && planned.All(p => p.TitleBlock == null) ? " without a titleblock (none is loaded)" : string.Empty));
        }

        [Op("edit_sheets.duplicate")]
        public static void Duplicate(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSheet source = SheetSupport.Sheet(c);
            string number = (c.Str("number") ?? string.Empty).Trim();
            if (number.Length == 0) throw OpException.InvalidArgs("number", "is required (the new sheet number)");
            if (SheetSupport.NumberTaken(doc, number)) throw SheetSupport.NumberTakenError(doc, "number", number);
            if (source.IsPlaceholder) throw OpException.InvalidArgs("sheet", "'" + SheetSupport.Label(source) + "' is a placeholder sheet; placeholders cannot be duplicated");

            string mode = c.Args.Enum("mode", "with_views", "empty", "with_views", "with_detailing");
            SheetDuplicateOption option = mode == "empty" ? SheetDuplicateOption.DuplicateEmptySheet
                : mode == "with_detailing" ? SheetDuplicateOption.DuplicateSheetWithViewsAndDetailing
                : SheetDuplicateOption.DuplicateSheetWithViewsOnly;
            if (!source.CanBeDuplicated(option))
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Revit cannot duplicate sheet " + SheetSupport.Label(source) + " " + mode.Replace('_', ' ') + ".",
                    new Dictionary<string, object> { ["hint"] = "try mode empty, or duplicate the views with edit_views duplicate and place them" });
            }

            ElementId newId = source.Duplicate(option);
            ViewSheet sheet = doc.GetElement(newId) as ViewSheet ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not duplicate sheet " + SheetSupport.Label(source) + ".");
            sheet.SheetNumber = number;
            string name = c.Str("name");
            sheet.Name = string.IsNullOrWhiteSpace(name) ? source.Name : name.Trim();
            c.Created(sheet);
            c.Output("sheet", sheet.Id);
            List<string> skipped = CopySheetParameters(source, sheet);

            var views = new TableBuilder(new[] { "source_view", "view", "id" });
            if (option != SheetDuplicateOption.DuplicateEmptySheet)
            {
                c.MarkDirty();
                c.EnsureRegenerated();
                MatchViewports(c, source, sheet, c.Str("prefix"), views);
                MatchSchedules(doc, source, sheet);
            }

            var data = new Dictionary<string, object>
            {
                ["sheet"] = SheetSupport.SheetData(sheet),
                ["source"] = SheetSupport.SheetData(source),
                ["mode"] = mode
            };
            if (views.Rows.Count > 0) data["views"] = views.ToWire();
            if (skipped.Count > 0)
            {
                data["parameters_not_copied"] = skipped.Cast<object>().ToList();
                c.Warn(WarningCodes.PartialResult, skipped.Count.ToString(CultureInfo.InvariantCulture) + " sheet parameter(s) could not be copied: " + string.Join(", ", skipped.Take(5)), new[] { sheet.Id });
            }
            c.Data(data);
            c.Summary("duplicated sheet " + SheetSupport.Label(source) + " as " + SheetSupport.Label(sheet) + " (" + mode.Replace('_', ' ') +
                      (views.Rows.Count > 0 ? ", " + Count(views.Rows.Count, "view") + " copied" : string.Empty) + ")");
        }

        [Op("edit_sheets.rename")]
        public static void Rename(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSheet sheet = SheetSupport.Sheet(c);
            string number = c.Str("number");
            string name = c.Str("name");
            if (string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(name))
            {
                throw OpException.InvalidArgs("number", "give number and/or name", new Dictionary<string, object> { ["op"] = "rename", ["sheet"] = sheet.SheetNumber, ["name"] = sheet.Name });
            }
            string before = SheetSupport.Label(sheet);
            if (!string.IsNullOrWhiteSpace(number) && !string.Equals(number.Trim(), sheet.SheetNumber, StringComparison.Ordinal))
            {
                if (SheetSupport.NumberTaken(doc, number, sheet.Id)) throw SheetSupport.NumberTakenError(doc, "number", number.Trim());
                string old = sheet.SheetNumber;
                sheet.SheetNumber = number.Trim();
                c.Modified(sheet, "number", old);
            }
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), sheet.Name, StringComparison.Ordinal))
            {
                string old = sheet.Name;
                sheet.Name = name.Trim();
                c.Modified(sheet, "name", old);
            }
            c.Output("sheet", sheet.Id);
            c.Data(new Dictionary<string, object> { ["sheet"] = SheetSupport.SheetData(sheet), ["before"] = before });
            c.Summary(c.ModifiedIds.Count == 0 ? "sheet " + before + " already has that number and name" : "renamed sheet " + before + " to " + SheetSupport.Label(sheet));
        }

        [Op("edit_sheets.set_titleblock")]
        public static void SetTitleBlock(ChangeContext c)
        {
            Document doc = c.Doc;
            List<ViewSheet> sheets = SheetSupport.Sheets(c);
            string value = c.Str("titleblock");
            if (string.IsNullOrWhiteSpace(value)) throw OpException.InvalidArgs("titleblock", "is required (a titleblock type \"Family: Type\")");
            FamilySymbol symbol = SheetSupport.TitleBlock(doc, value);
            if (!symbol.IsActive) symbol.Activate();

            var table = new TableBuilder(new[] { "sheet", "titleblock", "action" });
            foreach (ViewSheet sheet in sheets)
            {
                List<FamilyInstance> existing = SheetSupport.TitleBlocksOn(doc, sheet);
                if (existing.Count == 0)
                {
                    FamilyInstance placed = doc.Create.NewFamilyInstance(XYZ.Zero, symbol, sheet)
                        ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not place the titleblock on sheet " + SheetSupport.Label(sheet) + ".");
                    c.Created(placed);
                    table.Add(sheet.SheetNumber, Resolve.FullTypeName(symbol), "placed");
                    continue;
                }
                foreach (FamilyInstance instance in existing)
                {
                    if (instance.Symbol != null && instance.Symbol.Id == symbol.Id)
                    {
                        table.Add(sheet.SheetNumber, Resolve.FullTypeName(symbol), "unchanged");
                        continue;
                    }
                    string before = instance.Symbol == null ? null : Resolve.FullTypeName(instance.Symbol);
                    instance.ChangeTypeId(symbol.Id);
                    c.Modified(instance, "type", before);
                    table.Add(sheet.SheetNumber, Resolve.FullTypeName(symbol), "changed from " + before);
                }
            }
            c.Data(new Dictionary<string, object> { ["sheets"] = table.ToWire() });
            c.Summary("titleblock " + Resolve.FullTypeName(symbol) + " on " + Count(sheets.Count, "sheet") +
                      " (" + c.ModifiedIds.Count.ToString(CultureInfo.InvariantCulture) + " changed, " + c.CreatedIds.Count.ToString(CultureInfo.InvariantCulture) + " placed)");
        }

        // ---------------------------------------------------------------------------------------------------------

        /// <summary>NAME_TAKEN for items: details.next is the whole items list with every used number replaced by a free one.</summary>
        private static OpException ItemsTaken(ChangeContext c, Document doc, List<Planned> planned, Planned first)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var corrected = new List<object>();
            foreach (Planned plan in planned)
            {
                string number = plan.Number;
                if (SheetSupport.NumberTaken(doc, number) || taken.Contains(number)) number = SheetSupport.NextFreeNumber(doc, number, taken);
                taken.Add(number);
                var item = new Dictionary<string, object> { ["number"] = number };
                if (!string.IsNullOrWhiteSpace(plan.Name)) item["name"] = plan.Name;
                if (!string.IsNullOrWhiteSpace(plan.TitleBlock)) item["titleblock"] = plan.TitleBlock;
                corrected.Add(item);
            }
            return new OpException(ErrorCodes.NameTaken,
                "Sheet number '" + first.Number + "' (" + first.Field + ") is already used in '" + doc.Title + "'; the fix suggests free numbers.",
                new Dictionary<string, object> { ["param"] = string.IsNullOrEmpty(c.Args.Path) ? "items" : c.Args.Field("items"), ["value"] = first.Number, ["next"] = corrected });
        }

        private sealed class Planned
        {
            public string Number;
            public string Name;
            public string Field;
            public string TitleBlock;
        }

        /// <summary>Copies writable sheet parameters that differ (number and name excepted); returns the names it could not copy.</summary>
        private static List<string> CopySheetParameters(ViewSheet source, ViewSheet target)
        {
            var skipped = new List<string>();
            var excluded = new HashSet<long>
            {
                new ElementId(BuiltInParameter.SHEET_NUMBER).Value,
                new ElementId(BuiltInParameter.SHEET_NAME).Value
            };
            foreach (Parameter from in source.GetOrderedParameters())
            {
                if (from == null || from.IsReadOnly || !from.HasValue || excluded.Contains(from.Id.Value)) continue;
                Parameter to;
                try { to = target.get_Parameter(from.Definition); } catch (Autodesk.Revit.Exceptions.ApplicationException) { to = null; }
                string label = from.Definition?.Name ?? from.Id.Value.ToString(CultureInfo.InvariantCulture);
                if (to == null || to.IsReadOnly || to.StorageType != from.StorageType)
                {
                    skipped.Add(label);
                    continue;
                }
                try
                {
                    bool ok = true;
                    switch (from.StorageType)
                    {
                        case StorageType.String:
                            if (to.AsString() != from.AsString()) ok = to.Set(from.AsString() ?? string.Empty);
                            break;
                        case StorageType.Double:
                            if (Math.Abs(to.AsDouble() - from.AsDouble()) > 1e-12) ok = to.Set(from.AsDouble());
                            break;
                        case StorageType.Integer:
                            if (to.AsInteger() != from.AsInteger()) ok = to.Set(from.AsInteger());
                            break;
                        case StorageType.ElementId:
                            if (to.AsElementId() != from.AsElementId()) ok = to.Set(from.AsElementId());
                            break;
                    }
                    if (!ok) skipped.Add(label);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException || ex is InvalidOperationException)
                {
                    skipped.Add(label);
                }
            }
            return skipped;
        }

        /// <summary>
        /// Pairs each source viewport with the duplicate at the same box centre (native duplication keeps them), then
        /// restores type, rotation, label and centre, and renames copied views with <paramref name="prefix"/>.
        /// </summary>
        private static void MatchViewports(ChangeContext c, ViewSheet source, ViewSheet sheet, string prefix, TableBuilder views)
        {
            Document doc = c.Doc;
            List<Viewport> remaining = SheetSupport.ViewportsOn(doc, sheet);
            foreach (Viewport original in SheetSupport.ViewportsOn(doc, source))
            {
                XYZ center = original.GetBoxCenter();
                Viewport copy = remaining.OrderBy(v => v.GetBoxCenter().DistanceTo(center)).FirstOrDefault();
                if (copy == null) continue;
                remaining.Remove(copy);
                View from = doc.GetElement(original.ViewId) as View;
                View to = doc.GetElement(copy.ViewId) as View;
                if (from == null || to == null) continue;
                if (to.Id != from.Id)
                {
                    c.Created(to.Id);
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        string wanted = prefix + from.Name;
                        to.Name = UniqueViewName(doc, wanted, to.Id);
                    }
                    views.Add(from.Name, to.Name, to.Id.Value);
                }
                if (copy.GetTypeId() != original.GetTypeId()) copy.ChangeTypeId(original.GetTypeId());
                if (copy.Rotation != original.Rotation) copy.Rotation = original.Rotation;
                try
                {
                    copy.LabelOffset = original.LabelOffset;
                    copy.LabelLineLength = original.LabelLineLength;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Some viewport types have no label.
                }
                if (copy.GetBoxCenter().DistanceTo(center) > 1e-6) copy.SetBoxCenter(center);
            }
        }

        /// <summary>Schedules duplicated with the sheet keep their insertion points.</summary>
        private static void MatchSchedules(Document doc, ViewSheet source, ViewSheet sheet)
        {
            List<ScheduleSheetInstance> copies = SheetSupport.SchedulesOn(doc, sheet).Where(s => !s.IsTitleblockRevisionSchedule).ToList();
            foreach (ScheduleSheetInstance original in SheetSupport.SchedulesOn(doc, source).Where(s => !s.IsTitleblockRevisionSchedule))
            {
                ScheduleSheetInstance copy = copies.Where(s => s.ScheduleId == original.ScheduleId).OrderBy(s => s.Point.DistanceTo(original.Point)).FirstOrDefault();
                if (copy == null) continue;
                copies.Remove(copy);
                if (copy.Point.DistanceTo(original.Point) > 1e-6) copy.Point = original.Point;
            }
        }

        internal static string UniqueViewName(Document doc, string wanted, ElementId self)
        {
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.Id != self).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(wanted)) return wanted;
            for (int n = 2; n < 1000; n++)
            {
                string candidate = wanted + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                if (!names.Contains(candidate)) return candidate;
            }
            return wanted + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        internal static string Count(int n, string noun)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "s");
        }
    }
}
