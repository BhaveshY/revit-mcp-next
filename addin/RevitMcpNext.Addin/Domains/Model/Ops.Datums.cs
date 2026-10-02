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
        private static Dictionary<string, object> PreviewCreateLevel(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string name = GetString(operation, "name");
            Dictionary<string, object> elevation = GetDictionary(operation, "elevation");
            if (string.IsNullOrWhiteSpace(name)) return BlockedChange(operation, index, "create_level requires name.");
            if (elevation == null) return BlockedChange(operation, index, "create_level requires elevation.");
            if (LevelNameExists(document, name)) return BlockedChange(operation, index, "A level named '" + name + "' already exists.");
            if (validationContext != null && !validationContext.TryAddLevelName(name))
            {
                return BlockedChange(operation, index, "The change set creates duplicate level name '" + name + "'.");
            }

            double internalElevation;
            try
            {
                internalElevation = ToInternalElevation(elevation);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            return Change(operation, index, "ready",
                target: new Dictionary<string, object> { ["document"] = document.Title },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(internalElevation, UnitTypeId.Millimeters), "mm", "metric")
                });
        }

        private static Dictionary<string, object> ApplyCreateLevel(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateLevel(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_level preview failed.");
            }

            string name = GetString(operation, "name");
            double internalElevation = ToInternalElevation(GetDictionary(operation, "elevation"));
            Level level = Level.Create(document, internalElevation);
            level.Name = name;

            return Change(operation, index, "applied",
                target: ElementTarget(level, null),
                before: null,
                after: new Dictionary<string, object>
                {
                    ["id"] = ToElementIdString(level.Id),
                    ["uniqueId"] = level.UniqueId,
                    ["name"] = level.Name,
                    ["elevation"] = UnitValue(UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters), "mm", "metric")
                });
        }

        private static Dictionary<string, object> PreviewCreateGrid(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            Dictionary<string, object> startValue = GetDictionary(operation, "start");
            Dictionary<string, object> endValue = GetDictionary(operation, "end");
            if (startValue == null) return BlockedChange(operation, index, "create_grid requires start.");
            if (endValue == null) return BlockedChange(operation, index, "create_grid requires end.");

            string name = GetString(operation, "name");
            if (!string.IsNullOrWhiteSpace(name) && GridNameExists(document, name))
            {
                return BlockedChange(operation, index, "A grid named '" + name + "' already exists.");
            }
            if (!string.IsNullOrWhiteSpace(name) && validationContext != null && !validationContext.TryAddGridName(name))
            {
                return BlockedChange(operation, index, "The change set creates duplicate grid name '" + name + "'.");
            }

            XYZ start;
            XYZ end;
            try
            {
                start = ToInternalPoint(startValue, "start");
                end = ToInternalPoint(endValue, "end");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            string geometryError = ValidateLinearDatum(document, start, end, "create_grid");
            if (!string.IsNullOrWhiteSpace(geometryError)) return BlockedChange(operation, index, geometryError);

            var after = new Dictionary<string, object>
            {
                ["start"] = PointValue(start),
                ["end"] = PointValue(end),
                ["length"] = LengthValue(start.DistanceTo(end))
            };
            if (!string.IsNullOrWhiteSpace(name)) after["name"] = name.Trim();

            return Change(operation, index, "ready",
                target: new Dictionary<string, object> { ["document"] = document.Title },
                before: null,
                after: after);
        }

        private static Dictionary<string, object> ApplyCreateGrid(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateGrid(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_grid preview failed.");
            }

            XYZ start = ToInternalPoint(GetDictionary(operation, "start"), "start");
            XYZ end = ToInternalPoint(GetDictionary(operation, "end"), "end");
            Grid grid = Grid.Create(document, Line.CreateBound(start, end));
            string name = GetString(operation, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                grid.Name = name.Trim();
            }

            return Change(operation, index, "applied",
                target: ElementTarget(grid, null),
                before: null,
                after: GridSnapshot(grid));
        }

        private static bool LevelNameExists(Document document, string name)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Any(level => string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool GridNameExists(Document document, string name)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Any(grid => string.Equals(grid.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string ValidateLinearDatum(Document document, XYZ start, XYZ end, string operationName)
        {
            if (Math.Abs(start.Z - end.Z) > 0.000001)
            {
                return operationName + " start and end must have the same z elevation.";
            }

            double length = start.DistanceTo(end);
            double minimumLength = Math.Max(document.Application.ShortCurveTolerance, 0.000001);
            if (length <= minimumLength)
            {
                return operationName + " line is shorter than Revit's minimum curve length.";
            }

            return null;
        }
    }
}
