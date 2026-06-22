using System;
using System.Collections.Generic;

namespace RevitMcpNext.Contracts
{
    public static class BridgeProtocol
    {
        public const string Version = "2026-06-22";
    }

    public sealed class BridgeRequestEnvelope
    {
        public string ProtocolVersion { get; set; } = BridgeProtocol.Version;
        public string RequestId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string? AuthToken { get; set; }
        public string Operation { get; set; } = string.Empty;
        public string OperationKind { get; set; } = "read";
        public int TimeoutMs { get; set; } = 30000;
        public string? DocumentFingerprint { get; set; }
        public long? ExpectedGeneration { get; set; }
        public Dictionary<string, object> Payload { get; set; } = new Dictionary<string, object>();
    }

    public sealed class BridgeResponseEnvelope
    {
        public bool Ok { get; set; }
        public string RequestId { get; set; } = string.Empty;
        public object? Data { get; set; } = new Dictionary<string, object>();
        public BridgeError? Error { get; set; }
        public List<BridgeWarning> Warnings { get; set; } = new List<BridgeWarning>();
        public BridgeMetrics Metrics { get; set; } = new BridgeMetrics();
        public long? Generation { get; set; }
    }

    public sealed class BridgeWarning
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public sealed class BridgeError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool Recoverable { get; set; } = true;
        public string? SuggestedNextAction { get; set; }
    }

    public sealed class BridgeMetrics
    {
        public long ElapsedMs { get; set; }
        public long? CollectorElapsedMs { get; set; }
        public bool? CacheHit { get; set; }
        public int? ReturnedCount { get; set; }
        public int? TotalCount { get; set; }
    }

    public sealed class QueuedRevitRequest
    {
        public QueuedRevitRequest(BridgeRequestEnvelope envelope, DateTimeOffset enqueuedAt)
        {
            Envelope = envelope;
            EnqueuedAt = enqueuedAt;
        }

        public BridgeRequestEnvelope Envelope { get; }
        public DateTimeOffset EnqueuedAt { get; }
    }
}
