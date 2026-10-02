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
