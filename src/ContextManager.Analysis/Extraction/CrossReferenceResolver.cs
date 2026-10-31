using ContextManager.Analysis.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Extraction;

public class CrossReferenceResolver
{
    public (IReadOnlyList<ReferenceInfo> References, IReadOnlyList<string> Unresolved) Resolve(
        IReadOnlyList<ContextFileAnalysis> files,
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> treeByPath,
        CancellationToken ct = default)
    {
        var inputPaths = new HashSet<string>(files.Select(f => f.File), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string From, string To, string Via)>();
        var references = new List<ReferenceInfo>();
        var unresolvedSeen = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var file in files)
        {
            var tree = treeByPath[file.File];
            var model = compilation.GetSemanticModel(tree);
            var root = (CompilationUnitSyntax)tree.GetRoot(ct);
            var typeNames = file.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeName = MemberExtractor.RenderTypeName(typeDecl);
                if (!typeNames.Contains(typeName))
                    continue;

                if (typeDecl.BaseList is not null)
                {
                    var baseTypes = typeDecl.BaseList.Types.OfType<SimpleBaseTypeSyntax>().ToList();
                    for (var index = 0; index < baseTypes.Count; index++)
                    {
                        var baseType = baseTypes[index].Type;
                        var symbol = model.GetTypeInfo(baseType, ct).Type;
                        var toName = baseType.ToString();
                        var resolvedFile = ResolveFile(symbol, inputPaths);
                        var via = index == 0 && MemberExtractor.IsBaseClass(typeDecl, baseType, model) ? "base" : "implements";

                        AddReference(typeName, toName, via, resolvedFile, IsMetadataResolved(symbol),
                            seen, references, unresolvedSeen, unresolved);
                    }
                }

                var ctorParameters = ConstructorParameterLocator.Locate(typeDecl);
                if (ctorParameters is not null)
                {
                    foreach (var param in ctorParameters.Value)
                    {
                        if (param.Type is null || IsPredefined(param.Type)) continue;
                        var symbol = model.GetTypeInfo(param.Type, ct).Type;
                        var toName = param.Type.ToString();
                        var resolvedFile = ResolveFile(symbol, inputPaths);
                        AddReference(typeName, toName, "constructor", resolvedFile, IsMetadataResolved(symbol),
                            seen, references, unresolvedSeen, unresolved);
                    }
                }

                foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (AccessLevel.OfMember(method, method.ExplicitInterfaceSpecifier) == "private")
                        continue;

                    foreach (var param in method.ParameterList.Parameters)
                    {
                        if (param.Type is null || IsPredefined(param.Type)) continue;
                        var symbol = model.GetTypeInfo(param.Type, ct).Type;
                        var toName = param.Type.ToString();
                        var resolvedFile = ResolveFile(symbol, inputPaths);
                        AddReference(typeName, toName, "parameter", resolvedFile, IsMetadataResolved(symbol),
                            seen, references, unresolvedSeen, unresolved);
                    }
                }
            }
        }

        return (references, unresolved);
    }

    private static string? ResolveFile(ITypeSymbol? symbol, HashSet<string> inputPaths)
    {
        if (symbol is null)
            return null;

        var declRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef is null)
            return null;

        var filePath = declRef.SyntaxTree.FilePath;
        return inputPaths.Contains(filePath) ? filePath : null;
    }

    private static void AddReference(
        string from,
        string to,
        string via,
        string? resolvedFile,
        bool metadataResolved,
        HashSet<(string, string, string)> seen,
        List<ReferenceInfo> references,
        HashSet<string> unresolvedSeen,
        List<string> unresolved)
    {
        var key = (from, to, via);
        if (!seen.Add(key))
            return;

        references.Add(new ReferenceInfo(from, to, via, resolvedFile));

        if (resolvedFile is null && !metadataResolved && unresolvedSeen.Add(to))
            unresolved.Add(to);
    }

    private static bool IsMetadataResolved(ITypeSymbol? symbol)
    {
        if (symbol is null)
            return false;

        if (symbol is IArrayTypeSymbol array)
            return IsMetadataResolved(array.ElementType);

        if (symbol.TypeKind == TypeKind.Error)
            return false;

        if (symbol is INamedTypeSymbol named)
        {
            if (named.IsGenericType && named.TypeArguments.Any(a => a.TypeKind == TypeKind.Error))
                return false;

            return named.DeclaringSyntaxReferences.IsEmpty &&
                   named.OriginalDefinition.DeclaringSyntaxReferences.IsEmpty;
        }

        return symbol.DeclaringSyntaxReferences.IsEmpty;
    }

    private static bool IsPredefined(TypeSyntax type) =>
        type is PredefinedTypeSyntax or NullableTypeSyntax { ElementType: PredefinedTypeSyntax };
}
