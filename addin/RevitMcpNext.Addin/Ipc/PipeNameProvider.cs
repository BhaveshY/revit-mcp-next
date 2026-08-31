using System;
using System.Diagnostics;
using System.Text;

namespace RevitMcpNext.Addin.Ipc
{
    internal static class PipeNameProvider
    {
        public static string GetBasePipeName()
        {
            string configured = Environment.GetEnvironmentVariable("REVIT_MCP_NEXT_PIPE");
            return string.IsNullOrWhiteSpace(configured) ? "revit-mcp-next" : configured.Trim();
        }

        public static string CreateRuntimeInstanceId(string revitVersion)
        {
            string version = Sanitize(revitVersion);
            if (string.IsNullOrWhiteSpace(version)) version = "unknown";
            string configuredPrefix = Sanitize(Environment.GetEnvironmentVariable("REVIT_MCP_NEXT_INSTANCE_ID"));
            string prefix = string.IsNullOrWhiteSpace(configuredPrefix) ? "revit-" + version : configuredPrefix;
            return prefix + "-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public static string GetRuntimePipeName(string runtimeInstanceId)
        {
            string instanceId = Sanitize(runtimeInstanceId);
            if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("A runtime instance ID is required.", nameof(runtimeInstanceId));
            return GetBasePipeName() + "-" + instanceId;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var builder = new StringBuilder(Math.Min(64, value.Length));
            foreach (char character in value.Trim())
            {
                if (builder.Length >= 64) break;
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_') builder.Append(character);
            }
            return builder.ToString();
        }
    }
}
