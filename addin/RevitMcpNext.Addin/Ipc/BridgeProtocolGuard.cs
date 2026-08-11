using System;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    internal enum BridgeProtocolStatus
    {
        Current,
        Missing,
        Mismatch
    }

    internal static class BridgeProtocolGuard
    {
        public static BridgeProtocolStatus Classify(string protocolVersion)
        {
            if (string.IsNullOrWhiteSpace(protocolVersion)) return BridgeProtocolStatus.Missing;
            return string.Equals(protocolVersion, BridgeProtocol.Version, StringComparison.Ordinal)
                ? BridgeProtocolStatus.Current
                : BridgeProtocolStatus.Mismatch;
        }
    }
}
