using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// edit_sheets place | move_viewport | remove_viewport | set_viewport_type (D3 §3.4). Views and legends become
    /// Viewports, schedules ScheduleSheetInstances. Sheet coordinates are mm from the sheet origin; `at` defaults to the
    /// titleblock centre.
    /// </summary>
    internal static class ViewportOps
    {
        [Op("edit_sheets.place")]
        public static void Place(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSheet sheet = SheetSupport.Sheet(c);
            View view = c.View("view");
            if (view is ViewSheet) throw OpException.InvalidArgs("view", "'" + view.Name + "' is a sheet; a sheet cannot be placed on a sheet");
            if (view.IsTemplate) throw OpException.InvalidArgs("view", "'" + view.Name + "' is a view template; templates cannot be placed on sheets");

            XYZ at = c.Args.PointOpt("at", 0);
            at = at == null ? SheetSupport.SheetCenter(doc, sheet) : new XYZ(at.X, at.Y, 0);
            var data = new Dictionary<string, object> { ["sheet"] = SheetSupport.SheetData(sheet) };

            if (view is ViewSchedule schedule)
            {
                if (IsRevisionOrKeynoteSchedule(schedule))
                {
                    throw OpException.InvalidArgs("view", "'" + schedule.Name + "' is a titleblock revision or keynote schedule; it cannot be placed this way");
                }
                if (c.Has("type")) c.Warn(WarningCodes.ParamIgnored, "type: schedules on sheets have no viewport type");
                ScheduleSheetInstance instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, at)
                    ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not place schedule '" + schedule.Name + "' on sheet " + SheetSupport.Label(sheet) + ".");
                c.Created(instance);
                c.Output("viewport", instance.Id);
                data["placed"] = new Dictionary<string, object> { ["id"] = instance.Id.Value, ["kind"] = "schedule", ["view"] = schedule.Name, ["at"] = SheetSupport.Point2(instance.Point) };
                c.Data(data);
                c.Summary("placed schedule '" + schedule.Name + "' on sheet " + SheetSupport.Label(sheet));
                return;
            }

            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)) throw CannotPlace(doc, sheet, view, c);
            Viewport viewport = Viewport.Create(doc, sheet.Id, view.Id, at)
                ?? throw c.Error(ErrorCodes.RevitRefused, "Revit did not place '" + view.Name + "' on sheet " + SheetSupport.Label(sheet) + ".");
            if (c.Has("type"))
            {
                ElementType type = SheetSupport.ViewportType(doc, viewport, c.Str("type"));
                if (viewport.GetTypeId() != type.Id) viewport.ChangeTypeId(type.Id);
            }
            c.Created(viewport);
            c.Output("viewport", viewport.Id);
            c.MarkDirty();
            c.EnsureRegenerated();
            data["placed"] = new Dictionary<string, object>
            {
                ["id"] = viewport.Id.Value,
                ["kind"] = view.ViewType == ViewType.Legend ? "legend" : "view",
                ["view"] = view.Name,
                ["view_id"] = view.Id.Value,
                ["type"] = doc.GetElement(viewport.GetTypeId())?.Name,
                ["at"] = SheetSupport.Point2(viewport.GetBoxCenter())
            };
            c.Data(data);
            c.Summary("placed '" + view.Name + "' on sheet " + SheetSupport.Label(sheet));
        }

        [Op("edit_sheets.move_viewport")]
        public static void Move(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSheet sheet = SheetSupport.Sheet(c);
            Element placement = SheetSupport.Placement(c, sheet);
            XYZ at = c.Args.PointOpt("at", 0);
            XYZ by = c.Has("by") ? c.Args.Vector("by") : null;
            if (at == null && by == null) throw OpException.InvalidArgs("at", "give at (new centre [x,y] mm) or by (shift [dx,dy] mm)");
            if (at != null && by != null) throw OpException.InvalidArgs("by", "give at or by, not both");

            XYZ before;
            XYZ after;
            if (placement is Viewport viewport)
            {
                before = viewport.GetBoxCenter();
                after = at != null ? new XYZ(at.X, at.Y, before.Z) : before + new XYZ(by.X, by.Y, 0);
                viewport.SetBoxCenter(after);
            }
            else
            {
                var instance = (ScheduleSheetInstance)placement;
                before = instance.Point;
                after = at != null ? new XYZ(at.X, at.Y, before.Z) : before + new XYZ(by.X, by.Y, 0);
                instance.Point = after;
            }
            c.Modified(placement, "center", SheetSupport.Point2(before));
            c.Output("viewport", placement.Id);
            c.Data(new Dictionary<string, object>
            {
                ["viewport"] = placement.Id.Value,
                ["from"] = SheetSupport.Point2(before),
                ["to"] = SheetSupport.Point2(after)
            });
            c.Summary("moved '" + ViewName(doc, placement) + "' on sheet " + SheetSupport.Label(sheet) + " to [" +
                      string.Join(", ", SheetSupport.Point2(after)) + "] mm");
        }

        [Op("edit_sheets.remove_viewport")]
        public static void Remove(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSheet sheet = SheetSupport.Sheet(c);
            Element placement = SheetSupport.Placement(c, sheet);
            string name = ViewName(doc, placement);
            ICollection<ElementId> deleted = c.Delete(new List<ElementId> { placement.Id });
            c.Data(new Dictionary<string, object> { ["removed"] = placement.Id.Value, ["view"] = name, ["sheet"] = SheetSupport.SheetData(sheet) });
            c.Summary("removed '" + name + "' from sheet " + SheetSupport.Label(sheet) + " (the view itself is kept" +
                      (deleted.Count > 1 ? "; " + (deleted.Count - 1).ToString(CultureInfo.InvariantCulture) + " dependent element(s) removed" : string.Empty) + ")");
        }

        [Op("edit_sheets.set_viewport_type")]
        public static void SetType(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewSheet sheet = SheetSupport.Sheet(c);
            Element placement = SheetSupport.Placement(c, sheet);
            if (!(placement is Viewport viewport))
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "Schedules on sheets have no viewport type.",
                    new Dictionary<string, object> { ["hint"] = "viewport types apply to views and legends only" });
            }
            string value = c.Str("type");
            if (string.IsNullOrWhiteSpace(value)) throw OpException.InvalidArgs("type", "is required (a viewport type name)");
            ElementType type = SheetSupport.ViewportType(doc, viewport, value);
            string before = doc.GetElement(viewport.GetTypeId())?.Name;
            if (viewport.GetTypeId() != type.Id)
            {
                viewport.ChangeTypeId(type.Id);
                c.Modified(viewport, "type", before);
            }
            c.Output("viewport", viewport.Id);
            c.Data(new Dictionary<string, object> { ["viewport"] = viewport.Id.Value, ["type"] = type.Name, ["before"] = before });
            c.Summary(c.ModifiedIds.Count == 0
                ? "'" + ViewName(doc, viewport) + "' already uses viewport type " + type.Name
                : "viewport type of '" + ViewName(doc, viewport) + "' on sheet " + SheetSupport.Label(sheet) + " is now " + type.Name);
        }

        // ---------------------------------------------------------------------------------------------------------

        private static OpException CannotPlace(Document doc, ViewSheet sheet, View view, ChangeContext c)
        {
            ViewSheet other = SheetSupport.SheetShowing(doc, view);
            if (other != null && other.Id == sheet.Id)
            {
                return new OpException(ErrorCodes.UnsupportedOp, "'" + view.Name + "' is already on sheet " + SheetSupport.Label(sheet) + ".",
                    new Dictionary<string, object>
                    {
                        ["alternative"] = new Dictionary<string, object>
                        {
                            ["tool"] = "edit_sheets",
                            ["args"] = new Dictionary<string, object> { ["op"] = "move_viewport", ["sheet"] = sheet.SheetNumber, ["view"] = view.Name, ["at"] = c.Args.Get("at") ?? new List<object> { 0, 0 } }
                        }
                    });
            }
            if (other != null)
            {
                var args = new Dictionary<string, object> { ["op"] = "duplicate", ["view"] = view.Name, ["sheet"] = sheet.SheetNumber };
                if (c.Args.Get("at") != null) args["at"] = c.Args.Get("at");
                return new OpException(ErrorCodes.UnsupportedOp,
                    "'" + view.Name + "' is already placed on sheet " + SheetSupport.Label(other) + "; a view can be on one sheet only (legends and schedules excepted).",
                    new Dictionary<string, object>
                    {
                        ["sheet"] = other.SheetNumber,
                        ["alternative"] = new Dictionary<string, object> { ["tool"] = "edit_views", ["args"] = args }
                    });
            }
            return new OpException(ErrorCodes.UnsupportedOp,
                "Revit cannot place '" + view.Name + "' (" + view.ViewType + ") on a sheet.",
                new Dictionary<string, object> { ["hint"] = "only graphical views, legends and schedules can be placed; empty or browser views cannot" });
        }

        private static bool IsRevisionOrKeynoteSchedule(ViewSchedule schedule)
        {
            try
            {
                return schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        internal static string ViewName(Document doc, Element placement)
        {
            if (placement is Viewport viewport) return doc.GetElement(viewport.ViewId)?.Name ?? viewport.Id.Value.ToString(CultureInfo.InvariantCulture);
            if (placement is ScheduleSheetInstance instance) return doc.GetElement(instance.ScheduleId)?.Name ?? instance.Id.Value.ToString(CultureInfo.InvariantCulture);
            return placement?.Name;
        }
    }
}
