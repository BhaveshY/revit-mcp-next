using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Shared view helpers for the views/visual/ui lane (P-VIEWS-VISUAL): display styles, the 3D orientation presets
    /// (D1 §4B edit_views, D3 §3.1), colors, view family types, names, view-template control detection, crop boxes and
    /// the compact view descriptions used by get_view, capture and edit_views.
    /// </summary>
    internal static class ViewKit
    {
        public static readonly string[] OrientPresets = { "iso_se", "iso_sw", "iso_ne", "iso_nw", "top", "front", "back", "left", "right" };
        public static readonly string[] StyleNames = { "wireframe", "hidden", "shaded", "consistent", "realistic" };

        // ---------------------------------------------------------------------------------------------------------
        // Display styles
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>wireframe | hidden | shaded | consistent | realistic → DisplayStyle ("shaded" keeps edges on).</summary>
        public static DisplayStyle ParseStyle(string value, string param = "style")
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_"))
            {
                case "wireframe": return DisplayStyle.Wireframe;
                case "hidden":
                case "hidden_line":
                case "hlr": return DisplayStyle.HLR;
                case "shaded":
                case "shaded_edges":
                case "shading": return DisplayStyle.ShadingWithEdges;
                case "consistent":
                case "consistent_colors":
                case "flat": return DisplayStyle.FlatColors;
                case "realistic": return DisplayStyle.Realistic;
                default:
                    throw OpException.InvalidArgs(param, "must be one of " + string.Join(", ", StyleNames) + " (got '" + value + "')");
            }
        }

        public static string StyleName(DisplayStyle style)
        {
            switch (style)
            {
                case DisplayStyle.Wireframe: return "wireframe";
                case DisplayStyle.HLR: return "hidden";
                case DisplayStyle.Shading:
                case DisplayStyle.ShadingWithEdges: return "shaded";
                case DisplayStyle.FlatColors: return "consistent";
                case DisplayStyle.Realistic:
                case DisplayStyle.RealisticWithEdges:
                case DisplayStyle.Rendering: return "realistic";
                default: return style.ToString().ToLowerInvariant();
            }
        }

        /// <summary>Raster-like styles (shaded, consistent, realistic) favour JPEG output.</summary>
        public static bool IsShadedStyle(DisplayStyle style)
        {
            switch (style)
            {
                case DisplayStyle.Wireframe:
                case DisplayStyle.HLR:
                case DisplayStyle.Undefined:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>Sets a display style; "realistic" falls back to the older Rendering style when Revit refuses it.</summary>
        public static void SetStyle(View view, DisplayStyle style)
        {
            try
            {
                view.DisplayStyle = style;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) when (style == DisplayStyle.Realistic)
            {
                view.DisplayStyle = DisplayStyle.Rendering;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // 3D orientation presets
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The viewing direction (eye → target) of a preset. North is project +Y.</summary>
        public static XYZ Forward(string preset, string param = "orient")
        {
            switch ((preset ?? "iso_se").Trim().ToLowerInvariant())
            {
                case "iso_se": return new XYZ(-1, 1, -1).Normalize();
                case "iso_sw": return new XYZ(1, 1, -1).Normalize();
                case "iso_ne": return new XYZ(-1, -1, -1).Normalize();
                case "iso_nw": return new XYZ(1, -1, -1).Normalize();
                case "top": return new XYZ(0, 0, -1);
                case "front": return new XYZ(0, 1, 0);
                case "back": return new XYZ(0, -1, 0);
                case "left": return new XYZ(1, 0, 0);
                case "right": return new XYZ(-1, 0, 0);
                default:
                    throw OpException.InvalidArgs(param, "must be one of " + string.Join(", ", OrientPresets) + " (got '" + preset + "')");
            }
        }

        /// <summary>A ViewOrientation3D looking at <paramref name="target"/> from <paramref name="distanceFt"/> away.</summary>
        public static ViewOrientation3D Orientation(string preset, XYZ target, double distanceFt, string param = "orient")
        {
            XYZ forward = Forward(preset, param);
            XYZ up;
            if (Math.Abs(forward.Z) > 0.999) up = XYZ.BasisY;
            else up = (XYZ.BasisZ - forward.Multiply(XYZ.BasisZ.DotProduct(forward))).Normalize();
            double distance = distanceFt > 1 ? distanceFt : 100;
            XYZ eye = (target ?? XYZ.Zero) - forward.Multiply(distance);
            return new ViewOrientation3D(eye, up, forward);
        }

        /// <summary>Applies a preset to a 3D view around its section box (or the given center).</summary>
        public static void Orient(View3D view, string preset, XYZ center = null, double? distanceFt = null, string param = "orient")
        {
            if (view.IsLocked)
            {
                throw new OpException(ErrorCodes.UnsupportedOp, "The 3D view '" + view.Name + "' is locked, so its orientation cannot change.",
                    new Dictionary<string, object> { ["hint"] = "unlock the 3D view in Revit (or duplicate it) and retry" });
            }
            XYZ target = center;
            double distance = distanceFt ?? 0;
            if (target == null)
            {
                BoundingBoxXYZ box = null;
                try { if (view.IsSectionBoxActive) box = view.GetSectionBox(); } catch { box = null; }
                if (box != null)
                {
                    XYZ min = box.Transform.OfPoint(box.Min), max = box.Transform.OfPoint(box.Max);
                    target = (min + max) / 2;
                    distance = Math.Max(distance, min.DistanceTo(max) * 1.5);
                }
            }
            if (target == null)
            {
                try
                {
                    ViewOrientation3D current = view.GetOrientation();
                    target = current.EyePosition + current.ForwardDirection.Multiply(100);
                    distance = Math.Max(distance, 100);
                }
                catch
                {
                    target = XYZ.Zero;
                }
            }
            view.SetOrientation(Orientation(preset, target, distance, param));
        }

        public static string OrientName(View3D view)
        {
            try
            {
                XYZ f = view.GetOrientation().ForwardDirection;
                foreach (string preset in OrientPresets)
                {
                    if (Forward(preset).IsAlmostEqualTo(f, 1e-3)) return preset;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Colors
        // ---------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, int[]> NamedColors = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = new[] { 255, 0, 0 }, ["green"] = new[] { 0, 160, 0 }, ["blue"] = new[] { 0, 0, 255 },
            ["yellow"] = new[] { 255, 220, 0 }, ["orange"] = new[] { 255, 136, 0 }, ["purple"] = new[] { 128, 0, 160 },
            ["magenta"] = new[] { 255, 0, 255 }, ["pink"] = new[] { 255, 105, 180 }, ["cyan"] = new[] { 0, 200, 220 },
            ["teal"] = new[] { 0, 128, 128 }, ["lime"] = new[] { 120, 220, 0 }, ["navy"] = new[] { 0, 0, 128 },
            ["maroon"] = new[] { 128, 0, 0 }, ["olive"] = new[] { 128, 128, 0 }, ["brown"] = new[] { 140, 80, 20 },
            ["black"] = new[] { 0, 0, 0 }, ["white"] = new[] { 255, 255, 255 }, ["gray"] = new[] { 128, 128, 128 },
            ["grey"] = new[] { 128, 128, 128 }, ["silver"] = new[] { 192, 192, 192 }, ["lightgray"] = new[] { 211, 211, 211 },
            ["darkgray"] = new[] { 80, 80, 80 }, ["rot"] = new[] { 255, 0, 0 }, ["gruen"] = new[] { 0, 160, 0 },
            ["grün"] = new[] { 0, 160, 0 }, ["blau"] = new[] { 0, 0, 255 }, ["gelb"] = new[] { 255, 220, 0 }
        };

        /// <summary>#RRGGBB, RRGGBB, #RGB, "r,g,b" or a basic name.</summary>
        public static Color ParseColor(string value, string param = "color")
        {
            string text = (value ?? string.Empty).Trim();
            if (NamedColors.TryGetValue(text.Replace(" ", string.Empty), out int[] named)) return new Color((byte)named[0], (byte)named[1], (byte)named[2]);
            string hex = text.StartsWith("#", StringComparison.Ordinal) ? text.Substring(1) : text;
            if (hex.Length == 3 && hex.All(Uri.IsHexDigit)) hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
            if (hex.Length == 6 && hex.All(Uri.IsHexDigit))
            {
                return new Color(
                    byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            string[] parts = text.Trim('(', ')', '[', ']').Replace("rgb", string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                var channels = new byte[3];
                bool ok = true;
                for (int i = 0; i < 3 && ok; i++)
                {
                    ok = int.TryParse(parts[i].Trim('(', ')'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel) && channel >= 0 && channel <= 255;
                    if (ok) channels[i] = (byte)channel;
                }
                if (ok) return new Color(channels[0], channels[1], channels[2]);
            }
            throw OpException.InvalidArgs(param, "must be a color: #RRGGBB, 'r,g,b' or a name like red (got '" + value + "')");
        }

        public static string Hex(Color color)
        {
            if (color == null || !color.IsValid) return null;
            return "#" + color.Red.ToString("X2", CultureInfo.InvariantCulture) + color.Green.ToString("X2", CultureInfo.InvariantCulture) + color.Blue.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>A deterministic palette: Tableau-10, then an HSV spread for more values.</summary>
        public static Color PaletteColor(int index)
        {
            int[][] tableau =
            {
                new[] { 78, 121, 167 }, new[] { 242, 142, 43 }, new[] { 225, 87, 89 }, new[] { 118, 183, 178 }, new[] { 89, 161, 79 },
                new[] { 237, 201, 72 }, new[] { 176, 122, 161 }, new[] { 255, 157, 167 }, new[] { 156, 117, 95 }, new[] { 186, 176, 172 }
            };
            if (index >= 0 && index < tableau.Length) return new Color((byte)tableau[index][0], (byte)tableau[index][1], (byte)tableau[index][2]);
            double hue = ((index - tableau.Length) * 137.508) % 360.0;
            double saturation = 0.65, value = (index % 2 == 0) ? 0.85 : 0.7;
            double c = value * saturation, x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1)), m = value - c;
            double r, g, b;
            if (hue < 60) { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return new Color((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        // ---------------------------------------------------------------------------------------------------------
        // View family types, names
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The named view family type of a family, else the document default, else the first of the family.</summary>
        public static ViewFamilyType FamilyType(Document doc, ViewFamily family, string name = null, string param = "view_type", ElementTypeGroup? defaultGroup = null)
        {
            List<ViewFamilyType> types = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == family).ToList();
            if (!string.IsNullOrWhiteSpace(name))
            {
                ViewFamilyType byId = Resolve.ById<ViewFamilyType>(doc, name);
                if (byId != null && byId.ViewFamily == family) return byId;
                return Resolve.Single(types, t => t.Name, name, param, family + " view type");
            }
            if (defaultGroup.HasValue)
            {
                try
                {
                    ElementId id = doc.GetDefaultElementTypeId(defaultGroup.Value);
                    if (id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is ViewFamilyType byDefault && byDefault.ViewFamily == family) return byDefault;
                }
                catch
                {
                    // Fall back to the first type of the family.
                }
            }
            ViewFamilyType first = types.FirstOrDefault();
            if (first == null)
            {
                throw new OpException(ErrorCodes.NotFound, "The document has no " + family + " view type.",
                    new Dictionary<string, object> { ["param"] = param, ["kind"] = "view type", ["value"] = family.ToString(), ["candidates"] = new List<object>() });
            }
            return first;
        }

        /// <summary>True when another view (or sheet) already carries this name.</summary>
        public static bool NameTaken(Document doc, string name, ElementId except = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string text = name.Trim();
            return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Any(v => (except == null || v.Id != except) && string.Equals(v.Name, text, StringComparison.OrdinalIgnoreCase) && !(v is ViewSheet));
        }

        /// <summary>A free variant "Name (2)" for NAME_TAKEN fixes.</summary>
        public static string NextFreeName(Document doc, string name)
        {
            for (int i = 2; i < 200; i++)
            {
                string candidate = name.Trim() + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                if (!NameTaken(doc, candidate)) return candidate;
            }
            return name.Trim() + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        public static OpException NameTakenError(Document doc, string name, string param = "name")
        {
            return new OpException(ErrorCodes.NameTaken, "A view named '" + name + "' already exists.",
                new Dictionary<string, object> { ["param"] = param, ["value"] = name, ["next"] = NextFreeName(doc, name) });
        }

        /// <summary>Renames after the uniqueness check (NAME_TAKEN with a free suggestion).</summary>
        public static void Rename(View view, string name, string param = "name")
        {
            string text = (name ?? string.Empty).Trim();
            if (text.Length == 0) throw OpException.InvalidArgs(param, "must not be empty");
            if (string.Equals(view.Name, text, StringComparison.Ordinal)) return;
            if (!(view is ViewSheet) && NameTaken(view.Document, text, view.Id)) throw NameTakenError(view.Document, text, param);
            try
            {
                view.Name = text;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw OpException.InvalidArgs(param, "Revit refused the name '" + text + "': " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // View templates
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The template applied to the view (null for templates themselves and views without one).</summary>
        public static View TemplateOf(View view)
        {
            if (view == null || view.IsTemplate) return null;
            ElementId id;
            try { id = view.ViewTemplateId; } catch { return null; }
            if (id == null || id == ElementId.InvalidElementId) return null;
            return view.Document.GetElement(id) as View;
        }

        /// <summary>True when the view's template controls any of these parameters.</summary>
        public static bool TemplateControls(View view, params BuiltInParameter[] parameters)
        {
            View template = TemplateOf(view);
            if (template == null) return false;
            try
            {
                var controllable = new HashSet<long>(template.GetTemplateParameterIds().Select(id => id.Value));
                var excluded = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(id => id.Value));
                return parameters.Any(p => controllable.Contains((long)p) && !excluded.Contains((long)p));
            }
            catch
            {
                return false;
            }
        }

        public static OpException TemplateControlled(View view, string what)
        {
            View template = TemplateOf(view);
            string name = template?.Name ?? "its view template";
            return new OpException(ErrorCodes.TemplateControlled,
                "The view template '" + name + "' controls " + what + " of '" + view.Name + "'.",
                new Dictionary<string, object> { ["template"] = template?.Name, ["view"] = view.Name, ["setting"] = what });
        }

        /// <summary>Throws TEMPLATE_CONTROLLED when the view's template controls the parameters.</summary>
        public static void EnsureNotTemplateControlled(View view, string what, params BuiltInParameter[] parameters)
        {
            if (TemplateControls(view, parameters)) throw TemplateControlled(view, what);
        }

        /// <summary>The V/G parameter that governs a category's visibility/overrides in templates.</summary>
        public static BuiltInParameter VisibilityParameterFor(Category category)
        {
            try
            {
                switch (category.CategoryType)
                {
                    case CategoryType.Annotation: return BuiltInParameter.VIS_GRAPHICS_ANNOTATION;
                    case CategoryType.AnalyticalModel: return BuiltInParameter.VIS_GRAPHICS_ANALYTICAL_MODEL;
                    default:
                        if (Resolve.SafeBuiltIn(category) == BuiltInCategory.INVALID && category.Id.Value > 0) return BuiltInParameter.VIS_GRAPHICS_IMPORT;
                        return BuiltInParameter.VIS_GRAPHICS_MODEL;
                }
            }
            catch
            {
                return BuiltInParameter.VIS_GRAPHICS_MODEL;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Descriptions
        // ---------------------------------------------------------------------------------------------------------

        public static int? ScaleOf(View view)
        {
            try
            {
                if (view is ViewSheet || view is ViewSchedule) return null;
                int scale = view.Scale;
                return scale > 0 ? scale : (int?)null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>{id, name, type, scale} of a view.</summary>
        public static Dictionary<string, object> Brief(View view)
        {
            var data = new Dictionary<string, object>
            {
                ["id"] = view.Id.Value,
                ["name"] = view.Name,
                ["type"] = view.ViewType.ToString()
            };
            int? scale = ScaleOf(view);
            if (scale.HasValue) data["scale"] = scale.Value;
            if (view is ViewSheet sheet) data["number"] = sheet.SheetNumber;
            if (view.IsTemplate) data["template"] = true;
            return data;
        }

        /// <summary>"Level 1 (FloorPlan 1:100)".</summary>
        public static string Title(View view)
        {
            int? scale = ScaleOf(view);
            string name = view is ViewSheet sheet ? sheet.SheetNumber + " - " + sheet.Name : view.Name;
            return name + " (" + view.ViewType + (scale.HasValue ? " 1:" + scale.Value.ToString(CultureInfo.InvariantCulture) : string.Empty) + ")";
        }

        /// <summary>Views that can be drawn (not templates, schedules, browsers or internal views).</summary>
        public static bool IsGraphical(View view)
        {
            if (view == null || view.IsTemplate) return false;
            switch (view.ViewType)
            {
                case ViewType.Schedule:
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Internal:
                case ViewType.Undefined:
                case ViewType.PanelSchedule:
                case ViewType.ColumnSchedule:
                    return false;
                default:
                    // Report views (Report, CostReport, LoadsReport, pressure loss, systems analysis) differ per year.
                    return !view.ViewType.ToString().EndsWith("Report", StringComparison.Ordinal);
            }
        }

        /// <summary>Plan-like views whose crop box maps to model XY.</summary>
        public static bool IsPlanLike(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                    return true;
                case ViewType.Detail:
                    return view is ViewPlan;
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Crop boxes
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A crop box for the rectangle [a0,b0,a1,b1] (feet). Plans: model XY. Sections/elevations: a = coordinate along
        /// the view's right direction (model point · right), b = elevation (z). The view's own crop transform is kept.
        /// </summary>
        public static BoundingBoxXYZ CropFor(View view, double a0, double b0, double a1, double b1)
        {
            BoundingBoxXYZ current = view.CropBox;
            Transform transform = current?.Transform ?? Transform.Identity;
            XYZ p0, p1;
            if (IsPlanLike(view) || view.ViewType == ViewType.DraftingView)
            {
                p0 = new XYZ(Math.Min(a0, a1), Math.Min(b0, b1), transform.Origin.Z);
                p1 = new XYZ(Math.Max(a0, a1), Math.Max(b0, b1), transform.Origin.Z);
            }
            else
            {
                XYZ right = transform.BasisX, up = transform.BasisY;
                p0 = right.Multiply(Math.Min(a0, a1)) + up.Multiply(Math.Min(b0, b1));
                p1 = right.Multiply(Math.Max(a0, a1)) + up.Multiply(Math.Max(b0, b1));
            }
            Transform inverse = transform.Inverse;
            XYZ l0 = inverse.OfPoint(p0), l1 = inverse.OfPoint(p1);
            var box = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(Math.Min(l0.X, l1.X), Math.Min(l0.Y, l1.Y), current?.Min.Z ?? -1),
                Max = new XYZ(Math.Max(l0.X, l1.X), Math.Max(l0.Y, l1.Y), current?.Max.Z ?? 1)
            };
            return box;
        }

        /// <summary>The crop rectangle in model XY (feet) when the crop transform is axis-aligned; null otherwise.</summary>
        public static double[] CropRectXY(View view)
        {
            try
            {
                BoundingBoxXYZ crop = view.CropBox;
                if (crop == null) return null;
                Transform t = crop.Transform ?? Transform.Identity;
                if (!t.BasisX.IsAlmostEqualTo(XYZ.BasisX, 1e-6) || !t.BasisY.IsAlmostEqualTo(XYZ.BasisY, 1e-6)) return null;
                XYZ a = t.OfPoint(crop.Min), b = t.OfPoint(crop.Max);
                return new[] { Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y) };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Width/height of the crop region (any view type), or null.</summary>
        public static double? CropAspect(View view)
        {
            try
            {
                BoundingBoxXYZ crop = view.CropBox;
                if (crop == null) return null;
                double w = Math.Abs(crop.Max.X - crop.Min.X), h = Math.Abs(crop.Max.Y - crop.Min.Y);
                if (w < 1e-6 || h < 1e-6) return null;
                return w / h;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A model box [x0,y0,z0,x1,y1,z1] in feet as BoundingBoxXYZ (corners sorted).</summary>
        public static BoundingBoxXYZ ModelBox(double[] ft)
        {
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(ft[0], ft[3]), Math.Min(ft[1], ft[4]), Math.Min(ft[2], ft[5])),
                Max = new XYZ(Math.Max(ft[0], ft[3]), Math.Max(ft[1], ft[4]), Math.Max(ft[2], ft[5]))
            };
        }

        /// <summary>The union bounding box of elements in model coordinates (null when none has extents).</summary>
        public static BoundingBoxXYZ UnionBox(IEnumerable<Element> elements, double marginFt)
        {
            XYZ min = null, max = null;
            foreach (Element element in elements)
            {
                BoundingBoxXYZ box = null;
                try { box = element.get_BoundingBox(null); } catch { box = null; }
                if (box == null) continue;
                Transform t = box.Transform ?? Transform.Identity;
                foreach (XYZ corner in Corners(box))
                {
                    XYZ p = t.OfPoint(corner);
                    min = min == null ? p : Geometry.Min(min, p);
                    max = max == null ? p : Geometry.Max(max, p);
                }
            }
            if (min == null) return null;
            XYZ pad = new XYZ(marginFt, marginFt, marginFt);
            return new BoundingBoxXYZ { Min = min - pad, Max = max + pad };
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            XYZ a = box.Min, b = box.Max;
            yield return new XYZ(a.X, a.Y, a.Z);
            yield return new XYZ(b.X, a.Y, a.Z);
            yield return new XYZ(a.X, b.Y, a.Z);
            yield return new XYZ(b.X, b.Y, a.Z);
            yield return new XYZ(a.X, a.Y, b.Z);
            yield return new XYZ(b.X, a.Y, b.Z);
            yield return new XYZ(a.X, b.Y, b.Z);
            yield return new XYZ(b.X, b.Y, b.Z);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Element selectors shared by ui, capture and view_graphics (ids | from | filter)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The elements named by ids, from (r# expanded by the broker, or "selection"/"last") or filter{}.</summary>
        public static List<Element> SelectedElements(RequestContext ctx, PayloadReader args, Document doc, bool required = true, int limit = 100000)
        {
            if (args.Has("ids")) return Resolve.Elements(doc, args, "ids", ctx);
            if (args.Has("from")) return Resolve.Elements(doc, args, "from", ctx);
            PayloadReader filter = args.Sub("filter");
            if (filter != null)
            {
                ElementQuery query = ElementQuery.Compile(doc, filter, ctx, ctx.UiDoc);
                if (query.IsLink) throw new OpException(ErrorCodes.LinkReadOnly, "filter.link selects elements of a linked model, which this op cannot use.",
                    new Dictionary<string, object> { ["link"] = query.LinkName });
                return query.Ids(limit, ctx).Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
            }
            if (required) throw OpException.InvalidArgs("ids", "give ids, from (r# handle, 'selection' or 'last') or filter");
            return new List<Element>();
        }

        public static bool HasSelector(PayloadReader args) => args.Has("ids") || args.Has("from") || args.Has("filter");

        /// <summary>Category ids for the category param ("annotations" handled by callers).</summary>
        public static List<Category> Categories(Document doc, PayloadReader args, string key = "category")
        {
            var result = new List<Category>();
            foreach (string name in args.Strs(key))
            {
                Category category = Resolve.Category(doc, name, args.Field(key));
                if (result.All(c => c.Id != category.Id)) result.Add(category);
            }
            return result;
        }
    }
}
