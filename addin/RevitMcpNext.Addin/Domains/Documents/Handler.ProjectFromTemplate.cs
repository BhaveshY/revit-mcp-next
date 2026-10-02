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
        private BridgeResponseEnvelope HandleCreateProjectFromTemplate(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            Dictionary<string, object> payload = request.Payload ?? new Dictionary<string, object>();
            if (!GetBool(payload, "confirm", false))
            {
                return Failure(request, "CONFIRMATION_REQUIRED", "revit.create_project_from_template requires confirm=true because it creates or overwrites a local RVT file.", sw);
            }

            string templatePathRaw = GetString(payload, "templatePath");
            string outputPathRaw = GetString(payload, "outputPath");
            if (string.IsNullOrWhiteSpace(templatePathRaw))
            {
                return Failure(request, "TEMPLATE_PATH_REQUIRED", "templatePath is required and must point to a local .rte file.", sw);
            }
            if (string.IsNullOrWhiteSpace(outputPathRaw))
            {
                return Failure(request, "OUTPUT_PATH_REQUIRED", "outputPath is required and must point to a disposable .rvt file.", sw);
            }

            string templatePath;
            string outputPath;
            try
            {
                templatePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(templatePathRaw.Trim()));
                outputPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(outputPathRaw.Trim()));
            }
            catch (Exception ex)
            {
                return Failure(request, "INVALID_MODEL_PATH", "templatePath and outputPath must be valid local paths. " + ex.Message, sw);
            }

            if (!string.Equals(Path.GetExtension(templatePath), ".rte", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(request, "TEMPLATE_EXTENSION_REQUIRED", "templatePath must point to a Revit template file (.rte): " + templatePath, sw);
            }
            if (!string.Equals(Path.GetExtension(outputPath), ".rvt", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(request, "OUTPUT_EXTENSION_REQUIRED", "outputPath must point to a Revit project file (.rvt): " + outputPath, sw);
            }
            if (!File.Exists(templatePath))
            {
                return Failure(request, "TEMPLATE_NOT_FOUND", "Revit template file was not found: " + templatePath, sw);
            }

            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Failure(request, "OUTPUT_DIRECTORY_REQUIRED", "outputPath must include a parent directory: " + outputPath, sw);
            }

            bool overwrite = GetBool(payload, "overwrite", false);
            bool outputExists = File.Exists(outputPath);
            if (outputExists && !overwrite)
            {
                return Failure(request, "OUTPUT_ALREADY_EXISTS", "Output RVT already exists. Pass overwrite=true only for a known disposable fixture: " + outputPath, sw);
            }

            Document createdDocument = null;
            try
            {
                Directory.CreateDirectory(outputDirectory);
                createdDocument = app.Application.NewProjectDocument(templatePath);
                if (createdDocument == null)
                {
                    return Failure(request, "PROJECT_CREATE_FAILED", "Revit did not create a project document from template: " + templatePath, sw);
                }

                var saveAsOptions = new SaveAsOptions
                {
                    OverwriteExistingFile = overwrite
                };
                createdDocument.SaveAs(outputPath, saveAsOptions);

                Document resultDocument = ActivateCreatedProject(app, createdDocument, outputPath);
                createdDocument = resultDocument;
                Document activeDocument = app.ActiveUIDocument?.Document;
                bool activated = IsExactDocumentIdentity(resultDocument, activeDocument, outputPath);
                if (!activated)
                {
                    throw new InvalidOperationException(
                        "Revit created the project, but the active UI document identity did not match the saved output. " +
                        "Expected path: " + outputPath + ". No target confirmation was issued.");
                }
                long generation = _generations.GetGeneration(resultDocument);
                string fingerprint = ComputeDocumentFingerprint(resultDocument);
                string centralModelPath = GetDocumentCentralModelPath(resultDocument);
                var data = new Dictionary<string, object>
                {
                    ["templatePath"] = templatePath,
                    ["outputPath"] = outputPath,
                    ["overwritten"] = outputExists,
                    ["activated"] = activated,
                    ["instanceId"] = _runtimeInstanceId,
                    ["document"] = BuildDocumentSummary(resultDocument, activeDocument),
                    ["activationConfirmation"] = new Dictionary<string, object>
                    {
                        ["confirmed"] = true,
                        ["instanceId"] = _runtimeInstanceId,
                        ["documentFingerprint"] = fingerprint,
                        ["documentPath"] = resultDocument.PathName,
                        ["centralModelPath"] = centralModelPath,
                        ["generation"] = generation,
                        ["uiActive"] = true
                    },
                    ["source"] = "revit-api"
                };

                return Success(request, data, sw, generation: generation);
            }
            catch
            {
                if (createdDocument != null && string.IsNullOrWhiteSpace(createdDocument.PathName))
                {
                    try
                    {
                        createdDocument.Close(false);
                    }
                    catch
                    {
                        // Preserve the original Revit API failure.
                    }
                }

                throw;
            }
        }

        private static Document ActivateCreatedProject(UIApplication app, Document createdDocument, string outputPath)
        {
            Document activeDocument = app.ActiveUIDocument?.Document;
            if (IsExactDocumentIdentity(createdDocument, activeDocument, outputPath))
            {
                return activeDocument;
            }

            if (createdDocument != null)
            {
                createdDocument.Close(false);
            }

            UIDocument activatedDocument = app.OpenAndActivateDocument(outputPath);
            if (activatedDocument?.Document == null)
            {
                throw new InvalidOperationException("Revit created the project but did not activate it in the UI: " + outputPath);
            }
            Document confirmedActiveDocument = app.ActiveUIDocument?.Document;
            if (!IsExactDocumentIdentity(activatedDocument.Document, confirmedActiveDocument, outputPath))
            {
                throw new InvalidOperationException(
                    "Revit opened the created project, but the active UI document did not match its exact path and fingerprint: " + outputPath);
            }

            return confirmedActiveDocument;
        }
    }
}
