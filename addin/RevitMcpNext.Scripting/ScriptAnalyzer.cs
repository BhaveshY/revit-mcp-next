using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Scripting
{
    /// <summary>
    /// Semantic deny-list analysis (SPEC §11.5-§11.6): every name, member access, object creation, indexer and attribute of
    /// the script is bound with the semantic model and checked against <see cref="DenyRules"/> by its resolved symbol
    /// (aliases, extension methods and inherited members included). Constructing Transaction, SubTransaction,
    /// TransactionGroup or any *EditScope marks the script as managing its own transactions (denied in read mode).
    /// The generated helper classes (__McpGuard, __McpTimeoutException) are skipped; user code that references them is denied.
    /// </summary>
    internal sealed class ScriptAnalyzer : CSharpSyntaxWalker
    {
        private const int MaxDenied = 20;

        private static readonly HashSet<string> GeneratedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "__McpGuard",
            "__McpTimeoutException"
        };

        private readonly SemanticModel _model;
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public ScriptAnalyzer(SemanticModel model)
            : base(SyntaxWalkerDepth.Node)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public List<ScriptDeniedSymbol> Denied { get; } = new List<ScriptDeniedSymbol>();

        public bool UsesTransactions { get; private set; }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            if (GeneratedTypes.Contains(node.Identifier.ValueText)) return;
            base.VisitClassDeclaration(node);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            Check(node);
            base.VisitIdentifierName(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            Check(node);
            base.VisitGenericName(node);
        }

        public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            Check(node);
            base.VisitElementAccessExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            CheckCreation(node);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            CheckCreation(node);
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitAttribute(AttributeSyntax node)
        {
            ISymbol ctor = Bind(node);
            if (ctor?.ContainingType != null) Report(node, ctor.ContainingType, DenyRules.ForType(ctor.ContainingType));
            base.VisitAttribute(node);
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            // Subscribing handlers to Revit events would keep script code alive (and running) after the script returns.
            if (node.IsKind(SyntaxKind.AddAssignmentExpression) || node.IsKind(SyntaxKind.SubtractAssignmentExpression))
            {
                if (Bind(node.Left) is IEventSymbol evt && DenyRules.NamespaceOf(evt.ContainingType).StartsWith("Autodesk.Revit", StringComparison.Ordinal))
                {
                    Report(node.Left, evt, DenyRules.Always);
                }
            }
            base.VisitAssignmentExpression(node);
        }

        public override void VisitUnsafeStatement(UnsafeStatementSyntax node)
        {
            Report(node, "unsafe", DenyRules.Always);
            base.VisitUnsafeStatement(node);
        }

        public override void VisitPointerType(PointerTypeSyntax node)
        {
            Report(node, "pointer types", DenyRules.Always);
            base.VisitPointerType(node);
        }

        public override void VisitFunctionPointerType(FunctionPointerTypeSyntax node)
        {
            Report(node, "function pointers", DenyRules.Always);
            base.VisitFunctionPointerType(node);
        }

        private void CheckCreation(ExpressionSyntax node)
        {
            ITypeSymbol type = _model.GetTypeInfo(node).Type;
            if (type == null) return;
            if (DenyRules.IsTransactionType(type))
            {
                UsesTransactions = true;
                Report(node, DenyRules.FullName(type) + ".ctor", DenyRules.Transaction);
            }
            Report(node, type, DenyRules.ForType(type));
            ISymbol ctor = Bind(node);
            if (ctor != null) Report(node, ctor, DenyRules.ForMember(ctor));
        }

        private void Check(ExpressionSyntax node)
        {
            ISymbol symbol = Bind(node);
            if (symbol == null)
            {
                ITypeSymbol type = _model.GetTypeInfo(node).Type;
                if (type != null && type.TypeKind == TypeKind.Dynamic) Report(node, "dynamic", DenyRules.Always);
                return;
            }

            if (IsGenerated(symbol))
            {
                Report(node, symbol.Name, DenyRules.Always);
                return;
            }

            switch (symbol)
            {
                case IAliasSymbol alias:
                    if (alias.Target is ITypeSymbol target) Report(node, target, DenyRules.ForType(target));
                    break;
                case ITypeSymbol type:
                    Report(node, type, DenyRules.ForType(type));
                    break;
                case IMethodSymbol _:
                case IPropertySymbol _:
                case IFieldSymbol _:
                case IEventSymbol _:
                    Report(node, symbol, DenyRules.ForMember(symbol));
                    break;
                case ILocalSymbol local:
                    if (local.Type?.TypeKind == TypeKind.Dynamic) Report(node, "dynamic", DenyRules.Always);
                    break;
                case IParameterSymbol parameter:
                    if (parameter.Type?.TypeKind == TypeKind.Dynamic) Report(node, "dynamic", DenyRules.Always);
                    break;
            }
        }

        private ISymbol Bind(SyntaxNode node)
        {
            try
            {
                SymbolInfo info = _model.GetSymbolInfo(node);
                return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool IsGenerated(ISymbol symbol)
        {
            for (ISymbol current = symbol; current != null; current = current.ContainingType)
            {
                if (GeneratedTypes.Contains(current.Name)) return true;
            }
            return false;
        }

        private void Report(SyntaxNode node, ISymbol symbol, string list)
        {
            if (list == null || symbol == null) return;
            Report(node, DenyRules.Describe(symbol), list);
        }

        private void Report(SyntaxNode node, string symbol, string list)
        {
            if (list == null || string.IsNullOrEmpty(symbol)) return;
            if (!_seen.Add(list + "|" + symbol)) return;
            if (Denied.Count >= MaxDenied) return;
            RoslynScriptCompiler.Position(node.GetLocation(), out int line, out int column);
            Denied.Add(new ScriptDeniedSymbol { Symbol = symbol, DenyList = list, Line = line, Column = column });
        }
    }
}
