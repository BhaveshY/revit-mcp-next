using System;

namespace RevitMcpNext.Addin.Ipc
{
    internal static class PipeNameProvider
    {
        public static string GetDefaultPipeName()
        {
            string configured = Environment.GetEnvironmentVariable("REVIT_MCP_NEXT_PIPE");
            return string.IsNullOrWhiteSpace(configured) ? "revit-mcp-next" : configured.Trim();
        }
    }
}
