using System;
using System.Globalization;
using System.Security.Cryptography;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    /// <summary>
    /// Instance ids and pipe names (SPEC §4.6.1, D2 §2.4): instanceId = r&lt;year&gt;-&lt;pid&gt;-&lt;6 hex&gt;, primary pipe
    /// revit-mcp-next-&lt;instanceId&gt;, control pipe revit-mcp-next-&lt;instanceId&gt;-control. The broker derives the pipe
    /// names from the registration file name, so even an unreadable registration still names its pipes.
    /// </summary>
    internal static class PipeNameProvider
    {
        public static string CreateInstanceId(int year, int pid)
        {
            byte[] random = new byte[3];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(random);
            }
            string hex = BitConverter.ToString(random).Replace("-", string.Empty).ToLowerInvariant();
            return "r" + year.ToString(CultureInfo.InvariantCulture) + "-" + pid.ToString(CultureInfo.InvariantCulture) + "-" + hex;
        }

        public static string GetPrimaryPipeName(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("An instance id is required.", nameof(instanceId));
            return BridgeProtocol.PrimaryPipeName(instanceId);
        }

        public static string GetControlPipeName(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("An instance id is required.", nameof(instanceId));
            return BridgeProtocol.ControlPipeName(instanceId);
        }
    }
}
