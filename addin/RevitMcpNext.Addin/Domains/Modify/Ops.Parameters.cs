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
        private static Dictionary<string, object> PreviewSetParameter(Document document, Dictionary<string, object> operation, int index)
        {
            string elementId = GetString(operation, "elementId");
            string parameterName = GetString(operation, "parameterName");
            bool hasParameterRef = operation != null && operation.ContainsKey("parameterRef");
            Dictionary<string, object> parameterRef = GetDictionary(operation, "parameterRef");
            if (string.IsNullOrWhiteSpace(elementId)) return BlockedChange(operation, index, "set_parameter requires elementId.");
            if (hasParameterRef && parameterRef == null)
            {
                return BlockedChange(operation, index, "set_parameter parameterRef must be an object. Legacy parameterName is not used when parameterRef is present.");
            }
            if (!hasParameterRef && string.IsNullOrWhiteSpace(parameterName))
            {
                return BlockedChange(operation, index, "set_parameter requires parameterRef or legacy parameterName.");
            }
            if (!operation.TryGetValue("value", out object value)) return BlockedChange(operation, index, "set_parameter requires value.");

            Element element = ResolveElement(document, elementId);
            if (element == null) return BlockedChange(operation, index, "Element " + elementId + " was not found.");
            string uniqueIdError = ValidateExpectedUniqueId(operation, element, elementId);
            if (!string.IsNullOrWhiteSpace(uniqueIdError)) return BlockedChange(operation, index, uniqueIdError);

            ParameterResolution resolution = ResolveParameter(element, parameterRef, parameterName);
            if (!resolution.Ok) return BlockedChange(operation, index, resolution.Error);

            Dictionary<string, object> target = ParameterTarget(element, resolution);
            Parameter parameter = resolution.Parameter;
            if (parameter.IsReadOnly)
            {
                return Change(operation, index, "blocked", target, ParameterSnapshot(parameter), null,
                    "Parameter '" + resolution.ResolvedName + "' on element " + elementId + " is read-only.");
            }

            string editabilityError = OwnedByOtherUserError(target, "Element " + elementId);
            if (!string.IsNullOrWhiteSpace(editabilityError))
            {
                return Change(operation, index, "blocked", target, ParameterSnapshot(parameter), null, editabilityError);
            }

            PreparedParameterValue prepared;
            try
            {
                prepared = PrepareParameterValue(document, parameter, value, resolution.IsStable);
            }
            catch (Exception ex)
            {
                return Change(operation, index, "blocked", target, ParameterSnapshot(parameter), null,
                    "Parameter '" + resolution.ResolvedName + "' on element " + elementId + " rejected the requested value: " + ex.Message);
            }

            return Change(operation, index, "ready", target,
                before: ParameterSnapshot(parameter),
                after: prepared.PreviewSnapshot(parameter));
        }

        private static Dictionary<string, object> ApplySetParameter(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewSetParameter(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "set_parameter preview failed.");
            }

            string elementId = GetString(operation, "elementId");
            string parameterName = GetString(operation, "parameterName");
            Dictionary<string, object> parameterRef = GetDictionary(operation, "parameterRef");
            Element element = ResolveElement(document, elementId);
            ParameterResolution resolution = ResolveParameter(element, parameterRef, parameterName);
            if (!resolution.Ok) throw new InvalidOperationException(resolution.Error);

            Parameter parameter = resolution.Parameter;
            PreparedParameterValue prepared = PrepareParameterValue(document, parameter, operation["value"], resolution.IsStable);
            Dictionary<string, object> before = ParameterSnapshot(parameter);
            SetPreparedParameterValue(parameter, prepared);
            Dictionary<string, object> after = ParameterSnapshot(parameter);
            if (prepared.IsUnitValue)
            {
                after["inputValue"] = prepared.InputValue;
                after["internalValue"] = after["value"];
                AddIfNotBlank(after, "requestedUnitTypeId", prepared.RequestedUnitTypeId);
            }

            return Change(operation, index, "applied", ParameterTarget(element, resolution), before, after);
        }

        private sealed class ParameterResolution
        {
            private ParameterResolution(
                Parameter parameter,
                bool isStable,
                string requestedKind,
                string requestedValue,
                Dictionary<string, object> requestedReference,
                string error)
            {
                Parameter = parameter;
                IsStable = isStable;
                RequestedKind = requestedKind;
                RequestedValue = requestedValue;
                RequestedReference = requestedReference;
                Error = error;
            }

            public Parameter Parameter { get; }
            public bool IsStable { get; }
            public string RequestedKind { get; }
            public string RequestedValue { get; }
            public Dictionary<string, object> RequestedReference { get; }
            public string Error { get; }
            public bool Ok => Parameter != null && string.IsNullOrWhiteSpace(Error);
            public string ResolvedName => Parameter?.Definition?.Name ?? RequestedValue ?? "(unnamed)";

            public static ParameterResolution Success(
                Parameter parameter,
                bool isStable,
                string requestedKind,
                string requestedValue,
                Dictionary<string, object> requestedReference)
            {
                return new ParameterResolution(parameter, isStable, requestedKind, requestedValue, requestedReference, null);
            }

            public static ParameterResolution Failure(
                bool isStable,
                string requestedKind,
                string requestedValue,
                Dictionary<string, object> requestedReference,
                string error)
            {
                return new ParameterResolution(null, isStable, requestedKind, requestedValue, requestedReference, error);
            }
        }

        private sealed class PreparedParameterValue
        {
            public object InputValue { get; set; }
            public object StorageValue { get; set; }
            public bool IsUnitValue { get; set; }
            public string SpecTypeId { get; set; }
            public string RequestedUnitTypeId { get; set; }
            public string ParameterUnitTypeId { get; set; }
            public Element ReferencedElement { get; set; }

            public Dictionary<string, object> PreviewSnapshot(Parameter parameter)
            {
                var snapshot = new Dictionary<string, object>
                {
                    ["value"] = SerializeStorageValue(StorageValue),
                    ["storageType"] = parameter.StorageType.ToString()
                };

                if (IsUnitValue)
                {
                    snapshot["inputValue"] = InputValue;
                    snapshot["internalValue"] = StorageValue;
                }
                AddIfNotBlank(snapshot, "specTypeId", SpecTypeId);
                AddIfNotBlank(snapshot, "requestedUnitTypeId", RequestedUnitTypeId);
                AddIfNotBlank(snapshot, "unitTypeId", ParameterUnitTypeId);
                if (ReferencedElement != null)
                {
                    snapshot["referencedElement"] = ElementSummary(ReferencedElement.Document, ReferencedElement);
                }
                return snapshot;
            }
        }

        private static ParameterResolution ResolveParameter(
            Element element,
            Dictionary<string, object> parameterRef,
            string legacyParameterName)
        {
            if (element == null)
            {
                return ParameterResolution.Failure(parameterRef != null, null, null, parameterRef, "A target element is required.");
            }

            if (parameterRef == null)
            {
                string legacyName = NormalizeOptionalText(legacyParameterName);
                Parameter legacyParameter = string.IsNullOrWhiteSpace(legacyName) ? null : element.LookupParameter(legacyName);
                return legacyParameter == null
                    ? ParameterResolution.Failure(false, "legacyName", legacyName, null,
                        "Parameter '" + legacyName + "' was not found on element " + ToElementIdString(element.Id) + ".")
                    : ParameterResolution.Success(legacyParameter, false, "legacyName", legacyName, null);
            }

            string kind = NormalizeOptionalText(GetString(parameterRef, "kind"));
            Dictionary<string, object> requestedReference = CloneDictionary(parameterRef);
            if (string.Equals(kind, "builtInParameter", StringComparison.OrdinalIgnoreCase))
            {
                string requested = NormalizeOptionalText(GetString(parameterRef, "builtInParameter"));
                if (string.IsNullOrWhiteSpace(requested) ||
                    !Enum.TryParse(requested, true, out BuiltInParameter builtInParameter) ||
                    !Enum.IsDefined(typeof(BuiltInParameter), builtInParameter))
                {
                    return ParameterResolution.Failure(true, kind, requested, requestedReference,
                        "Unknown builtInParameter '" + (requested ?? string.Empty) + "' for element " + ToElementIdString(element.Id) + ".");
                }

                Parameter parameter = element.get_Parameter(builtInParameter);
                return parameter == null
                    ? ParameterResolution.Failure(true, kind, requested, requestedReference,
                        "Built-in parameter '" + requested + "' was not found on element " + ToElementIdString(element.Id) + ".")
                    : ParameterResolution.Success(parameter, true, kind, requested, requestedReference);
            }

            if (string.Equals(kind, "definitionId", StringComparison.OrdinalIgnoreCase))
            {
                string requested = NormalizeOptionalText(GetString(parameterRef, "definitionId"));
                if (!long.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out long requestedId) || requestedId == -1)
                {
                    return ParameterResolution.Failure(true, kind, requested, requestedReference,
                        "definitionId must be a valid Revit parameter id for element " + ToElementIdString(element.Id) + ".");
                }

                List<Parameter> matches = EnumerateParameters(element)
                    .Where(parameter => GetParameterIdValue(parameter) == requestedId)
                    .ToList();
                return ResolveUniqueParameterMatch(element, matches, kind, requested, requestedReference);
            }

            if (string.Equals(kind, "sharedParameterGuid", StringComparison.OrdinalIgnoreCase))
            {
                string requested = NormalizeOptionalText(GetString(parameterRef, "sharedParameterGuid"));
                if (!Guid.TryParse(requested, out Guid requestedGuid))
                {
                    return ParameterResolution.Failure(true, kind, requested, requestedReference,
                        "sharedParameterGuid '" + (requested ?? string.Empty) + "' is invalid for element " + ToElementIdString(element.Id) + ".");
                }

                List<Parameter> matches = EnumerateParameters(element)
                    .Where(parameter => TryGetSharedParameterGuid(parameter, out Guid parameterGuid) && parameterGuid == requestedGuid)
                    .ToList();
                return ResolveUniqueParameterMatch(element, matches, kind, requestedGuid.ToString("D"), requestedReference);
            }

            if (string.Equals(kind, "name", StringComparison.OrdinalIgnoreCase))
            {
                string requested = NormalizeOptionalText(GetString(parameterRef, "name"));
                if (string.IsNullOrWhiteSpace(requested))
                {
                    return ParameterResolution.Failure(true, kind, requested, requestedReference,
                        "parameterRef name cannot be empty for element " + ToElementIdString(element.Id) + ".");
                }

                List<Parameter> matches = EnumerateParameters(element)
                    .Where(parameter => string.Equals(parameter?.Definition?.Name, requested, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return ResolveUniqueParameterMatch(element, matches, kind, requested, requestedReference);
            }

            return ParameterResolution.Failure(true, kind, null, requestedReference,
                "Unsupported parameterRef kind '" + (kind ?? string.Empty) + "' for element " + ToElementIdString(element.Id) + ".");
        }

        private static ParameterResolution ResolveUniqueParameterMatch(
            Element element,
            List<Parameter> matches,
            string kind,
            string requested,
            Dictionary<string, object> requestedReference)
        {
            int matchCount = matches?.Count ?? 0;
            string identity = kind + " '" + (requested ?? string.Empty) + "'";
            if (matchCount == 0)
            {
                return ParameterResolution.Failure(true, kind, requested, requestedReference,
                    "Parameter " + identity + " was not found on element " + ToElementIdString(element.Id) + ".");
            }
            if (matchCount > 1)
            {
                return ParameterResolution.Failure(true, kind, requested, requestedReference,
                    "Parameter " + identity + " is ambiguous on element " + ToElementIdString(element.Id) +
                    "; " + matchCount.ToString(CultureInfo.InvariantCulture) + " parameters matched.");
            }
            return ParameterResolution.Success(matches[0], true, kind, requested, requestedReference);
        }

        private static IEnumerable<Parameter> EnumerateParameters(Element element)
        {
            if (element?.Parameters == null) return Enumerable.Empty<Parameter>();
            return element.Parameters.Cast<Parameter>();
        }

        private static long GetParameterIdValue(Parameter parameter)
        {
            try
            {
                return parameter == null ? long.MinValue : GetElementIdValue(parameter.Id);
            }
            catch
            {
                return long.MinValue;
            }
        }

        private static bool TryGetSharedParameterGuid(Parameter parameter, out Guid guid)
        {
            guid = Guid.Empty;
            try
            {
                if (parameter == null || !parameter.IsShared) return false;
                guid = parameter.GUID;
                return guid != Guid.Empty;
            }
            catch
            {
                ExternalDefinition definition = parameter?.Definition as ExternalDefinition;
                if (definition == null) return false;
                guid = definition.GUID;
                return guid != Guid.Empty;
            }
        }

        private static Dictionary<string, object> ElementTarget(Element element, string parameterName)
        {
            var target = new Dictionary<string, object>
            {
                ["elementId"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element)
            };
            if (element.Category != null) target["category"] = element.Category.Name;
            if (!string.IsNullOrWhiteSpace(parameterName)) target["parameterName"] = parameterName;
            target["editability"] = BuildEditabilityEvidence(element);
            return target;
        }

        private static Dictionary<string, object> ParameterTarget(Element element, ParameterResolution resolution)
        {
            Dictionary<string, object> target = ElementTarget(element, resolution.ResolvedName);
            if (resolution.RequestedReference != null)
            {
                target["requestedParameterRef"] = CloneDictionary(resolution.RequestedReference);
            }
            else
            {
                target["legacyParameterName"] = resolution.RequestedValue;
            }

            Dictionary<string, object> resolvedParameter = BuildParameterSummary(resolution.Parameter, null, false);
            resolvedParameter.Remove("source");
            target["resolvedParameter"] = resolvedParameter;
            return target;
        }

        private static Dictionary<string, object> ElementTypeTarget(ElementType elementType)
        {
            Dictionary<string, object> target = ElementTypeSnapshot(elementType);
            target["elementId"] = ToElementIdString(elementType.Id);
            target["editability"] = BuildEditabilityEvidence(elementType);
            return target;
        }

        private static Dictionary<string, object> ParameterSnapshot(Parameter parameter)
        {
            Dictionary<string, object> snapshot = BuildParameterSummary(parameter, null, true);
            snapshot.Remove("source");
            return snapshot;
        }

        private static PreparedParameterValue PrepareParameterValue(
            Document document,
            Parameter parameter,
            object value,
            bool stableReference)
        {
            if (parameter.IsReadOnly) throw new InvalidOperationException("Parameter is read-only.");

            ForgeTypeId dataType = GetParameterDataType(parameter);
            string specTypeId = GetForgeTypeIdString(dataType);
            string parameterUnitTypeId = GetForgeTypeIdString(GetParameterUnitTypeId(parameter));
            Dictionary<string, object> unitValue = value as Dictionary<string, object>;
            if (unitValue != null)
            {
                if (parameter.StorageType != StorageType.Double)
                {
                    throw new InvalidOperationException("UnitValue is only valid for Double parameters.");
                }
                if (!IsMeasurableParameter(parameter, dataType))
                {
                    throw new InvalidOperationException("The Double parameter does not expose a measurable Revit spec.");
                }

                if (!unitValue.TryGetValue("value", out object numericValue) || numericValue == null || numericValue is bool)
                {
                    throw new InvalidOperationException("UnitValue.value must be a finite number.");
                }
                double input = Convert.ToDouble(numericValue, CultureInfo.InvariantCulture);
                EnsureFinite(input, "UnitValue.value");
                string unit = NormalizeOptionalText(GetString(unitValue, "unit"));
                if (string.IsNullOrWhiteSpace(unit)) throw new InvalidOperationException("UnitValue.unit is required.");

                double internalValue;
                string requestedUnitTypeId = null;
                if (IsInternalUnitToken(unit))
                {
                    internalValue = input;
                }
                else
                {
                    ForgeTypeId requestedUnit = ResolveUnitTypeId(unit);
                    if (!UnitUtils.IsUnit(requestedUnit) || !UnitUtils.IsValidUnit(dataType, requestedUnit))
                    {
                        throw new InvalidOperationException(
                            "Unit '" + unit + "' is not compatible with parameter spec '" + specTypeId + "'.");
                    }
                    internalValue = UnitUtils.ConvertToInternalUnits(input, requestedUnit);
                    requestedUnitTypeId = GetForgeTypeIdString(requestedUnit);
                }
                EnsureFinite(internalValue, "Converted internal value");

                return new PreparedParameterValue
                {
                    InputValue = CloneDictionary(unitValue),
                    StorageValue = internalValue,
                    IsUnitValue = true,
                    SpecTypeId = specTypeId,
                    RequestedUnitTypeId = requestedUnitTypeId,
                    ParameterUnitTypeId = parameterUnitTypeId
                };
            }

            var prepared = new PreparedParameterValue
            {
                InputValue = value,
                SpecTypeId = specTypeId,
                ParameterUnitTypeId = parameterUnitTypeId
            };

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    prepared.StorageValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    break;
                case StorageType.Integer:
                    if (IsYesNoParameter(parameter, dataType))
                    {
                        prepared.StorageValue = NormalizeYesNoValue(value, stableReference);
                    }
                    else
                    {
                        if (stableReference && value is bool)
                        {
                            throw new InvalidOperationException("Boolean values are only valid for Yes/No integer parameters.");
                        }
                        prepared.StorageValue = NormalizeIntegerValue(value, stableReference);
                    }
                    break;
                case StorageType.Double:
                    if (value is bool) throw new InvalidOperationException("Boolean values are not valid for Double parameters.");
                    double doubleValue = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    EnsureFinite(doubleValue, "Double parameter value");
                    prepared.StorageValue = doubleValue;
                    break;
                case StorageType.ElementId:
                    ElementId elementId = NormalizeElementIdValue(value);
                    prepared.StorageValue = elementId;
                    if (GetElementIdValue(elementId) >= 0)
                    {
                        prepared.ReferencedElement = document.GetElement(elementId);
                        if (prepared.ReferencedElement == null)
                        {
                            throw new InvalidOperationException("Referenced element " + ToElementIdString(elementId) + " was not found.");
                        }
                    }
                    break;
                default:
                    throw new InvalidOperationException("Unsupported parameter storage type: " + parameter.StorageType);
            }

            return prepared;
        }

        private static void SetPreparedParameterValue(Parameter parameter, PreparedParameterValue prepared)
        {
            bool changed;
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    changed = parameter.Set((string)prepared.StorageValue);
                    break;
                case StorageType.Integer:
                    changed = parameter.Set((int)prepared.StorageValue);
                    break;
                case StorageType.Double:
                    changed = parameter.Set((double)prepared.StorageValue);
                    break;
                case StorageType.ElementId:
                    changed = parameter.Set((ElementId)prepared.StorageValue);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported parameter storage type: " + parameter.StorageType);
            }

            if (!changed && !ParameterMatchesPreparedValue(parameter, prepared.StorageValue))
            {
                throw new InvalidOperationException("Revit rejected the parameter value.");
            }
        }

        private static bool ParameterMatchesPreparedValue(Parameter parameter, object preparedValue)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return string.Equals(parameter.AsString() ?? string.Empty, (string)preparedValue ?? string.Empty, StringComparison.Ordinal);
                case StorageType.Integer:
                    return parameter.AsInteger() == (int)preparedValue;
                case StorageType.Double:
                    double expected = (double)preparedValue;
                    double tolerance = Math.Max(0.000000001, Math.Abs(expected) * 0.000000001);
                    return Math.Abs(parameter.AsDouble() - expected) <= tolerance;
                case StorageType.ElementId:
                    return GetElementIdValue(parameter.AsElementId()) == GetElementIdValue((ElementId)preparedValue);
                default:
                    return false;
            }
        }

        private static int NormalizeYesNoValue(object value, bool stableReference)
        {
            if (value is bool booleanValue) return booleanValue ? 1 : 0;
            if (!stableReference) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (!IsNumeric(value)) throw new InvalidOperationException("Yes/No parameters accept only true, false, 0, or 1.");

            double numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Math.Abs(numeric) < 0.000000001) return 0;
            if (Math.Abs(numeric - 1.0) < 0.000000001) return 1;
            throw new InvalidOperationException("Yes/No parameters accept only true, false, 0, or 1.");
        }

        private static int NormalizeIntegerValue(object value, bool stableReference)
        {
            if (!stableReference) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (!IsNumeric(value)) throw new InvalidOperationException("Integer parameters require an integer numeric value.");

            double numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            EnsureFinite(numeric, "Integer parameter value");
            if (numeric < int.MinValue || numeric > int.MaxValue || Math.Abs(numeric - Math.Round(numeric)) > 0.000000001)
            {
                throw new InvalidOperationException("Integer parameters require a whole number in the Int32 range.");
            }
            return Convert.ToInt32(numeric, CultureInfo.InvariantCulture);
        }

        private static ElementId NormalizeElementIdValue(object value)
        {
            if (value == null || value is bool) throw new InvalidOperationException("ElementId parameters require a numeric element id.");
            if (IsNumeric(value))
            {
                double numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                EnsureFinite(numeric, "ElementId parameter value");
                if (Math.Abs(numeric - Math.Round(numeric)) > 0.000000001)
                {
                    throw new InvalidOperationException("ElementId parameters require a whole numeric id.");
                }
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                throw new InvalidOperationException("ElementId parameters require a numeric element id.");
            }
            return CreateElementId(parsed.ToString(CultureInfo.InvariantCulture));
        }

        private static ForgeTypeId GetParameterDataType(Parameter parameter)
        {
            try
            {
#if REVIT2021
#pragma warning disable CS0618
                return parameter?.Definition?.GetSpecTypeId();
#pragma warning restore CS0618
#else
                return parameter?.Definition?.GetDataType();
#endif
            }
            catch
            {
                return null;
            }
        }

        private static ForgeTypeId GetParameterUnitTypeId(Parameter parameter)
        {
            try
            {
                return parameter?.StorageType == StorageType.Double ? parameter.GetUnitTypeId() : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsMeasurableParameter(Parameter parameter, ForgeTypeId dataType)
        {
#if REVIT2021
#pragma warning disable CS0618
            return parameter?.StorageType == StorageType.Double &&
                   parameter.Definition != null &&
                   parameter.Definition.UnitType != UnitType.UT_Undefined;
#pragma warning restore CS0618
#else
            return dataType != null && !dataType.Empty() && UnitUtils.IsMeasurableSpec(dataType);
#endif
        }

        private static bool IsYesNoParameter(Parameter parameter, ForgeTypeId dataType)
        {
#if REVIT2021
#pragma warning disable CS0618
            return parameter?.Definition?.ParameterType == ParameterType.YesNo;
#pragma warning restore CS0618
#else
            return ParameterWriteContract.IsYesNoDataType(dataType);
#endif
        }

        private static string GetForgeTypeIdString(ForgeTypeId typeId)
        {
            return ParameterWriteContract.GetForgeTypeIdString(typeId);
        }

        private static bool IsInternalUnitToken(string unit)
        {
            return ParameterWriteContract.IsInternalUnitToken(unit);
        }

        private static ForgeTypeId ResolveUnitTypeId(string unit)
        {
            return ParameterWriteContract.ResolveUnitTypeId(unit);
        }

        private static void EnsureFinite(double value, string label)
        {
            ParameterWriteContract.EnsureFinite(value, label);
        }

        private static object SerializeStorageValue(object value)
        {
            return value is ElementId elementId ? ToElementIdString(elementId) : value;
        }

        private static Dictionary<string, object> BuildEditabilityEvidence(Element element)
        {
            var evidence = new Dictionary<string, object>();
            Document document = element?.Document;
            bool isWorkshared = document?.IsWorkshared == true;
            evidence["isWorkshared"] = isWorkshared;
            if (!isWorkshared)
            {
                evidence["checkoutStatus"] = "NotApplicable";
                evidence["isEditable"] = true;
                evidence["ownedByOtherUser"] = false;
                return evidence;
            }

            try
            {
                string owner;
                CheckoutStatus status = WorksharingUtils.GetCheckoutStatus(document, element.Id, out owner);
                evidence["checkoutStatus"] = status.ToString();
                evidence["isEditable"] = status != CheckoutStatus.OwnedByOtherUser;
                evidence["ownedByOtherUser"] = status == CheckoutStatus.OwnedByOtherUser;
                AddIfNotBlank(evidence, "owner", owner);

                if (string.IsNullOrWhiteSpace(owner) && status == CheckoutStatus.OwnedByOtherUser)
                {
                    using (WorksharingTooltipInfo tooltip = WorksharingUtils.GetWorksharingTooltipInfo(document, element.Id))
                    {
                        AddIfNotBlank(evidence, "owner", tooltip?.Owner);
                        AddIfNotBlank(evidence, "lastChangedBy", tooltip?.LastChangedBy);
                    }
                }
            }
            catch (Exception ex)
            {
                evidence["checkoutStatus"] = "Unknown";
                evidence["isEditable"] = null;
                evidence["ownedByOtherUser"] = false;
                evidence["evidenceError"] = ex.Message;
            }
            return evidence;
        }

        private static string OwnedByOtherUserError(Dictionary<string, object> target, string label)
        {
            Dictionary<string, object> editability = GetDictionary(target, "editability");
            if (!GetBool(editability, "isWorkshared", false) || GetBool(editability, "isEditable", false)) return null;

            string owner = GetString(editability, "owner");
            if (GetBool(editability, "ownedByOtherUser", false))
            {
                return label + " is owned by another Revit user" +
                       (string.IsNullOrWhiteSpace(owner) ? "." : " ('" + owner + "').") +
                       " Synchronize or request ownership before previewing this edit.";
            }

            string evidenceError = GetString(editability, "evidenceError");
            return label + " editability could not be verified in this workshared document." +
                   (string.IsNullOrWhiteSpace(evidenceError) ? string.Empty : " Revit reported: " + evidenceError) +
                   " Refresh worksharing state before previewing this edit.";
        }
    }
}
