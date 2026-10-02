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
        private static Dictionary<string, object> PreviewMoveElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> translationValue = GetDictionary(operation, "translation");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "move_element requires elementId.");
            if (translationValue == null) return BlockedChange(operation, index, "move_element requires translation.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be moved.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot be moved.");

            XYZ translation;
            try
            {
                translation = ToInternalPoint(translationValue, "translation");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (VectorLength(translation) <= 0) return BlockedChange(operation, index, "move_element translation must be non-zero.");

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation)
                });
        }

        private static Dictionary<string, object> ApplyMoveElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewMoveElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "move_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = LocationSnapshot(element);
            XYZ translation = ToInternalPoint(GetDictionary(operation, "translation"), "translation");
            ElementTransformUtils.MoveElement(document, element.Id, translation);
            Element movedElement = document.GetElement(element.Id);

            return Change(operation, index, "applied", ElementTarget(movedElement, null),
                before: before,
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation),
                    ["location"] = LocationSnapshot(movedElement)
                });
        }

        private static Dictionary<string, object> PreviewRotateElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> axisStartValue = GetDictionary(operation, "axisStart");
            Dictionary<string, object> axisEndValue = GetDictionary(operation, "axisEnd");
            Dictionary<string, object> angleValue = GetDictionary(operation, "angle");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "rotate_element requires elementId.");
            if (axisStartValue == null) return BlockedChange(operation, index, "rotate_element requires axisStart.");
            if (axisEndValue == null) return BlockedChange(operation, index, "rotate_element requires axisEnd.");
            if (angleValue == null) return BlockedChange(operation, index, "rotate_element requires angle.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be rotated.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot be rotated.");

            XYZ axisStart;
            XYZ axisEnd;
            double angleRadians;
            try
            {
                axisStart = ToInternalPoint(axisStartValue, "axisStart");
                axisEnd = ToInternalPoint(axisEndValue, "axisEnd");
                angleRadians = ToInternalAngle(angleValue);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (axisStart.DistanceTo(axisEnd) <= Math.Max(document.Application.ShortCurveTolerance, 0.000001))
            {
                return BlockedChange(operation, index, "rotate_element axisStart and axisEnd must define a non-zero axis.");
            }
            if (Math.Abs(angleRadians) <= 0.000000001)
            {
                return BlockedChange(operation, index, "rotate_element angle must be non-zero.");
            }

            return Change(operation, index, "ready", ElementTarget(element, null),
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["axisStart"] = PointValue(axisStart),
                    ["axisEnd"] = PointValue(axisEnd),
                    ["angle"] = AngleValue(angleRadians)
                });
        }

        private static Dictionary<string, object> ApplyRotateElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewRotateElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "rotate_element preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = LocationSnapshot(element);
            XYZ axisStart = ToInternalPoint(GetDictionary(operation, "axisStart"), "axisStart");
            XYZ axisEnd = ToInternalPoint(GetDictionary(operation, "axisEnd"), "axisEnd");
            double angleRadians = ToInternalAngle(GetDictionary(operation, "angle"));
            ElementTransformUtils.RotateElement(document, element.Id, Line.CreateBound(axisStart, axisEnd), angleRadians);
            Element rotatedElement = document.GetElement(element.Id);

            return Change(operation, index, "applied", ElementTarget(rotatedElement, null),
                before: before,
                after: new Dictionary<string, object>
                {
                    ["axisStart"] = PointValue(axisStart),
                    ["axisEnd"] = PointValue(axisEnd),
                    ["angle"] = AngleValue(angleRadians),
                    ["location"] = LocationSnapshot(rotatedElement)
                });
        }

        private static Dictionary<string, object> PreviewCopyElement(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            Dictionary<string, object> translationValue = GetDictionary(operation, "translation");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "copy_element requires elementId.");
            if (translationValue == null) return BlockedChange(operation, index, "copy_element requires translation.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is an element type and cannot be copied.");
            if (element.ViewSpecific) return BlockedChange(operation, index, "Element " + elementId + " is view-specific and cannot be copied by copy_element.");

            XYZ translation;
            try
            {
                translation = ToInternalPoint(translationValue, "translation");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            if (VectorLength(translation) <= 0) return BlockedChange(operation, index, "copy_element translation must be non-zero.");

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["source"] = ElementSummary(document, element)
                },
                before: LocationSnapshot(element),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation)
                });
        }

        private static Dictionary<string, object> ApplyCopyElement(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCopyElement(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "copy_element preview failed.");
            }

            Element source = ResolveElement(document, GetString(operation, "elementId"));
            XYZ translation = ToInternalPoint(GetDictionary(operation, "translation"), "translation");
            ICollection<ElementId> copiedIds = ElementTransformUtils.CopyElement(document, source.Id, translation);
            if (copiedIds == null || copiedIds.Count == 0)
            {
                throw new InvalidOperationException("copy_element did not return any copied element ids.");
            }

            Element[] copiedElements = copiedIds
                .Select(id => document.GetElement(id))
                .Where(element => element != null)
                .ToArray();

            return Change(operation, index, "applied",
                target: new Dictionary<string, object>
                {
                    ["source"] = ElementSummary(document, source)
                },
                before: LocationSnapshot(source),
                after: new Dictionary<string, object>
                {
                    ["translation"] = PointValue(translation),
                    ["copiedElementIds"] = copiedIds.Select(ToElementIdString).ToArray(),
                    ["copiedElements"] = copiedElements.Select(element => ElementSummary(document, element)).ToArray()
                });
        }
    }
}
