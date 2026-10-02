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
        private static Dictionary<string, object> ElementSummary(Document document, Element element)
        {
            var summary = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(element.Id),
                ["uniqueId"] = element.UniqueId,
                ["class"] = element.GetType().Name,
                ["name"] = SafeElementName(element)
            };

            if (element.Category != null) summary["category"] = element.Category.Name;

            ElementId typeId = element.GetTypeId();
            if (IsValidElementId(typeId)) summary["typeId"] = ToElementIdString(typeId);

            ElementId levelId = GetLevelId(element);
            if (IsValidElementId(levelId)) summary["levelId"] = ToElementIdString(levelId);

            ElementType elementType = element as ElementType;
            if (elementType != null && !string.IsNullOrWhiteSpace(elementType.FamilyName))
            {
                summary["familyName"] = elementType.FamilyName;
            }

            return summary;
        }

        private static Dictionary<string, object> ElementTypeSnapshot(
            ElementType elementType,
            string proposedName = null,
            bool includeIdentity = true)
        {
            if (elementType == null)
            {
                return new Dictionary<string, object>
                {
                    ["available"] = false
                };
            }

            string typeName = proposedName ?? SafeElementName(elementType);
            var snapshot = new Dictionary<string, object>
            {
                ["class"] = elementType.GetType().Name,
                ["name"] = typeName,
                ["typeName"] = typeName,
                ["familyName"] = GetFamilyName(elementType) ?? string.Empty,
                ["canBeRenamed"] = elementType.CanBeRenamed,
                ["canBeCopied"] = elementType.CanBeCopied
            };

            if (includeIdentity)
            {
                string elementTypeId = ToElementIdString(elementType.Id);
                snapshot["id"] = elementTypeId;
                snapshot["elementTypeId"] = elementTypeId;
                snapshot["uniqueId"] = elementType.UniqueId;
            }

            if (elementType.Category != null) snapshot["category"] = elementType.Category.Name;

            string familyId = GetFamilyIdString(elementType);
            if (!string.IsNullOrWhiteSpace(familyId)) snapshot["familyId"] = familyId;

            return snapshot;
        }

        private static Dictionary<string, object> TypeSnapshot(Document document, Element element)
        {
            ElementType elementType = element as ElementType;
            if (elementType != null) return ElementSummary(document, elementType);

            ElementId typeId = element?.GetTypeId();
            if (!IsValidElementId(typeId))
            {
                return new Dictionary<string, object>
                {
                    ["available"] = false
                };
            }

            ElementType resolvedType = document.GetElement(typeId) as ElementType;
            if (resolvedType == null)
            {
                return new Dictionary<string, object>
                {
                    ["typeId"] = ToElementIdString(typeId),
                    ["available"] = false
                };
            }

            return ElementSummary(document, resolvedType);
        }

        private static bool IsValidTypeForElement(Element element, ElementId typeId)
        {
            try
            {
                ICollection<ElementId> validTypeIds = element.GetValidTypes();
                if (validTypeIds == null || validTypeIds.Count == 0) return true;
                return validTypeIds.Any(candidate => string.Equals(ToElementIdString(candidate), ToElementIdString(typeId), StringComparison.Ordinal));
            }
            catch
            {
                return true;
            }
        }

        private static Dictionary<string, object> WallSnapshot(Wall wall)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(wall.Id),
                ["uniqueId"] = wall.UniqueId,
                ["name"] = SafeElementName(wall),
                ["typeId"] = ToElementIdString(wall.GetTypeId()),
                ["levelId"] = ToElementIdString(GetLevelId(wall)),
                ["flipped"] = wall.Flipped
            };

            Parameter height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (height != null && height.StorageType == StorageType.Double) snapshot["height"] = LengthValue(height.AsDouble());

            Parameter baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            if (baseOffset != null && baseOffset.StorageType == StorageType.Double) snapshot["baseOffset"] = LengthValue(baseOffset.AsDouble());

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve?.Curve != null && locationCurve.Curve.IsBound)
            {
                snapshot["start"] = PointValue(locationCurve.Curve.GetEndPoint(0));
                snapshot["end"] = PointValue(locationCurve.Curve.GetEndPoint(1));
                snapshot["length"] = LengthValue(locationCurve.Curve.Length);
            }

            return snapshot;
        }

        private static Dictionary<string, object> GridSnapshot(Grid grid)
        {
            var snapshot = ElementSummary(grid.Document, grid);
            Curve curve = grid.Curve;
            if (curve != null && curve.IsBound)
            {
                snapshot["start"] = PointValue(curve.GetEndPoint(0));
                snapshot["end"] = PointValue(curve.GetEndPoint(1));
                snapshot["length"] = LengthValue(curve.Length);
            }

            return snapshot;
        }

        private static Dictionary<string, object> FloorSnapshot(Floor floor, IReadOnlyList<XYZ> outline)
        {
            var snapshot = ElementSummary(floor.Document, floor);
            ElementId levelId = GetLevelId(floor);
            if (IsValidElementId(levelId)) snapshot["levelId"] = ToElementIdString(levelId);
            if (outline != null && outline.Count > 0)
            {
                snapshot["outline"] = PointArrayValue(outline);
                snapshot["area"] = AreaValue(PolygonAreaInternal(outline));
            }

            Parameter structural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
            if (structural != null && structural.StorageType == StorageType.Integer)
            {
                snapshot["structural"] = structural.AsInteger() != 0;
            }

            return snapshot;
        }

        private static Dictionary<string, object> RoomSnapshot(Room room)
        {
            var snapshot = ElementSummary(room.Document, room);
            string number = GetRoomNumber(room);
            if (!string.IsNullOrWhiteSpace(number)) snapshot["number"] = number;
            snapshot["name"] = GetRoomName(room);

            ElementId levelId = GetLevelId(room);
            if (IsValidElementId(levelId))
            {
                snapshot["levelId"] = ToElementIdString(levelId);
                Element level = room.Document.GetElement(levelId);
                if (level != null) snapshot["levelName"] = SafeElementName(level);
            }

            ElementId phaseId = GetCreatedPhaseId(room);
            if (IsValidElementId(phaseId))
            {
                snapshot["phaseId"] = ToElementIdString(phaseId);
                Element phase = room.Document.GetElement(phaseId);
                if (phase != null) snapshot["phaseName"] = SafeElementName(phase);
            }

            double area = SafeRoomArea(room);
            double volume = SafeRoomVolume(room);
            if (area > 0) snapshot["area"] = AreaValue(area);
            if (volume > 0) snapshot["volume"] = VolumeValue(volume);

            Parameter perimeter = room.get_Parameter(BuiltInParameter.ROOM_PERIMETER);
            if (perimeter != null && perimeter.StorageType == StorageType.Double)
            {
                double perimeterValue = perimeter.AsDouble();
                if (perimeterValue > 0) snapshot["perimeter"] = LengthValue(perimeterValue);
            }

            LocationPoint point = room.Location as LocationPoint;
            if (point != null) snapshot["location"] = PointValue(point.Point);

            snapshot["isPlaced"] = IsRoomPlaced(room);
            snapshot["isEnclosed"] = area > 0;

            string department = GetRoomDepartment(room);
            if (!string.IsNullOrWhiteSpace(department)) snapshot["department"] = department;

            return snapshot;
        }

        private static Dictionary<string, object> FamilyInstanceSnapshot(FamilyInstance instance)
        {
            var snapshot = ElementSummary(instance.Document, instance);
            snapshot["pinned"] = instance.Pinned;

            FamilySymbol symbol = instance.Symbol;
            if (symbol != null)
            {
                snapshot["familySymbolId"] = ToElementIdString(symbol.Id);
                snapshot["familySymbolName"] = SafeElementName(symbol);
                snapshot["familyName"] = GetFamilyName(symbol);
                string placementType = GetPlacementType(symbol);
                if (!string.IsNullOrWhiteSpace(placementType)) snapshot["placementType"] = placementType;
                snapshot["symbolIsActive"] = symbol.IsActive;

                string builtInCategory = GetBuiltInCategoryName(symbol);
                if (!string.IsNullOrWhiteSpace(builtInCategory)) snapshot["builtInCategory"] = builtInCategory;
            }

            ElementId levelId = GetLevelId(instance);
            if (IsValidElementId(levelId))
            {
                snapshot["levelId"] = ToElementIdString(levelId);
                Element level = instance.Document.GetElement(levelId);
                if (level != null) snapshot["levelName"] = SafeElementName(level);
            }

            Element host = null;
            try
            {
                host = instance.Host;
            }
            catch
            {
                host = null;
            }

            if (host != null)
            {
                snapshot["hostElementId"] = ToElementIdString(host.Id);
                snapshot["hostCategory"] = host.Category?.Name;
                snapshot["host"] = ElementSummary(instance.Document, host);
            }

            Dictionary<string, object> location = LocationSnapshot(instance);
            if (location != null) snapshot["location"] = location;

            TryAddFamilyInstanceBool(snapshot, instance, "CanFlipFacing");
            TryAddFamilyInstanceBool(snapshot, instance, "FacingFlipped");
            TryAddFamilyInstanceBool(snapshot, instance, "CanFlipHand");
            TryAddFamilyInstanceBool(snapshot, instance, "HandFlipped");

            try
            {
                snapshot["facingOrientation"] = PointValue(instance.FacingOrientation);
            }
            catch
            {
                // Optional family instance metadata varies by placement type.
            }

            try
            {
                snapshot["handOrientation"] = PointValue(instance.HandOrientation);
            }
            catch
            {
                // Optional family instance metadata varies by placement type.
            }

            return snapshot;
        }

        private static void TryAddFamilyInstanceBool(Dictionary<string, object> snapshot, FamilyInstance instance, string propertyName)
        {
            try
            {
                System.Reflection.PropertyInfo property = instance.GetType().GetProperty(propertyName);
                if (property != null && property.GetValue(instance, null) is bool value)
                {
                    snapshot[char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1)] = value;
                }
            }
            catch
            {
                // Optional family instance metadata varies by placement type.
            }
        }

        private static Dictionary<string, object> DeleteSnapshot(Document document, Element element)
        {
            var snapshot = ElementSummary(document, element);
            snapshot["pinned"] = element.Pinned;
            snapshot["viewSpecific"] = element.ViewSpecific;
            return snapshot;
        }

        private static Dictionary<string, object> LocationSnapshot(Element element)
        {
            if (element == null) return null;

            LocationPoint point = element.Location as LocationPoint;
            if (point != null)
            {
                var result = new Dictionary<string, object>
                {
                    ["point"] = PointValue(point.Point)
                };
                try { result["rotation"] = Math.Round(point.Rotation, 6); }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) { result["rotationUnavailableReason"] = ex.Message; }
                return result;
            }

            LocationCurve curve = element.Location as LocationCurve;
            if (curve?.Curve != null && curve.Curve.IsBound)
            {
                return new Dictionary<string, object>
                {
                    ["start"] = PointValue(curve.Curve.GetEndPoint(0)),
                    ["end"] = PointValue(curve.Curve.GetEndPoint(1)),
                    ["length"] = LengthValue(curve.Curve.Length)
                };
            }

            return BoundsSnapshot(element) ?? UnavailableGeometrySnapshot();
        }

        private static Dictionary<string, object> BoundsSnapshot(Element element)
        {
            if (element == null) return null;

            try
            {
                return BoundsValue(element.get_BoundingBox(null));
            }
            catch
            {
                return UnavailableGeometrySnapshot();
            }
        }

        private static Dictionary<string, object> BoundsValue(BoundingBoxXYZ boundingBox)
        {
            if (boundingBox?.Min == null || boundingBox.Max == null) return UnavailableGeometrySnapshot();

            XYZ min = boundingBox.Min;
            XYZ max = boundingBox.Max;
            Transform transform = boundingBox.Transform;
            XYZ[] corners =
            {
                TransformBoundingBoxPoint(transform, new XYZ(min.X, min.Y, min.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(max.X, min.Y, min.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(min.X, max.Y, min.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(min.X, min.Y, max.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(max.X, max.Y, min.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(max.X, min.Y, max.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(min.X, max.Y, max.Z)),
                TransformBoundingBoxPoint(transform, new XYZ(max.X, max.Y, max.Z))
            };

            return new Dictionary<string, object>
            {
                ["min"] = PointValue(new XYZ(corners.Min(point => point.X), corners.Min(point => point.Y), corners.Min(point => point.Z))),
                ["max"] = PointValue(new XYZ(corners.Max(point => point.X), corners.Max(point => point.Y), corners.Max(point => point.Z)))
            };
        }

        private static XYZ TransformBoundingBoxPoint(Transform transform, XYZ point)
        {
            return transform == null ? point : transform.OfPoint(point);
        }

        private static Dictionary<string, object> UnavailableGeometrySnapshot()
        {
            return new Dictionary<string, object>
            {
                ["available"] = false
            };
        }

        private static string SafeElementName(Element element)
        {
            try
            {
                return element.Name;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
