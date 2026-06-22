using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpNext.Addin.Revit;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Ipc
{
    internal sealed class NamedPipeHost : IDisposable
    {
        private readonly string _pipeName;
        private readonly RevitRequestQueue _requestQueue;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private Task _acceptLoop;

        public NamedPipeHost(string pipeName, RevitRequestQueue requestQueue)
        {
            _pipeName = pipeName;
            _requestQueue = requestQueue;
        }

        public void Start()
        {
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try
            {
                _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Shutdown should not block Revit.
            }
            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 4,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        using (server)
                        {
                            await HandleClientAsync(server, cancellationToken).ConfigureAwait(false);
                        }
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
        {
            string requestJson = await FramedPipeTransport.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);

            // JSON parsing is intentionally not implemented in the skeleton to avoid choosing
            // a serializer before the canonical contract generator lands.
            var request = new BridgeRequestEnvelope
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Operation = "raw",
                PayloadJson = requestJson
            };

            BridgeResponseEnvelope response = await _requestQueue.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
            string responseJson = response.Ok
                ? "{\"ok\":true,\"requestId\":\"" + Escape(response.RequestId) + "\",\"data\":{},\"warnings\":[],\"metrics\":{\"elapsedMs\":" + response.Metrics.ElapsedMs + "}}"
                : "{\"ok\":false,\"requestId\":\"" + Escape(response.RequestId) + "\",\"error\":{\"code\":\"" + Escape(response.Error?.Code) + "\",\"message\":\"" + Escape(response.Error?.Message) + "\",\"recoverable\":true},\"warnings\":[]}";

            await FramedPipeTransport.WriteFrameAsync(stream, responseJson, cancellationToken).ConfigureAwait(false);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
