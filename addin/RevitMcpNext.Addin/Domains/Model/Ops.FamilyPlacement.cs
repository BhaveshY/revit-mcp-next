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
        private static Dictionary<string, object> PreviewPlaceFamilyInstance(Document document, Dictionary<string, object> operation, int index)
        {
            FamilyPlacementRequest placement;
            string error;
            if (!TryBuildFamilyPlacementRequest(document, operation, out placement, out error))
            {
                return BlockedChange(operation, index, error);
            }

            return Change(operation, index, "ready",
                target: FamilyPlacementTarget(document, placement),
                before: null,
                after: FamilyPlacementPreviewSnapshot(placement));
        }

        private static Dictionary<string, object> ApplyPlaceFamilyInstance(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewPlaceFamilyInstance(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "place_family_instance preview failed.");
            }

            FamilyPlacementRequest placement;
            string error;
            if (!TryBuildFamilyPlacementRequest(document, operation, out placement, out error))
            {
                throw new InvalidOperationException(error ?? "place_family_instance validation failed.");
            }

            bool activatedSymbol = false;
            if (!placement.Symbol.IsActive)
            {
                placement.Symbol.Activate();
                document.Regenerate();
                activatedSymbol = true;
            }

            FamilyInstance instance;
            if (string.Equals(placement.PlacementMode, "wallHosted", StringComparison.OrdinalIgnoreCase))
            {
                instance = placement.Level == null
                    ? document.Create.NewFamilyInstance(placement.Location, placement.Symbol, placement.Host, StructuralType.NonStructural)
                    : document.Create.NewFamilyInstance(placement.Location, placement.Symbol, placement.Host, placement.Level, StructuralType.NonStructural);
            }
            else
            {
                instance = document.Create.NewFamilyInstance(placement.Location, placement.Symbol, placement.Level, StructuralType.NonStructural);
            }

            if (instance == null)
            {
                throw new InvalidOperationException("Revit did not create a family instance for place_family_instance.");
            }

            ApplyFamilyInstancePostPlacementTransforms(document, instance, placement);
            document.Regenerate();
            FamilyInstance created = document.GetElement(instance.Id) as FamilyInstance ?? instance;
            Dictionary<string, object> snapshot = FamilyInstanceSnapshot(created);
            snapshot["activatedSymbol"] = activatedSymbol;
            snapshot["placementMode"] = placement.PlacementMode;

            return Change(operation, index, "applied",
                target: ElementTarget(created, null),
                before: null,
                after: snapshot);
        }

        private static bool TryBuildFamilyPlacementRequest(
            Document document,
            Dictionary<string, object> operation,
            out FamilyPlacementRequest placement,
            out string error)
        {
            placement = null;
            error = null;

            string familySymbolId = GetString(operation, "familySymbolId");
            if (string.IsNullOrWhiteSpace(familySymbolId))
            {
                error = "place_family_instance requires familySymbolId.";
                return false;
            }

            FamilySymbol symbol = ResolveElement(document, familySymbolId) as FamilySymbol;
            if (symbol == null)
            {
                error = "FamilySymbol " + familySymbolId + " was not found.";
                return false;
            }

            if (symbol.Family == null)
            {
                error = "FamilySymbol " + familySymbolId + " does not expose a parent family.";
                return false;
            }

            Dictionary<string, object> locationValue = GetDictionary(operation, "location");
            if (locationValue == null)
            {
                error = "place_family_instance requires location.";
                return false;
            }

            string hostElementId = GetString(operation, "hostElementId");
            string levelId = GetString(operation, "levelId");
            bool hasHost = !string.IsNullOrWhiteSpace(hostElementId);
            bool allowPinnedHost = GetBool(operation, "allowPinnedHost", false);
            bool flipFacing = GetBool(operation, "flipFacing", false) || GetBool(operation, "facingFlipped", false);
            bool flipHand = GetBool(operation, "flipHand", false) || GetBool(operation, "handFlipped", false);

            double rotationRadians = 0;
            bool hasRotation = false;
            Dictionary<string, object> rotationValue = GetDictionary(operation, "rotation") ?? GetDictionary(operation, "angle");
            if (rotationValue != null)
            {
                try
                {
                    rotationRadians = ToInternalAngle(rotationValue);
                    if (!IsFinite(rotationRadians))
                    {
                        error = "place_family_instance rotation must be a finite angle.";
                        return false;
                    }

                    hasRotation = true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            FamilyPlacementType placementType = symbol.Family.FamilyPlacementType;
            if (IsWallHostedDoorWindowCategory(symbol))
            {
                if (placementType != FamilyPlacementType.OneLevelBasedHosted)
                {
                    error = "FamilySymbol " + familySymbolId + " is a door/window symbol but has unsupported placementType " + placementType + ". Expected OneLevelBasedHosted.";
                    return false;
                }

                if (!hasHost)
                {
                    error = "place_family_instance requires hostElementId for wall-hosted door/window symbols.";
                    return false;
                }

                Element host = ResolveElement(document, hostElementId);
                Wall hostWall = host as Wall;
                if (hostWall == null)
                {
                    error = "hostElementId " + hostElementId + " must reference a Wall for wall-hosted door/window placement.";
                    return false;
                }

                error = ValidateExpectedUniqueId(operation, hostWall, hostElementId, "expectedHostUniqueId", "Host element");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return false;
                }

                string hostError = ValidateWallFamilyInstanceHost(document, hostWall);
                if (!string.IsNullOrWhiteSpace(hostError))
                {
                    error = hostError;
                    return false;
                }

                if (hostWall.Pinned && !allowPinnedHost)
                {
                    error = "Host wall " + hostElementId + " is pinned. Pass allowPinnedHost=true only after explicitly reviewing the host modification.";
                    return false;
                }

                Level level = null;
                string levelSource = null;
                if (!string.IsNullOrWhiteSpace(levelId))
                {
                    level = ResolveElement(document, levelId) as Level;
                    if (level == null)
                    {
                        error = "Level " + levelId + " was not found.";
                        return false;
                    }

                    levelSource = "explicit";
                }
                else
                {
                    level = ResolveHostLevel(document, hostWall);
                    levelSource = level == null ? null : "host";
                }

                XYZ location;
                try
                {
                    location = ToInternalPlacementPoint(locationValue, "location", level?.Elevation);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }

                placement = new FamilyPlacementRequest(
                    "wallHosted",
                    symbol,
                    placementType,
                    hostWall,
                    level,
                    levelSource,
                    location,
                    hasRotation,
                    rotationRadians,
                    flipFacing,
                    flipHand,
                    allowPinnedHost);
                return true;
            }

            if (IsLevelBasedFurnitureEquipmentFixtureCategory(symbol))
            {
                if (!string.IsNullOrWhiteSpace(GetString(operation, "expectedHostUniqueId")))
                {
                    error = "expectedHostUniqueId requires hostElementId and is only supported for hosted place_family_instance operations.";
                    return false;
                }

                if (placementType != FamilyPlacementType.OneLevelBased)
                {
                    error = "FamilySymbol " + familySymbolId + " has placementType " + placementType + ". Level-based furniture/equipment/fixture placement requires OneLevelBased.";
                    return false;
                }

                if (hasHost)
                {
                    error = "hostElementId is only supported for wall-hosted door/window placement in place_family_instance.";
                    return false;
                }

                if (flipFacing || flipHand)
                {
                    error = "flipFacing and flipHand are only supported for wall-hosted door/window placement.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(levelId))
                {
                    error = "place_family_instance requires levelId for level-based furniture/equipment/fixture symbols.";
                    return false;
                }

                Level level = ResolveElement(document, levelId) as Level;
                if (level == null)
                {
                    error = "Level " + levelId + " was not found.";
                    return false;
                }

                XYZ location;
                try
                {
                    location = ToInternalPlacementPoint(locationValue, "location", level.Elevation);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }

                placement = new FamilyPlacementRequest(
                    "levelBased",
                    symbol,
                    placementType,
                    null,
                    level,
                    "explicit",
                    location,
                    hasRotation,
                    rotationRadians,
                    false,
                    false,
                    allowPinnedHost);
                return true;
            }

            string categoryName = symbol.Category == null ? "(none)" : symbol.Category.Name;
            error = "FamilySymbol " + familySymbolId + " category '" + categoryName + "' is not supported by place_family_instance. First supported cases are wall-hosted doors/windows and level-based furniture/equipment/fixtures.";
            return false;
        }

        private static void ApplyFamilyInstancePostPlacementTransforms(Document document, FamilyInstance instance, FamilyPlacementRequest placement)
        {
            if (placement.HasRotation && Math.Abs(placement.RotationRadians) > 0.000000001)
            {
                Line axis = Line.CreateBound(placement.Location, placement.Location + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(document, instance.Id, axis, placement.RotationRadians);
                instance = document.GetElement(instance.Id) as FamilyInstance ?? instance;
            }

            if (placement.FlipFacing)
            {
                if (!instance.CanFlipFacing)
                {
                    throw new InvalidOperationException("Created family instance does not support flipFacing.");
                }

                instance.flipFacing();
            }

            if (placement.FlipHand)
            {
                if (!instance.CanFlipHand)
                {
                    throw new InvalidOperationException("Created family instance does not support flipHand.");
                }

                instance.flipHand();
            }
        }

        private static Dictionary<string, object> FamilyPlacementTarget(Document document, FamilyPlacementRequest placement)
        {
            var target = new Dictionary<string, object>
            {
                ["document"] = document.Title,
                ["placementMode"] = placement.PlacementMode,
                ["familySymbol"] = ElementSummary(document, placement.Symbol)
            };

            if (placement.Level != null)
            {
                target["level"] = BuildLevelSummary(placement.Level);
                if (!string.IsNullOrWhiteSpace(placement.LevelSource)) target["levelSource"] = placement.LevelSource;
            }

            if (placement.Host != null)
            {
                Dictionary<string, object> host = ElementSummary(document, placement.Host);
                host["pinned"] = placement.Host.Pinned;
                target["host"] = host;
            }

            return target;
        }

        private static Dictionary<string, object> FamilyPlacementPreviewSnapshot(FamilyPlacementRequest placement)
        {
            var after = new Dictionary<string, object>
            {
                ["familySymbolId"] = ToElementIdString(placement.Symbol.Id),
                ["familySymbolName"] = SafeElementName(placement.Symbol),
                ["familyName"] = GetFamilyName(placement.Symbol),
                ["placementType"] = placement.PlacementType.ToString(),
                ["placementMode"] = placement.PlacementMode,
                ["location"] = PointValue(placement.Location),
                ["activationRequired"] = !placement.Symbol.IsActive,
                ["structuralType"] = StructuralType.NonStructural.ToString()
            };

            string builtInCategory = GetBuiltInCategoryName(placement.Symbol);
            if (!string.IsNullOrWhiteSpace(builtInCategory)) after["builtInCategory"] = builtInCategory;
            if (placement.Symbol.Category != null) after["category"] = placement.Symbol.Category.Name;

            if (placement.Level != null)
            {
                after["levelId"] = ToElementIdString(placement.Level.Id);
                after["levelName"] = placement.Level.Name;
                if (!string.IsNullOrWhiteSpace(placement.LevelSource)) after["levelSource"] = placement.LevelSource;
            }

            if (placement.Host != null)
            {
                after["hostElementId"] = ToElementIdString(placement.Host.Id);
                after["hostName"] = SafeElementName(placement.Host);
                after["hostPinned"] = placement.Host.Pinned;
                after["allowPinnedHost"] = placement.AllowPinnedHost;
            }

            if (placement.HasRotation) after["rotation"] = AngleValue(placement.RotationRadians);
            if (placement.FlipFacing) after["flipFacing"] = true;
            if (placement.FlipHand) after["flipHand"] = true;

            return after;
        }

        private sealed class FamilyPlacementRequest
        {
            public FamilyPlacementRequest(
                string placementMode,
                FamilySymbol symbol,
                FamilyPlacementType placementType,
                Element host,
                Level level,
                string levelSource,
                XYZ location,
                bool hasRotation,
                double rotationRadians,
                bool flipFacing,
                bool flipHand,
                bool allowPinnedHost)
            {
                PlacementMode = placementMode;
                Symbol = symbol;
                PlacementType = placementType;
                Host = host;
                Level = level;
                LevelSource = levelSource;
                Location = location;
                HasRotation = hasRotation;
                RotationRadians = rotationRadians;
                FlipFacing = flipFacing;
                FlipHand = flipHand;
                AllowPinnedHost = allowPinnedHost;
            }

            public string PlacementMode { get; }
            public FamilySymbol Symbol { get; }
            public FamilyPlacementType PlacementType { get; }
            public Element Host { get; }
            public Level Level { get; }
            public string LevelSource { get; }
            public XYZ Location { get; }
            public bool HasRotation { get; }
            public double RotationRadians { get; }
            public bool FlipFacing { get; }
            public bool FlipHand { get; }
            public bool AllowPinnedHost { get; }
        }

        private static Level ResolveHostLevel(Document document, Element host)
        {
            if (host == null) return null;

            ElementId levelId = GetLevelId(host);
            if (IsValidElementId(levelId))
            {
                Level level = document.GetElement(levelId) as Level;
                if (level != null) return level;
            }

            try
            {
                Parameter baseConstraint = host.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                ElementId baseLevelId = baseConstraint?.AsElementId();
                if (IsValidElementId(baseLevelId))
                {
                    return document.GetElement(baseLevelId) as Level;
                }
            }
            catch
            {
                // Non-wall hosts or unusual wall variants may not expose a base constraint.
            }

            return null;
        }

        private static string GetPlacementType(Element element)
        {
            FamilySymbol symbol = element as FamilySymbol;
            return symbol?.Family?.FamilyPlacementType.ToString() ?? string.Empty;
        }

        private static bool IsSupportedWallHostedFamilySymbol(FamilySymbol symbol)
        {
            return symbol != null &&
                   symbol.Family != null &&
                   symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted &&
                   IsWallHostedDoorWindowCategory(symbol);
        }

        private static bool IsSupportedLevelBasedFamilySymbol(FamilySymbol symbol)
        {
            return symbol != null &&
                   symbol.Family != null &&
                   symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased &&
                   IsLevelBasedFurnitureEquipmentFixtureCategory(symbol);
        }

        private static bool IsWallHostedDoorWindowCategory(FamilySymbol symbol)
        {
            return IsBuiltInCategory(symbol, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows);
        }

        private static bool IsLevelBasedFurnitureEquipmentFixtureCategory(FamilySymbol symbol)
        {
            return IsBuiltInCategory(
                symbol,
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_FurnitureSystems,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_SpecialityEquipment);
        }
    }
}
