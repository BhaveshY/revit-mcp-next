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
        private BridgeResponseEnvelope HandlePreviewChange(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.preview_change_set.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            var warnings = new List<BridgeWarning>();
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> operations = GetOperations(payload);
            if (operations.Count == 0)
            {
                return Failure(request, "EMPTY_CHANGE_SET", "A change set must contain at least one operation.", sw);
            }
            if (operations.Count > MaxChangeSetOperations)
            {
                return Failure(request, "CHANGE_SET_TOO_LARGE", "A change set can contain at most 50 operations.", sw);
            }

            string transactionName = GetTransactionName(payload);
            string documentFingerprint = ComputeDocumentFingerprint(document);
            string previewId = ComputePreviewId(document, transactionName, operations);
            var changes = new List<Dictionary<string, object>>();
            var validationContext = new PreviewValidationContext();
            bool ready = true;
            string riskLevel = "low";

            for (int index = 0; index < operations.Count; index++)
            {
                Dictionary<string, object> change = PreviewOperation(document, operations[index], index, validationContext);
                changes.Add(change);
                string status = GetString(change, "status");
                if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase)) ready = false;
                string operationType = GetString(operations[index], "type");
                if (IsViewWorkflowOperation(operationType) || string.Equals(operationType, "create_level", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_wall", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_grid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_floor", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_room", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "place_family_instance", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_sheet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "place_view_on_sheet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_schedule", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "add_schedule_field", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "place_schedule_on_sheet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "create_text_note", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "load_family", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "tag_room", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "tag_element", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "copy_element", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "change_element_type", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "rename_element_type", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(operationType, "duplicate_element_type", StringComparison.OrdinalIgnoreCase))
                {
                    riskLevel = "medium";
                }
                if (string.Equals(operationType, "delete_element", StringComparison.OrdinalIgnoreCase))
                {
                    riskLevel = "high";
                }
            }

            string operationsHash = ComputeChangeSetHash(operations);
            string changesHash = ComputeChangeSetHash(changes);
            string changeSetHash = ComputePreviewChangeSetHash(documentFingerprint, generation, transactionName, operationsHash, changesHash);
            PreviewToken token = _previewTokens.Issue(
                previewId,
                request.SessionId,
                _runtimeInstanceId,
                documentFingerprint,
                generation,
                transactionName,
                operationsHash,
                changesHash,
                changeSetHash,
                ready,
                operations.Count);

            var data = new Dictionary<string, object>
            {
                ["previewId"] = previewId,
                ["instanceId"] = _runtimeInstanceId,
                ["documentFingerprint"] = documentFingerprint,
                ["changeSetHash"] = changeSetHash,
                ["baseGeneration"] = generation,
                ["generation"] = generation,
                ["transactionName"] = transactionName,
                ["operationCount"] = operations.Count,
                ["ready"] = ready,
                ["requiresConfirmation"] = true,
                ["expiresAt"] = FormatUtc(token.ExpiresAtUtc),
                ["previewExpiresAtUtc"] = FormatUtc(token.ExpiresAtUtc),
                ["riskLevel"] = riskLevel,
                ["changes"] = changes.ToArray()
            };

            return Success(request, data, sw, warnings, new BridgeMetrics
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                ReturnedCount = changes.Count,
                TotalCount = changes.Count
            }, generation: generation);
        }

        private BridgeResponseEnvelope HandleApplyChange(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                return Failure(request, "NO_ACTIVE_DOCUMENT", "Open a Revit project document before calling revit.apply_change_set.", sw);
            }

            BridgeResponseEnvelope generationFailure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (generationFailure != null) return generationFailure;

            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            if (!GetBool(payload, "confirm", false))
            {
                return Failure(request, "CONFIRMATION_REQUIRED", "revit.apply_change_set requires confirm=true.", sw);
            }

            string providedPreviewId = GetString(payload, "previewId");
            string providedChangeSetHash = GetString(payload, "changeSetHash");
            string providedExpiresAt = GetString(payload, "expiresAt");
            long? providedBaseGeneration = GetLong(payload, "baseGeneration");
            if (string.IsNullOrWhiteSpace(providedPreviewId) ||
                string.IsNullOrWhiteSpace(providedChangeSetHash) ||
                string.IsNullOrWhiteSpace(providedExpiresAt) ||
                !providedBaseGeneration.HasValue)
            {
                return Failure(request, "PREVIEW_METADATA_REQUIRED", "revit.apply_change_set requires previewId, baseGeneration, changeSetHash, and expiresAt from revit.preview_change_set.", sw);
            }

            if (!DateTimeOffset.TryParse(providedExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedExpiresAt))
            {
                return Failure(request, "PREVIEW_EXPIRES_AT_INVALID", "revit.apply_change_set expiresAt must be the ISO 8601 timestamp returned by revit.preview_change_set.", sw);
            }

            if (parsedExpiresAt <= DateTimeOffset.UtcNow)
            {
                return Failure(request, "PREVIEW_EXPIRED", "The preview has expired. Run revit.preview_change_set again before applying.", sw);
            }

            List<Dictionary<string, object>> operations = GetOperations(payload);
            if (operations.Count == 0)
            {
                return Failure(request, "EMPTY_CHANGE_SET", "A change set must contain at least one operation.", sw);
            }
            if (operations.Count > MaxChangeSetOperations)
            {
                return Failure(request, "CHANGE_SET_TOO_LARGE", "A change set can contain at most 50 operations.", sw);
            }

            string transactionName = GetTransactionName(payload);
            string documentFingerprint = ComputeDocumentFingerprint(document);
            string expectedPreviewId = ComputePreviewId(document, transactionName, operations);
            if (!string.Equals(providedPreviewId, expectedPreviewId, StringComparison.Ordinal))
            {
                return Failure(request, "PREVIEW_ID_MISMATCH", "The supplied previewId does not match the current change set and document.", sw);
            }

            PreviewTokenValidation metadataValidation = _previewTokens.ValidateMetadata(
                providedPreviewId,
                request.SessionId,
                _runtimeInstanceId,
                documentFingerprint,
                generation,
                providedChangeSetHash);
            if (!metadataValidation.Ok)
            {
                return Failure(request, metadataValidation.Code, metadataValidation.Message, sw);
            }

            var previewChanges = new List<Dictionary<string, object>>();
            var validationContext = new PreviewValidationContext();
            for (int index = 0; index < operations.Count; index++)
            {
                Dictionary<string, object> change = PreviewOperation(document, operations[index], index, validationContext);
                previewChanges.Add(change);
            }

            PreviewTokenValidation tokenValidation = _previewTokens.Validate(
                providedPreviewId,
                request.SessionId,
                _runtimeInstanceId,
                documentFingerprint,
                generation,
                transactionName,
                ComputeChangeSetHash(operations),
                ComputeChangeSetHash(previewChanges),
                providedChangeSetHash);
            if (!tokenValidation.Ok)
            {
                return Failure(request, tokenValidation.Code, tokenValidation.Message, sw);
            }

            _previewTokens.Consume(request.SessionId, _runtimeInstanceId, providedPreviewId);
            List<Dictionary<string, object>> appliedChanges = _transactions.Write(document, transactionName, () =>
            {
                var results = new List<Dictionary<string, object>>();
                for (int index = 0; index < operations.Count; index++)
                {
                    results.Add(ApplyOperation(document, operations[index], index));
                }
                return results;
            }, failOnWarnings: operations.Any(op => IsViewWorkflowOperation(GetString(op, "type"))));
            long appliedGeneration = _generations.GetGeneration(document);

            var data = new Dictionary<string, object>
            {
                ["previewId"] = expectedPreviewId,
                ["instanceId"] = _runtimeInstanceId,
                ["documentFingerprint"] = documentFingerprint,
                ["changeSetHash"] = tokenValidation.Token.ChangeSetHash,
                ["baseGeneration"] = tokenValidation.Token.Generation,
                ["generation"] = appliedGeneration,
                ["transactionName"] = transactionName,
                ["applied"] = true,
                ["changedCount"] = appliedChanges.Count,
                ["changes"] = appliedChanges.ToArray()
            };

            return Success(request, data, sw, metrics: new BridgeMetrics
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                ReturnedCount = appliedChanges.Count,
                TotalCount = appliedChanges.Count
            }, generation: appliedGeneration);
        }

        private static Dictionary<string, object> PreviewOperation(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string type = GetString(operation, "type");
            switch (type)
            {
                case "create_plan_view":
                case "duplicate_view":
                case "duplicate_sheet":
                case "copy_view_annotations":
                case "create_dimension":
                case "update_dimension":
                return PreviewViewOperation(document, operation, index);
                case "set_parameter":
                    return PreviewSetParameter(document, operation, index);
                case "create_level":
                    return PreviewCreateLevel(document, operation, index, validationContext);
                case "create_wall":
                    return PreviewCreateWall(document, operation, index);
                case "move_element":
                    return PreviewMoveElement(document, operation, index);
                case "rotate_element":
                    return PreviewRotateElement(document, operation, index);
                case "copy_element":
                    return PreviewCopyElement(document, operation, index);
                case "change_element_type":
                    return PreviewChangeElementType(document, operation, index);
                case "rename_element_type":
                    return PreviewRenameElementType(document, operation, index, validationContext);
                case "duplicate_element_type":
                    return PreviewDuplicateElementType(document, operation, index, validationContext);
                case "set_element_pinned":
                    return PreviewSetElementPinned(document, operation, index);
                case "create_grid":
                    return PreviewCreateGrid(document, operation, index, validationContext);
                case "create_floor":
                    return PreviewCreateFloor(document, operation, index);
                case "create_room":
                    return PreviewCreateRoom(document, operation, index, validationContext);
                case "place_family_instance":
                    return PreviewPlaceFamilyInstance(document, operation, index);
                case "create_sheet":
                    return PreviewCreateSheet(document, operation, index, validationContext);
                case "place_view_on_sheet":
                    return PreviewPlaceViewOnSheet(document, operation, index);
                case "create_schedule":
                    return PreviewCreateSchedule(document, operation, index);
                case "add_schedule_field":
                    return PreviewAddScheduleField(document, operation, index);
                case "place_schedule_on_sheet":
                    return PreviewPlaceScheduleOnSheet(document, operation, index);
                case "create_text_note":
                    return PreviewCreateTextNote(document, operation, index);
                case "load_family":
                    return PreviewLoadFamily(document, operation, index);
                case "tag_room":
                    return PreviewTagRoom(document, operation, index);
                case "tag_element":
                    return PreviewTagElement(document, operation, index);
                case "delete_element":
                    return PreviewDeleteElement(document, operation, index);
                default:
                    return BlockedChange(operation, index, "Unsupported change operation type: " + (type ?? "(missing)"));
            }
        }

        private static Dictionary<string, object> ApplyOperation(Document document, Dictionary<string, object> operation, int index)
        {
            string type = GetString(operation, "type");
            switch (type)
            {
                case "create_plan_view":
                case "duplicate_view":
                case "duplicate_sheet":
                case "copy_view_annotations":
                case "create_dimension":
                case "update_dimension":
                return ApplyViewOperation(document, operation, index);
                case "set_parameter":
                    return ApplySetParameter(document, operation, index);
                case "create_level":
                    return ApplyCreateLevel(document, operation, index);
                case "create_wall":
                    return ApplyCreateWall(document, operation, index);
                case "move_element":
                    return ApplyMoveElement(document, operation, index);
                case "rotate_element":
                    return ApplyRotateElement(document, operation, index);
                case "copy_element":
                    return ApplyCopyElement(document, operation, index);
                case "change_element_type":
                    return ApplyChangeElementType(document, operation, index);
                case "rename_element_type":
                    return ApplyRenameElementType(document, operation, index);
                case "duplicate_element_type":
                    return ApplyDuplicateElementType(document, operation, index);
                case "set_element_pinned":
                    return ApplySetElementPinned(document, operation, index);
                case "create_grid":
                    return ApplyCreateGrid(document, operation, index);
                case "create_floor":
                    return ApplyCreateFloor(document, operation, index);
                case "create_room":
                    return ApplyCreateRoom(document, operation, index);
                case "place_family_instance":
                    return ApplyPlaceFamilyInstance(document, operation, index);
                case "create_sheet":
                    return ApplyCreateSheet(document, operation, index);
                case "place_view_on_sheet":
                    return ApplyPlaceViewOnSheet(document, operation, index);
                case "create_schedule":
                    return ApplyCreateSchedule(document, operation, index);
                case "add_schedule_field":
                    return ApplyAddScheduleField(document, operation, index);
                case "place_schedule_on_sheet":
                    return ApplyPlaceScheduleOnSheet(document, operation, index);
                case "create_text_note":
                    return ApplyCreateTextNote(document, operation, index);
                case "load_family":
                    return ApplyLoadFamily(document, operation, index);
                case "tag_room":
                    return ApplyTagRoom(document, operation, index);
                case "tag_element":
                    return ApplyTagElement(document, operation, index);
                case "delete_element":
                    return ApplyDeleteElement(document, operation, index);
                default:
                    throw new InvalidOperationException("Unsupported change operation type: " + (type ?? "(missing)"));
            }
        }

        private static Dictionary<string, object> Change(
            Dictionary<string, object> operation,
            int index,
            string status,
            Dictionary<string, object> target,
            Dictionary<string, object> before,
            Dictionary<string, object> after,
            string message = null)
        {
            var change = new Dictionary<string, object>
            {
                ["operationIndex"] = index,
                ["type"] = GetString(operation, "type") ?? "unknown",
                ["status"] = status
            };

            string operationId = GetString(operation, "id");
            if (!string.IsNullOrWhiteSpace(operationId)) change["operationId"] = operationId;
            if (target != null) change["target"] = target;
            if (before != null) change["before"] = before;
            if (after != null) change["after"] = after;
            if (!string.IsNullOrWhiteSpace(message)) change["message"] = message;
            return change;
        }

        private static Dictionary<string, object> BlockedChange(Dictionary<string, object> operation, int index, string message)
        {
            return Change(operation, index, "blocked", null, null, null, message);
        }

        private static string ValidateExpectedUniqueId(
            Dictionary<string, object> operation,
            Element element,
            string elementId,
            string fieldName = "expectedUniqueId",
            string label = "Element")
        {
            string expectedUniqueId = GetString(operation, fieldName);
            if (string.IsNullOrWhiteSpace(expectedUniqueId)) return null;

            if (!string.Equals(element.UniqueId, expectedUniqueId, StringComparison.Ordinal))
            {
                return label + " " + elementId + " uniqueId did not match " + fieldName + ".";
            }

            return null;
        }

        private sealed class PreviewValidationContext
        {
            private readonly HashSet<string> _levelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _gridNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _roomNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _sheetNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _elementTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool TryAddLevelName(string name)
            {
                return _levelNames.Add((name ?? string.Empty).Trim());
            }

            public bool TryAddGridName(string name)
            {
                return _gridNames.Add((name ?? string.Empty).Trim());
            }

            public bool TryAddRoomNumber(string number)
            {
                return _roomNumbers.Add((number ?? string.Empty).Trim());
            }

            public bool TryAddSheetNumber(string number)
            {
                return _sheetNumbers.Add((number ?? string.Empty).Trim());
            }

            public bool TryAddElementTypeName(ElementType elementType, string name)
            {
                string key = GetElementTypeNameScopeKey(elementType) + "|name:" + NormalizeOptionalText(name);
                return _elementTypeNames.Add(key);
            }
        }

        private static string GetTransactionName(Dictionary<string, object> payload)
        {
            string name = GetString(payload, "transactionName");
            return string.IsNullOrWhiteSpace(name) ? "Revit MCP Next change" : name.Trim();
        }

        private string ComputePreviewId(Document document, string transactionName, List<Dictionary<string, object>> operations)
        {
            string raw = _runtimeInstanceId + "|" + ComputeDocumentFingerprint(document) + "|" + transactionName + "|" + Canonicalize(operations);
            return HashString(raw).Substring(0, 24);
        }

        private static string ComputeChangeSetHash(object value)
        {
            return "sha256:" + HashString(Canonicalize(value));
        }

        private static string ComputePreviewChangeSetHash(
            string documentFingerprint,
            long generation,
            string transactionName,
            string operationsHash,
            string changesHash)
        {
            return ComputeChangeSetHash(new Dictionary<string, object>
            {
                ["documentFingerprint"] = documentFingerprint,
                ["baseGeneration"] = generation,
                ["transactionName"] = transactionName,
                ["operationsHash"] = operationsHash,
                ["changesHash"] = changesHash
            });
        }

        private static string FormatUtc(DateTimeOffset value)
        {
            return value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        private static string Canonicalize(object value)
        {
            return CanonicalJson.Serialize(value);
        }

        private static string HashString(string raw)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
