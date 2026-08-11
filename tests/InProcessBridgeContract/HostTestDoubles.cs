using Autodesk.Revit.UI;
using RevitMcpNext.Contracts;

namespace Autodesk.Revit.UI
{
    public sealed class UIApplication
    {
    }
}

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class RevitRequestQueue
    {
    }

    internal sealed class TransactionService
    {
    }

    internal sealed class DocumentGenerationTracker
    {
    }

    internal sealed class RevitExternalEventHandler
    {
        public RevitExternalEventHandler(
            RevitRequestQueue queue,
            TransactionService transactions,
            DocumentGenerationTracker generations)
        {
        }

        public BridgeResponseEnvelope HandleDirect(UIApplication app, BridgeRequestEnvelope request)
        {
            throw new System.InvalidOperationException("Protocol contract tests must return before Revit execution.");
        }
    }
}
