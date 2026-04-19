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
                var typeName = typeDecl.Identifier.ValueText;
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

                        AddReference(typeName, toName, via, resolvedFile,
                            seen, references, unresolvedSeen, unresolved);
                    }
                }

                // --- constructor dependencies ---
                // Records: primary constructor parameters
                if (typeDecl is RecordDeclarationSyntax record && record.ParameterList is not null)
                {
                    foreach (var param in record.ParameterList.Parameters)
                    {
                        if (param.Type is null) continue;
                        var symbol = model.GetTypeInfo(param.Type, ct).Type;
                        var toName = param.Type.ToString();
                        var resolvedFile = ResolveFile(symbol, inputPaths);
                        AddReference(typeName, toName, "constructor", resolvedFile,
                            seen, references, unresolvedSeen, unresolved);
                    }
                }
                else
                {
                    // Classes/structs: pick constructor with most parameters
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
                            var symbol = model.GetTypeInfo(param.Type, ct).Type;
                            var toName = param.Type.ToString();
                            var resolvedFile = ResolveFile(symbol, inputPaths);
                            AddReference(typeName, toName, "constructor", resolvedFile,
                                seen, references, unresolvedSeen, unresolved);
                        }
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
                        AddReference(typeName, toName, "parameter", resolvedFile,
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
        HashSet<(string, string, string)> seen,
        List<ReferenceInfo> references,
        HashSet<string> unresolvedSeen,
        List<string> unresolved)
    {
        var key = (from, to, via);
        if (!seen.Add(key))
            return;

        references.Add(new ReferenceInfo(from, to, via, resolvedFile));

        if (resolvedFile is null && unresolvedSeen.Add(to))
            unresolved.Add(to);
    }

    // Strip generic type arguments for comparison (e.g. "List<T>" → "List")
    private static string StripGenerics(string typeName)
    {
        var idx = typeName.IndexOf('<');
        return idx >= 0 ? typeName[..idx] : typeName;
    }
}
