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
        private static Dictionary<string, object> PreviewCreateSheet(
            Document document,
            Dictionary<string, object> operation,
            int index,
            PreviewValidationContext validationContext = null)
        {
            string sheetNumber = NormalizeOptionalText(GetString(operation, "sheetNumber"));
            if (string.IsNullOrWhiteSpace(sheetNumber)) return BlockedChange(operation, index, "create_sheet requires sheetNumber.");
            if (SheetNumberExists(document, sheetNumber)) return BlockedChange(operation, index, "A sheet numbered '" + sheetNumber + "' already exists.");
            if (validationContext != null && !validationContext.TryAddSheetNumber(sheetNumber))
            {
                return BlockedChange(operation, index, "The change set creates duplicate sheet number '" + sheetNumber + "'.");
            }

            FamilySymbol titleBlockType = null;
            string titleBlockTypeId = GetString(operation, "titleBlockTypeId");
            if (!string.IsNullOrWhiteSpace(titleBlockTypeId))
            {
                titleBlockType = ResolveTitleBlockType(document, titleBlockTypeId);
                if (titleBlockType == null) return BlockedChange(operation, index, "Title block type " + titleBlockTypeId + " was not found.");
            }

            var target = new Dictionary<string, object>
            {
                ["document"] = document.Title,
                ["sheetNumber"] = sheetNumber
            };
            if (titleBlockType != null) target["titleBlockType"] = ElementSummary(document, titleBlockType);

            var after = new Dictionary<string, object>
            {
                ["sheetNumber"] = sheetNumber,
                ["name"] = NormalizeOptionalText(GetString(operation, "name"))
            };
            if (titleBlockType != null)
            {
                after["titleBlockTypeId"] = ToElementIdString(titleBlockType.Id);
                after["titleBlockTypeName"] = SafeElementName(titleBlockType);
            }

            return Change(operation, index, "ready", target, before: null, after: after);
        }

        private static Dictionary<string, object> ApplyCreateSheet(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewCreateSheet(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "create_sheet preview failed.");
            }

            FamilySymbol titleBlockType = ResolveTitleBlockType(document, GetString(operation, "titleBlockTypeId"));
            ElementId titleBlockTypeId = titleBlockType == null ? ElementId.InvalidElementId : titleBlockType.Id;
            ViewSheet sheet = ViewSheet.Create(document, titleBlockTypeId);
            if (sheet == null)
            {
                throw new InvalidOperationException("Revit did not create a sheet.");
            }

            sheet.SheetNumber = NormalizeOptionalText(GetString(operation, "sheetNumber"));
            string name = NormalizeOptionalText(GetString(operation, "name"));
            if (!string.IsNullOrWhiteSpace(name)) sheet.Name = name;

            return Change(operation, index, "applied",
                target: ElementTarget(sheet, null),
                before: null,
                after: SheetSnapshot(document, sheet));
        }

        private static Dictionary<string, object> PreviewPlaceViewOnSheet(Document document, Dictionary<string, object> operation, int index)
        {
            string sheetId = GetString(operation, "sheetId");
            string viewId = GetString(operation, "viewId");
            Dictionary<string, object> centerValue = GetDictionary(operation, "center");
            if (string.IsNullOrWhiteSpace(sheetId)) return BlockedChange(operation, index, "place_view_on_sheet requires sheetId.");
            if (string.IsNullOrWhiteSpace(viewId)) return BlockedChange(operation, index, "place_view_on_sheet requires viewId.");
            if (centerValue == null) return BlockedChange(operation, index, "place_view_on_sheet requires center.");

            ViewSheet sheet = ResolveElement(document, sheetId) as ViewSheet;
            if (sheet == null) return BlockedChange(operation, index, "Sheet " + sheetId + " was not found.");

            View view = ResolveElement(document, viewId) as View;
            if (view == null) return BlockedChange(operation, index, "View " + viewId + " was not found.");
            if (view is ViewSheet) return BlockedChange(operation, index, "place_view_on_sheet cannot place a sheet on a sheet.");
            if (view.IsTemplate) return BlockedChange(operation, index, "View " + viewId + " is a template and cannot be placed on a sheet.");

            XYZ center;
            try
            {
                center = ToInternalSheetPoint(centerValue, "center");
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, ex.Message);
            }

            bool canAdd;
            try
            {
                canAdd = Viewport.CanAddViewToSheet(document, sheet.Id, view.Id);
            }
            catch (Exception ex)
            {
                return BlockedChange(operation, index, "Revit could not validate sheet placement: " + ex.Message);
            }

            if (!canAdd)
            {
                return BlockedChange(operation, index, "View " + viewId + " cannot be placed on sheet " + sheetId + ". It may already be placed, be unsupported, or be incompatible with viewport placement.");
            }

            return Change(operation, index, "ready",
                target: new Dictionary<string, object>
                {
                    ["sheet"] = SheetSnapshot(document, sheet),
                    ["view"] = BuildViewSummary(view)
                },
                before: null,
                after: new Dictionary<string, object>
                {
                    ["sheetId"] = ToElementIdString(sheet.Id),
                    ["viewId"] = ToElementIdString(view.Id),
                    ["center"] = PointValue(center)
                });
        }

        private static Dictionary<string, object> ApplyPlaceViewOnSheet(Document document, Dictionary<string, object> operation, int index)
        {
            Dictionary<string, object> preview = PreviewPlaceViewOnSheet(document, operation, index);
            if (!string.Equals(GetString(preview, "status"), "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(preview, "message") ?? "place_view_on_sheet preview failed.");
            }

            ViewSheet sheet = ResolveElement(document, GetString(operation, "sheetId")) as ViewSheet;
            View view = ResolveElement(document, GetString(operation, "viewId")) as View;
            XYZ center = ToInternalSheetPoint(GetDictionary(operation, "center"), "center");
            Viewport viewport = Viewport.Create(document, sheet.Id, view.Id, center);
            if (viewport == null)
            {
                throw new InvalidOperationException("Revit did not create a viewport for place_view_on_sheet.");
            }

            return Change(operation, index, "applied",
                target: ElementTarget(viewport, null),
                before: null,
                after: ViewportSnapshot(document, viewport));
        }

        private static bool SheetNumberExists(Document document, string number)
        {
            string normalized = NormalizeOptionalText(number);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            return new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Any(sheet => string.Equals(sheet.SheetNumber, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static FamilySymbol ResolveTitleBlockType(Document document, string titleBlockTypeId)
        {
            if (string.IsNullOrWhiteSpace(titleBlockTypeId)) return null;

            FamilySymbol symbol = ResolveElement(document, titleBlockTypeId) as FamilySymbol;
            if (symbol == null) return null;
            return string.Equals(GetBuiltInCategoryName(symbol), "OST_TitleBlocks", StringComparison.OrdinalIgnoreCase) ? symbol : null;
        }

        private static Dictionary<string, object> SheetSnapshot(Document document, ViewSheet sheet)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(sheet.Id),
                ["uniqueId"] = sheet.UniqueId,
                ["sheetNumber"] = sheet.SheetNumber,
                ["name"] = SafeElementName(sheet),
                ["titleBlockIds"] = GetSheetTitleBlockIds(document, sheet).ToArray()
            };

            return snapshot;
        }

        private static Dictionary<string, object> ViewportSnapshot(Document document, Viewport viewport)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["id"] = ToElementIdString(viewport.Id),
                ["uniqueId"] = viewport.UniqueId,
                ["viewId"] = ToElementIdString(viewport.ViewId)
            };

            try
            {
                snapshot["sheetId"] = ToElementIdString(viewport.SheetId);
            }
            catch
            {
                // Older/unusual viewport variants may not expose SheetId reliably.
            }

            View view = document.GetElement(viewport.ViewId) as View;
            if (view != null)
            {
                snapshot["viewName"] = SafeElementName(view);
                snapshot["viewType"] = view.ViewType.ToString();
            }

            try
            {
                snapshot["center"] = PointValue(viewport.GetBoxCenter());
            }
            catch
            {
                // Viewport box center can be unavailable for unusual sheet contents.
            }

            return snapshot;
        }
    }
}
