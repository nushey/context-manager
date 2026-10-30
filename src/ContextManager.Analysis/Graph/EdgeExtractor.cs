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

            var sourceNode = NodeClassifier.NodeFor(typeSymbol);
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

                    var edgeType = baseSymbol.TypeKind == TypeKind.Interface ? "IMPLEMENTS" : "INHERITS";
                    EmitGenericAwareEdge(baseSymbol, sourceNode, edgeType, seen, edges);
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

            foreach (var constructor in typeSymbol.InstanceConstructors)
            {
                var constructorNode = NodeClassifier.NodeFor(constructor);
                if (constructorNode is not null)
                    AddEdge(sourceNode, constructorNode, "CONTAINS", seen, edges);
            }

            foreach (var (positionalProperty, parameterType) in PositionalProperties(model, typeDecl, typeSymbol, ct))
            {
                var propertyNode = NodeClassifier.NodeFor(positionalProperty);
                if (propertyNode is not null)
                    AddEdge(sourceNode, propertyNode, "CONTAINS", seen, edges);
                EmitReferencesForType(model.GetTypeInfo(parameterType, ct).Type, sourceNode, seen, edges);
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
                        var memberNode = NodeClassifier.NodeFor(memberSymbol);
                        if (memberNode is null) continue;
                        AddEdge(sourceNode, memberNode, "CONTAINS", seen, edges);
                        break;
                    }
                    case PropertyDeclarationSyntax propertyDecl:
                    {
                        var memberSymbol = model.GetDeclaredSymbol(propertyDecl, ct) as IPropertySymbol;
                        if (memberSymbol is null) continue;
                        var memberNode = NodeClassifier.NodeFor(memberSymbol);
                        if (memberNode is null) continue;
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

                var methodNode = NodeClassifier.NodeFor(methodSymbol);
                if (methodNode is null)
                    continue;

                // RETURNS edges: method return type → if source type (open generic for constructed)
                EmitGenericAwareEdge(
                    model.GetTypeInfo(methodDecl.ReturnType, ct).Type,
                    methodNode, "RETURNS", seen, edges);

                EmitCallsFromBody(model, methodDecl.Body ?? (SyntaxNode?)methodDecl.ExpressionBody,
                    methodNode, seen, edges, ct);
            }

            foreach (var field in typeDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in field.Declaration.Variables)
                    EmitCallsFromBody(model, variable.Initializer, sourceNode, seen, edges, ct);
            }

            foreach (var constructor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
                EmitCallsFromBody(model, constructor.Body ?? (SyntaxNode?)constructor.ExpressionBody,
                    sourceNode, seen, edges, ct);

            foreach (var property in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
            {
                var propertySymbol = model.GetDeclaredSymbol(property, ct) as IPropertySymbol;
                var propertyNode = propertySymbol is null ? null : NodeClassifier.NodeFor(propertySymbol);
                if (propertyNode is null)
                    continue;

                EmitCallsFromBody(model, property.ExpressionBody, propertyNode, seen, edges, ct);
                EmitCallsFromBody(model, property.Initializer, propertyNode, seen, edges, ct);
                if (property.AccessorList is null)
                    continue;

                foreach (var accessor in property.AccessorList.Accessors)
                    EmitCallsFromBody(model, accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody,
                        propertyNode, seen, edges, ct);
            }
        }

        EmitTopLevelStatements(model, root, seen, edges, ct);

        return edges;
    }

    internal static IEnumerable<(IPropertySymbol Property, TypeSyntax Type)> PositionalProperties(
        SemanticModel model,
        TypeDeclarationSyntax typeDecl,
        INamedTypeSymbol typeSymbol,
        CancellationToken ct)
    {
        if (typeDecl is not RecordDeclarationSyntax { ParameterList: { } parameterList })
            yield break;

        foreach (var parameter in parameterList.Parameters)
        {
            if (parameter.Type is null || model.GetDeclaredSymbol(parameter, ct) is not { } parameterSymbol)
                continue;

            var property = typeSymbol.GetMembers(parameterSymbol.Name)
                .OfType<IPropertySymbol>()
                .FirstOrDefault(p => p.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) == parameter));
            if (property is not null)
                yield return (property, parameter.Type);
        }
    }

    internal static (GraphNode Program, GraphNode EntryPoint)? TopLevelNodes(
        SemanticModel model,
        CompilationUnitSyntax root,
        CancellationToken ct)
    {
        if (!root.Members.OfType<GlobalStatementSyntax>().Any()
            || model.GetDeclaredSymbol(root, ct) is not IMethodSymbol entryPoint)
            return null;

        var filePath = root.SyntaxTree.FilePath;
        if (NodeClassifier.NodeFor(entryPoint.ContainingType, filePath) is not { } programNode)
            return null;

        var parameters = string.Join(", ", entryPoint.Parameters.Select(p => p.Type.ToDisplayString()));
        var entryPointNode = new GraphNode($"{programNode.Id}.{entryPoint.MetadataName}({parameters})", "Method", programNode.File);
        return (programNode, entryPointNode);
    }

    private static void EmitTopLevelStatements(
        SemanticModel model,
        CompilationUnitSyntax root,
        HashSet<(string, string, string)> seen,
        List<GraphEdge> edges,
        CancellationToken ct)
    {
        if (TopLevelNodes(model, root, ct) is not var (programNode, entryPointNode))
            return;

        AddEdge(programNode, entryPointNode, "CONTAINS", seen, edges);
        foreach (var statement in root.Members.OfType<GlobalStatementSyntax>())
            EmitCallsFromBody(model, statement, entryPointNode, seen, edges, ct);
    }

    private static bool IsSourceSymbol(ISymbol symbol)
    {
        // BCL and external types have no declaring syntax references.
        // Generated file filtering (obj/, bin/, .g.cs) is the caller's responsibility
        // (GraphBuilder filters documents before invoking EdgeExtractor).
        if (!symbol.DeclaringSyntaxReferences.IsEmpty)
            return true;

        var canonical = NodeClassifier.Canonicalize(symbol);
        if (!canonical.DeclaringSyntaxReferences.IsEmpty)
            return true;

        if (canonical is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } constructedType })
            return IsSourceSymbol(constructedType);

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

        if (typeSymbol is IArrayTypeSymbol array)
        {
            EmitGenericAwareEdge(array.ElementType, sourceNode, edgeType, seen, edges);
            return;
        }

        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } named
            && !named.OriginalDefinition.Equals(named, SymbolEqualityComparer.Default))
        {
            // Constructed generic: target the open generic definition with the primary edge,
            // and surface each type argument as a REFERENCES edge.
            var openDef = named.OriginalDefinition;
            if (IsSourceSymbol(openDef))
            {
                var defNode = NodeClassifier.NodeFor(openDef);
                if (defNode is not null)
                    AddEdge(sourceNode, defNode, edgeType, seen, edges);
            }

            foreach (var typeArg in named.TypeArguments)
            {
                if (edgeType == "RETURNS")
                    EmitGenericAwareEdge(typeArg, sourceNode, edgeType, seen, edges);
                else
                    EmitReferencesForType(typeArg, sourceNode, seen, edges);
            }
        }
        else if (IsSourceSymbol(typeSymbol))
        {
            var targetNode = NodeClassifier.NodeFor(typeSymbol);
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

        if (typeSymbol is IArrayTypeSymbol array)
        {
            EmitReferencesForType(array.ElementType, sourceNode, seen, edges);
            return;
        }

        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } named
            && !named.OriginalDefinition.Equals(named, SymbolEqualityComparer.Default))
        {
            // Constructed generic: target the open generic definition and each type arg.
            var openDef = named.OriginalDefinition;
            if (IsSourceSymbol(openDef))
            {
                var defNode = NodeClassifier.NodeFor(openDef);
                if (defNode is not null)
                    AddEdge(sourceNode, defNode, "REFERENCES", seen, edges);
            }

            foreach (var typeArg in named.TypeArguments)
                EmitReferencesForType(typeArg, sourceNode, seen, edges);
        }
        else if (IsSourceSymbol(typeSymbol))
        {
            var targetNode = NodeClassifier.NodeFor(typeSymbol);
            if (targetNode is not null)
                AddEdge(sourceNode, targetNode, "REFERENCES", seen, edges);
        }
    }

    private static void EmitCallsFromBody(
        SemanticModel model,
        SyntaxNode? body,
        GraphNode sourceNode,
        HashSet<(string, string, string)> seen,
        List<GraphEdge> edges,
        CancellationToken ct)
    {
        if (body is null)
            return;

        foreach (var node in body.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax:
                    if (model.GetSymbolInfo(node, ct).Symbol is IMethodSymbol invokedSymbol)
                        EmitCall(invokedSymbol, sourceNode, seen, edges);
                    break;
                case SimpleNameSyntax name:
                    switch (model.GetSymbolInfo(name, ct).Symbol)
                    {
                        case IMethodSymbol referencedMethod:
                            EmitCall(referencedMethod, sourceNode, seen, edges);
                            break;
                        case IPropertySymbol property when IsSourceSymbol(property):
                            if (NodeClassifier.NodeFor(property) is { } propertyNode)
                                AddEdge(sourceNode, propertyNode, "REFERENCES", seen, edges);
                            break;
                    }
                    break;
            }
        }
    }

    private static void EmitCall(
        IMethodSymbol invokedSymbol,
        GraphNode sourceNode,
        HashSet<(string, string, string)> seen,
        List<GraphEdge> edges)
    {
        var invokedNode = invokedSymbol.MethodKind == MethodKind.LocalFunction || !IsSourceSymbol(invokedSymbol)
            ? null
            : NodeClassifier.NodeFor(invokedSymbol);
        if (invokedNode is not null)
            AddEdge(sourceNode, invokedNode, "CALLS", seen, edges);

        foreach (var typeArgument in invokedSymbol.TypeArguments)
            EmitReferencesForType(typeArgument, sourceNode, seen, edges);

        if (invokedSymbol.ContainingType is { IsGenericType: true } containingType)
        {
            foreach (var typeArgument in containingType.TypeArguments)
                EmitReferencesForType(typeArgument, sourceNode, seen, edges);
        }
    }
}
