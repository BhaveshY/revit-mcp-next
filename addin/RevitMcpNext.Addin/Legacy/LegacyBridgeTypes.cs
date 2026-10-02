using System.Collections.Generic;

namespace RevitMcpNext.Addin.Revit
{
    // Protocol v2 envelope types, kept only so the legacy per-op handlers (Domains/**, Serialization/**) keep
    // compiling until the wave-2 lanes rewrite them onto RequestContext/ChangeContext. Nothing on the wire uses them;
    // Legacy/LegacyAdapter.cs converts between them and protocol v3. Deleted in wave 3.

    internal sealed class LegacyRequest
    {
        public string BridgeProtocolVersion { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string AuthToken { get; set; }
        public string Operation { get; set; } = string.Empty;
        public string OperationKind { get; set; } = "read";
        public int TimeoutMs { get; set; } = 30000;
        public string InstanceId { get; set; }
        public string DocumentFingerprint { get; set; }
        public long? ExpectedGeneration { get; set; }
        public Dictionary<string, object> Payload { get; set; } = new Dictionary<string, object>();
    }

    internal sealed class LegacyResponse
    {
        public bool Ok { get; set; }
        public string RequestId { get; set; } = string.Empty;
        public object Data { get; set; } = new Dictionary<string, object>();
        public LegacyError Error { get; set; }
        public List<LegacyWarning> Warnings { get; set; } = new List<LegacyWarning>();
        public LegacyMetrics Metrics { get; set; } = new LegacyMetrics();
        public long? Generation { get; set; }
    }

    internal sealed class LegacyWarning
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    internal sealed class LegacyError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool Recoverable { get; set; } = true;
        public string SuggestedNextAction { get; set; }
    }

    internal sealed class LegacyMetrics
    {
        public long ElapsedMs { get; set; }
        public long? QueueWaitMs { get; set; }
        public long? RevitExecutionMs { get; set; }
        public long? CollectorElapsedMs { get; set; }
        public bool? CacheHit { get; set; }
        public int? ReturnedCount { get; set; }
        public int? TotalCount { get; set; }
    }
}
