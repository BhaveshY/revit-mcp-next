using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Addin.Ipc;

namespace RevitMcpNext.Addin.Code
{
    /// <summary>One run_csharp call as recorded in &lt;home&gt;\logs\code-exec-audit.jsonl (SPEC §11.10).</summary>
    internal sealed class CodeAuditEntry
    {
        public string Client { get; set; }
        public string Session { get; set; }
        public string RequestId { get; set; }
        public string WriteTag { get; set; }
        public int RevitYear { get; set; }
        public string DocKey { get; set; }
        public string DocTitle { get; set; }
        public string Mode { get; set; }
        public string CodeSha256 { get; set; }
        public string ArgsSha256 { get; set; }
        public string Token { get; set; }
        /// <summary>not_required | preapproved | granted | declined | blocked | pending.</summary>
        public string Consent { get; set; }
        public long DurationMs { get; set; }
        /// <summary>ok | not_applied | error.</summary>
        public string Outcome { get; set; } = "error";
        public string Error { get; set; }
        public string ErrorMessage { get; set; }
        public int Added { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        public int Warnings { get; set; }
        public string Compiler { get; set; }
        /// <summary>Started when the call arrived (durationMs).</summary>
        public System.Diagnostics.Stopwatch Watch { get; } = System.Diagnostics.Stopwatch.StartNew();
    }

    /// <summary>
    /// The append-only audit of code execution: one JSON line per call (including refused, failed and declined calls)
    /// plus the source text in &lt;home&gt;\logs\code\&lt;sha256&gt;.cs. Failures to write the audit are logged, never thrown.
    /// </summary>
    internal static class CodeAudit
    {
        private static readonly object Gate = new object();

        public static void Write(McpHome home, CodeAuditEntry entry, string code)
        {
            if (home == null || entry == null) return;
            try
            {
                var line = new Dictionary<string, object>
                {
                    ["ts"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["client"] = entry.Client,
                    ["session"] = entry.Session,
                    ["requestId"] = entry.RequestId,
                    ["writeTag"] = entry.WriteTag,
                    ["revitYear"] = entry.RevitYear,
                    ["docKey"] = entry.DocKey,
                    ["docTitle"] = entry.DocTitle,
                    ["mode"] = entry.Mode,
                    ["codeSha256"] = entry.CodeSha256,
                    ["argsSha256"] = entry.ArgsSha256,
                    ["token"] = entry.Token,
                    ["consent"] = entry.Consent,
                    ["durationMs"] = entry.DurationMs,
                    ["outcome"] = entry.Outcome,
                    ["error"] = entry.Error,
                    ["errorMessage"] = entry.ErrorMessage,
                    ["changes"] = new Dictionary<string, object> { ["added"] = entry.Added, ["modified"] = entry.Modified, ["deleted"] = entry.Deleted },
                    ["warnings"] = entry.Warnings,
                    ["compiler"] = entry.Compiler
                };
                string json = JsonWireCodec.Serialize(line);
                lock (Gate)
                {
                    Directory.CreateDirectory(home.LogsDir);
                    File.AppendAllText(home.CodeAuditLog, json + "\n", new UTF8Encoding(false));
                    if (!string.IsNullOrWhiteSpace(entry.CodeSha256))
                    {
                        string source = home.CodeSourceFile(entry.CodeSha256);
                        if (!File.Exists(source))
                        {
                            Directory.CreateDirectory(home.CodeLogDir);
                            File.WriteAllText(source, code ?? string.Empty, new UTF8Encoding(false));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("code", "Writing the code-execution audit failed.", ex);
            }
        }

        /// <summary>sha256 of the canonical JSON of the script args (keys sorted at every level).</summary>
        public static string ArgsSha(IDictionary<string, object> args)
        {
            try
            {
                return ScriptSource.Sha256(JsonWireCodec.Serialize(Canonical(args ?? new Dictionary<string, object>())));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static object Canonical(object value)
        {
            switch (value)
            {
                case IDictionary<string, object> map:
                {
                    var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object> pair in map) sorted[pair.Key] = Canonical(pair.Value);
                    return sorted.ToDictionary(pair => pair.Key, pair => pair.Value);
                }
                case string text:
                    return text;
                case IEnumerable list:
                    return list.Cast<object>().Select(Canonical).ToList();
                default:
                    return value;
            }
        }
    }
}
