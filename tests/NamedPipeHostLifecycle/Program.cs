using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Addin.Ipc;
using RevitMcpNext.Addin.Revit;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.NamedPipeHostLifecycle
{
    internal static class Program
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(25);

        private static int Main()
        {
            try
            {
                RunAsync().GetAwaiter().GetResult();
                Console.WriteLine("Named-pipe host lifecycle regression harness passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Named-pipe host lifecycle regression harness failed.");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static async Task RunAsync()
        {
            await RunCaseAsync(
                "four held connections do not kill later acceptance",
                FourHeldConnectionsDoNotKillLaterAcceptanceAsync).ConfigureAwait(false);
            await RunCaseAsync(
                "partial clients are reclaimed",
                PartialClientsAreReclaimedAsync).ConfigureAwait(false);
            await RunCaseAsync(
                "clients that stop reading responses are reclaimed",
                NonReadingClientsAreReclaimedAsync).ConfigureAwait(false);
            await RunCaseAsync(
                "transient listener factory faults recover",
                TransientListenerFactoryFaultRecoversAsync).ConfigureAwait(false);
        }

        private static async Task RunCaseAsync(string name, Func<Task> test)
        {
            DiagnosticsLogger.Clear();
            Console.WriteLine("[run] " + name);
            await test().ConfigureAwait(false);
            Console.WriteLine("[pass] " + name);
        }

        private static async Task FourHeldConnectionsDoNotKillLaterAcceptanceAsync()
        {
            string pipeName = UniquePipeName("held");
            var queue = new RevitRequestQueue();
            using (var host = new NamedPipeHost(pipeName, queue, PipeAuthOptions.FromEnvironment()))
            {
                host.Start();

                var heldClients = new List<Task<BridgeResponseEnvelope>>();
                for (int index = 0; index < 4; index++)
                {
                    heldClients.Add(SendRequestAsync(pipeName, "hold-" + index, "hold", timeoutMs: 12000));
                }

                await queue.WaitForAcceptedCountAsync(4, TestTimeout).ConfigureAwait(false);

                Task<BridgeResponseEnvelope> later = SendRequestAsync(
                    pipeName,
                    "later-status",
                    "status",
                    timeoutMs: 12000);

                await Task.Delay(200).ConfigureAwait(false);
                queue.ReleaseAllHeld();

                BridgeResponseEnvelope laterResponse = await WithTimeoutAsync(later, TestTimeout).ConfigureAwait(false);
                Assert(laterResponse.Ok, "A request submitted after four held clients did not succeed.");
                Assert(
                    string.Equals(laterResponse.RequestId, "later-status", StringComparison.Ordinal),
                    "Later response request ID did not match.");

                BridgeResponseEnvelope[] heldResponses = await WithTimeoutAsync(
                    Task.WhenAll(heldClients),
                    TestTimeout).ConfigureAwait(false);
                Assert(heldResponses.All(response => response.Ok), "One or more held requests failed after release.");

                BridgeResponseEnvelope finalProbe = await WithTimeoutAsync(
                    SendRequestAsync(pipeName, "final-status", "status", timeoutMs: 5000),
                    TestTimeout).ConfigureAwait(false);
                Assert(finalProbe.Ok, "Listener did not remain healthy after held-client pressure.");
            }
        }

        private static async Task PartialClientsAreReclaimedAsync()
        {
            string pipeName = UniquePipeName("partial");
            var queue = new RevitRequestQueue();
            using (var host = new NamedPipeHost(pipeName, queue, PipeAuthOptions.FromEnvironment()))
            {
                host.Start();

                var partialClients = new List<NamedPipeClientStream>();
                try
                {
                    for (int index = 0; index < 4; index++)
                    {
                        var client = new NamedPipeClientStream(
                            ".",
                            pipeName,
                            PipeDirection.InOut,
                            PipeOptions.Asynchronous);
                        partialClients.Add(client);
                        await ConnectAsync(client, 5000).ConfigureAwait(false);
                        byte[] partialHeader = { 0, 0 };
                        await client.WriteAsync(partialHeader, 0, partialHeader.Length).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(11)).ConfigureAwait(false);
                    BridgeResponseEnvelope response = await WithTimeoutAsync(
                        SendRequestAsync(pipeName, "after-partials", "status", timeoutMs: 5000),
                        TestTimeout).ConfigureAwait(false);
                    Assert(response.Ok, "Listener did not reclaim stalled partial clients after the handshake timeout.");
                    Assert(queue.AcceptedCount == 1, "Partial clients should not reach the Revit request queue.");
                }
                finally
                {
                    foreach (NamedPipeClientStream client in partialClients)
                    {
                        client.Dispose();
                    }
                }
            }
        }

        private static async Task TransientListenerFactoryFaultRecoversAsync()
        {
            string pipeName = UniquePipeName("factory-fault");
            var queue = new RevitRequestQueue();
            int factoryCalls = 0;

            NamedPipeServerStream Factory()
            {
                int call = Interlocked.Increment(ref factoryCalls);
                if (call == 5)
                {
                    throw new IOException("Injected transient listener creation failure.");
                }

                return CreateTestServer(pipeName);
            }

            using (var host = new NamedPipeHost(
                pipeName,
                queue,
                PipeAuthOptions.FromEnvironment(),
                Factory))
            {
                host.Start();

                BridgeResponseEnvelope first = await WithTimeoutAsync(
                    SendRequestAsync(pipeName, "trigger-recreate", "status", timeoutMs: 5000),
                    TestTimeout).ConfigureAwait(false);
                Assert(first.Ok, "Initial request failed before listener recreation.");

                await WaitUntilAsync(
                    () => Volatile.Read(ref factoryCalls) >= 6,
                    TestTimeout,
                    "Listener factory was not retried after the injected failure.").ConfigureAwait(false);

                BridgeResponseEnvelope recovered = await WithTimeoutAsync(
                    SendRequestAsync(pipeName, "after-factory-fault", "status", timeoutMs: 5000),
                    TestTimeout).ConfigureAwait(false);
                Assert(recovered.Ok, "Listener did not recover after a transient factory failure.");
                Assert(
                    DiagnosticsLogger.Snapshot().Any(entry =>
                        entry.IndexOf("Injected transient listener creation failure", StringComparison.Ordinal) >= 0),
                    "Transient listener failure was not recorded in diagnostics.");
            }
        }

        private static async Task NonReadingClientsAreReclaimedAsync()
        {
            string pipeName = UniquePipeName("non-reading");
            var queue = new RevitRequestQueue();
            using (var host = new NamedPipeHost(pipeName, queue, PipeAuthOptions.FromEnvironment()))
            {
                host.Start();

                var clients = new List<NamedPipeClientStream>();
                try
                {
                    for (int index = 0; index < 4; index++)
                    {
                        var client = new NamedPipeClientStream(
                            ".",
                            pipeName,
                            PipeDirection.InOut,
                            PipeOptions.Asynchronous);
                        clients.Add(client);
                        await ConnectAsync(client, 5000).ConfigureAwait(false);
                        await WriteRequestAsync(
                            client,
                            "non-reading-" + index,
                            "large-response",
                            timeoutMs: 12000).ConfigureAwait(false);
                    }

                    await queue.WaitForAcceptedCountAsync(4, TestTimeout).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);

                    BridgeResponseEnvelope response = await WithTimeoutAsync(
                        SendRequestAsync(pipeName, "after-non-reading", "status", timeoutMs: 5000),
                        TestTimeout).ConfigureAwait(false);
                    Assert(response.Ok, "Listener did not reclaim clients that stopped reading responses.");
                    Assert(
                        DiagnosticsLogger.Snapshot().Count(entry =>
                            entry.IndexOf("timed out while writing the response frame", StringComparison.Ordinal) >= 0) >= 4,
                        "Expected all four blocked response writes to be recorded in diagnostics.");
                }
                finally
                {
                    foreach (NamedPipeClientStream client in clients)
                    {
                        client.Dispose();
                    }
                }
            }
        }

        private static NamedPipeServerStream CreateTestServer(string pipeName)
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 4,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        private static async Task<BridgeResponseEnvelope> SendRequestAsync(
            string pipeName,
            string requestId,
            string operation,
            int timeoutMs)
        {
            using (var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            {
                await ConnectAsync(client, timeoutMs).ConfigureAwait(false);
                await WriteRequestAsync(client, requestId, operation, timeoutMs).ConfigureAwait(false);
                string responseJson = await FramedPipeTransport.ReadFrameAsync(
                    client,
                    CancellationToken.None).ConfigureAwait(false);
                return DeserializeResponse(responseJson);
            }
        }

        private static Task WriteRequestAsync(
            NamedPipeClientStream client,
            string requestId,
            string operation,
            int timeoutMs)
        {
            string request = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                ["protocolVersion"] = BridgeProtocol.Version,
                ["requestId"] = requestId,
                ["sessionId"] = "lifecycle-harness",
                ["operation"] = operation,
                ["operationKind"] = "read",
                ["timeoutMs"] = timeoutMs,
                ["payload"] = new Dictionary<string, object>()
            });
            return FramedPipeTransport.WriteFrameAsync(
                client,
                request,
                CancellationToken.None);
        }

        private static BridgeResponseEnvelope DeserializeResponse(string json)
        {
            var body = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (body == null)
            {
                throw new InvalidDataException("Bridge response was not a JSON object.");
            }

            bool ok = body.TryGetValue("ok", out object okValue) && Convert.ToBoolean(okValue);
            return new BridgeResponseEnvelope
            {
                Ok = ok,
                RequestId = body.TryGetValue("requestId", out object requestId)
                    ? Convert.ToString(requestId)
                    : string.Empty,
                Error = ok || !body.TryGetValue("error", out object errorValue)
                    ? null
                    : DeserializeError(errorValue)
            };
        }

        private static BridgeError DeserializeError(object value)
        {
            var body = value as Dictionary<string, object>;
            return new BridgeError
            {
                Code = body != null && body.TryGetValue("code", out object code)
                    ? Convert.ToString(code)
                    : "UNKNOWN",
                Message = body != null && body.TryGetValue("message", out object message)
                    ? Convert.ToString(message)
                    : "Unknown bridge failure."
            };
        }

        private static Task ConnectAsync(NamedPipeClientStream client, int timeoutMs)
        {
            return client.ConnectAsync(timeoutMs);
        }

        private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
            {
                throw new TimeoutException("Operation did not complete within " + timeout + ".");
            }

            return await task.ConfigureAwait(false);
        }

        private static async Task WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout,
            string failureMessage)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(failureMessage);
                }

                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        private static string UniquePipeName(string suffix)
        {
            return "revit-mcp-next-lifecycle-" + suffix + "-" + Guid.NewGuid().ToString("N");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
