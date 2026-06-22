using System.Security.Principal;

namespace RevitMcpNext.Addin.Ipc
{
    internal static class PipeNameProvider
    {
        public static string GetDefaultPipeName()
        {
            string userSid = WindowsIdentity.GetCurrent()?.User?.Value ?? "unknown-user";
            return "revit-mcp-next-" + userSid.Replace("-", "_");
        }
    }
}

