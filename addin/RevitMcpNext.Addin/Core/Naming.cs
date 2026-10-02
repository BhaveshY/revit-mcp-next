using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitMcpNext.Addin
{
    /// <summary>Transaction-name classes (SPEC §4.4).</summary>
    internal enum NameClass
    {
        /// <summary>Not ours.</summary>
        Foreign = 0,
        /// <summary>MCP &lt;wtag&gt; &lt;tool&gt;.&lt;op&gt; | change_set ... | edit_family.&lt;op&gt; | undo.compensate.</summary>
        Write = 1,
        /// <summary>MCP temp &lt;purpose&gt; - always rolled back; ignored by generations, stamps and the change ring.</summary>
        Temp = 2,
        /// <summary>MCP ui &lt;op&gt; - temporary view modes; undo treats them as ours and harmless.</summary>
        Ui = 3
    }

    /// <summary>A parsed transaction name.</summary>
    internal sealed class ParsedName
    {
        public NameClass Class { get; set; }
        /// <summary>Write class: the broker write tag (w17).</summary>
        public string WriteTag { get; set; }
        /// <summary>Write class: everything after the tag (registry key or "change_set ...").</summary>
        public string Body { get; set; }
        /// <summary>Temp class: the purpose (preview, capture, probe, run_csharp, export.ifc, schedule_fields).</summary>
        public string Purpose { get; set; }
        /// <summary>Ui class: the ui op.</summary>
        public string UiOp { get; set; }
    }

    /// <summary>
    /// The only source of MCP transaction and group names, and their parser (SPEC §4.4; mirrors contracts/src/naming.ts).
    /// Only WriteScope/TempScope (and the ui helper) create names; nothing else writes free-form transaction names.
    /// </summary>
    internal static class Naming
    {
        public const string Prefix = "MCP ";
        /// <summary>Tag used when a write arrives without a broker write tag (in-process callers, raw pipe tests).</summary>
        public const string FallbackWriteTag = "w0";

        public static class Purposes
        {
            public const string Preview = "preview";
            public const string Capture = "capture";
            public const string Probe = "probe";
            public const string RunCSharp = "run_csharp";
            public const string ExportIfc = "export.ifc";
            public const string ScheduleFields = "schedule_fields";
        }

        private static readonly Regex Parser = new Regex(@"^MCP (?:(w\d+) (.+)|temp (.+)|ui (\S+))$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex WriteTagShape = new Regex(@"^w\d+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>"MCP w17 create_elements.wall" (a single tool call).</summary>
        public static string Write(string writeTag, string key)
        {
            return Prefix + NormalizeTag(writeTag) + " " + Clean(key);
        }

        /// <summary>"MCP w17 change_set &lt;name&gt; (3 ops)" or "MCP w17 change_set (3 ops)".</summary>
        public static string ChangeSet(string writeTag, string name, int opCount)
        {
            string label = string.IsNullOrWhiteSpace(name) ? string.Empty : Clean(name) + " ";
            return Prefix + NormalizeTag(writeTag) + " change_set " + label + "(" +
                   opCount.ToString(CultureInfo.InvariantCulture) + (opCount == 1 ? " op)" : " ops)");
        }

        /// <summary>"MCP w18 edit_family.add_param" (inside the family document).</summary>
        public static string FamilyWrite(string writeTag, string op)
        {
            return Prefix + NormalizeTag(writeTag) + " edit_family." + Clean(op);
        }

        /// <summary>"MCP w19 undo.compensate".</summary>
        public static string Compensation(string writeTag)
        {
            return Prefix + NormalizeTag(writeTag) + " undo.compensate";
        }

        /// <summary>"MCP temp &lt;purpose&gt;".</summary>
        public static string Temp(string purpose)
        {
            return Prefix + "temp " + Clean(string.IsNullOrWhiteSpace(purpose) ? Purposes.Probe : purpose);
        }

        /// <summary>"MCP ui &lt;op&gt;" (op has no spaces).</summary>
        public static string Ui(string op)
        {
            string clean = Clean(string.IsNullOrWhiteSpace(op) ? "op" : op).Replace(' ', '_');
            return Prefix + "ui " + clean;
        }

        public static ParsedName Parse(string name)
        {
            var parsed = new ParsedName { Class = NameClass.Foreign };
            if (string.IsNullOrEmpty(name)) return parsed;
            Match match = Parser.Match(name);
            if (!match.Success) return parsed;
            if (match.Groups[1].Success)
            {
                parsed.Class = NameClass.Write;
                parsed.WriteTag = match.Groups[1].Value;
                parsed.Body = match.Groups[2].Value;
            }
            else if (match.Groups[3].Success)
            {
                parsed.Class = NameClass.Temp;
                parsed.Purpose = match.Groups[3].Value;
            }
            else
            {
                parsed.Class = NameClass.Ui;
                parsed.UiOp = match.Groups[4].Value;
            }
            return parsed;
        }

        public static NameClass Classify(string name)
        {
            return Parse(name).Class;
        }

        /// <summary>True for any name that starts with "MCP " (the FailuresProcessing safety net acts on these).</summary>
        public static bool IsMcpName(string name)
        {
            return name != null && name.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when every name is temp (or the list is empty while a TempScope is active). DocumentChanged handlers use
        /// this to ignore our own throw-away transactions.
        /// </summary>
        public static bool AllTemp(ICollection<string> names)
        {
            if (names == null || names.Count == 0) return TempScope.IsActive;
            foreach (string name in names)
            {
                if (Classify(name) != NameClass.Temp) return false;
            }
            return true;
        }

        /// <summary>Write when any name is a write, else Ui when any is ui, else Temp when all are temp, else Foreign.</summary>
        public static NameClass ClassifyAll(ICollection<string> names)
        {
            if (names == null || names.Count == 0) return NameClass.Foreign;
            bool anyUi = false, allTemp = true;
            foreach (string name in names)
            {
                NameClass cls = Classify(name);
                if (cls == NameClass.Write) return NameClass.Write;
                if (cls == NameClass.Ui) anyUi = true;
                if (cls != NameClass.Temp) allTemp = false;
            }
            if (anyUi) return NameClass.Ui;
            return allTemp ? NameClass.Temp : NameClass.Foreign;
        }

        public static string NormalizeTag(string writeTag)
        {
            string tag = (writeTag ?? string.Empty).Trim();
            return WriteTagShape.IsMatch(tag) ? tag : FallbackWriteTag;
        }

        private static string Clean(string text)
        {
            string value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length > 120 ? value.Substring(0, 120) : value;
        }
    }
}
