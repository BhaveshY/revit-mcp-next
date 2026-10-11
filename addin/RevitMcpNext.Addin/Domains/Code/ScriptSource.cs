using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitMcpNext.Addin.Code
{
    /// <summary>
    /// Builds the compilation unit for a run_csharp body (SPEC §11.3-§11.4):
    /// <code>
    /// public static class __McpScript {
    ///     public static object Run(Document doc, UIDocument uidoc, Application app, IDictionary&lt;string, object&gt; args, Action&lt;object&gt; log) { &lt;user code&gt; }
    /// }
    /// public static class __McpGuard { DeadlineTicks, Cancel, Depth, Tick(), Enter(), Exit() }
    /// </code>
    /// The user code sits under <c>#line 1 "user.cs"</c>, so compiler diagnostics and runtime stack traces (portable PDB)
    /// carry user-code lines. Leading <c>using X;</c> lines are hoisted to the top (blank lines keep the numbering) and a
    /// lone expression without ';' becomes <c>return (object)(expr);</c>. The guard fields are set by reflection, so the
    /// script references nothing of the add-in.
    /// </summary>
    internal static class ScriptSource
    {
        public const string ScriptType = "__McpScript";
        public const string GuardType = "__McpGuard";
        public const string TimeoutType = "__McpTimeoutException";
        public const string RunMethod = "Run";
        public const int MaxDepth = 256;

        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "Autodesk.Revit.DB",
            "Autodesk.Revit.DB.Architecture",
            "Autodesk.Revit.DB.Structure",
            "Autodesk.Revit.UI"
        };

        // using System.IO;   using static System.Math;   using Env = System.Environment;
        private static readonly Regex UsingLine = new Regex(
            @"^\s*using\s+(?:static\s+)?(?:[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*\s*;\s*$",
            RegexOptions.CultureInvariant);

        /// <summary>The generated source; <paramref name="expressionMode"/> tells whether the code was a lone expression.</summary>
        public static string Build(string userCode, out bool expressionMode)
        {
            string code = (userCode ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = code.Split('\n');
            var hoisted = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!UsingLine.IsMatch(lines[i])) break;
                hoisted.Add(trimmed);
                lines[i] = string.Empty;
            }
            string body = string.Join("\n", lines);
            expressionMode = IsLoneExpression(body);

            var sb = new StringBuilder(body.Length + 2048);
            foreach (string ns in DefaultUsings) sb.Append("using ").Append(ns).Append(";\n");
            foreach (string line in hoisted) sb.Append(line).Append('\n');
            sb.Append("public static class ").Append(ScriptType).Append("\n{\n");
            sb.Append("    public static object ").Append(RunMethod)
              .Append("(Document doc, UIDocument uidoc, Autodesk.Revit.ApplicationServices.Application app, IDictionary<string, object> args, Action<object> log)\n    {\n");
            sb.Append("#line 1 \"user.cs\"\n");
            // The closing ");" goes on its own line so a trailing // comment cannot swallow it; leading blank lines stay
            // inside the parentheses so user line numbers do not shift.
            if (expressionMode) sb.Append("return (object)(").Append(body.TrimEnd()).Append("\n);\n");
            else sb.Append(body).Append('\n');
            sb.Append("#line hidden\n");
            sb.Append("        return null;\n    }\n}\n");
            sb.Append(GuardSource);
            return sb.ToString();
        }

        /// <summary>sha256 (lower hex) of the code exactly as received.</summary>
        public static string Sha256(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>The first <paramref name="count"/> lines of the code (consent prompt).</summary>
        public static string Head(string code, int count)
        {
            string[] lines = (code ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            if (lines.Length <= count) return string.Join("\n", lines);
            var head = new string[count];
            Array.Copy(lines, head, count);
            return string.Join("\n", head) + "\n... (" + (lines.Length - count).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more lines)";
        }

        /// <summary>A single expression: no ';', no braces, one logical statement (e.g. <c>doc.Title</c>).</summary>
        private static bool IsLoneExpression(string body)
        {
            string text = StripComments(body).Trim();
            if (text.Length == 0) return false;
            if (text.IndexOf(';') >= 0 || text.IndexOf('{') >= 0 || text.IndexOf('}') >= 0) return false;
            if (Regex.IsMatch(text, @"^(return|throw|var|if|for|foreach|while|do|switch|try|using|goto|break|continue)\b", RegexOptions.CultureInvariant)) return false;
            return true;
        }

        private static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (string line in text.Split('\n'))
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(comment >= 0 ? line.Substring(0, comment) : line).Append('\n');
            }
            return sb.ToString();
        }

        private const string GuardSource =
            "public static class __McpGuard\n" +
            "{\n" +
            "    public static long DeadlineTicks;\n" +
            "    public static volatile bool Cancel;\n" +
            "    [System.ThreadStatic] public static int Depth;\n" +
            "    public static int MaxDepth = 256;\n" +
            "    public static void Tick()\n" +
            "    {\n" +
            "        if (Cancel) throw new __McpTimeoutException(\"The script was cancelled.\");\n" +
            "        if (DeadlineTicks != 0 && System.DateTime.UtcNow.Ticks > DeadlineTicks) throw new __McpTimeoutException(\"The script exceeded its time limit.\");\n" +
            "    }\n" +
            "    public static void Enter()\n" +
            "    {\n" +
            "        Tick();\n" +
            "        if (++Depth > MaxDepth) { Depth--; throw new System.InsufficientExecutionStackException(\"The script nested calls deeper than \" + MaxDepth + \" levels (runaway recursion?).\"); }\n" +
            "    }\n" +
            "    public static void Exit()\n" +
            "    {\n" +
            "        if (Depth > 0) Depth--;\n" +
            "    }\n" +
            "}\n" +
            "public sealed class __McpTimeoutException : System.Exception\n" +
            "{\n" +
            "    public __McpTimeoutException(string message) : base(message) { }\n" +
            "}\n";
    }
}
