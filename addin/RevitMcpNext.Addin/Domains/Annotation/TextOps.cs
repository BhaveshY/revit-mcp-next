using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin
{
    /// <summary>annotate text (D3 §3.5): a TextNote at a view point; width is the paper wrap width (mm), clamped to the type's limits.</summary>
    internal static class TextOps
    {
        [Op("annotate.text")]
        public static void Text(ChangeContext c)
        {
            Document doc = c.Doc;
            View view = AnnotationSupport.AnnotationView(c, "a text note", allowSheet: true, allow3D: true);
            string text = c.Str("text");
            if (string.IsNullOrWhiteSpace(text)) throw OpException.InvalidArgs("text", "is required (the note text)");
            // Revit separates paragraphs with \r.
            text = text.Replace("\r\n", "\r").Replace('\n', '\r');

            XYZ at = AnnotationSupport.OnPlane(AnnotationSupport.ViewPlane(view), c.Point("at"));
            TextNoteType type = AnnotationSupport.TypeOrDefault<TextNoteType>(c, "type", ElementTypeGroup.TextNoteType);
            var options = new TextNoteOptions(type.Id)
            {
                HorizontalAlignment = HorizontalTextAlignment.Left,
                Rotation = c.Has("rotation") ? c.Deg("rotation") : 0
            };

            TextNote note;
            double? width = c.Args.MmOpt("width");
            if (width.HasValue)
            {
                double min = TextElement.GetMinimumAllowedWidth(doc, type.Id);
                double max = TextElement.GetMaximumAllowedWidth(doc, type.Id);
                double clamped = Math.Max(min, Math.Min(max, width.Value));
                if (Math.Abs(clamped - width.Value) > 1e-9)
                {
                    c.Warn(WarningCodes.ValueClamped, "width " + Units.FtToMm(width.Value).ToString("0.#", CultureInfo.InvariantCulture) + " mm clamped to " +
                                                      Units.FtToMm(clamped).ToString("0.#", CultureInfo.InvariantCulture) + " mm (limits of '" + type.Name + "')");
                }
                note = TextNote.Create(doc, view.Id, at, clamped, text, options);
            }
            else
            {
                note = TextNote.Create(doc, view.Id, at, text, options);
            }
            if (note == null) throw c.Error(ErrorCodes.RevitRefused, "Revit did not create the text note in '" + view.Name + "'.");
            if (c.Bool("leader", false)) note.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);

            c.Created(note);
            c.Output("text", note.Id);
            c.Data(new Dictionary<string, object>
            {
                ["id"] = note.Id.Value,
                ["view"] = view.Name,
                ["type"] = type.Name,
                ["at"] = OutputJson.Point(note.Coord),
                ["width"] = Units.Mm(note.Width)
            });
            string preview = text.Length > 40 ? text.Substring(0, 40) + "..." : text;
            c.Summary("text note '" + preview.Replace('\r', ' ') + "' in " + AnnotationSupport.ViewLabel(view));
        }
    }
}
