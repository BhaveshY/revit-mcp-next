using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RevitMcpNext.Scripting
{
    /// <summary>
    /// Inserts the cooperative guards of SPEC §11.4 into the script:
    /// <list type="bullet">
    /// <item><c>global::__McpGuard.Tick();</c> as the first statement of every loop body (for, foreach, while, do) and
    /// after every label (goto loops): a deadline or cancel request throws out of the innermost loop on its next pass;</item>
    /// <item><c>Enter(); try { body } finally { Exit(); }</c> around every method, local function, lambda and anonymous
    /// method body: Enter ticks and caps the call depth at 256, so runaway recursion fails cleanly instead of overflowing
    /// Revit's stack.</item>
    /// </list>
    /// Expression-bodied lambdas and local functions are turned into blocks; whether the body is a statement or a
    /// returned value comes from the semantic model of the original tree. The generated <c>__McpGuard</c> class is left
    /// alone. Runs on a tree that compiled without errors.
    /// </summary>
    internal sealed class GuardRewriter : CSharpSyntaxRewriter
    {
        private const string Guard = "global::__McpGuard";

        private readonly Dictionary<TextSpan, bool> _voidBodies = new Dictionary<TextSpan, bool>();

        public GuardRewriter(SemanticModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            // Classify every expression-bodied lambda / local function of the original tree: true = the body is a
            // statement (void, or an async Task/ValueTask), false = its value is returned.
            foreach (SyntaxNode node in model.SyntaxTree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case LambdaExpressionSyntax lambda when lambda.ExpressionBody != null:
                        // Expression trees (Expression<Func<...>>) cannot hold block bodies: leave those lambdas alone.
                        ITypeSymbol converted = model.GetTypeInfo(lambda).ConvertedType;
                        if (converted != null && converted.Name == "Expression" && DenyRules.NamespaceOf(converted) == "System.Linq.Expressions") break;
                        if (model.GetSymbolInfo(lambda).Symbol is IMethodSymbol lambdaMethod) _voidBodies[lambda.Span] = IsVoidLike(lambdaMethod);
                        break;
                    case LocalFunctionStatementSyntax local when local.ExpressionBody != null:
                        if (model.GetDeclaredSymbol(local) is IMethodSymbol localMethod) _voidBodies[local.Span] = IsVoidLike(localMethod);
                        break;
                }
            }
        }

        public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            if (node.Identifier.ValueText == "__McpGuard" || node.Identifier.ValueText == "__McpTimeoutException") return node;
            return base.VisitClassDeclaration(node);
        }

        // ---- functions: Enter/try/finally/Exit

        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node);
            return visited.Body == null ? visited : visited.WithBody(Wrap(visited.Body));
        }

        public override SyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            bool isExpression = node.ExpressionBody != null;
            bool voidLike = isExpression && _voidBodies.TryGetValue(node.Span, out bool v) && v;
            bool known = !isExpression || _voidBodies.ContainsKey(node.Span);
            var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node);
            if (visited.Body != null) return visited.WithBody(Wrap(visited.Body));
            if (visited.ExpressionBody == null || !known) return visited;
            BlockSyntax body = Wrap(BodyBlock(visited.ExpressionBody.Expression, voidLike));
            return visited.WithExpressionBody(null).WithSemicolonToken(default(SyntaxToken)).WithBody(body);
        }

        public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            bool known = _voidBodies.TryGetValue(node.Span, out bool voidLike);
            var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node);
            if (visited.Block != null) return visited.WithBlock(Wrap(visited.Block));
            if (visited.ExpressionBody == null || !known) return visited;
            return visited.WithExpressionBody(null).WithBlock(Wrap(BodyBlock(visited.ExpressionBody, voidLike)));
        }

        public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            bool known = _voidBodies.TryGetValue(node.Span, out bool voidLike);
            var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node);
            if (visited.Block != null) return visited.WithBlock(Wrap(visited.Block));
            if (visited.ExpressionBody == null || !known) return visited;
            return visited.WithExpressionBody(null).WithBlock(Wrap(BodyBlock(visited.ExpressionBody, voidLike)));
        }

        public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        {
            var visited = (AnonymousMethodExpressionSyntax)base.VisitAnonymousMethodExpression(node);
            return visited.Block == null ? visited : visited.WithBlock(Wrap(visited.Block));
        }

        // ---- loops and labels: Tick first

        public override SyntaxNode VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node);
            return visited.WithStatement(Ticked(visited.Statement));
        }

        public override SyntaxNode VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node);
            return visited.WithStatement(Ticked(visited.Statement));
        }

        public override SyntaxNode VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node);
            return visited.WithStatement(Ticked(visited.Statement));
        }

        public override SyntaxNode VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node);
            return visited.WithStatement(Ticked(visited.Statement));
        }

        public override SyntaxNode VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node);
            return visited.WithStatement(Ticked(visited.Statement));
        }

        public override SyntaxNode VisitLabeledStatement(LabeledStatementSyntax node)
        {
            var visited = (LabeledStatementSyntax)base.VisitLabeledStatement(node);
            return visited.WithStatement(Ticked(visited.Statement));
        }

        // ---- helpers

        private static bool IsVoidLike(IMethodSymbol method)
        {
            if (method.ReturnsVoid) return true;
            if (!method.IsAsync) return false;
            ITypeSymbol type = method.ReturnType;
            if (type is INamedTypeSymbol named && named.IsGenericType) return false;
            string name = type?.Name ?? string.Empty;
            return name == "Task" || name == "ValueTask";
        }

        private static StatementSyntax Call(string member)
        {
            return SyntaxFactory.ParseStatement(Guard + "." + member + "();");
        }

        /// <summary>{ Tick(); statement } (an existing block gets Tick() as its first statement).</summary>
        private static StatementSyntax Ticked(StatementSyntax statement)
        {
            if (statement is BlockSyntax block) return block.WithStatements(block.Statements.Insert(0, Call("Tick")));
            return SyntaxFactory.Block(Call("Tick"), statement);
        }

        /// <summary>{ Enter(); try { body } finally { Exit(); } }.</summary>
        private static BlockSyntax Wrap(BlockSyntax body)
        {
            TryStatementSyntax guarded = SyntaxFactory.TryStatement(
                body,
                default(SyntaxList<CatchClauseSyntax>),
                SyntaxFactory.FinallyClause(SyntaxFactory.Block(Call("Exit"))));
            return SyntaxFactory.Block(Call("Enter"), guarded);
        }

        /// <summary>The block form of an expression body: <c>{ expr; }</c> or <c>{ return expr; }</c> (throw stays a statement).</summary>
        private static BlockSyntax BodyBlock(ExpressionSyntax expression, bool voidLike)
        {
            if (expression is ThrowExpressionSyntax thrown) return SyntaxFactory.Block(SyntaxFactory.ThrowStatement(thrown.Expression));
            if (voidLike) return SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(expression));
            return SyntaxFactory.Block(SyntaxFactory.ReturnStatement(expression));
        }
    }
}
