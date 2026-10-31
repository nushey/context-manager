using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Graph;

public class EdgeExtractor
{
    public (IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges) Extract(
        SemanticModel model,
        CompilationUnitSyntax root,
        CancellationToken ct = default)
    {
        var contribution = new Contribution();

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSymbol
                || NodeClassifier.NodeFor(typeSymbol, root.SyntaxTree.FilePath) is not { } sourceNode)
                continue;

            contribution.Declare(sourceNode);

            if (typeDecl.BaseList is not null)
            {
                foreach (var baseTypeSyntax in typeDecl.BaseList.Types.OfType<SimpleBaseTypeSyntax>())
                {
                    if (model.GetTypeInfo(baseTypeSyntax.Type, ct).Type is not INamedTypeSymbol baseSymbol || !IsSourceSymbol(baseSymbol))
                        continue;

                    var edgeType = baseSymbol.TypeKind == TypeKind.Interface ? "IMPLEMENTS" : "INHERITS";
                    EmitTypeEdge(baseSymbol, sourceNode, edgeType, contribution);
                }
            }

            if (ConstructorParameterLocator.Locate(typeDecl) is { } ctorParameters)
            {
                foreach (var param in ctorParameters)
                {
                    if (param.Type is not null)
                        EmitTypeEdge(model.GetTypeInfo(param.Type, ct).Type, sourceNode, "INJECTS", contribution);
                }
            }

            foreach (var constructor in typeSymbol.InstanceConstructors)
                Contain(sourceNode, NodeClassifier.NodeFor(constructor), contribution);

            foreach (var (positionalProperty, parameterType) in PositionalProperties(model, typeDecl, typeSymbol, ct))
            {
                Contain(sourceNode, NodeClassifier.NodeFor(positionalProperty), contribution);
                EmitTypeEdge(model.GetTypeInfo(parameterType, ct).Type, sourceNode, "REFERENCES", contribution);
            }

            foreach (var memberDecl in typeDecl.Members)
            {
                if (memberDecl is MethodDeclarationSyntax or PropertyDeclarationSyntax
                    && model.GetDeclaredSymbol(memberDecl, ct) is { } memberSymbol)
                    Contain(sourceNode, NodeClassifier.NodeFor(memberSymbol), contribution);
            }

            foreach (var memberDecl in typeDecl.Members)
            {
                switch (memberDecl)
                {
                    case MethodDeclarationSyntax methodDecl:
                        EmitTypeEdge(model.GetTypeInfo(methodDecl.ReturnType, ct).Type, sourceNode, "REFERENCES", contribution);
                        foreach (var param in methodDecl.ParameterList.Parameters)
                        {
                            if (param.Type is not null)
                                EmitTypeEdge(model.GetTypeInfo(param.Type, ct).Type, sourceNode, "REFERENCES", contribution);
                        }
                        break;
                    case PropertyDeclarationSyntax propertyDecl:
                        EmitTypeEdge(model.GetTypeInfo(propertyDecl.Type, ct).Type, sourceNode, "REFERENCES", contribution);
                        break;
                    case FieldDeclarationSyntax fieldDecl:
                        EmitTypeEdge(model.GetTypeInfo(fieldDecl.Declaration.Type, ct).Type, sourceNode, "REFERENCES", contribution);
                        break;
                }
            }

            foreach (var methodDecl in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(methodDecl, ct) is not { } methodSymbol
                    || NodeClassifier.NodeFor(methodSymbol) is not { } methodNode)
                    continue;

                EmitTypeEdge(model.GetTypeInfo(methodDecl.ReturnType, ct).Type, methodNode, "RETURNS", contribution);
                EmitCallsFromBody(model, methodDecl.Body ?? (SyntaxNode?)methodDecl.ExpressionBody, methodNode, contribution, ct);
            }

            foreach (var field in typeDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in field.Declaration.Variables)
                    EmitCallsFromBody(model, variable.Initializer, sourceNode, contribution, ct);
            }

            foreach (var constructor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
                EmitCallsFromBody(model, constructor.Body ?? (SyntaxNode?)constructor.ExpressionBody, sourceNode, contribution, ct);

            foreach (var property in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(property, ct) is not { } propertySymbol
                    || NodeClassifier.NodeFor(propertySymbol) is not { } propertyNode)
                    continue;

                EmitCallsFromBody(model, property.ExpressionBody, propertyNode, contribution, ct);
                EmitCallsFromBody(model, property.Initializer, propertyNode, contribution, ct);
                foreach (var accessor in property.AccessorList?.Accessors ?? [])
                    EmitCallsFromBody(model, accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody, propertyNode, contribution, ct);
            }
        }

        EmitTopLevelStatements(model, root, contribution, ct);

        return (contribution.Nodes, contribution.Edges);
    }

    private sealed class Contribution
    {
        private readonly HashSet<string> _nodeIds = new(StringComparer.Ordinal);
        private readonly HashSet<(string From, string To, string Type)> _edgeKeys = [];

        public List<GraphNode> Nodes { get; } = [];
        public List<GraphEdge> Edges { get; } = [];

        public void Declare(GraphNode node)
        {
            if (_nodeIds.Add(node.Id))
                Nodes.Add(node);
        }

        public void Add(GraphNode source, GraphNode target, string type)
        {
            if (_edgeKeys.Add((source.Id, target.Id, type)))
                Edges.Add(new GraphEdge(source, target, type));
        }
    }

    private static void Contain(GraphNode type, GraphNode? member, Contribution contribution)
    {
        if (member is null)
            return;

        contribution.Declare(member);
        contribution.Add(type, member, "CONTAINS");
    }

    private static IEnumerable<(IPropertySymbol Property, TypeSyntax Type)> PositionalProperties(
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

    private static void EmitTopLevelStatements(
        SemanticModel model,
        CompilationUnitSyntax root,
        Contribution contribution,
        CancellationToken ct)
    {
        if (!root.Members.OfType<GlobalStatementSyntax>().Any()
            || model.GetDeclaredSymbol(root, ct) is not IMethodSymbol entryPoint
            || NodeClassifier.NodeFor(entryPoint.ContainingType, root.SyntaxTree.FilePath) is not { } programNode)
            return;

        var parameters = string.Join(", ", entryPoint.Parameters.Select(p => p.Type.ToDisplayString()));
        var entryPointNode = new GraphNode($"{programNode.Id}.{entryPoint.MetadataName}({parameters})", "Method", programNode.File);

        contribution.Declare(programNode);
        Contain(programNode, entryPointNode, contribution);
        foreach (var statement in root.Members.OfType<GlobalStatementSyntax>())
            EmitCallsFromBody(model, statement, entryPointNode, contribution, ct);
    }

    private static bool IsSourceSymbol(ISymbol symbol)
    {
        if (!symbol.DeclaringSyntaxReferences.IsEmpty)
            return true;

        var canonical = NodeClassifier.Canonicalize(symbol);
        if (!canonical.DeclaringSyntaxReferences.IsEmpty)
            return true;

        return canonical is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } constructedType }
            && IsSourceSymbol(constructedType);
    }

    private static void EmitTypeEdge(
        ITypeSymbol? typeSymbol,
        GraphNode sourceNode,
        string edgeType,
        Contribution contribution)
    {
        switch (typeSymbol)
        {
            case null:
                return;
            case IArrayTypeSymbol array:
                EmitTypeEdge(array.ElementType, sourceNode, edgeType, contribution);
                return;
            case INamedTypeSymbol { IsGenericType: true } named
                when !named.OriginalDefinition.Equals(named, SymbolEqualityComparer.Default):
                if (IsSourceSymbol(named.OriginalDefinition) && NodeClassifier.NodeFor(named.OriginalDefinition) is { } definitionNode)
                    contribution.Add(sourceNode, definitionNode, edgeType);

                foreach (var typeArg in named.TypeArguments)
                    EmitTypeEdge(typeArg, sourceNode, edgeType == "RETURNS" ? "RETURNS" : "REFERENCES", contribution);
                return;
            default:
                if (IsSourceSymbol(typeSymbol) && NodeClassifier.NodeFor(typeSymbol) is { } targetNode)
                    contribution.Add(sourceNode, targetNode, edgeType);
                return;
        }
    }

    private static void EmitCallsFromBody(
        SemanticModel model,
        SyntaxNode? body,
        GraphNode sourceNode,
        Contribution contribution,
        CancellationToken ct)
    {
        if (body is null)
            return;

        foreach (var node in body.DescendantNodesAndSelf())
        {
            if (TestedType(model, node, ct) is { } testedType)
                EmitTypeEdge(model.GetTypeInfo(testedType, ct).Type, sourceNode, "REFERENCES", contribution);

            switch (node)
            {
                case InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax:
                    if (model.GetSymbolInfo(node, ct).Symbol is IMethodSymbol invokedSymbol)
                        EmitCall(invokedSymbol, sourceNode, contribution);
                    break;
                case SimpleNameSyntax name:
                    switch (model.GetSymbolInfo(name, ct).Symbol)
                    {
                        case IMethodSymbol referencedMethod:
                            EmitCall(referencedMethod, sourceNode, contribution);
                            break;
                        case IPropertySymbol property when IsSourceSymbol(property) && NodeClassifier.NodeFor(property) is { } propertyNode:
                            contribution.Add(sourceNode, propertyNode, "REFERENCES");
                            break;
                    }
                    break;
            }
        }
    }

    private static TypeSyntax? TestedType(SemanticModel model, SyntaxNode node, CancellationToken ct) => node switch
    {
        ConstantPatternSyntax { Expression: TypeSyntax constantType } when model.GetSymbolInfo(constantType, ct).Symbol is ITypeSymbol => constantType,
        CaseSwitchLabelSyntax { Value: TypeSyntax labelType } when model.GetSymbolInfo(labelType, ct).Symbol is ITypeSymbol => labelType,
        DeclarationPatternSyntax declaration => declaration.Type,
        TypePatternSyntax typePattern => typePattern.Type,
        RecursivePatternSyntax { Type: { } recursiveType } => recursiveType,
        BinaryExpressionSyntax { Right: TypeSyntax isType } binary when binary.IsKind(SyntaxKind.IsExpression) => isType,
        TypeOfExpressionSyntax typeOf => typeOf.Type,
        _ => null,
    };

    private static void EmitCall(IMethodSymbol invokedSymbol, GraphNode sourceNode, Contribution contribution)
    {
        if (invokedSymbol.MethodKind != MethodKind.LocalFunction
            && IsSourceSymbol(invokedSymbol)
            && NodeClassifier.NodeFor(invokedSymbol) is { } invokedNode)
            contribution.Add(sourceNode, invokedNode, "CALLS");

        foreach (var typeArgument in invokedSymbol.TypeArguments)
            EmitTypeEdge(typeArgument, sourceNode, "REFERENCES", contribution);

        if (invokedSymbol.ContainingType is { IsGenericType: true } containingType)
        {
            foreach (var typeArgument in containingType.TypeArguments)
                EmitTypeEdge(typeArgument, sourceNode, "REFERENCES", contribution);
        }
    }
}
