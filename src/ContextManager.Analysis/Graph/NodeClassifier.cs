using Microsoft.CodeAnalysis;

namespace ContextManager.Analysis.Graph;

// Single source of truth for graph node identity (Id string + Kind label). ClassifyTypeKind
// orders IsRecord before Struct/Class: a `record struct` has TypeKind.Struct AND IsRecord, so
// IsRecord must be checked first — otherwise it is mislabeled "Class". NodeFor is shared by
// GraphBuilder (node harvest) and EdgeExtractor (edge endpoints) so both paths agree on the
// Id (ToDisplayString) and Kind for every symbol category.
internal static class NodeClassifier
{
    public static string? ClassifyTypeKind(INamedTypeSymbol symbol) => symbol switch
    {
        { TypeKind: TypeKind.Interface } => "Interface",
        { IsRecord: true } => "Record",
        { TypeKind: TypeKind.Class } => "Class",
        { TypeKind: TypeKind.Struct } => "Class",
        _ => null
    };

    public static bool IsTypeKind(string kind) => kind is "Interface" or "Record" or "Class";

    public static GraphNode? NodeFor(ISymbol symbol, string? fallbackFilePath = null)
    {
        symbol = Canonicalize(symbol);
        var kind = symbol switch
        {
            INamedTypeSymbol named => ClassifyTypeKind(named),
            IMethodSymbol => "Method",
            IPropertySymbol => "Property",
            _ => null
        };

        return kind is null ? null : new GraphNode(symbol.ToDisplayString(), kind, DeclaringFile(symbol, fallbackFilePath));
    }

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
