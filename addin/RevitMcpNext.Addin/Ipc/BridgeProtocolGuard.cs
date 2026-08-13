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
        public static BridgeProtocolStatus Classify(string bridgeProtocolVersion)
        {
            if (string.IsNullOrWhiteSpace(bridgeProtocolVersion)) return BridgeProtocolStatus.Missing;
            return string.Equals(bridgeProtocolVersion, BridgeProtocol.Version, StringComparison.Ordinal)
                ? BridgeProtocolStatus.Current
                : BridgeProtocolStatus.Mismatch;
        }
    }
}
