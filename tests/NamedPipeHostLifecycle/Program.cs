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
            await RunCaseAsync(
                "bridge protocol version is mandatory at named-pipe ingress",
                BridgeProtocolVersionIsMandatoryAsync).ConfigureAwait(false);
            await RunCaseAsync(
                "reserved control pipe stays reachable and cancels queued work",
                ReservedControlPipeStaysReachableAsync).ConfigureAwait(false);
            await RunCaseAsync(
                "write request outcomes replay and remain queryable",
                WriteRequestOutcomesReplayAsync).ConfigureAwait(false);
            await RunCaseAsync(
                "request state, preview sessions, and canonical JSON preserve invariants",
                FoundationUnitInvariantsAsync).ConfigureAwait(false);
        }

        private static async Task BridgeProtocolVersionIsMandatoryAsync()
        {
            Assert(BridgeProtocolGuard.Classify(null) == BridgeProtocolStatus.Missing,
                "Shared ingress bridge protocol guard did not classify a missing version.");
            Assert(BridgeProtocolGuard.Classify("1900-01-01") == BridgeProtocolStatus.Mismatch,
                "Shared ingress bridge protocol guard did not classify a wrong version.");
            Assert(BridgeProtocolGuard.Classify(BridgeProtocol.Version) == BridgeProtocolStatus.Current,
                "Shared ingress bridge protocol guard did not accept the current version.");

            string pipeName = UniquePipeName("protocol");
            var queue = new RevitRequestQueue();
            using (var host = new NamedPipeHost(pipeName, queue, PipeAuthOptions.FromEnvironment()))
            {
                host.Start();

                BridgeResponseEnvelope missing = await SendRequestAsync(
                    pipeName,
                    "missing-protocol",
                    "status",
                    timeoutMs: 5000,
                    bridgeProtocolVersion: null).ConfigureAwait(false);
                Assert(!missing.Ok && missing.Error?.Code == "PROTOCOL_VERSION_REQUIRED",
                    "A named-pipe request without protocolVersion was not rejected explicitly.");

                BridgeResponseEnvelope wrong = await SendRequestAsync(
                    pipeName,
                    "wrong-protocol",
                    "status",
                    timeoutMs: 5000,
                    bridgeProtocolVersion: "1900-01-01").ConfigureAwait(false);
                Assert(!wrong.Ok && wrong.Error?.Code == "PROTOCOL_VERSION_MISMATCH",
                    "A named-pipe request with the wrong protocolVersion did not preserve mismatch rejection.");
                Assert(queue.AcceptedCount == 0, "Invalid protocol requests reached the Revit queue.");

                BridgeResponseEnvelope current = await SendRequestAsync(
                    pipeName,
                    "current-protocol",
                    "status",
                    timeoutMs: 5000).ConfigureAwait(false);
                Assert(current.Ok && queue.AcceptedCount == 1,
                    "A request using the current protocolVersion was not accepted.");
            }
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
                Assert(finalProbe.Metrics.QueueWaitMs == 2 && finalProbe.Metrics.RevitExecutionMs == 3,
                    "Named-pipe serialization did not preserve queued Revit phase metrics.");
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

        private static async Task ReservedControlPipeStaysReachableAsync()
        {
            string pipeName = UniquePipeName("control");
            var queue = new RevitRequestQueue();
            using (var host = new NamedPipeHost(pipeName, queue, PipeAuthOptions.FromEnvironment()))
            {
                host.Start();
                var heldClients = new List<Task<BridgeResponseEnvelope>>();
                for (int index = 0; index < 4; index++)
                {
                    heldClients.Add(SendRequestAsync(pipeName, "control-hold-" + index, "hold", timeoutMs: 12000));
                }

                await queue.WaitForAcceptedCountAsync(4, TestTimeout).ConfigureAwait(false);
                BridgeResponseEnvelope health = await SendRequestAsync(
                    host.ControlPipeName,
                    "control-health",
                    "bridge_health",
                    timeoutMs: 5000).ConfigureAwait(false);
                Assert(health.Ok, "Reserved control pipe did not answer bridge_health while all primary workers were occupied.");
                var healthData = health.Data as Dictionary<string, object>;
                Assert(healthData != null, "bridge_health did not return diagnostics.");
                Assert(!Convert.ToBoolean(healthData["healthy"]),
                    "bridge_health reported healthy while no primary listener was available.");
                Assert(Convert.ToInt32(healthData["primaryWaitingListeners"]) == 0,
                    "bridge_health did not report the exhausted primary listener pool.");
                Assert(Convert.ToInt32(healthData["primaryActiveConnections"]) == 4,
                    "bridge_health did not separate the four active primary connections.");
                Assert(Convert.ToInt32(healthData["controlWaitingListeners"]) == 0,
                    "bridge_health did not separate the active control listener from waiting listeners.");
                Assert(Convert.ToInt32(healthData["controlActiveConnections"]) == 1,
                    "bridge_health did not report its active control connection.");
                Assert(queue.AcceptedCount == 4, "bridge_health must not enter the Revit request queue.");

                BridgeResponseEnvelope cancel = await SendRequestAsync(
                    host.ControlPipeName,
                    "control-cancel",
                    "cancel_request",
                    timeoutMs: 5000,
                    operationKind: "debug",
                    payload: new Dictionary<string, object>
                    {
                        ["requestId"] = "control-hold-0",
                        ["reason"] = "lifecycle test"
                    }).ConfigureAwait(false);
                Assert(cancel.Ok, "Reserved control pipe did not answer cancel_request.");
                var cancelData = cancel.Data as Dictionary<string, object>;
                Assert(cancelData != null && Convert.ToBoolean(cancelData["cancelled"]), "Direct cancellation did not cancel pending work.");

                queue.ReleaseAllHeld();
                BridgeResponseEnvelope[] heldResponses = await Task.WhenAll(heldClients).ConfigureAwait(false);
                Assert(heldResponses.Count(response => !response.Ok && response.Error?.Code == "REQUEST_CANCELLED") == 1,
                    "Exactly one held request should report cancellation.");

                BridgeResponseEnvelope recoveredHealth = await WaitForHealthyPrimaryPoolAsync(host).ConfigureAwait(false);
                var recoveredHealthData = recoveredHealth.Data as Dictionary<string, object>;
                Assert(recoveredHealth.Ok && Convert.ToBoolean(recoveredHealthData?["healthy"]),
                    "bridge_health did not recover after primary listeners resumed waiting.");
                Assert(Convert.ToInt32(recoveredHealthData["primaryWaitingListeners"]) > 0,
                    "Recovered bridge health did not report an available primary listener.");
            }
        }

        private static async Task<BridgeResponseEnvelope> WaitForHealthyPrimaryPoolAsync(NamedPipeHost host)
        {
            DateTime deadline = DateTime.UtcNow + TestTimeout;
            BridgeResponseEnvelope response = null;
            do
            {
                response = await SendRequestAsync(
                    host.ControlPipeName,
                    "control-health-recovered-" + Guid.NewGuid().ToString("N"),
                    "bridge_health",
                    timeoutMs: 5000).ConfigureAwait(false);
                var data = response.Data as Dictionary<string, object>;
                if (response.Ok && data != null && Convert.ToBoolean(data["healthy"]))
                {
                    return response;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return response;
        }

        private static async Task WriteRequestOutcomesReplayAsync()
        {
            string pipeName = UniquePipeName("outcomes");
            var queue = new RevitRequestQueue();
            using (var host = new NamedPipeHost(pipeName, queue, PipeAuthOptions.FromEnvironment()))
            {
                host.Start();
                Task<BridgeResponseEnvelope> first = SendRequestAsync(
                    pipeName,
                    "write-replay-1",
                    "write-once",
                    timeoutMs: 5000,
                    operationKind: "write");
                Task<BridgeResponseEnvelope> second = SendRequestAsync(
                    pipeName,
                    "write-replay-1",
                    "write-once",
                    timeoutMs: 5000,
                    operationKind: "write");

                BridgeResponseEnvelope[] responses = await Task.WhenAll(first, second).ConfigureAwait(false);
                Assert(responses.All(response => response.Ok), "A duplicate write request did not replay the successful result.");
                Assert(queue.AcceptedCount == 1, "A duplicate write request reached the Revit queue more than once.");

                BridgeResponseEnvelope lookup = await SendRequestAsync(
                    host.ControlPipeName,
                    "lookup-write-replay-1",
                    "get_request_result",
                    timeoutMs: 5000,
                    operationKind: "debug",
                    payload: new Dictionary<string, object> { ["requestId"] = "write-replay-1" }).ConfigureAwait(false);
                Assert(lookup.Ok, "get_request_result failed for a completed write.");
                var lookupData = lookup.Data as Dictionary<string, object>;
                Assert(lookupData != null && Convert.ToBoolean(lookupData["found"]), "Completed write was absent from the outcome ledger.");
                Assert(string.Equals(Convert.ToString(lookupData["state"]), "committed", StringComparison.Ordinal),
                    "Completed write did not report committed state.");
                var nestedResponse = lookupData["response"] as Dictionary<string, object>;
                Assert(nestedResponse != null && Convert.ToBoolean(nestedResponse["ok"]),
                    "get_request_result did not return the cached bridge response.");

                BridgeResponseEnvelope payloadConflict = await SendRequestAsync(
                    pipeName,
                    "write-replay-1",
                    "write-once",
                    timeoutMs: 5000,
                    operationKind: "write",
                    payload: new Dictionary<string, object> { ["changed"] = true }).ConfigureAwait(false);
                Assert(!payloadConflict.Ok && payloadConflict.Error?.Code == "REQUEST_ID_CONFLICT",
                    "Reusing a write request ID with a different payload was not rejected.");

                BridgeResponseEnvelope conflict = await SendRequestAsync(
                    pipeName,
                    "write-replay-1",
                    "different-write",
                    timeoutMs: 5000,
                    operationKind: "write").ConfigureAwait(false);
                Assert(!conflict.Ok && conflict.Error?.Code == "REQUEST_ID_CONFLICT",
                    "Reusing a write request ID for another operation was not rejected.");

                Task<BridgeResponseEnvelope> runningWrite = SendRequestAsync(
                    pipeName,
                    "write-running-1",
                    "hold",
                    timeoutMs: 10000,
                    operationKind: "write");
                await queue.WaitForAcceptedCountAsync(2, TestTimeout).ConfigureAwait(false);
                BridgeResponseEnvelope runningLookup = await SendRequestAsync(
                    host.ControlPipeName,
                    "lookup-write-running-1",
                    "get_request_result",
                    timeoutMs: 5000,
                    operationKind: "debug",
                    payload: new Dictionary<string, object> { ["requestId"] = "write-running-1" }).ConfigureAwait(false);
                var runningData = runningLookup.Data as Dictionary<string, object>;
                Assert(runningLookup.Ok && string.Equals(Convert.ToString(runningData?["state"]), "running", StringComparison.Ordinal),
                    "A dequeued in-flight write did not report running state.");
                queue.ReleaseAllHeld();
                Assert((await runningWrite.ConfigureAwait(false)).Ok, "Held running write did not complete after release.");
            }
        }

        private static Task FoundationUnitInvariantsAsync()
        {
            string stringCanonical = CanonicalJson.Serialize(new Dictionary<string, object> { ["value"] = "1\n\"" });
            string numberCanonical = CanonicalJson.Serialize(new Dictionary<string, object> { ["value"] = 1 });
            Assert(!string.Equals(stringCanonical, numberCanonical, StringComparison.Ordinal),
                "Canonical JSON collapsed string and numeric values.");
            Assert(stringCanonical == "{\"value\":\"1\\n\\\"\"}", "Canonical JSON did not escape string data deterministically.");

            var pending = new QueuedRevitWorkItem(Request("pending-cancel", "read", "read"), CancellationToken.None);
            Assert(pending.TryCancel("REQUEST_CANCELLED", "test"), "Pending work did not transition to cancelled.");
            Assert(!pending.TryBeginExecution(), "Cancelled work transitioned into running state.");

            var running = new QueuedRevitWorkItem(Request("running-cancel", "read", "read"), CancellationToken.None);
            Thread.Sleep(15);
            Assert(running.TryBeginExecution(), "Pending work did not transition to running.");
            Assert(!running.TryCancel("REQUEST_CANCELLED", "test"), "Running work was cancelled unsafely.");
            BridgeResponseEnvelope timedResponse = SuccessResponse("running-cancel");
            Assert(running.TrySetResult(timedResponse, revitExecutionMs: 7), "Running work did not transition to completed.");
            Assert(timedResponse.Metrics.QueueWaitMs >= 1, "Queued work did not record time spent waiting for Revit.");
            Assert(timedResponse.Metrics.RevitExecutionMs == 7, "Queued work did not record Revit execution time.");
            Assert(timedResponse.Metrics.ElapsedMs == 1, "Queued phase timing overwrote the existing elapsed metric.");

            var previews = new PreviewTokenStore(TimeSpan.FromMinutes(1));
            previews.Issue("preview-1", "session-a", "instance-a", "doc", 7, "tx", "ops", "changes", "hash", true, 1);
            previews.Issue("preview-1", "session-b", "instance-a", "doc", 7, "tx", "ops", "changes", "hash", true, 1);
            Assert(previews.ValidateMetadata("preview-1", "session-a", "instance-a", "doc", 7, "hash").Ok,
                "Issuing the same preview ID in another session invalidated the first session.");
            Assert(!previews.ValidateMetadata("preview-1", "session-c", "instance-a", "doc", 7, "hash").Ok,
                "Preview metadata was accepted across sessions.");
            Assert(!previews.ValidateMetadata("preview-1", "session-a", "instance-b", "doc", 7, "hash").Ok,
                "Preview metadata was accepted across Revit instances.");

            string instanceA = PipeNameProvider.CreateRuntimeInstanceId("2024");
            string instanceB = PipeNameProvider.CreateRuntimeInstanceId("2024");
            Assert(!string.Equals(instanceA, instanceB, StringComparison.Ordinal),
                "Two add-in runtimes were assigned the same instance ID.");
            Assert(!string.Equals(PipeNameProvider.GetRuntimePipeName(instanceA), PipeNameProvider.GetRuntimePipeName(instanceB), StringComparison.Ordinal),
                "Two add-in runtimes were assigned the same named pipe.");
            Assert(PipeNameProvider.GetRuntimePipeName(instanceA).Contains(instanceA),
                "The runtime pipe does not identify its Revit instance.");

            var ledger = new RequestOutcomeLedger(1, TimeSpan.FromMinutes(1));
            BridgeRequestEnvelope ledgerRequest = Request("ledger-1", "write", "write");
            RequestOutcomeLease owner = ledger.Acquire(ledgerRequest, "fingerprint-1");
            Assert(owner.IsOwner, "First ledger acquisition was not the owner.");
            Assert(ledger.Acquire(ledgerRequest, "fingerprint-2").ErrorCode == "REQUEST_ID_CONFLICT",
                "Ledger replayed a request ID whose payload fingerprint changed.");
            Assert(ledger.Acquire(Request("ledger-2", "write", "write"), "fingerprint-2").ErrorCode == "REQUEST_LEDGER_CAPACITY",
                "Ledger did not bound concurrent in-flight outcomes.");
            ledger.Complete("lifecycle-harness", "ledger-1", SuccessResponse("ledger-1"), RequestOutcomeState.Committed);
            Assert(ledger.Acquire(Request("ledger-2", "write", "write"), "fingerprint-2").IsOwner,
                "Ledger did not evict a terminal outcome when capacity was needed.");
            return Task.CompletedTask;
        }

        private static BridgeRequestEnvelope Request(string requestId, string operation, string operationKind)
        {
            return new BridgeRequestEnvelope
            {
                BridgeProtocolVersion = BridgeProtocol.Version,
                RequestId = requestId,
                SessionId = "lifecycle-harness",
                Operation = operation,
                OperationKind = operationKind,
                TimeoutMs = 5000,
                Payload = new Dictionary<string, object>()
            };
        }

        private static BridgeResponseEnvelope SuccessResponse(string requestId)
        {
            return new BridgeResponseEnvelope
            {
                Ok = true,
                RequestId = requestId,
                Data = new Dictionary<string, object>(),
                Metrics = new BridgeMetrics { ElapsedMs = 1 }
            };
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
            int timeoutMs,
            string operationKind = "read",
            Dictionary<string, object> payload = null,
            string sessionId = "lifecycle-harness",
            string bridgeProtocolVersion = BridgeProtocol.Version)
        {
            using (var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            {
                await ConnectAsync(client, timeoutMs).ConfigureAwait(false);
                await WriteRequestAsync(client, requestId, operation, timeoutMs, operationKind, payload, sessionId, bridgeProtocolVersion).ConfigureAwait(false);
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
            int timeoutMs,
            string operationKind = "read",
            Dictionary<string, object> payload = null,
            string sessionId = "lifecycle-harness",
            string bridgeProtocolVersion = BridgeProtocol.Version)
        {
            var requestBody = new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["sessionId"] = sessionId,
                ["operation"] = operation,
                ["operationKind"] = operationKind,
                ["timeoutMs"] = timeoutMs,
                ["payload"] = payload ?? new Dictionary<string, object>()
            };
            if (bridgeProtocolVersion != null) requestBody["protocolVersion"] = bridgeProtocolVersion;
            string request = new JavaScriptSerializer().Serialize(requestBody);
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
                Data = ok && body.TryGetValue("data", out object dataValue) ? dataValue : null,
                Metrics = body.TryGetValue("metrics", out object metricsValue)
                    ? DeserializeMetrics(metricsValue)
                    : new BridgeMetrics(),
                Error = ok || !body.TryGetValue("error", out object errorValue)
                    ? null
                    : DeserializeError(errorValue)
            };
        }

        private static BridgeMetrics DeserializeMetrics(object value)
        {
            var body = value as Dictionary<string, object>;
            return new BridgeMetrics
            {
                ElapsedMs = body != null && body.TryGetValue("elapsedMs", out object elapsedMs)
                    ? Convert.ToInt64(elapsedMs)
                    : 0,
                QueueWaitMs = body != null && body.TryGetValue("queueWaitMs", out object queueWaitMs)
                    ? Convert.ToInt64(queueWaitMs)
                    : (long?)null,
                RevitExecutionMs = body != null && body.TryGetValue("revitExecutionMs", out object revitExecutionMs)
                    ? Convert.ToInt64(revitExecutionMs)
                    : (long?)null
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
