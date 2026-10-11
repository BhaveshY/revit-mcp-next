using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace RevitMcpNext.Scripting
{
    /// <summary>
    /// The semantic deny lists of SPEC §11.6, matched on resolved symbols (never on text):
    /// "always" (denied in every mode), "lifecycle" (read and dry_run always; commit unless codeExecution.allowUnsafeApis)
    /// and "transaction" (constructing Transaction/SubTransaction/TransactionGroup/*EditScope; denied in read mode only).
    /// This is a guardrail against accidental damage by generated code, not a security sandbox: the code runs in Revit's
    /// process with the user's rights, which is why code execution is opt-in and audited.
    /// </summary>
    internal static class DenyRules
    {
        public const string Always = "always";
        public const string Lifecycle = "lifecycle";
        public const string Transaction = "transaction";

        /// <summary>A member rule: declaring type (or a base type) + member name ("*" suffix = prefix match, "*" = any member).</summary>
        private sealed class MemberRule
        {
            public MemberRule(string type, string member, string list)
            {
                Type = type;
                Member = member;
                List = list;
            }

            public string Type { get; }
            public string Member { get; }
            public string List { get; }

            public bool Matches(string memberName)
            {
                if (Member == "*") return true;
                if (Member.EndsWith("*", StringComparison.Ordinal)) return memberName.StartsWith(Member.Substring(0, Member.Length - 1), StringComparison.Ordinal);
                return string.Equals(Member, memberName, StringComparison.Ordinal);
            }
        }

        /// <summary>Namespaces whose every type is denied (prefix match on the full namespace).</summary>
        private static readonly string[] DeniedNamespaces =
        {
            "System.Net",
            "Microsoft.Win32",
            "System.Reflection.Emit",
            "System.Runtime.Loader",
            "System.IO.Pipes",
            "System.IO.MemoryMappedFiles",
            "System.IO.IsolatedStorage",
            "System.Windows.Forms",
            "UIFrameworkServices",
            "UIFramework",
            "Autodesk.Windows",
            "AdWindows"
        };

        /// <summary>Types whose every use (reference, construction, members) is denied.</summary>
        private static readonly HashSet<string> DeniedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Diagnostics.Process",
            "System.Diagnostics.ProcessStartInfo",
            "System.Runtime.InteropServices.Marshal",
            "System.Runtime.InteropServices.DllImportAttribute",
            "System.Runtime.InteropServices.LibraryImportAttribute",
            "System.Runtime.InteropServices.NativeLibrary",
            "System.Runtime.CompilerServices.Unsafe",
            "System.AppDomain",
            "System.Activator",
            "System.Threading.Thread",
            "System.Threading.ThreadPool",
            "System.Threading.Timer",
            "System.Timers.Timer",
            "System.Threading.Tasks.Parallel",
            "System.Threading.Tasks.TaskFactory",
            "System.Windows.Window",
            "System.IO.FileStream",
            "System.IO.Compression.ZipFile",
            "System.IO.Compression.ZipFileExtensions",
            "Autodesk.Revit.UI.ExternalEvent",
            "Autodesk.Revit.DB.UpdaterRegistry"
        };

        private static readonly List<MemberRule> MemberRules = new List<MemberRule>
        {
            // ---- always
            new MemberRule("System.Environment", "Exit", Always),
            new MemberRule("System.Environment", "FailFast", Always),
            new MemberRule("System.Environment", "SetEnvironmentVariable", Always),
            new MemberRule("System.Reflection.Assembly", "Load*", Always),
            new MemberRule("System.Reflection.Assembly", "UnsafeLoadFrom", Always),
            new MemberRule("System.Reflection.Assembly", "ReflectionOnlyLoad*", Always),
            new MemberRule("System.Reflection.Assembly", "CreateInstance", Always),
            new MemberRule("System.Type", "GetType", Always),
            new MemberRule("System.Type", "GetTypeFromProgID", Always),
            new MemberRule("System.Type", "GetTypeFromCLSID", Always),
            new MemberRule("System.Type", "InvokeMember", Always),
            new MemberRule("System.Reflection.MethodBase", "Invoke", Always),
            new MemberRule("System.Reflection.PropertyInfo", "SetValue", Always),
            new MemberRule("System.Reflection.FieldInfo", "SetValue", Always),
            new MemberRule("System.Delegate", "CreateDelegate", Always),
            new MemberRule("System.Delegate", "DynamicInvoke", Always),
            new MemberRule("System.Threading.Tasks.Task", "Run", Always),
            new MemberRule("System.Threading.Tasks.Task", "Factory", Always),
            new MemberRule("System.IO.File", "Write*", Always),
            new MemberRule("System.IO.File", "Append*", Always),
            new MemberRule("System.IO.File", "Create*", Always),
            new MemberRule("System.IO.File", "Delete", Always),
            new MemberRule("System.IO.File", "Move", Always),
            new MemberRule("System.IO.File", "Copy", Always),
            new MemberRule("System.IO.File", "Replace", Always),
            new MemberRule("System.IO.File", "Open", Always),
            new MemberRule("System.IO.File", "OpenWrite", Always),
            new MemberRule("System.IO.File", "OpenHandle", Always),
            new MemberRule("System.IO.File", "Set*", Always),
            new MemberRule("System.IO.File", "Encrypt", Always),
            new MemberRule("System.IO.File", "Decrypt", Always),
            new MemberRule("System.IO.Directory", "Create*", Always),
            new MemberRule("System.IO.Directory", "Delete", Always),
            new MemberRule("System.IO.Directory", "Move", Always),
            new MemberRule("System.IO.Directory", "Set*", Always),
            new MemberRule("System.IO.FileSystemInfo", "Delete", Always),
            new MemberRule("System.IO.FileSystemInfo", "Create*", Always),
            new MemberRule("System.IO.FileSystemInfo", "Refresh", Always),
            new MemberRule("System.IO.FileInfo", "Create*", Always),
            new MemberRule("System.IO.FileInfo", "Append*", Always),
            new MemberRule("System.IO.FileInfo", "Delete", Always),
            new MemberRule("System.IO.FileInfo", "MoveTo", Always),
            new MemberRule("System.IO.FileInfo", "CopyTo", Always),
            new MemberRule("System.IO.FileInfo", "Replace", Always),
            new MemberRule("System.IO.FileInfo", "Open", Always),
            new MemberRule("System.IO.FileInfo", "OpenWrite", Always),
            new MemberRule("System.IO.FileInfo", "Encrypt", Always),
            new MemberRule("System.IO.FileInfo", "Decrypt", Always),
            new MemberRule("System.IO.DirectoryInfo", "Create*", Always),
            new MemberRule("System.IO.DirectoryInfo", "Delete", Always),
            new MemberRule("System.IO.DirectoryInfo", "MoveTo", Always),
            new MemberRule("Autodesk.Revit.UI.UIApplication", "PostCommand", Always),
            new MemberRule("Autodesk.Revit.UI.UIControlledApplication", "*", Always),

            // ---- lifecycle (side effects outside the undo stack; a rollback cannot take them back)
            new MemberRule("Autodesk.Revit.DB.Document", "Save", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "SaveAs", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "SaveCloudModel", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "SaveAsCloudModel", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "SaveToProjectAsImage", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "Close", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "SynchronizeWithCentral", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "ReloadLatest", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "Export*", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "EnableWorksharing", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "EnableCloudWorksharing", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "EditFamily", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.Document", "Print", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.PrintManager", "SubmitPrint", Lifecycle),
            new MemberRule("Autodesk.Revit.ApplicationServices.Application", "OpenDocumentFile", Lifecycle),
            new MemberRule("Autodesk.Revit.ApplicationServices.Application", "OpenBuildingComponentDocument", Lifecycle),
            new MemberRule("Autodesk.Revit.ApplicationServices.Application", "New*Document", Lifecycle),
            new MemberRule("Autodesk.Revit.ApplicationServices.Application", "CopyModel", Lifecycle),
            new MemberRule("Autodesk.Revit.UI.UIApplication", "OpenAndActivateDocument", Lifecycle),
            new MemberRule("Autodesk.Revit.UI.UIDocument", "SaveAndClose", Lifecycle),
            new MemberRule("Autodesk.Revit.UI.UIDocument", "SaveAs", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.RevitLinkType", "Reload", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.RevitLinkType", "ReloadFrom", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.RevitLinkType", "Unload", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.RevitLinkType", "LoadFrom", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.WorksharingUtils", "*", Lifecycle),
            new MemberRule("Autodesk.Revit.DB.TransmissionData", "WriteTransmissionData", Lifecycle)
        };

        /// <summary>Types whose construction makes the code manage its own transactions.</summary>
        private static readonly HashSet<string> TransactionTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Autodesk.Revit.DB.Transaction",
            "Autodesk.Revit.DB.SubTransaction",
            "Autodesk.Revit.DB.TransactionGroup"
        };

        /// <summary>The full metadata name of a type ("System.IO.File"), generic arguments removed.</summary>
        public static string FullName(ITypeSymbol type)
        {
            if (type == null) return string.Empty;
            if (type is IArrayTypeSymbol array) return FullName(array.ElementType);
            if (type is IPointerTypeSymbol pointer) return FullName(pointer.PointedAtType);
            ITypeSymbol original = type.OriginalDefinition ?? type;
            var parts = new List<string>();
            ITypeSymbol current = original;
            while (current != null)
            {
                parts.Insert(0, current.MetadataName);
                current = current.ContainingType;
            }
            string ns = NamespaceOf(original);
            return ns.Length == 0 ? string.Join(".", parts) : ns + "." + string.Join(".", parts);
        }

        public static string NamespaceOf(ISymbol symbol)
        {
            INamespaceSymbol ns = symbol?.ContainingNamespace;
            if (ns == null || ns.IsGlobalNamespace) return string.Empty;
            return ns.ToDisplayString();
        }

        /// <summary>The deny list a type reference falls under, or null.</summary>
        public static string ForType(ITypeSymbol type)
        {
            if (type == null) return null;
            if (type is IArrayTypeSymbol array) return ForType(array.ElementType);
            if (type is IPointerTypeSymbol) return Always;
            if (type.TypeKind == TypeKind.Dynamic) return Always;
            string full = FullName(type);
            if (DeniedTypes.Contains(full)) return Always;
            string ns = NamespaceOf(type.OriginalDefinition ?? type);
            foreach (string denied in DeniedNamespaces)
            {
                if (ns == denied || ns.StartsWith(denied + ".", StringComparison.Ordinal)) return Always;
            }
            return null;
        }

        /// <summary>The deny list a member use falls under (declaring type or any base type), or null.</summary>
        public static string ForMember(ISymbol member)
        {
            if (member == null) return null;
            INamedTypeSymbol container = member.ContainingType;
            if (container == null) return null;
            string fromType = ForType(container);
            if (fromType != null) return fromType;
            string name = MemberName(member);
            for (INamedTypeSymbol type = container; type != null; type = type.BaseType)
            {
                string full = FullName(type);
                foreach (MemberRule rule in MemberRules)
                {
                    if (rule.Type == full && rule.Matches(name)) return rule.List;
                }
            }
            return null;
        }

        /// <summary>True when constructing this type means the code runs its own transactions or edit scopes.</summary>
        public static bool IsTransactionType(ITypeSymbol type)
        {
            if (type == null) return false;
            for (ITypeSymbol current = type; current != null; current = current.BaseType)
            {
                string full = FullName(current);
                if (TransactionTypes.Contains(full)) return true;
                if (full.StartsWith("Autodesk.Revit.DB.", StringComparison.Ordinal) && current.Name.EndsWith("EditScope", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Accessors map to their property/event name; constructors to ".ctor".</summary>
        private static string MemberName(ISymbol member)
        {
            if (member is IMethodSymbol method)
            {
                if (method.AssociatedSymbol != null) return method.AssociatedSymbol.Name;
                if (method.MethodKind == MethodKind.ReducedExtension && method.ReducedFrom != null) return method.ReducedFrom.Name;
            }
            return member.Name;
        }

        /// <summary>"System.Diagnostics.Process.Start" for messages.</summary>
        public static string Describe(ISymbol symbol)
        {
            if (symbol == null) return string.Empty;
            if (symbol is ITypeSymbol type) return FullName(type);
            string container = symbol.ContainingType == null ? NamespaceOf(symbol) : FullName(symbol.ContainingType);
            string name = symbol is IMethodSymbol m && m.MethodKind == MethodKind.Constructor ? "ctor" : MemberName(symbol);
            return container.Length == 0 ? name : container + "." + name;
        }

        public static IEnumerable<string> AllDeniedNamespaces() => DeniedNamespaces.ToList();
    }
}
