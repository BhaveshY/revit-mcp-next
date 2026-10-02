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
        private BridgeResponseEnvelope HandleCancel(BridgeRequestEnvelope request, Stopwatch sw)
        {
            string requestId = GetString(request.Payload, "requestId");
            string reason = GetString(request.Payload, "reason");
            bool cancelled = _queue.TryCancelQueued(requestId, reason);
            var data = new Dictionary<string, object>
            {
                ["cancelled"] = cancelled,
                ["message"] = cancelled
                    ? "Queued request cancelled before Revit processed it."
                    : "No queued cancellable request matched. In-flight Revit API work cannot be interrupted safely."
            };

            if (!string.IsNullOrWhiteSpace(requestId)) data["requestId"] = requestId;
            if (!string.IsNullOrWhiteSpace(reason)) data["reason"] = reason;

            return Success(request, data, sw);
        }
    }
}
