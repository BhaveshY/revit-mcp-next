using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpNext.Addin.Revit
{
    internal static class ModelDeliveryFixture
    {
        private const string OwnershipFileName = ".revit-mcp-fixture.json";

        public static Dictionary<string, object> Create(UIApplication uiApplication, Dictionary<string, object> payload)
        {
            if (uiApplication == null) throw new ModelDeliveryException("REVIT_APPLICATION_REQUIRED", "Revit is not available.");
            if (!GetBool(payload, "confirm")) throw new ModelDeliveryException("CONFIRMATION_REQUIRED", "create_model_delivery_fixture requires confirm=true.");
            string templatePath = NormalizeFilePath(GetString(payload, "templatePath"), ".rte");
            string fixtureRoot = NormalizeDirectoryPath(GetString(payload, "fixtureRoot"));
            string fixtureId = SafeName(GetString(payload, "fixtureId"));
            if (!File.Exists(templatePath)) throw new ModelDeliveryException("TEMPLATE_NOT_FOUND", "Revit template was not found: " + templatePath);
            if (Directory.Exists(fixtureRoot)) throw new ModelDeliveryException("FIXTURE_ROOT_EXISTS", "Fixture root already exists and will not be overwritten: " + fixtureRoot);

            Directory.CreateDirectory(fixtureRoot);
            File.WriteAllText(Path.Combine(fixtureRoot, OwnershipFileName), "{\"fixtureId\":\"" + fixtureId + "\"}");
            string sourceRoot = Path.Combine(fixtureRoot, "sources");
            string deliveryRoot = Path.Combine(fixtureRoot, "delivery");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(deliveryRoot);

            string version = uiApplication.Application.VersionNumber;
            string modelBPath = Path.Combine(sourceRoot, fixtureId + "-Structure-Source-" + version + ".rvt");
            string modelAPath = Path.Combine(sourceRoot, fixtureId + "-Architecture-Source-" + version + ".rvt");
            CreateCentralModel(uiApplication.Application, templatePath, modelBPath, null, out _);
            CreateCentralModel(uiApplication.Application, templatePath, modelAPath, modelBPath, out string expectedTransform);

            string targetA = "Issued-Architecture-" + version + ".rvt";
            string targetB = "Issued-Structure-" + version + ".rvt";
            string packageName = fixtureId + "-package";
            string deliveryId = fixtureId + "-run";
            var recipe = new Dictionary<string, object>
            {
                ["projectId"] = fixtureId,
                ["recipeVersion"] = "1",
                ["deliveryId"] = deliveryId,
                ["packageName"] = packageName,
                ["destinationRoot"] = deliveryRoot,
                ["sourceModels"] = new object[]
                {
                    Source("architecture", modelAPath, targetA, "Architecture"),
                    Source("structure", modelBPath, targetB, "Structure")
                },
                ["linkRules"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["sourceModelId"] = "architecture",
                        ["matchFileName"] = Path.GetFileName(modelBPath),
                        ["action"] = "repath",
                        ["targetModelId"] = "structure",
                        ["expectedInstanceCount"] = 1,
                        ["required"] = true
                    }
                },
                ["coordinates"] = new Dictionary<string, object>
                {
                    ["preserveLinkTransforms"] = true,
                    ["packagedLinkPathType"] = "relative"
                },
                ["cleanup"] = new Dictionary<string, object>
                {
                    ["deleteSheets"] = true,
                    ["deleteViews"] = true,
                    ["deleteSchedules"] = true,
                    ["deleteLegends"] = true,
                    ["deleteDraftingViews"] = true,
                    ["deleteViewTemplates"] = true,
                    ["deleteUnusedFilters"] = true,
                    ["removeUnmappedLinks"] = true,
                    ["purgeUnusedPasses"] = 1,
                    ["protectedViewNames"] = new[] { "START", "START 3D" }
                },
                ["exports"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["format"] = "dwg",
                        ["modelIds"] = new[] { "architecture", "structure" },
                        ["outputSubdirectory"] = "DWG",
                        ["viewNames"] = new[] { "START" },
                        ["required"] = true
                    }
                },
                ["qa"] = new Dictionary<string, object>
                {
                    ["requireStandalone"] = true,
                    ["requireNoCentralPath"] = true,
                    ["requireSourceHashUnchanged"] = true,
                    ["requireCleanupMatchesPreview"] = true,
                    ["requireAllRequiredExports"] = true
                }
            };

            return new Dictionary<string, object>
            {
                ["fixtureId"] = fixtureId,
                ["fixtureRoot"] = fixtureRoot,
                ["revitVersion"] = version,
                ["templatePath"] = templatePath,
                ["sourcePaths"] = new[] { modelAPath, modelBPath },
                ["sourceSha256"] = new Dictionary<string, object>
                {
                    ["architecture"] = Sha256(modelAPath),
                    ["structure"] = Sha256(modelBPath)
                },
                ["expectedArchitectureLinkTransform"] = expectedTransform,
                ["recipe"] = recipe
            };
        }

        private static Dictionary<string, object> Source(string id, string path, string target, string role)
        {
            return new Dictionary<string, object>
            {
                ["id"] = id,
                ["sourcePath"] = path,
                ["targetFileName"] = target,
                ["role"] = role,
                ["startViewName"] = "START",
                ["start3dViewName"] = "START 3D"
            };
        }

        private static void CreateCentralModel(
            Autodesk.Revit.ApplicationServices.Application application,
            string templatePath,
            string outputPath,
            string linkedModelPath,
            out string linkTransform)
        {
            linkTransform = null;
            Document document = application.NewProjectDocument(templatePath);
            try
            {
                RevitLinkInstance linkInstance = null;
                using (var transaction = new Transaction(document, "Create model-delivery fixture content"))
                {
                    transaction.Start();
                    Level level = Level.Create(document, UnitUtils.ConvertToInternalUnits(1234, UnitTypeId.Millimeters));
                    level.Name = "MCP DELIVERY LEVEL";
                    ViewFamilyType floorPlanType = FindViewFamilyType(document, ViewFamily.FloorPlan);
                    ViewFamilyType threeDType = FindViewFamilyType(document, ViewFamily.ThreeDimensional);
                    ViewFamilyType draftingType = FindViewFamilyType(document, ViewFamily.Drafting);
                    ViewPlan start = ViewPlan.Create(document, floorPlanType.Id, level.Id);
                    start.Name = "START";
                    View3D start3d = View3D.CreateIsometric(document, threeDType.Id);
                    start3d.Name = "START 3D";
                    ViewDrafting temporaryDrafting = ViewDrafting.Create(document, draftingType.Id);
                    temporaryDrafting.Name = "TEMP DELIVERY DRAFTING";
                    ViewSheet sheet = ViewSheet.Create(document, ElementId.InvalidElementId);
                    sheet.SheetNumber = "MCP-DELIVERY-DELETE";
                    sheet.Name = "TEMP DELIVERY SHEET";
                    ViewSchedule schedule = ViewSchedule.CreateSchedule(document, Category.GetCategory(document, BuiltInCategory.OST_Walls).Id);
                    schedule.Name = "TEMP DELIVERY SCHEDULE";
                    View template = start.CreateViewTemplate();
                    template.Name = "TEMP DELIVERY TEMPLATE";
                    SelectionFilterElement filter = SelectionFilterElement.Create(document, "TEMP DELIVERY FILTER");
                    filter.SetElementIds(new List<ElementId>());
                    Wall.Create(document, Line.CreateBound(XYZ.Zero, new XYZ(30, 0, 0)), level.Id, false);

                    if (!string.IsNullOrWhiteSpace(linkedModelPath))
                    {
                        LinkLoadResult linkTypeResult = RevitLinkType.Create(
                            document,
                            ModelPathUtils.ConvertUserVisiblePathToModelPath(linkedModelPath),
                            new RevitLinkOptions(false));
                        linkInstance = RevitLinkInstance.Create(document, linkTypeResult.ElementId);
                        ElementTransformUtils.MoveElement(document, linkInstance.Id, new XYZ(10, 20, 3));
                        ElementTransformUtils.RotateElement(document, linkInstance.Id, Line.CreateBound(XYZ.Zero, XYZ.BasisZ), Math.PI / 6.0);
                    }
                    transaction.Commit();
                }

                if (linkInstance != null) linkTransform = TransformSignature(linkInstance.GetTotalTransform());
                document.EnableWorksharing("Shared Levels and Grids", "MCP Delivery Workset");
                var saveOptions = new SaveAsOptions { Compact = true, OverwriteExistingFile = false, MaximumBackups = 1 };
                var worksharing = new WorksharingSaveAsOptions { SaveAsCentral = true };
                saveOptions.SetWorksharingOptions(worksharing);
                document.SaveAs(outputPath, saveOptions);
                if (linkInstance != null)
                {
                    (document.GetElement(linkInstance.GetTypeId()) as RevitLinkType)?.Unload(null);
                }
            }
            finally
            {
                document.Close(false);
            }
        }

        private static ViewFamilyType FindViewFamilyType(Document document, ViewFamily family)
        {
            ViewFamilyType type = new FilteredElementCollector(document)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(candidate => candidate.ViewFamily == family);
            if (type == null) throw new ModelDeliveryException("FIXTURE_VIEW_TYPE_MISSING", "Template does not provide a " + family + " view family type.");
            return type;
        }

        private static string TransformSignature(Transform transform)
        {
            double[] values =
            {
                transform.Origin.X, transform.Origin.Y, transform.Origin.Z,
                transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z,
                transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z,
                transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z
            };
            return string.Join(",", values.Select(value => Math.Round(value, 9).ToString("R", CultureInfo.InvariantCulture)));
        }

        private static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 hash = SHA256.Create())
            {
                return string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static string NormalizeFilePath(string value, string extension)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)) throw new ModelDeliveryException("ABSOLUTE_PATH_REQUIRED", "Fixture paths must be absolute.");
            string path = Path.GetFullPath(value.Trim());
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) throw new ModelDeliveryException("INVALID_FIXTURE_PATH", "Expected a " + extension + " path: " + path);
            return path;
        }

        private static string NormalizeDirectoryPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)) throw new ModelDeliveryException("ABSOLUTE_PATH_REQUIRED", "fixtureRoot must be absolute.");
            return Path.GetFullPath(value.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains("\\") || value.Contains("/"))
            {
                throw new ModelDeliveryException("INVALID_FIXTURE_ID", "fixtureId must be a non-empty file-name-safe value.");
            }
            return value.Trim();
        }

        private static string GetString(Dictionary<string, object> payload, string key)
        {
            return payload != null && payload.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static bool GetBool(Dictionary<string, object> payload, string key)
        {
            return payload != null && payload.TryGetValue(key, out object value) && value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
    }
}
