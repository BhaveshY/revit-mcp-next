using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Diagnostics
{
    internal static class DiagnosticsLogger
    {
        private static readonly ConcurrentQueue<string> Entries = new ConcurrentQueue<string>();

        public static void Info(string message)
        {
            Entries.Enqueue("INFO " + message);
        }

        public static void Error(string message, Exception exception = null)
        {
            Entries.Enqueue("ERROR " + message + (exception == null ? string.Empty : " " + exception.GetType().Name + ": " + exception.Message));
        }

        public static string[] Snapshot()
        {
            return Entries.ToArray();
        }

        public static void Clear()
        {
            while (Entries.TryDequeue(out _))
            {
            }
        }
    }
}

namespace RevitMcpNext.Addin.Ipc
{
    internal sealed class PipeAuthOptions
    {
        public bool IsRequired => false;

        public static PipeAuthOptions FromEnvironment()
        {
            return new PipeAuthOptions();
        }

        public bool IsAuthorized(string providedToken)
        {
            return true;
        }
    }

}

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class RevitRequestQueue
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _held =
            new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _accepted = new SemaphoreSlim(0);
        private int _acceptedCount;

        public int AcceptedCount => Volatile.Read(ref _acceptedCount);

        public async Task<BridgeResponseEnvelope> EnqueueAsync(
            BridgeRequestEnvelope envelope,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acceptedCount);
            _accepted.Release();

            if (string.Equals(envelope.Operation, "hold", StringComparison.Ordinal))
            {
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_held.TryAdd(envelope.RequestId, release))
                {
                    throw new InvalidOperationException("Duplicate held request ID: " + envelope.RequestId);
                }

                using (cancellationToken.Register(() => release.TrySetCanceled()))
                {
                    try
                    {
                        await release.Task.ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        return Failure(envelope, "REQUEST_CANCELLED", "Held test request was cancelled.");
                    }
                    finally
                    {
                        _held.TryRemove(envelope.RequestId, out _);
                    }
                }
            }

            var data = new Dictionary<string, object>
            {
                ["operation"] = envelope.Operation
            };
            if (string.Equals(envelope.Operation, "large-response", StringComparison.Ordinal))
            {
                data["payload"] = new string('x', 3 * 1024 * 1024);
            }

            return new BridgeResponseEnvelope
            {
                Ok = true,
                RequestId = envelope.RequestId,
                Data = data,
                Metrics = new BridgeMetrics { ElapsedMs = 1 }
            };
        }

        public async Task WaitForAcceptedCountAsync(int expected, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (AcceptedCount < expected)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !await _accepted.WaitAsync(remaining).ConfigureAwait(false))
                {
                    throw new TimeoutException(
                        "Timed out waiting for " + expected + " accepted requests; observed " + AcceptedCount + ".");
                }
            }
        }

        public void ReleaseAllHeld()
        {
            foreach (TaskCompletionSource<bool> release in _held.Values)
            {
                release.TrySetResult(true);
            }
        }

        private static BridgeResponseEnvelope Failure(
            BridgeRequestEnvelope request,
            string code,
            string message)
        {
            return new BridgeResponseEnvelope
            {
                Ok = false,
                RequestId = request.RequestId,
                Error = new BridgeError
                {
                    Code = code,
                    Message = message,
                    Recoverable = true
                }
            };
        }
    }
}
