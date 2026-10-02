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
        private BridgeResponseEnvelope HandleCreateModelDeliveryFixture(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            try
            {
                return Success(request, ModelDeliveryFixture.Create(app, request.Payload), sw);
            }
            catch (ModelDeliveryException ex)
            {
                return Failure(request, ex.Code, ex.Message, sw);
            }
        }

        private BridgeResponseEnvelope HandlePreviewModelDelivery(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            try
            {
                DeliveryTargetBinding target = ResolveDeliveryTarget(app, request, sw, out BridgeResponseEnvelope failure);
                if (failure != null) return failure;
                return Success(request, _modelDelivery.Preview(app, request.SessionId, target, request.Payload), sw, generation: target.Generation);
            }
            catch (ModelDeliveryException ex)
            {
                return Failure(request, ex.Code, ex.Message, sw);
            }
        }

        private BridgeResponseEnvelope HandleInspectModelDelivery(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            try
            {
                DeliveryTargetBinding target = ResolveDeliveryTarget(app, request, sw, out BridgeResponseEnvelope failure);
                if (failure != null) return failure;
                return Success(request, _modelDelivery.Inspect(app, target, request.Payload), sw, generation: target.Generation);
            }
            catch (ModelDeliveryException ex)
            {
                return Failure(request, ex.Code, ex.Message, sw);
            }
        }

        private BridgeResponseEnvelope HandleExecuteModelDelivery(UIApplication app, BridgeRequestEnvelope request, Stopwatch sw)
        {
            try
            {
                DeliveryTargetBinding target = ResolveDeliveryTarget(app, request, sw, out BridgeResponseEnvelope failure);
                if (failure != null) return failure;
                return Success(request, _modelDelivery.Execute(app, request.SessionId, target, request.Payload), sw, generation: target.Generation);
            }
            catch (ModelDeliveryException ex)
            {
                return Failure(request, ex.Code, ex.Message, sw);
            }
        }

        private DeliveryTargetBinding ResolveDeliveryTarget(
            UIApplication app,
            BridgeRequestEnvelope request,
            Stopwatch sw,
            out BridgeResponseEnvelope failure)
        {
            failure = null;
            Document document = ResolveDocument(app, request);
            if (document == null)
            {
                failure = Failure(
                    request,
                    "TARGET_SELECTION_REQUIRED",
                    "Model Delivery requires an exact Revit session target. Open a pilot control project, call revit.list_documents, then revit.set_target with instanceId and documentFingerprint.",
                    sw);
                return null;
            }

            failure = ValidateExpectedGeneration(request, document, sw, out long generation);
            if (failure != null) return null;
            return new DeliveryTargetBinding(
                _runtimeInstanceId,
                ComputeDocumentFingerprint(document),
                generation,
                document.PathName,
                GetDocumentCentralModelPath(document));
        }

        private BridgeResponseEnvelope HandleGetModelDeliveryStatus(BridgeRequestEnvelope request, Stopwatch sw)
        {
            try
            {
                return Success(request, _modelDelivery.GetStatus(request.SessionId, request.Payload), sw);
            }
            catch (ModelDeliveryException ex)
            {
                return Failure(request, ex.Code, ex.Message, sw);
            }
        }

        private BridgeResponseEnvelope HandleCancelModelDelivery(BridgeRequestEnvelope request, Stopwatch sw)
        {
            try
            {
                return Success(request, _modelDelivery.Cancel(request.SessionId, request.Payload), sw);
            }
            catch (ModelDeliveryException ex)
            {
                return Failure(request, ex.Code, ex.Message, sw);
            }
        }
    }
}
