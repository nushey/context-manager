using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis;

public class ContextAnalyzer
{
    private readonly CrossReferenceResolver _resolver;

    public ContextAnalyzer(CrossReferenceResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<ContextAnalysis> AnalyzeAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        var paths = filePaths.Select(Path.GetFullPath).ToList();
        var treeByPath = new Dictionary<string, SyntaxTree>(paths.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!treeByPath.ContainsKey(path))
                treeByPath[path] = CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(path, ct), path: path, cancellationToken: ct);
        }

        var compilation = FrameworkCompilation.Create(treeByPath.Values, ct);
        var files = new List<ContextFileAnalysis>(paths.Count);

        foreach (var path in paths)
        {
            var tree = treeByPath[path];
            var root = (CompilationUnitSyntax)tree.GetRoot(ct);
            var extractor = new TypeExtractor(compilation.GetSemanticModel(tree));
            extractor.Visit(root);

            var contextTypes = extractor.Types
                .Select(t => new ContextTypeInfo(
                    Name: t.Name,
                    Kind: t.Kind,
                    Base: t.Base,
                    Implements: t.Implements,
                    Attributes: t.Attributes,
                    ConstructorDependencies: t.ConstructorDependencies?.Select(d => d.Type).ToList(),
                    Methods: t.Methods?.Select(MethodSignatureFormatter.Format).ToList()))
                .ToList();

            files.Add(new ContextFileAnalysis(
                File: path,
                Namespace: TypeExtractor.NamespaceOf(root),
                Types: contextTypes));
        }

        var (references, unresolved) = _resolver.Resolve(files, compilation, treeByPath, ct);

        return new ContextAnalysis(
            Files: files,
            References: references,
            Unresolved: unresolved);
    }
}
