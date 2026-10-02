using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private static void AddWarningOnce(List<BridgeWarning> warnings, string code, string message)
        {
            if (warnings.Any(warning => string.Equals(warning.Code, code, StringComparison.OrdinalIgnoreCase))) return;
            warnings.Add(new BridgeWarning
            {
                Code = code,
                Message = message
            });
        }

        private static BridgeResponseEnvelope Success(
            BridgeRequestEnvelope request,
            object data,
            Stopwatch sw,
            List<BridgeWarning> warnings = null,
            BridgeMetrics metrics = null,
            long? generation = null)
        {
            sw.Stop();
            BridgeMetrics actualMetrics = metrics ?? new BridgeMetrics();
            actualMetrics.ElapsedMs = sw.ElapsedMilliseconds;
            return new BridgeResponseEnvelope
            {
                Ok = true,
                RequestId = request.RequestId,
                Data = data,
                Warnings = warnings ?? new List<BridgeWarning>(),
                Metrics = actualMetrics,
                Generation = generation ?? 0
            };
        }

        private static BridgeResponseEnvelope Failure(BridgeRequestEnvelope request, string code, string message, Stopwatch sw = null)
        {
            sw?.Stop();
            return new BridgeResponseEnvelope
            {
                Ok = false,
                RequestId = request.RequestId,
                Error = new BridgeError
                {
                    Code = code,
                    Message = message,
                    Recoverable = true
                },
                Metrics = new BridgeMetrics { ElapsedMs = sw?.ElapsedMilliseconds ?? 0 }
            };
        }
    }
}
