using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// ui (D3 §3.2, SPEC §5.9): drive the Revit window without changing the model. select/zoom_to/open_views/close_views
    /// need the target doc UI-active (the dispatcher returns NEEDS_ACTIVE_DOC otherwise) and never switch documents.
    /// isolate/hide/reset run in "MCP ui &lt;op&gt;" transactions (temporary view mode). activate_view/activate_doc switch
    /// the UI explicitly (the broker re-pins). ui.dialogs and ui.press are control-pipe ops (P-REL-ADDIN).
    /// </summary>
    internal static class UiHandlers
    {
        private const int MaxListedIds = 50;
        private const string TemporaryNote = "temporary view mode; undo is not needed";

        // ---------------------------------------------------------------------------------------------------- select

        [Op("ui.select")]
        public static OpResult Select(RequestContext ctx)
        {
            UIDocument uidoc = ActiveUiDoc(ctx);
            Document doc = ctx.Doc;
            string mode = ctx.Args.Enum("mode", "replace", "replace", "add", "remove");
            List<ElementId> ids = ViewKit.SelectedElements(ctx, ctx.Args, doc).Select(e => e.Id).ToList();
            var selection = new List<ElementId>();
            if (mode != "replace")
            {
                selection.AddRange(uidoc.Selection.GetElementIds());
            }
            if (mode == "remove")
            {
                var remove = new HashSet<long>(ids.Select(id => id.Value));
                selection = selection.Where(id => !remove.Contains(id.Value)).ToList();
            }
            else
            {
                var have = new HashSet<long>(selection.Select(id => id.Value));
                selection.AddRange(ids.Where(id => have.Add(id.Value)));
            }
            uidoc.Selection.SetElementIds(selection);
            if (ctx.Args.Bool("zoom") && selection.Count > 0) uidoc.ShowElements(selection);
            var data = new Dictionary<string, object>
            {
                ["selected"] = selection.Count,
                ["ids"] = selection.Take(MaxListedIds).Select(id => (object)id.Value).ToList()
            };
            string verb = mode == "remove" ? "removed " + ids.Count.ToString(CultureInfo.InvariantCulture) + " from" : mode == "add" ? "added " + ids.Count.ToString(CultureInfo.InvariantCulture) + " to" : "set";
            return ctx.Ok(data, verb + " the selection: " + selection.Count.ToString(CultureInfo.InvariantCulture) + " element" + (selection.Count == 1 ? string.Empty : "s") + " selected" + (ctx.Args.Bool("zoom") ? ", zoomed" : string.Empty));
        }

        // --------------------------------------------------------------------------------------------------- zoom_to

        [Op("ui.zoom_to")]
        public static OpResult ZoomTo(RequestContext ctx)
        {
            UIDocument uidoc = ActiveUiDoc(ctx);
            Document doc = ctx.Doc;
            if (ctx.Args.Has("view"))
            {
                View target = Resolve.View(doc, ctx.Args.Str("view"), "view", uidoc);
                if (uidoc.ActiveView == null || uidoc.ActiveView.Id != target.Id) ActivateView(uidoc, target);
            }
            View view = uidoc.ActiveView;
            string what;
            if (ctx.Args.Has("ids") || ctx.Args.Has("from"))
            {
                List<ElementId> ids = ViewKit.SelectedElements(ctx, ctx.Args, doc).Select(e => e.Id).ToList();
                uidoc.ShowElements(ids);
                what = ids.Count.ToString(CultureInfo.InvariantCulture) + " element" + (ids.Count == 1 ? string.Empty : "s");
                view = uidoc.ActiveView;
            }
            else if (ctx.Args.Has("region"))
            {
                double[] region = ctx.Args.BoxFt("region");
                UIView uiView = OpenUiView(uidoc, view) ?? throw new OpException(ErrorCodes.NotFound, "The active view '" + view.Name + "' is not open in a window.",
                    new Dictionary<string, object> { ["param"] = "view", ["kind"] = "view", ["value"] = view.Name, ["candidates"] = new List<object>() });
                XYZ p0, p1;
                RegionCorners(view, region, out p0, out p1);
                uiView.ZoomAndCenterRectangle(p0, p1);
                what = "the region";
            }
            else if (ctx.Args.Bool("fit"))
            {
                UIView uiView = OpenUiView(uidoc, view);
                uiView?.ZoomToFit();
                what = "fit";
            }
            else
            {
                throw OpException.InvalidArgs("ids", "zoom_to needs ids, from, region or fit:true");
            }
            return ctx.Ok(ViewKit.Brief(view), "zoomed '" + view.Name + "' to " + what).WithOutput("view", view.Id.Value);
        }

        // ----------------------------------------------------------------------------------- isolate / hide / reset

        [Op("ui.isolate")]
        public static void Isolate(ChangeContext c)
        {
            View view = TemporaryView(c);
            string what;
            if (c.Has("ids") || c.Has("from"))
            {
                List<ElementId> ids = ViewKit.SelectedElements(c.Ctx, c.Args, c.Doc).Select(e => e.Id).ToList();
                view.IsolateElementsTemporary(ids);
                what = ids.Count.ToString(CultureInfo.InvariantCulture) + " element" + (ids.Count == 1 ? string.Empty : "s");
            }
            else if (c.Has("category"))
            {
                List<Category> categories = ViewKit.Categories(c.Doc, c.Args);
                view.IsolateCategoriesTemporary(categories.Select(cat => cat.Id).ToList());
                what = string.Join(", ", categories.Select(Rows.CategoryLabel));
            }
            else
            {
                throw OpException.InvalidArgs("ids", "isolate needs ids, from or category");
            }
            c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["temporary"] = true });
            c.Summary("isolated " + what + " in '" + view.Name + "' (" + TemporaryNote + ")");
        }

        [Op("ui.hide")]
        public static void Hide(ChangeContext c)
        {
            View view = TemporaryView(c);
            string what;
            if (c.Has("ids") || c.Has("from"))
            {
                List<Element> elements = ViewKit.SelectedElements(c.Ctx, c.Args, c.Doc);
                List<ElementId> ids = elements.Where(e => SafeCanHide(e, view)).Select(e => e.Id).ToList();
                if (ids.Count == 0) throw OpException.InvalidArgs(c.Has("ids") ? "ids" : "from", "none of these elements can be hidden in '" + view.Name + "'");
                view.HideElementsTemporary(ids);
                if (ids.Count < elements.Count) c.Warn(WarningCodes.PartialResult, (elements.Count - ids.Count).ToString(CultureInfo.InvariantCulture) + " element(s) cannot be hidden in '" + view.Name + "'");
                what = ids.Count.ToString(CultureInfo.InvariantCulture) + " element" + (ids.Count == 1 ? string.Empty : "s");
            }
            else if (c.Has("category"))
            {
                List<Category> categories = ViewKit.Categories(c.Doc, c.Args);
                view.HideCategoriesTemporary(categories.Select(cat => cat.Id).ToList());
                what = string.Join(", ", categories.Select(Rows.CategoryLabel));
            }
            else
            {
                throw OpException.InvalidArgs("ids", "hide needs ids, from or category");
            }
            c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["temporary"] = true });
            c.Summary("temporarily hid " + what + " in '" + view.Name + "' (" + TemporaryNote + ")");
        }

        [Op("ui.reset")]
        public static void Reset(ChangeContext c)
        {
            View view = TemporaryView(c);
            bool active = SafeBool(() => view.IsTemporaryHideIsolateActive());
            if (active) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            c.Data(new Dictionary<string, object> { ["view"] = view.Id.Value, ["was_active"] = active });
            c.Summary((active ? "reset temporary hide/isolate in '" : "no temporary hide/isolate was active in '") + view.Name + "' (" + TemporaryNote + ")");
        }

        // ---------------------------------------------------------------------------------------------- activation

        [Op("ui.activate_view")]
        public static OpResult ActivateView(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            View view = Resolve.View(doc, ctx.Args.Str("view"), "view", ctx.UiDoc);
            if (view.IsTemplate) throw OpException.InvalidArgs("view", "'" + view.Name + "' is a view template; templates cannot be opened");
            bool switched = false;
            if (!ctx.DocIsActive)
            {
                UiActivation.ActivateDocument(ctx, doc, view);
                switched = true;
            }
            UIDocument uidoc = ctx.App.ActiveUIDocument;
            if (uidoc.ActiveView == null || uidoc.ActiveView.Id != view.Id) ActivateView(uidoc, view);
            Dictionary<string, object> data = ViewKit.Brief(view);
            if (switched) data["doc_activated"] = true;
            return ctx.Ok(data, "activated " + ViewKit.Title(view) + (switched ? " (switched to " + doc.Title + ")" : string.Empty)).WithOutput("view", view.Id.Value);
        }

        [Op("ui.activate_doc")]
        public static OpResult ActivateDoc(RequestContext ctx)
        {
            Document doc = ctx.Doc;
            if (doc == null && ctx.Request.Doc != null)
            {
                throw new OpException(ErrorCodes.DocNotOpen, "The document to activate is no longer open in this Revit.",
                    new Dictionary<string, object> { ["key"] = ctx.Request.Doc.Key, ["rid"] = ctx.Request.Doc.Rid });
            }
            if (doc == null) throw OpException.InvalidArgs("doc", "is required (the document to bring to the front: #n, title or path)");
            bool already = UiActivation.IsActive(ctx.App, doc);
            if (!already) UiActivation.ActivateDocument(ctx, doc, null);
            View active = SafeGet(() => ctx.App.ActiveUIDocument?.ActiveView);
            var data = new Dictionary<string, object> { ["doc"] = doc.Title, ["was_active"] = already };
            if (active != null) data["view"] = ViewKit.Brief(active);
            return ctx.Ok(data, (already ? doc.Title + " already is the active document" : "activated " + doc.Title) + (active != null ? ", view " + active.Name : string.Empty));
        }

        // ----------------------------------------------------------------------------------------- open/close views

        [Op("ui.open_views")]
        public static OpResult OpenViews(RequestContext ctx)
        {
            UIDocument uidoc = ActiveUiDoc(ctx);
            Document doc = ctx.Doc;
            ElementId activeId = uidoc.ActiveView?.Id;
            var table = new TableBuilder(new[] { "id", "name", "type", "active" });
            IList<UIView> open = uidoc.GetOpenUIViews();
            foreach (UIView uiView in open)
            {
                View view = doc.GetElement(uiView.ViewId) as View;
                if (view == null) continue;
                table.Add(view.Id.Value, view is ViewSheet sheet ? sheet.SheetNumber + " - " + sheet.Name : view.Name, view.ViewType.ToString(), view.Id == activeId);
            }
            return ctx.Ok(table.ToWire(), open.Count.ToString(CultureInfo.InvariantCulture) + " open view" + (open.Count == 1 ? string.Empty : "s") + " in " + doc.Title);
        }

        [Op("ui.close_views")]
        public static OpResult CloseViews(RequestContext ctx)
        {
            UIDocument uidoc = ActiveUiDoc(ctx);
            Document doc = ctx.Doc;
            List<string> names = ctx.Args.Strs("views");
            if (names.Count == 0) throw OpException.InvalidArgs("views", "is required (view names/ids, or ['all_but_active'])");
            List<UIView> open = uidoc.GetOpenUIViews().ToList();
            ElementId activeId = uidoc.ActiveView?.Id;
            var toClose = new List<UIView>();
            bool allButActive = names.Any(n => string.Equals(n.Replace(" ", "_"), "all_but_active", StringComparison.OrdinalIgnoreCase));
            if (allButActive)
            {
                toClose.AddRange(open.Where(v => v.ViewId != activeId));
            }
            foreach (string name in names.Where(n => !string.Equals(n.Replace(" ", "_"), "all_but_active", StringComparison.OrdinalIgnoreCase)))
            {
                View view = Resolve.View(doc, name, "views", uidoc);
                UIView match = open.FirstOrDefault(v => v.ViewId == view.Id);
                if (match == null)
                {
                    ctx.Warn(WarningCodes.PartialResult, "'" + view.Name + "' is not open");
                    continue;
                }
                if (!toClose.Contains(match)) toClose.Add(match);
            }
            if (toClose.Count > 0 && toClose.Count >= open.Count)
            {
                throw new OpException(ErrorCodes.LastView, "Closing these views would close every view of '" + doc.Title + "'; Revit would close the document.",
                    new Dictionary<string, object> { ["open"] = open.Count });
            }
            var closed = new List<object>();
            foreach (UIView uiView in toClose)
            {
                string label = (doc.GetElement(uiView.ViewId) as View)?.Name ?? uiView.ViewId.Value.ToString(CultureInfo.InvariantCulture);
                uiView.Close();
                closed.Add(label);
            }
            return ctx.Ok(new Dictionary<string, object> { ["closed"] = closed, ["open"] = open.Count - closed.Count },
                "closed " + closed.Count.ToString(CultureInfo.InvariantCulture) + " view" + (closed.Count == 1 ? string.Empty : "s") + "; " + (open.Count - closed.Count).ToString(CultureInfo.InvariantCulture) + " still open");
        }

        // --------------------------------------------------------------------------------------------------- helpers

        private static UIDocument ActiveUiDoc(RequestContext ctx)
        {
            UIDocument uidoc = ctx.DocIsActive ? ctx.UiDoc ?? ctx.App?.ActiveUIDocument : null;
            if (uidoc == null)
            {
                throw new OpException(ErrorCodes.NeedsActiveDoc, "'" + ctx.Doc?.Title + "' is not the active document in Revit.",
                    new Dictionary<string, object> { ["doc"] = ctx.Doc?.Title, ["rid"] = ctx.Rid });
            }
            return uidoc;
        }

        /// <summary>view= or the document's active view; templates, sheets and schedules have no temporary mode.</summary>
        private static View TemporaryView(ChangeContext c)
        {
            View view = Resolve.View(c.Doc, c.Str("view") ?? "active", c.Args.Field("view"), c.Ctx.UiDoc);
            if (view.IsTemplate || view is ViewSheet || view.ViewType == ViewType.Schedule)
            {
                throw OpException.InvalidArgs(c.Args.Field("view"), "'" + view.Name + "' (" + view.ViewType + ") has no temporary hide/isolate");
            }
            return view;
        }

        internal static void ActivateView(UIDocument uidoc, View view)
        {
            try
            {
                uidoc.ActiveView = view;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new OpException(ErrorCodes.RevitRefused, "Revit cannot open '" + view.Name + "': " + ex.Message,
                    new Dictionary<string, object> { ["apiMessage"] = ex.Message });
            }
        }

        private static UIView OpenUiView(UIDocument uidoc, View view)
        {
            return uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
        }

        /// <summary>[x0,y0,x1,y1] (feet): model XY at the plan level, or along-view/elevation coordinates elsewhere.</summary>
        internal static void RegionCorners(View view, double[] region, out XYZ p0, out XYZ p1)
        {
            double x0 = region[0], y0 = region[1];
            double x1 = region.Length == 6 ? region[3] : region[2];
            double y1 = region.Length == 6 ? region[4] : region[3];
            if (ViewKit.IsPlanLike(view) || view.ViewType == ViewType.DraftingView || view is View3D || view is ViewSheet)
            {
                double z = view.GenLevel?.ProjectElevation ?? 0;
                p0 = new XYZ(x0, y0, z);
                p1 = new XYZ(x1, y1, z);
                return;
            }
            XYZ origin = view.Origin, right = view.RightDirection, up = view.UpDirection;
            XYZ basePoint = origin - right.Multiply(origin.DotProduct(right)) - up.Multiply(origin.DotProduct(up));
            p0 = basePoint + right.Multiply(x0) + up.Multiply(y0);
            p1 = basePoint + right.Multiply(x1) + up.Multiply(y1);
        }

        private static bool SafeCanHide(Element element, View view)
        {
            try { return element.CanBeHidden(view); } catch { return false; }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return false; }
        }

        private static T SafeGet<T>(Func<T> read) where T : class
        {
            try { return read(); } catch { return null; }
        }
    }

    /// <summary>Brings a document to the front (V4: OpenAndActivateDocument re-activates an open doc; ShowElements fallback).</summary>
    internal static class UiActivation
    {
        public static bool IsActive(UIApplication app, Document doc)
        {
            try
            {
                Document active = app?.ActiveUIDocument?.Document;
                return active != null && active.Equals(doc);
            }
            catch
            {
                return false;
            }
        }

        public static void ActivateDocument(RequestContext ctx, Document doc, View preferred)
        {
            UIApplication app = ctx.App;
            if (IsActive(app, doc)) return;
            string reason = null;
            try
            {
                if (doc.IsModelInCloud)
                {
                    app.OpenAndActivateDocument(doc.GetCloudModelPath(), new OpenOptions(), false);
                }
                else if (!string.IsNullOrWhiteSpace(doc.PathName) && File.Exists(doc.PathName) && !doc.IsFamilyDocument)
                {
                    app.OpenAndActivateDocument(doc.PathName);
                }
            }
            catch (Exception ex) when (!(ex is OpException))
            {
                reason = ex.Message;
            }
            if (IsActive(app, doc)) return;

            // Fallback (unsaved, detached or family docs): showing an element opens a view of its document.
            try
            {
                var uidoc = new UIDocument(doc);
                ElementId element = ShowTarget(doc, preferred);
                if (element != null) uidoc.ShowElements(element);
            }
            catch (Exception ex) when (!(ex is OpException))
            {
                reason = reason ?? ex.Message;
            }
            if (IsActive(app, doc)) return;
            throw new OpException(ErrorCodes.RevitRefused, "Revit did not switch to '" + doc.Title + "'" + (reason == null ? "." : ": " + reason),
                new Dictionary<string, object> { ["apiMessage"] = reason ?? "the document stayed in the background", ["doc"] = doc.Title });
        }

        private static ElementId ShowTarget(Document doc, View preferred)
        {
            if (preferred != null && !preferred.IsTemplate)
            {
                try
                {
                    ElementId inView = new FilteredElementCollector(doc, preferred.Id).WhereElementIsNotElementType().FirstElementId();
                    if (inView != null && inView != ElementId.InvalidElementId) return inView;
                }
                catch
                {
                    // Try a level instead.
                }
            }
            ElementId level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();
            if (level != null && level != ElementId.InvalidElementId) return level;
            ElementId any = new FilteredElementCollector(doc).WhereElementIsNotElementType().FirstElementId();
            return any == ElementId.InvalidElementId ? null : any;
        }
    }
}
