using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Workshared edit checks (SPEC §9.5, D3 §2.8): elements owned by another user → NOT_EDITABLE {owner}; elements
    /// updated or deleted in central → NOT_EDITABLE {reason: updated_in_central}. Previews use <see cref="Inspect"/> to
    /// skip elements that would be borrowed (static validation only, SPEC §6.2).
    /// </summary>
    internal static class WorksharingGuard
    {
        internal sealed class Report
        {
            /// <summary>Elements a dry run must not touch (not owned by anyone, or owned by another user).</summary>
            public List<long> NotProbed { get; } = new List<long>();
            public List<long> OwnedByOthers { get; } = new List<long>();
            public List<long> UpdatedInCentral { get; } = new List<long>();
            /// <summary>Distinct owner names of OwnedByOthers.</summary>
            public List<string> Owners { get; } = new List<string>();
        }

        public static Report Inspect(Document doc, IEnumerable<ElementId> ids)
        {
            var report = new Report();
            if (doc == null || !doc.IsWorkshared || ids == null) return report;
            foreach (ElementId id in ids.Where(id => id != null && id != ElementId.InvalidElementId).Distinct())
            {
                CheckoutStatus status;
                string owner = null;
                try
                {
                    status = WorksharingUtils.GetCheckoutStatus(doc, id, out owner);
                }
                catch
                {
                    continue;
                }
                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    report.OwnedByOthers.Add(id.Value);
                    report.NotProbed.Add(id.Value);
                    if (!string.IsNullOrWhiteSpace(owner) && !report.Owners.Contains(owner)) report.Owners.Add(owner);
                }
                else if (status == CheckoutStatus.NotOwned)
                {
                    report.NotProbed.Add(id.Value);
                }

                try
                {
                    ModelUpdatesStatus updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                    if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral) report.UpdatedInCentral.Add(id.Value);
                }
                catch
                {
                    // Not available (e.g. element not yet in central).
                }
            }
            return report;
        }

        /// <summary>Throws NOT_EDITABLE when an element is owned by another user or changed in central.</summary>
        public static void EnsureEditable(Document doc, IEnumerable<ElementId> ids)
        {
            if (doc == null || !doc.IsWorkshared || ids == null) return;
            Report report = Inspect(doc, ids);
            if (report.OwnedByOthers.Count > 0)
            {
                string owners = string.Join(", ", report.Owners);
                throw new OpException(ErrorCodes.NotEditable,
                    report.OwnedByOthers.Count.ToString(CultureInfo.InvariantCulture) + " element(s) are borrowed by " +
                    (owners.Length == 0 ? "another user" : owners) + " in '" + doc.Title + "'.",
                    new Dictionary<string, object>
                    {
                        ["owner"] = report.Owners.FirstOrDefault(),
                        ["owners"] = report.Owners.Cast<object>().ToList(),
                        ["reason"] = "owned_by_other",
                        ["ids"] = report.OwnedByOthers.Take(20).Cast<object>().ToList()
                    });
            }
            if (report.UpdatedInCentral.Count > 0)
            {
                throw new OpException(ErrorCodes.NotEditable,
                    report.UpdatedInCentral.Count.ToString(CultureInfo.InvariantCulture) + " element(s) were changed in the central model after '" +
                    doc.Title + "' was last reloaded.",
                    new Dictionary<string, object>
                    {
                        ["reason"] = "updated_in_central",
                        ["ids"] = report.UpdatedInCentral.Take(20).Cast<object>().ToList()
                    });
            }
        }

        public static void EnsureEditable(Document doc, Element element)
        {
            if (element != null) EnsureEditable(doc, new[] { element.Id });
        }
    }
}
