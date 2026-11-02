using Microsoft.CodeAnalysis;

namespace ContextManager.Analysis.Graph;

internal static class NodeClassifier
{
    public const string MethodNodeKind = "Method";

    public static string? ClassifyTypeKind(INamedTypeSymbol symbol) => symbol switch
    {
        { TypeKind: TypeKind.Interface } => "Interface",
        { IsRecord: true } => "Record",
        { TypeKind: TypeKind.Class } => "Class",
        { TypeKind: TypeKind.Struct } => "Class",
        _ => null
    };

    public static GraphNode? NodeFor(ISymbol symbol, string? fallbackFilePath = null)
    {
        symbol = Canonicalize(symbol);
        var kind = symbol switch
        {
            INamedTypeSymbol named => ClassifyTypeKind(named),
            IMethodSymbol => MethodNodeKind,
            IPropertySymbol => "Property",
            _ => null
        };

        return kind is null ? null : new GraphNode(symbol.ToDisplayString(), kind, DeclaringFile(symbol, fallbackFilePath),
            SearchName(symbol), symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null);
    }

    private static string SearchName(ISymbol symbol) => symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor
        ? constructor.ContainingType.Name
        : symbol.Name;

    private static string? DeclaringFile(ISymbol symbol, string? fallbackFilePath)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? symbol.ContainingType?.DeclaringSyntaxReferences.FirstOrDefault();
        var path = reference?.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(path))
            path = fallbackFilePath;

        return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
    }

    public static ISymbol Canonicalize(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { ReducedFrom: not null } method => method.ReducedFrom.OriginalDefinition,
        IMethodSymbol method => method.OriginalDefinition,
        IPropertySymbol property => property.OriginalDefinition,
        INamedTypeSymbol named => named.OriginalDefinition,
        _ => symbol
    };
}
