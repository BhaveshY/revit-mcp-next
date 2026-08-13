using System;
using System.Collections.Generic;
using RevitMcpNext.Addin;
using RevitMcpNext.Addin.Ipc;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.InProcessBridgeContract
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                AssertDeprecatedProtocolAlias();
                AssertFailure(RequestJson(null), "PROTOCOL_VERSION_REQUIRED");
                AssertFailure(RequestJson("1900-01-01"), "PROTOCOL_VERSION_MISMATCH");
                AssertFailure(RequestJson(BridgeProtocol.Version), "NO_UI_APPLICATION");
                Console.WriteLine("In-process bridge protocol contract checks passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void AssertDeprecatedProtocolAlias()
        {
            var alias = typeof(BridgeRequestEnvelope).GetProperty("ProtocolVersion");
            if (alias == null || !Attribute.IsDefined(alias, typeof(ObsoleteAttribute)))
            {
                throw new InvalidOperationException("The deprecated ProtocolVersion compatibility alias is missing.");
            }

            var request = new BridgeRequestEnvelope();
            alias.SetValue(request, "compatibility-test", null);
            if (!string.Equals(request.BridgeProtocolVersion, "compatibility-test", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ProtocolVersion does not forward to BridgeProtocolVersion.");
            }
        }

        private static string RequestJson(string bridgeProtocolVersion)
        {
            var request = new Dictionary<string, object>
            {
                ["requestId"] = "in-process-protocol-test",
                ["sessionId"] = "contract-test",
                ["operation"] = "status",
                ["operationKind"] = "read",
                ["timeoutMs"] = 5000,
                ["payload"] = new Dictionary<string, object>()
            };
            if (bridgeProtocolVersion != null) request["protocolVersion"] = bridgeProtocolVersion;
            return JsonWireCodec.Serialize(request);
        }

        private static void AssertFailure(string requestJson, string expectedCode)
        {
            string responseJson = RevitMcpInProcessBridge.ExecuteJson(null, requestJson);
            var response = JsonWireCodec.DeserializeObject(responseJson) as Dictionary<string, object>;
            var error = response != null && response.TryGetValue("error", out object errorValue)
                ? errorValue as Dictionary<string, object>
                : null;
            string actualCode = error != null && error.TryGetValue("code", out object codeValue)
                ? Convert.ToString(codeValue)
                : null;
            if (!string.Equals(actualCode, expectedCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Expected in-process error " + expectedCode + " but received " + (actualCode ?? "(none)") + ".");
            }
        }
    }
}
