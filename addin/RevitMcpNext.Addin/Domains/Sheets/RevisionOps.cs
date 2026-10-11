using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// edit_sheets revision_create | revision_edit | revision_add | revision_remove (D3 §3.4). Revisions are project
    /// elements; sheets list them through their additional revision ids (clouds on a sheet's views add revisions too,
    /// and those cannot be removed from the sheet without deleting the clouds).
    /// </summary>
    internal static class RevisionOps
    {
        [Op("edit_sheets.revision_create")]
        public static void Create(ChangeContext c)
        {
            Document doc = c.Doc;
            string description = (c.Str("description") ?? string.Empty).Trim();
            if (description.Length == 0) throw OpException.InvalidArgs("description", "is required (the revision description)");

            Revision revision = Revision.Create(doc) ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not create the revision.");
            revision.Description = description;
            if (c.Has("date")) revision.RevisionDate = c.Str("date");
            if (c.Has("issued_by")) revision.IssuedBy = c.Str("issued_by");
            if (c.Has("issued_to")) revision.IssuedTo = c.Str("issued_to");
            c.Created(revision);
            c.Output("revision", revision.Id);

            int same = SheetSupport.AllRevisions(doc).Count(r => r.Id != revision.Id && Resolve.Eq(r.Description, description));
            var data = new Dictionary<string, object> { ["revision"] = SheetSupport.RevisionData(revision) };
            if (same > 0)
            {
                data["note"] = same.ToString(CultureInfo.InvariantCulture) + " other revision(s) have the same description; refer to this one by id or 'Seq. " +
                               revision.SequenceNumber.ToString(CultureInfo.InvariantCulture) + "'";
            }
            c.Data(data);
            c.Summary("created revision Seq. " + revision.SequenceNumber.ToString(CultureInfo.InvariantCulture) + " '" + description + "'");
        }

        [Op("edit_sheets.revision_edit")]
        public static void Edit(ChangeContext c)
        {
            Document doc = c.Doc;
            Revision revision = SheetSupport.FindRevision(doc, c.Str("revision"));
            bool? issued = c.Args.BoolOpt("issued");
            bool editsText = c.Has("description") || c.Has("date") || c.Has("issued_by") || c.Has("issued_to");
            if (!editsText && !issued.HasValue)
            {
                throw OpException.InvalidArgs("description", "give at least one of description, date, issued_by, issued_to, issued",
                    new Dictionary<string, object> { ["op"] = "revision_edit", ["revision"] = c.Str("revision"), ["issued"] = true });
            }
            if (editsText && revision.Issued && issued != false)
            {
                throw new OpException(ErrorCodes.InvalidArgs,
                    "Revision '" + SheetSupport.Describe(revision).Trim() + "' is issued, so its fields are locked; pass issued:false to unlock and edit it.",
                    new Dictionary<string, object>
                    {
                        ["param"] = "issued",
                        ["reason"] = "revision is issued",
                        ["example"] = new Dictionary<string, object> { ["op"] = "revision_edit", ["revision"] = c.Str("revision"), ["issued"] = false, ["description"] = c.Str("description") ?? revision.Description }
                    });
            }

            Dictionary<string, object> before = SheetSupport.RevisionData(revision);
            var changed = new List<string>();
            // Unlock first, lock last: Revit refuses edits of an issued revision.
            if (issued == false && revision.Issued)
            {
                revision.Issued = false;
                changed.Add("issued");
            }
            if (c.Has("description") && !string.Equals(revision.Description, c.Str("description"), StringComparison.Ordinal))
            {
                revision.Description = c.Str("description");
                changed.Add("description");
            }
            if (c.Has("date") && !string.Equals(revision.RevisionDate, c.Str("date"), StringComparison.Ordinal))
            {
                revision.RevisionDate = c.Str("date");
                changed.Add("date");
            }
            if (c.Has("issued_by") && !string.Equals(revision.IssuedBy, c.Str("issued_by"), StringComparison.Ordinal))
            {
                revision.IssuedBy = c.Str("issued_by");
                changed.Add("issued_by");
            }
            if (c.Has("issued_to") && !string.Equals(revision.IssuedTo, c.Str("issued_to"), StringComparison.Ordinal))
            {
                revision.IssuedTo = c.Str("issued_to");
                changed.Add("issued_to");
            }
            if (issued == true && !revision.Issued)
            {
                revision.Issued = true;
                changed.Add("issued");
            }
            if (changed.Count > 0) c.Modified(revision, "revision", before);
            c.Output("revision", revision.Id);
            c.Data(new Dictionary<string, object> { ["revision"] = SheetSupport.RevisionData(revision), ["changed"] = changed.Cast<object>().ToList() });
            c.Summary(changed.Count == 0
                ? "revision '" + revision.Description + "' already has these values"
                : "revision Seq. " + revision.SequenceNumber.ToString(CultureInfo.InvariantCulture) + " '" + revision.Description + "': " + string.Join(", ", changed) + " updated");
        }

        [Op("edit_sheets.revision_add")]
        public static void Add(ChangeContext c)
        {
            Document doc = c.Doc;
            Revision revision = SheetSupport.FindRevision(doc, c.Str("revision"));
            List<ViewSheet> sheets = SheetSupport.Sheets(c);
            var table = new TableBuilder(new[] { "sheet", "action" });
            foreach (ViewSheet sheet in sheets)
            {
                ICollection<ElementId> additional = sheet.GetAdditionalRevisionIds();
                if (additional.Contains(revision.Id))
                {
                    table.Add(sheet.SheetNumber, "already on sheet");
                    continue;
                }
                var updated = new List<ElementId>(additional) { revision.Id };
                sheet.SetAdditionalRevisionIds(updated);
                c.Modified(sheet, "revisions", additional.Select(id => (object)id.Value).ToList());
                table.Add(sheet.SheetNumber, "added");
            }
            c.Output("revision", revision.Id);
            c.Data(new Dictionary<string, object> { ["revision"] = SheetSupport.RevisionData(revision), ["sheets"] = table.ToWire() });
            c.Summary("revision '" + revision.Description + "' added to " + SheetOps.Count(c.ModifiedIds.Count, "sheet") +
                      (c.ModifiedIds.Count < sheets.Count ? " (" + (sheets.Count - c.ModifiedIds.Count).ToString(CultureInfo.InvariantCulture) + " already had it)" : string.Empty));
        }

        [Op("edit_sheets.revision_remove")]
        public static void Remove(ChangeContext c)
        {
            Document doc = c.Doc;
            Revision revision = SheetSupport.FindRevision(doc, c.Str("revision"));
            List<ViewSheet> sheets = SheetSupport.Sheets(c);
            var table = new TableBuilder(new[] { "sheet", "action" });
            var byClouds = new List<ElementId>();
            foreach (ViewSheet sheet in sheets)
            {
                ICollection<ElementId> additional = sheet.GetAdditionalRevisionIds();
                if (additional.Contains(revision.Id))
                {
                    sheet.SetAdditionalRevisionIds(additional.Where(id => id != revision.Id).ToList());
                    c.Modified(sheet, "revisions", additional.Select(id => (object)id.Value).ToList());
                }
                c.MarkDirty();
                c.EnsureRegenerated();
                bool stillShown = sheet.GetAllRevisionIds().Contains(revision.Id);
                if (stillShown)
                {
                    byClouds.Add(sheet.Id);
                    table.Add(sheet.SheetNumber, "still shown: revision clouds on this sheet or its views use it");
                }
                else
                {
                    table.Add(sheet.SheetNumber, additional.Contains(revision.Id) ? "removed" : "was not on sheet");
                }
            }
            if (byClouds.Count > 0)
            {
                c.Warn(WarningCodes.PartialResult,
                    "revision '" + revision.Description + "' stays on " + SheetOps.Count(byClouds.Count, "sheet") +
                    " because revision clouds use it; delete or reassign those clouds first", byClouds);
            }
            c.Output("revision", revision.Id);
            c.Data(new Dictionary<string, object> { ["revision"] = SheetSupport.RevisionData(revision), ["sheets"] = table.ToWire() });
            c.Summary("revision '" + revision.Description + "' removed from " + SheetOps.Count(c.ModifiedIds.Count, "sheet") +
                      (byClouds.Count > 0 ? "; still shown on " + byClouds.Count.ToString(CultureInfo.InvariantCulture) + " by clouds" : string.Empty));
        }
    }
}
