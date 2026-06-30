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

    public static GraphNode? NodeFor(ISymbol symbol)
    {
        var kind = symbol switch
        {
            INamedTypeSymbol named => ClassifyTypeKind(named),
            IMethodSymbol => "Method",
            IPropertySymbol => "Property",
            _ => null
        };

        return kind is null ? null : new GraphNode(symbol.ToDisplayString(), kind);
    }
}
