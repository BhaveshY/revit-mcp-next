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
        private static bool IsExactDocumentIdentity(Document expectedDocument, Document actualDocument, string expectedPath = null)
        {
            if (expectedDocument == null || actualDocument == null) return false;
            string expectedFingerprint = ComputeDocumentFingerprint(expectedDocument);
            string actualFingerprint = ComputeDocumentFingerprint(actualDocument);
            if (!string.Equals(expectedFingerprint, actualFingerprint, StringComparison.OrdinalIgnoreCase)) return false;

            string requiredPath = string.IsNullOrWhiteSpace(expectedPath) ? expectedDocument.PathName : expectedPath;
            if (string.IsNullOrWhiteSpace(requiredPath)) return true;
            return DocumentPathsEqual(requiredPath, actualDocument.PathName);
        }

        private static bool DocumentPathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static Element ResolveElement(Document document, string elementId)
        {
            try
            {
                return document.GetElement(CreateElementId(elementId));
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, object> BuildDocumentSummary(Document document, Document activeDocument)
        {
            string fingerprint = ComputeDocumentFingerprint(document);
            string activeFingerprint = activeDocument == null ? null : ComputeDocumentFingerprint(activeDocument);
            var summary = new Dictionary<string, object>
            {
                ["documentId"] = GetDocumentId(document),
                ["title"] = document.Title,
                ["fingerprint"] = fingerprint,
                ["isActive"] = string.Equals(fingerprint, activeFingerprint, StringComparison.OrdinalIgnoreCase),
                ["isWorkshared"] = document.IsWorkshared,
                ["isModified"] = document.IsModified,
                ["generation"] = _generations.GetGeneration(document)
            };

            if (!string.IsNullOrWhiteSpace(document.PathName)) summary["path"] = document.PathName;
            string centralModelPath = GetDocumentCentralModelPath(document);
            if (!string.IsNullOrWhiteSpace(centralModelPath)) summary["centralModelPath"] = centralModelPath;

            View activeView = SafeActiveView(document);
            if (activeView != null)
            {
                summary["activeView"] = BuildViewSummary(activeView);
            }

            return summary;
        }

        private static Dictionary<string, object> BuildDocumentReference(Document document, long generation)
        {
            var summary = new Dictionary<string, object>
            {
                ["fingerprint"] = ComputeDocumentFingerprint(document),
                ["title"] = document.Title,
                ["generation"] = generation
            };

            if (!string.IsNullOrWhiteSpace(document.PathName)) summary["path"] = document.PathName;
            return summary;
        }

        private static IEnumerable<Document> EnumerateDocuments(UIApplication app)
        {
            foreach (Document document in app.Application.Documents)
            {
                if (IsTargetableTopLevelDocument(document)) yield return document;
            }
        }

        private static bool IsTargetableTopLevelDocument(Document document)
        {
            if (document == null) return false;
            try
            {
                return document.IsValidObject && !document.IsLinked;
            }
            catch
            {
                return false;
            }
        }

        private static Document ResolveDocument(UIApplication app, BridgeRequestEnvelope request)
        {
            string requestedFingerprint = request.DocumentFingerprint ?? GetString(request.Payload, "documentFingerprint");
            List<Document> documents = EnumerateDocuments(app).ToList();

            if (string.IsNullOrWhiteSpace(requestedFingerprint))
            {
                if (documents.Count == 0) return null;
                if (documents.Count == 1) return documents[0];
                throw new TargetResolutionException(
                    "TARGET_SELECTION_REQUIRED",
                    "Multiple Revit documents are open. Select an exact documentFingerprint before running a document-scoped operation.");
            }

            Document match = documents
                .FirstOrDefault(document => string.Equals(ComputeDocumentFingerprint(document), requestedFingerprint, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            throw new TargetResolutionException(
                "TARGET_DOCUMENT_UNAVAILABLE",
                "The targeted Revit document is closed, changed, or unavailable. Refresh revit.list_documents and select it again.");
        }

        private static bool IsUiActiveDocument(UIApplication app, Document document)
        {
            return app.ActiveUIDocument != null && IsExactDocumentIdentity(document, app.ActiveUIDocument.Document);
        }

        private static string GetDocumentCentralModelPath(Document document)
        {
            if (document == null || !document.IsWorkshared) return string.Empty;
            try
            {
                ModelPath modelPath = document.GetWorksharingCentralModelPath();
                return modelPath == null ? string.Empty : ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetDocumentId(Document document)
        {
            return string.IsNullOrWhiteSpace(document.PathName)
                ? document.Title + ":" + document.GetHashCode().ToString(CultureInfo.InvariantCulture)
                : document.PathName;
        }

        private static string ComputeDocumentFingerprint(Document document)
        {
            return DocumentGenerationTracker.ComputeDocumentFingerprint(document);
        }

        private static View SafeActiveView(Document document)
        {
            try
            {
                return document.ActiveView;
            }
            catch
            {
                return null;
            }
        }
    }
}
