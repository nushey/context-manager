using Microsoft.CodeAnalysis;

namespace ContextManager.Analysis.Graph;

// Single source of truth for the node Kind label of an INamedTypeSymbol. Order matters: a
// `record struct` has TypeKind.Struct AND IsRecord, so IsRecord must be checked before the
// Struct/Class arms — otherwise it is mislabeled "Class". Shared by GraphBuilder (node harvest)
// and EdgeExtractor (edge endpoints) so both paths agree.
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
}
