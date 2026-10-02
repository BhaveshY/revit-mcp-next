using System.Collections.Generic;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    internal enum BridgeProtocolStatus
    {
        Supported,
        Missing,
        /// <summary>The broker speaks an older protocol than this add-in accepts.</summary>
        BrokerTooOld,
        /// <summary>The broker speaks a newer protocol than this add-in accepts.</summary>
        BrokerTooNew
    }

    /// <summary>Protocol range check (SPEC §4.6.1, D2 §4.2): accepts [MinVersion, MaxVersion].</summary>
    internal static class BridgeProtocolGuard
    {
        public static BridgeProtocolStatus Classify(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return BridgeProtocolStatus.Missing;
            if (BridgeProtocol.Compare(version, BridgeProtocol.MinVersion) < 0) return BridgeProtocolStatus.BrokerTooOld;
            if (BridgeProtocol.Compare(version, BridgeProtocol.MaxVersion) > 0) return BridgeProtocolStatus.BrokerTooNew;
            return BridgeProtocolStatus.Supported;
        }

        /// <summary>
        /// Null when supported; else the error response for the request (ADDIN_NEWER_THAN_BROKER for an old broker,
        /// ADDIN_OUTDATED for a newer broker or a request without "v").
        /// </summary>
        public static BridgeResponse Check(BridgeRequest request, string addinVersion)
        {
            BridgeProtocolStatus status = Classify(request?.V);
            if (status == BridgeProtocolStatus.Supported) return null;
            var details = new Dictionary<string, object>
            {
                ["protocol"] = BridgeProtocol.RangeWire(),
                ["brokerProtocol"] = request?.V,
                ["addinVersion"] = addinVersion
            };
            switch (status)
            {
                case BridgeProtocolStatus.BrokerTooOld:
                    return BridgeResponse.Failure(request?.RequestId, ErrorCodes.AddinNewerThanBroker,
                        "The broker speaks bridge protocol " + request?.V + ", older than this add-in accepts (" +
                        BridgeProtocol.MinVersion + " to " + BridgeProtocol.MaxVersion + ").", details);
                case BridgeProtocolStatus.BrokerTooNew:
                    return BridgeResponse.Failure(request?.RequestId, ErrorCodes.AddinOutdated,
                        "The broker speaks bridge protocol " + request?.V + ", newer than this add-in accepts (" +
                        BridgeProtocol.MinVersion + " to " + BridgeProtocol.MaxVersion + ").", details);
                default:
                    // Protocol v2 brokers sent "protocolVersion" instead of "v": treat a missing "v" as an old broker.
                    return BridgeResponse.Failure(request?.RequestId, ErrorCodes.AddinNewerThanBroker,
                        "The request has no protocol version (field \"v\"), so it comes from an older broker; this add-in speaks " +
                        BridgeProtocol.Version + ".", details);
            }
        }
    }
}
