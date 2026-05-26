using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Graph;

public class EdgeExtractor
{
    public IReadOnlyList<GraphEdge> Extract(
        SemanticModel model,
        CompilationUnitSyntax root,
        CancellationToken ct = default)
    {
        var edges = new List<GraphEdge>();
        var seen = new HashSet<(string From, string To, string Type)>();

        var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

        foreach (var typeDecl in typeDeclarations)
        {
            var typeSymbol = model.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
            if (typeSymbol is null)
                continue;

            var sourceNode = NodeFor(typeSymbol);
            if (sourceNode is null)
                continue;

            // INHERITS / IMPLEMENTS edges from base list
            if (typeDecl.BaseList is not null)
            {
                foreach (var baseTypeSyntax in typeDecl.BaseList.Types.OfType<SimpleBaseTypeSyntax>())
                {
                    var baseSymbol = model.GetTypeInfo(baseTypeSyntax.Type, ct).Type as INamedTypeSymbol;
                    if (baseSymbol is null || !IsSourceSymbol(baseSymbol))
                        continue;

                    var targetNode = NodeFor(baseSymbol);
                    if (targetNode is null)
                        continue;

                    var edgeType = baseSymbol.TypeKind == TypeKind.Interface ? "IMPLEMENTS" : "INHERITS";
                    AddEdge(sourceNode, targetNode, edgeType, seen, edges);
                }
            }

            // INJECTS edges from constructor parameters
            var ctors = typeDecl.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Where(c => !c.Modifiers.Any(m => m.ValueText == "static"))
                .ToList();

            if (ctors.Count > 0)
            {
                var chosen = ctors.MaxBy(c => c.ParameterList.Parameters.Count)!;
                foreach (var param in chosen.ParameterList.Parameters)
                {
                    if (param.Type is null) continue;
                    var paramSymbol = model.GetTypeInfo(param.Type, ct).Type;
                    if (paramSymbol is null || !IsSourceSymbol(paramSymbol))
                        continue;

                    var targetNode = NodeFor(paramSymbol);
                    if (targetNode is null)
                        continue;

                    AddEdge(sourceNode, targetNode, "INJECTS", seen, edges);
                }
            }

            // CALLS and RETURNS edges from methods
            foreach (var methodDecl in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol;
                if (methodSymbol is null)
                    continue;

                var methodNode = new GraphNode(methodSymbol.ToDisplayString(), "Method");

                // RETURNS edges: method return type → if source type
                var returnSymbol = model.GetTypeInfo(methodDecl.ReturnType, ct).Type;
                if (returnSymbol is not null && IsSourceSymbol(returnSymbol))
                {
                    var returnNode = NodeFor(returnSymbol);
                    if (returnNode is not null)
                        AddEdge(methodNode, returnNode, "RETURNS", seen, edges);
                }

                // CALLS edges: invocations within the method body
                if (methodDecl.Body is not null || methodDecl.ExpressionBody is not null)
                {
                    SyntaxNode? bodyNode = methodDecl.Body ?? (SyntaxNode?)methodDecl.ExpressionBody;
                    if (bodyNode is not null)
                    {
                        foreach (var invocation in bodyNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            var invokedSymbol = model.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
                            if (invokedSymbol is null || !IsSourceSymbol(invokedSymbol))
                                continue;

                            var invokedNode = new GraphNode(invokedSymbol.ToDisplayString(), "Method");
                            AddEdge(methodNode, invokedNode, "CALLS", seen, edges);
                        }
                    }
                }
            }
        }

        return edges;
    }

    private static GraphNode? NodeFor(ISymbol symbol)
    {
        var kind = symbol switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => "Interface",
            INamedTypeSymbol { TypeKind: TypeKind.Class } => "Class",
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => "Class",
            INamedTypeSymbol { IsRecord: true } => "Record",
            IMethodSymbol => "Method",
            IPropertySymbol => "Property",
            _ => null
        };

        return kind is null ? null : new GraphNode(symbol.ToDisplayString(), kind);
    }

    private static bool IsSourceSymbol(ISymbol symbol)
    {
        // BCL and external types have no declaring syntax references.
        // Generated file filtering (obj/, bin/, .g.cs) is the caller's responsibility
        // (GraphBuilder filters documents before invoking EdgeExtractor).
        return !symbol.DeclaringSyntaxReferences.IsEmpty;
    }

    private static void AddEdge(
        GraphNode source,
        GraphNode target,
        string type,
        HashSet<(string, string, string)> seen,
        List<GraphEdge> edges)
    {
        var key = (source.Id, target.Id, type);
        if (!seen.Add(key))
            return;

        edges.Add(new GraphEdge(source, target, type));
    }
}
