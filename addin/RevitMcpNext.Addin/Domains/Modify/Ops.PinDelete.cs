using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private static Dictionary<string, object> PreviewSetElementPinned(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "set_element_pinned requires elementId.");
            if (!operation.TryGetValue("pinned", out object pinnedValue) || pinnedValue == null)
            {
                return BlockedChange(operation, index, "set_element_pinned requires pinned.");
            }

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be pinned or unpinned.");

            bool desiredPinned = Convert.ToBoolean(pinnedValue, CultureInfo.InvariantCulture);
            bool? expectedPinned = GetNullableBool(operation, "expectedPinned");
            if (expectedPinned.HasValue && expectedPinned.Value != element.Pinned)
            {
                return BlockedChange(
                    operation,
                    index,
                    "Element " + elementId + " pinned state is " + element.Pinned.ToString(CultureInfo.InvariantCulture) +
                    " but expectedPinned was " + expectedPinned.Value.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: new Dictionary<string, object>
                {
                    ["pinned"] = element.Pinned
                },
                after: new Dictionary<string, object>
                {
                    ["pinned"] = desiredPinned
                });
        }

        private static Dictionary<string, object> ApplySetElementPinned(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewSetElementPinned(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "set_element_pinned preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            bool before = element.Pinned;
            bool desiredPinned = GetBool(operation, "pinned", false);
            element.Pinned = desiredPinned;

            return Change(operation, index, "applied", ElementTarget(element, null),
                before: new Dictionary<string, object>
                {
                    ["pinned"] = before
                },
                after: new Dictionary<string, object>
                {
                    ["pinned"] = element.Pinned
                });
        }

        private static Dictionary<string, object> PreviewDeleteElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "delete_element requires elementId.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be deleted by delete_element.");

            string expectedUniqueId = GetString(operation, "expectedUniqueId");
            if (!string.IsNullOrWhiteSpace(expectedUniqueId) && !string.Equals(element.UniqueId, expectedUniqueId, StringComparison.Ordinal))
            {
                return BlockedChange(operation, index, "Element " + elementId + " uniqueId did not match expectedUniqueId.");
            }

            bool? expectedPinned = GetNullableBool(operation, "expectedPinned");
            if (expectedPinned.HasValue && expectedPinned.Value != element.Pinned)
            {
                return BlockedChange(
                    operation,
                    index,
                    "Element " + elementId + " pinned state is " + element.Pinned.ToString(CultureInfo.InvariantCulture) +
                    " but expectedPinned was " + expectedPinned.Value.ToString(CultureInfo.InvariantCulture) + ".");
            }

            if (element.Pinned && !GetBool(operation, "allowPinned", false))
            {
                return BlockedChange(operation, index, "Element " + elementId + " is pinned. Pass allowPinned=true only after explicitly reviewing the target.");
            }

            ElementId targetId = element.Id;
            Dictionary<string, object> target = ElementTarget(element, null);
            Dictionary<string, object> before = DeleteSnapshot(document, element);
            DeleteProbeResult probe;
            try
            {
                probe = ProbeDeleteElement(document, element);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, "Revit could not preview delete_element for " + elementId + ": " + FormatPreviewProbeError("delete_element", ex));
            }

            Dictionary<string, object> after = DeleteAfterSnapshot(probe.DeletedIds, targetId);
            string guardFailure = ValidateDeleteProbeGuards(operation, probe);
            if (!string.IsNullOrWhiteSpace(guardFailure))
            {
                return Change(operation, index, "blocked", target, before, after, guardFailure);
            }

            return Change(operation, index, "ready", target,
                before: before,
                after: after);
        }

        private static Dictionary<string, object> ApplyDeleteElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewDeleteElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "delete_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            ElementId targetId = element.Id;
            Dictionary<string, object> target = ElementTarget(element, null);
            Dictionary<string, object> before = DeleteSnapshot(document, element);
            ICollection<ElementId> deletedIds = document.Delete(targetId);

            return Change(operation, index, "applied", target,
                before: before,
                after: DeleteAfterSnapshot(deletedIds ?? Array.Empty<ElementId>(), targetId));
        }

        private static string ValidateDeleteProbeGuards(Dictionary<string, object> operation, DeleteProbeResult probe)
        {
            int deletedCount = probe.DeletedIds.Count;
            int dependentCount = probe.DependentIds.Count;
            bool allowDependentDeletes = GetBool(operation, "allowDependentDeletes", false);
            IReadOnlyList<string> expectedDeletedElementIds = GetStringList(operation, "expectedDeletedElementIds");
            int? expectedDeletedCount = GetInt(operation, "expectedDeletedCount");
            int dependentDeleteLimit = Math.Min(
                MaxDeleteDependentLimit,
                Math.Max(1, GetInt(operation, "dependentDeleteLimit") ?? DefaultDeleteDependentLimit));

            if (expectedDeletedCount.HasValue && expectedDeletedCount.Value != deletedCount)
            {
                return "delete_element would delete " + deletedCount.ToString(CultureInfo.InvariantCulture) +
                    " element(s), but expectedDeletedCount was " + expectedDeletedCount.Value.ToString(CultureInfo.InvariantCulture) + ".";
            }

            if (expectedDeletedElementIds.Count > 0)
            {
                var expected = new HashSet<string>(expectedDeletedElementIds, StringComparer.OrdinalIgnoreCase);
                var actual = new HashSet<string>(probe.DeletedIds.Select(ToElementIdString), StringComparer.OrdinalIgnoreCase);
                if (expected.Count != actual.Count || !expected.SetEquals(actual))
                {
                    return "delete_element expectedDeletedElementIds did not match Revit's delete set. Actual deletedElementIds: " +
                        FormatElementIdList(actual) + ".";
                }
            }

            if (deletedCount > dependentDeleteLimit && !allowDependentDeletes && expectedDeletedElementIds.Count == 0)
            {
                return "delete_element would delete " + deletedCount.ToString(CultureInfo.InvariantCulture) +
                    " element(s), above dependentDeleteLimit " + dependentDeleteLimit.ToString(CultureInfo.InvariantCulture) +
                    ". Pass allowDependentDeletes=true or exact expectedDeletedElementIds after reviewing the preview.";
            }

            if (dependentCount > 0 && !allowDependentDeletes && expectedDeletedElementIds.Count == 0)
            {
                return "delete_element would also delete " + dependentCount.ToString(CultureInfo.InvariantCulture) +
                    " dependent element(s): " + FormatElementIdList(probe.DependentIds.Select(ToElementIdString)) +
                    ". Pass allowDependentDeletes=true or exact expectedDeletedElementIds after reviewing the preview.";
            }

            return null;
        }

        private static DeleteProbeResult ProbeDeleteElement(Document document, Element element)
        {
            ElementId targetId = element.Id;
            if (document.IsModifiable)
            {
                using (var subTransaction = new SubTransaction(document))
                {
                    bool started = false;
                    try
                    {
                        if (subTransaction.Start() != TransactionStatus.Started)
                        {
                            throw new InvalidOperationException("Could not start Revit subtransaction for delete preview.");
                        }
                        started = true;
                        ICollection<ElementId> deletedIds = document.Delete(targetId);
                        return new DeleteProbeResult(deletedIds ?? Array.Empty<ElementId>(), targetId);
                    }
                    finally
                    {
                        RollBackPreviewProbeSubTransaction(subTransaction, started);
                    }
                }
            }

            using (var transaction = new Transaction(document, "Revit MCP preview delete_element"))
            {
                TransactionStatus startStatus = transaction.Start();
                if (startStatus != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("Could not start Revit transaction for delete preview: " + startStatus);
                }

                ConfigurePreviewProbeTransaction(transaction);
                try
                {
                    ICollection<ElementId> deletedIds = document.Delete(targetId);
                    return new DeleteProbeResult(deletedIds ?? Array.Empty<ElementId>(), targetId);
                }
                finally
                {
                    RollBackPreviewProbeTransaction(transaction);
                }
            }
        }

        private static Dictionary<string, object> DeleteAfterSnapshot(IEnumerable<ElementId> deletedIds, ElementId targetId)
        {
            List<ElementId> ids = (deletedIds ?? Array.Empty<ElementId>()).ToList();
            string targetIdString = ToElementIdString(targetId);
            List<ElementId> dependentIds = ids
                .Where(id => !string.Equals(ToElementIdString(id), targetIdString, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var after = new Dictionary<string, object>
            {
                ["deleted"] = true,
                ["deletedCount"] = ids.Count,
                ["deletedElementIds"] = ids.Select(ToElementIdString).ToArray(),
                ["dependentDeletedCount"] = dependentIds.Count
            };

            if (dependentIds.Count > 0)
            {
                after["dependentDeletedElementIds"] = dependentIds.Select(ToElementIdString).ToArray();
            }

            return after;
        }

        private sealed class DeleteProbeResult
        {
            public DeleteProbeResult(IEnumerable<ElementId> deletedIds, ElementId targetId)
            {
                DeletedIds = (deletedIds ?? Array.Empty<ElementId>()).ToList();
                string targetIdString = ToElementIdString(targetId);
                DependentIds = DeletedIds
                    .Where(id => !string.Equals(ToElementIdString(id), targetIdString, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            public List<ElementId> DeletedIds { get; }
            public List<ElementId> DependentIds { get; }
        }
    }
}
