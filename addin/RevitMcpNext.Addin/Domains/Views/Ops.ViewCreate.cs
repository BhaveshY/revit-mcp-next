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
        private static bool ViewNameExists(Document document, string name)
        {
            string normalized = NormalizeOptionalText(name);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            return new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(view => string.Equals(SafeElementName(view), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }
}
