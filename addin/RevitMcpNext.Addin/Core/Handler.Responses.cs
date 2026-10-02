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
        private static void AddWarningOnce(List<LegacyWarning> warnings, string code, string message)
        {
            if (warnings.Any(warning => string.Equals(warning.Code, code, StringComparison.OrdinalIgnoreCase))) return;
            warnings.Add(new LegacyWarning
            {
                Code = code,
                Message = message
            });
        }

        private static LegacyResponse Success(
            LegacyRequest request,
            object data,
            Stopwatch sw,
            List<LegacyWarning> warnings = null,
            LegacyMetrics metrics = null,
            long? generation = null)
        {
            sw.Stop();
            LegacyMetrics actualMetrics = metrics ?? new LegacyMetrics();
            actualMetrics.ElapsedMs = sw.ElapsedMilliseconds;
            return new LegacyResponse
            {
                Ok = true,
                RequestId = request.RequestId,
                Data = data,
                Warnings = warnings ?? new List<LegacyWarning>(),
                Metrics = actualMetrics,
                Generation = generation ?? 0
            };
        }

        private static LegacyResponse Failure(LegacyRequest request, string code, string message, Stopwatch sw = null)
        {
            sw?.Stop();
            return new LegacyResponse
            {
                Ok = false,
                RequestId = request.RequestId,
                Error = new LegacyError
                {
                    Code = code,
                    Message = message,
                    Recoverable = true
                },
                Metrics = new LegacyMetrics { ElapsedMs = sw?.ElapsedMilliseconds ?? 0 }
            };
        }
    }
}
