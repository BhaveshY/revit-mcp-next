using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// edit_views (D1 §4B, D3 §3): create views of ten kinds, duplicate, rename, set, create_template, delete. Every op
    /// runs inside the ChangeEngine's transaction (tx "in"); create and duplicate are strict (warnings roll back).
    /// Lengths arrive in mm and are converted with the PayloadReader/Units helpers.
    /// </summary>
    internal static partial class EditViewsOps
    {
        private static readonly string[] Kinds = { "floor_plan", "ceiling_plan", "structural_plan", "area_plan", "section", "elevation", "3d", "callout", "drafting", "legend" };
        private const double DefaultSectionDepthMm = 3000;
        private const double DefaultSectionHeightMm = 4000;
        private const double FitMarginMm = 500;

        // ---------------------------------------------------------------------------------------------------- create

        [Op("edit_views.create")]
        public static void Create(ChangeContext c)
        {
            Document doc = c.Doc;
            string kind = c.Args.Enum("kind", null, Kinds) ?? throw OpException.InvalidArgs("kind", "is required: one of " + string.Join(", ", Kinds));
            View view;
            switch (kind)
            {
                case "floor_plan":
                    view = CreatePlan(c, ViewFamily.FloorPlan, ElementTypeGroup.ViewTypeFloorPlan);
                    break;
                case "ceiling_plan":
                    view = CreatePlan(c, ViewFamily.CeilingPlan, ElementTypeGroup.ViewTypeCeilingPlan);
                    break;
                case "structural_plan":
                    view = CreatePlan(c, ViewFamily.StructuralPlan, ElementTypeGroup.ViewTypeStructuralPlan);
                    break;
                case "area_plan":
                    view = CreateAreaPlan(c);
                    break;
                case "section":
                    view = CreateSection(c);
                    break;
                case "elevation":
                    view = CreateElevation(c);
                    break;
                case "3d":
                    view = Create3D(c);
                    break;
                case "callout":
                    view = CreateCallout(c);
                    break;
                case "drafting":
                    view = ViewDrafting.Create(doc, ViewKit.FamilyType(doc, ViewFamily.Drafting, c.Str("view_type"), c.Args.Field("view_type"), ElementTypeGroup.ViewTypeDrafting).Id);
                    break;
                default:
                    view = CreateLegend(c);
                    break;
            }

            c.Created(view);
            if (c.Has("name")) ViewKit.Rename(view, c.Str("name"), c.Args.Field("name"));
            if (c.Has("template")) ApplyTemplate(view, c.Str("template"), c.Args.Field("template"));
            if (c.Has("scale") && !(view is View3D v3 && v3.IsPerspective)) SetScale(view, c.Int("scale"), c.Args.Field("scale"));
            c.Output("view", view.Id);
            c.Data(ViewKit.Brief(view));
            c.Summary("created " + kind.Replace('_', ' ') + " view '" + view.Name + "'" + (view.GenLevel != null ? " on " + view.GenLevel.Name : string.Empty));
        }

        private static View CreatePlan(ChangeContext c, ViewFamily family, ElementTypeGroup group)
        {
            Level level = c.Level("level");
            ViewFamilyType type = ViewKit.FamilyType(c.Doc, family, c.Str("view_type"), c.Args.Field("view_type"), group);
            return ViewPlan.Create(c.Doc, type.Id, level.Id);
        }

        private static View CreateAreaPlan(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level");
            List<AreaScheme> schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            if (schemes.Count == 0)
            {
                throw new OpException(ErrorCodes.NotFound, "The document has no area scheme.",
                    new Dictionary<string, object> { ["param"] = "area_scheme", ["kind"] = "area scheme", ["value"] = c.Str("area_scheme"), ["candidates"] = new List<object>() });
            }
            AreaScheme scheme = c.Has("area_scheme")
                ? Resolve.ById<AreaScheme>(doc, c.Str("area_scheme")) ?? Resolve.Single(schemes, s => s.Name, c.Str("area_scheme"), c.Args.Field("area_scheme"), "area scheme")
                : schemes.FirstOrDefault(s => !s.IsGrossBuildingArea) ?? schemes[0];
            return ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);
        }

        /// <summary>
        /// Section through start→end looking to the left of that direction (V5): BasisX = start→end, BasisY = Z,
        /// BasisZ = BasisX × Z points toward the viewer; the cut plane sits at local z = 0 and the far clip at −depth.
        /// With ids/from and no start/end, the section cuts along X just south of the elements and looks north.
        /// </summary>
        private static View CreateSection(ChangeContext c)
        {
            Document doc = c.Doc;
            Level level = c.Level("level", required: false);
            double depth = c.Mm("depth", DefaultSectionDepthMm);
            if (depth <= 0) throw OpException.InvalidArgs(c.Args.Field("depth"), "must be greater than 0");
            XYZ start, end;
            double bottom, top;
            if (c.Has("start") || c.Has("end"))
            {
                start = c.Point("start");
                end = c.Point("end");
                level = level ?? LowestLevel(doc);
                double baseZ = level?.ProjectElevation ?? 0;
                double height = c.Has("height") ? c.Mm("height") : DefaultHeightFt(doc, level);
                if (height <= 0) throw OpException.InvalidArgs(c.Args.Field("height"), "must be greater than 0");
                bottom = baseZ - Units.MmToFt(300);
                top = baseZ + height;
            }
            else
            {
                BoundingBoxXYZ fit = FitBox(c, "section") ?? throw OpException.InvalidArgs("start", "section needs start and end ([x,y] mm), or ids/from to fit around");
                start = new XYZ(fit.Min.X, fit.Min.Y, 0);
                end = new XYZ(fit.Max.X, fit.Min.Y, 0);
                if (!c.Has("depth")) depth = fit.Max.Y - fit.Min.Y;
                bottom = fit.Min.Z;
                top = fit.Max.Z;
            }

            XYZ flat = new XYZ(end.X - start.X, end.Y - start.Y, 0);
            double length = flat.GetLength();
            if (length < Units.MmToFt(10)) throw OpException.InvalidArgs(c.Args.Field("end"), "must be at least 10 mm away from start");
            XYZ along = flat.Normalize();
            // V5: CreateSection looks along sectionBox.Transform.BasisZ from the cut plane at Min.Z (measured live on 2027:
            // BasisZ = along x Z put the cut line at the far end, looking right). Looking left of start->end therefore
            // needs BasisZ = Z x along with the volume z in [0, depth]; BasisX = BasisY x BasisZ keeps the frame
            // right-handed (Revit derives the screen-right direction itself, which comes out as start->end).
            XYZ left = XYZ.BasisZ.CrossProduct(along);
            var transform = Transform.Identity;
            transform.Origin = new XYZ((start.X + end.X) / 2, (start.Y + end.Y) / 2, 0);
            transform.BasisX = XYZ.BasisZ.CrossProduct(left);
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = left;
            var box = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-length / 2, bottom, 0),
                Max = new XYZ(length / 2, top, depth)
            };
            ViewFamilyType type = ViewKit.FamilyType(doc, ViewFamily.Section, c.Str("view_type"), c.Args.Field("view_type"), ElementTypeGroup.ViewTypeSection);
            ViewSection section = ViewSection.CreateSection(doc, type.Id, box);
            Parameter far = section.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            if (far != null && !far.IsReadOnly) far.Set(depth);
            return section;
        }

        private static View CreateElevation(ChangeContext c)
        {
            Document doc = c.Doc;
            if (!c.Has("at")) throw OpException.InvalidArgs("at", "elevation needs at: the marker point [x,y] mm");
            string direction = c.Args.Enum("direction", "north", "north", "south", "east", "west");
            ViewPlan host = HostPlan(c);
            XYZ at = c.Point("at", host.GenLevel?.ProjectElevation ?? 0);
            int scale = c.Has("scale") ? c.Int("scale") : 100;
            ViewFamilyType type = ViewKit.FamilyType(doc, ViewFamily.Elevation, c.Str("view_type"), c.Args.Field("view_type"), ElementTypeGroup.ViewTypeElevation);
            ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, type.Id, at, scale);
            c.Created(marker);
            XYZ looking = LookingVector(direction);
            // V5: the index → direction mapping is verified on the created view instead of assumed. Wrong-facing tries
            // are deleted only after the match exists: deleting a marker's last view deletes the marker (e2e S50, 2027).
            var wrong = new List<ElementId>();
            var tried = new List<string>();
            ViewSection match = null;
            int count = marker.MaximumViewCount;
            for (int index = 0; index < count && match == null; index++)
            {
                if (!marker.IsAvailableIndex(index)) continue;
                ViewSection elevation = marker.CreateElevation(doc, host.Id, index);
                doc.Regenerate();
                XYZ actual = elevation.ViewDirection.Negate();
                tried.Add(index.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", OutputJson.Direction(actual)));
                if (new XYZ(actual.X, actual.Y, 0).Normalize().IsAlmostEqualTo(looking, 1e-3)) match = elevation;
                else wrong.Add(elevation.Id);
            }
            if (match == null && wrong.Count > 0 && doc.GetElement(wrong[0]) is ViewSection first)
            {
                // Marker types with fewer views than directions (MaximumViewCount 1 in the Revit 2027 templates): turn the
                // marker so its view faces the requested direction.
                XYZ actual = first.ViewDirection.Negate();
                XYZ flatActual = new XYZ(actual.X, actual.Y, 0).Normalize();
                double angle = flatActual.AngleOnPlaneTo(looking, XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, marker.Id, Line.CreateBound(at, at + XYZ.BasisZ), angle);
                doc.Regenerate();
                XYZ turned = first.ViewDirection.Negate();
                tried.Add("rotated " + Math.Round(angle * 180 / Math.PI).ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", OutputJson.Direction(turned)));
                if (new XYZ(turned.X, turned.Y, 0).Normalize().IsAlmostEqualTo(looking, 1e-3))
                {
                    match = first;
                    wrong.RemoveAt(0);
                }
            }
            if (match != null)
            {
                if (wrong.Count > 0) doc.Delete(wrong);
                return match;
            }
            throw new OpException(ErrorCodes.RevitRefused, "Revit did not create an elevation looking " + direction + " from this marker.",
                new Dictionary<string, object> { ["apiMessage"] = "no marker index faces " + direction, ["tried"] = tried, ["max"] = count });
        }

        private static View Create3D(ChangeContext c)
        {
            Document doc = c.Doc;
            ViewFamilyType type = ViewKit.FamilyType(doc, ViewFamily.ThreeDimensional, c.Str("view_type"), c.Args.Field("view_type"), ElementTypeGroup.ViewType3D);
            bool perspective = c.Bool("perspective");
            View3D view = perspective ? View3D.CreatePerspective(doc, type.Id) : View3D.CreateIsometric(doc, type.Id);
            BoundingBoxXYZ fit = FitBox(c, "3d");
            if (c.Has("section_box"))
            {
                SetSectionBox(view, c.Args, "section_box");
            }
            else if (fit != null)
            {
                view.SetSectionBox(fit);
                view.IsSectionBoxActive = true;
            }
            if (c.Has("orient") || fit != null)
            {
                XYZ center = fit == null ? null : (fit.Min + fit.Max) / 2;
                double? distance = fit == null ? (double?)null : fit.Min.DistanceTo(fit.Max) * 1.5;
                ViewKit.Orient(view, c.Str("orient") ?? "iso_se", center, distance, c.Args.Field("orient"));
            }
            return view;
        }

        private static View CreateCallout(ChangeContext c)
        {
            Document doc = c.Doc;
            View parent = c.View("view");
            double[] region = c.Args.BoxFt("region");
            if (region.Length != 4) throw OpException.InvalidArgs(c.Args.Field("region"), "callout region is [x0,y0,x1,y1] mm in the parent view");
            ViewFamilyType type;
            if (c.Has("view_type"))
            {
                type = Resolve.ById<ViewFamilyType>(doc, c.Str("view_type")) ??
                       Resolve.Single(new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>(), t => t.Name, c.Str("view_type"), c.Args.Field("view_type"), "view type");
            }
            else
            {
                type = doc.GetElement(parent.GetTypeId()) as ViewFamilyType ?? ViewKit.FamilyType(doc, ViewFamily.Detail, null, "view_type");
            }
            XYZ p0, p1;
            if (ViewKit.IsPlanLike(parent))
            {
                double z = parent.GenLevel?.ProjectElevation ?? 0;
                p0 = new XYZ(region[0], region[1], z);
                p1 = new XYZ(region[2], region[3], z);
            }
            else
            {
                XYZ origin = parent.Origin, right = parent.RightDirection, up = parent.UpDirection;
                XYZ basePoint = origin - right.Multiply(origin.DotProduct(right)) - up.Multiply(origin.DotProduct(up));
                p0 = basePoint + right.Multiply(region[0]) + up.Multiply(region[1]);
                p1 = basePoint + right.Multiply(region[2]) + up.Multiply(region[3]);
            }
            return ViewSection.CreateCallout(doc, parent.Id, type.Id, p0, p1);
        }

        private static View CreateLegend(ChangeContext c)
        {
            Document doc = c.Doc;
            View source;
            if (c.Has("view"))
            {
                source = c.View("view");
                if (source.ViewType != ViewType.Legend) throw OpException.InvalidArgs(c.Args.Field("view"), "'" + source.Name + "' is not a legend; give a legend to copy");
            }
            else
            {
                source = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.ViewType == ViewType.Legend && !v.IsTemplate);
                if (source == null) throw new OpException(ErrorCodes.LegendNeedsSeed, "'" + doc.Title + "' has no legend to copy; the Revit API cannot create the first legend.");
            }
            ViewDuplicateOption option = source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate) ? ViewDuplicateOption.Duplicate : ViewDuplicateOption.WithDetailing;
            if (!source.CanViewBeDuplicated(option)) throw new OpException(ErrorCodes.UnsupportedOp, "The legend '" + source.Name + "' cannot be duplicated.");
            return doc.GetElement(source.Duplicate(option)) as View;
        }

        // ------------------------------------------------------------------------------------------------- duplicate

        [Op("edit_views.duplicate")]
        public static void Duplicate(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = c.View("view");
            string mode = c.Args.Enum("mode", "copy", "copy", "detailing", "dependent");
            ViewDuplicateOption option = mode == "dependent" ? ViewDuplicateOption.AsDependent : mode == "detailing" ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate;
            if (!view.CanViewBeDuplicated(option))
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + view.Name + "' (" + view.ViewType + ") cannot be duplicated as " + mode + ".",
                    new Dictionary<string, object> { ["view"] = view.Name, ["mode"] = mode });
            }
            View copy = doc.GetElement(view.Duplicate(option)) as View;
            c.Created(copy);
            if (c.Has("name")) ViewKit.Rename(copy, c.Str("name"), c.Args.Field("name"));
            var data = ViewKit.Brief(copy);
            if (c.Has("sheet"))
            {
                ViewSheet sheet = Resolve.Sheet(doc, c.Str("sheet"), c.Args.Field("sheet"));
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, copy.Id))
                {
                    throw new OpException(ErrorCodes.RevitRefused, "'" + copy.Name + "' cannot be placed on sheet " + sheet.SheetNumber + ".",
                        new Dictionary<string, object> { ["apiMessage"] = "Viewport.CanAddViewToSheet returned false" });
                }
                XYZ center = c.Has("at") ? c.Point("at") : SheetCenter(sheet);
                Viewport viewport = Viewport.Create(doc, sheet.Id, copy.Id, new XYZ(center.X, center.Y, 0));
                c.Created(viewport);
                c.Output("sheet", sheet.Id);
                data["sheet"] = sheet.SheetNumber;
                data["viewport"] = viewport.Id.Value;
            }
            c.Output("view", copy.Id);
            c.Data(data);
            c.Summary("duplicated '" + view.Name + "' as '" + copy.Name + "' (" + mode + ")" + (c.Has("sheet") ? " on sheet " + data["sheet"] : string.Empty));
        }

        // ---------------------------------------------------------------------------------------------------- rename

        [Op("edit_views.rename")]
        public static void Rename(ChangeContext c)
        {
            View view = c.View("view");
            string before = view.Name;
            ViewKit.Rename(view, c.Str("name"), c.Args.Field("name"));
            c.Modified(view, "name", before);
            c.Output("view", view.Id);
            c.Data(ViewKit.Brief(view));
            c.Summary("renamed '" + before + "' to '" + view.Name + "'");
        }

        // ------------------------------------------------------------------------------------------- create_template

        [Op("edit_views.create_template")]
        public static void CreateTemplate(ChangeContext c)
        {
            View view = c.View("view");
            if (view.IsTemplate) throw OpException.InvalidArgs(c.Args.Field("view"), "'" + view.Name + "' already is a view template");
            if (view is ViewSheet || view.ViewType == ViewType.Schedule)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "View templates cannot be made from " + view.ViewType + " views.");
            }
            string name = c.Str("name");
            if (string.IsNullOrWhiteSpace(name)) throw OpException.InvalidArgs(c.Args.Field("name"), "is required (the template name)");
            if (ViewKit.NameTaken(c.Doc, name)) throw ViewKit.NameTakenError(c.Doc, name, c.Args.Field("name"));
            View template = view.CreateViewTemplate();
            c.Created(template);
            ViewKit.Rename(template, name, c.Args.Field("name"));
            c.Output("view", template.Id);
            c.Output("template", template.Id);
            c.Data(ViewKit.Brief(template));
            c.Summary("created view template '" + template.Name + "' from '" + view.Name + "'");
        }

        // ---------------------------------------------------------------------------------------------------- delete

        [Op("edit_views.delete")]
        public static void Delete(ChangeContext c)
        {
            Document doc = c.Doc;
            List<string> names = c.Args.Strs("views");
            if (names.Count == 0) throw OpException.InvalidArgs(c.Args.Field("views"), "is required (view names or ids)");
            var views = new List<View>();
            foreach (string name in names)
            {
                View view = Resolve.View(doc, name, c.Args.Field("views"), c.Ctx.UiDoc, strict: true);
                if (views.All(v => v.Id != view.Id)) views.Add(view);
            }
            ElementId activeId = null;
            try { if (c.Ctx.DocIsActive) activeId = c.Ctx.UiDoc?.ActiveView?.Id; } catch { activeId = null; }
            View active = activeId == null ? null : views.FirstOrDefault(v => v.Id == activeId);
            if (active != null)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "'" + active.Name + "' is the active view in Revit and cannot be deleted; activate another view first.",
                    new Dictionary<string, object> { ["view"] = active.Name, ["alternative"] = "ui activate_view" });
            }
            // Ids before the delete: the View objects are invalid afterwards.
            List<ElementId> viewIds = views.Select(v => v.Id).ToList();
            ICollection<ElementId> deleted = c.Delete(viewIds);
            c.Data(new Dictionary<string, object> { ["views"] = viewIds.Select(id => (object)id.Value).ToList(), ["deleted_total"] = deleted.Count });
            c.Summary("deleted " + views.Count.ToString(CultureInfo.InvariantCulture) + " view" + (views.Count == 1 ? string.Empty : "s") +
                (deleted.Count > views.Count ? " (+" + (deleted.Count - views.Count).ToString(CultureInfo.InvariantCulture) + " dependent elements)" : string.Empty));
        }

        // --------------------------------------------------------------------------------------------------- helpers

        private static Level LowestLevel(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).FirstOrDefault();
        }

        private static double DefaultHeightFt(Document doc, Level level)
        {
            Level above = level == null ? null : Resolve.LevelAbove(doc, level);
            double height = above != null && level != null ? above.ProjectElevation - level.ProjectElevation : 0;
            return height > Units.MmToFt(500) ? height : Units.MmToFt(DefaultSectionHeightMm);
        }

        /// <summary>The union box (plus a margin) of ids/from elements, or null when neither is given.</summary>
        private static BoundingBoxXYZ FitBox(ChangeContext c, string kind)
        {
            if (!c.Has("ids") && !c.Has("from")) return null;
            List<Element> elements = ViewKit.SelectedElements(c.Ctx, c.Args, c.Doc);
            BoundingBoxXYZ box = ViewKit.UnionBox(elements, Units.MmToFt(FitMarginMm));
            if (box == null)
            {
                throw OpException.InvalidArgs(c.Has("ids") ? "ids" : "from", "none of the " + elements.Count.ToString(CultureInfo.InvariantCulture) + " elements has a 3D extent to fit the " + kind + " around");
            }
            return box;
        }

        private static XYZ LookingVector(string direction)
        {
            switch (direction)
            {
                case "south": return new XYZ(0, -1, 0);
                case "east": return new XYZ(1, 0, 0);
                case "west": return new XYZ(-1, 0, 0);
                default: return new XYZ(0, 1, 0);
            }
        }

        /// <summary>The plan that hosts an elevation marker: view=, else a floor plan of level=, else the first floor plan.</summary>
        private static ViewPlan HostPlan(ChangeContext c)
        {
            Document doc = c.Doc;
            if (c.Has("view"))
            {
                View view = c.View("view");
                if (view is ViewPlan plan && !plan.IsTemplate) return plan;
                throw OpException.InvalidArgs(c.Args.Field("view"), "'" + view.Name + "' is not a plan view; elevations are placed in plans");
            }
            Level level = c.Level("level", required: false);
            List<ViewPlan> plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(p => !p.IsTemplate && p.ViewType == ViewType.FloorPlan).ToList();
            ViewPlan host = level == null ? plans.OrderBy(p => p.GenLevel?.ProjectElevation ?? 0).FirstOrDefault() : plans.FirstOrDefault(p => p.GenLevel?.Id == level.Id);
            if (host == null)
            {
                throw new OpException(ErrorCodes.NotFound, "No floor plan" + (level == null ? string.Empty : " of " + level.Name) + " to place the elevation marker in.",
                    new Dictionary<string, object> { ["param"] = "view", ["kind"] = "floor plan", ["value"] = level?.Name, ["candidates"] = plans.Select(p => (object)p.Name).Take(8).ToList() });
            }
            return host;
        }

        internal static XYZ SheetCenter(ViewSheet sheet)
        {
            try
            {
                BoundingBoxUV outline = sheet.Outline;
                return new XYZ((outline.Min.U + outline.Max.U) / 2, (outline.Min.V + outline.Max.V) / 2, 0);
            }
            catch
            {
                return new XYZ(Units.MmToFt(420), Units.MmToFt(297), 0);
            }
        }
    }
}
