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
        private static bool TryParseBuiltInCategory(string value, out BuiltInCategory category)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (Enum.TryParse(trimmed, true, out category)) return true;
            return Enum.TryParse("OST_" + trimmed.Replace(" ", string.Empty), true, out category);
        }

        private static Type ResolveElementType(string className)
        {
            string normalized = (className ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return null;
            Type type = typeof(Element).Assembly.GetType("Autodesk.Revit.DB." + normalized, false, true);
            return type != null && typeof(Element).IsAssignableFrom(type) ? type : null;
        }

        private static ElementId CreateElementId(string value)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long idValue))
            {
                throw new ArgumentException("Element id must be numeric: " + value);
            }

            try
            {
                return (ElementId)Activator.CreateInstance(typeof(ElementId), idValue);
            }
            catch
            {
                return (ElementId)Activator.CreateInstance(typeof(ElementId), Convert.ToInt32(idValue));
            }
        }

        private static object GetPropertyValue(object target, string propertyName)
        {
            if (target == null || string.IsNullOrWhiteSpace(propertyName)) return null;
            try
            {
                return target.GetType().GetProperty(propertyName)?.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static object InvokeParameterless(object target, string methodName)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName)) return null;
            try
            {
                return target.GetType().GetMethod(methodName, Type.EmptyTypes)?.Invoke(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static object InvokeMethod(object target, string methodName, params object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName)) return null;
            try
            {
                Type[] argumentTypes = args?.Select(arg => arg?.GetType() ?? typeof(object)).ToArray() ?? Type.EmptyTypes;
                return target.GetType().GetMethod(methodName, argumentTypes)?.Invoke(target, args);
            }
            catch
            {
                return null;
            }
        }

        private static ElementId GetReflectedElementId(object target, string propertyName)
        {
            object value = GetPropertyValue(target, propertyName);
            return value is ElementId elementId ? elementId : ElementId.InvalidElementId;
        }

        private static bool IsValidElementId(ElementId id)
        {
            return id != null && GetElementIdValue(id) >= 0;
        }

        private static string ToElementIdString(ElementId id)
        {
            return id == null ? string.Empty : GetElementIdValue(id).ToString(CultureInfo.InvariantCulture);
        }

        private static string ToWorksetIdString(WorksetId id)
        {
            if (id == null) return string.Empty;

            object value = typeof(WorksetId).GetProperty("IntegerValue")?.GetValue(id, null);
            return value == null ? id.ToString() : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2027
            return id.Value;
#else
            object value = typeof(ElementId).GetProperty("Value")?.GetValue(id, null);
            if (value != null) return Convert.ToInt64(value, CultureInfo.InvariantCulture);

#pragma warning disable CS0618
            return id.IntegerValue;
#pragma warning restore CS0618
#endif
        }
    }
}
