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
        private static Dictionary<string, object> PreviewChangeElementType(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            string typeId = GetString(operation, "typeId");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "change_element_type requires elementId.");
            if (string.IsNullOrWhiteSpace(typeId)) return BlockedChange(operation, index, "change_element_type requires typeId.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);
            if (element is ElementType) return BlockedChange(operation, index, "Element " + elementId + " is already an element type and cannot change type.");
            if (element.Pinned) return BlockedChange(operation, index, "Element " + elementId + " is pinned and cannot change type.");

            Dictionary<string, object> target = ElementTarget(element, null);
            string editabilityError = OwnedByOtherUserError(target, "Element " + elementId);
            if (!string.IsNullOrWhiteSpace(editabilityError))
            {
                return Change(operation, index, "blocked", target, TypeSnapshot(document, element), null, editabilityError);
            }

            ElementType targetType = ResolveElement(document, typeId) as ElementType;
            if (targetType == null) return BlockedChange(operation, index, "Type " + typeId + " was not found.");

            ElementId currentTypeId = element.GetTypeId();
            if (!IsValidElementId(currentTypeId)) return BlockedChange(operation, index, "Element " + elementId + " does not expose a valid type id.");
            if (string.Equals(ToElementIdString(currentTypeId), ToElementIdString(targetType.Id), StringComparison.Ordinal))
            {
                return BlockedChange(operation, index, "Element " + elementId + " already has type " + typeId + ".");
            }
            if (!IsValidTypeForElement(element, targetType.Id))
            {
                return BlockedChange(operation, index, "Type " + typeId + " is not valid for element " + elementId + ".");
            }

            return Change(operation, index, "ready", target,
                before: TypeSnapshot(document, element),
                after: TypeSnapshot(document, targetType));
        }

        private static Dictionary<string, object> ApplyChangeElementType(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewChangeElementType(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "change_element_type preview failed.");
            }

            Element element = ResolveElement(document, GetString(operation, "elementId"));
            Dictionary<string, object> before = TypeSnapshot(document, element);
            ElementType targetType = ResolveElement(document, GetString(operation, "typeId")) as ElementType;
            ElementId changedElementId = element.ChangeTypeId(targetType.Id);
            Element changedElement = IsValidElementId(changedElementId) ? document.GetElement(changedElementId) : null;
            if (changedElement == null) changedElement = document.GetElement(element.Id) ?? element;

            return Change(operation, index, "applied", ElementTarget(changedElement, null),
                before: before,
                after: TypeSnapshot(document, changedElement));
        }

        private static Dictionary<string, object> PreviewRenameElementType(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string elementTypeId = GetString(operation, "elementTypeId");
            string newName = NormalizeOptionalText(GetString(operation, "newName"));
            if (string.IsNullOrWhiteSpace(elementTypeId))
            {
                return BlockedChange(operation, index, "rename_element_type requires elementTypeId.");
            }
            if (string.IsNullOrWhiteSpace(newName))
            {
                return BlockedChange(operation, index, "rename_element_type requires a non-empty newName.");
            }

            ElementType elementType = ResolveElement(document, elementTypeId) as ElementType;
            if (elementType == null)
            {
                return BlockedChange(operation, index, "Element " + elementTypeId + " is not an ElementType.");
            }

            string uniqueIdError = ValidateExpectedUniqueId(
                operation,
                elementType,
                elementTypeId,
                "expectedUniqueId",
                "ElementType");
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);

            Dictionary<string, object> target = ElementTypeTarget(elementType);
            string editabilityError = OwnedByOtherUserError(target, "ElementType " + elementTypeId);
            if (!string.IsNullOrWhiteSpace(editabilityError))
            {
                return Change(operation, index, "blocked", target, ElementTypeSnapshot(elementType), null, editabilityError);
            }
            if (!elementType.CanBeRenamed)
            {
                return BlockedChange(operation, index, "ElementType " + elementTypeId + " cannot be renamed.");
            }
            if (!NamingUtils.IsValidName(newName))
            {
                return BlockedChange(operation, index, "newName contains characters that Revit does not allow in an ElementType name.");
            }

            string currentName = SafeElementName(elementType);
            if (ElementTypeNamesEqual(currentName, newName))
            {
                return BlockedChange(operation, index, "ElementType " + elementTypeId + " is already named '" + newName + "'.");
            }

            ElementType conflict = FindElementTypeNameConflict(document, elementType, newName, elementType.Id);
            if (conflict != null)
            {
                return BlockedChange(operation, index, ElementTypeNameConflictMessage(newName, conflict));
            }
            if (validationContext != null && !validationContext.TryAddElementTypeName(elementType, newName))
            {
                return BlockedChange(
                    operation,
                    index,
                    "The change set requests duplicate ElementType name '" + newName + "' in family '" + GetFamilyName(elementType) + "'.");
            }

            return Change(
                operation,
                index,
                "ready",
                target,
                before: ElementTypeSnapshot(elementType),
                after: ElementTypeSnapshot(elementType, newName, includeIdentity: true));
        }

        private static Dictionary<string, object> ApplyRenameElementType(
            Document document,
            Dictionary<string, object> operation,
            int index)
        {
            Dictionary<string, object> preview = PreviewRenameElementType(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "rename_element_type preview failed.");
            }

            ElementType elementType = ResolveElement(document, GetString(operation, "elementTypeId")) as ElementType;
            Dictionary<string, object> before = ElementTypeSnapshot(elementType);
            elementType.Name = NormalizeOptionalText(GetString(operation, "newName"));

            return Change(
                operation,
                index,
                "applied",
                ElementTypeTarget(elementType),
                before: before,
                after: ElementTypeSnapshot(elementType));
        }

        private static Dictionary<string, object> PreviewDuplicateElementType(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string sourceTypeId = GetString(operation, "sourceTypeId");
            string newName = NormalizeOptionalText(GetString(operation, "newName"));
            if (string.IsNullOrWhiteSpace(sourceTypeId))
            {
                return BlockedChange(operation, index, "duplicate_element_type requires sourceTypeId.");
            }
            if (string.IsNullOrWhiteSpace(newName))
            {
                return BlockedChange(operation, index, "duplicate_element_type requires a non-empty newName.");
            }

            ElementType sourceType = ResolveElement(document, sourceTypeId) as ElementType;
            if (sourceType == null)
            {
                return BlockedChange(operation, index, "Element " + sourceTypeId + " is not an ElementType.");
            }

            string uniqueIdError = ValidateExpectedUniqueId(
                operation,
                sourceType,
                sourceTypeId,
                "expectedUniqueId",
                "Source ElementType");
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);

            Dictionary<string, object> target = ElementTypeTarget(sourceType);
            string editabilityError = OwnedByOtherUserError(target, "Source ElementType " + sourceTypeId);
            if (!string.IsNullOrWhiteSpace(editabilityError))
            {
                return Change(operation, index, "blocked", target, ElementTypeSnapshot(sourceType), null, editabilityError);
            }
            if (!sourceType.CanBeCopied)
            {
                return BlockedChange(operation, index, "ElementType " + sourceTypeId + " cannot be duplicated.");
            }
            if (!NamingUtils.IsValidName(newName))
            {
                return BlockedChange(operation, index, "newName contains characters that Revit does not allow in an ElementType name.");
            }

            ElementType conflict = FindElementTypeNameConflict(document, sourceType, newName, excludedElementTypeId: null);
            if (conflict != null)
            {
                return BlockedChange(operation, index, ElementTypeNameConflictMessage(newName, conflict));
            }
            if (validationContext != null && !validationContext.TryAddElementTypeName(sourceType, newName))
            {
                return BlockedChange(
                    operation,
                    index,
                    "The change set requests duplicate ElementType name '" + newName + "' in family '" + GetFamilyName(sourceType) + "'.");
            }

            return Change(
                operation,
                index,
                "ready",
                target,
                before: ElementTypeSnapshot(sourceType),
                after: ElementTypeSnapshot(sourceType, newName, includeIdentity: false));
        }

        private static Dictionary<string, object> ApplyDuplicateElementType(
            Document document,
            Dictionary<string, object> operation,
            int index)
        {
            Dictionary<string, object> preview = PreviewDuplicateElementType(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "duplicate_element_type preview failed.");
            }

            ElementType sourceType = ResolveElement(document, GetString(operation, "sourceTypeId")) as ElementType;
            Dictionary<string, object> before = ElementTypeSnapshot(sourceType);
            ElementType newType = sourceType.Duplicate(NormalizeOptionalText(GetString(operation, "newName")));
            if (newType == null)
            {
                throw new InvalidOperationException("Revit did not return the duplicated ElementType.");
            }

            return Change(
                operation,
                index,
                "applied",
                ElementTypeTarget(newType),
                before: before,
                after: ElementTypeSnapshot(newType));
        }

        private static ElementType FindElementTypeNameConflict(
            Document document,
            ElementType sourceType,
            string requestedName,
            ElementId excludedElementTypeId)
        {
            string scopeKey = GetElementTypeNameScopeKey(sourceType);
            string excludedId = IsValidElementId(excludedElementTypeId)
                ? ToElementIdString(excludedElementTypeId)
                : null;

            return new FilteredElementCollector(document)
                .WhereElementIsElementType()
                .OfType<ElementType>()
                .FirstOrDefault(candidate =>
                    (string.IsNullOrWhiteSpace(excludedId) ||
                     !string.Equals(ToElementIdString(candidate.Id), excludedId, StringComparison.Ordinal)) &&
                    string.Equals(GetElementTypeNameScopeKey(candidate), scopeKey, StringComparison.OrdinalIgnoreCase) &&
                    ElementTypeNamesEqual(SafeElementName(candidate), requestedName));
        }

        private static string GetElementTypeNameScopeKey(ElementType elementType)
        {
            if (elementType is FamilySymbol symbol && symbol.Family != null && IsValidElementId(symbol.Family.Id))
            {
                return "FamilySymbol|familyId:" + ToElementIdString(symbol.Family.Id);
            }

            return (elementType?.GetType().FullName ?? typeof(ElementType).FullName) +
                   "|familyName:" + (GetFamilyName(elementType) ?? string.Empty);
        }

        private static bool ElementTypeNamesEqual(string left, string right)
        {
            try
            {
                return NamingUtils.CompareNames(left ?? string.Empty, right ?? string.Empty) == 0;
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string ElementTypeNameConflictMessage(string requestedName, ElementType conflict)
        {
            string familyName = GetFamilyName(conflict);
            string familyDescription = string.IsNullOrWhiteSpace(familyName)
                ? "the same ElementType family scope"
                : "family '" + familyName + "'";
            return "An ElementType named '" + requestedName + "' already exists in " + familyDescription +
                   " (elementTypeId " + ToElementIdString(conflict.Id) + ").";
        }
    }
}
