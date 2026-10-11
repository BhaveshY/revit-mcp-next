using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// OverrideGraphicSettings helpers for view_graphics, get_view and capture highlights: building overrides from the
    /// shared args (color, fill, line_weight, fill_pattern, transparency, halftone), describing non-default overrides
    /// compactly, and the red highlight used by capture.
    /// </summary>
    internal static class GraphicsKit
    {
        /// <summary>The solid fill pattern element (drafting target preferred), or null when the document has none.</summary>
        public static FillPatternElement SolidFill(Document doc)
        {
            FillPatternElement best = null;
            foreach (FillPatternElement element in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                FillPattern pattern;
                try { pattern = element.GetFillPattern(); } catch { continue; }
                if (pattern == null || !pattern.IsSolidFill) continue;
                if (pattern.Target == FillPatternTarget.Drafting) return element;
                if (best == null) best = element;
            }
            return best;
        }

        /// <summary>True when any of the override args is present.</summary>
        public static bool HasOverrideArgs(PayloadReader args)
        {
            return args.Has("color") || args.Has("fill") || args.Has("line_weight") || args.Has("fill_pattern") || args.Has("transparency") || args.Has("halftone");
        }

        /// <summary>
        /// Applies the override args on top of <paramref name="start"/> (null = no overrides). <paramref name="defaultFill"/>
        /// is the fill default when a color is given without fill (elements/categories true, filters false).
        /// </summary>
        public static OverrideGraphicSettings Build(Document doc, OverrideGraphicSettings start, PayloadReader args, bool defaultFill, List<string> changed)
        {
            var ogs = start == null ? new OverrideGraphicSettings() : new OverrideGraphicSettings(start);
            Color color = args.Has("color") ? ViewKit.ParseColor(args.Str("color"), args.Field("color")) : null;
            bool fill = args.BoolOpt("fill") ?? defaultFill;

            if (color != null)
            {
                ogs.SetProjectionLineColor(color);
                ogs.SetCutLineColor(color);
                changed?.Add("color " + ViewKit.Hex(color));
            }

            ElementId patternId = null;
            if (args.Has("fill_pattern"))
            {
                patternId = Resolve.FillPattern(doc, args.Str("fill_pattern"), args.Field("fill_pattern")).Id;
                changed?.Add("fill_pattern " + args.Str("fill_pattern"));
            }
            bool wantsFill = patternId != null || (fill && color != null) || (args.Has("fill") && fill);
            if (wantsFill)
            {
                if (patternId == null)
                {
                    FillPatternElement solid = SolidFill(doc) ?? throw new OpException(ErrorCodes.NotFound, "The document has no solid fill pattern.",
                        new Dictionary<string, object> { ["param"] = "fill_pattern", ["kind"] = "fill pattern", ["value"] = "solid", ["candidates"] = new List<object>() });
                    patternId = solid.Id;
                }
                ogs.SetSurfaceForegroundPatternId(patternId);
                ogs.SetSurfaceForegroundPatternVisible(true);
                ogs.SetCutForegroundPatternId(patternId);
                ogs.SetCutForegroundPatternVisible(true);
                if (color != null)
                {
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetCutForegroundPatternColor(color);
                }
                if (changed != null && !changed.Any(s => s.StartsWith("fill_pattern", StringComparison.Ordinal))) changed.Add("fill");
            }
            else if (args.Has("fill") && !fill)
            {
                ogs.SetSurfaceForegroundPatternId(ElementId.InvalidElementId);
                ogs.SetCutForegroundPatternId(ElementId.InvalidElementId);
                changed?.Add("fill off");
            }

            if (args.Has("line_weight"))
            {
                int weight = args.Int("line_weight");
                if (weight < 1 || weight > 16) throw OpException.InvalidArgs(args.Field("line_weight"), "must be 1-16 (got " + weight.ToString(CultureInfo.InvariantCulture) + ")");
                ogs.SetProjectionLineWeight(weight);
                ogs.SetCutLineWeight(weight);
                changed?.Add("line_weight " + weight.ToString(CultureInfo.InvariantCulture));
            }
            if (args.Has("transparency"))
            {
                int transparency = (int)Math.Round(args.Num("transparency"));
                if (transparency < 0 || transparency > 100) throw OpException.InvalidArgs(args.Field("transparency"), "must be 0-100 (got " + transparency.ToString(CultureInfo.InvariantCulture) + ")");
                ogs.SetSurfaceTransparency(transparency);
                changed?.Add("transparency " + transparency.ToString(CultureInfo.InvariantCulture));
            }
            if (args.Has("halftone"))
            {
                bool halftone = args.Bool("halftone");
                ogs.SetHalftone(halftone);
                changed?.Add(halftone ? "halftone" : "halftone off");
            }
            return ogs;
        }

        /// <summary>A solid red (or given color) highlight: lines, surface and cut fill.</summary>
        public static OverrideGraphicSettings Highlight(Document doc, Color color)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetCutLineColor(color);
            ogs.SetProjectionLineWeight(5);
            ogs.SetCutLineWeight(5);
            FillPatternElement solid = SolidFill(doc);
            if (solid != null)
            {
                ogs.SetSurfaceForegroundPatternId(solid.Id);
                ogs.SetSurfaceForegroundPatternColor(color);
                ogs.SetSurfaceForegroundPatternVisible(true);
                ogs.SetCutForegroundPatternId(solid.Id);
                ogs.SetCutForegroundPatternColor(color);
                ogs.SetCutForegroundPatternVisible(true);
            }
            return ogs;
        }

        /// <summary>A compact description of the non-default parts of an override; null when nothing is overridden.</summary>
        public static Dictionary<string, object> Describe(Document doc, OverrideGraphicSettings ogs)
        {
            if (ogs == null) return null;
            var data = new Dictionary<string, object>();
            try
            {
                if (ogs.ProjectionLineColor != null && ogs.ProjectionLineColor.IsValid) data["color"] = ViewKit.Hex(ogs.ProjectionLineColor);
                if (ogs.ProjectionLineWeight != OverrideGraphicSettings.InvalidPenNumber) data["line_weight"] = ogs.ProjectionLineWeight;
                ElementId pattern = ogs.SurfaceForegroundPatternId;
                if (pattern != null && pattern != ElementId.InvalidElementId)
                {
                    data["fill_pattern"] = (doc.GetElement(pattern) as FillPatternElement)?.Name ?? pattern.Value.ToString(CultureInfo.InvariantCulture);
                    if (ogs.SurfaceForegroundPatternColor != null && ogs.SurfaceForegroundPatternColor.IsValid) data["fill"] = ViewKit.Hex(ogs.SurfaceForegroundPatternColor);
                    if (!ogs.IsSurfaceForegroundPatternVisible) data["fill_visible"] = false;
                }
                else if (ogs.SurfaceForegroundPatternColor != null && ogs.SurfaceForegroundPatternColor.IsValid)
                {
                    data["fill"] = ViewKit.Hex(ogs.SurfaceForegroundPatternColor);
                }
                if (ogs.CutLineColor != null && ogs.CutLineColor.IsValid && !data.ContainsKey("color")) data["cut_color"] = ViewKit.Hex(ogs.CutLineColor);
                if (ogs.Transparency > 0) data["transparency"] = ogs.Transparency;
                if (ogs.Halftone) data["halftone"] = true;
                if (ogs.ProjectionLinePatternId != null && ogs.ProjectionLinePatternId != ElementId.InvalidElementId)
                {
                    data["line_pattern"] = (doc.GetElement(ogs.ProjectionLinePatternId) as LinePatternElement)?.Name ?? ogs.ProjectionLinePatternId.Value.ToString(CultureInfo.InvariantCulture);
                }
                if (ogs.DetailLevel != ViewDetailLevel.Undefined) data["detail_level"] = ogs.DetailLevel.ToString().ToLowerInvariant();
            }
            catch
            {
                // Overrides that cannot be read are reported as empty.
            }
            return data.Count == 0 ? null : data;
        }
    }
}
