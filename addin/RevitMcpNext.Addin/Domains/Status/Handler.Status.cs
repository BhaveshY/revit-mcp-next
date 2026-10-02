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
        private static Dictionary<string, object> BuildAddinAssemblyIdentity()
        {
            var identity = new Dictionary<string, object>();

            try
            {
                string assemblyPath = typeof(RevitExternalEventHandler).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    identity["assemblyPath"] = assemblyPath;
                    if (File.Exists(assemblyPath))
                    {
                        using (SHA256 sha = SHA256.Create())
                        using (FileStream stream = File.OpenRead(assemblyPath))
                        {
                            identity["assemblySha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                        }

                        FileVersionInfo version = FileVersionInfo.GetVersionInfo(assemblyPath);
                        if (!string.IsNullOrWhiteSpace(version.FileVersion)) identity["fileVersion"] = version.FileVersion;
                        if (!string.IsNullOrWhiteSpace(version.ProductVersion)) identity["productVersion"] = version.ProductVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                identity["assemblyIdentityError"] = ex.Message;
            }

            return identity;
        }

        private Dictionary<string, object> BuildStatus(UIApplication app)
        {
            UIDocument activeUiDocument = app.ActiveUIDocument;
            Document activeDocument = activeUiDocument?.Document;
            var data = new Dictionary<string, object>
            {
                ["connected"] = true,
                ["instanceId"] = _runtimeInstanceId,
                ["brokerVersion"] = "unknown",
                ["addinVersion"] = AddinVersion,
                ["addinAssembly"] = BuildAddinAssemblyIdentity(),
                ["bridgeProtocolVersion"] = BridgeProtocol.Version,
                ["protocolVersion"] = BridgeProtocol.Version,
                ["revit"] = new Dictionary<string, object>
                {
                    ["version"] = app.Application.VersionNumber,
                    ["build"] = app.Application.VersionBuild,
                    ["processId"] = Process.GetCurrentProcess().Id
                },
                ["selection"] = new Dictionary<string, object>
                {
                    ["count"] = activeUiDocument?.Selection?.GetElementIds()?.Count ?? 0
                },
                ["diagnostics"] = new Dictionary<string, object>
                {
                    ["queue"] = _queue.GetDiagnosticsSnapshot(),
                    ["previewTokens"] = _previewTokens.GetDiagnosticsSnapshot(),
                    ["modelDelivery"] = _modelDelivery.GetDiagnosticsSnapshot(),
                    ["recovery"] = new[]
                    {
                        "If pendingCount stays above zero or lastRaiseResult is not Accepted/Pending, bring Revit forward and close modal dialogs.",
                        "Use revit.cancel_request with a queued requestId to cancel work that has not reached the Revit API yet.",
                        "Run revit.preview_change_set again when preview tokens expire or document generation changes."
                    }
                },
                ["capabilities"] = new[]
                {
                    "revit.bridge_health",
                    "revit.get_request_result",
                    "revit.status",
                    "revit.list_documents",
                    "revit.create_project_from_template",
                    "revit.create_model_delivery_fixture",
                    "revit.get_levels",
                    "revit.get_view_details",
                    "revit.get_dimensions",
                    "revit.activate_view",
                    "revit.get_views",
                    "revit.get_sheets",
                    "revit.get_schedules",
                    "revit.get_schedule_fields",
                    "revit.get_current_view",
                    "revit.get_current_view_elements",
                    "revit.get_selection",
                    "revit.analyze_model",
                    "revit.get_model_readiness",
                    "revit.get_model_context",
                    "revit.get_material_quantities",
                    "revit.get_warnings",
                    "revit.get_rooms",
                    "revit.catalog",
                    "revit.query",
                    "revit.describe_parameters",
                    "revit.inspect_model_delivery",
                    "revit.preview_model_delivery",
                    "revit.execute_model_delivery",
                    "revit.get_model_delivery_status",
                    "revit.cancel_model_delivery",
                    "revit.preview_change_set",
                    "revit.apply_change_set",
                    "revit.cancel_request"
                },
                ["warnings"] = Array.Empty<object>()
            };

            if (activeDocument != null)
            {
                data["activeDocument"] = BuildDocumentSummary(activeDocument, activeDocument);
            }

            return data;
        }

        private object[] BuildDocumentList(UIApplication app)
        {
            Document activeDocument = app.ActiveUIDocument?.Document;
            return EnumerateDocuments(app)
                .Select(document => BuildDocumentSummary(document, activeDocument))
                .ToArray();
        }
    }
}
