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

            // INJECTS edges — primary constructor (C# 12) first, fallback to the declared
            // constructor with most parameters, via the shared locator.
            var ctorParameters = ConstructorParameterLocator.Locate(typeDecl);
            if (ctorParameters is not null)
            {
                foreach (var param in ctorParameters.Value)
                {
                    if (param.Type is null) continue;
                    EmitGenericAwareEdge(
                        model.GetTypeInfo(param.Type, ct).Type,
                        sourceNode, "INJECTS", seen, edges);
                }
            }

            // CONTAINS edges — type → each declared method and property
            foreach (var memberDecl in typeDecl.Members)
            {
                switch (memberDecl)
                {
                    case MethodDeclarationSyntax methodDecl:
                    {
                        var memberSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol;
                        if (memberSymbol is null) continue;
                        var memberNode = new GraphNode(memberSymbol.ToDisplayString(), "Method");
                        AddEdge(sourceNode, memberNode, "CONTAINS", seen, edges);
                        break;
                    }
                    case PropertyDeclarationSyntax propertyDecl:
                    {
                        var memberSymbol = model.GetDeclaredSymbol(propertyDecl, ct) as IPropertySymbol;
                        if (memberSymbol is null) continue;
                        var memberNode = new GraphNode(memberSymbol.ToDisplayString(), "Property");
                        AddEdge(sourceNode, memberNode, "CONTAINS", seen, edges);
                        break;
                    }
                }
            }

            // REFERENCES edges — user-defined types in member signatures (not method bodies)
            foreach (var memberDecl in typeDecl.Members)
            {
                switch (memberDecl)
                {
                    case MethodDeclarationSyntax methodDecl:
                    {
                        // Return type
                        EmitReferencesForType(
                            model.GetTypeInfo(methodDecl.ReturnType, ct).Type,
                            sourceNode, seen, edges);

                        // Parameter types
                        foreach (var param in methodDecl.ParameterList.Parameters)
                        {
                            if (param.Type is null) continue;
                            EmitReferencesForType(
                                model.GetTypeInfo(param.Type, ct).Type,
                                sourceNode, seen, edges);
                        }
                        break;
                    }
                    case PropertyDeclarationSyntax propertyDecl:
                    {
                        EmitReferencesForType(
                            model.GetTypeInfo(propertyDecl.Type, ct).Type,
                            sourceNode, seen, edges);
                        break;
                    }
                    case FieldDeclarationSyntax fieldDecl:
                    {
                        EmitReferencesForType(
                            model.GetTypeInfo(fieldDecl.Declaration.Type, ct).Type,
                            sourceNode, seen, edges);
                        break;
                    }
                }
            }

            // CALLS and RETURNS edges from methods
            foreach (var methodDecl in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol;
                if (methodSymbol is null)
                    continue;

                var methodNode = new GraphNode(methodSymbol.ToDisplayString(), "Method");

                // RETURNS edges: method return type → if source type (open generic for constructed)
                EmitGenericAwareEdge(
                    model.GetTypeInfo(methodDecl.ReturnType, ct).Type,
                    methodNode, "RETURNS", seen, edges);

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
            INamedTypeSymbol named => NodeClassifier.ClassifyTypeKind(named),
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
        if (!symbol.DeclaringSyntaxReferences.IsEmpty)
            return true;

        // Constructed generics (e.g. IRepository<Attraction>) have empty DeclaringSyntaxReferences —
        // the declaration lives on the open generic (OriginalDefinition), not the instantiation.
        if (symbol is INamedTypeSymbol { IsGenericType: true } named)
            return !named.OriginalDefinition.DeclaringSyntaxReferences.IsEmpty;

        return false;
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

    // Emits a primary edge (INJECTS / RETURNS) from sourceNode for the given type symbol.
    // For constructed generics (e.g. IRepository<MyEntity>), the primary edge targets the open
    // generic definition (OriginalDefinition) — never the closed form, which has no standalone
    // node in the graph — and each user-defined type argument is emitted as a REFERENCES edge so
    // it is not lost. Mirrors EmitReferencesForType's collapse for the non-REFERENCES edge types.
    private static void EmitGenericAwareEdge(
        ITypeSymbol? typeSymbol,
        GraphNode sourceNode,
        string edgeType,
        HashSet<(string, string, string)> seen,
        List<GraphEdge> edges)
    {
        if (typeSymbol is null)
            return;

        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } named
            && !named.OriginalDefinition.Equals(named, SymbolEqualityComparer.Default))
        {
            // Constructed generic: target the open generic definition with the primary edge,
            // and surface each type argument as a REFERENCES edge.
            var openDef = named.OriginalDefinition;
            if (IsSourceSymbol(openDef))
            {
                var defNode = NodeFor(openDef);
                if (defNode is not null)
                    AddEdge(sourceNode, defNode, edgeType, seen, edges);
            }

            foreach (var typeArg in named.TypeArguments)
                EmitReferencesForType(typeArg, sourceNode, seen, edges);
        }
        else if (IsSourceSymbol(typeSymbol))
        {
            var targetNode = NodeFor(typeSymbol);
            if (targetNode is not null)
                AddEdge(sourceNode, targetNode, edgeType, seen, edges);
        }
    }

    // Emits REFERENCES edge(s) from sourceNode for the given type symbol (if user-defined).
    // For constructed generics (e.g. IRepository<Attraction>), emits REFERENCES to the open
    // generic definition (OriginalDefinition) and to each user-defined type argument — not
    // to the constructed form itself, which has no standalone node in the graph.
    private static void EmitReferencesForType(
        ITypeSymbol? typeSymbol,
        GraphNode sourceNode,
        HashSet<(string, string, string)> seen,
        List<GraphEdge> edges)
    {
        if (typeSymbol is null)
            return;

        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } named
            && !named.OriginalDefinition.Equals(named, SymbolEqualityComparer.Default))
        {
            // Constructed generic: target the open generic definition and each type arg.
            var openDef = named.OriginalDefinition;
            if (IsSourceSymbol(openDef))
            {
                var defNode = NodeFor(openDef);
                if (defNode is not null)
                    AddEdge(sourceNode, defNode, "REFERENCES", seen, edges);
            }

            foreach (var typeArg in named.TypeArguments)
                EmitReferencesForType(typeArg, sourceNode, seen, edges);
        }
        else if (IsSourceSymbol(typeSymbol))
        {
            var targetNode = NodeFor(typeSymbol);
            if (targetNode is not null)
                AddEdge(sourceNode, targetNode, "REFERENCES", seen, edges);
        }
    }
}
