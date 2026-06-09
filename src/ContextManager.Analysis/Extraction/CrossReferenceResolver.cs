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
            if (!treeByPath.TryGetValue(file.File, out var tree))
                continue;

            var model = compilation.GetSemanticModel(tree);
            var root = (CompilationUnitSyntax)tree.GetRoot(ct);

            // Build a lookup of type name → ContextTypeInfo for this file
            var typeSet = file.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

            // Walk all type declarations in this file
            var typeDeclarations = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>();

            foreach (var typeDecl in typeDeclarations)
            {
                var typeName = MemberExtractor.RenderTypeName(typeDecl);
                if (!typeSet.ContainsKey(typeName))
                    continue;

                // --- base type and implements ---
                if (typeDecl.BaseList is not null)
                {
                    foreach (var baseType in typeDecl.BaseList.Types.OfType<SimpleBaseTypeSyntax>())
                    {
                        var symbol = model.GetTypeInfo(baseType.Type, ct).Type;
                        var toName = baseType.Type.ToString();
                        var resolvedFile = ResolveFile(symbol, inputPaths);

                        // Determine via: use "base" if typeName has Base == toName, otherwise "implements"
                        var contextType = typeSet[typeName];
                        var via = (contextType.Base is not null &&
                                   string.Equals(StripGenerics(toName), StripGenerics(contextType.Base), StringComparison.Ordinal))
                            ? "base"
                            : "implements";

                        AddReference(typeName, toName, via, resolvedFile, IsMetadataResolved(symbol),
                            seen, references, unresolvedSeen, unresolved);
                    }
                }

                // --- constructor dependencies (primary ctor first, fallback declared ctor) ---
                var ctorParameters = ConstructorParameterLocator.Locate(typeDecl);
                if (ctorParameters is not null)
                {
                    foreach (var param in ctorParameters.Value)
                    {
                        if (param.Type is null) continue;
                        var symbol = model.GetTypeInfo(param.Type, ct).Type;
                        var toName = param.Type.ToString();
                        var resolvedFile = ResolveFile(symbol, inputPaths);
                        AddReference(typeName, toName, "constructor", resolvedFile, IsMetadataResolved(symbol),
                            seen, references, unresolvedSeen, unresolved);
                    }
                }

                // --- method parameter types (non-private methods only) ---
                bool isInterface = typeDecl is InterfaceDeclarationSyntax;

                foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    string methodAccess;
                    if (isInterface && !method.Modifiers.Any())
                        methodAccess = "public";
                    else
                        methodAccess = AccessLevel.FromModifiers(method.Modifiers, isTopLevelType: false);

                    if (methodAccess == "private")
                        continue;

                    foreach (var param in method.ParameterList.Parameters)
                    {
                        if (param.Type is null) continue;
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

        // Types that resolved to metadata (BCL/external assemblies) are known quantities —
        // unresolved is reserved for user types missing from the input set.
        if (resolvedFile is null && !metadataResolved && unresolvedSeen.Add(to))
            unresolved.Add(to);
    }

    // True when the symbol resolved to a metadata (BCL/external) declaration: non-error,
    // with no declaring syntax anywhere — including the open generic definition and array
    // element types. User types outside the input set stay error/source symbols and remain
    // in unresolved, which is that list's entire purpose.
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

    // Strip generic type arguments for comparison (e.g. "List<T>" → "List")
    private static string StripGenerics(string typeName)
    {
        var idx = typeName.IndexOf('<');
        return idx >= 0 ? typeName[..idx] : typeName;
    }
}
