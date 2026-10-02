using System;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Binds a handler to a registry key (SPEC §9.3). Keys are exact ("create_elements.wall") or a tool wildcard
    /// ("list.*", exact bindings win). Methods are static, in any type of the add-in assembly, with one of two shapes:
    /// <code>
    /// static OpResult X(RequestContext ctx)   // read, ui, lifecycle, code (and writes that manage their own transactions)
    /// static void     X(ChangeContext c)      // write ops run by the ChangeEngine (tx in | own | none); ui ops with tx in
    /// </code>
    /// Catalog keys take all metadata (kind, scope, tx, blast, ...) from the embedded catalog; the named properties
    /// below are used only for internal keys outside the catalog (prefixes "dev." and "test.").
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    internal sealed class OpAttribute : Attribute
    {
        public OpAttribute(string key)
        {
            Key = key ?? string.Empty;
        }

        public string Key { get; }

        /// <summary>Internal keys only: read | write | ui | lifecycle | code.</summary>
        public string Kind { get; set; } = "read";

        /// <summary>Internal keys only: none | any | project | family | project_or_family.</summary>
        public string Scope { get; set; } = "none";

        /// <summary>Internal keys only: none | in | own | temp | lifecycle.</summary>
        public string Tx { get; set; } = "none";

        /// <summary>Internal keys only: comma-separated blast rules (delete,bulk,create,...).</summary>
        public string Blast { get; set; } = string.Empty;

        /// <summary>Internal keys only: needs the UI-active document.</summary>
        public bool Ui { get; set; }

        /// <summary>Internal keys only: may run from the Idling fallback.</summary>
        public bool Idle { get; set; } = true;

        /// <summary>Internal keys only: callable from the in-process bridge.</summary>
        public bool Inproc { get; set; }

        /// <summary>Internal keys only: minimum Revit year.</summary>
        public int Min { get; set; } = 2024;

        /// <summary>Internal keys only: never | auto | always.</summary>
        public string Job { get; set; } = "never";

        /// <summary>Internal keys only: warnings roll back.</summary>
        public bool Strict { get; set; }

        /// <summary>Internal keys only: allowed inside change_set.</summary>
        public bool Cs { get; set; }
    }
}
